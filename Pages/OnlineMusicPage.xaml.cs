using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class OnlineMusicPage : UserControl, IMusicPage
{
	/// <summary>四个源的固定顺序，源选项行和预取都按这个顺序走。</summary>
	private static readonly string[] EngineOrder = { "netease", "kugou", "qq", "bili" };

	/// <summary>B 站没有分页接口，只能出第 1 页，所以它不参与后续页的预取。</summary>
	private static bool SupportsPaging(string engine) => engine != "bili";

	/// <summary>每个源单页拉多少条。</summary>
	private const int PageSize = 30;

	/// <summary>单行实际占高（含行间距），翻页滚动按这个步长走。
	/// 必须和 TrackRows.BuildRow 里封面尺寸 + Padding + Margin 对得上
	/// （38 封面 + 5*2 padding + 2*2 margin = 54），否则翻页会滚偏。</summary>
	private const double RowHeight = 54.0;

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

	/// <summary>左下角限次提示的自动隐藏定时器；见 ShowStatusForSeconds。</summary>
	private DispatcherTimer? _statusTimer;
	private int _statusStamp;

	/// <summary>B 站播放量排序开关。默认开 —— B 站结果里混着大量
	/// 零播放和搬运条目，按播放量排完基本就是「能听的在前」。</summary>
	private bool _sortByPlay = true;

	// 按源缓存「已经建好的行控件」，切源时直接复用，别再为 60 条结果重建一遍 UI。
	// 数据条数一变（重新搜索 / 又翻一页）就重建，见 RenderEngine。
	// 行控件缓存：源 -> (条目 key -> 已建好的行)。
	// 按条目 key 而不是按下标存，B 站按播放量重排时只改顺序、不必重建控件。
	private readonly Dictionary<string, Dictionary<string, FrameworkElement>> _rowCache
		= new(StringComparer.OrdinalIgnoreCase);

	// 排序规则已内置：B 站结果固定按播放量降序，其它平台保持平台原顺序，
	// 界面上不再有可点的开关，所以也不需要开关状态和事件屏蔽字段了。
	private bool _searching;

	/// <summary>搜索代次号。用户在上一轮结果回来之前又搜了一次的话，
	/// 老结果直接丢掉，不然会出现「先搜 A 再搜 B，最后显示 A 的结果」。</summary>
	private int _searchToken;
	// 评论区请求令牌：防止连点两首歌时旧请求后到、覆盖新歌的评论
	private int _commentToken;
	// 当前正在看评论的曲目，发表评论时要用
	private object? _commentTarget;
	// 下一页游标，空串表示第一页
	private string _commentCursor = "";
	// 本轮是否已经到底 / 正在取，避免触底反复发请求
	private bool _commentMoreDone;

	/// <summary>页脚里那个水平 StackPanel，null 表示当前没有页脚（更新页码前先判空）。</summary>
	private StackPanel? _footerItem;
	private TextBlock _footerPageText = null!;
	private Button _footerPrev = null!;
	private Button _footerNext = null!;

	public OnlineMusicPage()
	{
		InitializeComponent();
		HintText.Visibility = Visibility.Visible;
		ResetComments();
		TrackRows.AccentChanged += OnAccentChanged;
	}

	/// <summary>换强调色：行缓存里存着的行是按旧颜色建的，必须整批丢掉重建，
	/// 否则切换强调色后搜索结果还是上一套颜色的播放键和源标签。</summary>
	private void OnAccentChanged()
	{
		_rowCache.Clear();
		OnlineList.Items.Clear();
		if (!string.IsNullOrEmpty(_lastKeyword))
		{
			RenderEngine(_selectedEngine, animate: false);
		}
	}

	public void OnNavigatedTo()
	{
		BuildEnginePanel();
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
		// 立即重排当前源，不然要等下次搜索才看出生效
		RenderEngine(_selectedEngine);
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
		if (_lastKeyword.Length == 0)
		{
			_sortHint.Visibility = Visibility.Collapsed;
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
		bool changed = !string.Equals(_selectedEngine, key, StringComparison.Ordinal);
		_selectedEngine = key;
		foreach (var kv in _engineButtons)
		{
			ApplyEngineButtonVisual(kv.Value, string.Equals(kv.Key, key, StringComparison.Ordinal));
		}
		// 只有真换了源才落盘。SettingsManager.Save 是同步写文件，
		// 同一个源反复点也去写一遍，点按钮的手感会发涩。
		if (changed)
		{
			var s = SettingsManager.Instance;
			s.OnlineEngines = new List<string> { key };
			SettingsManager.Save(s);
		}
		UpdateSortHint();

		if (string.IsNullOrEmpty(_lastKeyword))
		{
			return;
		}
		RenderEngine(key);
		// 切到某个源就顺手把它下一页预取掉。RenderEngine 末尾统一会做这件事，
		// 这里不用再单独调一次。
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

	private void ResetEngineCaches()
	{
		_engineCache.Clear();
		_engineSeen.Clear();
		_nextPage.Clear();
		_exhausted.Clear();
		_prefetching.Clear();
		_visiblePage.Clear();
		// 数据整个换了，之前建好的行控件全部作废。
		_rowCache.Clear();
		foreach (string key in EngineOrder)
		{
			_engineCache[key] = new List<object>();
			_engineSeen[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			_nextPage[key] = 0;
			_exhausted[key] = false;
			_visiblePage[key] = 1;
		}
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
		_searching = true;
		_loadingMore = false;
		HideLoading();
		HintText.Visibility = Visibility.Collapsed;
		ShowLoading(Loc.Default["online_loading_search"]);
		try
		{
			ResetEngineCaches();
			_lastKeyword = keyword;

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
				MergeInto(kv.Key, await kv.Value);
			}

			// VIP / 版权受限的曲目，用户真正要的是「能播的那份」。
			// 原来只有点了播放、拿不到 url 之后才去 B 站搜候选，
			// 于是搜索结果页看起来一切正常，非会员点进去才发现下不了。
			// 现在搜完就直接把 B 站候选顶到该源列表最前面。
			int merged = await MergeBiliCandidatesForRestrictedAsync(token);

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
			LogHelper.Log("在线搜索: " + keyword + " -> "
				+ string.Join(" / ", EngineOrder.Select(k => k + " " + _engineCache[k].Count)));
			// 全空时报出真实原因。QQ/B站 0 条多数是被限流，不是「没这首歌」，
			// 不说清楚的话用户只会反复重搜。
			SetStatus(BuildSearchStatus(keyword));

			// 提示放在最后：SetStatus 会掐掉定时器并清空行，
			// 早一步设置的话这条一次性说明会被下面的状态覆盖掉。
			if (merged > 0)
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

	/// <summary>逐个平台判断「这批结果里有没有下不了的歌」，
	/// 有就去 B 站找同一首的候选，并插到该平台结果的最前面。
	/// 每次搜索最多处理 <see cref="MaxVipProbes"/> 首受限歌：
	/// B 站按 IP 限流，一次搜索打十几个关键词会把限流窗口撑到几分钟，
	/// 反而害了后面所有搜索。返回实际插入的条数。</summary>
	private const int MaxVipProbes = 3;

	/// <summary>每个受限歌最多插几条候选。多了会把真正的原曲挤到很后面。</summary>
	private const int MaxCandidatesPerTrack = 5;

	private async Task<int> MergeBiliCandidatesForRestrictedAsync(int token)
	{
		// B 站自己就是一个被选中的源时不用再合：用户已经能看到那批结果了，
		// 往别的源里再复制一份只会让点「播放更多」时的条数对不上。
		if (_engineCachesContain("bili"))
		{
			return 0;
		}

		var byEngine = new List<KeyValuePair<string, List<object>>>();
		foreach (string key in EngineOrder)
		{
			if (key == "bili") continue;
			if (_engineCache.TryGetValue(key, out List<object>? cache) && cache.Count > 0)
			{
				byEngine.Add(new KeyValuePair<string, List<object>>(key, cache));
			}
		}

		int inserted = 0;
		int probes = 0;
		foreach (var kv in byEngine)
		{
			var restricted = kv.Value.Where(IsRestricted).ToList();
			if (restricted.Count == 0) continue;

			var fresh = new List<object>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (object track in restricted)
			{
				if (token != _searchToken) return inserted;
				if (probes >= MaxVipProbes) break;
				probes++;

				string name = Title(track);
				string singer = FirstArtist(ArtistOf(track));
				List<BiliTrack> cands = await SearchBiliCandidatesAsync(name, singer);
				if (token != _searchToken) return inserted;

				foreach (BiliTrack b in cands)
				{
					if (fresh.Count >= MaxCandidatesPerTrack) break;
					// 同一个 B 站视频不要在一条结果里出现两次
					if (seen.Add(b.Bvid)) fresh.Add(b);
				}
			}

			if (fresh.Count == 0) continue;

			// 置顶合并：把 B 站候选插到该源最前面。
			// 用 Insert 而不是清空重建，是为了让已有的行控件缓存继续有效。
			var cache = kv.Value;
			cache.InsertRange(0, fresh);
			// 置顶进来的候选也要登记进去重集合，否则「加载更多」分页回来时
			// 同一首歌会被当成新结果再插一遍。
			foreach (object item in fresh)
			{
				string key2 = Normalize(Title(item)) + "|" + Normalize(FirstArtist(ArtistOf(item)));
				if (_engineSeen.TryGetValue(kv.Key, out HashSet<string>? s)) s.Add(key2);
			}
			inserted += fresh.Count;
			LogHelper.Log("VIP 置顶合并: 源=" + kv.Key + " 受限=" + restricted.Count
				+ " B站候选=" + fresh.Count);
		}
		return inserted;
	}

	private bool _engineCachesContain(string key)
	{
		return _engineCache.TryGetValue(key, out List<object>? c) && c.Count > 0;
	}

	/// <summary>这条结果是不是 VIP / 版权受限。
	/// 三个平台的判断字段都不一样，但只有它们各自有，
	/// 别的类型（比如已经插进来的 B 站候选）一律返回 false。</summary>
	private static bool IsRestricted(object item) => item switch
	{
		NeteaseTrack n => n.IsRestricted,
		KugouTrack k => k.IsRestricted,
		QqTrack q => q.IsRestricted,
		_ => false
	};

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

	private void MergeInto(string engine, List<object> items)
	{
		if (!_engineCache.TryGetValue(engine, out List<object>? cache) || _engineSeen[engine] == null)
		{
			return;
		}
		HashSet<string> seen = _engineSeen[engine];
		foreach (object item in items)
		{
			string key = Normalize(Title(item)) + "|" + Normalize(FirstArtist(ArtistOf(item)));
			if (seen.Add(key))
			{
				cache.Add(item);
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

	/// <summary>某个源的下一页：结果追加进缓存。</summary>
	private async Task<bool> FetchNextPageAsync(string engine, int pageIndex)
	{
		List<object> items;
		try
		{
			items = await FetchEnginePageAsync(engine, _lastKeyword, pageIndex);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "加载 " + engine + " 第 " + (pageIndex + 1) + " 页失败");
			return false;
		}
		MergeInto(engine, items);
		_nextPage[engine] = pageIndex + 1;
		if (engine == "netease")
		{
			SyncNeteaseQueue();
		}
		if (items.Count == 0)
		{
			// 拉空了就把游标退回去并记下来，免得后面每点一次都白请求一次空页
			_nextPage[engine] = pageIndex;
			_exhausted[engine] = true;
			return false;
		}
		return true;
	}

	/// <summary>后台预取某源的下一页。用户要的行为是：
	/// 搜索时只并发拉四源第 1 页 -> 点到某个源才开始拉它第 2 页
	/// -> 第 2 页到手后才开始拉第 3 页。所以这里只在「已经看过第 1 页」之后才动。
	/// 拉完要重绘，因为用户可能正盯着这个源看。</summary>
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
		int page = _nextPage[engine];
		Task task = RunPrefetchAsync(engine, page);
		_prefetching[engine] = task;
		return task;
	}

	private async Task RunPrefetchAsync(string engine, int page)
	{
		try
		{
			bool got = await FetchNextPageAsync(engine, page);
			// 预取到的新数据只对「当前页之后的内容」有影响。
			// 只有当用户已经翻到了能看到这批数据的页，才需要重画；
			// 还停在第 1 页时列表内容一个字都没变，重画只会让白滚动一下。
			if (got && string.Equals(engine, _selectedEngine, StringComparison.Ordinal)
				&& CurrentPageHasNewData(engine))
			{
				// 预取落地不播淡入：这时候用户可能正在滚列表，
				// 一次全列表的 320ms 透明度动画正���好卡住滚轮。
				RenderEngine(engine, animate: false);
			}
			LogHelper.Log($"预取 {engine} 第 {page + 1} 页: {(got ? "ok" : "无更多")}");
		}
		finally
		{
			_prefetching.Remove(engine);
		}
	}

	/// <summary>当前显示的这一页是不是有可能被新预取到的数据改变。
	/// 只有用户已经站在最后一页（下一页已经被预取填满）时才可能。</summary>
	private bool CurrentPageHasNewData(string engine)
	{
		int cached = _engineCache.TryGetValue(engine, out List<object>? c) ? c.Count : 0;
		int visible = _visiblePage.TryGetValue(engine, out int v) ? v : 1;
		return cached >= visible * PageSize;
	}

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
		double keepOffset = OnlineScroll.VerticalOffset;
		OnlineList.Items.Clear();

		List<object> cache = _engineCache.TryGetValue(engine, out List<object>? c) ? c : new List<object>();
		// B 站按播放量重排整份缓存。行控件按「条目 key」缓存，
		// 所以重排只改变行的先后顺序，不需要重建任何控件。
		if (SupportsPopularity(engine) && _sortByPlay)
		{
			cache = cache
				.Select((item, idx) => (item, idx, pop: PopularityOf(item)))
				.OrderByDescending(t => t.pop)
				.ThenBy(t => t.idx)
				.Select(t => t.item)
				.ToList();
		}

		int visible = _visiblePage.TryGetValue(engine, out int vp) ? vp : 1;
		if (visible < 1)
		{
			visible = 1;
		}
		int start = (visible - 1) * PageSize;
		int end = Math.Min(cache.Count, start + PageSize);
		for (int i = Math.Max(0, start); i < end; i++)
		{
			OnlineList.Items.Add(GetOrBuildRow(engine, cache[i]));
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
		BuildFooter(engine);
		UpdateFooter();
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
		if (map.TryGetValue(key, out FrameworkElement? hit) && hit.Parent == null)
		{
			return hit;
		}
		FrameworkElement row = BuildEngineRow(engine, item);
		map[key] = row;
		return row;
	}

	/// <summary>给单条结果建一行。排序已经搬到 RenderEngine，这里只管建控件。</summary>
	private FrameworkElement BuildEngineRow(string engine, object item)
	{
		SavedTrack? track = ToSavedTrack(item);
		if (track == null)
		{
			return new TextBlock { Height = RowHeight };
		}
		object copy = item;
		Action? comment = IsCommentable(item) ? () => OpenComments(copy) : null;
		Action? download = IsDownloadable(item) ? () => _ = DownloadToLibraryAsync(copy) : null;
		(string label, Brush brush)? tag = EngineTag(item);
		var row = TrackRows.BuildRow(track, SubLine(item), () => Play(copy), UserLibrary.IsFavorite(track),
			() => UserLibrary.ToggleFavorite(track), null, comment, download, tag?.label, tag?.brush);
		row.Tag = copy;
		return row;
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
			// 翻到下一页：可见页号 +1
			int cur = _visiblePage.TryGetValue(engine, out int v) ? v : 1;
			_visiblePage[engine] = cur + 1;
			RenderEngine(engine);
			// 列表现在只装当前页，所以直接回到顶部；以前是算 (cur*PageSize*RowHeight)
			// 往下滚，可列表里是全部结果，滚下去看到的是重复内容。
			OnlineScroll.ScrollToVerticalOffset(0);
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

	private static string Title(object item) => item switch
	{
		NeteaseTrack t => t.Name,
		KugouTrack k => k.Display,
		QqTrack q => q.Name,
		BiliTrack b => b.Display,
		_ => ""
	};

	private static string ArtistOf(object item) => item switch
	{
		NeteaseTrack t => t.ArtistNames,
		KugouTrack k => k.SingerName,
		QqTrack q => q.Singer,
		BiliTrack b => b.Author,
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
				OnlineScroll.ScrollToVerticalOffset(0);
				return;
			}
			_visiblePage[FooterEngine] = cur - 1;
			// 必须重绘：列表只装当前页，退页不重绘的话看到的还是原来那 30 条
			RenderEngine(FooterEngine);
			OnlineScroll.ScrollToVerticalOffset(0);
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

	private void Play(object item)
	{
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
		}
	}

	private static (string Label, Brush Brush)? EngineTag(object item)
	{
		return item switch
		{
			NeteaseTrack => ("网易云", new SolidColorBrush(Color.FromRgb(0xC2, 0x0C, 0x0C))),
			KugouTrack => ("酷狗", new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x00))),
			QqTrack => ("QQ音乐", new SolidColorBrush(Color.FromRgb(0x31, 0xC2, 0x7C))),
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

	private static string CommentSource(object item)
	{
		return item switch
		{
			NeteaseTrack => "netease",
			BiliTrack => "bili",
			KugouTrack => "kugou",
			_ => ""
		};
	}

	private static SavedTrack? ToSavedTrack(object item)
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
					Cover = t.Album?.PicUrl ?? ""
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
					Cover = b.Pic
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
			default:
				return null;
		}
	}

	// 双击播放已经由 TrackRows.BuildRow 统一处理（每一行的 MouseLeftButtonDown 里
	// 数 ClickCount>=2 就播），这里原来还有一个 OnlineList_PreviewMouseDoubleClick，
	// 两者会各播一次——WPF 里 PreviewMouseDoubleClick 在第二次 MouseUp 时触发，
	// 行的 MouseLeftButtonDown(ClickCount=2) 在第二次 MouseDown 时触发，两个都会跑。
	// 所以页面级这个处理器删掉，只留行内那一个。

	private void PlayOnlineFailed(NeteaseTrack track, string? error)
	{
		Dispatcher.BeginInvoke(new Action(() =>
		{
			if (!string.IsNullOrEmpty(error) && error.Contains("VIP", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus(Loc.Default["online_vip"]);
				ShowBiliSources(track);
				return;
			}
			SetStatus(error ?? Loc.Default["cm_fail"]);
		}));
	}

	private void ShowBiliSources(NeteaseTrack track)
	{
		// 原来这里会把评论区整个收起来，因为两个面板是叠在一起的、互相压。
		// 现在评论在右栏、B站源在左栏，不再冲突，也不用互相让位。
		BiliSourceHeaderText.Text = Loc.Default["bili_src_title"] + "  ·  " + track.Name;
		BiliSourceSubText.Text = Loc.Default["bili_src_sub"];
		BiliSourceStatusText.Text = Loc.Default["bili_src_load"];
		BiliSourceList.Children.Clear();
		BiliSourcePanel.Visibility = Visibility.Visible;
		_ = LoadBiliSourcesAsync(track);
	}

	private async Task LoadBiliSourcesAsync(NeteaseTrack track)
	{
		try
		{
			// VIP 歌曲在原平台拿不到直链，只能退到 B 站找候选。
			// 但只按曲名搜很容易搜到翻唱/现场版，命中很少，
			// 所以按「曲名」→「曲名+歌手」多搜几轮合并去重，再按播放量排。
			// 只在这里补充候选，不会静默替换用户选的那首歌。
			var results = await SearchBiliCandidatesAsync(track.Name, FirstArtist(track.ArtistNames ?? ""));
			if (BiliSourcePanel.Visibility != Visibility.Visible)
			{
				return;
			}
			BiliSourceList.Children.Clear();
			if (results.Count == 0)
			{
				// 被风控和「真的没这个歌」是两回事，不能都显示成「没有找到」，
				// 否则用户只会以为歌不存在，其实是请求被限流了
				BiliSourceStatusText.Text = BiliClient.LastSearchBlocked
					? Loc.Default["bili_src_blocked"]
					: Loc.Default["bili_src_none"];
				return;
			}
			BiliSourceStatusText.Text = Loc.Default.Format("bili_src_count", results.Count);
			foreach (BiliTrack b in results)
			{
				BiliSourceList.Children.Add(BuildBiliSourceRow(b));
			}
		}
		catch
		{
			if (BiliSourcePanel.Visibility == Visibility.Visible)
			{
				BiliSourceStatusText.Text = Loc.Default["cm_fail"];
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
		MusicService.Play(local);
		MusicService.SetOnlineTitle(track.Display);
		LogHelper.Log("B站播放: " + track.Display);
	}

	private async Task PlayKugouAsync(KugouTrack track)
	{
		SetStatus(Loc.Default["online_loading"] + " " + track.Display);
		string? local = await KugouClient.DownloadSongAsync(track, p =>
		{
			Dispatcher.BeginInvoke(new Action(() => SetStatus(Loc.Default["online_loading"] + " " + (int)Math.Round(p * 100) + "%")));
		});
		if (string.IsNullOrEmpty(local))
		{
			SetStatus(Loc.Default["online_vip"]);
			return;
		}
		string imported = MusicService.ImportToLibrary(local, track.Display);
		MusicService.Play(imported);
		MusicService.SetOnlineTitle(track.Display);
		LogHelper.Log("酷狗播放: " + track.Display);
	}

	private async Task PlayQqAsync(QqTrack track)
	{
		SetStatus(Loc.Default["online_loading"] + " " + track.Display);
		string? local = await QqClient.DownloadSongAsync(track, p =>
		{
			Dispatcher.BeginInvoke(new Action(() => SetStatus(Loc.Default["online_loading"] + " " + (int)Math.Round(p * 100) + "%")));
		});
		if (string.IsNullOrEmpty(local))
		{
			SetStatus(Loc.Default["online_vip"]);
			return;
		}
		string imported = MusicService.ImportToLibrary(local, track.Display);
		MusicService.RememberDownload(track.Display, imported);
		MusicService.Play(imported);
		MusicService.SetOnlineTitle(track.Display);
		LogHelper.Log("QQ播放: " + track.Display);
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
						Dispatcher.BeginInvoke(new Action(() => SetStatus(Loc.Default["online_loading"] + " " + (int)Math.Round(p * 100) + "% · " + title)));
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
							Dispatcher.BeginInvoke(new Action(() => SetStatus(Loc.Default["online_loading"] + " " + (int)Math.Round(p * 100) + "% · " + title)));
						});
						break;
					case QqTrack qq:
						SetStatus(Loc.Default.Format("dl_progress", title));
						local = await QqClient.DownloadSongAsync(qq, p =>
						{
							Dispatcher.BeginInvoke(new Action(() => SetStatus(Loc.Default["online_loading"] + " " + (int)Math.Round(p * 100) + "% · " + title)));
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

	/// <summary>点评论按钮。同一首歌已经开着就收起来——面板上的关闭键被去掉之后，
	/// 这是唯一的收起入口，不然用户只能靠切歌把它挤掉。</summary>
	private void OpenComments(object item)
	{
		// 右侧评论区是常驻的一栏，但「评论列表」本身是用户点开才弹出来的，
		// 没点开时这里只显示一个空态提示。
		// 原来是浮层 + Visibility 开关，用户反馈「打不开」——
		// 浮层是盖在列表上的，既难点又和列表内容抢位置。
		if (CommentBodyHost.Visibility == Visibility.Visible && SameTrack(_commentTarget, item))
		{
			CollapseCommentList();
			CommentHeaderText.Text = Loc.Default["cm_title"];
			CommentLoginHint.Visibility = Visibility.Collapsed;
			SetCommentStatus("");
			CommentPlaceholderText.Visibility = Visibility.Visible;
			return;
		}
		_commentTarget = item;
		CommentHeaderText.Text = Loc.Default["cm_title"] + "  ·  " + Title(item);
		CommentPlaceholderText.Visibility = Visibility.Collapsed;
		SetCommentStatus(Loc.Default["cm_load"]);
		CommentMoreHint.Visibility = Visibility.Collapsed;
		CommentList.Children.Clear();
		_commentToken++;
		_commentCursor = "";
		_commentMoreDone = false;
		PopInCommentPanel();
		PopInCommentList();
		RefreshComposeState();
		_ = LoadCommentsAsync(item, _commentToken);
	}

	/// <summary>是不是同一首。列表里的行是 copy 过的对象实例，
	/// 直接 ReferenceEquals 永远为 false，所以按 key 比。</summary>
	private static bool SameTrack(object? a, object? b)
	{
		if (a == null || b == null) return false;
		if (a.GetType() != b.GetType()) return false;
		return string.Equals(Title(a), Title(b), StringComparison.Ordinal)
			&& string.Equals(ArtistOf(a), ArtistOf(b), StringComparison.Ordinal);
	}


	/// <summary>下滑接近底部才取下一页。游标为空说明已经翻到底，不再发请求。
	/// 原来的阈值只有 24px，而且只看 VerticalChange&gt;0：手指一滑就把整页内容
	/// 顶上来，用户还没看就又追加一页，看起来像评论自己涌出来。
	/// 现在要求「确实已经在底部附近」才触发，并且加载期间只认一次。</summary>
	private async void CommentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.VerticalChange <= 0 || _commentMoreDone || _commentTarget == null)
		{
			return;
		}
		// 内容不足一屏时 ScrollableHeight 为 0，滚不到底，也就没有「更多」
		if (CommentScroll.ScrollableHeight <= 0.0)
		{
			return;
		}
		if (e.VerticalOffset < e.ViewportHeight - 160)
		{
			return;
		}
		_commentMoreDone = true;
		CommentMoreHint.Text = Loc.Default["cm_more_loading"];
		CommentMoreHint.Visibility = Visibility.Visible;
		object target = _commentTarget;
		int token = _commentToken;
		try
		{
			OnlineComments.Page? page = await OnlineComments.FetchPageAsync(CommentSource(target), target, _commentCursor);
			if (page == null || token != _commentToken || !ReferenceEquals(_commentTarget, target))
			{
				return;
			}
			if (page.Comments.Count > 0)
			{
				AppendComments(page.Comments, stagger: false);
			}
			_commentCursor = page.Next;
			_commentMoreDone = string.IsNullOrEmpty(page.Next);
			CommentMoreHint.Text = _commentMoreDone ? Loc.Default["cm_no_more"] : Loc.Default["cm_more"];
			if (_commentMoreDone)
			{
				// 到底了就别一直挂着提示
				await Task.Delay(1800);
				if (token == _commentToken)
				{
					CommentMoreHint.Visibility = Visibility.Collapsed;
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "加载更多评论失败");
			_commentMoreDone = false;
			CommentMoreHint.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>状态文字和「加载更多」提示共用底部那一行，
	/// 这里统一收口，保证两者互斥，不会叠成两行糊字。</summary>
	private void SetCommentStatus(string text)
	{
		CommentStatusText.Text = text;
		if (!string.IsNullOrEmpty(text))
		{
			CommentMoreHint.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>评论列表展开动画：先稍微往左回弹一下，再往右归位到 0。
	/// 需求是「往左回弹往右归位」——注意 X 轴正方向是右边，
	/// 所以回弹那一帧是负值（往左），归位收在 0。</summary>
	private void PopInCommentList()
	{
		try
		{
			CommentBodyHost.Visibility = Visibility.Visible;
			CommentPlaceholderText.Visibility = Visibility.Collapsed;

			var trans = CommentBodyHost.RenderTransform as TranslateTransform;
			if (trans == null)
			{
				trans = new TranslateTransform();
				CommentBodyHost.RenderTransform = trans;
			}
			trans.BeginAnimation(TranslateTransform.XProperty, null);
			trans.BeginAnimation(TranslateTransform.YProperty, null);
			CommentBodyHost.BeginAnimation(UIElement.OpacityProperty, null);

			CommentBodyHost.Opacity = 0;
			trans.X = 16;

			// 关键帧：16 -> -7（往左回弹） -> 0（往右归位）
			var x = new DoubleAnimationUsingKeyFrames();
			x.KeyFrames.Add(new LinearDoubleKeyFrame(16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
			x.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)),
				new CubicEase { EasingMode = EasingMode.EaseOut }));
			x.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
				new CubicEase { EasingMode = EasingMode.EaseOut }));
			trans.BeginAnimation(TranslateTransform.XProperty, x);

			trans.BeginAnimation(TranslateTransform.YProperty,
				new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(220)))
				{ EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

			CommentBodyHost.BeginAnimation(UIElement.OpacityProperty,
				new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(150))));
		}
		catch
		{
		}
	}

	/// <summary>评论列表展开动画。原来只有 CommentBodyHost（列表本体）回弹，
	/// 上面的标题、登录提示和底下的输入框是「啪」一下直接出现的，
	/// 三个区域和主体的动画对不上，看起来像两段不同的界面。
	/// 现在整栏一起淡入并从右侧轻微滑入，列表本体的回弹保留在 PopInCommentList 里。</summary>
	private void PopInCommentPanel()
	{
		try
		{
			var pt = CommentPanel.RenderTransform as TranslateTransform;
			if (pt == null)
			{
				pt = new TranslateTransform();
				CommentPanel.RenderTransform = pt;
			}
			CommentPanel.BeginAnimation(UIElement.OpacityProperty, null);
			pt.BeginAnimation(TranslateTransform.XProperty, null);
			pt.X = 22;
			CommentPanel.Opacity = 0;

			pt.BeginAnimation(TranslateTransform.XProperty,
				new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260)))
				{ EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
			CommentPanel.BeginAnimation(UIElement.OpacityProperty,
				new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(190))));
		}
		catch
		{
			// 动画起不来时宁可立刻显示，也不要留在 Opacity=0 上整栏看不见。
			CommentPanel.BeginAnimation(UIElement.OpacityProperty, null);
			CommentPanel.Opacity = 1;
		}
	}

	/// <summary>评论列表收起动画：先往左顿一下，然后快速往右收缩并淡出。
	/// 收缩方向朝右（X 正方向），淡出结束后才真正 Collapse，
	/// 否则动画还没播完元素就没了，看起来像「卡了一下」。</summary>
	private void CollapseCommentList()
	{
		try
		{
			// 收起时输入框先退场：它只有一行高度，跟着淡出是平的，
			// 单独往下压一点更像「收起来」而不是「被关掉」。
			if (CommentComposeBar.Visibility == Visibility.Visible)
			{
				CommentComposeBar.BeginAnimation(UIElement.OpacityProperty,
					new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(120))));
			}

			CommentBodyHost.BeginAnimation(UIElement.OpacityProperty, null);
			CommentBodyHost.BeginAnimation(TranslateTransform.XProperty, null);
			CommentBodyHost.BeginAnimation(TranslateTransform.YProperty, null);

			var trans = CommentBodyHost.RenderTransform as TranslateTransform;
			if (trans == null)
			{
				trans = new TranslateTransform();
				CommentBodyHost.RenderTransform = trans;
			}
			trans.X = 0;
			trans.Y = 0;

			// 关键帧：0 -> -8（往左一下） -> 26（快速往右收缩出去）
			var x = new DoubleAnimationUsingKeyFrames();
			x.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
			x.KeyFrames.Add(new EasingDoubleKeyFrame(-8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)),
				new CubicEase { EasingMode = EasingMode.EaseOut }));
			x.KeyFrames.Add(new EasingDoubleKeyFrame(26, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)),
				new CubicEase { EasingMode = EasingMode.EaseIn }));
			var done = new EventHandler((s, _) =>
			{
				trans.BeginAnimation(TranslateTransform.XProperty, null);
				trans.X = 0;
				CommentBodyHost.Visibility = Visibility.Collapsed;
			});
			x.Completed += done;

			trans.BeginAnimation(TranslateTransform.XProperty, x);
			// 快速淡出，和收缩同步收尾
			CommentBodyHost.BeginAnimation(UIElement.OpacityProperty,
				new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(210))));
		}
		catch
		{
			CommentBodyHost.Visibility = Visibility.Collapsed;
		}
	}

	private async Task LoadCommentsAsync(object item, int token)
	{
		// 必须走 FetchPageAsync：这里以前用的是 FetchAsync，
		// 返回的只是第一页、没有把游标存进 _commentCursor。
		// 于是用户往下滑一下，ScrollChanged 就拿空游标又去拉「第一页」，
		// 一页页地往上叠 —— 表现就是评论还没滑到底就全涌出来了。
		OnlineComments.Page? page = await OnlineComments.FetchPageAsync(CommentSource(item), item, "");
		// 连续点两首歌时，先发的请求可能后回来，会把后点的歌的评论盖掉。
		// 令牌对不上就丢弃这份结果。
		if (token != _commentToken)
		{
			return;
		}
		CommentList.Children.Clear();
		if (page == null)
		{
			SetCommentStatus(Loc.Default["cm_fail"]);
			_commentMoreDone = true;
			return;
		}
		// 游标在这一步就存好，触底时才能接着往下翻而不是重新拉第一页
		_commentCursor = page.Next;
		_commentMoreDone = string.IsNullOrEmpty(page.Next);
		// 首屏就已经够长、根本滑不到底时，标记成到底，避免用户空滚一次
		if (page.Comments.Count == 0)
		{
			SetCommentStatus(Loc.Default["cm_none"]);
			CommentMoreHint.Visibility = Visibility.Collapsed;
			return;
		}
		// 不再显示评论条数
		SetCommentStatus("");
		CommentMoreHint.Text = _commentMoreDone ? Loc.Default["cm_no_more"] : Loc.Default["cm_more"];
		CommentMoreHint.Visibility = _commentMoreDone ? Visibility.Collapsed : Visibility.Visible;
		AppendComments(page.Comments);
		await Task.Yield();
		// 首屏渲染完再判断：内容一屏装得下就直接算到底，
		// 否则用户会看到「加载更多」提示但怎么滚都触发不了。
		if (!_commentMoreDone && CommentScroll.ScrollableHeight <= 0.0)
		{
			_commentMoreDone = true;
			CommentMoreHint.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>把评论渲染成条目追加到列表。首屏和「加载更多」共用。
	/// <paramref name="stagger"/> 大于 0 时逐条错峰淡入：一次只滚进来 5 条，
	/// 否则一屏 20 条同时淡入看起来像整体闪一下，反而没有「依次出现」的感觉。
	/// 「加载更多」传 0，不做动画——那时候用户已经在往下读了。</summary>
	private void AppendComments(List<OnlineComment> comments, bool stagger = true)
	{
		int index = 0;
		foreach (OnlineComment c in comments)
		{
			FrameworkElement row = BuildCommentRow(c, 0);
			CommentList.Children.Add(row);
			if (stagger && index < 5)
			{
				FadeInCommentRow(row, TimeSpan.FromMilliseconds(45 * index));
			}
			index++;
		}
	}

	/// <summary>单条评论的淡入 + 轻微上浮。延迟由调用方给，
	/// 这样 5 条之间的间隔是均匀的，不会因为加载耗时不同而挤在一起。</summary>
	private static void FadeInCommentRow(FrameworkElement row, TimeSpan delay)
	{
		try
		{
			row.RenderTransformOrigin = new Point(0.5, 1.0);
			var t = new TranslateTransform();
			row.RenderTransform = t;
			row.Opacity = 0;
			t.BeginAnimation(TranslateTransform.YProperty, null);
			row.BeginAnimation(UIElement.OpacityProperty, null);

			t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, new Duration(TimeSpan.FromMilliseconds(240)))
			{
				BeginTime = delay,
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			});
			row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(190)))
			{
				BeginTime = delay
			});
		}
		catch
		{
		}
	}

	/// <summary>构建一条评论。depth&gt;0 表示这是别人下面的子回复，
	/// 会缩进并加一条竖线，这样用户能一眼看出层级 ——
	/// 之前所有评论都是同一层平铺，看起来就像「一半评论都在回复某条评论」。</summary>
	private FrameworkElement BuildCommentRow(OnlineComment c, int depth)
	{
		bool nested = depth > 0;
		var stack = new StackPanel
		{
			Margin = new Thickness(nested ? 14 : 0, 0, 0, nested ? 8 : 12)
		};

		// 头部：昵称 · 时间 · 回复对象 · 点赞
		var head = new StackPanel { Orientation = Orientation.Horizontal };
		head.Children.Add(new TextBlock
		{
			Text = string.IsNullOrEmpty(c.Time) ? c.User : c.User + "  ·  " + c.Time,
			FontSize = nested ? 11.5 : 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Accent(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			MaxWidth = 150
		});
		if (c.IsReply && !string.IsNullOrEmpty(c.ReplyTo))
		{
			// 标出「回复 @某某」，原来完全没这个信息
			head.Children.Add(new TextBlock
			{
				Text = "  回复 " + c.ReplyTo,
				FontSize = 11,
				Foreground = ThemeManager.Secondary(),
				TextTrimming = TextTrimming.CharacterEllipsis,
				MaxWidth = 110
			});
		}
		if (c.Likes > 0)
		{
			// 点赞数。原来完全没有这个字段，评论看不出热度。
			head.Children.Add(new TextBlock
			{
				Text = "  ♥ " + FormatCount(c.Likes),
				FontSize = 11,
				Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
				VerticalAlignment = VerticalAlignment.Center
			});
		}
		stack.Children.Add(head);

		var body = new TextBlock
		{
			Text = c.Content,
			FontSize = 13,
			// 原来写死 0xE0E0E0 的浅灰，是照深色主题配的；
			// 浅色主题下就是白底浅灰字，看着就「糊」。
			Foreground = ThemeManager.Primary(),
			TextWrapping = TextWrapping.Wrap,
			LineHeight = 19,
			Margin = new Thickness(0, 4, 0, 0)
		};
		// 小字号正文走默认的 Ideal 平滑会发虚，Display 关掉字距微调后笔画更实
		TextOptions.SetTextFormattingMode(body, TextFormattingMode.Display);
		stack.Children.Add(body);

		// 接口内嵌带的子回复。缩进 + 左侧一条竖线标出层级。
		if (c.Replies.Count > 0)
		{
			var subBox = new StackPanel();
			foreach (OnlineComment sub in c.Replies)
			{
				subBox.Children.Add(BuildCommentRow(sub, depth + 1));
			}
			stack.Children.Add(new Border
			{
				Margin = new Thickness(8, 6, 0, 0),
				BorderBrush = ThemeManager.Secondary(),
				BorderThickness = new Thickness(2, 0, 0, 0),
				Padding = new Thickness(8, 0, 0, 0),
				Opacity = 0.92,
				Child = subBox
			});
		}
		else if (c.ReplyCount > 0 && !nested)
		{
			// 只知道有 N 条回复但接口这次没带回来
			stack.Children.Add(new TextBlock
			{
				Text = Loc.Default.Format("cm_reply_more", c.ReplyCount),
				FontSize = 11,
				Foreground = ThemeManager.Secondary(),
				Margin = new Thickness(8, 4, 0, 0)
			});
		}
		return stack;
	}

	/// <summary>1234 -&gt; 1234，12345 -&gt; 1.2万，和平台习惯一致。</summary>
	private static string FormatCount(long n)
	{
		if (n < 10000)
		{
			return n.ToString();
		}
		return (n / 10000.0).ToString("0.#") + "万";
	}

	/// <summary>统一的底部状态行写入。空串时把整行收起来，
	/// 这样状态行不会在没内容的时候白占 19px，列表能铺满卡片底部。</summary>
	private void SetStatus(string text)
	{
		// 普通状态（搜索结果为空、加载条数…）自己管生命周期。
		// 定时器只服务于「限次提示」那条路，所以先掐掉，
		// 否则上一条提示的定时器会把这一条提前抹掉。
		StopStatusTimer();
		StatusText.Text = text ?? "";
		StatusText.Visibility = string.IsNullOrWhiteSpace(StatusText.Text)
			? Visibility.Collapsed
			: Visibility.Visible;
	}

	private void StopStatusTimer()
	{
		if (_statusTimer != null)
		{
			_statusTimer.Stop();
			_statusTimer = null;
		}
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
			if (!string.IsNullOrWhiteSpace(StatusText.Text))
			{
				StatusText.Text = "";
				StatusText.Visibility = Visibility.Collapsed;
			}
		};
		_statusTimer = timer;
		timer.Start();
	}

	/// <summary>清空右侧评论区，回到「选一首歌」的空态。
	/// 栏本身不隐藏 —— 它是常驻的，隐藏就没地方可去了。</summary>
	private void ResetComments()
	{
		_commentToken++;
		CommentList.Children.Clear();
		CommentHeaderText.Text = Loc.Default["cm_title"];
		CommentLoginHint.Visibility = Visibility.Collapsed;
		SetCommentStatus("");
		CommentMoreHint.Visibility = Visibility.Collapsed;
		_commentCursor = "";
		_commentMoreDone = false;
		_commentTarget = null;
		CommentComposeBar.Visibility = Visibility.Collapsed;
		CommentInputBox.Text = "";
		CollapseCommentList();
	}

	
	// ---------- 发表评论 ----------

	/// <summary>只有「Cookie 有效 + 平台支持发表 + 接口可达 + 已拿到 csrf」时才放开输入框。
	/// 条件不满足一律 disabled，避免用户写完一堆字点发送才发现发不了。</summary>
	private void RefreshComposeState()
	{
		try
		{
			object? target = _commentTarget;
			if (target == null)
			{
				CommentComposeBar.Visibility = Visibility.Collapsed;
				return;
			}
			string source = CommentSource(target);
			bool supported = source == "bili" || source == "netease";
			CommentComposeBar.Visibility = Visibility.Visible;
			if (!supported)
			{
				CommentInputBox.IsEnabled = false;
				CommentSendButton.IsEnabled = false;
				CommentLoginHint.Text = GetCommentLoginHint(source);
				CommentLoginHint.Visibility = Visibility.Visible;
				CommentInputBox.ToolTip = GetCommentLoginHint(source);
				return;
			}
			CommentLoginHint.Text = OnlineComments.CanPost(source) ? Loc.Default["cm_login_ok"] : GetCommentLoginHint(source);
			CommentInputBox.IsEnabled = OnlineComments.CanPost(source);
			CommentInputBox.ToolTip = CommentInputBox.IsEnabled ? null : GetCommentLoginHint(source);
			UpdateSendEnabled();
		}
		catch
		{
			CommentInputBox.IsEnabled = false;
			CommentSendButton.IsEnabled = false;
		}
	}

	private void UpdateSendEnabled()
	{
		CommentSendButton.IsEnabled = CommentInputBox.IsEnabled
			&& !string.IsNullOrWhiteSpace(CommentInputBox.Text);
	}

	/// <summary>登录提示按平台分流。原来所有平台都用同一句
	/// 「评论需要登录」，用户看到 B 站歌下面提示「Cookie 已失效」根本对不上；
	/// 酷狗/QQ 根本不支持发表，也不该复用「登录即可发」的措辞。</summary>
	private static string GetCommentLoginHint(string source)
	{
		return source switch
		{
			"netease" => Loc.Default["cm_netease_login"],
			"bili" => Loc.Default["cm_bili_login"],
			"kugou" => Loc.Default["cm_kugou_note"],
			_ => Loc.Default["cm_hint_login"]
		};
	}

	private void CommentInputBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		UpdateSendEnabled();
	}

	private void CommentInputBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
		{
			e.Handled = true;
			CommentSendButton_Click(sender, new RoutedEventArgs());
		}
	}

	private async void CommentSendButton_Click(object sender, RoutedEventArgs e)
	{
		object? target = _commentTarget;
		string text = CommentInputBox.Text.Trim();
		if (target == null || text.Length == 0 || !CommentInputBox.IsEnabled)
		{
			return;
		}
		CommentInputBox.IsEnabled = false;
		CommentSendButton.IsEnabled = false;
		SetCommentStatus(Loc.Default["cm_posting"]);
		try
		{
			string err = await OnlineComments.PostAsync(CommentSource(target), target, text);
			if (err.Length > 0)
			{
				SetCommentStatus(Loc.Default["cm_post_fail"] + "  " + err);
			}
			else
			{
				SetCommentStatus(Loc.Default["cm_posted"]);
				CommentInputBox.Text = "";
				// 重新拉一遍，让新评论出现在列表里
				int token = ++_commentToken;
				await LoadCommentsAsync(target, token);
			}
		}
		catch (Exception ex)
		{
			SetCommentStatus(Loc.Default["cm_post_fail"]);
			LogHelper.LogError(ex, "发表评论失败");
		}
		finally
		{
			RefreshComposeState();
		}
	}
}
