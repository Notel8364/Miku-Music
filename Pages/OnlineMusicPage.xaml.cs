using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicBox.Controls;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class OnlineMusicPage : UserControl, IMusicPage
{
	/// <summary>五个源的固定顺序，源选项行和预取都按这个顺序走。
	/// 汽水排 B 站前面：它是正经曲库搜索（ly-music-source 的 /luna/pc/search/all），
	/// 和网易云/酷狗/QQ 一档；B 站是视频替代源，仍垫底。</summary>
	private static readonly string[] EngineOrder = { "netease", "kugou", "qq", "soda", "bili" };

	/// <summary>B 站没有分页接口，只能出第 1 页，所以它不参与后续页的预取。</summary>
	private static bool SupportsPaging(string engine) => engine != "bili";

	/// <summary>每个源单页拉多少条。</summary>
	private const int PageSize = 30;

	// 行缓存上限（按行数）。30 行一页，5 页的行在手，来回翻页基本全复用；
	// 再往上翻就开始挤掉最早离页的行，内存不跟着翻页深度无限涨。
	private const int RowCacheCap = 150;

	/// <summary>单行实际占高（含行间距），翻页滚动按这个步长走。
	/// 直接引用 TrackRows 的统一尺寸常量，不再各页抄一份 54，
	/// 免得以后调行高时一边改了、另一边翻页滚偏。</summary>
	private const double RowHeight = TrackRows.RowHeight;

	/// <summary>翻页动画时长（毫秒）。
	/// 300ms 太赶了：列表一滑到底、回顶还没走完就已经落定，整段看着像「闪一下」
	/// 而不是「翻了一页」。给到 620ms —— 慢到能看清内容从哪来，
	/// 又不至于让连点下一页的人觉得卡。</summary>
	private const double PageTurnMs = 620.0;

	/// <summary>翻页动画的起始透明度。太高就没有「换了一页」的感觉。</summary>
	private const double PageTurnFromOpacity = 0.25;

	/// <summary>翻页时整块的滑入距离（像素）。行程短了就算时间够长也像原地淡入，
	/// 620ms 配 52px 是试出来的比例：起手慢、行程长、落定轻。</summary>
	private const double PageTurnOffset = 52.0;

	private readonly Dictionary<string, Button> _engineButtons = new Dictionary<string, Button>();
	private readonly Dictionary<string, List<object>> _engineCache = new Dictionary<string, List<object>>();
	private readonly Dictionary<string, HashSet<string>> _engineSeen = new Dictionary<string, HashSet<string>>();
	/// <summary>每个源下一个要拉的页号（0 起）。_engineCache 里已经有几页就等于 _nextPage。</summary>
	private readonly Dictionary<string, int> _nextPage = new Dictionary<string, int>();
	/// <summary>某个源是不是已经拉到空页了（到底了），避免反复请求。</summary>
	private readonly Dictionary<string, bool> _exhausted = new Dictionary<string, bool>();
	/// <summary>正在后台预取的源 → 那个预取的 Task。
	/// 存 Task 而不是只存 HashSet：点「下一页」时必须能 await 已经在飞的那次预取，
	/// 否则 _prefetching.Add 返回 false 直接 return，页面只是重绘一下什么都没等到。</summary>
	private readonly Dictionary<string, Task> _prefetching = new Dictionary<string, Task>();
	/// <summary>用户当前正在看第几页（1 起）。和「已加载几页」分开：
	/// 预取只往缓存里塞数据，不该把页码顶到下一页去。</summary>
	private readonly Dictionary<string, int> _visiblePage = new Dictionary<string, int>();

	private string _selectedEngine = "netease";
	private string _lastKeyword = "";
	private string? _pendingKeyword;
	private bool _autoSearched;
	private bool _loadingMore;

	/// <summary>歌单模式：结果列表装的是账号收藏夹里某个歌单的整份曲目，不是搜索结果。
	/// 歌单顺序是用户自己排的，所以这个模式下不按相关度/评论数/播放量重排，
	/// 也没有「下一页」可翻（见 <see cref="ShowExternalPlaylist"/>）。
	/// 新搜索一来就得退出这个模式，见 DoOnlineSearchAsync。</summary>
	private bool _playlistMode;

	/// <summary>歌单模式下灌进来的那份歌单的平台歌单 id（platform+id 才能唯一定位
	/// 一份在线歌单）。启动自动播放的播放上下文按它恢复列表：收藏夹缓存命中直接用，
	/// 没命中再异步拉一次。未知 id（老入口没带）记空串，恢复时静默跳过。</summary>
	private string _playlistId = "";

	/// <summary>左下角限次提示的自动隐藏定时器；见 ShowStatusForSeconds。</summary>
	private DispatcherTimer? _statusTimer;
	// 状态行收起时的淡出动画。留着引用是为了能中途掐掉：
	// 上一条提示正在淡出、新的一条又来了，得把旧动画停掉，不然两条会叠着淡。
	private DoubleAnimation? _statusFade;
	private int _statusStamp;

	// VIP 结论广播的合并定时器：实测结论逐条广播（搜索结果一页几十条、
	// 主动探测开着时更是上百条），每条都 RenderEngine 整页重画会把 UI
	// 打成重画节拍器。400ms 合并成一拍，标签渐进就位，重画次数压掉两个量级。
	private DispatcherTimer? _accessRefreshTimer;

	/// <summary>B 站播放量排序开关。默认开 —— B 站结果里混着大量
	/// 零播放和搬运条目，按播放量排完基本就是「能听的在前」。</summary>
	private bool _sortByPlay = true;

	/// <summary>评论数排序开关。默认关 —— 网易云这一档要额外发 30 个请求
	/// 才能把评论数取齐（搜索接口不给这个字段），当默认项等于每次搜索都多打一轮。</summary>
	private bool _sortByComment;

	/// <summary>评论数正在补齐。补完会重排一次，这期间不再重复发起。</summary>
	private bool _fillingCommentCounts;

	private CheckBox? _sortByCommentBox;

	// 按源缓存「已经建好的行控件」，切源时直接复用，别再为 60 条结果重建一遍 UI。
	// 数据条数一变（重新搜索 / 又翻一页）就重建，见 RenderEngine。
	// 行控件缓存：源 -> (条目 key -> 已建好的行)。
	// 按条目 key 而不是按下标存，B 站按播放量重排时只改顺序、不必重建控件。
	private readonly Dictionary<string, Dictionary<string, FrameworkElement>> _rowCache
		= new(StringComparer.OrdinalIgnoreCase);

	// 本轮渲染已经用掉的行。GetOrBuildRow 判断「能不能复用缓存行」靠它，
	// 不再靠 row.Parent == null：同一个 key 在一页里出现两次（搜索结果去重
	// 偶尔漏网）时第二个条目必须建新行，但跨页复用不能被「上一页还没摘干净
	// 的 Parent」挡住——被挡住就会悄悄重建一行，封面从 0 重新淡入，
	// 表现就是翻页时已看过的封面闪一下。
	private readonly HashSet<FrameworkElement> _renderPassUsed = new();

	// 每个源的「显示顺序」冻结表：一次排好后不再随缓存增长重排，
	// 预取落地的新条目只会被追加到队尾（见 MergeInto）。这样当前可见页
	// 的内容在翻页/重搜之前一个字都不会变，预取落地永远不需要重画 ——
	// 上一轮的「可见页快照比对」之所以还闪，是因为相关性排序在整份缓存上
	// 重算，第 2 页的新条目总能挤进第 1 页的前 30 名，比对永远判「变了」，
	// 于是 QQ/酷狗这类结果多的源一页搜索连环整页重建五六轮（日志证据：
	// 20:00:14-19 六秒内 kugou2→qq2→qq3→kugou3→netease2→kugou4→netease3
	// 无用户操作连续落地）。冻结顺序后「单向追加 + 行复用」才真正成立。
	private readonly Dictionary<string, List<object>> _displayOrder = new(StringComparer.Ordinal);

	// —— 定位球跨页记忆（内存级，不落盘）——
	// 记住「哪首歌上次是在哪个源/哪次搜索/第几页被放响/定位到的」。
	// 定位球在当前列表找不到行时（用户切了源/换了搜索词/翻了页），按这份
	// 记忆把列表恢复到当时现场再定位；仍找不到才提示「定位不到」。
	private sealed record LocateMemo(string Engine, string Keyword, int Page);
	private static readonly Dictionary<string, LocateMemo> LocateMemos = new(StringComparer.OrdinalIgnoreCase);
	private string? _pendingLocateKey;

	/// <summary>定位球触发了「按记忆重搜恢复」：搜索完成后页面自己翻页居中收尾
	///（CompletePendingLocateRestore），MainWindow 据此跳过「定位不到」的提示。</summary>
	public bool LocateRestorePending => _pendingLocateKey != null;

	// 排序规则已内置：B 站结果固定按播放量降序，其它平台保持平台原顺序，
	// 界面上不再有可点的开关，所以也不需要开关状态和事件屏蔽字段了。
	private bool _searching;

	/// <summary>每个源实际用的查询词。默认就是用户输入的那个词，
	/// 但搜不到时会换成扩展词重搜（见 DoOnlineSearchAsync），那之后翻页必须
	/// 继续用**当初搜出结果的那个词**——用原词去拉第 2 页，
	/// 会出现「第 1 页是『雲鶴』、第 2 页是『云鹤』」这种跨词拼起来的列表。</summary>
	private readonly Dictionary<string, string> _engineQuery = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>搜索代次号。用户在上一轮结果回来之前又搜了一次的话，
	/// 老结果直接丢掉，不然会出现「先搜 A 再搜 B，最后显示 A 的结果」。</summary>
	private int _searchToken;

	/// <summary>页脚里那个水平 StackPanel，null 表示当前没有页脚（更新页码前先判空）。</summary>
	private StackPanel? _footerItem;
	private TextBlock _footerPageText = null!;
	private Button _footerPrev = null!;
	private Button _footerFirst = null!;
	private Button _footerNext = null!;

	public OnlineMusicPage()
	{
		InitializeComponent();
		HintText.Visibility = Visibility.Visible;
		TrackRows.AccentChanged += OnAccentChanged;
		// 切界面语言后重刷这个页里代码赋过值的字
		// （B 站源那一行的标题、状态行）。评论面板自己订阅语言事件。
		// 这些都是 `text.Text = Loc.Default[...]`，只在赋值那一刻取一次值，
		// XAML 里那些 Binding 会自己变、它们不会 —— 于是切完语言
		// 页面上半截是新语言、下半截还停在旧语言。
		Loc.Default.LanguageChanged += OnLanguageChanged;
		// 某首歌的可播结论被实测结果改写（撤销或补上 VIP 标签）时，
		// 屏幕上那些行是按旧结论画的，得重建 —— 不然用户得重新搜一遍才看得到变化。
		TrackAccess.Changed += OnAccessChanged;
		// 会话切歌的出口：MusicService 的上一首/下一首要推进在线会话队列时，
		// 把队列里的那条曲目对象交回本页播放（全平台都能走 Play 这条路）。
		// 本页实例整个程序周期只有一份（MainWindow 缓存），直接挂实例方法。
		MusicService.SessionPlayer = item =>
		{
			try
			{
				Play(item);
				return true;
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "会话切歌失败");
				return false;
			}
		};
		// 预加载：只把下一首下进各平台的缓存目录，绝不播放、不改状态行、
		// 不弹提示。切歌时同一条 DownloadSongAsync 会命中缓存（DownloadCoordinator
		// 还会把并发的预取与播放合并成同一个下载任务），于是没有取流等待。
		MusicService.SessionPrefetcher = async (item, ct) =>
		{
			// 预取是「尽力而为」：只在还没开始下载时响应切歌取消。
			// 一旦开始下载就不传 CancellationToken —— 下载任务可能被正在
			// 切过去的播放复用（DownloadCoordinator 按目标文件去重），
			// 中途取消会把播放一起打断。让它把缓存写完整反而最有用。
			ct.ThrowIfCancellationRequested();
			CancellationToken none = CancellationToken.None;
			switch (item)
			{
				case NeteaseTrack ne:
					string? url = await NeteaseApiClient.GetSongUrlAsync(ne.Id, none).ConfigureAwait(false);
					if (!string.IsNullOrEmpty(url))
					{
						await NeteaseApiClient.DownloadSongAsync(ne.Id, url, null, none).ConfigureAwait(false);
					}
					break;
				case KugouTrack kg:
					// noteAccess:false —— 后台预取不该改行上的 VIP 结论，
					// 也不该触发列表重画；可达性以用户真正点播的那次为准。
					await KugouClient.DownloadSongAsync(kg, null, none, noteAccess: false).ConfigureAwait(false);
					break;
				case QqTrack qq:
					await QqClient.DownloadSongAsync(qq, null, none, noteAccess: false).ConfigureAwait(false);
					break;
				case BiliTrack bili:
					// BBDown 是外部进程、按「目录里最新音频」取结果，预取会与
					// 正在播放的下载抢文件判定；B 站走原来的取流提示，不预取。
					break;
			}
		};
	}

	/// <summary>实测改写了某首歌的受限结论：丢掉行缓存重画当前那一栏。
	/// 事件可能来自后台线程（刚刚那次下载），回到 UI 线程再动控件。
	/// 只攒标记不重画，由 400ms 合并定时器统一执行：主动探测开着时
	/// 结论会连续广播几十上百条，每条都整页 RenderEngine 就是重画节拍器。
	/// 直接整批重建最稳：VIP 标签、是否可下载这些在建行时就烘进控件里了，
	/// 事后单独改标签容易跟行上的其它状态对不上（行缓存在这里一并清掉，
	/// 翻页回来复用缓存行时不会把旧标签带回来）。</summary>
	private void OnAccessChanged(string? key)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action<string?>(OnAccessChanged), key);
			return;
		}
		if (!IsLoaded || (string.IsNullOrEmpty(_lastKeyword) && !_playlistMode))
		{
			return;
		}
		if (_accessRefreshTimer == null)
		{
			var timer = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(400)
			};
			timer.Tick += (_, _) =>
			{
				timer.Stop();
				_accessRefreshTimer = null;
				if (!IsLoaded || (string.IsNullOrEmpty(_lastKeyword) && !_playlistMode))
				{
					return;
				}
				_rowCache.Clear();
				RenderEngine(_selectedEngine, animate: false);
			};
			_accessRefreshTimer = timer;
		}
		_accessRefreshTimer.Start();
	}

	/// <summary>B 站源那一栏当前处在什么状态。
	/// 记状态而不是把 Loc 键存下来：其中一条是 Format 带数量的，
	/// 存键会漏掉参数。</summary>
	private enum BiliStatus { Loading, Blocked, Empty, Count, Fail }

	private BiliStatus _biliStatus = BiliStatus.Loading;
	private int _biliStatusCount;
	private string _biliTrackName = "";

	private void SetBiliStatus(BiliStatus s, int count = 0)
	{
		_biliStatus = s;
		_biliStatusCount = count;
		if (BiliSourceStatusText == null)
		{
			return;
		}
		BiliSourceStatusText.Text = s switch
		{
			BiliStatus.Blocked => Loc.Default["bili_src_blocked"],
			BiliStatus.Empty => Loc.Default["bili_src_none"],
			BiliStatus.Count => Loc.Default.Format("bili_src_count", count),
			BiliStatus.Fail => Loc.Default["cm_fail"],
			_ => Loc.Default["bili_src_load"]
		};
	}

	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		try
		{
			BuildEnginePanel();
			RefreshBiliSourceHeader();
		}
		catch { }
	}

	/// <summary>重刷 B 站源那一栏的标题和状态行。
	/// 状态不重查网络，只把当前那一种状态按新语言重新说一遍。</summary>
	private void RefreshBiliSourceHeader()
	{
		if (BiliSourceHeaderText == null || BiliSourcePanel == null)
		{
			return;
		}
		if (BiliSourcePanel.Visibility != Visibility.Visible)
		{
			return;
		}
		BiliSourceHeaderText.Text = Loc.Default["bili_src_title"] + "  ·  " + _biliTrackName;
		BiliSourceSubText.Text = Loc.Default["bili_src_sub"];
		BiliSourceStatusText.Text = _biliStatus switch
		{
			BiliStatus.Blocked => Loc.Default["bili_src_blocked"],
			BiliStatus.Empty => Loc.Default["bili_src_none"],
			BiliStatus.Count => Loc.Default.Format("bili_src_count", _biliStatusCount),
			BiliStatus.Fail => Loc.Default["cm_fail"],
			_ => Loc.Default["bili_src_load"]
		};
	}

	/// <summary>换强调色：行缓存里存着的行是按旧颜色建的，必须整批丢掉重建，
	/// 否则切换强调色后搜索结果还是上一套颜色的播放键和源标签。</summary>
	private void OnAccentChanged()
	{
		_rowCache.Clear();
		OnlineList.Items.Clear();
		if (!string.IsNullOrEmpty(_lastKeyword) || _playlistMode)
		{
			RenderEngine(_selectedEngine, animate: false);
		}
	}

	public void OnNavigatedTo()
	{
		BuildEnginePanel();
		// 定位球跳转进来时不要自动搜索：那一发随机关键词的搜索会 ResetEngineCaches，
		// 把刚刚准备用来定位的缓存/记忆页码全部冲掉，定位就必败（老失败点）。
		if (_suppressAutoSearchOnce)
		{
			_suppressAutoSearchOnce = false;
		}
		else
		{
			// 从标题栏搜索框发起的搜索：如果还没搜过就补跑一次（页面可能是被延迟创建的）。
			if (!string.IsNullOrWhiteSpace(_pendingKeyword))
			{
				string kw = _pendingKeyword;
				_pendingKeyword = null;
				RunSearch(kw);
			}
			else if (!_autoSearched)
			{
				_autoSearched = true;
				_ = AutoSearchRandomAsync();
			}
		}
	}

	/// <summary>MainWindow 在「点定位球 → NavigateTo(online)」之前置位：
	/// 这次进入在线页时跳过随机自动搜索，避免它把定位要用的缓存冲掉。</summary>
	private bool _suppressAutoSearchOnce;
	public void SuppressAutoSearchOnce() => _suppressAutoSearchOnce = true;

	/// <summary>主窗口在跳转之前先把关键词递过来。页面如果还没建，OnNavigatedTo 会
	/// 取出来跑，并且因此跳过随机自动搜索（省掉一次白跑的请求）；
	/// 页面已经在了就直接搜。
	///
	/// 这里不要再去判断 IsLoaded 然后塞 _pendingKeyword：主窗口的调用顺序是
	/// 先 NavigateTo（内部 OnNavigatedTo + 挂树）才轮到搜索，那时候 OnNavigatedTo
	/// 早就过去了，词塞进去没人取——用户输入的关键词会被静默吃掉，只剩自动搜索结果。</summary>
	public void QueueSearch(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return;
		}
		if (IsLoaded)
		{
			RunSearch(keyword.Trim());
		}
		else
		{
			_pendingKeyword = keyword.Trim();
		}
	}

	private void RunSearch(string keyword)
	{
		_autoSearched = true;
		_ = DoOnlineSearchAsync(keyword);
	}

	private static readonly Random HobbyRandom = new Random();

	/// <summary>进入页面时随机取一个爱好提示词自动搜索，避免空列表。</summary>
	private async Task AutoSearchRandomAsync()
	{
		try
		{
			string raw = SettingsManager.Instance.HobbyKeywords ?? "";
			var pool = raw.Split(new[] { ',', '，', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Trim())
				.Where(s => s.Length > 0)
				.ToList();
			if (pool.Count == 0) return;
			RunSearch(pool[HobbyRandom.Next(pool.Count)]);
			await Task.Yield();
		}
		catch
		{
		}
	}

	/// <summary>四个源各建一个按钮，单选：点谁选中谁，选中态只有强调色描边 + 淡底。</summary>
	private void BuildEnginePanel()
	{
		EnginePanel.Children.Clear();
		_engineButtons.Clear();
		var engines = SettingsManager.Instance.OnlineEngines ?? new List<string>();
		// 老配置里存的是勾选过的多个源，这里取第一个作为默认选中项
		string saved = engines.FirstOrDefault(e => EngineOrder.Contains(e)) ?? _selectedEngine;
		_selectedEngine = EngineOrder.Contains(saved) ? saved : EngineOrder[0];

		foreach (string key in EngineOrder)
		{
			var btn = new Button
			{
				Content = EngineLabel(key),
				MinWidth = 86,
				Padding = new Thickness(14, 6, 14, 6),
				Margin = new Thickness(2, 0, 2, 0),
				Cursor = System.Windows.Input.Cursors.Hand,
				FontSize = 13,
				BorderThickness = new Thickness(1)
			};
			btn.Tag = key;
			btn.Click += EngineButton_Click;
			ApplyEngineButtonVisual(btn, key == _selectedEngine);
			_engineButtons[key] = btn;
			EnginePanel.Children.Add(btn);
		}

		_sortByPlay = SettingsManager.Instance.OnlineSortByPlay;
		BuildSortToggle();
	}

	// 「按播放量排序」开关。加在源按钮行的最右边。
	private TextBlock? _sortHint;
	private CheckBox? _sortByPlayBox;

	/// <summary>排序方式。勾选框是恢复了的功能：之前用户明确要求去掉，
	/// 现在又要求加回来。行为保持原样——B 站按播放量从高到低，
	/// 其它平台保持接口返回顺序（它们根本没这个字段）。</summary>
	private void BuildSortToggle()
	{
		var box = new CheckBox
		{
			Content = Loc.Default["online_sort_play"],
			IsChecked = _sortByPlay,
			FontSize = 11.5,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = ThemeManager.Secondary(),
			Margin = new Thickness(10, 0, 0, 0),
			ToolTip = Loc.Default["online_sort_play_only_bili"]
		};
		box.Checked += SortByPlayBox_Changed;
		box.Unchecked += SortByPlayBox_Changed;
		_sortByPlayBox = box;
		EnginePanel.Children.Add(box);

		// 第二个排序维度：评论数。跟播放量分开做成两个勾选框而不是一个下拉，
		// 是因为两个维度适用���源不一样（B 站两个都有，酷狗只有评论数，
		// 网易云的评论数要另外发请求才拿得到），分开才能各自按源启用/禁用。
		var cbox = new CheckBox
		{
			Content = Loc.Default["online_sort_comment"],
			IsChecked = _sortByComment,
			FontSize = 11.5,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = ThemeManager.Secondary(),
			Margin = new Thickness(10, 0, 0, 0),
			ToolTip = Loc.Default["online_sort_comment_tip"]
		};
		cbox.Checked += SortByCommentBox_Changed;
		cbox.Unchecked += SortByCommentBox_Changed;
		_sortByCommentBox = cbox;
		EnginePanel.Children.Add(cbox);

		_sortHint = new TextBlock
		{
			FontSize = 11.5,
			Foreground = ThemeManager.Tertiary(),
			VerticalAlignment = VerticalAlignment.Center,
			TextWrapping = TextWrapping.NoWrap
		};
		EnginePanel.Children.Add(_sortHint);
		UpdateSortHint();
	}

	private void SortByPlayBox_Changed(object sender, RoutedEventArgs e)
	{
		if (_sortByPlayBox == null) return;
		_sortByPlay = _sortByPlayBox.IsChecked == true;
		SettingsManager.Instance.OnlineSortByPlay = _sortByPlay;
		SettingsManager.Save(SettingsManager.Instance);
		UpdateSortHint();
		// 排序规则变了：丢弃冻结的显示顺序，让渲染时整份重排
		//（这是唯一允许重排旧条目的时机 —— 用户显式要求）。
		InvalidateDisplayOrder(_selectedEngine);
		// 立即重排当前源，不然要等下次搜索才看出生效
		RenderEngine(_selectedEngine);
	}

	private void SortByCommentBox_Changed(object sender, RoutedEventArgs e)
	{
		if (_sortByCommentBox == null) return;
		_sortByComment = _sortByCommentBox.IsChecked == true;
		UpdateSortHint();
		// 评论数重新补齐/规则变化后整份重排，同样先作废冻结顺序。
		InvalidateDisplayOrder(_selectedEngine);
		RenderEngine(_selectedEngine);
		if (_sortByComment)
		{
			_ = FillCommentCountsAsync(_selectedEngine);
		}
	}

	/// <summary>这个源能不能按评论数排。
	/// 四个源都能：B 站/QQ 的搜索结果自带，网易云靠另外补一次请求，
	/// 酷狗的 comment_num 视接口版本给不给 —— 万一没给就是全 0，
	/// 同分按原下标兜底，等于维持平台原顺序，不会把列表搞乱。
	/// 所以这里一律返回 true，不做「这个源不支持」的禁用态（汽水例外，见下）。</summary>
	// 汽水搜索结果不带评论数（SavedTrack 恒为 0），不提供评论数排序，
	// 免得勾上之后「看着排了其实没动」。
	private static bool SupportsCommentCount(string engine) => engine != "soda";

	private static long CommentCountOf(object item) => item switch
	{
		BiliTrack b => b.CommentCount,
		NeteaseTrack n => n.CommentCount,
		QqTrack q => q.CommentCount,
		// 酷狗的评论数解析层已经填好了，之前这里漏了它：
		// 结果酷狗勾上评论数排序后所有人都是 0，等于按原顺序返回，
		// 看着像「排序没生效」但又没报错。
		KugouTrack k => k.CommentCount,
		_ => 0
	};

	/// <summary>把当前源的评论数补齐再重排一次。
	///
	/// 只有勾了评论数排序才会走这里，而且只处理当前这一页（30 条）。
	/// 网易云的评论数不在搜索结果里，一条歌一次请求，并发压到 6：
	/// 再高就容易被判成爬虫，反而一条都取不回来。
	/// 取不到的（失败）保持原值，不写 -1，这样下次翻页还会再试一次。
	/// 过程中不再往左下角写「已补 x/y」的进度（用户要求去掉）：
	/// 这一步在后台跑，本来就没在等它，弹个进度反而像卡住了；
	/// 排序完成的提示由 UpdateSortHint 的 online_sort_comment_on 负责。</summary>
	private async Task FillCommentCountsAsync(string engine)
	{
		if (_fillingCommentCounts || !_sortByComment || !SupportsCommentCount(engine)) return;
		if (!_engineCache.TryGetValue(engine, out List<object>? cache) || cache.Count == 0) return;
		_fillingCommentCounts = true;
		try
		{
			int start = (_visiblePage.TryGetValue(engine, out int vp) ? Math.Max(1, vp) : 1) - 1;
			int from = Math.Max(0, start * PageSize);
			int to = Math.Min(cache.Count, from + PageSize);
			var pending = new List<NeteaseTrack>();
			for (int i = from; i < to; i++)
			{
				// 网易云之外的源（现在只有 B 站）搜索结果里就带着评论数，不用问。
				if (cache[i] is NeteaseTrack n && n.CommentCount < 0)
				{
					pending.Add(n);
				}
			}
			if (pending.Count == 0) return;
			const int Concurrency = 6;
			foreach (var batch in Chunk(pending, Concurrency))
			{
				await Task.WhenAll(batch.Select(async n =>
				{
					long? total = await OnlineComments.FetchTotalAsync("netease", n);
					if (total.HasValue)
					{
						n.CommentCount = total.Value;
					}
				}));
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "补齐评论数失败");
		}
		finally
		{
			_fillingCommentCounts = false;
		}
		if (IsLoaded && IsCurrentPage())
		{
			// 评论数是新数据：想按评论数排就得整份重排，先作废冻结顺序
			//（用户显式开了评论数排序，这属于「要求重排」）。
			InvalidateDisplayOrder(_selectedEngine);
			RenderEngine(_selectedEngine, animate: false);
		}
	}

	private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
	{
		for (int i = 0; i < source.Count; i += size)
		{
			yield return source.GetRange(i, Math.Min(size, source.Count - i));
		}
	}

	private bool IsCurrentPage()
	{
		return MainWindow.Instance?.IsCurrentPage(this) ?? false;
	}

	private void UpdateSortHint()
	{
		if (_sortHint == null)
		{
			return;
		}
		// 只有 B 站的搜索结果带播放量，其它三个平台没这个字段，提示里说清楚。
		bool applicable = SupportsPopularity(_selectedEngine);
		// 勾选框只在当前源真的支持播放量排序时才可点。
		// 不支持还让它可点，用户会以为点了没用——其实按了也确实是没用。
		if (_sortByPlayBox != null)
		{
			_sortByPlayBox.IsEnabled = applicable;
			_sortByPlayBox.Opacity = applicable ? 1.0 : 0.45;
		}
		// 评论数：三个源都支持（网易云靠补请求，B 站/QQ 搜索结果自带）。
		if (_sortByCommentBox != null)
		{
			bool cmOk = SupportsCommentCount(_selectedEngine);
			_sortByCommentBox.IsEnabled = cmOk;
			_sortByCommentBox.Opacity = cmOk ? 1.0 : 0.45;
		}
		if (_lastKeyword.Length == 0)
		{
			_sortHint.Visibility = Visibility.Collapsed;
		}
		else if (_sortByComment && SupportsCommentCount(_selectedEngine))
		{
			_sortHint.Visibility = Visibility.Visible;
			_sortHint.Text = Loc.Default["online_sort_comment_on"];
		}
		else
		{
			_sortHint.Visibility = Visibility.Visible;
			_sortHint.Text = applicable
				? (_sortByPlay
					? Loc.Default["online_sort_play_on"]
					: Loc.Default["online_sort_play_off"])
				: Loc.Default["online_sort_play_only_bili"];
		}
	}

	private static string EngineLabel(string key) => Loc.Default[key switch
	{
		"netease" => "set_eng_netease",
		"kugou" => "set_eng_kugou",
		"qq" => "set_eng_qq",
		"soda" => "set_eng_qishui",
		_ => "set_eng_bili"
	}];

	/// <summary>选中只改描边和一点淡底，颜色全部取主题资源，所以换深/浅色会自动跟上。</summary>
	private void ApplyEngineButtonVisual(Button btn, bool selected)
	{
		btn.Background = selected ? ThemeManager.AccentSubtle() : Brushes.Transparent;
		btn.BorderBrush = selected ? ThemeManager.Accent() : ThemeManager.CardBorder();
		btn.Foreground = selected ? ThemeManager.Primary() : ThemeManager.Secondary();
		btn.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
	}

	private void EngineButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string key })
		{
			return;
		}
		// 歌单模式直接拦：结果列表只装当前歌单（单平台、无翻页），
		// 切源没有意义；更重要的是不能让 _selectedEngine 漂到空源上，
		// 否则实测标签刷新（OnAccessChanged）会把整个列表重绘成空白。
		if (_playlistMode && !string.Equals(_selectedEngine, key, StringComparison.Ordinal))
		{
			return;
		}
		bool changed = !string.Equals(_selectedEngine, key, StringComparison.Ordinal);
		SwitchEngine(key);

		if (string.IsNullOrEmpty(_lastKeyword))
		{
			return;
		}
		RenderEngine(key);
		// B 站替代源面板的唯一手动入口：用户自己切到「BiliBili 源下载」这一栏，
		// 且之前点过受限歌（记了曲名+歌手上下文）时，把候选面板摆出来让人挑；
		// 播放链路里已无任何自动触发路径。没有受限上下文就不弹——这时列表里
		// 就是本次关键词的 B 站搜索结果，面板没东西可摆。
		if (string.Equals(key, "bili", StringComparison.Ordinal) && changed && _lastVipName.Length > 0)
		{
			ShowVipFallback(_lastVipName, _lastVipArtist);
		}
		// 评论数排序开着的话，切源后要把新源的评论数补齐再排一次，
		// 不然用户会看到「勾着按评论数，结果顺序和没勾时一样」。
		if (_sortByComment)
		{
			_ = FillCommentCountsAsync(key);
		}
		// 切到某个源就顺手把它下一页预取掉。RenderEngine 末尾统一会做这件事，
		// 这里不用再单独调一次。
	}

	/// <summary>切源（源按钮/定位球记忆恢复共用）：换选中态、落盘、刷排序提示。
	/// 只有真换了源才写设置——SettingsManager.Save 是同步写文件，
	/// 同一个源反复点也去写一遍，点按钮的手感会发涩。</summary>
	private void SwitchEngine(string key)
	{
		bool changed = !string.Equals(_selectedEngine, key, StringComparison.Ordinal);
		_selectedEngine = key;
		foreach (var kv in _engineButtons)
		{
			ApplyEngineButtonVisual(kv.Value, string.Equals(kv.Key, key, StringComparison.Ordinal));
		}
		if (changed)
		{
			var s = SettingsManager.Instance;
			s.OnlineEngines = new List<string> { key };
			SettingsManager.Save(s);
		}
		UpdateSortHint();
	}

	/// <summary>「停在第 P 页就把第 P+1 页预取掉」——这是消除翻页等待的关键。
	/// 搜索完成、切换源、点上一页/下一页走过的每条路径最后都会渲染一次，
	/// 由 RenderEngine 在收尾处统一调用，所以节奏始终是
	/// 第 1 页在屏 → 第 2 页在飞 → 翻到第 2 页 → 第 3 页开飞。
	/// 预取只往后台加数据，不改当前页的内容，所以不会打断正在进行的滚动。</summary>
	private void EnsurePrefetchAhead()
	{
		string engine = _selectedEngine;
		if (string.IsNullOrEmpty(_lastKeyword) || _searching || _loadingMore)
		{
			return;
		}
		_ = PrefetchNextPageAsync(engine);
	}

	/// <summary>把下一页前几首的封面提前拉进缓存（只进缓存不显示）。
	/// 数据预取（EnsurePrefetchAhead）只拉列表数据，封面还是要等行进视口
	/// 才发请求——翻页后那一下「占位 → 图」的突兀切换就是封面闪现的来源。
	/// 提前 8 张：翻页后首屏正好全覆盖，字节/磁盘缓存命中时解码即显。
	/// 已缓存的不发请求（PreloadAsync 自己判断），失败静默，不影响列表。</summary>
	private void PreloadNextPageCovers(string engine)
	{
		try
		{
			if (!_engineCache.TryGetValue(engine, out List<object>? cache) || cache.Count == 0)
			{
				return;
			}
			int visible = _visiblePage.TryGetValue(engine, out int vp) ? vp : 1;
			int start = visible * PageSize;
			if (start >= cache.Count)
			{
				return;
			}
			int end = Math.Min(cache.Count, start + 8);
			for (int i = start; i < end; i++)
			{
				string? cover = ToSavedTrack(cache[i])?.Cover;
				if (!string.IsNullOrWhiteSpace(cover))
				{
					_ = CoverLoader.PreloadAsync(cover);
				}
			}
		}
		catch
		{
			// 预取封面是锦上添花，出任何状况都不影响列表本身。
		}
	}

	private void ResetEngineCaches()
	{
		_engineCache.Clear();
		_engineSeen.Clear();
		_nextPage.Clear();
		_exhausted.Clear();
		_prefetching.Clear();
		_visiblePage.Clear();
		_engineQuery.Clear();
		// 数据整个换了，冻结的显示顺序随之作废（下次渲染按新数据重排）。
		_displayOrder.Clear();
		// 数据整个换了，之前建好的行控件全部作废。
		_rowCache.Clear();
		// B站卡片同理：新搜索的 bvid 集合完全不同，旧卡片不再复用。
		_biliCardCache.Clear();
		foreach (string key in EngineOrder)
		{
			_engineCache[key] = new List<object>();
			_engineSeen[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			_nextPage[key] = 0;
			_exhausted[key] = false;
			_visiblePage[key] = 1;
		}
	}

	/// <summary>账号页收藏夹把某个歌单的整份曲目直接灌进结果列表（不走搜索，由 MainWindow 调）。
	/// 曲目已经按歌单拉好，这里只接管列表状态并渲染：
	/// <list type="bullet">
	/// <item>进「歌单模式」（<see cref="_playlistMode"/>）：保序、不重排、无翻页、不预取；</item>
	/// <item>行控件仍走 GetOrBuildRow → BuildEngineRow，播放/下载/VIP 实测标签和搜索结果完全一致；</item>
	/// <item>置 <see cref="_autoSearched"/>：页面可能还没进过，OnNavigatedTo 里的随机自动搜索
	/// 不许再跑，否则会把刚灌进去的歌单整个冲掉。</item>
	/// </list></summary>
	public void ShowExternalPlaylist(string engine, string playlistName, List<object> tracks, string? playlistId = null)
	{
		int token = ++_searchToken; // 让还在飞的搜索结果作废，免得它回头把歌单覆盖掉
		_searching = false;
		_loadingMore = false;
		HideLoading();
		_autoSearched = true;
		_playlistMode = true;
		_playlistId = playlistId ?? "";
		// 不把歌单名当搜索词：相关度排序、行内关键词高亮都不该对歌单名生效，
		// RenderEngine 里「按像不像搜索词排序」的分支也就自然不会命中。
		_lastKeyword = "";
		ResetEngineCaches();
		MergeInto(engine, tracks);
		_nextPage[engine] = 0;
		_exhausted[engine] = true; // 歌单就这一份，没有下一页
		_selectedEngine = engine;
		// 把这个源落成「默认选中源」：随后的 OnNavigatedTo 会跑 BuildEnginePanel，
		// 而它按设置项覆盖 _selectedEngine——不先写进去的话，高亮选中的源
		// 和实际展示的歌单引擎会对不上（配置里存的是 kugou、灌的是 netease 就穿帮了）。
		// 行为和手点源按钮一致（点按钮也是这么落盘的）。
		SettingsManager.Instance.OnlineEngines = new List<string> { engine };
		SettingsManager.Save(SettingsManager.Instance);
		if (engine == "netease")
		{
			// 网易云歌单要能顺着队列「下一首/上一首」播下去
			SyncNeteaseQueue();
		}
		HintText.Visibility = Visibility.Collapsed;
		SetStatus(Loc.Default.Format("online_playlist_loaded", playlistName, tracks.Count));
		RenderEngine(engine, animate: false);
		LogHelper.LogDebug("灌入歌单: engine=" + engine + " name=" + playlistName + " 条数=" + tracks.Count + " token=" + token);
	}

	/// <summary>搜索：四个源第 1 页一起并发拉（用户要的「四个源提前搜」），
	/// 拉完只渲染当前选中源的那一份。</summary>
	private async Task DoOnlineSearchAsync(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return;
		}
		int token = ++_searchToken;
		// 新搜索一来就退出歌单模式：结果列表换回搜索语义（重排/翻页/预取全恢复）
		_playlistMode = false;
		_playlistId = "";
		_searching = true;
		_loadingMore = false;
		HideLoading();
		HintText.Visibility = Visibility.Collapsed;
		ShowLoading(Loc.Default["online_loading_search"]);
		try
		{
			ResetEngineCaches();
			_lastKeyword = keyword;
			foreach (string key in EngineOrder)
			{
				_engineQuery[key] = keyword;
			}

			var tasks = new Dictionary<string, Task<List<object>>>();
			foreach (string key in EngineOrder)
			{
				tasks[key] = FetchEnginePageAsync(key, keyword, 0);
			}
			await Task.WhenAll(tasks.Values);
			if (token != _searchToken)
			{
				// 期间用户又搜了一次，这批结果作废
				return;
			}

			foreach (var kv in tasks)
			{
				List<object> got = await kv.Value;
				MergeInto(kv.Key, got);
				// 结果里的歌名记进补全引擎：下次敲「Bad Ap」就能补出「Bad Apple!!」。
				// 只记歌名不记别的，出现在列表里记 1 分（播了的记 3 分，在播放那边）。
				QueryBrain.NoteTitles(got.Select(Title));
			}

			await RetryEmptyEnginesWithVariantsAsync(keyword, token);
			if (token != _searchToken)
			{
				return;
			}
			// 结果太单薄就再补一批相似的：只搜那一首歌名时，
			// 三十条结果常常去重完只剩一行，既看不出命中没有也没法往下翻。
			await FillSimilarResultsAsync(keyword, token);
			if (token != _searchToken)
			{
				return;
			}

			// VIP / 版权受限的歌不再往本源的结果里插 B 站候选。
			//
			// 原来搜完就把 B 站候选顶到该源最前面，看着是「贴心」，实际最别扭：
			// 用户在网易云这一栏里翻到的却是 B 站视频，行标签、时长、可用操作全不一样，
			// 而且分页/去重的条数对不上。现在 B 站结果只待在「BiliBili 源下载」那一栏
			// （同一个关键词已经搜过了，切过去就能看），这里只提示一句。
			int restricted = CountRestricted(_selectedEngine);

			// 第 1 页（pageIndex=0）已经在缓存里了，游标必须推到 1。
			// 之前忘了推，_nextPage 一直停在 0，后面点源按钮时
			// PrefetchNextPageAsync 的 `_nextPage[engine] <= 0` 判断直接把预取挡掉了，
			// 于是「点源才搜第二页」这个行为从来没生效过。
			foreach (string key in EngineOrder)
			{
				_nextPage[key] = 1;
			}

			SyncNeteaseQueue();
			RenderEngine(_selectedEngine);
			// 定位球记忆恢复（RevealFromMemo 触发的重搜）走完了：
			// 在这里翻到记忆页码并居中高亮收尾。
			CompletePendingLocateRestore();
			LogHelper.Log("在线搜索: " + keyword + " -> "
				+ string.Join(" / ", EngineOrder.Select(k => k + " " + _engineCache[k].Count)));
			// 全空时报出真实原因。QQ/B站 0 条多数是被限流，不是「没这首歌」，
			// 不说清楚的话用户只会反复重搜。
			SetStatus(BuildSearchStatus(keyword));

			// 提示放在最后：SetStatus 会掐掉定时器并清空行，
			// 早一步设置的话这条一次性说明会被下面的状态覆盖掉。
			if (restricted > 0)
			{
				ShowStatusForSeconds(Loc.Default["vip_fallback"], 10);
			}
		}
		catch (Exception ex)
		{
			if (token == _searchToken)
			{
				SetStatus(Loc.Default["cm_fail"]);
				LogHelper.LogError(ex, "在线搜索失败: " + keyword);
			}
		}
		finally
		{
			if (token == _searchToken)
			{
				_searching = false;
				HideLoading();
			}
		}
	}

	/// <summary>搜不到结果的源，用「等价写法」再搜一遍。
	///
	/// 各平台的搜索接口对简繁的处理不一样：有的两边都收，有的只收自己那一边。
	/// 于是「云鹤」搜不到《雲鶴サクリファイス》，反过来用繁体词搜简体歌也可能落空。
	/// 这里把关键词扩成几个等价词（简转繁 / 繁转简 / 去掉 feat.、Live 这类括号装饰），
	/// 只给**仍然是空的源**补搜，已经有结果的源一个请求都不多打——
	/// 多花的那点流量只在「用户没搜到东西」的时候才发生，搜到了就完全不走这里。
	///
	/// 用哪个词搜出来的会记进 _engineQuery，翻页时继续用同一个词。</summary>
	private async Task RetryEmptyEnginesWithVariantsAsync(string keyword, int token)
	{
		if (EngineOrder.All(k => _engineCache[k].Count > 0))
		{
			return;
		}
		IReadOnlyList<string> variants = SearchMatcher.QueryVariants(keyword);
		// 第 0 个就是原词，主查询已经用过了，从第 1 个开始。
		for (int i = 1; i < variants.Count; i++)
		{
			if (token != _searchToken)
			{
				return;
			}
			string variant = variants[i];
			var empty = new List<string>();
			foreach (string key in EngineOrder)
			{
				if (_engineCache[key].Count == 0)
				{
					empty.Add(key);
				}
			}
			if (empty.Count == 0)
			{
				return;
			}

			var retry = new Dictionary<string, Task<List<object>>>();
			foreach (string key in empty)
			{
				retry[key] = FetchEnginePageAsync(key, variant, 0);
			}
			await Task.WhenAll(retry.Values);
			if (token != _searchToken)
			{
				return;
			}

			bool gained = false;
			foreach (var kv in retry)
			{
				List<object> items = await kv.Value;
				if (items.Count == 0)
				{
					continue;
				}
				gained = true;
				MergeInto(kv.Key, items);
				_engineQuery[kv.Key] = variant;
			}
			if (gained)
			{
				LogHelper.Log("在线搜索: 「" + keyword + "」空，用扩展词「" + variant + "」补搜到结果");
				return;
			}
		}
	}

	/// <summary>这个源的结果里有多少首是 VIP / 版权受限。
	///
	/// 只用来在搜索完提示一句「切到 BiliBili 源下载也能听」，
	/// 不会再去动任何一个源的缓存：B 站结果一律只待在 B 站那一栏。
	/// B 站自己被选中时这里自然是 0（B 站结果没有受限字段）。</summary>
	private int CountRestricted(string engine)
	{
		if (!_engineCache.TryGetValue(engine, out List<object>? cache))
		{
			return 0;
		}
		int n = 0;
		foreach (object item in cache)
		{
			if (IsRestricted(item))
			{
				n++;
			}
		}
		return n;
	}

	/// <summary>这条结果是不是 VIP / 版权受限。
	///
	/// 不直接读平台字段：网易云的 fee、QQ 的 payplay、酷狗的 pay_type 只能说明
	/// 「这首对口要钱」，跟本程序现在能不能播它是两回事 —— 会员赠送、专辑解禁、
	/// 低音质免费、账号 cookie 都会让字段失真，照着打标签就会出现
	/// 「标着 VIP，点开能听还能下」。
	/// TrackAccess 是「平台字段（收紧后）+ 实测结论」的合成判定：
	/// 真的拉过直链、成功过一次就记得能播，接口明确拒绝才记得受限。</summary>
	private static bool IsRestricted(object item) => TrackAccess.IsRestricted(item);

	/// <summary>拉单个源的一页。B 站只有第 1 页。</summary>
	private static async Task<List<object>> FetchEnginePageAsync(string engine, string keyword, int pageIndex)
	{
		int offset = pageIndex * PageSize;
		switch (engine)
		{
			case "netease":
				return (await NeteaseApiClient.SearchAsync(keyword, PageSize, offset)).Cast<object>().ToList();
			case "kugou":
				return (await KugouClient.SearchAsync(keyword, PageSize, offset)).Cast<object>().ToList();
			case "qq":
				return (await QqClient.SearchAsync(keyword, PageSize, offset)).Cast<object>().ToList();
			case "soda":
				// 汽水搜索：/luna/pc/search/all，无需登录 Cookie（实测匿名可搜，
				// 只是结果条数受服务端限制）。返回 SavedTrack（soda），行渲染/
				// 收藏/定位与其它源走同一套通用链路。
				return (await SodaClient.FetchSearchAsync(keyword, PageSize, offset)).Cast<object>().ToList();
			default:
				return (await BiliClient.SearchAsync(keyword)).Cast<object>().ToList();
		}
	}

	/// <summary>没有结果时要说清是「没搜到」还是「接口限流」，
	/// 否则用户看到 0 条只会以为歌不存在。</summary>
	private string BuildSearchStatus(string keyword)
	{
		if (EngineOrder.Any(k => _engineCache[k].Count > 0))
		{
			return "";
		}
		if (QqClient.LastSearchUnavailable)
		{
			return Loc.Default["on_src_unavailable"];
		}
		if (BiliClient.LastSearchBlocked)
		{
			return Loc.Default["bili_src_blocked"];
		}
		return Loc.Default.Format("on_src_none", keyword);
	}

	/// <summary>把一批结果并进某个源的缓存，按「歌名 + 首位歌手」去重。
	///
	/// <paramref name="excludeDecorated"/> 打开时，括号里写着 Remix / 重制 /
	/// Live / 翻唱 / 伴奏 这类后缀的条目直接丢掉 —— 那是同名衍生版，不算
	/// 「相似结果」，放进来只会把真正的同名单曲挤下去。</summary>
	private void MergeInto(string engine, List<object> items, bool excludeDecorated = false)
	{
		if (!_engineCache.TryGetValue(engine, out List<object>? cache) || _engineSeen[engine] == null)
		{
			return;
		}
		HashSet<string> seen = _engineSeen[engine];
		// 显示顺序已冻结时就地追加新条目（队尾）：预取/补搜来的数据不会因为
		// 重新排序挤进当前可见页，可见页保持冻结，也就不需要重画。
		// 顺序还没建（首次渲染前）时不追加，DisplayOrderFor 会在渲染时整份排好。
		List<object>? frozenOrder = _displayOrder.TryGetValue(engine, out List<object>? ord) ? ord : null;
		foreach (object item in items)
		{
			string raw = Title(item);
			if (excludeDecorated && IsDecoratedVariant(raw))
			{
				continue;
			}
			string key = Normalize(raw) + "|" + Normalize(FirstArtist(ArtistOf(item)));
			if (seen.Add(key))
			{
				cache.Add(item);
				frozenOrder?.Add(item);
			}
		}
	}

	/// <summary>网易云的播放队列走这里；只认当前缓存里有的曲子。</summary>
	private void SyncNeteaseQueue()
	{
		var netease = _engineCache.TryGetValue("netease", out List<object>? cache)
			? cache.OfType<NeteaseTrack>().ToList()
			: new List<NeteaseTrack>();
		MusicService.SetOnlineQueue(netease);
	}

	/// <summary>括号里写着衍生版标记：Remix / 重制 / Live / 翻唱 / 伴奏 / 纯音乐……
	///
	/// 这些是「同名但不是那首歌」的典型。补相似结果时要把它们挡掉，
	/// 否则一个热歌关键词丢进去，前排会被同一首歌的十几个混音版占满，
	/// 真正的原版和其它歌反而排不上。</summary>
	private static readonly Regex DecoratedVariant = new(
		@"[\(\（\[【][^\)\）\]】]*?(remix|re-?mix|remaster|re:?master|edit|bootleg|sped\s?up|slowed|nightcore|inst\.?|instrumental|off\s?-?\s?vocal|acoustic|demo|live|现场|翻唱|翻奏|cover|歌ってみた|重制|重置|重混|混音|伴奏|纯音乐|钢琴版|钢琴|试听版|特别版|周年版)[^\)\）\]】]*?[\)\）\]】]",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>这条结果的歌名里带衍生版标记（括号里有 Remix / 重制 之类）。</summary>
	private static bool IsDecoratedVariant(string? title)
	{
		return !string.IsNullOrWhiteSpace(title) && DecoratedVariant.IsMatch(title);
	}

	/// <summary>歌名去掉括号装饰后与搜索词是同一首（再忽略简繁、大小写、空格）。</summary>
	private static bool IsExactTitleHit(object item, string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return false;
		}
		string b = SearchMatcher.Normalize(SearchMatcher.StripDecorators(keyword));
		if (b.Length == 0)
		{
			return false;
		}
		string a = SearchMatcher.Normalize(Title(item));
		return a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
	}

	/// <summary>单个源的结果少得可怜时，用「相近写法」再补一批。
	///
	/// 为什么要补：搜一整首完整的歌名时，接口给的三十条经常是同一首歌被收进
	/// 不同专辑、不同合集，按「歌名 + 歌手」去重之后屏幕上就剩孤零零一行。
	/// 一行既看不出「这就是我要的那首」还是「只有这么点」，也没法往下翻。
	///
	/// 补的这一批按顺序试，够了就停：
	///   ① 歌名去掉括号装饰后的原名（「XXX (Remix)」→「XXX」），
	///      把原版和其它版本捞进来；
	///   ② 还差太远的话，拿当前排第一那首的歌手再搜一轮，
	///      同歌手的其它歌也算「相似」。
	/// 带衍生版标记的条目一律丢掉（见 IsDecoratedVariant）。</summary>
	private async Task FillSimilarResultsAsync(string keyword, int token)
	{
		// 少于这个数就算「太单薄」，需要补。一屏能放三十行，十二行以下确实太空。
		const int ThinResultCount = 12;
		foreach (string engine in EngineOrder)
		{
			if (token != _searchToken)
			{
				return;
			}
			if (!_engineCache.TryGetValue(engine, out List<object>? cache)
				|| cache.Count >= ThinResultCount)
			{
				continue;
			}

			// ① 原名（去括号装饰）。和主查询同一个源、同一条链路，去重表是共用的。
			string bare = SearchMatcher.StripDecorators(keyword).Trim();
			if (bare.Length > 0 && !string.Equals(bare, keyword, StringComparison.OrdinalIgnoreCase))
			{
				List<object> got = await SafeFetchAsync(engine, bare, 0);
				if (token != _searchToken)
				{
					return;
				}
				MergeInto(engine, got, excludeDecorated: true);
			}

			// ② 还不够就按排第一那首的歌手再补一轮。
			if (cache.Count < ThinResultCount)
			{
				object? top = cache.Count > 0 ? cache[0] : null;
				string singer = top == null ? "" : FirstArtist(ArtistOf(top));
				if (singer.Length > 0 && !string.Equals(singer, keyword, StringComparison.OrdinalIgnoreCase))
				{
					List<object> got = await SafeFetchAsync(engine, singer, 0);
					if (token != _searchToken)
					{
						return;
					}
					MergeInto(engine, got, excludeDecorated: true);
				}
			}
		}
	}

	/// <summary>拉一页，失败就当成「这个源这次没有结果」，不往上抛。</summary>
	private static async Task<List<object>> SafeFetchAsync(string engine, string query, int pageIndex)
	{
		try
		{
			return await FetchEnginePageAsync(engine, query, pageIndex);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "补充相似结果失败: " + engine + " / " + query);
			return new List<object>();
		}
	}

	/// <summary>某个源的下一页：结果追加进缓存。</summary>
	private async Task<bool> FetchNextPageAsync(string engine, int pageIndex)
	{
		int token = _searchToken; // 起飞时记下搜索代次
		List<object> items;
		try
		{
			// 用这个源当初搜出第 1 页的那个词，不是用户输入的词：
			// 第 1 页可能是靠扩展词补搜出来的，翻页得接着同一个词往下拉。
			string query = _engineQuery.TryGetValue(engine, out string? q) && !string.IsNullOrEmpty(q)
				? q
				: _lastKeyword;
			items = await FetchEnginePageAsync(engine, query, pageIndex);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "加载 " + engine + " 第 " + (pageIndex + 1) + " 页失败");
			return false;
		}
		// 预取在飞时用户重搜/切源（ResetEngineCaches 会整份清掉缓存并换代次），
		// 这页数据属于上一轮关键词：不做代次校验的话它会落进新一轮的缓存，
		// _nextPage 还会被旧页号顶乱 —— 新搜索结果直接被旧词数据污染。
		if (token != _searchToken)
		{
			LogHelper.LogDebug("预取作废: " + engine + " 第 " + (pageIndex + 1) + " 页（搜索已换轮）");
			return false;
		}
		if (!_engineCache.TryGetValue(engine, out List<object>? cache) || _engineSeen[engine] == null)
		{
			return false;
		}
		int before = cache.Count;
		MergeInto(engine, items);
		_nextPage[engine] = pageIndex + 1;
		if (engine == "netease")
		{
			SyncNeteaseQueue();
		}
		// 一条都没新加进来 = 接口翻来覆去给的都是同一批（或者干脆是空的）。
		// 原来只认「返回 0 条」才算到底，于是有些关键词能一直点「下一页」，
		// 翻过去却是一片空白 —— 因为新拿到的那 30 条早就被去重吃掉了。
		if (cache.Count == before)
		{
			_nextPage[engine] = pageIndex;
			_exhausted[engine] = true;
			return false;
		}
		// 不足一整页 = 接口后面没有更多了。分页接口只有一种情况会不满页返回：
		// 剩下的条数不够凑满 PageSize。这里当场记成已加载完，
		// 省得再白发一次请求才知道到底。
		if (items.Count < PageSize)
		{
			_exhausted[engine] = true;
		}
		return true;
	}

	/// <summary>后台预取某源的下一页。用户要的行为是：
	/// 搜索时只并发拉四源第 1 页 -> 点到某个源才开始拉它第 2 页
	/// -> 第 2 页到手后才开始拉第 3 页。所以这里只在「已经看过第 1 页」之后才动。
	/// 落地不重绘（见 RunPrefetchAsync）：新条目单向追加进队尾，可见页不变。</summary>
	private Task PrefetchNextPageAsync(string engine)
	{
		if (!SupportsPaging(engine)
			|| _lastKeyword.Length == 0
			|| _searching
			|| _exhausted.TryGetValue(engine, out bool done) && done
			|| _nextPage[engine] <= 0        // 第 1 页还没进缓存，别跳着拉第 2 页
			|| _prefetching.ContainsKey(engine))  // 这个源已经在预取中了
		{
			return Task.CompletedTask;
		}
		// 接力预取的上限：最多比可见页超前 MaxAutoPrefetchAhead 页。
		// 上一轮没有这个闸，「落地→EnsurePrefetchAhead→再落地」的链会一口气
		// 把 600 条结果的前六七页全部拉下来（日志证据同上），网络白打、
		// 缓存白占。超过上限就停，等用户真翻过去再补。
		int visible = _visiblePage.TryGetValue(engine, out int vp) ? Math.Max(1, vp) : 1;
		if (_nextPage[engine] - visible >= MaxAutoPrefetchAhead)
		{
			return Task.CompletedTask;
		}
		int page = _nextPage[engine];
		Task task = RunPrefetchAsync(engine, page);
		_prefetching[engine] = task;
		return task;
	}

	/// <summary>自动接力预取最多比可见页超前几页。2 = 当前页的后两页提前就绪，
	/// 用户点「下一页」不用等，又不至于把整份结果一口气拉完。</summary>
	private const int MaxAutoPrefetchAhead = 2;

	private async Task RunPrefetchAsync(string engine, int page)
	{
		try
		{
			bool got = await FetchNextPageAsync(engine, page);
			// 预取落地一律不重画。新条目在 MergeInto 里被单向追加到显示顺序的
			// 队尾（顺序一旦冻结就不再整份重排），当前可见页的内容不可能被
			// 预取改变 —— 上一轮的「可见页快照比对」还会闪，根因是相关性排序
			// 每次都在整份缓存上重算，第 2 页的新条目总能挤进第 1 页前 30 名，
			// 比对永远判「变了」：日志里 20:00:14-19 无用户操作六秒内
			// kugou2→qq2→qq3→kugou3→netease2→kugou4→netease3 连环落地，
			// 每次落地都是一次整页 Clear 重建（行重挂树+动画广播重订阅=闪烁，
			// 行缓存/封面预取成倍膨胀=内存涨）。现在只刷新页脚并接力预取。
			if (got && string.Equals(engine, _selectedEngine, StringComparison.Ordinal))
			{
				LogHelper.Log($"预取 {engine} 第 {page + 1} 页落地：只追加不重画（可见页冻结）");
				UpdateFooter();
				EnsurePrefetchAhead();
			}
			LogHelper.Log($"预取 {engine} 第 {page + 1} 页: {(got ? "ok" : "无更多")}");
		}
		finally
		{
			_prefetching.Remove(engine);
		}
	}

	/// <summary>某个源的显示顺序。首次访问时按当前排序规则整份排好并冻结；
	/// 之后 MergeInto 的新条目只会追加到队尾。排序规则变了（用户勾了
	/// 播放量/评论数排序）由对应开关调用 InvalidateDisplayOrder 重排。
	/// RenderEngine、定位球、页码计算必须共用这一份，页码才不会对不上。</summary>
	private List<object> DisplayOrderFor(string engine)
	{
		if (!_displayOrder.TryGetValue(engine, out List<object>? list))
		{
			_engineCache.TryGetValue(engine, out List<object>? cache);
			list = OrderForDisplay(engine, cache ?? new List<object>());
			_displayOrder[engine] = list;
		}
		return list;
	}

	/// <summary>丢弃某个源的冻结显示顺序，下次渲染时按当前规则整份重排。
	/// 只在用户显式改排序规则时调用 —— 平时缓存增长一律走单向追加。</summary>
	private void InvalidateDisplayOrder(string engine) => _displayOrder.Remove(engine);

	/// <summary>等某个源当前在飞的预取结束（没有在飞就立刻返回）。</summary>
	private async Task AwaitPrefetchAsync(string engine)
	{
		Task? running;
		if (_prefetching.TryGetValue(engine, out running) && running != null)
		{
			try
			{
				await running;
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "等待预取失败");
			}
		}
	}

	/// <summary>把某引擎的缓存排成「显示顺序」（RenderEngine 与 RevealPlayingRow 共用）。
	/// 评论数排序优先级最高：它是用户明确勾的，就该压过播放量这种默认维度。
	/// 同分再按播放量兜底，全平就按原下标，保证稳定排序。
	/// 歌单模式三个排序分支全部跳过：歌单顺序是用户自己排的，重排等于把用户的整理打乱。
	/// 两处必须走同一份排序：定位球按显示顺序算页码，各写一份迟早排岔、翻错页。</summary>
	private List<object> OrderForDisplay(string engine, List<object> cache)
	{
		if (!_playlistMode && _sortByComment && SupportsCommentCount(engine))
		{
			bool byPlay = SupportsPopularity(engine) && _sortByPlay;
			return cache
				.Select((item, idx) => (item, idx, cm: CommentCountOf(item), pop: byPlay ? PopularityOf(item) : 0L))
				.OrderByDescending(t => t.cm)
				.ThenByDescending(t => t.pop)
				.ThenBy(t => t.idx)
				.Select(t => t.item)
				.ToList();
		}
		// B 站按播放量重排整份缓存。行控件按「条目 key」缓存，
		// 所以重排只改变行的先后顺序，不需要重建任何控件。
		if (!_playlistMode && SupportsPopularity(engine) && _sortByPlay)
		{
			return cache
				.Select((item, idx) => (item, idx, pop: PopularityOf(item)))
				.OrderByDescending(t => t.pop)
				.ThenBy(t => t.idx)
				.Select(t => t.item)
				.ToList();
		}
		if (!_playlistMode && cache.Count > 1 && _lastKeyword.Length > 0)
		{
			// 其它源按「像不像用户搜的那首」排。
			// 接口给的是热度混相关度，歌名里带关键词的那几首经常掉到第二页去；
			// 排序键相同就按原下标兜底，保证是稳定排序，不会每刷新一次顺序都变。
			//
			// 完全同名的那条（忽略括号装饰、简繁、大小写）额外加 2 分：
			// 搜「XXX (Remix)」时，排最前面的就该是那首 Remix 本体，
			// 而不是恰好也带这几个字的另一首歌。
			return cache
				.Select((item, idx) => (item, idx, score: SearchMatcher.Score(Title(item), ArtistOf(item), _lastKeyword)
					+ (IsExactTitleHit(item, _lastKeyword) ? 2.0 : 0.0)))
				.OrderByDescending(t => t.score)
				.ThenBy(t => t.idx)
				.Select(t => t.item)
				.ToList();
		}
		return cache;
	}

	/// <summary>定位球用：在当前引擎的搜索结果里找正在播放的那首。
	/// 行容器在分页里：按显示顺序算出所在页，不在当前页就先翻过去再渲染，
	/// 最后把行容器返回。找不到（换了搜索词/清了结果）先查「定位记忆」——
	/// 这首上次是在哪个源/哪次搜索/第几页放响的，按记忆把列表恢复过去；
	/// 关键词变了就重搜（异步恢复，见 LocateRestorePending），仍找不到才
	/// 返回 null 由调用方提示，不强制重搜。</summary>
	public FrameworkElement? RevealPlayingRow()
	{
		string? key = MusicService.BuildCurrentTrack()?.Key;
		if (string.IsNullOrEmpty(key) || key.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
		{
			// 本地文件不在搜索结果里（这里只有在线条目），交给来源页去定位。
			LogHelper.Log($"[定位] 在线页跳过：key={key ?? "null"}（本地文件不归这里管）");
			return null;
		}
		// 先在当前源找；没有再扫其余源的缓存（当前曲目可能来自另一个源——
		// 比如正在播 QQ 的歌而用户最后点的是网易云标签）。找到就切过去，
		// 这正是「定位不到」的老失败点之一：只认 _selectedEngine。
		if (_engineCache.TryGetValue(_selectedEngine, out List<object>? cache) && cache.Count > 0)
		{
			FrameworkElement? row = RevealInEngine(_selectedEngine, cache, key);
			if (row != null)
			{
				return row;
			}
		}
		else
		{
			LogHelper.Log($"[定位] 当前源 {_selectedEngine} 缓存为空（没搜过/已被重搜清掉）");
		}
		foreach (string engine in EngineOrder)
		{
			if (string.Equals(engine, _selectedEngine, StringComparison.Ordinal)
				|| !_engineCache.TryGetValue(engine, out List<object>? other) || other.Count == 0)
			{
				continue;
			}
			// 先真正切到那个源再渲染定位页，保证源按钮/页脚/渲染状态一致。
			SwitchEngine(engine);
			FrameworkElement? row = RevealInEngine(engine, other, key);
			if (row != null)
			{
				LogHelper.Log($"[定位] 当前源没有，在 {engine} 的缓存里找到并切源");
				return row;
			}
		}
		// 所有源的缓存里都没有 → 查记忆恢复（可能切源/重搜，异步时返回 null）。
		if (LocateMemos.TryGetValue(key, out LocateMemo? memo))
		{
			LogHelper.Log($"[定位] 缓存未命中，按记忆恢复：{memo.Engine} / 「{memo.Keyword}」 / 第 {memo.Page} 页");
			return RevealFromMemo(key, memo);
		}
		LogHelper.Log($"[定位] 彻底未命中：所有源缓存与定位记忆里都没有 {key}");
		return null;
	}

	/// <summary>在指定源的缓存（冻结显示顺序）里定位 key，翻页渲染并返回行。
	/// RevealPlayingRow 的共用内层，步骤日志全挂这里，方便从日志一眼看出
	/// 失败在哪一环（键不匹配/页码越界/行 Tag 不匹配）。</summary>
	private FrameworkElement? RevealInEngine(string engine, List<object> cache, string key)
	{
		List<object> ordered = DisplayOrderFor(engine);
		int idx = ordered.FindIndex(it =>
			string.Equals(ToSavedTrack(it)?.Key, key, StringComparison.OrdinalIgnoreCase));
		if (idx < 0)
		{
			return null;
		}
		int page = idx / PageSize + 1;
		int totalPages = Math.Max(1, (ordered.Count + PageSize - 1) / PageSize);
		LogHelper.Log($"[定位] {engine} 命中：索引 {idx}/{ordered.Count} → 第 {page}/{totalPages} 页");
		int visible = _visiblePage.TryGetValue(engine, out int v) ? v : 1;
		if (page != visible)
		{
			// 翻到正在播放那首所在的页：与页脚翻页走同一套 RenderEngine（不播动画），
			// 渲染完回顶部，跟手动翻页的表现一致。
			// RenderEngine 内部已 Cancel，这里再补一次保险：直接赋值前
			// 停掉残存补间，避免它把刚设好的 0 又拽走。
			_visiblePage[engine] = page;
			RenderEngine(engine, animate: false);
			SmoothScroll.Cancel(OnlineScroll);
			OnlineScroll.ScrollToVerticalOffset(0.0);
		}
		// 定位成功也刷新记忆：下次按最新位置恢复。
		if (string.Equals(engine, _selectedEngine, StringComparison.Ordinal))
		{
			LocateMemos[key] = new LocateMemo(engine, _lastKeyword, page);
		}
		FrameworkElement? row = FindRowByKey(key);
		if (row == null)
		{
			// 到了这一步说明页码没错但行没挂出来：页码越界或渲染后 Tag 对不上。
			LogHelper.Log($"[定位] 第 {page} 页已渲染但没找到行（Tag 与 {key} 不匹配）");
		}
		return row;
	}

	/// <summary>点播/定位成功时记一笔「这首在哪个源/哪次搜索/第几页」。
	/// 只在搜索模式记（歌单模式无翻页语义）；本地文件不记。</summary>
	private void RememberLocate(object item)
	{
		try
		{
			if (_playlistMode)
			{
				return;
			}
			string? key = ToSavedTrack(item)?.Key;
			if (string.IsNullOrEmpty(key) || key.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			if (!_engineCache.TryGetValue(_selectedEngine, out List<object>? cache) || cache.Count == 0)
			{
				return;
			}
			// 页码必须用冻结显示顺序（DisplayOrderFor）算，与 RenderEngine/
			// 定位球同源 —— 这里如果拿 OrderForDisplay 现场重排，预取追加进队尾
			// 的新条目可能被排到前面去，记下的页码和真实显示页就对不上。
			List<object> ordered = DisplayOrderFor(_selectedEngine);
			int idx = ordered.FindIndex(it => ReferenceEquals(it, item)
				|| string.Equals(ToSavedTrack(it)?.Key, key, StringComparison.OrdinalIgnoreCase));
			if (idx >= 0)
			{
				LocateMemos[key] = new LocateMemo(_selectedEngine, _lastKeyword, idx / PageSize + 1);
			}
		}
		catch
		{
			// 记忆失败不影响点播本身。
		}
	}

	/// <summary>按定位记忆恢复现场：切回记忆的源（与手动点源按钮同一套
	/// 视觉/落盘），关键词一致就直接翻到记忆页码返回行；关键词变了说明
	/// 列表数据已不对应，重搜记忆关键词，搜完在 DoOnlineSearchAsync 收尾
	/// 处翻页居中（异步路径，LocateRestorePending=true 期间 MainWindow 不报「定位不到」）。</summary>
	private FrameworkElement? RevealFromMemo(string key, LocateMemo memo)
	{
		if (!string.Equals(_selectedEngine, memo.Engine, StringComparison.Ordinal))
		{
			LogHelper.Log($"[定位] 记忆恢复：切源 {_selectedEngine} → {memo.Engine}");
			SwitchEngine(memo.Engine);
		}
		if (!string.Equals(_lastKeyword, memo.Keyword, StringComparison.Ordinal))
		{
			// 搜索页重搜过，列表键已经换了：按记忆的关键词重搜，搜完由
			// DoOnlineSearchAsync 收尾调 CompletePendingLocateRestore 翻页居中。
			LogHelper.Log($"[定位] 记忆恢复：关键词不一致（当前「{_lastKeyword}」≠记忆「{memo.Keyword}」），重搜后恢复");
			_pendingLocateKey = key;
			RunSearch(memo.Keyword);
			return null;
		}
		if (!_engineCache.TryGetValue(memo.Engine, out List<object>? cache) || cache.Count == 0)
		{
			LogHelper.Log($"[定位] 记忆恢复：{memo.Engine} 缓存为空，无法恢复");
			return null;
		}
		List<object> ordered = DisplayOrderFor(memo.Engine);
		int idx = ordered.FindIndex(it =>
			string.Equals(ToSavedTrack(it)?.Key, key, StringComparison.OrdinalIgnoreCase));
		if (idx < 0)
		{
			// 记忆也过时了（缓存被重搜覆盖等）
			LogHelper.Log($"[定位] 记忆恢复：{memo.Engine} 缓存里已没有 {key}，记忆过时");
			return null;
		}
		_visiblePage[memo.Engine] = idx / PageSize + 1;
		RenderEngine(memo.Engine, animate: false);
		SmoothScroll.Cancel(OnlineScroll);
		OnlineScroll.ScrollToVerticalOffset(0.0);
		return FindRowByKey(key);
	}

	/// <summary>重搜恢复的收尾：翻到记忆页码、把行交给 MainWindow 居中+高亮。
	/// 找不到行（记忆彻底过时）也由 FinishLocateReveal 统一给提示。</summary>
	private void CompletePendingLocateRestore()
	{
		string? key = _pendingLocateKey;
		if (string.IsNullOrEmpty(key))
		{
			return;
		}
		_pendingLocateKey = null;
		LogHelper.Log($"[定位] 重搜完成，收尾恢复：{_selectedEngine} / {key}");
		FrameworkElement? row = null;
		if (_engineCache.TryGetValue(_selectedEngine, out List<object>? cache) && cache.Count > 0)
		{
			List<object> ordered = DisplayOrderFor(_selectedEngine);
			int idx = ordered.FindIndex(it =>
				string.Equals(ToSavedTrack(it)?.Key, key, StringComparison.OrdinalIgnoreCase));
			if (idx >= 0)
			{
				_visiblePage[_selectedEngine] = idx / PageSize + 1;
				RenderEngine(_selectedEngine, animate: false);
				SmoothScroll.Cancel(OnlineScroll);
				OnlineScroll.ScrollToVerticalOffset(0.0);
				row = FindRowByKey(key);
			}
			else
			{
				LogHelper.Log($"[定位] 重搜结果里没有 {key}，定位终止");
			}
		}
		MainWindow.Instance?.FinishLocateReveal(row);
	}

	/// <summary>按条目 Key 在当前已渲染的行里找行容器（定位球/恢复收尾共用）。</summary>
	private FrameworkElement? FindRowByKey(string key)
	{
		foreach (object o in OnlineList.Items)
		{
			if (o is FrameworkElement fe && fe.Tag != null
				&& string.Equals(ToSavedTrack(fe.Tag)?.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				return fe;
			}
		}
		return null;
	}

	/// <summary>只渲染当前这一页。
	/// 关键改动：以前 BuildEngineRows 是遍历**整个**缓存给每一条都建一整行控件
	/// （一行 21 个元素、4 个按钮各带一份 Style+ControlTemplate），
	/// 翻到第 4 页就是白建 120 行、约 2500 个元素，而屏幕上只放得下 30 行 ——
	/// 这就是「第二页一下子铺到 120 条会卡一下」的原因（页脚「已加载 120 首」
	/// 显示的也是缓存总数，不是真的显示了 120 条）。
	/// 现在按「当前页需要的条目」逐个取，取不到才建，并且建好的行按条目 key 缓存，
	/// 翻页只换排列顺序、不再重建已建过的行。</summary>
	private void RenderEngine(string engine, bool animate = true)
	{
		// 先记住当前滚动位置，重建完再放回去。
		// 预取是自动的，下一页一到就可能触发一次 RenderEngine，而
		// Items.Clear() 会让 ScrollViewer 的 offset 归零。用户正往下滚的时候
		// 被反复拽回顶部，体验上就是「滚轮时灵时不灵」。
		// 重建是直赋偏移的路子，先把可能还在跑的翻页/滚轮补间停掉，
		// 不然它会按旧目标继续逐帧写偏移，跟这里的恢复互相拽。
		SmoothScroll.Cancel(OnlineScroll);
		double keepOffset = OnlineScroll.VerticalOffset;
		OnlineList.Items.Clear();
		// 开新一轮渲染：上一轮用过的行现在都可以复用了。
		_renderPassUsed.Clear();

		// B站源用卡片网格（WrapPanel，像B站首页那样一排排封面卡），
		// 其它源保持行列表（StackPanel）。面板模板都定义在
		// OnlineList.Resources 里，这里按源挑一个装上。
		bool cardMode = string.Equals(engine, "bili", StringComparison.Ordinal);
		OnlineList.ItemsPanel = (ItemsPanelTemplate)OnlineList.FindResource(
			cardMode ? "CardPanelTemplate" : "RowPanelTemplate");

		List<object> cache = DisplayOrderFor(engine);
		// 冻结的显示顺序是 RenderEngine / 定位球 / 页码计算共用的同一份，
		// 页码永远按它算，不会出现「渲染的和定位算的是两种排法」。

		int start, end;
		if (cardMode)
		{
			// B站源没有分页（SupportsPaging=false），一次搜索已经把多页结果
			// 全收进缓存，这里一屏全铺出来；页脚本来也是藏着的。
			start = 0;
			end = cache.Count;
		}
		else
		{
			int visible = _visiblePage.TryGetValue(engine, out int vp) ? vp : 1;
			if (visible < 1)
			{
				visible = 1;
			}
			start = (visible - 1) * PageSize;
			end = Math.Min(cache.Count, start + PageSize);
		}
		for (int i = Math.Max(0, start); i < end; i++)
		{
			OnlineList.Items.Add(cardMode ? GetOrBuildBiliCard(cache[i]) : GetOrBuildRow(engine, cache[i]));
		}
		if (cache.Count == 0)
		{
			OnlineList.Items.Add(new TextBlock
			{
				// 没搜过的时候留空——用户说别堆提示，教人怎么用不如让他自己试。
				// 搜了但没结果才给一句「无结果」，那是必要反馈。
				Text = _lastKeyword.Length == 0 ? "" : Loc.Default["online_none"],
				FontSize = 14,
				Foreground = ThemeManager.Tertiary(),
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 24, 0, 0)
			});
		}
		// 可见页快照已随「预取落地重画」一起移除：预取不再触发重画，
		// 也就不需要「可见页是否变了」的比对（见 RunPrefetchAsync 注释）。
		BuildFooter(engine);
		UpdateFooter();
		// 歌单模式没有「下一页」，页脚整个收起来，免得用户对着一个不存在的翻页键发呆。
		FooterHost.Visibility = _playlistMode ? Visibility.Collapsed : Visibility.Visible;
		// 内容重新加完再把滚动位置放回去。OnlineScroll 现在是像素滚动
		// （CanContentScroll=False），ScrollToVerticalOffset 才和实际偏移一致。
		if (keepOffset > 0)
		{
			OnlineScroll.ScrollToVerticalOffset(keepOffset);
		}
		// 只有用户主动翻页/切源才做淡入。预取落地时内容可能根本没变，
		// 那时候重播一次全列表的 320ms 透明度动画只会「闪一下」并拖慢滚轮。
		if (animate)
		{
			FadeInResults();
		}
		// 站在第 P 页就把第 P+1 页预取掉，让点「下一页」时几乎不用等。
		EnsurePrefetchAhead();
		// 行缓存裁剪 + 下一页封面预取：内存有上限、翻页封面即现。
		TrimRowCache(engine, RowCacheCap);
		PreloadNextPageCovers(engine);
		// 主动探测 VIP（设置开关，默认关）：当前页排前面（用户正盯着的行
		// 最先拿到标签结论），整份缓存跟在后面慢慢补。
		if (cache.Count > 0)
		{
			var probeOrder = new List<object>(cache.Count);
			int ps = Math.Max(0, start);
			int pe = Math.Min(cache.Count, end);
			for (int i = ps; i < pe; i++)
			{
				probeOrder.Add(cache[i]);
			}
			for (int i = 0; i < cache.Count; i++)
			{
				if (i < ps || i >= pe)
				{
					probeOrder.Add(cache[i]);
				}
			}
			VipProbe.Schedule(probeOrder);
		}
		// 评论数排序的统一补齐入口。
		//
		// 之前只在「勾选排序」和「切源」两个地方调，于是新搜一次、切一页都不会补：
		// 那些新条目评论数还是 0，排序一执行就全沉到本页末尾，看着像排序坏了。
		// 挂在这里是因为所有改内容的路径（搜完、切源、翻页、补齐完成）
		// 最后都会走到 RenderEngine，一处覆盖全部，不会再漏。
		//
		// FillCommentCountsAsync 自身有 _fillingCommentCounts 挡重入，
		// 而且它只在 _sortByComment 为真时才真的发请求，
		// 所以这里无条件调用不会给正常排序带来额外开销。
		// 歌单模式不发：评论数只在排序时才用得上，歌单不重排，白打 30 个请求。
		if (!_playlistMode)
		{
			_ = FillCommentCountsAsync(engine);
		}
	}

	/// <summary>翻页动画：整块结果按翻页方向滑入，同时把滚动位置平滑带回顶部。
	///
	/// 方向：+1 = 往下翻（新一页从下面顶上来），-1 = 往上翻（从上面压下来），
	/// 0 = 不分方向（切源、重新搜索这类没有「前后页」关系的换列表）。
	///
	/// 原来这里只调 <see cref="FadeInResults"/>（整列表淡入 + 10px 上移）：
	/// 翻页时新内容是整块替换的，10px 的位移配 320ms 的淡入看着像「闪一下」，
	/// 而且滚动的落点是瞬间归零的（ScrollToVerticalOffset(0)），
	/// 正好和列表的淡入同时发生，视觉上是「内容糊了」而不是「翻了一页」。
	/// 这里换成方向明确的整格滑动 + 平滑回到开头，翻页才有翻页的样子。
	/// </summary>
	private void AnimatePageTurn(int direction)
	{
		try
		{
			// 复用同一个 TranslateTransform：每次 new 一个塞进去的话旧动画没被取消，
			// 连续点「下一页」会有两个 300ms 的动画叠着抢 Y，画面一直在抖。
			if (OnlineList.RenderTransform is not TranslateTransform trans)
			{
				trans = new TranslateTransform();
				OnlineList.RenderTransform = trans;
			}
			trans.BeginAnimation(TranslateTransform.YProperty, null);
			OnlineList.BeginAnimation(UIElement.OpacityProperty, null);

			double from = direction >= 0 ? PageTurnOffset : -PageTurnOffset;
			var dur = TimeSpan.FromMilliseconds(PageTurnMs);
			var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

			// FillBehavior.Stop 结束后属性会回落到基值，所以基值必须是终点（0 / 1），
			// 起止值由动画自己给。反过来写（基值设成起点）会让列表停在半路。
			trans.Y = 0.0;
			trans.BeginAnimation(TranslateTransform.YProperty,
				new DoubleAnimation(from, 0.0, dur)
				{
					EasingFunction = ease,
					FillBehavior = FillBehavior.Stop
				});
			OnlineList.Opacity = 1.0;
			OnlineList.BeginAnimation(UIElement.OpacityProperty,
				new DoubleAnimation(PageTurnFromOpacity, 1.0, dur)
				{
					EasingFunction = ease,
					FillBehavior = FillBehavior.Stop
				});
		}
		catch
		{
			OnlineList.Opacity = 1.0;
		}
	}

	/// <summary>翻页后把列表平滑滚回开头。
	///
	/// 列表只装当前这一页，所以翻完必须回顶部，但直接 ScrollToVerticalOffset(0)
	/// 是瞬移：淡入动画正在往上走、视口同时往下砸，两股运动叠在一起。
	/// 这里复用滚轮那套平滑滚动（<see cref="SmoothScroll"/>）把当前偏移追到 0。
	/// 注意不能用 BeginAnimation(VerticalOffsetProperty)：那个属性带
	/// IsAnimationProhibited，动画会抛异常（竖排歌词已经踩过一次）。
	///
	/// 用的是 AnimateTo（固定时长）而不是 Animate（滚轮那种指数逼近）：
	/// 后者对 900px 的距离只用一两帧，看着像瞬移，回弹就白做了。
	/// 时长跟着滑入一起，两段运动同时收尾。
	/// </summary>
	private void AnimateScrollToTop()
	{
		try
		{
			if (OnlineScroll.VerticalOffset <= 0.5)
			{
				OnlineScroll.ScrollToVerticalOffset(0.0);
				return;
			}
			SmoothScroll.AnimateTo(OnlineScroll, 0.0, PageTurnMs);
		}
		catch
		{
		}
	}

	/// <summary>取某条结果对应的行控件，没有就建一个并按条目 key 缓存。</summary>
	private FrameworkElement GetOrBuildRow(string engine, object item)
	{
		if (!_rowCache.TryGetValue(engine, out Dictionary<string, FrameworkElement>? map))
		{
			map = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
			_rowCache[engine] = map;
		}
		string key = Normalize(Title(item)) + "|" + Normalize(FirstArtist(ArtistOf(item)));
		// 本轮还没用过缓存行就直接复用：封面、VIP 标签、收藏心形都在控件上，
		// 复用 = 翻回来时已看过的封面原样显示，不重建、不重新淡入。
		// （Parent 挡不挡不再重要：Items.Clear() 之后重新 Add 同一个元素是安全的，
		// 真正冲突的只有「本轮已经把它摆进列表还要再摆一次」，那由 _renderPassUsed 把关。）
		if (map.TryGetValue(key, out FrameworkElement? hit) && !_renderPassUsed.Contains(hit))
		{
			_renderPassUsed.Add(hit);
			return hit;
		}
		FrameworkElement row = BuildEngineRow(engine, item);
		map[key] = row;
		_renderPassUsed.Add(row);
		return row;
	}

	/// <summary>把离页的行控件从缓存里挤出去。
	/// 行缓存原本只进不出：翻几页歌单/翻深几次搜索，几百个行控件（每行 21 个元素）
	/// 全都钉在 Dictionary 里，内存白白涨。这里在每轮渲染收尾时按「最近用过的
	/// 行优先保留」裁到上限——离页的行被挤掉后，翻回去要重建一次，
	/// 但封面三层缓存都在，重建只是重新挂控件，不走网络。</summary>
	private void TrimRowCache(string engine, int cap)
	{
		if (!_rowCache.TryGetValue(engine, out Dictionary<string, FrameworkElement>? map)
			|| map.Count <= cap)
		{
			return;
		}
		foreach (var kv in map)
		{
			if (map.Count <= cap)
			{
				break;
			}
			// 本轮渲染正在显示的行绝对不挤：挤了下一帧布局直接崩。
			if (!_renderPassUsed.Contains(kv.Value))
			{
				map.Remove(kv.Key);
			}
		}
	}

	/// <summary>给单条结果建一行。排序已经搬到 RenderEngine，这里只管建控件。</summary>
	private FrameworkElement BuildEngineRow(string engine, object item)
	{
		return BuildEngineRowCore(item, _lastKeyword);
	}

	/// <summary>行构建的核心实现：引擎标签、VIP 实测标签、收藏心形、评论/下载
	/// 挂钩全在这一份里。高亮词从参数走 —— 搜索页传 _lastKeyword，
	/// 音乐库收藏夹（BuildLibraryRow）传它自己的筛选词，两边共用同一条链路，
	/// 播放/下载/VIP 行为天然一致。</summary>
	internal FrameworkElement BuildEngineRowCore(object item, string? highlight)
	{
		SavedTrack? track = ToSavedTrack(item);
		if (track == null)
		{
			return new TextBlock { Height = RowHeight };
		}
		object copy = item;
		// 评论面板已经是全局的第三界面（Views/CommentsPanel，挂在 MainWindow），
		// 行内评论键直接开面板，搜索页不再持有评论的任何状态。
		Action? comment = IsCommentable(item) ? () => MainWindow.Instance?.ToggleComments(copy) : null;
		Action? download = IsDownloadable(item) ? () => _ = DownloadToLibraryAsync(copy) : null;
		(string label, Brush brush)? tag = EngineTag(item);
		// VIP 标签只认实测结论（TrackAccess.IsRestricted）：点播放真被拒过才标。
		// 平台的收费字段（网易云 fee、酷狗 pay_type、QQ pay_play）错得太多，
		// 照着猜会出现「标着 VIP 能正常听」「没标点了才说要 VIP」两种误导，
		// 所以搜索结果默认不打标签，实测拒绝后 TrackAccess.Changed 会重建行补上。
		bool vip = IsRestricted(item);
		var row = TrackRows.BuildRow(new TrackRows.TrackRowSpec
		{
			Track = track,
			SubOverride = SubLine(item),
			OnPlay = () => Play(copy),
			IsFavorite = UserLibrary.IsFavorite(track),
			OnToggleFavorite = () => UserLibrary.ToggleFavorite(track),
			OnComment = comment,
			OnDownload = download,
			EngineLabel = tag?.label,
			EngineBrush = tag?.brush,
			Highlight = highlight,
			SecondChipLabel = vip ? Loc.Default["online_vip_tag"] : null
		});
		row.Tag = copy;
		return row;
	}

	/// <summary>音乐库「收藏夹」复用的行构建入口：和搜索结果完全同一个构建函数、
	/// 同一套播放（Play）/下载（DownloadToLibraryAsync）实现，包括 VIP 实测标签
	/// 与受限后的 B 站替代源面板。音乐库只提供自己的高亮词。</summary>
	public FrameworkElement BuildLibraryRow(object item, string? highlight = null)
	{
		return BuildEngineRowCore(item, highlight);
	}

	/// <summary>音乐库收藏夹的播放入口：与搜索结果完全同一条 Play 链路。
	/// 受限时的表现已经统一 —— 任何入口都只弹一句全局 Toast，自动弹面板 /
	/// 自动搜 B 站的分支已整体删除，原先用来区分两种表现的「外部触发」标记
	/// 也就没有存在的必要了。</summary>
	public void PlayExternal(object item)
	{
		Play(item);
	}

	/// <summary>音乐库收藏夹的下载入口，同上转发到 DownloadToLibraryAsync。</summary>
	public Task DownloadExternalAsync(object item) => DownloadToLibraryAsync(item);

	// —— B站源卡片视图 ——

	/// <summary>已建好的 B 站卡片，键 = bvid。卡片只在首次遇到时构建一次，
	/// 重新渲染（切源回来 / 状态变化重画）直接复用，封面不会闪一下重新淡入。</summary>
	private readonly Dictionary<string, FrameworkElement> _biliCardCache = new(StringComparer.Ordinal);

	/// <summary>取一张 B 站卡片（带缓存）。bili 缓存里理论上只会是 BiliTrack，
	/// 万一混进别的类型就兜底走行渲染，别让整页跟着崩。</summary>
	private FrameworkElement GetOrBuildBiliCard(object item)
	{
		if (item is BiliTrack b && !string.IsNullOrEmpty(b.Bvid))
		{
			if (_biliCardCache.TryGetValue(b.Bvid, out FrameworkElement? hit))
			{
				return hit;
			}
			FrameworkElement card = BuildBiliCard(b);
			_biliCardCache[b.Bvid] = card;
			return card;
		}
		return GetOrBuildRow("bili", item);
	}

	/// <summary>建一张 B站风格的视频卡：上面封面（左下播放量、右下时长压在
	/// 半透明黑条上，跟B站一个读法），下面标题（最多两行）+ UP主一行，
	/// 右下角收藏/下载两个小按钮。点卡片任意位置直接播放。
	/// 文案全部来自数据本身和现有多语言键，这里不引入新键。</summary>
	private FrameworkElement BuildBiliCard(BiliTrack b)
	{
		var card = new Border
		{
			Width = 186,
			Margin = new Thickness(6),
			CornerRadius = new CornerRadius(10),
			Background = (Brush)FindResource("CardBgBrush"),
			BorderBrush = (Brush)FindResource("CardBorderBrush"),
			BorderThickness = new Thickness(1),
			Cursor = System.Windows.Input.Cursors.Hand,
			// FindRowByKey 按 Tag 找行容器（定位球/恢复收尾共用），卡片同样要能被找到。
			Tag = b
		};
		// Border.CornerRadius 不裁子元素，封面方角会顶出圆角外。
		// 在尺寸确定后给整卡补一个圆角矩形 Clip，一次就够。
		card.SizeChanged += (_, _) =>
		{
			card.Clip = new RectangleGeometry(
				new Rect(0, 0, card.ActualWidth, card.ActualHeight), 10, 10);
		};

		var root = new Grid();
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		card.Child = root;

		// —— 封面 ——
		var coverHost = new Grid
		{
			Height = 104,
			Background = (Brush)FindResource("CardBgSecondaryBrush")
		};
		var cover = new Image { Stretch = Stretch.UniformToFill };
		coverHost.Children.Add(cover);
		CoverLoader.LoadAsync(cover, b.Pic, 256);
		var overlay = new Border
		{
			VerticalAlignment = VerticalAlignment.Bottom,
			Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
			Padding = new Thickness(7, 2, 7, 3)
		};
		overlay.Child = new TextBlock
		{
			FontSize = 11,
			Foreground = Brushes.White,
			Text = "▶ " + BiliClient.FormatPlay(b.Play)
				+ (string.IsNullOrEmpty(b.DurationRaw) ? "" : "   " + b.DurationRaw)
		};
		coverHost.Children.Add(overlay);
		Grid.SetRow(coverHost, 0);
		root.Children.Add(coverHost);

		// —— 标题：最多两行，超出省略 ——
		var title = new TextBlock
		{
			FontSize = 12.5,
			TextWrapping = TextWrapping.Wrap,
			MaxHeight = 38,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(9, 7, 9, 0),
			Text = b.Display,
			ToolTip = b.Display
		};
		title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
		Grid.SetRow(title, 1);
		root.Children.Add(title);

		// —— UP主 + 收藏/下载 ——
		SavedTrack? st = ToSavedTrack(b);
		bool fav = st != null && UserLibrary.IsFavorite(st);
		var bottom = new DockPanel { LastChildFill = true, Margin = new Thickness(9, 4, 6, 8) };

		var dlBtn = TrackRows.IconButton("\uE896", 28, Loc.Default["act_download"]);
		DockPanel.SetDock(dlBtn, Dock.Right);
		dlBtn.Click += (_, e) =>
		{
			e.Handled = true;
			_ = DownloadToLibraryAsync(b);
		};
		bottom.Children.Add(dlBtn);

		var heart = TrackRows.IconButton("\uEB51", 28, Loc.Default["act_fav"]);
		DockPanel.SetDock(heart, Dock.Right);
		void SetHeart(bool on)
		{
			if (heart.Content is TextBlock g)
			{
				g.Text = on ? "\uEB52" : "\uEB51";
			}
			heart.Foreground = on
				? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
				: ThemeManager.Secondary();
		}
		SetHeart(fav);
		heart.Click += (_, e) =>
		{
			e.Handled = true;
			if (st == null)
			{
				return;
			}
			UserLibrary.ToggleFavorite(st);
			bool on = UserLibrary.IsFavorite(st);
			SetHeart(on);
			TrackRows.SyncHearts(st.Key);
		};
		bottom.Children.Add(heart);

		var author = new TextBlock
		{
			FontSize = 11.5,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			Text = string.IsNullOrEmpty(b.Author) ? b.TypeName : b.Author,
			ToolTip = b.Author
		};
		author.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
		bottom.Children.Add(author);
		Grid.SetRow(bottom, 2);
		root.Children.Add(bottom);

		// 点卡片直接播放（B站习惯：点封面就是打开）。按钮自己 e.Handled，
		// 不会误触播放。
		card.MouseLeftButtonDown += (_, e) =>
		{
			e.Handled = true;
			Play(b);
		};
		return card;
	}

	/// <summary>该平台的搜索结果里带不带播放量。只有 B 站（play 字段）带。</summary>
	private static bool SupportsPopularity(string engine) =>
		string.Equals(engine, "bili", StringComparison.OrdinalIgnoreCase);

	/// <summary>取播放量；平台没给就返回 0（调用前先用 SupportsPopularity 判断）。</summary>
	private static long PopularityOf(object item) => item switch
	{
		BiliTrack b => b.Play,
		_ => 0L
	};

	private void BuildFooter(string engine)
	{
		// 三个控件贴在一起、整体居中：外层 Border 是 XAML 里的 FooterHost（已设
		// HorizontalAlignment=Center），这里只往里塞一个水平 StackPanel。
		// 不再用 Grid 三列 + Star 中间列，那样会把上一页推到最左、下一页推到最右，
		// 也就是用户说的「不靠边都居中贴合一起」。
		var panel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};

		// 三个控件之间留一点间距（用户要求「上下页和中间隔开一点」），
		// 但仍然整体居中；间距加在按钮的 Margin 上，不动 TextBlock 的 MinWidth。
		// 「第一页」放在「上一页」左边（用户要求）。翻回首页不用连点上一页：
		// 三个键同一个 GhostButtonStyle，看起来是一组，不抢视觉。
		_footerFirst = new Button
		{
			Content = Loc.Default["online_first"],
			Padding = new Thickness(14, 6, 14, 6),
			Margin = new Thickness(0, 0, 4, 0),
			BorderThickness = new Thickness(0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Style = (Style)FindResource("GhostButtonStyle")
		};
		_footerFirst.Click += (_, _) => GoFirstPage();
		panel.Children.Add(_footerFirst);

		_footerPrev = new Button
		{
			Content = Loc.Default["online_prev"],
			Padding = new Thickness(14, 6, 14, 6),
			Margin = new Thickness(0, 0, 4, 0),
			BorderThickness = new Thickness(0),
			Cursor = System.Windows.Input.Cursors.Hand,
			Style = (Style)FindResource("GhostButtonStyle")
		};
		_footerPrev.Click += (_, _) => GoPrevPage();
		panel.Children.Add(_footerPrev);

		_footerPageText = new TextBlock
		{
			FontSize = 12.5,
			Foreground = ThemeManager.Tertiary(),
			MinWidth = 128,
			TextAlignment = TextAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(10, 0, 10, 0)
		};
		panel.Children.Add(_footerPageText);

		_footerNext = new Button
		{
			Content = Loc.Default["online_next"],
			Padding = new Thickness(14, 6, 14, 6),
			Margin = new Thickness(4, 0, 0, 0),
			BorderThickness = new Thickness(0),
			Cursor = System.Windows.Input.Cursors.Hand,
			// 之前用的是 AccentButtonStyle（实心强调色），所以「下一页」比「上一页」
			// 显眼一大截，看着不平衡。用户要求两者一样显示，统一都用 GhostButtonStyle。
			Style = (Style)FindResource("GhostButtonStyle")
		};
		_footerNext.Click += (_, _) => _ = LoadMoreForSelectedAsync();
		panel.Children.Add(_footerNext);

		FooterHost.Child = panel;
		FooterHost.Visibility = Visibility.Visible;
		_footerItem = panel;
		FooterEngine = engine;
	}

	private string FooterEngine = "netease";

	private void UpdateFooter()
	{
		if (_footerItem == null)
		{
			return;
		}
		// 页码显示「用户正在看的这一页」，不是「已加载了几页」：
		// 点源后台预取下一页时，页码不该自己跳到下一页去。
		int visible = _visiblePage.TryGetValue(FooterEngine, out int v) ? v : 1;
		// _nextPage 是「下一个要拉的页号」，也就是「已经加载了几页」
		bool done = _exhausted.TryGetValue(FooterEngine, out bool d) && d;
		int cached = _engineCache.TryGetValue(FooterEngine, out List<object>? cc) ? cc.Count : 0;
		// 页脚只报当前页码。原来这里还挂了个「已加载 {1} 首」，
		// 但那个数字一直是硬编码传进来的 0，所以永远显示「已加载 0 首」，
		// 是个只会误导人的死数字。要么给真值要么删掉，这里删掉。
		_footerPageText.Text = Loc.Default.Format("online_page", visible);
		// 已经在第一页时两个左翻键都置灰，不然点了没反应。
		_footerFirst.IsEnabled = visible > 1;
		_footerPrev.IsEnabled = visible > 1;
		// 「下一页」可用的条件是「还有没看过的页」：缓存里已经存着下一页（之前翻过又退回来），
		// 或者还能继续往后拉。以前只判断 !done，于是拉到底一次之后按钮就永久禁用，
		// 退回上一页再点「下一页」完全没反应。
		bool hasCachedNext = cached > visible * PageSize;
		// 已经在加载时必须把按钮也禁掉。hasCachedNext 这一支原来没看 _loadingMore，
		// 于是请求在飞的时候按钮还是可点的，连点两下会同时发两个翻页请求。
		bool canFetchMore = SupportsPaging(FooterEngine) && !_loadingMore && !done;
		_footerNext.IsEnabled = !_loadingMore && (hasCachedNext || canFetchMore);
		if (!SupportsPaging(FooterEngine))
		{
			_footerNext.Content = Loc.Default["online_more_none"];
		}
		else if (done && !hasCachedNext)
		{
			_footerNext.Content = Loc.Default["online_more_end"];
		}
	}

	/// <summary>「下一页」按钮。
	/// 正常情况下点源时已经在预取下一页了，这里 await 那个任务；
	/// 万一没在飞（比如刚搜完就点），就直接把当前要的那页拉下来，而不是只重绘一遍。</summary>
	private async Task LoadMoreForSelectedAsync()
	{
		if (_searching || _lastKeyword.Length == 0)
		{
			return;
		}
		string engine = _selectedEngine;
		_loadingMore = true;
		UpdateFooter();
		try
		{
			if (_prefetching.ContainsKey(engine))
			{
				// 已经在飞了：等它落地
				await AwaitPrefetchAsync(engine);
			}
			else if (SupportsPaging(engine)
				&& !(_exhausted.TryGetValue(engine, out bool finished) && finished))
			{
				// 没有在飞：直接拉，不预取的那一次
				await FetchNextPageAsync(engine, _nextPage[engine]);
			}
			// 真的有新一页的数据才前进。
			//
			// 原来这里无条件 +1，于是「点得到下一页」和「下一页有内容」是两回事：
			// 已经拉到底之后按钮还可点，翻过去列表一片空白；退回来再看，
			// 页脚又变成「已加载全部」—— 同一次搜索里前后说法不一致。
			// 缓存里没有第 cur+1 页的数据就说明到底了，原地停住并把按钮收掉。
			int cached = _engineCache.TryGetValue(engine, out List<object>? cc) ? cc.Count : 0;
			int cur = _visiblePage.TryGetValue(engine, out int v) ? v : 1;
			if (cached > cur * PageSize)
			{
				_visiblePage[engine] = cur + 1;
				// animate: false —— 翻页动画由 AnimatePageTurn 单独做，
				// 这里再让 RenderEngine 淡入一次就是两套动画叠着抢 Opacity/Y。
				RenderEngine(engine, animate: false);
				AnimatePageTurn(1);
				// 列表现在只装当前页，所以要回顶部；但不瞬移，
				// 平滑追到 0，和列表的滑入一起构成「翻了一页」。
				AnimateScrollToTop();
			}
			else
			{
				_exhausted[engine] = true;
				UpdateFooter();
			}
		}
		catch (Exception ex)
		{
			SetStatus(Loc.Default["cm_fail"]);
			LogHelper.LogError(ex, "加载下一页失败");
		}
		finally
		{
			_loadingMore = false;
			// 原来这里又 RenderEngine 了一次，等于点一次「下一页」把整个列表
			// 清空重建两遍、白滚动两遍、淡入动画播两次 —— 就是「下一页很卡」的直接原因。
			// try 里已经画过一次，finally 只需要把按钮状态改回来。
			UpdateFooter();
			// try 里那次渲染时 _loadingMore 还是 true，预取被挡住了，
			// 所以要等这里放行之后才启动「第 3 页」的预取，
			// 正好就是「翻到第 2 页就开始预取第 3 页」这条节奏。
			EnsurePrefetchAhead();
		}
	}

	// 评论面板（Views/CommentsPanel）通过 using static 复用这三个取标题/歌手
	// /平台源的 helper，所以是 internal 而不是 private。
	internal static string Title(object item) => item switch
	{
		NeteaseTrack t => t.Name,
		KugouTrack k => k.Display,
		QqTrack q => q.Name,
		BiliTrack b => b.Display,
		// 汽水搜索结果就是 SavedTrack（Type=soda），标题/歌手直接取字段。
		SavedTrack s => s.Title,
		_ => ""
	};

	internal static string ArtistOf(object item) => item switch
	{
		NeteaseTrack t => t.ArtistNames,
		KugouTrack k => k.SingerName,
		QqTrack q => q.Singer,
		BiliTrack b => b.Author,
		SavedTrack s => s.Artist,
		_ => ""
	};

	/// <summary>上一页：结果都缓存在列表里，直接把当前页号退回去重绘即可。</summary>
	private void GoPrevPage()
	{
		try
		{
			int cur = _visiblePage.TryGetValue(FooterEngine, out int v) ? v : 1;
			if (cur <= 1)
			{
				// 已经在第一页：不用重画，把可能滚下去的那段平滑带回顶部。
				AnimateScrollToTop();
				return;
			}
			_visiblePage[FooterEngine] = cur - 1;
			// 必须重绘：列表只装当前页，退页不重绘的话看到的还是原来那 30 条
			RenderEngine(FooterEngine, animate: false);
			// 方向是 -1：新一页从上面压下来，和「下一页」的方向感相反，
			// 连点两个方向时能看出到底是往前翻还是往回翻。
			AnimatePageTurn(-1);
			AnimateScrollToTop();
		}
		catch
		{
		}
	}

	/// <summary>回到第一页。结果全都缓存在列表里，直接把页号设成 1 再重绘。</summary>
	private void GoFirstPage()
	{
		try
		{
			int cur = _visiblePage.TryGetValue(FooterEngine, out int v) ? v : 1;
			if (cur <= 1)
			{
				AnimateScrollToTop();
				return;
			}
			_visiblePage[FooterEngine] = 1;
			RenderEngine(FooterEngine, animate: false);
			// 方向和「上一页」一致：都往回翻。用户是从很后面的页一路退回来的。
			AnimatePageTurn(-1);
			AnimateScrollToTop();
		}
		catch
		{
		}
	}

	private void ShowLoading(string text)
	{
		LoadingText.Text = text;
		LoadingOverlay.Visibility = Visibility.Visible;
		var rot = new DoubleAnimation(0.0, 360.0, TimeSpan.FromSeconds(1))
		{
			RepeatBehavior = RepeatBehavior.Forever
		};
		LoadingRotate.BeginAnimation(RotateTransform.AngleProperty, rot);
	}

	private void HideLoading()
	{
		LoadingRotate.BeginAnimation(RotateTransform.AngleProperty, null);
		LoadingOverlay.Visibility = Visibility.Collapsed;
	}

	private static string FirstArtist(string artist)
	{
		if (string.IsNullOrWhiteSpace(artist))
		{
			return "";
		}
		string[] parts = artist.Split('/', ',', '&', '、', ';');
		return parts.Length > 0 ? parts[0].Trim() : artist.Trim();
	}

	private static string Normalize(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		return Regex.Replace(text.ToLowerInvariant(), @"[\s\-_\.\u3000()（）\[\]【】,，、/·!！?？'""“”‘’:：;；]", "");
	}

	private static bool IsCommentable(object item)
	{
		return item is NeteaseTrack || item is BiliTrack || item is KugouTrack;
	}

	private static bool IsDownloadable(object item)
	{
		return item is BiliTrack || item is KugouTrack || item is NeteaseTrack || item is QqTrack;
	}

	private void FadeInResults()
	{
		try
		{
			var fade = new DoubleAnimation
			{
				From = 0.0,
				To = 1.0,
				Duration = TimeSpan.FromMilliseconds(320),
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			var slide = new DoubleAnimation
			{
				From = 10.0,
				To = 0.0,
				Duration = TimeSpan.FromMilliseconds(320),
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			// 复用同一个 RenderTransform。原来的写法每次都 new 一个新的塞进去，
			// 旧 Storyboard 既没 BeginAnimation(null) 取消、也没从旧变换上断开，
			// 连续翻页时会有两三个 320ms 的动画叠在一起抢 Opacity/Y，
			// 表现就是滚动过程中画面一直在跳。
			if (OnlineList.RenderTransform is not TranslateTransform trans)
			{
				trans = new TranslateTransform();
				OnlineList.RenderTransform = trans;
			}
			Storyboard.SetTarget(fade, OnlineList);
			Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
			Storyboard.SetTarget(slide, OnlineList);
			Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.YProperty));
			trans.Y = 10;
			var sb = new Storyboard();
			sb.Children.Add(fade);
			sb.Children.Add(slide);
			sb.Begin();
		}
		catch
		{
			OnlineList.Opacity = 1.0;
		}
	}

	/// <summary>音乐库收藏夹当前展示的那份列表（过滤/排序后的源快照）。
	/// 音乐库在重建在线视图时塞进来，本页点播时用它登记会话队列 ——
	/// 「下一首」才能顺着收藏夹这份列表走。</summary>
	private IReadOnlyList<object>? _librarySessionSource;

	/// <summary>由音乐库页在刷新在线视图时登记/清空（进本地视图传 null）。</summary>
	public void SetLibrarySessionSource(IReadOnlyList<object>? source)
	{
		_librarySessionSource = source;
	}

	/// <summary>点播时把「用户看到的那份列表」登记成 MusicService 的会话队列：
	/// 优先收藏夹源，其次歌单模式灌进来的那份，最后退到当前引擎的搜索缓存。
	/// 找不到（对象不在任何一份列表里）就不登记，老逻辑照旧。
	/// IndexOf 用自写的线性查找：源是 IReadOnlyList，没有现成的 IndexOf。</summary>
	private void RegisterSessionQueue(object item)
	{
		try
		{
			int idx;
			if (_librarySessionSource != null && (idx = IndexInList(_librarySessionSource, item)) >= 0)
			{
				MusicService.SetSessionQueue(_librarySessionSource, idx);
				return;
			}
			if (_engineCache.TryGetValue(_selectedEngine, out List<object>? cache)
				&& (idx = IndexInList(cache, item)) >= 0)
			{
				MusicService.SetSessionQueue(cache, idx);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "登记会话队列失败");
		}
	}

	private static int IndexInList(IReadOnlyList<object> list, object item)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (ReferenceEquals(list[i], item))
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>登记播放上下文（「启动时自动播放」用）：和 RegisterSessionQueue
	/// 同一套判定，把「这次点播来自哪份列表」记成一个可恢复的 listKey ——
	/// 收藏夹视图 libfav / 歌单 playlist:&lt;平台&gt;:&lt;id&gt; / 搜索
	/// search:&lt;引擎&gt;:&lt;关键词&gt;。定位不到就记空串，恢复端静默跳过。
	/// 记录失败只吞掉，不影响播放本身。</summary>
	private void RegisterPlayContext(object item)
	{
		try
		{
			string listKey = "";
			if (_librarySessionSource != null && IndexInList(_librarySessionSource, item) >= 0)
			{
				listKey = "libfav";
			}
			else if (_playlistMode)
			{
				if (!string.IsNullOrWhiteSpace(_playlistId))
				{
					listKey = "playlist:" + _selectedEngine + ":" + _playlistId;
				}
			}
			else if (_lastKeyword.Length > 0
				|| _engineQuery.TryGetValue(_selectedEngine, out string? q) && !string.IsNullOrWhiteSpace(q))
			{
				// 记「喂给这个引擎的那条查询」而不是原始输入框关键词：带变体扩展的
				// 查询才是真正产出这批结果的东西，按它重搜才能回到同一份列表附近。
				string query = _engineQuery.TryGetValue(_selectedEngine, out string? eq) && eq.Length > 0
					? eq
					: _lastKeyword;
				listKey = "search:" + _selectedEngine + ":" + query;
			}
			MusicService.PendingPlayContext = new PlayContext { Page = "online", ListKey = listKey };
		}
		catch
		{
		}
	}

	private void Play(object item)
	{
		// 定位球记忆：在线条目点播时就地记下「源+关键词+所在页」。
		// 之后用户切了源/换了搜索词/翻了页，定位球仍能按这份记忆恢复现场
		// （见 RevealPlayingRow），而不是对着空列表报「定位不到」。
		RememberLocate(item);
		// 每次点播都先刷新会话队列：用户最近一次从哪份列表点的歌，
		// 「上一首/下一首」就该顺哪份走（音乐库收藏夹/歌单/搜索结果）。
		RegisterSessionQueue(item);
		// 播放上下文同点登记：切歌成功后由 PlayCore 落盘，启动自动播放据此恢复列表。
		RegisterPlayContext(item);
		// 可达性检查：只有「实测确认过确实受限」的才跳过——那种要直链必被拒，
		// 早点把提示摆出来，用户不必干等一次注定失败的请求。
		//
		// 仅仅是平台字段说收费（TrackAccess 没记过结论）时**照常试着播**：
		// 那些字段错得挺多，一首能听的歌被标签劝退比多等一秒更糟。
		// 试的结果会被 TrackAccess 记下来：能播就把标签撤掉，
		// 被拒就记成受限，下次再到这一条就直接给提示。
		if (TrackAccess.StateOf(item) == AccessState.Restricted)
		{
			// 任何入口（搜索页 / 音乐库收藏夹）点受限歌都只弹一句「VIP/版权受限」
			// 的全局 Toast：不再自动搜 B 站、不再自动弹替代源面板、不再自动写
			// 状态行——那条「已搜索 B 站音源」的链路已整体删除。
			// B 站替代源的唯一入口是顶部源选择里的「BiliBili 源下载」手动切换，
			// 这里只把受限曲目记成上下文，供手动切源时摆候选面板用。
			RememberVipTrack(Title(item), FirstArtist(ArtistOf(item)));
			ToastVipNotice();
			return;
		}
		switch (item)
		{
			case NeteaseTrack track:
				MusicService.PlayOnline(track, err => PlayOnlineFailed(track, err));
				break;
			case BiliTrack bili:
				_ = PlayBiliAsync(bili);
				break;
			case KugouTrack kugou:
				_ = PlayKugouAsync(kugou);
				break;
			case QqTrack qq:
				_ = PlayQqAsync(qq);
				break;
			case SavedTrack s when string.Equals(s.Type, "soda", StringComparison.Ordinal):
				// 汽水曲目（搜索结果 / 收藏夹入库形态）：分享页免登录取流，
				// 下载进缓存后按本地文件播（与酷狗/QQ 同一条动线）。
				_ = PlaySodaAsync(s);
				break;
			case SavedTrack:
				// 其它形态的 SavedTrack 不走在线取流；给一句明确提示而不是静默
				// 无反应——静默会让用户误以为点了没响应是程序出 bug。
				MainWindow.Instance?.ShowToast(Loc.Default["online_soda_play_unsupported"], false);
				break;
		}
	}

	private static (string Label, Brush Brush)? EngineTag(object item)
	{
		return item switch
		{
			NeteaseTrack => ("网易云", new SolidColorBrush(Color.FromRgb(0xC2, 0x0C, 0x0C))),
			KugouTrack => ("酷狗", new SolidColorBrush(Color.FromRgb(0x2C, 0xA5, 0xF6))),
			QqTrack => ("QQ音乐", new SolidColorBrush(Color.FromRgb(0x31, 0xC2, 0x7C))),
			// 汽水品牌色改为官方「汽水绿」（黑底 + 青绿 ≈ #24E3C5）：
			// 走 TrackRows.SodaAccentBrush，亮色主题下自动换成深青保证白底可读。
			// 注意 SavedTrack 还有收藏夹/本地等形态，只有 Type=soda 才贴汽水标。
			SavedTrack s when string.Equals(s.Type, "soda", StringComparison.Ordinal)
				=> ("汽水", TrackRows.SodaAccentBrush()),
			BiliTrack => ("BiliBili", new SolidColorBrush(Color.FromRgb(0xFB, 0x72, 0x99))),
			_ => null
		};
	}

	private static string SubLine(object item)
	{
		return item switch
		{
			NeteaseTrack t => t.ArtistNames + "  ·  " + TrackRows.FormatDuration(t.DurationMs),
			KugouTrack k => k.SingerName + "  ·  " + FormatKugouExtras(k),
			// 搜索结果行不再显示播放量。副标题里塞「UP主 · 播放 12.3万 · 时长」
			// 三个字段，播放量既不是筛选依据也占掉了宽度，
			// 而且它紧挨着下载/播放按钮，容易被当成按钮的一部分。
			// 需要看播放量时用「按播放量排序」，热度排序仍然可用。
			BiliTrack b => (string.IsNullOrEmpty(b.Author) ? b.TypeName : b.Author) + "  ·  " + TrackRows.FormatDuration((long)b.Duration.TotalSeconds * 1000),
			QqTrack q => q.Singer + (string.IsNullOrEmpty(q.Album) ? "" : "  ·  " + q.Album) + "  ·  " + TrackRows.FormatDuration(q.DurationMs),
			SavedTrack s => s.Artist + (s.DurationMs > 0 ? "  ·  " + TrackRows.FormatDuration(s.DurationMs) : ""),
			_ => ""
		};
	}

	private static string FormatKugouExtras(KugouTrack k)
	{
		string text = "";
		if (!string.IsNullOrWhiteSpace(k.AlbumName))
		{
			text += k.AlbumName + "  ·  ";
		}
		text += TrackRows.FormatDuration(k.DurationMs);
		string size = KugouClient.FormatFileSize(k.FileSize);
		if (!string.IsNullOrEmpty(size))
		{
			text += "  ·  " + size;
		}
		return text;
	}

	internal static string CommentSource(object item)
	{
		return item switch
		{
			NeteaseTrack => "netease",
			BiliTrack => "bili",
			KugouTrack => "kugou",
			_ => ""
		};
	}

	/// <summary>bilibili 的封面 CDN 时不时吐 http 地址，某些网络环境下会被
	/// 断掉重定向直接拒绝，升成 https 稳一点；其它来源原样返回。</summary>
	private static string NormalizeCoverUrl(string url)
	{
		if (string.IsNullOrEmpty(url))
		{
			return url;
		}
		return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			? "https://" + url.Substring(7)
			: url;
	}

	/// <summary>行收藏/封面用的统一映射。原来是 private，音乐库「收藏夹」页
	/// 复用同一份映射（AccountLibraryService.ToSavedTrack 直接转发到这里）：
	/// 同一首歌两边存进收藏的 Key 必须一致，各写一份迟早会漂移。</summary>
	internal static SavedTrack? ToSavedTrack(object item)
	{
		switch (item)
		{
			case NeteaseTrack t:
				return new SavedTrack
				{
					Type = "netease",
					Id = t.Id.ToString(),
					Title = t.Name,
					Artist = t.ArtistNames,
					DurationMs = t.DurationMs,
					Cover = t.AlbumInfo?.PicUrl ?? ""
				};
			case KugouTrack k:
				return new SavedTrack
				{
					Type = "kugou",
					Id = k.Hash,
					Title = k.Display,
					Artist = k.SingerName,
					DurationMs = k.DurationMs,
					Cover = k.ImgUrl ?? ""
				};
			case BiliTrack b:
				return new SavedTrack
				{
					Type = "bili",
					Id = b.Bvid,
					Title = b.Display,
					Artist = b.Author,
					DurationMs = (long)b.Duration.TotalMilliseconds,
					Cover = NormalizeCoverUrl(b.Pic)
				};
			case QqTrack q:
				return new SavedTrack
				{
					Type = "qq",
					Id = q.Mid,
					Title = q.Display,
					Artist = q.Singer,
					DurationMs = q.DurationMs,
					Cover = q.CoverUrl
				};
			case SavedTrack s:
				// 汽水曲目入库时本身就是 SavedTrack（音乐库收藏夹形态），原样返回即可。
				return s;
			default:
				return null;
		}
	}

	// 双击播放已经由 TrackRows.BuildRow 统一处理（每一行的 MouseLeftButtonDown 里
	// 数 ClickCount>=2 就播），这里原来还有一个 OnlineList_PreviewMouseDoubleClick，
	// 两者会各播一次——WPF 里 PreviewMouseDoubleClick 在第二次 MouseUp 时触发，
	// 行的 MouseLeftButtonDown(ClickCount=2) 在第二次 MouseDown 时触发，两个都会跑。
	// 所以页面级这个处理器删掉，只留行内那一个。

	/// <summary>网易云播放在线失败的兜底。报错文案带「VIP / 会员 / 付费 / 版权」
	/// 时只弹一句全局 Toast——自动摆 B 站替代源面板的分支已整体删除，
	/// 替代源的唯一入口是顶部源选择里的「BiliBili 源下载」手动切换。</summary>
	private void PlayOnlineFailed(NeteaseTrack track, string? error)
	{
		Dispatcher.BeginInvoke(new Action(() =>
		{
			// 报错文案里带「VIP / 会员 / 付费 / 版权」才是真的受限。
			// TrackAccess 那边已经按「接口给不给直链」记过结论了，这里是补一层
			// 文案兜底（同一句也会出现在歌词/副标题之类的场景里）。
			if (TrackAccess.SoundsRestricted(error))
			{
				RememberVipTrack(track.Name, FirstArtist(track.ArtistNames ?? ""));
				ToastVipNotice();
				return;
			}
			SetStatus(error ?? Loc.Default["cm_fail"]);
		}));
	}

	// 最近一次实测受限的曲目（曲名 + 歌手）。B 站替代源面板的唯一入口是
	// 顶部「BiliBili 源下载」手动切换：切过去时若记有这份上下文，面板按它
	// 列候选；从没碰过受限歌时切源就只有正常的 B 站搜索结果，什么都不弹。
	private string _lastVipName = "";
	private string _lastVipArtist = "";

	private void RememberVipTrack(string name, string artist)
	{
		_lastVipName = name ?? "";
		_lastVipArtist = artist ?? "";
	}

	/// <summary>受限歌曲的替代源候选面板。只允许手动入口调用（顶部源选择切到
	/// 「BiliBili 源下载」），播放链路里任何地方都不得自动触发。</summary>
	private void ShowVipFallback(string name, string singer)
	{
		// 原来这里会把评论区整个收起来，因为两个面板是叠在一起的、互相压。
		// 现在评论在右栏、B站源在左栏，不再冲突，也不用互相让位。
		// 状态行不再自动写「已搜索 B 站音源」：面板底部有自己的加载/计数状态。
		_biliTrackName = name;
		BiliSourceHeaderText.Text = Loc.Default["bili_src_title"] + "  ·  " + name;
		BiliSourceSubText.Text = Loc.Default["bili_src_sub"];
		SetBiliStatus(BiliStatus.Loading);
		BiliSourceList.Children.Clear();
		BiliSourcePanel.Visibility = Visibility.Visible;
		_ = LoadVipFallbackAsync(name, singer);
	}

	/// <summary>受限提示：一句全局 Toast。所有平台、所有入口的受限分支都只走到
	/// 这里为止——自动搜 B 站 / 自动弹替代源面板 / 自动写搜索页状态行的链路
	/// 已按需求整体删除，替代源只能从顶部源选择「BiliBili 源下载」手动进。</summary>
	private static void ToastVipNotice()
	{
		if (Application.Current?.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["online_vip"], false);
		}
	}

	private async Task LoadVipFallbackAsync(string name, string singer)
	{
		try
		{
			// VIP 歌曲在原平台拿不到直链，只能退到 B 站找候选。
			// 但只按曲名搜很容易搜到翻唱/现场版，命中很少，
			// 所以按「曲名」→「曲名+歌手」多搜几轮合并去重，再按播放量排。
			// 只在这里补充候选，不会静默替换用户选的那首歌。
			var results = await SearchBiliCandidatesAsync(name, singer);
			if (BiliSourcePanel.Visibility != Visibility.Visible)
			{
				return;
			}
			BiliSourceList.Children.Clear();
			if (results.Count == 0)
			{
				// 被风控和「真的没这个歌」是两回事，不能都显示成「没有找到」，
				// 否则用户只会以为歌不存在，其实是请求被限流了
				SetBiliStatus(BiliClient.LastSearchBlocked ? BiliStatus.Blocked : BiliStatus.Empty);
				return;
			}
			SetBiliStatus(BiliStatus.Count, results.Count);
			foreach (BiliTrack b in results)
			{
				BiliSourceList.Children.Add(BuildBiliSourceRow(b));
			}
		}
		catch
		{
			if (BiliSourcePanel.Visibility == Visibility.Visible)
			{
				SetBiliStatus(BiliStatus.Fail);
			}
		}
	}

	/// <summary>多关键词搜 B 站候选：曲名 → 曲名+歌手，去重后按真实播放量降序。
	/// 曲名先去修饰（feat./live/cover 等），翻唱和现场版大多带这些词，
	/// 剥掉之后命中率明显更高。
	/// 入参是纯字符串而不是 NeteaseTrack：受限判定现在三个平台都要做，
	/// 酷狗/QQ 的结果里根本没有 NeteaseTrack，硬套类型就得先造一个假对象。</summary>
	private static async Task<List<BiliTrack>> SearchBiliCandidatesAsync(string name, string singer)
	{
		var merged = new List<BiliTrack>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(name)) return merged;
		bool blocked = false;
		foreach (string kw in BuildBiliQueries(name, singer))
		{
			var got = await BiliClient.SearchAsync(kw);
			if (BiliClient.LastSearchBlocked) blocked = true;
			foreach (BiliTrack b in got)
			{
				if (seen.Add(b.Bvid))
				{
					merged.Add(b);
				}
			}
			// 第一轮就有足够候选就不用再打了。B 站按 IP 限流，
			// 多余的请求只会把限流窗口撑长，反而害了后面的搜索。
			if (merged.Count >= 20) break;
		}
		BiliClient.SetLastSearchBlocked(blocked);
		// 只有 B 站真实播放量可用于排序
		return merged
			.OrderByDescending(b => b.Play)
			.ThenByDescending(b => b.Duration.TotalSeconds)
			.ToList();
	}

	private static IEnumerable<string> BuildBiliQueries(string name, string singer)
	{
		name = (name ?? "").Trim();
		singer = (singer ?? "").Trim();
		if (name.Length == 0) yield break;

		yield return name;

		string simplified = StripDecorators(name);
		if (simplified.Length > 0 && !string.Equals(simplified, name, StringComparison.OrdinalIgnoreCase))
		{
			yield return simplified;
		}

		// 曲名+歌手命中通常最准（同名歌很多），但要排在纯曲名后面
		if (singer.Length > 0)
		{
			if (simplified.Length > 0)
			{
				yield return simplified + " " + singer;
			}
			yield return name + " " + singer;
		}
	}

	/// <summary>剥掉曲名里的修饰词，只留下歌名本体。</summary>
	private static string StripDecorators(string name)
	{
		string s = name;
		// 括号里的内容通常是 feat./cover/影视/现场 之类的标注
		int p = s.IndexOf('(');
		if (p > 0) s = s.Substring(0, p);
		p = s.IndexOf('（');
		if (p > 0) s = s.Substring(0, p);
		p = s.IndexOf('[');
		if (p > 0) s = s.Substring(0, p);
		s = Regex.Replace(s, @"\s*[-–—]\s*(live|cover|remix|acoustic|instrumental|伴奏|翻唱|现场|纯音)\s*$",
			"", RegexOptions.IgnoreCase);
		s = Regex.Replace(s, @"\s*\(?\s*(feat\.?|featuring)\s.*$", "", RegexOptions.IgnoreCase);
		return s.Trim();
	}

	private Grid BuildBiliSourceRow(BiliTrack b)
	{
		var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		var titleBlock = new TextBlock
		{
			Text = b.Display,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Primary(),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		var subBlock = new TextBlock
		{
			Text = (string.IsNullOrEmpty(b.Author) ? b.TypeName : b.Author)
				+ "  ·  播放 " + BiliClient.FormatPlay(b.Play)
				+ "  ·  " + TrackRows.FormatDuration((long)b.Duration.TotalMilliseconds),
			FontSize = 11.5,
			Foreground = ThemeManager.Secondary(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 3, 0, 0)
		};
		var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
		textPanel.Children.Add(titleBlock);
		textPanel.Children.Add(subBlock);
		Grid.SetColumn(textPanel, 0);
		grid.Children.Add(textPanel);

		var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		Button dlBtn = TrackRows.IconButton("\uE896", 34, Loc.Default["act_download"]);
		dlBtn.Foreground = ThemeManager.Secondary();
		dlBtn.Click += (_, _) => _ = DownloadToLibraryAsync(b);
		actions.Children.Add(dlBtn);
		Button playBtn = TrackRows.IconButton("\uE768", 34, Loc.Default["act_play"]);
		playBtn.Background = TrackRows.AccentBrush;
		playBtn.Foreground = Brushes.White;
		playBtn.Click += (_, _) =>
		{
			BiliSourcePanel.Visibility = Visibility.Collapsed;
			_ = PlayBiliAsync(b);
		};
		actions.Children.Add(playBtn);
		Grid.SetColumn(actions, 1);
		grid.Children.Add(actions);
		return grid;
	}

	private void BiliSourceCloseButton_Click(object sender, RoutedEventArgs e)
	{
		BiliSourcePanel.Visibility = Visibility.Collapsed;
	}

	private async Task PlayBiliAsync(BiliTrack track)
	{
		SetStatus(Loc.Default["online_loading"] + " " + track.Display);
		// 命中同名已下载文件则直接播放，不重复下载
		string? local = MusicService.GetCachedDownload(track.Display);
		if (string.IsNullOrEmpty(local))
		{
			local = await BiliClient.DownloadAudioAsync(track, p =>
			{
				Dispatcher.BeginInvoke(new Action(() => SetStatus(p)));
			});
			if (string.IsNullOrEmpty(local))
			{
				SetStatus(Loc.Default["cm_fail"]);
				return;
			}
			local = MusicService.ImportToLibrary(local, track.Display);
			MusicService.RememberDownload(track.Display, local);
		}
		MusicService.PlayOnlineLocal(local, ToSavedTrack(track), track.Display);
		LogHelper.Log("B站播放: " + track.Display);
		ClearFetchProgress();
	}

	private async Task PlayKugouAsync(KugouTrack track)
	{
		SetStatus(Loc.Default["online_loading"] + " " + track.Display);
		string? local = await KugouClient.DownloadSongAsync(track, p =>
		{
			Dispatcher.BeginInvoke(new Action(() => ShowFetchPercent(p)));
		});
		if (string.IsNullOrEmpty(local))
		{
			// 拿不到直链 = 酷狗不给播（多为 VIP/付费）。只弹一句全局 Toast：
			// 状态行不再自动写「已搜索 B 站音源」（那条自动链路已删，
			// 替代源走顶部源选择「BiliBili 源下载」手动切换）。
			RememberVipTrack(track.Display, track.SingerName);
			ToastVipNotice();
			return;
		}
		string imported = MusicService.ImportToLibrary(local, track.Display);
		MusicService.PlayOnlineLocal(imported, ToSavedTrack(track), track.Display);
		LogHelper.Log("酷狗播放: " + track.Display);
		ClearFetchProgress();
	}

	/// <summary>汽水播放：分享页免登录取直链 → 下载缓存 → 按本地文件播。
	/// 与酷狗/QQ 同一条动线；拿不到直链（风控/下架）才提示。</summary>
	private async Task PlaySodaAsync(SavedTrack track)
	{
		string display = track.Title ?? track.Id ?? "汽水曲目";
		SetStatus(Loc.Default["online_loading"] + " " + display);
		string? local = await SodaClient.DownloadSongAsync(track, p =>
		{
			Dispatcher.BeginInvoke(new Action(() => ShowFetchPercent(p)));
		});
		if (string.IsNullOrEmpty(local))
		{
			// 拿不到直链（分享页被风控/曲目下架）：按普通「加载失败」提示，
			// 不再用「暂未接入」——现在播放链路已经接了。
			MainWindow.Instance?.ShowToast(Loc.Default["cm_fail"], false);
			return;
		}
		string imported = MusicService.ImportToLibrary(local, display);
		MusicService.RememberDownload(display, imported);
		MusicService.PlayOnlineLocal(imported, track, display);
		LogHelper.Log("汽水播放: " + display);
		ClearFetchProgress();
	}

	private async Task PlayQqAsync(QqTrack track)
	{
		SetStatus(Loc.Default["online_loading"] + " " + track.Display);
		string? local = await QqClient.DownloadSongAsync(track, p =>
		{
			Dispatcher.BeginInvoke(new Action(() => ShowFetchPercent(p)));
		});
		if (string.IsNullOrEmpty(local))
		{
			// 拿不到直链 = QQ 不给播（多为 VIP/付费）。只弹一句全局 Toast：
			// 状态行不再自动写「已搜索 B 站音源」（那条自动链路已删，
			// 替代源走顶部源选择「BiliBili 源下载」手动切换）。
			RememberVipTrack(track.Display, track.Singer);
			ToastVipNotice();
			return;
		}
		string imported = MusicService.ImportToLibrary(local, track.Display);
		MusicService.RememberDownload(track.Display, imported);
		MusicService.PlayOnlineLocal(imported, ToSavedTrack(track), track.Display);
		LogHelper.Log("QQ播放: " + track.Display);
		ClearFetchProgress();
	}

	private async Task DownloadToLibraryAsync(object item)
	{
		string title = Title(item);
		string? local = null;
		try
		{
			switch (item)
			{
				case BiliTrack bili:
					SetStatus(Loc.Default.Format("dl_progress", title));
					local = await BiliClient.DownloadAudioAsync(bili, p =>
					{
						Dispatcher.BeginInvoke(new Action(() => SetStatus(p ?? (Loc.Default["dl_progress"] ?? "下载中") + " · " + title)));
					});
					break;
				case KugouTrack kugou:
					SetStatus(Loc.Default.Format("dl_progress", title));
					local = await KugouClient.DownloadSongAsync(kugou, p =>
					{
						Dispatcher.BeginInvoke(new Action(() => ShowFetchPercent(p, title)));
					});
					break;
				case NeteaseTrack netease:
						SetStatus(Loc.Default.Format("dl_progress", title));
						string? url = await NeteaseApiClient.GetSongUrlAsync(netease.Id);
						if (string.IsNullOrEmpty(url))
						{
						SetStatus(Loc.Default["online_vip"]);
						return;
					}

					local = await NeteaseApiClient.DownloadSongAsync(netease.Id, url, p =>
						{
							Dispatcher.BeginInvoke(new Action(() => ShowFetchPercent(p, title)));
						});
						break;
					case QqTrack qq:
						SetStatus(Loc.Default.Format("dl_progress", title));
						local = await QqClient.DownloadSongAsync(qq, p =>
						{
							Dispatcher.BeginInvoke(new Action(() => ShowFetchPercent(p, title)));
						});
						break;
				}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "下载到音乐库失败: " + title);
			SetStatus(Loc.Default.Format("dl_failed", title));
			return;
		}
		if (string.IsNullOrEmpty(local))
		{
			SetStatus(Loc.Default.Format("dl_failed", title));
			return;
		}
		string imported = MusicService.ImportToLibrary(local, title);
		MusicService.RememberDownload(title, imported);
		SetStatus(Loc.Default.Format("dl_done", title));
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default.Format("dl_done", title));
		}
		LogHelper.Log("已下载到音乐库: " + title + " -> " + imported);
	}

	/// <summary>统一的底部状态行写入。空串时把整行收起来，
	/// 这样状态行不会在没内容的时候白占 19px，列表能铺满卡片底部。
	///
	/// 收起用淡出而不是直接 Collapsed：写进度的时候数字是一路跳的，
	/// 播完那句「加载中 100%」要收掉，直接砍掉是「啪」一下没了，
	/// 淡出 180ms 看着才是这条提示自己退场。出现方向则不动画 ——
	/// 进度是频繁触发的，每次都淡入会把字糊成一片，反而看不清在走到几。</summary>
	private void SetStatus(string text)
	{
		// 普通状态（搜索结果为空、加载条数…）自己管生命周期。
		// 定时器只服务于「限次提示」那条路，所以先掐掉，
		// 否则上一条提示的定时器会把这一条提前抹掉。
		StopStatusTimer();
		string next = text ?? "";
		if (string.IsNullOrWhiteSpace(next))
		{
			FadeOutStatus();
			return;
		}
		StopStatusFade();
		StatusText.BeginAnimation(UIElement.OpacityProperty, null);
		StatusText.Opacity = 1.0;
		StatusText.Text = next;
		StatusText.Visibility = Visibility.Visible;
	}

	/// <summary>状态行淡出后真的收起来。淡出期间保持 Visible，
	/// 这样它的高度还在，列表不会在动画过程中往上跳一下。</summary>
	private void FadeOutStatus()
	{
		if (StatusText.Visibility != Visibility.Visible || StatusText.Opacity <= 0.01)
		{
			// 已经是空的（或正在淡出），直接把可见性收掉即可。
			StopStatusFade();
			StatusText.Text = "";
			StatusText.Visibility = Visibility.Collapsed;
			return;
		}
		StopStatusFade();
		var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
		};
		fade.Completed += (_, _) =>
		{
			// 动画期间可能又写了新内容，那就别收。
			if (StatusText.Opacity <= 0.01)
			{
				StatusText.Text = "";
				StatusText.Visibility = Visibility.Collapsed;
			}
		};
		_statusFade = fade;
		StatusText.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	private void StopStatusFade()
	{
		if (_statusFade != null)
		{
			StatusText.BeginAnimation(UIElement.OpacityProperty, null);
			_statusFade = null;
		}
	}

	private void StopStatusTimer()
	{
		if (_statusTimer != null)
		{
			_statusTimer.Stop();
			_statusTimer = null;
		}
	}

	/// <summary>拉取/播放成功后把左下角那条百分比收掉（换成「拉取成功」闪一下再退场）。
	///
	/// 进度回调最后一次写的是「加载中 100%」，而播放成功的分支原来直接
	/// 就去 Play 了，没回头清这一行 —— 于是百分比停在 100% 再也不消失，
	/// 用户看着像还在转、其实歌都放起来了。下载失败那条路有 SetStatus
	/// 覆盖所以看不出来，只有成功这条路会漏。</summary>
	private void ClearFetchProgress() => ShowStatusForSeconds(Loc.Default["online_fetch_ok"], 2);

	/// <summary>下载进度直接进左下角状态行。到 100% 时不写「正在搜索… 100%」
	/// 这类看着像还卡住的残句——用户点名要这么处理——换成闪一句「拉取成功」退场。
	/// 90% 之后磁盘写入 / 导入库里常常还要再磨几百毫秒，如果这期间又冒一次
	/// 100% 会把刚闪出来的「拉取成功」顶掉，所以 99.9% 就切换，留出缓冲。</summary>
	private void ShowFetchPercent(double p, string? title = null)
	{
		if (p >= 0.999)
		{
			ShowStatusForSeconds(Loc.Default["online_fetch_ok"] + (string.IsNullOrEmpty(title) ? "" : " · " + title), 2);
			return;
		}
		SetStatus(Loc.Default["online_loading"] + " " + (int)Math.Round(p * 100) + "%" + (string.IsNullOrEmpty(title) ? "" : " · " + title));
	}

	/// <summary>左下角那条提示只闪一下就走。
	/// 之前它和「加载中 40%」共用同一行且不自己消失，
	/// 「VIP 已切到 B 站」这种一次性说明会一直压在左下角，
	/// 挡住正在更新的进度文字，用户只能靠猜它是不是还在加载。
	/// 现在到期自动隐藏；但如果期间又有新状态（真在下载/加载），
	/// 定时器回调会发现自己已经过期，就不覆盖新文字。</summary>
	private void ShowStatusForSeconds(string text, int seconds)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		int stamp = ++_statusStamp;
		// 显示侧要和 SetStatus 一样先把可能正在淡出的旧动画掐掉、
		// 把不透明度按回 1，否则新提示会接着上一条的半透明往下淡，
		// 看起来像自己本来就是残的。
		StopStatusFade();
		StatusText.BeginAnimation(UIElement.OpacityProperty, null);
		StatusText.Opacity = 1.0;
		StatusText.Text = text;
		StatusText.Visibility = Visibility.Visible;

		StopStatusTimer();
		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, seconds)) };
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			_statusTimer = null;
			// 期间被 SetStatus 换掉了内容就别动它
			if (stamp != _statusStamp) return;
			// 到点后走淡出，和进度那条收尾是同一条路。
			// 原来这里是直接 Collapsed，等于「啪」一下就没了 ——
			// 而这条路恰好是「检测到 VIP / 版权受限」这类一次性提示走的，
			// 也就是说左下角最常出现的那条提示根本享受不到淡出。
			FadeOutStatus();
		};
		_statusTimer = timer;
		timer.Start();
	}

}
