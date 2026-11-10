using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicBox;
using MusicBox.Controls;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class MusicLibraryPage : UserControl, IMusicPage, IMusicPageLeave
{
	private bool _ready;

	private string _searchFilter = "";

	// 列表是不是已经过期了。之前每次切到本页都无条件重建全部行，
	// 而播放/收藏状态变化早就通过 StateChanged 重建过了 ——
	// 于是每次点导航栏回来，39 首歌的行控件都被拆了重造一遍，
	// 在快速切换时这段同步开销全砸在 UI 线程上，手感就是「卡一下」。
	// 真正改了东西才重建，没改就直接用现成的。
	private bool _needRefresh = true;

	// 上次重画列表时的数据指纹，见 OnStateChanged 的说明。
	private string _lastSignature = "";

	// —— 收藏夹（各平台账号歌单）——
	// 歌曲区当前显示什么："local" 本地曲库 / "all" 收藏夹全部歌曲 /
	// "playlist:平台:歌单id" 具体某个歌单。左栏点击决定，右栏跟着切。
	private string _viewMode = "local";

	// 当前展示的歌单名，只给计数文案用。
	private string _currentPlaylistName = "";

	// 本地路径行收放动画的代次。快速来回切视图时上一轮动画的 Completed
	// 还挂着 —— 靠代次把过期回调作废，新一轮从当前真实高度接着走，不会跳。
	// 分页页脚用同一套动画，各自有独立的代次字段。
	private int _folderBarAnimGen;
	private int _pagerAnimGen;

	// 最近一次拉到的平台分组（含每个平台的接入状态），左栏据此排版。
	private List<AccountPlaylistGroup>? _groups;

	// —— 收藏夹收缩栏（平台分组可折叠）——
	// 各平台组的行面板与箭头引用：折叠动画要拿它们，键 = 平台名。
	private sealed class PlaylistGroupUi
	{
		public string Platform = "";
		/// <summary>组根（组头 + 行面板）。吸顶量位置时量它：它自己不带变换，
		/// 直接量组头会把上一轮加的位移一并算进去，一轮轮地漂。</summary>
		public StackPanel Root = null!;
		public Border Header = null!;
		public StackPanel Rows = null!;
		/// <summary>组头的吸顶位移。只动它不改布局，滚动时不会触发重排。</summary>
		public TranslateTransform HeaderShift = null!;
		public RotateTransform Chevron = null!;
	}

	private readonly Dictionary<string, PlaylistGroupUi> _playlistGroupUis = new(StringComparer.Ordinal);

	/// <summary>分组在面板里的显示顺序。粘顶是「一个接一个往下叠」，
	/// 必须按视觉顺序遍历，字典的顺序不保证。</summary>
	private readonly List<PlaylistGroupUi> _playlistStickyOrder = new();

	// 折叠状态。只存内存（与收藏页分组折叠一致）：歌单列表本来就只拉一次，
	// 重启回默认展开没有任何代价，不值得为它写设置文件。
	private readonly HashSet<string> _collapsedPlaylistGroups = new(StringComparer.Ordinal);

	// 歌单「列表」本次运行是否已拉过：进页面自动拉一次（已登录时），
	// 之后不再自动轮询 —— 曲目必须用户点击才拉，列表也只允许手动点按钮重拉。
	private bool _playlistsLoaded;

	private bool _playlistsLoading;

	// 「全部歌曲」触发的后台批量加载进行中。期间每拉完一个歌单就增量刷新，
	// 防止重复触发两个批量任务交错打接口。
	private bool _batchRunning;

	// 在线视图需要重画（VIP 实测结论更新 / 强调色变化 / 关键词在离页时被清掉）。
	private bool _onlineStale;

	// 当前关键词命中的总数（含因渲染上限没画出来的部分）。
	// 增量追加时累加，整表重画时重算 —— 计数文案用它才不会随批量加载来回跳。
	private int _matchedTotal;

	// —— 歌曲区分页（每页 100 首，页脚固定在左栏收藏夹面板底部：「第一页/
	// 上一页/页码/下一页」，与在线音乐页同一套交互。本地曲库几千首、
	// 收藏夹歌单几千首时，一次切歌/一次状态广播要实例化的行控件从全量掉到
	// 100，这是「点播放/切歌 UI 卡一下」的兜底方案。）——
	private const int PageSize = 100;

	// 本地视图当前页（1 起）。
	private int _localPage = 1;

	// 在线视图当前页（1 起）。
	private int _onlinePage = 1;

	// 在线视图的源快照（过滤/视图决定的那份完整列表）：翻页、页脚和
	// 会话队列登记都用它，重画时重算。列表对象同时交给在线页登记会话，
	// 「下一首」顺这份完整列表走（跨页），不是只顺当前页。
	private List<object> _onlineSource = new();

	// 在线行的 ListBoxItem.Tag。本地行 Tag 是文件路径字符串，两边靠类型区分：
	// 右键菜单 / 双击 / 回车播放都先看是不是在线条目再走本地播放。
	private sealed class OnlineItem
	{
		public OnlineItem(object track) => Track = track;
		public object Track { get; }
	}

	// 左栏「本地音乐 / 全部歌曲」两个固定入口的副标题，曲目数变了要跟着刷新。
	private TextBlock? _localEntrySub;
	private TextBlock? _allEntrySub;

	// 时长探测广播的尾沿合并定时器：见 OnLocalMetaUpdated。
	private DispatcherTimer? _metaRefreshTimer;

	// 整页渲染代次：RenderOnlinePage 每次入口自增。VIP 结论这类异步回调
	// 攒下时记一份代次，真正执行时对不上（期间发生过整页渲染）就丢弃——
	// 整页渲染重建行时已经把当时的最新结论烘进控件里，过期回调碰旧 key
	// 只会白白扰动新列表。
	private int _renderGeneration;

	// VIP 结论广播的合并定时器 + 待更新 key 集（key → 攒下时的渲染代次）：
	// 探测一轮逐首广播结论（1550 首就是 1550 条），绝不能每条都整页重画，
	// 这里 400ms 固定节拍合并后做定向行更新，见 OnTrackAccessChanged / FlushTrackAccessUpdates。
	private DispatcherTimer? _accessRefreshTimer;
	private Dictionary<string, int>? _pendingAccessKeys;

	// 上次 VipProbe 排程键（视图+筛选词+页码）。同键重渲染不重排：
	// 以前每次渲染尾部都把全量探测 Schedule 一遍，generation++ 把上一轮
	// 扫掠作废重启，结论落地又触发下一次渲染+重排 —— 这就是日志里
	// 200ms 节拍整页重画循环的另一半。排程键只在换源/翻页时才该变。
	private string? _vipProbeKey;

	public MusicLibraryPage()
	{
		InitializeComponent();
		MusicService.StateChanged += OnStateChanged;
		TrackRows.AccentChanged += OnAccentChanged;
		// VIP 标签只信实测：某首歌真的播放被拒后 TrackAccess 广播结论，
		// 在线视图里的行要重画才补得上标签（和在线搜索页同一套联动）。
		TrackAccess.Changed += OnTrackAccessChanged;
		// 后台把某首歌的时长探出来之后通知过来，让屏幕上这一页补上时长。
		LocalTrackMeta.Updated += OnLocalMetaUpdated;
	}

	/// <summary>换强调色：行里抄的旧颜色要重画才能变过来。在线行（收藏夹）同样适用。</summary>
	private void OnAccentChanged()
	{
		_needRefresh = true;
		_onlineStale = true;
		if (IsLoaded)
		{
			if (_viewMode == "local")
			{
				RefreshLibraryList();
			}
			else if (IsOnScreen())
			{
				// 换强调色只是行配色变：页码和滚动位置都要原地保住。
				RefreshOnlineView(resetPage: false);
			}
		}
	}

	/// <summary>VIP 实测结论更新（某首歌真的播放被拒/成功过）。
	/// 这里只攒 key 不重画：VIP 探测开一轮会逐首广播结论（1550 首的歌单
	/// 就是 1550 条），以前每条都 RefreshOnlineView 整页重画，渲染尾部又把
	/// 探测重排一遍，形成 200ms 节拍的自激重画风暴（UI 未响应的主因）。
	/// 现在统一进 400ms 合并定时器做定向行更新（FlushTrackAccessUpdates）；
	/// 本地视图不受影响，直接跳过。</summary>
	private void OnTrackAccessChanged(string? key)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action<string?>(OnTrackAccessChanged), key);
			return;
		}
		if (_viewMode == "local" || !IsLoaded || string.IsNullOrEmpty(key))
		{
			return;
		}
		// 攒 key 时顺手记下当前渲染代次：执行阶段据此丢弃过期回调。
		(_pendingAccessKeys ??= new Dictionary<string, int>(StringComparer.Ordinal))[key] = _renderGeneration;
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
				FlushTrackAccessUpdates();
			};
			_accessRefreshTimer = timer;
		}
		_accessRefreshTimer.Start();
	}

	/// <summary>把攒下的 VIP 结论落到屏幕上：只重建受影响的那几行，
	/// 绝不整页重画。为什么固定节拍而不是尾沿合并：探测扫掠是连续事件流，
	/// 尾沿定时器会被一路推后、标签要等整轮探完才出现；400ms 节拍让标签
	/// 随扫掠渐进就位，一拍只动几个行控件（每行亚毫秒级），UI 无感。
	/// 找不到对应行的 key（结论属于别的页/别的视图）不重画但标脏：
	/// 行是构建那一刻读结论的，切回本页整页重画才能补上。</summary>
	private void FlushTrackAccessUpdates()
	{
		var pending = _pendingAccessKeys;
		_pendingAccessKeys = null;
		if (pending == null || pending.Count == 0)
		{
			return;
		}
		// 页面不在前台就别动行：反正看不见，切回来时 OnNavigatedTo 看脏标记补上。
		if (!IsOnScreen())
		{
			_onlineStale = true;
			return;
		}
		int gen = _renderGeneration;
		bool stale = false;
		foreach (var kv in pending)
		{
			// 代次守卫：这条 key 攒下之后发生过整页渲染（翻页/换源/兜底重画）
			// → 新行构建时已经读到了这条结论，过期回调直接丢弃。
			if (kv.Value != gen)
			{
				continue;
			}
			if (!ReplaceOnlineRow(kv.Key))
			{
				stale = true;
			}
		}
		if (stale)
		{
			_onlineStale = true;
		}
	}

	/// <summary>在当前列表里找 Tag 匹配 key 的在线行，原位重建一行。
	/// 返回 false = 列表里没有这行（结论属于别的页）。为什么原位替换而不是
	/// 整页重画：一条结论只影响一行，整页重建（清空+重挂上百个行容器+滚动
	/// 位置恢复+封面重淡入）就是重画风暴本身；替换出的行首帧会自己应用
	/// 播放高亮（TrackRows.BuildRow 内处理），选中态也手动接住，视觉无跳变。</summary>
	private bool ReplaceOnlineRow(string key)
	{
		for (int i = 0; i < LibraryListBox.Items.Count; i++)
		{
			if (LibraryListBox.Items[i] is FrameworkElement row
				&& row.Tag is object track
				&& track is not string
				&& string.Equals(TrackAccess.KeyOf(track), key, StringComparison.Ordinal))
			{
				bool wasSelected = ReferenceEquals(LibraryListBox.SelectedItem, row);
				var rebuilt = MakeOnlineItemRow(track, _searchFilter);
				LibraryListBox.Items[i] = rebuilt;
				if (wasSelected)
				{
					LibraryListBox.SelectedItem = rebuilt;
				}
				return true;
			}
		}
		return false;
	}

	public void OnNavigatedTo()
	{
		// _ready 以前只有「选择音乐文件夹」按钮里会置 true，
		// 于是正常启动进来它一直是 false，SearchBox_TextChanged 直接 return，
		// 搜索框看着能打字、其实什么都没发生。页面真正被显示出来就算初始化完成。
		_ready = true;
		RefreshFolderText();
		ApplyCollectionLocalization();
		// 左栏按当前状态排版（第一次进页面时构建，之后只刷新计数）。
		EnsurePlaylistPanel();
		// 打开本页就后台重扫一次：用户在文件资源管理器里改名 / 加歌 / 删歌，
		// 只有切回本页才会看到结果。监视器虽然也在跑，但它只看得到程序开着
		// 这段时间发生的事，网络盘 / 换机器 / 外部批量改名之后它一概不知。
		// 扫盘在后台线程 + 有代次保护，扫完只发一次 StateChanged，
		// 这一页 OnStateChanged 里靠指纹判断要不要真重画。
		MusicService.RefreshLibraryIfIdle();
		// 收藏夹歌单列表：本次运行进页面自动拉一次（任一平台登录过才拉），
		// 曲目必须点击才拉，这里拉的是「歌单名单」本身，请求很轻。
		AutoLoadPlaylistsOnce();
		if (_viewMode != "local")
		{
			// 在线视图：只有标记过期才重画（VIP 结论/强调色变化、离页清了关键词），
			// 否则保留现成的行，切回来不闪一下也不白费构建开销。
			// 页码保留：脏标记来自标签/配色刷新（不该翻页）；离页清筛选词
			// 那条路已经在 OnNavigatedFrom 里把页码重置过。
			if (_onlineStale && IsOnScreen())
			{
				_onlineStale = false;
				RefreshOnlineView(resetPage: false);
			}
			return;
		}
		// 只有列表真的过期了才重建。切走时清掉筛选词会置脏，所以这条路径照样会重画。
		if (_needRefresh)
		{
			RefreshLibraryList();
		}
	}

	/// <summary>切离本页时清空筛选词：行为固定为「不保留关键词」
	///（原先的「保留关键词」勾选框已删）—— 每次跳去别的页面再回来都从
	/// 全部歌曲开始，上次会话的关键词不再带入，免得「看着只有 3 首、
	/// 其实是筛过的」这种困惑。
	/// 这里只改状态，不重建列表 —— 重建要跑一遍全部歌曲，
	/// 而列表在切走时本来就会盖住，没必要白花这个开销。</summary>
	public void OnNavigatedFrom()
	{
		if (string.IsNullOrEmpty(_searchFilter) && string.IsNullOrEmpty(SearchBox.Text))
		{
			return;
		}
		_searchFilter = "";
		// 过滤是回车提交式的，改 Text 不会再顺带触发一次筛选
		//（TextChanged 只管清除按钮的显隐），直接清就行。
		SearchBox.Text = "";
		// 筛选变了，列表得重画。等回到本页时 OnNavigatedTo 会补上
		// （本地看 _needRefresh，收藏夹在线视图看 _onlineStale）。
		// 筛选词变化属于「换源」：在线视图的页码在这里一并归零，
		// OnNavigatedTo 那次重画才不用（也不能）再动页码。
		_needRefresh = true;
		_onlineStale = true;
		_onlinePage = 1;
	}

	/// <summary>筛选确认按钮：点它和按 Enter 走同一条路。
	/// 提交完把焦点还给输入框，方便紧接着改关键词再按 Enter。</summary>
	private void CommitFilterButton_Click(object sender, RoutedEventArgs e)
	{
		CommitSearchFilter();
		SearchBox.Focus();
	}

	private void OnStateChanged()
	{
		// 播放状态变了，但列表数据（曲库/收藏/跳过/筛选）未必变了。
		// 切一首歌 MusicService 会连着抛好几次 StateChanged（起播、缓冲、状态位），
		// 以前每一次都把 39 行拆了重造一遍 —— 全砸在 UI 线程上，
		// 这就是「换歌时程序未响应」的主因。
		// 行内的播放高亮由 TrackRows.SetPlayingTrack 单独更新，跟重建列表无关，
		// 所以这里只在列表数据真的变了时才重建。
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(OnStateChanged));
			return;
		}
		if (_viewMode != "local")
		{
			// 在线视图（收藏夹歌单 / 全部歌曲）不依赖本地曲库的签名：
			// 播放状态的行内高亮由 TrackRows 全局更新，这里重建反而会把
			// 滚动位置顶回去。本地曲库变化在切回 local 视图时自然可见。
			RecordLocateViewMemo();
			return;
		}
		RecordLocateViewMemo();
		if (ListDataSignature() == _lastSignature)
		{
			SyncPlayingSelection();
			return;
		}
		_needRefresh = true;
		// 页面不在前台就别重画：反正看不见，等切回来时 OnNavigatedTo 会补上。
		if (IsOnScreen())
		{
			RefreshLibraryList();
		}
	}

	// ── 定位球的音乐库恢复 ────────────────────────────
	//
	// 定位球单击只能跳到「音乐库」这一页，页里还分本地/全部歌曲/各歌单
	// 好几个视图，本地和在线视图又都分页 —— 正在播的那首不在当前视图当前页
	// 时，靠可视树找行必然落空（日志里「全部路径未命中」的根源）。
	// 所以播的歌都记一笔「从哪个视图播的」，定位时切回去、翻到所在页、
	// 把行滚进视口，再交给 MainWindow 居中+脉冲。

	/// <summary>播放曲目 Key → 播放时所在视图（local / all / playlist:平台:id）。</summary>
	private static readonly Dictionary<string, string> LocateViewMemos = new(StringComparer.OrdinalIgnoreCase);
	private string? _lastMemoedKey;

	/// <summary>定位球用：这首歌有没有在音乐库播过（即有没有视图记忆）。
	/// 静态字典所以实例还不存在也能查——MainWindow 先查它再决定要不要
	/// 导航到音乐库做恢复，避免白跳一页。</summary>
	public static bool HasLocateMemo(string? key)
		=> !string.IsNullOrEmpty(key) && LocateViewMemos.ContainsKey(key);

	/// <summary>有新歌开始播放且音乐库正被看着时，记下「它属于当前视图」。
	/// 不在前台不记 —— 那首歌是从别的页面播的，跟这里无关。</summary>
	private void RecordLocateViewMemo()
	{
		try
		{
			string? key = MusicService.BuildCurrentTrack()?.Key;
			if (key == null || key == _lastMemoedKey || !IsOnScreen())
			{
				return;
			}
			_lastMemoedKey = key;
			LocateViewMemos[key] = _viewMode;
		}
		catch
		{
			// 记忆失败不影响播放。
		}
	}

	/// <summary>定位球恢复入口：切回这首歌播放时的视图，翻到所在页，
	/// 把行滚进视口并返回行容器（MainWindow 拿去居中+脉冲）。找不到返回 null。</summary>
	public async Task<FrameworkElement?> RevealPlayingRowAsync()
	{
		var cur = MusicService.BuildCurrentTrack();
		string? key = cur?.Key;
		if (key == null)
		{
			return null;
		}
		// 1) 视图恢复：按记忆切回去（切视图方法自带「已在视图就不重建」短路）。
		if (LocateViewMemos.TryGetValue(key, out string? view))
		{
			if (view == "local")
			{
				SelectLocalMode();
			}
			else if (view == "all")
			{
				SelectAllSongsMode();
			}
			else if (view.StartsWith("playlist:", StringComparison.Ordinal))
			{
				AccountPlaylist? pl = FindPlaylistByViewKey(view);
				if (pl != null)
				{
					await OpenPlaylistAsync(pl);
				}
			}
		}
		else if (key.StartsWith("local:", StringComparison.OrdinalIgnoreCase) && _viewMode != "local")
		{
			// 没有记忆的本地歌（如启动恢复）：本地音乐视图一定装得下它。
			SelectLocalMode();
		}
		// 2) 视图就位后按当前视图翻页找行。
		return _viewMode == "local" ? RevealLocalRow(MusicService.CurrentPath) : RevealOnlineRow(key);
	}

	/// <summary>视图键（playlist:平台:id）反查左栏歌单条目。</summary>
	private AccountPlaylist? FindPlaylistByViewKey(string viewKey)
	{
		string rest = viewKey.Substring("playlist:".Length);
		int sep = rest.IndexOf(':');
		if (sep <= 0 || _groups == null)
		{
			return null;
		}
		string platform = rest.Substring(0, sep);
		string id = rest.Substring(sep + 1);
		return _groups.SelectMany(g => g.Playlists)
			.FirstOrDefault(p => p.Platform == platform && p.Id == id);
	}

	/// <summary>本地视图：按筛选+分页规则算出目标页翻过去，选中并滚进视口。</summary>
	private FrameworkElement? RevealLocalRow(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}
		string filter = _searchFilter;
		string filterKey = NormalizeKey(filter);
		var matched = new List<string>();
		foreach (string file in MusicService.KnownMusic)
		{
			if (filterKey.Length == 0 || Matches(Path.GetFileName(file), filter, filterKey))
			{
				matched.Add(file);
			}
		}
		int idx = matched.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
		if (idx < 0)
		{
			if (filter.Length == 0)
			{
				return null; // 本地曲库里没有这首歌
			}
			// 被筛选词挡住了：清掉重来一遍（定位优先于保留筛选）。
			_searchFilter = "";
			SearchBox.Text = "";
			return RevealLocalRow(path);
		}
		int page = idx / PageSize + 1;
		if (page != _localPage)
		{
			_localPage = page;
			RefreshLibraryList();
		}
		foreach (object o in LibraryListBox.Items)
		{
			if (o is ListBoxItem it && it.Tag is string p
				&& string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
			{
				LibraryListBox.SelectedItem = it;
				LibraryListBox.ScrollIntoView(it);
				LibraryListBox.UpdateLayout();
				return it.Content as FrameworkElement ?? it;
			}
		}
		return null;
	}

	/// <summary>在线视图（全部歌曲/歌单）：翻到目标页，滚进视口并返回行。</summary>
	private FrameworkElement? RevealOnlineRow(string key)
	{
		int idx = _onlineSource.FindIndex(t =>
			string.Equals(OnlineMusicPage.ToSavedTrack(t)?.Key, key, StringComparison.OrdinalIgnoreCase));
		if (idx < 0)
		{
			return null;
		}
		int page = idx / PageSize + 1;
		if (page != _onlinePage)
		{
			_onlinePage = page;
			RefreshOnlineView(resetPage: false);
		}
		foreach (object o in LibraryListBox.Items)
		{
			if (o is FrameworkElement fe && fe.Tag is object track && track is not string
				&& string.Equals(OnlineMusicPage.ToSavedTrack(track)?.Key, key, StringComparison.Ordinal))
			{
				LibraryListBox.SelectedItem = fe;
				LibraryListBox.ScrollIntoView(fe);
				LibraryListBox.UpdateLayout();
				return fe;
			}
		}
		return null;
	}

	/// <summary>后台补到了某首歌的时长。
	/// 事件来自后台线程，这里先切回 UI 线程再动控件。
	///
	/// 注意这里**不能**每条广播都立刻整页重画：LocalTrackMeta 是逐首探测的，
	/// 几百首的歌库探一轮会连着广播几十秒 —— 以前每 400ms 就 Items.Clear() 重画
	/// 一遍，屏幕上那页的封面跟着一次次重新淡入，看起来就是「封面连着闪」，
	/// 内存也跟着一路涨。改成尾沿合并：广播停 800ms 才真重建一次，
	/// 一轮探测收尾时补上全部时长，中间零抖动。</summary>
	private void OnLocalMetaUpdated()
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(OnLocalMetaUpdated));
			return;
		}
		_needRefresh = true;
		if (_viewMode != "local")
		{
			// 在线视图（收藏夹歌单/全部歌曲）的行副标题用的是平台数据里的时长
			//（OnlineMusicPage.SubLine），本地时长广播跟它毫无关系。
			// 不拦住的话 800ms 定时器到点会把本地列表整页盖到在线歌单上。
			return;
		}
		if (!IsLoaded || !IsOnScreen())
		{
			// 页面不在前台就别重画：反正看不见，等切回来时 OnNavigatedTo 会补上。
			return;
		}
		if (_metaRefreshTimer == null)
		{
			var timer = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(800)
			};
			timer.Tick += (_, _) =>
			{
				timer.Stop();
				_metaRefreshTimer = null;
				if (_needRefresh && IsOnScreen())
				{
					RefreshLibraryList();
				}
			};
			_metaRefreshTimer = timer;
		}
		// 尾沿合并：每条广播都把定时器重置，探测连环来的时候一次都不重建，
		// 停下来 800ms 后才统一补一次。
		_metaRefreshTimer.Stop();
		_metaRefreshTimer.Start();
	}

	/// <summary>决定列表要不要重画的那几个输入的指纹。
	/// 故意不含「当前播放哪首」—— 那一项只影响选中项和高亮，
	/// 用 SyncPlayingSelection 单独同步就够了，不需要重造所有行。</summary>
	private static string ListDataSignature()
	{
		var s = SettingsManager.Instance;
		int skipped = s.SkippedMusic?.Count ?? 0;
		// 这里只放「列表内容真的取决于它」的量。
		// 原来还带着 UserLibrary.RecentlyPlayed.Count，可这一页从头到尾
		// 没用过 RecentlyPlayed（列表只由 KnownMusic + 跳过 + 收藏决定），
		// 于是每播一首歌 RecentlyPlayed 就会变，签名跟着变，
		// 触发一次全量重建——为了一个页面上根本不显示的数据，把 39 行全造一遍。
		//
		// 指纹这一项是「在文件资源管理器里改名/换文件」能生效的关键：
		// 这两种操作都不改曲库条数，只比条数的话签名前后完全一样，
		// 页面认定「没变」就不重画，屏幕上留着的还是改名前的歌名。
		return string.Concat(
			MusicService.MusicFolder, "|",
			MusicService.KnownMusic.Count.ToString(), "|",
			MusicService.LibraryFingerprint().ToString(), "|",
			MusicService.LocalCoverVersion.ToString(), "|",
			UserLibrary.Favorites.Count.ToString(), "|",
			skipped.ToString());
	}

	/// <summary>页面是否正在前台显示。用主窗的当前页判断比 IsVisible 可靠：
	/// 窗口最小化时 IsVisible 是 false，但那一页其实就是当前页。</summary>
	private bool IsOnScreen()
	{
		return MainWindow.Instance?.IsCurrentPage(this) ?? false;
	}

	/// <summary>把选中项挪到正在播放的那首。只动选中，不重建任何行。</summary>
	private void SyncPlayingSelection()
	{
		string? current = MusicService.CurrentPath;
		foreach (object o in LibraryListBox.Items)
		{
			if (o is ListBoxItem item && item.Tag is string path
				&& string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
			{
				if (!item.IsSelected)
				{
					LibraryListBox.SelectedItem = item;
					LibraryListBox.ScrollIntoView(item);
				}
				return;
			}
		}
	}

	private void RefreshFolderText()
	{
		FolderText.Text = MusicService.MusicFolder;
	}

	/// <summary>FolderBar（本地文件夹路径行）随视图滑收/滑开。</summary>
	private void AnimateFolderBar(bool show)
	{
		AnimateSlideFade(FolderBar, show, () => _folderBarAnimGen, v => _folderBarAnimGen = v);
	}

	/// <summary>通用的 300ms 淡入淡出+滑动展开/收起动画（FolderBar 与分页页脚共用）。
	/// 约 300ms 高度滑动 + 淡入淡出（CubicEase EaseOut）。以前是 200ms，但列表
	/// 重建（行容器生成 + 封面调度）在动画起跑后马上占住 UI 线程，200ms 那点
	/// 帧全被吃掉，肉眼只剩「直接蹦出来」；加长到 300ms 且把起跑推迟到
	/// 布局渲染完的那一拍（见 show 分支），两头一起保住观感。
	/// 只动 MaxHeight 这个布局量，Visibility 留到收起动画收尾才切 Collapsed ——
	/// 直接切是瞬变看不出过渡，还会让外层留白跟着跳一下；反向展开前先从
	/// Collapsed 恢复参与布局，否则高度没地方算，动画无从谈起。收尾一律把
	/// 基值归位（MaxHeight 放开、Opacity 回 1），下一轮从干净状态起步。
	/// 起动画前把基值改成当前显示值（动画进行中读到的 ActualHeight / Opacity
	/// 就是动画里的当前值）：连点来回切时从收了一半的状态接着走，不会先跳回
	/// 满高再收。getGen/setGen 是调用方自己的动画代次字段（闭包读写），
	/// 新动画自增后，旧动画的 Completed 靠代次比对自动作废。
	/// onHidden：收起动画跑完后调用（如清空 Child），传 null 就只切 Collapsed。</summary>
	private void AnimateSlideFade(FrameworkElement bar, bool show, Func<int> getGen, Action<int> setGen, Action? onHidden = null)
	{
		int gen = getGen() + 1;
		setGen(gen);
		var dur = TimeSpan.FromMilliseconds(300);
		var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
		// 起新动画前摘掉旧的，并把基值改成当前显示值（动画进行中读到的
		// ActualHeight / Opacity 就是动画里的当前值）：连点来回切时
		// 从收了一半的状态接着走，不会先跳回满高再收。
		double from = bar.ActualHeight;
		double fromOpacity = bar.Opacity;
		bar.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
		bar.BeginAnimation(UIElement.OpacityProperty, null);
		bar.MaxHeight = double.PositiveInfinity;
		bar.Opacity = fromOpacity;
		// 只在动画期间开裁剪：滑动中子内容会超出布局边界，不裁会悬在半空。
		bar.ClipToBounds = true;
		if (show)
		{
			// 本来就开着的（重复点「本地音乐」入口），别铺一次闪一下。
			if (bar.Visibility == Visibility.Visible && from > 1)
			{
				bar.Opacity = 1;
				bar.ClipToBounds = false;
				return;
			}
			// 先恢复参与布局（Collapsed 不占布局、量不出自然高度），
			// 再放开上限量一次目标高度。
			bar.Visibility = Visibility.Visible;
			bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			double target = bar.DesiredSize.Height;
			if (target <= 1)
			{
				bar.Visibility = Visibility.Collapsed;
				bar.Opacity = 1;
				bar.ClipToBounds = false;
				return;
			}
			if (from > target)
			{
				from = target;
			}
			bar.MaxHeight = from;
			// 淡入从 0 起：完整收起后再展开时透明度基值是 1（上轮收尾重置的），
			// 不归零的话高度在滑、透明度却一直是 1，看不出「淡入」；
			// 从收了一半的状态接着展开则沿用当前透明度，不跳。
			double startOpacity = from <= 0 ? 0 : fromOpacity;
			bar.Opacity = startOpacity;
			var grow = new DoubleAnimation(from, target, dur) { EasingFunction = ease };
			var fadeIn = new DoubleAnimation(startOpacity, 1, dur) { EasingFunction = ease };
			grow.Completed += (_, _) =>
			{
				if (getGen() != gen)
				{
					return;
				}
				// 先摘动画再放开上限：两句同一帧里跑完，中间不会渲染出
				// 「回落到基值 from」的那一帧。
				bar.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
				bar.MaxHeight = double.PositiveInfinity;
				bar.BeginAnimation(UIElement.OpacityProperty, null);
				bar.Opacity = 1;
				bar.ClipToBounds = false;
			};
			// 起跑推迟到布局+渲染队列清完的那一拍：调用方（切视图/翻页）紧接着
			// 会同步重建整表行，那段 UI 线程全占着，动画头几帧必然被吃掉 ——
			// 推迟一拍起步，300ms 全程都在空闲帧里跑，看得见完整过渡。
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
			{
				// 推迟期间又被新一轮动画覆盖了就让位，别抢跑。
				if (getGen() != gen)
				{
					return;
				}
				bar.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);
				bar.BeginAnimation(UIElement.OpacityProperty, fadeIn);
			});
		}
		else
		{
			if (bar.Visibility != Visibility.Visible || from <= 1)
			{
				// 已经是收起状态：直接落定，别播一遍从 0 收到 0 的空动画。
				bar.Visibility = Visibility.Collapsed;
				bar.MaxHeight = double.PositiveInfinity;
				bar.Opacity = 1;
				bar.ClipToBounds = false;
				return;
			}
			bar.MaxHeight = from;
			var shrink = new DoubleAnimation(from, 0, dur) { EasingFunction = ease };
			var fadeOut = new DoubleAnimation(fromOpacity, 0, dur) { EasingFunction = ease };
			shrink.Completed += (_, _) =>
			{
				if (getGen() != gen)
				{
					return;
				}
				// 先切 Collapsed 再摘动画：Visibility 一关高度就是 0，
				// 摘动画回落到基值 from 也不会闪出来。
				bar.Visibility = Visibility.Collapsed;
				onHidden?.Invoke();
				bar.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
				bar.MaxHeight = double.PositiveInfinity;
				bar.BeginAnimation(UIElement.OpacityProperty, null);
				bar.Opacity = 1;
				bar.ClipToBounds = false;
			};
			// 和 show 分支同款：起跑推迟到布局+渲染队列清完的那一拍。
			// 收起同样发生在切视图前后，紧跟着的整表重建会占住 UI 线程，
			// 300ms 的收起帧被吃光就成了「直接消失」。
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
			{
				if (getGen() != gen)
				{
					return;
				}
				bar.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
				bar.BeginAnimation(UIElement.OpacityProperty, fadeOut);
			});
		}
	}

	private void RefreshLibraryList()
	{
		if (!_ready && !IsLoaded)
		{
			return;
		}
		_needRefresh = false;
		_lastSignature = ListDataSignature();
		UiPerf.Measure("音乐库列表重建", () =>
		{
		LibraryListBox.Items.Clear();
		string filter = _searchFilter;
		// 搜 "CU" 要能命中 "C U Again"：歌名里的空格、下划线、连字符、
		// 各种括号和点都是「装饰」，不该挡在匹配中间。
		// 所以两边都先压成一个只由字母数字和汉字组成的串再比。
		string filterKey = NormalizeKey(filter);
		// 先把命中的文件收齐再分页：筛选词变化时页码拉回第一页，
		// 免得停在第 3 页翻出一个空页。
		List<string> matched = new List<string>();
		foreach (string file in MusicService.KnownMusic)
		{
			string full = Path.GetFileName(file);
			if (filterKey.Length == 0 || Matches(full, filter, filterKey))
			{
				matched.Add(file);
			}
		}
		if (filter.Length > 0)
		{
			_localPage = 1;
		}
		int pages = Math.Max(1, (int)Math.Ceiling(matched.Count / (double)PageSize));
		_localPage = Math.Clamp(_localPage, 1, pages);
		int start = (_localPage - 1) * PageSize;
		int end = Math.Min(matched.Count, start + PageSize);
		for (int i = start; i < end; i++)
		{
			string file = matched[i];
			string name = Path.GetFileNameWithoutExtension(file);
			bool skipped = MusicService.IsSkipped(file);
			// 带上已匹配到的平台封面：行里直接显示，收藏存下来的条目也带着，
			// 下次进收藏不用等重新匹配。
			//
			// 作者也一并从那份匹配结果里取：本地文件的文件名里往往没有歌手，
			// 而匹配到的那一版是有的。取不到就留空 —— 让行按「只有歌名」
			// 的排版收着，绝不拿文件名去顶替（那就是「歌名显示两遍」的来源）。
			//
			// 时长优先用记录里的；没有的话 LocalTrackMeta 会在后台解码一次
			// 这个文件把总长补上，补到之后会广播一次让这一页重画。
			var saved = new SavedTrack
			{
				Type = "local",
				Title = name,
				Artist = MusicService.GetLocalMatchedArtist(file),
				Path = file,
				Cover = MusicService.GetLocalMatchedCover(file),
				DurationMs = LocalTrackMeta.GetDuration(file)
			};
			bool fav = UserLibrary.IsFavorite(saved);

			// 复用「最近播放 / 我的收藏」那套行控件和存法：
			// 都是 SavedTrack + TrackRows.BuildRow，Key 都是 "local:" + 路径，
			// 收藏判定、去重、心形状态自然就对得上了，不用再单独画一遍。
			string sub = skipped ? Loc.Default["lib_skipped"] : string.Empty;
			// 这里原来在行的「评论」位上挂了逐首歌词按钮（图标还专门改成歌词字形）。
			// 歌词现在是全局的桌面悬浮窗，跟着底部播放栏走就够了：
			// 每一行都挂一个按钮，39 首就是 39 个入口，用户根本不知道该点哪个，
			// 而真正想看歌词时人是坐在播放栏那儿的。
			Grid row = TrackRows.BuildRow(new TrackRows.TrackRowSpec
			{
				Track = saved,
				SubOverride = sub,
				OnPlay = () => PlayLocal(file),
				IsFavorite = fav,
				OnToggleFavorite = () =>
				{
					UserLibrary.ToggleFavorite(saved);
					RefreshLibraryList();
				},
				// 删除键只给本地文件。曲库里本来全是本地文件，但判定放在这一层
				// 比「反正都是本地」稳：以后混进网络盘的条目时不会凭空多出一个
				// 点了就报错盘的按钮。
				OnRemove = string.Equals(saved.Type, "local", StringComparison.Ordinal)
					? () => DeleteLocalTrack(file, name)
					: null,
				Highlight = filter.Length > 0 ? filter : null,
				OnRename = (newName) => RenameTrack(file, newName)
			});

			// 外面还是套 ListBoxItem：选中高亮、右键菜单、双击都靠它拿 Tag
			var item = new ListBoxItem
			{
				Tag = file,
				Content = row,
				Padding = new Thickness(0)
			};
			if (string.Equals(MusicService.CurrentPath, file, StringComparison.OrdinalIgnoreCase))
			{
				item.IsSelected = true;
			}
			LibraryListBox.Items.Add(item);
		}
		CountText.Text = filter.Length > 0
			? $"筛选出 {matched.Count} 首 · 共 {MusicService.KnownMusic.Count} 首"
			: $"音乐库 ({MusicService.KnownMusic.Count} 首)";
		if (matched.Count == 0 && filter.Length > 0)
		{
			LibraryListBox.Items.Add(new TextBlock
			{
				Text = Loc.Default["lib_none"],
				FontSize = 14,
				Foreground = ThemeManager.Tertiary(),
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 24, 0, 0)
			});
		}
		// 页脚：固定在左栏收藏夹面板底部（原先是追加在列表末尾的 ListBoxItem，
		// 列表一长就沉底看不见）。本地音乐 / 全部歌曲 / 歌单三个视图共用。
		UpdatePaginationFooter(pages, _localPage, GoToLocalPage);
		// 渲染完第 N 页后，把第 N+1 页前 5 首的封面提前拉进封面内存缓存
		// （只进缓存不显示）：点「下一页」时前几首的封面即现。
		PreloadLocalCovers(matched, end);
		});
	}

	private void GoToLocalPage(int page)
	{
		_localPage = page;
		RefreshLibraryList();
		ScrollLibraryToTop();
	}

	/// <summary>翻页（第一页/上一页/页码/下一页）后把歌曲列表滚回顶部。
	/// 行容器刚重建完、布局未必已经跑过，extent 还是旧值时 ScrollToTop 会
	/// 滚不动 —— 先等一帧（Loaded 优先级正好排在布局后面）再滚。</summary>
	private void ScrollLibraryToTop()
	{
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
		{
			if (FindDescendantScroll(LibraryListBox) is ScrollViewer sv)
			{
				sv.ScrollToTop();
			}
		});
	}

	private static ScrollViewer? FindDescendantScroll(DependencyObject root)
	{
		int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < n; i++)
		{
			var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
			if (child is ScrollViewer sv)
			{
				return sv;
			}
			ScrollViewer? deep = FindDescendantScroll(child);
			if (deep != null)
			{
				return deep;
			}
		}
		return null;
	}

	/// <summary>本地视图的下一页封面预取：把 [start, start+5) 的封面拉进缓存
	/// （只进缓存不显示）。封面来自本地匹配记录，多为网络图，正好交给
	/// CoverLoader.PreloadAsync；本地路径/空值由 PreloadAsync 自己跳过。</summary>
	private static void PreloadLocalCovers(List<string> files, int start)
	{
		int end = Math.Min(files.Count, start + 5);
		for (int i = Math.Max(0, start); i < end; i++)
		{
			_ = CoverLoader.PreloadAsync(MusicService.GetLocalMatchedCover(files[i]));
		}
	}

	/// <summary>在线视图的对应预取：封面取 SavedTrack.Cover，同样只进缓存。</summary>
	private static void PreloadOnlineCovers(List<object> source, int start)
	{
		int end = Math.Min(source.Count, start + 5);
		for (int i = Math.Max(0, start); i < end; i++)
		{
			_ = CoverLoader.PreloadAsync(OnlineMusicPage.ToSavedTrack(source[i])?.Cover);
		}
	}

/// <summary>删一首本地音乐：弹确认（可勾「不再提醒」）、删文件、清收藏记录。</summary>
	private void DeleteLocalTrack(string path, string title)
	{
		if (!TrackDelete.RequestDelete(GetWindow(this), path, title))
		{
			return;
		}
		_needRefresh = true;
		RefreshLibraryList();
	}

	/// <summary>双击改名：MusicService.RenameFile 负责搬文件、同步下载缓存与
	/// 正在播放的路径、重扫曲库；UserLibrary 同步收藏/最近播放里的路径与标题。</summary>
	private void RenameTrack(string path, string newName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(newName))
			{
				return;
			}
			string? created = MusicService.RenameFile(path, newName);
			string clean = MusicService.CleanFileName(newName);
			if (string.IsNullOrEmpty(created) || clean.Length == 0)
			{
				Toast("重命名失败：名称无效或目标已存在");
				return;
			}
			UserLibrary.ApplyLocalRename(path, created, clean);
			Toast("已重命名为 " + clean);
			_needRefresh = true;
			RefreshLibraryList();
		}
		catch (Exception ex)
		{
			Toast("重命名失败");
			LogHelper.LogError(ex, "音乐库重命名失败: " + newName);
		}
	}

	private static void Toast(string msg)
	{
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(msg, false);
		}
	}

	private static Window? GetWindow(DependencyObject o) => Window.GetWindow(o);

/// <summary>把字符串压成「只由字母数字和汉字组成」的匹配键。
	/// "C U Again (feat. X)" -> "CUAGAINFEATX"，
	/// 于是搜 "cuag" 能命中、搜 "cu" 也能命中。</summary>
	private static string NormalizeKey(string text) => TrackRows.NormalizeKey(text);

	/// <summary>关键词是否命中这个文件名。
	/// 两条路都算命中：
	/// 1) 原样包含 —— 保留原来「搜 flac 能搜到 .flac」的行为；
	/// 2) 压掉分隔符后包含 —— 让 "cu" 能命中 "C U Again"。
	/// 第 2 条在原样匹配失败时才试，所以不会把行为弄反。</summary>
	private static bool Matches(string fileName, string filter, string filterKey)
		=> TrackRows.MatchesText(fileName, filter, filterKey);

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		// 只同步清除按钮的显隐。过滤改成按 Enter 提交（见 SearchBox_KeyDown）：
		// 索引会随后台批量加载持续变大，输入即筛每敲一键都要全量过滤一遍 +
		// 增量追加行，快速打字时白算好几轮；提交式时机让打字路径零开销。
		ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>统一筛选入口，按歌曲区当前视图分流：
	/// 本地曲库走老的整表重建，收藏夹在线视图走索引过滤（增量搜索）。
	/// 触发时机只有两处：搜索框里按了 Enter（提交关键词），
	/// 以及左栏切换视图（本地 / 全部歌曲 / 某个歌单）。</summary>
	private void ApplySearch()
	{
		if (_viewMode == "local")
		{
			_needRefresh = true;
			RefreshLibraryList();
			if (_searchFilter.Length > 0)
			{
				// 只在筛选时选中第一条：没筛选词时列表是「全部歌曲」，
				// 此时强行选中会把用户自己选的位置顶掉。
				SelectFirstMatch();
				TrackRows.AnimateFilteredIn(LibraryListBox);
			}
			return;
		}
		_onlineStale = false;
		RefreshOnlineView();
	}

	/// <summary>筛选完把第一条选中。
	///
	/// 为什么自动选：搜完列表前面那几条正好在视线里，不选中的话
	/// 「空格播第一条」就没有明确对象，用户得先用鼠标点一下才敢播。
	/// 正好赶在播的那首被选中时不动手——那是用户自己选的位置，别覆盖。</summary>
	private void SelectFirstMatch()
	{
		if (LibraryListBox.Items.Count == 0)
		{
			return;
		}
		// RefreshLibraryList 已经把正在播放的那首选上了，那就不动：
		// 那是用户自己的选择位置，别用「第一条」顶掉。
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string sel }
			&& string.Equals(sel, MusicService.CurrentPath, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (LibraryListBox.Items[0] is ListBoxItem first)
		{
			LibraryListBox.SelectedItem = first;
			LibraryListBox.ScrollIntoView(first);
		}
	}

	/// <summary>搜索框键位：Enter = 提交过滤（回车从「播第一条」改成了「执行筛选」），
	/// Esc = 清空输入框并把列表复位成全量，空格保留旧行为 = 播放选中行：
	/// 列表滚动时拇指正好压在这片区域，手不用挪到键盘就能把选中的那条放出来；
	/// 空格必须吃掉（Handled），否则会一边播一边把空格打进框里。</summary>
	private void SearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			case Key.Enter:
				e.Handled = true;
				CommitSearchFilter();
				break;
			case Key.Escape:
				e.Handled = true;
				ResetSearchFilter();
				break;
			case Key.Space:
				e.Handled = true;
				PlaySelectedItem();
				break;
		}
	}

	/// <summary>把搜索框当前内容提交为过滤词（Enter 触发）。筛选本体不动：
	/// 本地走整表重建、在线走 SearchIndex，页码拉回第一页、增量渲染都在原路径里。
	/// 与上次相同的词直接跳过 —— 回车连按不该把整表行容器拆了重造一遍；
	/// 切换视图的路径本来就会带上已提交的词重画，这里不需要再补一遍。</summary>
	private void CommitSearchFilter()
	{
		string keyword = SearchBox.Text?.Trim() ?? "";
		if (keyword == _searchFilter)
		{
			return;
		}
		_searchFilter = keyword;
		ApplySearch();
	}

	/// <summary>Esc / 清除按钮：清空输入框并把过滤词复位，列表回到全量。
	/// 过滤改成提交式之后，清掉文本不再会顺带触发一次筛选
	///（TextChanged 只管按钮显隐），所以复位必须在这里显式做，
	/// 否则框已经空了、列表却还停在筛选结果上。</summary>
	private void ResetSearchFilter()
	{
		if (string.IsNullOrEmpty(SearchBox.Text) && _searchFilter.Length == 0)
		{
			return;
		}
		SearchBox.Text = "";
		if (_searchFilter.Length == 0)
		{
			// 框里有字但还没按过回车：过滤从未生效，列表本来就是全量，
			// 只清输入框就够，不用白重画一遍。
			return;
		}
		_searchFilter = "";
		ApplySearch();
	}

	private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.OpenFolderDialog
		{
			Title = "选择音乐文件夹",
			InitialDirectory = Directory.Exists(MusicService.MusicFolder)
				? MusicService.MusicFolder
				: Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
		};
		if (dialog.ShowDialog() == true)
		{
			var s = SettingsManager.Instance;
			s.MusicFolder = dialog.FolderName;
			SettingsManager.Save(s);
			_ready = true;
			RefreshFolderText();
			MusicService.ReloadLibrary();
		}
	}

	private void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		MusicService.ReloadLibrary();
	}

	private void LibraryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		// 在线行（收藏夹）的双击由 TrackRows.BuildRow 行内自己挂的 onPlay 处理，
		// 这里的模式匹配对 OnlineItem 天然落空，不会触发第二次播放；只兜底本地路径行。
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			PlayLocal(path);
		}
	}

	private void LibraryListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
	{
		// 右键菜单里的「跳过 / 删除文件」只对本地文件有意义；
		// 选中的是在线条目（收藏夹）时把整个菜单压掉，免得点了报错。
		if (LibraryListBox.SelectedItem == null || LibraryListBox.SelectedItem is ListBoxItem { Tag: OnlineItem })
		{
			e.Handled = true;
		}
	}

	private void ContextPlay_Click(object sender, RoutedEventArgs e)
	{
		PlaySelectedItem();
	}

	private void ContextToggleSkip_Click(object sender, RoutedEventArgs e)
	{
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			MusicService.ToggleSkip(path);
		}
	}

	private void ContextDelete_Click(object sender, RoutedEventArgs e)
	{
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			DeleteLocalTrack(path, Path.GetFileNameWithoutExtension(path));
		}
	}

	/// <summary>本地行点播的统一入口：先登记播放上下文（「启动时自动播放」要靠它
	/// 找回「上次在本地音乐库听过歌」这个事实），再走播放链。行按钮、双击、
	/// 右键「播放」三处共用。</summary>
	private static void PlayLocal(string file)
	{
		MusicService.PendingPlayContext = new PlayContext { Page = "library", ListKey = "local" };
		MusicService.Play(file);
	}

	/// <summary>播放当前选中的行。本地行 Tag 是文件路径 → MusicService.Play；
	/// 在线行（收藏夹）Tag 是 OnlineItem → 转发给在线页的 PlayExternal，
	/// 播放/下载/VIP 实测与在线搜索结果是同一条链路。</summary>
	private void PlaySelectedItem()
	{
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: OnlineItem online })
		{
			var omp = MainWindow.Instance?.EnsureOnlinePage();
			if (omp != null)
			{
				// 受限提示不用在这里抢着弹：在线页 PlayExternal 的受限分支
				// 和播放失败兜底会统一弹全局 Toast（原先这里先弹一句、
				// 在线页再弹一句，会连着出现两条一样的提示）。
				omp.PlayExternal(online.Track);
			}
			return;
		}
		if (LibraryListBox.SelectedItem is ListBoxItem { Tag: string path })
		{
			PlayLocal(path);
		}
	}

	private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
	{
		// 先把焦点还给输入框：按钮拿走焦点之后 TextBox 会立刻变灰，
		// 那个还没点到的清除图标就会跟着灰掉，看着像禁用。
		SearchBox.Focus();
		// 清除 = Esc 同款语义（清文本 + 复位过滤回到全量）。过滤是提交式的，
		// 光清文本不会触发筛选，必须显式复位；ResetSearchFilter 自带空态短路。
		ResetSearchFilter();
	}

	// ══════════════════════════════ 收藏夹（各平台账号歌单）═════════════════════════════
	//
	// 数据都在 AccountLibraryService：平台分组列表、单曲目的内存缓存（键=平台:歌单id）、
	// 增量搜索索引。这一页只负责排版、点击和把行画出来 —— 行本身走
	// OnlineMusicPage.BuildLibraryRow（同一份 BuildEngineRowCore），
	// 所以播放/下载/VIP 实测标签与在线搜索结果完全一致。
	//
	// 节流原则（任务硬性要求）：进页面最多自动拉一次「歌单列表」，
	// 曲目必须用户点击歌单 / 点「全部歌曲」才拉，绝不轮询。

	/// <summary>XAML 里的中文文案在运行时按当前语言覆盖。
	/// 没走 x:Static 是因为切换语言不走重载页面，运行时改一次就够。</summary>
	private void ApplyCollectionLocalization()
	{
		PlaylistPanelTitle.Text = Loc.Default["lib_pl_title"];
		LoadPlaylistsButton.Content = Loc.Default["lib_pl_load"];
	}

	private void LoadPlaylistsButton_Click(object sender, RoutedEventArgs e)
	{
		// 手动点击总是重新拉一遍：歌单列表是轻接口，用户点按钮就是想刷新。
		_ = LoadPlaylistsAsync(auto: false);
	}

	/// <summary>进页面自动拉一次歌单列表。任一平台有 Cookie 才发请求；
	/// 全没登录时连列表都不拉，左栏保持「未登录」占位。
	/// 触发与跳过都留了日志：用户反馈「收藏夹没加载出歌单」时，
	/// 日志能直接分辨是「没触发」还是「触发了但拉空/失败」。</summary>
	private void AutoLoadPlaylistsOnce()
	{
		if (_playlistsLoaded || _playlistsLoading)
		{
			LogHelper.Log("收藏夹: 自动加载跳过（已加载或正在加载）");
			return;
		}
		var s = SettingsManager.Instance;
		bool netease = !string.IsNullOrWhiteSpace(s.NeteaseCookie);
		bool qq = !string.IsNullOrWhiteSpace(s.QqCookie);
		bool kugou = !string.IsNullOrWhiteSpace(s.KugouCookie);
		bool soda = !string.IsNullOrWhiteSpace(s.SodaCookie);
		if (!netease && !qq && !kugou && !soda)
		{
			// 一个平台的 Cookie 都没有：不发请求。左栏占位在 EnsurePlaylistPanel
			// 的构建里已经按「无 Cookie → 未登录」落好了（不再挂着
			// 「正在加载」），按钮仍可手动重试，等存了 Cookie 重进页面自动拉。
			LogHelper.Log("收藏夹: 自动加载跳过（四个平台均无 Cookie）");
			BuildPlaylistPanel();
			return;
		}
		LogHelper.Log($"收藏夹: 进页面自动拉取歌单列表（netease={netease} qq={qq} kugou={kugou} soda={soda}）");
		_ = LoadPlaylistsAsync(auto: true);
	}

	private async Task LoadPlaylistsAsync(bool auto)
	{
		if (_playlistsLoading)
		{
			return;
		}
		_playlistsLoading = true;
		try
		{
			// 拉取期间左栏给个进行中提示（没有转圈控件，文字最省事也最不容易过时）。
			SetPlaylistPanelHint(Loc.Default["lib_pl_loading"]);
			var groups = await AccountLibraryService.FetchAllPlaylistsAsync();
			_groups = SortGroupsBySavedOrder(groups);
			_playlistsLoaded = true;
			BuildPlaylistPanel();
			// 账号名补拉跟着歌单列表这一趟走（设置里已有就不发请求），
			// 拉到了会重建面板把昵称补到收缩栏标题行。
			_ = RefreshAccountNamesAsync();
			int total = groups.Sum(g => g.Playlists.Count);
			LogHelper.Log($"收藏夹: 歌单列表加载完成，共 {total} 个歌单 / {groups.Count} 个平台分组（" +
				string.Join(", ", groups.Select(g => $"{g.Platform}:{g.Status}:{g.Playlists.Count}")) + "）");
			if (total > 0)
			{
				// 自动拉取不弹 Toast（页面刚进来弹个「已加载」很吵）；
				// 手动点按钮才给确认。
				if (!auto)
				{
					Toast(Loc.Default.Format("lib_pl_loaded_list", total));
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "音乐库收藏夹: 歌单列表加载失败");
			Toast(Loc.Default["lib_pl_fail_net"], false);
		}
		finally
		{
			_playlistsLoading = false;
		}
	}

	/// <summary>平台分组排序。用户手动拖动过（LibraryPlatformOrderIsManual）
	/// 以落盘顺序为准，没排进列表的平台按歌单数升序跟在后面；没拖动过时
	/// 初始顺序就是「收藏夹从少到多」（数量升序，稳定排序保持同数的先后）。
	/// 设置里那份没打手动标记的旧顺序列表（旧版按账号加载先后自动写入的）
	/// 一律不当作排序依据。</summary>
	private List<AccountPlaylistGroup> SortGroupsBySavedOrder(List<AccountPlaylistGroup> groups)
	{
		try
		{
			var s = SettingsManager.Instance;
			if (!s.LibraryPlatformOrderIsManual)
			{
				return groups.OrderBy(g => g.Playlists.Count).ToList();
			}
			var order = s.LibraryPlatformOrder;
			if (order == null || order.Count == 0)
			{
				return groups.OrderBy(g => g.Playlists.Count).ToList();
			}
			int Rank(AccountPlaylistGroup g) => order.IndexOf(g.Platform) is int i and >= 0 ? i : int.MaxValue;
			// 排过序的按列表顺序排前面；没进列表的（新平台）按数量升序接在后面。
			return groups.OrderBy(Rank).ThenBy(g => g.Playlists.Count).ToList();
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("收藏夹平台排序失败，按默认顺序: " + ex.Message);
			return groups;
		}
	}

	/// <summary>左栏只放一条灰字提示（加载中 / 没登录等）。会先清空已有内容，
	/// 所以只在没有真实分组数据时调用。</summary>
	private void SetPlaylistPanelHint(string text)
	{
		PlaylistHost.Children.Clear();
		_localEntrySub = null;
		_allEntrySub = null;
		PlaylistHost.Children.Add(new TextBlock
		{
			Text = text,
			FontSize = 12,
			TextWrapping = TextWrapping.Wrap,
			Foreground = ThemeManager.Tertiary(),
			Margin = new Thickness(2, 6, 2, 6)
		});
	}

	/// <summary>左栏没排过版就排一次；排过了只刷「本地音乐 / 全部歌曲」的计数。
	/// 整体重建只发生在歌单列表（重新）加载完成时。</summary>
	private void EnsurePlaylistPanel()
	{
		if (PlaylistHost.Children.Count == 0 || _localEntrySub == null || _allEntrySub == null)
		{
			BuildPlaylistPanel();
			return;
		}
		UpdatePlaylistEntryCounts();
	}

	private void UpdatePlaylistEntryCounts()
	{
		if (_localEntrySub != null)
		{
			_localEntrySub.Text = Loc.Default.Format("acct_pl_count", MusicService.KnownMusic.Count);
		}
		if (_allEntrySub != null)
		{
			_allEntrySub.Text = Loc.Default.Format("acct_pl_count", AccountLibraryService.IndexCount);
		}
	}

	/// <summary>排左栏：两个固定入口（本地音乐 / 全部歌曲）+ 各平台歌单收缩栏。
	/// 有歌单的平台各成一条可折叠分组：标题行 = 平台名 + 账号名 + 收藏夹数量 +
	/// 箭头（点整条收起/展开，动效与收藏页分组头一致）；登录/网络等占位状态
	/// 统一收成一行灰字（同一种状态只出现一次），不再按平台逐条罗列。</summary>
	private void BuildPlaylistPanel()
	{
		PlaylistHost.Children.Clear();
		_localEntrySub = null;
		_allEntrySub = null;
		// 旧组面板全部随面板清掉：引用表也要跟着清，不然折叠会摸到已离树的行。
		_playlistGroupUis.Clear();
		_playlistStickyOrder.Clear();
		// 裁剪状态按元素记，旧元素已经离树，留着只会白占内存。
		_playlistClipStates.Clear();

		// —— 本地音乐入口：回到本地曲库视图（右栏默认状态）——
		PlaylistHost.Children.Add(MakeLibraryEntry(
			"\uE8D6", Loc.Default["lib_pl_local"],
			Loc.Default.Format("acct_pl_count", MusicService.KnownMusic.Count),
			out var localSub, SelectLocalMode));
		_localEntrySub = localSub;

		// —— 全部歌曲入口：合并去重展示索引里所有歌，点击同时触发后台批量加载 ——
		PlaylistHost.Children.Add(MakeLibraryEntry(
			"\uE8FD", Loc.Default["lib_pl_all"],
			Loc.Default.Format("acct_pl_count", AccountLibraryService.IndexCount),
			out var allSub, SelectAllSongsMode));
		_allEntrySub = allSub;

		if (_groups == null)
		{
			// 还没拉过列表：占位文案看登录状态 —— 一个平台都没登录时
			// 永远不会有结果，就别挂「正在加载」骗人；有登录才说「正在加载」，
			// 避免加载失败/没触发时占位文字永远停在加载中（用户报障：
			// 「收藏夹没加载出歌单」多半就是停在这句上）。
			bool anyCookie = !string.IsNullOrWhiteSpace(SettingsManager.Instance.NeteaseCookie)
				|| !string.IsNullOrWhiteSpace(SettingsManager.Instance.QqCookie)
				|| !string.IsNullOrWhiteSpace(SettingsManager.Instance.KugouCookie)
				|| !string.IsNullOrWhiteSpace(SettingsManager.Instance.SodaCookie);
			PlaylistHost.Children.Add(new TextBlock
			{
				Text = Loc.Default[anyCookie ? "lib_pl_loading" : "lib_pl_not_logged"],
				FontSize = 12,
				TextWrapping = TextWrapping.Wrap,
				Foreground = ThemeManager.Tertiary(),
				Margin = new Thickness(2, 10, 2, 0)
			});
			return;
		}

		// 失败/占位原因去重收集：四个平台各来一句「未登录」就成了复读机，
		// 同一种状态一行就够。
		var reasons = new List<string>();
		foreach (var g in _groups)
		{
			if (g.Status == AccountPlaylistStatus.Ok && g.Playlists.Count > 0)
			{
				// —— 收缩栏：标题行 = 平台名 + 账号名 + 收藏夹数量 + 箭头 ——
				// 折叠交互与收藏页分组头同一套（MaxHeight 动画 + 箭头转向）。
				// 账号名读设置（歌单加载完成后异步补拉一次并重建面板补显）。
				bool expanded = !_collapsedPlaylistGroups.Contains(g.Platform);
				var rows = new StackPanel();
				foreach (var pl in g.Playlists)
				{
					// 歌单行复用账号页那份构建（同一种底色、悬停、封面占位）：
					// 名字 + 「N 首」。曲目数是列表接口报的，点进去后以拉到的真实条数为准。
					// Platform 保留在 pl 对象里，OpenPlaylistAsync 按它路由请求。
					// 行装进组面板（不直接进 PlaylistHost）：折叠时整组一起收。
					rows.Children.Add(AccountPage.BuildPlaylistRow(
						pl.Name,
						Loc.Default.Format("acct_pl_count", pl.TrackCount),
						pl.CoverUrl,
						() => _ = OpenPlaylistAsync(pl),
						pl.Platform,
						transparent: true));
				}

				var chevron = TrackRows.MakeChevron(expanded);
				var chevronRot = (RotateTransform)chevron.RenderTransform!;

				(string label, System.Windows.Media.Brush brush)? tag = PlatformTag(g.Platform);
				Border chip = TrackRows.MakeChip(tag?.label ?? g.Platform, tag?.brush ?? ThemeManager.Accent());

				string nick = AccountLibraryService.AccountNameOf(g.Platform);
				var nameText = new TextBlock
				{
					Text = nick,
					FontSize = 11.5,
					Margin = new Thickness(2, 0, 0, 0),
					VerticalAlignment = VerticalAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis,
					// 昵称没拉到过就整块收起：留个空位只会让标题行看着缺了半截。
					Visibility = nick.Length > 0 ? Visibility.Visible : Visibility.Collapsed
				};
				nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");

				var countText = new TextBlock
				{
					Text = Loc.Default.Format("lib_pl_group_count", g.Playlists.Count),
					FontSize = 11.5,
					// 昵称和个数是两个相邻的 Auto 列，中间不留缝的话
					// 「Notel_doge」和「5 个歌单」会挤成「Notel_doge5 个歌单」。
					// 缝留在这列自己的左边距上：昵称收起时也只是离平台标签远 6px。
					Margin = new Thickness(6, 0, 0, 0),
					VerticalAlignment = VerticalAlignment.Center
				};
				countText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");

				var headGrid = new Grid();
				headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				Grid.SetColumn(chip, 0);
				Grid.SetColumn(nameText, 1);
				Grid.SetColumn(countText, 2);
				Grid.SetColumn(chevron, 3);
				headGrid.Children.Add(chip);
				headGrid.Children.Add(nameText);
				headGrid.Children.Add(countText);
				headGrid.Children.Add(chevron);

				var header = new Border
				{
					Child = headGrid,
					Padding = new Thickness(6, 6, 6, 6),
					CornerRadius = new CornerRadius(6),
					Cursor = Cursors.Hand,
					Background = Brushes.Transparent,
					Margin = new Thickness(0, 8, 0, 2),
					// 悬停才提示「整条可点」，静止时保持素净（与收藏页组头一致）。
					ToolTip = Loc.Default["act_toggle_section"]
				};
				// 吸顶位移挂上去：只改 RenderTransform，滚动时不触发重排。
				var headerShift = new TranslateTransform(0, 0);
				header.RenderTransform = headerShift;
				string platform = g.Platform;
				header.MouseEnter += (_, _) => header.SetResourceReference(Border.BackgroundProperty, "SubtleFillHoverBrush");
				header.MouseLeave += (_, _) => header.Background = Brushes.Transparent;
				// 点击 = 折叠/展开；按住拖动 = 排序。按下只记点位并抓获鼠标，
				// 移动超出系统拖拽阈值才进拖动态（拖到哪组中线就实时换位），
				// 松手落盘；没超过阈值就按普通点击处理。
				header.MouseLeftButtonDown += (_, e) => BeginGroupDrag(platform, header, e);
				header.MouseMove += (_, e) => GroupDragMove(e);
				header.MouseLeftButtonUp += (_, e) => EndGroupDrag(platform, header, e);
				header.LostMouseCapture += (_, _) => CancelGroupDrag(platform, header);

				// 组头压在行面板上面：吸顶时它是浮着的，行从它身下滚过去。
				Panel.SetZIndex(header, 1);
				var root = new StackPanel();
				root.Children.Add(header);
				root.Children.Add(rows);

				PlaylistHost.Children.Add(root);

				var ui = new PlaylistGroupUi
				{
					Platform = platform,
					Root = root,
					Header = header,
					Rows = rows,
					HeaderShift = headerShift,
					Chevron = chevronRot
				};
				_playlistGroupUis[platform] = ui;
				_playlistStickyOrder.Add(ui);
				// 重建面板时按记忆的折叠状态静态收起（不播动画）：整栏重排时
				// 每个折叠组各播一遍动画会显得乱。
				if (!expanded)
				{
					rows.Visibility = Visibility.Collapsed;
				}
				continue;
			}

			// 占位/失败原因：一律不带平台名（用户要求列表里不出现平台标识）。
			// QQ 的微信扫码会话（Cookie 里没有 uin）与其它平台的登录过期
			// 在这里合并成同一句通用提示，具体话术保留在打开歌单失败时的 Toast 里。
			string reason = g.Status switch
			{
				AccountPlaylistStatus.NotLoggedIn => Loc.Default["lib_pl_not_logged"],
				AccountPlaylistStatus.CookieInvalid => Loc.Default["lib_pl_fail_cookie_generic"],
				AccountPlaylistStatus.NetworkError => Loc.Default["lib_pl_fail_net_generic"],
				AccountPlaylistStatus.Ok => Loc.Default["lib_pl_group_empty"],
				_ => Loc.Default["lib_pl_unsupported"] + "：" + Loc.Default["lib_pl_unsupported_hint"]
			};
			if (!reasons.Contains(reason))
			{
				reasons.Add(reason);
			}
		}
		foreach (string reason in reasons)
		{
			PlaylistHost.Children.Add(new TextBlock
			{
				Text = reason,
				FontSize = 11.5,
				TextWrapping = TextWrapping.Wrap,
				Foreground = ThemeManager.Tertiary(),
				Margin = new Thickness(2, 10, 2, 0)
			});
		}
	}

	/// <summary>平台 → 名称 Loc 键。命名集中在这里，
	/// 占位文案拼句都用同一套，避免出现「网易云/NetEase」混排。
	/// 歌单列表本身已不显示平台名（用户要求），此键仅用于 Toast 等其它场景。</summary>
	private static string PlatformNameKey(string platform) => "lib_pl_plat_" + platform;

	/// <summary>平台显示名：有 Loc 键的用键，B站没有（按约定不新增 Loc 键）
	/// 直接给字面量。Toast/提示拼句统一走这里，不会漏出键名。</summary>
	private static string PlatformDisplayName(string platform)
		=> platform == "bili" ? "BiliBili" : Loc.Default[PlatformNameKey(platform)];

	/// <summary>平台 → 收缩栏标题行的标签（文字 + 底色）。色值与行内平台标签
	///（TrackRows.SavedEngineTag）同源：分组头和行看起来是同一套东西。
	/// 汽水走官方「汽水绿」（TrackRows.SodaAccentBrush，随主题亮暗切换深浅），
	/// 不再用通用的蓝紫色占位。</summary>
	private static (string Label, System.Windows.Media.Brush Brush)? PlatformTag(string platform) => platform switch
	{
		"netease" => ("网易云", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC2, 0x0C, 0x0C))),
		"qq" => ("QQ音乐", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x31, 0xC2, 0x7C))),
		"kugou" => ("酷狗", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0xA5, 0xF6))),
		"soda" => ("汽水", TrackRows.SodaAccentBrush()),
		// B站官方粉 #FB7299，与在线页 BiliBili 标签同一读法。
		"bili" => ("BiliBili", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFB, 0x72, 0x99))),
		_ => null
	};

	/// <summary>折叠/展开一个平台的歌单收缩栏。状态记在
	/// <see cref="_collapsedPlaylistGroups"/>，动画交给 TrackRows.SetPanelExpanded。</summary>
	private void TogglePlaylistGroup(string platform)
	{
		if (!_playlistGroupUis.TryGetValue(platform, out var ui))
		{
			return;
		}
		bool collapse = !_collapsedPlaylistGroups.Contains(platform);
		if (collapse)
		{
			_collapsedPlaylistGroups.Add(platform);
		}
		else
		{
			_collapsedPlaylistGroups.Remove(platform);
		}
		TrackRows.SetPanelExpanded(ui.Rows, ui.Chevron, !collapse);
		// 行的显隐会一路动画改高度，组头原位跟着变，粘顶得重算。
		QueueStickyUpdate();
	}

	// ── 收藏夹平台分组的按住拖动排序 ────────────────────────────
	//
	// 组头原来「按下即折叠」，和拖动天然冲突，所以改成：按下只记点位
	// 并抓获鼠标，移动超出系统拖拽阈值（Minimum{Horizontal,Vertical}DragDistance）
	// 才进拖动态 —— 拖到哪一组中线之下就实时跟那一组换位，所见即所得；
	// 松手把视觉顺序写进设置的 LibraryPlatformOrder（初始顺序是账号页
	// 各平台第一次成功加载收藏夹的先后，拖完之后以拖动结果为准）。
	// 没超过阈值的按下-松开就还是普通点击，折叠/展开行为不变。

	private string? _groupDragPlatform;
	private bool _groupDragArmed;
	private Point _groupDragPress;
	/// <summary>跟手的浮影：拖动时一枚「平台标签 + 昵称」的小卡片贴着指针走，
	/// 用户随时知道拖的是哪一个。Popup 装的是 body Border（居中定位要用它的实际尺寸）。</summary>
	private Popup? _groupDragGhost;
	private System.Windows.Controls.Border? _groupDragGhostBody;
	/// <summary>拖动扫过的高亮组头（别的平台）。换位动画之外再加一层
	/// 「松手会落在这」的落点反馈。</summary>
	private Border? _groupDragHover;
	/// <summary>拖到视口上下边缘时的自动滚动计时器。越贴近边缘滚得越快。</summary>
	private DispatcherTimer? _dragAutoScrollTimer;
	private double _dragAutoScrollSpeed;

	private void BeginGroupDrag(string platform, FrameworkElement header, MouseButtonEventArgs e)
	{
		// 已经有一只手在拖别的组时忽略新的按下（多键同时按的边角情况）。
		if (_groupDragPlatform != null)
		{
			return;
		}
		_groupDragPlatform = platform;
		_groupDragArmed = false;
		_groupDragPress = e.GetPosition(PlaylistHost);
		header.CaptureMouse();
		e.Handled = true;
	}

	private void GroupDragMove(MouseEventArgs e)
	{
		if (_groupDragPlatform == null)
		{
			return;
		}
		Point p = e.GetPosition(PlaylistHost);
		if (!_groupDragArmed)
		{
			if (Math.Abs(p.X - _groupDragPress.X) < SystemParameters.MinimumHorizontalDragDistance
				&& Math.Abs(p.Y - _groupDragPress.Y) < SystemParameters.MinimumVerticalDragDistance)
			{
				return;
			}
			_groupDragArmed = true;
			if (e.MouseDevice.Captured is FrameworkElement cap)
			{
				cap.Cursor = Cursors.SizeAll;
				// 原位组头压暗：浮影「拿走了」它，落下时恢复。
				cap.Opacity = 0.35;
			}
			ShowGroupDragGhost(_groupDragPlatform);
		}
		// 浮影中心对准指针：用户按住的是标签的中心，浮影就该以指针为中心，
		// 偏到右下角会让人以为拖的不是这一组。首帧量不到尺寸先按左上角走，
		// 一两帧后 ActualWidth/Height 生效就正了。
		if (_groupDragGhost != null && _groupDragGhostBody != null)
		{
			_groupDragGhost.HorizontalOffset = p.X - _groupDragGhostBody.ActualWidth / 2;
			_groupDragGhost.VerticalOffset = p.Y - _groupDragGhostBody.ActualHeight / 2;
		}
		UpdateGroupDragHover(_groupDragPlatform, p.Y);
		MoveGroupUnderPointer(_groupDragPlatform, p.Y);
		UpdateDragAutoScroll(e);
	}

	/// <summary>拖动浮影：一枚复刻组头前排内容（平台标签 + 账号名）的小卡片，
	/// 挂在 Popup 上跟着指针走。IsHitTestVisible=false，纯展示不挡事。</summary>
	private void ShowGroupDragGhost(string platform)
	{
		if (_groupDragGhost != null)
		{
			return;
		}
		(string label, System.Windows.Media.Brush brush)? tag = PlatformTag(platform);
		var chip = TrackRows.MakeChip(tag?.label ?? platform, tag?.brush ?? ThemeManager.Accent());
		string nick = AccountLibraryService.AccountNameOf(platform);
		if (nick.Length == 0)
		{
			nick = tag?.label ?? platform;
		}
		var text = new TextBlock
		{
			Text = nick,
			FontSize = 12,
			Margin = new Thickness(6, 0, 2, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
		var body = new Border
		{
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(10, 6, 10, 6),
			BorderThickness = new Thickness(1),
			Opacity = 0.94,
			Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { chip, text } }
		};
		body.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
		body.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush");
		body.Effect = new System.Windows.Media.Effects.DropShadowEffect
		{
			BlurRadius = 12,
			ShadowDepth = 2,
			Opacity = 0.35
		};
		_groupDragGhost = new Popup
		{
			AllowsTransparency = true,
			PlacementTarget = PlaylistHost,
			Placement = PlacementMode.RelativePoint,
			StaysOpen = true,
			IsHitTestVisible = false,
			Child = body
		};
		_groupDragGhostBody = body;
		_groupDragGhost.IsOpen = true;
	}

	/// <summary>拖动扫到哪个平台组上，就把哪组的组头点亮成强调色淡底——
	/// 用户在碰到别的组之前就能看到「松手会落进这一组」。</summary>
	private void UpdateGroupDragHover(string platform, double pointerY)
	{
		Border? hover = null;
		foreach (var u in _playlistGroupUis.Values)
		{
			if (u.Platform == platform || u.Root?.Parent != PlaylistHost)
			{
				continue;
			}
			Point tl = u.Root.TranslatePoint(new Point(0, 0), PlaylistHost);
			if (pointerY >= tl.Y && pointerY <= tl.Y + u.Root.ActualHeight)
			{
				hover = u.Header;
				break;
			}
		}
		if (ReferenceEquals(_groupDragHover, hover))
		{
			return;
		}
		if (_groupDragHover != null)
		{
			_groupDragHover.Background = Brushes.Transparent;
		}
		_groupDragHover = hover;
		if (hover != null)
		{
			hover.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
		}
	}

	/// <summary>收掉浮影和落点高亮，停掉自动滚动，原位组头恢复不透明。</summary>
	private void TeardownGroupDragVisuals(FrameworkElement header)
	{
		if (_groupDragGhost != null)
		{
			_groupDragGhost.IsOpen = false;
			_groupDragGhost = null;
		}
		_groupDragGhostBody = null;
		if (_groupDragHover != null)
		{
			_groupDragHover.Background = Brushes.Transparent;
			_groupDragHover = null;
		}
		_dragAutoScrollTimer?.Stop();
		_dragAutoScrollSpeed = 0;
		header.Opacity = 1.0;
	}

	private void EndGroupDrag(string platform, FrameworkElement header, MouseButtonEventArgs e)
	{
		bool wasDrag = _groupDragPlatform == platform && _groupDragArmed;
		if (_groupDragPlatform == platform)
		{
			header.ReleaseMouseCapture();
			header.Cursor = Cursors.Hand;
			_groupDragPlatform = null;
			_groupDragArmed = false;
			TeardownGroupDragVisuals(header);
		}
		if (wasDrag)
		{
			PersistGroupOrder();
			// 换位动画还有 160ms 才跑完，立刻重算吸顶会拿到半路上的位置。
			var sticky = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(200)
			};
			sticky.Tick += (_, _) =>
			{
				sticky.Stop();
				QueueStickyUpdate();
			};
			sticky.Start();
		}
		else
		{
			// 没拖动 = 普通点击，折叠/展开该分组。
			TogglePlaylistGroup(platform);
		}
		e.Handled = true;
	}

	private void CancelGroupDrag(string platform, FrameworkElement header)
	{
		// 捕获意外丢失（面板重建、切页、Alt-Tab）：收掉视觉痕迹，别当点击。
		if (_groupDragPlatform != platform)
		{
			return;
		}
		header.Cursor = Cursors.Hand;
		_groupDragPlatform = null;
		_groupDragArmed = false;
		TeardownGroupDragVisuals(header);
	}

	/// <summary>拖动中实时换位：按指针 Y 和各组中线比，越过哪组中线就插到哪组
	/// 前/后。PlaylistHost 里还混着「本地音乐/全部歌曲」两个入口行和占位文字，
	/// 所以先从子元素里把「组 root」挑出来按视觉顺序比较，再换算成整体索引搬移。</summary>
	private void MoveGroupUnderPointer(string platform, double pointerY)
	{
		if (!_playlistGroupUis.TryGetValue(platform, out var dragUi) || dragUi.Root?.Parent != PlaylistHost)
		{
			return;
		}
		var ordered = new List<PlaylistGroupUi>();
		foreach (object child in PlaylistHost.Children)
		{
			if (child is StackPanel sp)
			{
				foreach (var u in _playlistGroupUis.Values)
				{
					if (ReferenceEquals(u.Root, sp))
					{
						ordered.Add(u);
						break;
					}
				}
			}
		}
		int cur = ordered.FindIndex(u => ReferenceEquals(u.Root, dragUi.Root));
		if (cur < 0 || ordered.Count < 2)
		{
			return;
		}
		var roots = ordered.Select(u => u.Root).ToList();
		// 换位前先记下各组的位置：换完把「被挤动」的组都从原位平滑滑到新位，
		// 而不是瞬间跳过去 —— 跳变会让人看不清到底谁和谁换了。
		var beforeY = new Dictionary<StackPanel, double>(roots.Count);
		foreach (var r in roots)
		{
			beforeY[r] = RootLayoutY(r, PlaylistHost);
		}
		// 换位边界用「组头视觉中点」而不是整组中线：指针一碰到别的平台标签的
		// 中心就该换位。旧逻辑拿整组（组头+全部收藏夹行）的中线当边界，结果是
		// 「按在下一组的标签上没反应，非要拖进收藏夹行里才动」，手感很钝；
		// 拖到底部同理 —— 够到底部标签的中心就算移到最后。
		// 中点 = 根的布局位置 + 组头上边距 + 吸顶位移 + 半个组头高：
		// 吸顶位移必须算进去，否则贴在视口顶部的标签（布局位置早已滚出
		// 视口）永远够不着 —— 指针按在吸顶标签上却没反应就是这笔账没算；
		// 根的滑动动画偏移由 RootLayoutY 扣掉，拖动中量到的值是稳定的。
		int target = ordered.Count;
		for (int i = 0; i < ordered.Count; i++)
		{
			var u = ordered[i];
			double mid = RootLayoutY(u.Root, PlaylistHost) + u.Header.Margin.Top
				+ u.HeaderShift.Y + u.Header.ActualHeight / 2;
			if (pointerY < mid)
			{
				target = i;
				break;
			}
		}
		if (target == cur || target == cur + 1)
		{
			// 落点还在自己当前槽位里（前一组组头中线之后、下一组组头中线之前），不用动。
			return;
		}
		int removeIdx = PlaylistHost.Children.IndexOf(dragUi.Root);
		int insertIdx = target < ordered.Count
			? PlaylistHost.Children.IndexOf(ordered[target].Root)
			: PlaylistHost.Children.IndexOf(ordered[ordered.Count - 1].Root) + 1;
		PlaylistHost.Children.RemoveAt(removeIdx);
		if (removeIdx < insertIdx)
		{
			insertIdx--;
		}
		if (insertIdx < 0)
		{
			insertIdx = 0;
		}
		if (insertIdx > PlaylistHost.Children.Count)
		{
			insertIdx = PlaylistHost.Children.Count;
		}
		PlaylistHost.Children.Insert(insertIdx, dragUi.Root);
		// 先把布局跑完再量新位置：不刷新的话 TranslatePoint 拿到的还是
		// 换位前的旧坐标，差值恒为 0，滑动动画就一次都没触发过（被挤的组瞬间跳位）。
		PlaylistHost.UpdateLayout();
		// 位移过的组各自从旧位滑到新位（160ms 出弧线），浮影和指针还在继续跟手。
		foreach (var r in roots)
		{
			double after = RootLayoutY(r, PlaylistHost);
			AnimateGroupSlide(r, beforeY[r] - after);
		}
		ResyncStickyOrder();
	}

	/// <summary>组根的「布局位置」Y。TranslatePoint 会把 RenderTransform 一并算
	/// 进去，而拖动换位的滑动动画恰好挂在根的 RenderTransform 上 —— 不扣掉它，
	/// 所有拿根位置做依据的逻辑（吸顶、换位测距）在动画进行中都会漂移。
	/// 扣掉的正是动画的当前偏移：动画持有在 0 时（静止）扣减为零，无副作用。</summary>
	private static double RootLayoutY(StackPanel root, UIElement host)
	{
		double y = root.TranslatePoint(new Point(0, 0), host).Y;
		if (root.RenderTransform is TranslateTransform tt)
		{
			y -= tt.Y;
		}
		return y;
	}

	/// <summary>把一根组根从 <paramref name="deltaY"/> 的偏移平滑滑回 0。
	/// 用 RenderTransform 不碰布局，滑完自动停在原点。</summary>
	private static void AnimateGroupSlide(StackPanel root, double deltaY)
	{
		if (Math.Abs(deltaY) < 1.0)
		{
			return;
		}
		var tt = root.RenderTransform as TranslateTransform;
		if (tt == null)
		{
			tt = new TranslateTransform();
			root.RenderTransform = tt;
		}
		tt.BeginAnimation(TranslateTransform.YProperty, null);
		tt.Y = deltaY;
		var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(160))
		{
			EasingFunction = new System.Windows.Media.Animation.CubicEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
			}
		};
		tt.BeginAnimation(TranslateTransform.YProperty, anim);
	}

	// ── 拖动时的边缘自动滚动 ────────────────────────────────
	// 收藏夹比视口长的时候，把组拖到上面/下面必须能自己滚过去，不然
	// 够不着的组永远换不了位。指针进入视口上下 48px 的边缘带就开始滚，
	// 越贴近边缘越快（2 → 14 px/帧，60fps 下最高约 840px/s）；
	// 滚动后按当前指针位置把换位/高亮/浮影一起刷新，不用再晃鼠标。

	/// <summary>按指针在视口里的位置决定要不要滚、往哪滚、滚多快。</summary>
	private void UpdateDragAutoScroll(MouseEventArgs e)
	{
		Point sp = e.GetPosition(PlaylistScroll);
		const double zone = 48.0;
		double speed = 0;
		if (sp.Y < zone && PlaylistScroll.VerticalOffset > 0.5)
		{
			speed = -AutoScrollSpeed(sp.Y, zone, downward: false);
		}
		else if (sp.Y > PlaylistScroll.ViewportHeight - zone
			&& PlaylistScroll.VerticalOffset < PlaylistScroll.ScrollableHeight - 0.5)
		{
			speed = AutoScrollSpeed(PlaylistScroll.ViewportHeight - sp.Y, zone, downward: true);
		}
		SetDragAutoScroll(speed);
	}

	/// <summary>离边缘越近速度越快：正好压在边上最快，刚进边缘带时最慢。
	/// 往下单独调快：列表越往下内容越多（大歌单拖到底要好几屏），
	/// 上行那套速度拖下去太磨人，下行给到约三倍的巡航速度。</summary>
	private static double AutoScrollSpeed(double distToEdge, double zone, bool downward)
		=> downward
			? 6.0 + 30.0 * (1.0 - Math.Clamp(distToEdge, 0, zone) / zone)
			: 2.0 + 12.0 * (1.0 - Math.Clamp(distToEdge, 0, zone) / zone);

	private void SetDragAutoScroll(double speed)
	{
		if (Math.Abs(speed) < 0.01)
		{
			_dragAutoScrollTimer?.Stop();
			_dragAutoScrollSpeed = 0;
			return;
		}
		if (_dragAutoScrollTimer == null)
		{
			_dragAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
			_dragAutoScrollTimer.Tick += DragAutoScrollTick;
		}
		_dragAutoScrollSpeed = speed;
		_dragAutoScrollTimer.Start();
	}

	private void DragAutoScrollTick(object? sender, EventArgs e)
	{
		if (_groupDragPlatform == null || !_groupDragArmed)
		{
			_dragAutoScrollTimer?.Stop();
			_dragAutoScrollSpeed = 0;
			return;
		}
		// 不直接写 VerticalOffset（一格一格的，看着像逐帧跳），
		// 走滚轮同一条 SmoothScroll 管线：速度累加进目标偏移，
		// 渲染帧指数式追过去，起滚、变速、松手收尾全是平滑的。
		SmoothScroll.Animate(PlaylistScroll, _dragAutoScrollSpeed);
		// 内容滚了但指针没动：换位判定、落点高亮、浮影位置全部按新坐标重算，
		// 否则滚过去半天界面没反应，要再晃一下鼠标才动。
		Point p = Mouse.GetPosition(PlaylistHost);
		UpdateGroupDragHover(_groupDragPlatform, p.Y);
		MoveGroupUnderPointer(_groupDragPlatform, p.Y);
		if (_groupDragGhost != null && _groupDragGhostBody != null)
		{
			_groupDragGhost.HorizontalOffset = p.X - _groupDragGhostBody.ActualWidth / 2;
			_groupDragGhost.VerticalOffset = p.Y - _groupDragGhostBody.ActualHeight / 2;
		}
	}

	/// <summary>把吸顶顺序表按当前视觉顺序重排（拖动换位后吸顶算法读的就是它）。</summary>
	private void ResyncStickyOrder()
	{
		var ordered = new List<PlaylistGroupUi>();
		foreach (object child in PlaylistHost.Children)
		{
			if (child is StackPanel sp)
			{
				foreach (var u in _playlistGroupUis.Values)
				{
					if (ReferenceEquals(u.Root, sp))
					{
						ordered.Add(u);
						break;
					}
				}
			}
		}
		if (ordered.Count == _playlistStickyOrder.Count)
		{
			_playlistStickyOrder.Clear();
			_playlistStickyOrder.AddRange(ordered);
		}
	}

	/// <summary>把当前视觉顺序落盘：写入 LibraryPlatformOrder，并同步重排
	/// _groups（它是「重建面板/重新加载」时唯一的顺序来源）。</summary>
	private void PersistGroupOrder()
	{
		try
		{
			var order = new List<string>();
			foreach (object child in PlaylistHost.Children)
			{
				if (child is StackPanel sp)
				{
					foreach (var u in _playlistGroupUis.Values)
					{
						if (ReferenceEquals(u.Root, sp))
						{
							order.Add(u.Platform);
							break;
						}
					}
				}
			}
			if (order.Count == 0)
			{
				return;
			}
			var s = SettingsManager.Instance;
			s.LibraryPlatformOrder = order;
			// 落盘的是用户亲手拖出来的顺序：打上手动标记，之后初始排序
			// （歌单数升序）不再覆盖它。
			s.LibraryPlatformOrderIsManual = true;
			SettingsManager.Save(s);
			if (_groups != null)
			{
				var map = _groups.ToDictionary(g => g.Platform, g => g);
				var ordered = new List<AccountPlaylistGroup>();
				foreach (string p in order)
				{
					if (map.Remove(p, out var g))
					{
						ordered.Add(g);
					}
				}
				ordered.AddRange(_groups.Where(g => map.ContainsKey(g.Platform)));
				_groups = ordered;
			}
			LogHelper.Log("收藏夹平台顺序: " + string.Join(" > ", order));
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("收藏夹平台顺序落盘失败: " + ex.Message);
		}
	}

	// ── 吸顶带裁剪 ────────────────────────────────────────────────
	//
	// 组头是透明的（整栏走通透风格），所以被它压住的内容必须真的挖掉，
	// 不能指望底色去盖。
	//
	// 这里有一个踩过的坑：早先只裁「吸住的那一组自己的行」，于是别的元素
	// 照样从组头底下露出来 —— 两个入口行（本地音乐 / 全部歌曲）、占位文字、
	// 上一组的行都在裁剪名单之外。表现就是组头文字上方那 6px 内边距里
	// 漂着一排被切掉一半的字（用户截图里「收藏夹和网易云之间那道缝」）。
	//
	// 现在统一处理：PlaylistHost 里除了组头本身，全部按同一个带子裁剪。
	// 带子下沿 = 最下面那个吸住的组头的下沿；没有组头吸住就不裁。

	/// <summary>元素 → 上一帧的裁剪参数。几何只在参数变了才重建：
	/// 这段挂在 LayoutUpdated 上，旁边一有布局就调一次，无脑 new 几何等于
	/// 一直给渲染线程递新对象。</summary>
	private readonly Dictionary<FrameworkElement, (bool Clipped, double Band, double Top, double H, double W)>
		_playlistClipStates = new();

	private void ApplyStickyBandClip(FrameworkElement el, double bandBottom)
	{
		double h = el.ActualHeight;
		double w = el.ActualWidth;
		double top = el.TranslatePoint(new Point(0, 0), PlaylistScroll).Y;
		bool need = bandBottom > 0.5 && h > 1 && w > 1 && top < bandBottom;
		if (_playlistClipStates.TryGetValue(el, out var last))
		{
			if (!need)
			{
				if (last.Clipped)
				{
					el.Clip = null;
					_playlistClipStates[el] = (false, 0, 0, 0, 0);
				}
				return;
			}
			// 带子没动、元素也没动（只改了一个像素以内）就沿用上一份几何。
			if (last.Clipped
				&& Math.Abs(last.Band - bandBottom) < 0.5
				&& Math.Abs(last.Top - top) < 0.5
				&& Math.Abs(last.H - h) < 0.5
				&& Math.Abs(last.W - w) < 0.5)
			{
				return;
			}
		}
		else if (!need)
		{
			return;
		}
		// Rect 的第四个参数是「高度」不是「终点」：起点 y = -top，
		// 要一直覆盖到屏幕坐标 bandBottom，高度就是 bandBottom。
		var band = new Rect(-1000, -top, w + 2000, bandBottom);
		el.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
			new RectangleGeometry(new Rect(0, 0, w, h)),
			new RectangleGeometry(band));
		_playlistClipStates[el] = (true, bandBottom, top, h, w);
	}

	/// <summary>把吸顶带裁剪铺到面板里所有「会被滚上去」的元素上。</summary>
	private void ApplyStickyBandClipToHost(double bandBottom)
	{
		foreach (object child in PlaylistHost.Children)
		{
			if (child is not FrameworkElement fe)
			{
				continue;
			}
			// 组根 = 组头 + 行面板：只裁行面板，组头要留在带子上面。
			PlaylistGroupUi? group = null;
			foreach (PlaylistGroupUi g in _playlistStickyOrder)
			{
				if (ReferenceEquals(g.Root, fe))
				{
					group = g;
					break;
				}
			}
			if (group != null)
			{
				ApplyStickyBandClip(group.Rows, bandBottom);
			}
			else
			{
				ApplyStickyBandClip(fe, bandBottom);
			}
		}
	}

	private void PlaylistScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		UpdateStickyPlaylistHeaders();
	}

	/// <summary>布局一动（折叠动画每帧都在动）就要重算，否则组头会停在旧位置。
	/// 用 Background 优先级合帧：一次布局里可能连发好几次，没必要每次都算。</summary>
	private void PlaylistHost_LayoutUpdated(object? sender, EventArgs e)
	{
		QueueStickyUpdate();
	}

	private bool _stickyQueued;

	private void QueueStickyUpdate()
	{
		if (_stickyQueued)
		{
			return;
		}
		_stickyQueued = true;
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
		{
			_stickyQueued = false;
			UpdateStickyPlaylistHeaders();
		}));
	}

	/// <summary>平台分组头吸顶：要滚出视野的组头停在滚动区顶部；下一个组的头
	/// 升到槽位上方后停在前一个组头的下沿，一个接一个向下叠起来。
	/// 往回滚时按「后停的先走」依次放行，组头跟着内容自然滑下去。
	///
	/// 与「我的收藏」页（FavoritesPage.UpdateStickyHeaders）同一套算法：
	/// 全程只做两件不改布局的事 —— 给组头加位移、给行面板挂裁剪。
	/// 因为不动布局，这段和滚动条互不触发、不会打架。</summary>
	private void UpdateStickyPlaylistHeaders()
	{
		if (_playlistStickyOrder.Count == 0 || !PlaylistScroll.IsLoaded || PlaylistScroll.ActualHeight <= 0)
		{
			return;
		}
		double stickyTop = PlaylistScroll.Padding.Top + PlaylistScroll.BorderThickness.Top;
		double stackY = stickyTop;
		// 最下面那个吸住的组头的下沿。没有组头吸住时保持 0（不裁任何东西）。
		double bandBottom = 0.0;
		foreach (PlaylistGroupUi ui in _playlistStickyOrder)
		{
			double headerH = ui.Header.ActualHeight;
			if (headerH <= 0)
			{
				continue;
			}
			// 量根面板再加上组头的上边距 = 组头的原位。必须扣掉根上滑动动画的
			// 偏移（见 RootLayoutY）：拖动换位的那 160ms 里带着偏移量，吸顶
			// 会全部量错 —— 吸住的标签跟着乱跑、裁剪带抖动、「本地音乐/全部
			// 歌曲」两行被裁得忽隐忽现（拖动时界面闪烁的根源）。
			double naturalTop = RootLayoutY(ui.Root, PlaylistScroll) + ui.Header.Margin.Top;
			// 槽位排不进视口就不叠：硬压过去会把组头推到下沿以外。
			bool canPark = stackY + headerH <= PlaylistScroll.ActualHeight;
			bool pinned = canPark && naturalTop < stackY;
			double y = pinned ? stackY : naturalTop;
			ui.HeaderShift.Y = y - naturalTop;
			if (pinned)
			{
				// 吸住的组头之间贴紧（不带上边距）：中间那条 8px 的缝同样会
				// 让下面的行露出来，而这层楼本来就是「一叠标签」，贴紧才像。
				bandBottom = y + headerH;
				stackY = bandBottom;
			}
			else
			{
				stackY = y + headerH + ui.Header.Margin.Top;
			}
		}
		ApplyStickyBandClipToHost(bandBottom);
	}

	/// <summary>歌单列表加载完成后补拉一次平台账号名（网易云昵称 / QQ 昵称，
	/// 只在设置里还没有时才请求）。拉到会返回 true，此时重建左栏把账号名
	/// 补到收缩栏标题行上。本方法由 UI 上下文的 async 链调用，await 之后
	/// 天然回 UI 线程，直接重建面板即可。</summary>
	private async Task RefreshAccountNamesAsync()
	{
		try
		{
			bool changed = await AccountLibraryService.FetchAccountNamesAsync();
			if (changed && _groups != null)
			{
				BuildPlaylistPanel();
			}
		}
		catch (Exception ex)
		{
			// fire-and-forget 的兜底：昵称补拉失败只留痕，绝不影响歌单展示。
			LogHelper.LogDebug("收藏夹: 账号名补拉异常 " + ex.Message);
		}
	}

	/// <summary>左栏的「本地音乐 / 全部歌曲」固定入口：图标 + 名字 + 副标题计数。
	/// 底色与歌单行一致（不透明 InputBgBrush 系，悬停 RowHover）。</summary>
	private FrameworkElement MakeLibraryEntry(string glyph, string title, string sub, out TextBlock subOut, Action onClick)
	{
		var sub2 = new TextBlock
		{
			Text = sub,
			FontSize = 11,
			Foreground = ThemeManager.Tertiary(),
			VerticalAlignment = VerticalAlignment.Bottom,
			Margin = new Thickness(0, 1, 0, 0)
		};
		subOut = sub2;
		var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var icon = new TextBlock
		{
			Text = glyph,
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 15,
			Foreground = ThemeManager.Secondary(),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0)
		};
		Grid.SetColumn(icon, 0);
		var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		stack.Children.Add(new TextBlock
		{
			Text = title,
			FontSize = 13,
			Foreground = ThemeManager.Primary(),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		stack.Children.Add(sub2);
		Grid.SetColumn(stack, 1);
		grid.Children.Add(icon);
		grid.Children.Add(stack);
		var border = new Border
		{
			// 透明底：与收藏夹里的歌单行统一，整栏是一列通透的行，
			// 只有悬停时才浮出半透明高亮。透明后吸顶组头压上来不会有硬边。
			Background = Brushes.Transparent,
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(10, 8, 10, 8),
			Margin = new Thickness(0, 0, 0, 6),
			Cursor = Cursors.Hand,
			Child = grid,
			SnapsToDevicePixels = true
		};
		System.Windows.Media.Brush rest = border.Background;
		border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "SubtleFillHoverBrush");
		border.MouseLeave += (_, _) => border.Background = rest;
		border.MouseLeftButtonDown += (_, _) => onClick();
		return border;
	}

	private void SelectLocalMode()
	{
		// 已在本地视图（重复点「本地音乐」入口）：直接返回。
		// 列表早已铺好，再走一遍 ApplySearch 只会把整表行容器拆了重造、
		// 路径行动画重播一遍 —— 正是「重复点击已选中入口会卡一下」的来源。
		if (_viewMode == "local")
		{
			return;
		}
		_viewMode = "local";
		// 路径行只属于本地视图：切回来把它滑开。
		AnimateFolderBar(true);
		// 切视图不清关键词：关键词对本地列表同样有效（RefreshLibraryList 用 _searchFilter）。
		ApplySearch();
	}

	private void SelectAllSongsMode()
	{
		// 已在「全部歌曲」视图且索引里已有歌（行已铺好）：直接返回。
		// 再走一遍只会整表重画 + 重弹「正在后台加载 / 批量加载完成」两个 Toast；
		// 索引为空时仍放行 —— 用户可能是在批量加载失败后重试。
		if (_viewMode == "all" && AccountLibraryService.IndexCount > 0)
		{
			return;
		}
		_viewMode = "all";
		// 路径行只属于本地视图：进收藏夹（在线）视图滑收，给歌曲区让出空间。
		AnimateFolderBar(false);
		_currentPlaylistName = "";
		ApplySearch();
		// 全部歌曲 = 触发后台批量加载：顺序遍历各平台歌单，每拉完一个就
		// 并入索引并增量刷新当前视图；已拉过的歌单只吃缓存，不再发请求。
		_ = StartBatchLoadAsync();
	}

	private async Task OpenPlaylistAsync(AccountPlaylist pl)
	{
		// 重复点击当前已经展示着的歌单：曲目在缓存里、行容器也铺好了，直接返回。
		// 之前会再走一遍取数 + RefreshOnlineView 整表重画（清空重建最多 200 个行容器），
		// 一千多首的大歌单每重点一次就卡一下，收放动画也跟着重播 ——
		// 对「已选中」的歌单，什么都不做才是正确响应。
		if (_viewMode == "playlist:" + pl.Platform + ":" + pl.Id
			&& AccountLibraryService.IsPlaylistCached(pl.Platform, pl.Id))
		{
			return;
		}
		bool cached = AccountLibraryService.IsPlaylistCached(pl.Platform, pl.Id);
		if (!cached)
		{
			// 只有真正要发请求时才给进行中提示；缓存命中直接展示，不闪提示。
			CountText.Text = Loc.Default.Format("lib_pl_opening", pl.Name);
		}
		AccountPlaylistStatus status;
		List<object> tracks;
		try
		{
			var fetchSw = System.Diagnostics.Stopwatch.StartNew();
			(status, tracks) = await AccountLibraryService.FetchPlaylistTracksAsync(pl.Platform, pl.Id);
			fetchSw.Stop();
			// [perf]：验证大歌单（1800 首/9 批）拉取+转换不再占 UI 线程
			LogHelper.Log("[perf] 歌单拉取: " + pl.Platform + "「" + pl.Name + "」"
				+ (cached ? "（缓存命中）" : "") + " " + tracks.Count + " 首 耗时 " + fetchSw.ElapsedMilliseconds + "ms");
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库收藏夹: 歌单曲目加载失败 " + ex.Message);
			Toast(Loc.Default["lib_pl_fail_net"], false);
			if (_viewMode != "local")
			{
				// 拉取失败只是恢复现状的重渲染，别把页码/滚动位置也丢了。
				RefreshOnlineView(resetPage: false);
			}
			return;
		}
		if (status != AccountPlaylistStatus.Ok)
		{
			// 失败提示按状态分流：Cookie 失效给「去账号页重存」，
			// 网络异常给「稍后再试」，QQ 微信会话给专门话术。
			string msg = status switch
			{
				AccountPlaylistStatus.CookieInvalid => pl.Platform == "qq"
					? Loc.Default["lib_pl_fail_qq_wechat"]
					: Loc.Default.Format("lib_pl_fail_cookie", PlatformDisplayName(pl.Platform)),
				AccountPlaylistStatus.NetworkError => Loc.Default.Format("lib_pl_fail_net",
					PlatformDisplayName(pl.Platform)),
				_ => Loc.Default["lib_pl_unsupported_hint"]
			};
			Toast(msg, false);
			// 把计数文案从「正在加载歌单…」恢复成当前视图的真实状态。
			// 同上：失败恢复不重置页码。
			if (_viewMode != "local")
			{
				RefreshOnlineView(resetPage: false);
			}
			return;
		}
		// 曲目进索引（内部按 平台+id 去重，重复点击/重复歌单不会翻倍）。
		AccountLibraryService.AddToIndex(pl.Platform, tracks);
		_viewMode = "playlist:" + pl.Platform + ":" + pl.Id;
		_currentPlaylistName = pl.Name;
		// 进了歌单（在线）视图，本地路径行跟着滑收。
		AnimateFolderBar(false);
		// 缓存命中静默直接展示：之前这里每次都弹「已在缓存，直接展示」的 Toast，
		// 重点一次弹一次纯属噪音，按要求整条去掉（Loc 键 lib_pl_cached 保留不删）。
		RefreshOnlineView();
		UpdatePlaylistEntryCounts();
	}

	/// <summary>「全部歌曲」触发的后台批量加载：顺序遍历各平台已知的歌单
	/// （平台之间、歌单之间都串行，避免并发触发风控），每拉完一个歌单：
	/// 缓存 → 并入索引 → 若当前视图需要（全部歌曲视图或带关键词的在线视图）
	/// 就把新命中的行追加进列表。绝不攒到最后一次性渲染。</summary>
	private async Task StartBatchLoadAsync()
	{
		if (_batchRunning)
		{
			return;
		}
		if (_groups == null || _groups.All(g => g.Playlists.Count == 0))
		{
			Toast(Loc.Default["lib_pl_batch_nothing"], false);
			return;
		}
		_batchRunning = true;
		try
		{
			Toast(Loc.Default["lib_pl_batch_loading"]);
			foreach (var g in _groups)
			{
				if (g.Status != AccountPlaylistStatus.Ok || g.Playlists.Count == 0)
				{
					continue;
				}
				foreach (var pl in g.Playlists)
				{
					if (AccountLibraryService.IsPlaylistCached(pl.Platform, pl.Id))
					{
						// 已拉过的歌单：曲目早已在索引里，跳过不发请求。
						continue;
					}
					AccountPlaylistStatus status;
					List<object> tracks;
					try
					{
						(status, tracks) = await AccountLibraryService.FetchPlaylistTracksAsync(pl.Platform, pl.Id);
					}
					catch (Exception ex)
					{
						LogHelper.LogDebug("音乐库收藏夹: 批量加载单个歌单失败 " + pl.Name + " " + ex.Message);
						continue; // 单个歌单失败不中断批量，其它歌单照常
					}
					if (status != AccountPlaylistStatus.Ok)
					{
						continue;
					}
					var added = AccountLibraryService.AddToIndex(pl.Platform, tracks);
					// 增量刷新：用户停在「全部歌曲」或带着关键词的在线视图时，
					// 新入库且命中当前关键词的歌立刻追加显示。
					if (IsOnScreen() && (_viewMode == "all" || _searchFilter.Length > 0))
					{
						AppendIncremental(added);
					}
				}
			}
			Toast(Loc.Default.Format("lib_pl_batch_done", AccountLibraryService.IndexCount));
			// 批量结束把入口计数对齐一遍（全部歌曲副标题）。
			UpdatePlaylistEntryCounts();
		}
		finally
		{
			_batchRunning = false;
		}
	}

	/// <summary>把刚入库的曲目按当前关键词过滤后追加（增量 add）。
	/// 只动新增的行，已显示的不重建 —— 这是「增量搜索」的一半：
	/// 另一半是关键词过滤本身走索引快照（SearchIndex），两边都 O(新增量)。
	/// 分页规则：新增命中先进 _onlineSource（后面的页/会话队列都看得到）；
	/// 只有当前页还装得下时才补画行，页满了交给「下一页」。</summary>
	private void AppendIncremental(List<object> added)
	{
		if (added.Count == 0)
		{
			UpdateOnlineCountText(_matchedTotal);
			return;
		}
		string filter = _searchFilter;
		// 若列表当前只有一条占位（空态提示），先把占位摘掉再追加。
		if (LibraryListBox.Items.Count == 1 && LibraryListBox.Items[0] is ListBoxItem { Content: TextBlock })
		{
			LibraryListBox.Items.RemoveAt(0);
		}
		// 当前页还能补几行：只有停在「还没有页脚」的第一页时才直接补行，
		// 免得新行插到页脚下面；多页时新增命中只进源快照，
		// 用户翻页时 RenderOnlinePage 重新切片自然带上。
		int totalBefore = _onlineSource.Count;
		int pagesBefore = Math.Max(1, (int)Math.Ceiling(totalBefore / (double)PageSize));
		int roomOnPage = _onlinePage == pagesBefore && pagesBefore == 1
			? PageSize - totalBefore
			: 0;
		int matchedNow = 0;
		foreach (object t in added)
		{
			if (filter.Length > 0 && !IndexMatch(t, filter))
			{
				continue;
			}
			matchedNow++;
			_onlineSource.Add(t);
			if (roomOnPage > 0)
			{
				roomOnPage--;
				LibraryListBox.Items.Add(MakeOnlineItemRow(t, filter));
			}
		}
		_matchedTotal += matchedNow;
		UpdateOnlineCountText(_matchedTotal);
		// 只有第一页会直接补行：当增量追加把源撑过一页时，页脚此前从未画过
		//（pagesBefore==1），不在这里补画的话新增的页就没有「下一页」入口，
		// 只能等下一次全量渲染才出现。
		int pagesAfter = Math.Max(1, (int)Math.Ceiling(_onlineSource.Count / (double)PageSize));
		if (pagesAfter > 1)
		{
			UpdatePaginationFooter(pagesAfter, _onlinePage, GoToOnlinePage);
		}
	}

	/// <summary>索引关键词匹配：歌名/歌手/专辑任一包含即命中（忽略大小写，
	/// 兜底再比一次压掉空格标点的键，跟本地库同一条规则）。</summary>
	private static bool IndexMatch(object t, string filter)
	{
		string kwKey = TrackRows.NormalizeKey(filter);
		return TrackRows.MatchesText(AccountLibraryService.TitleOf(t), filter, kwKey)
			|| TrackRows.MatchesText(AccountLibraryService.ArtistOf(t), filter, kwKey)
			|| TrackRows.MatchesText(AccountLibraryService.AlbumOf(t), filter, kwKey);
	}

	/// <summary>重画右侧歌曲区（收藏夹在线视图）：
	/// 有关键词 → 搜索引（所有已加载的歌，不等歌单拉完）；
	/// 无关键词 → 当前视图的完整曲目（某个歌单 / 全部歌曲合并）。</summary>
	/// <param name="resetPage">只有换「源」才允许重置页码：筛选词变化、切歌单/全部歌曲
	/// 这类调用传 true；纯重渲染（VIP 标签、强调色、加载失败恢复）传 false——
	/// 否则点第 2 页的歌起播，TrackAccess.Changed 回来重画一次就把用户甩回第 1 页。</param>
	private void RefreshOnlineView(bool resetPage = true)
	{
		string filter = _searchFilter;
		List<object> source;
		if (filter.Length > 0)
		{
			// 增量搜索的核心：过滤范围是「已加载进索引的歌」，
			// 索引随后台批量加载逐步变大，这里每次都拿最新快照筛。
			source = AccountLibraryService.SearchIndex(filter);
		}
		else if (_viewMode == "all")
		{
			source = AccountLibraryService.AllTracks();
		}
		else
		{
			// playlist:平台:id
			string key = _viewMode.Substring("playlist:".Length);
			int sep = key.IndexOf(':');
			source = sep > 0
				? AccountLibraryService.GetCachedTracks(key.Substring(0, sep), key.Substring(sep + 1)) ?? new List<object>()
				: new List<object>();
		}
		// 换了源（新筛选/新视图）就从第一页看起；同一源内的重渲染保留页码，
		// 行重排时滚动位置也原样放回（RenderOnlinePage 里处理）。
		if (resetPage)
		{
			_onlinePage = 1;
		}
		_onlineSource = source;
		_matchedTotal = source.Count;
		RenderOnlinePage(filter, preserveScroll: !resetPage);
	}

	/// <summary>把 _onlineSource 的当前页画出来。翻页时清空重建行容器，
	/// 防止上一页的旧行残留在列表里（任务要求：翻页防旧行）。</summary>
	/// <param name="preserveScroll">纯重渲染（标签刷新等）传 true：Items.Clear()
	/// 会把 ScrollViewer 偏移打回 0，重建完要把用户原来的滚动位置放回去。</param>
	private void RenderOnlinePage(string filter, bool preserveScroll = false)
	{
		// 代次自增：异步回调（VIP 结论等）攒下时记的代次在下次执行时对不上，
		// 就说明期间整页渲染过、回调已过期，直接丢弃（见 FlushTrackAccessUpdates）。
		_renderGeneration++;
		var renderSw = System.Diagnostics.Stopwatch.StartNew();
		double keepOffset = 0.0;
		if (preserveScroll && FindDescendantScroll(LibraryListBox) is ScrollViewer svKeep)
		{
			keepOffset = svKeep.VerticalOffset;
		}
		LibraryListBox.Items.Clear();
		List<object> source = _onlineSource;
		int pages = Math.Max(1, (int)Math.Ceiling(source.Count / (double)PageSize));
		_onlinePage = Math.Clamp(_onlinePage, 1, pages);
		int start = (_onlinePage - 1) * PageSize;
		int end = Math.Min(source.Count, start + PageSize);
		for (int i = start; i < end; i++)
		{
			LibraryListBox.Items.Add(MakeOnlineItemRow(source[i], filter));
		}
		if (LibraryListBox.Items.Count == 0)
		{
			// 空态：带关键词 = 没命中；全部歌曲视图空 = 还没加载过任何在线歌；
			// 具体歌单空 = 平台侧这个歌单就是空的。
			string empty = filter.Length > 0
				? Loc.Default["lib_none"]
				: _viewMode == "all"
					? Loc.Default["lib_pl_index_empty"]
					: Loc.Default["acct_pl_tracks_empty"];
			LibraryListBox.Items.Add(new ListBoxItem
			{
				Content = new TextBlock
				{
					Text = empty,
					FontSize = 13,
					Foreground = ThemeManager.Tertiary(),
					Margin = new Thickness(8, 12, 8, 12)
				},
				IsHitTestVisible = false
			});
		}
		if (keepOffset > 0 && FindDescendantScroll(LibraryListBox) is ScrollViewer svBack)
		{
			// 行容器刚清空重建、布局未必已经跑过，extent 还是旧值时直接滚会被夹住，
			// 等 Loaded 一拍（排在布局后面）再把原偏移放回去。
			double back = keepOffset;
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
				() => svBack.ScrollToVerticalOffset(back));
		}
		// 页脚：固定在左栏收藏夹面板底部，多于一页才出现，交互与在线音乐页一致。
		UpdatePaginationFooter(pages, _onlinePage, GoToOnlinePage);
		// 渲染完第 N 页后，把第 N+1 页前 5 首的封面提前拉进缓存（只进缓存不显示）。
		PreloadOnlineCovers(source, end);
		UpdateOnlineCountText(source.Count);
		// 主动探测 VIP（设置开关，默认关）：收藏夹/歌单列表加载完后
		// 后台逐条实测打标。当前页排前面，用户正盯着的行最先拿到结论。
		// 同键（视图+筛选词+页码）不重排：以前每次渲染都把全量 Schedule 一遍，
		// generation++ 把上一轮扫掠作废重启，结论落地又触发下一次渲染+重排，
		// 自激成 200ms 节拍的重画循环。排程键只在换源/翻页时才变化，
		// 那才是需要重排探测优先级的时机。
		if (source.Count > 0)
		{
			string probeKey = _viewMode + "|" + filter + "|" + _onlinePage;
			if (probeKey != _vipProbeKey)
			{
				_vipProbeKey = probeKey;
				var probeOrder = new List<object>(source.Count);
				int ps = Math.Max(0, start);
				int pe = Math.Min(source.Count, end);
				for (int i = ps; i < pe; i++)
				{
					probeOrder.Add(source[i]);
				}
				for (int i = 0; i < source.Count; i++)
				{
					if (i < ps || i >= pe)
					{
						probeOrder.Add(source[i]);
					}
				}
				VipProbe.Schedule(probeOrder);
			}
		}
		if (filter.Length > 0)
		{
			TrackRows.AnimateFilteredIn(LibraryListBox);
		}
		// 登记会话源：收藏夹/歌单/全部歌曲里点播的「下一首」要顺这份
		// 完整列表走（跨页），交给在线页持有，MusicService 手动切歌时取用。
		MainWindow.Instance?.EnsureOnlinePage()?.SetLibrarySessionSource(source);
		renderSw.Stop();
		// [perf]：验证收藏夹歌单/全部歌曲列表走 100 首/页分页渲染——
		// 大歌单第 1 页应稳定在百毫秒级；若出现秒级耗时说明渲染又退化成全量。
		string viewName = filter.Length > 0
			? "搜索「" + filter + "」"
			: _viewMode == "all" ? "全部歌曲" : _currentPlaylistName;
		LogHelper.Log("[perf] 歌单渲染: " + viewName + " · 第 " + _onlinePage + "/" + pages + " 页 "
			+ (end - start) + " 行/共 " + source.Count + " 首 耗时 " + renderSw.ElapsedMilliseconds + "ms");
	}

	private void GoToOnlinePage(int page)
	{
		_onlinePage = page;
		RenderOnlinePage(_searchFilter);
		ScrollLibraryToTop();
	}

	/// <summary>把当前视图的分页状态刷进左栏底部那个固定页脚
	///（PaginationFooterHost，XAML 里 Dock 在收藏夹面板底部）。
	/// 只有一页时整个页脚收起来，不占收藏夹面板的空间。
	/// 本地音乐 / 全部歌曲 / 歌单三个视图共用这一个页脚：
	/// 每次重画列表（RefreshLibraryList / RenderOnlinePage）都会刷一遍，
	/// 所以视图切换后页脚的跳页回调自然跟着换。</summary>
	private void UpdatePaginationFooter(int pages, int current, Action<int> go)
	{
		if (pages <= 1)
		{
			// 收起也走滑收动画（与 FolderBar 同款）；Child 留到动画收尾再清，
			// 提前清空会量不到高度，滑动变成瞬间消失。
			AnimateSlideFade(PaginationFooterHost, false,
				() => _pagerAnimGen, v => _pagerAnimGen = v,
				onHidden: () => PaginationFooterHost.Child = null);
			return;
		}
		PaginationFooterHost.Child = BuildPaginationPanel(pages, current, go);
		AnimateSlideFade(PaginationFooterHost, true,
			() => _pagerAnimGen, v => _pagerAnimGen = v);
	}

	/// <summary>分页页脚内容：第一页/上一页/页码/下一页，
	/// 视觉与在线音乐页的翻页条一致（GhostButtonStyle）。go 参数是两套
	/// 视图各自的跳页回调（本地视图 → GoToLocalPage，在线视图 → GoToOnlinePage）。</summary>
	private FrameworkElement BuildPaginationPanel(int pages, int current, Action<int> go)
	{
		var panel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		Button MakeBtn(string text, int target, bool enabled)
		{
			var b = new Button
			{
				Content = text,
				Padding = new Thickness(14, 6, 14, 6),
				Margin = new Thickness(4, 0, 4, 0),
				BorderThickness = new Thickness(0),
				Cursor = System.Windows.Input.Cursors.Hand,
				IsEnabled = enabled,
				Style = (Style)FindResource("GhostButtonStyle")
			};
			int page = target;
			b.Click += (_, _) => go(page);
			return b;
		}
		panel.Children.Add(MakeBtn(Loc.Default["online_first"], 1, current > 1));
		panel.Children.Add(MakeBtn(Loc.Default["online_prev"], current - 1, current > 1));
		panel.Children.Add(new TextBlock
		{
			Text = Loc.Default.Format("online_page", current),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8, 0, 8, 0),
			Foreground = ThemeManager.Tertiary(),
			FontSize = 13
		});
		panel.Children.Add(MakeBtn(Loc.Default["online_next"], current + 1, current < pages));
		// 页脚挪到左栏底部固定后，不再需要 ListBoxItem 包装（以前是为了
		// 混进歌曲列表里）：直接把水平面板塞给 PaginationFooterHost。
		return panel;
	}

	/// <summary>在线行：构建转发给在线页（同一条链路），Tag 挂 OnlineItem
	/// 供双击/回车/右键识别。highlight 传给行内做关键词高亮，
	/// 与在线搜索页的高亮效果一致。</summary>
	private FrameworkElement MakeOnlineItemRow(object track, string highlight)
	{
		var omp = MainWindow.Instance?.EnsureOnlinePage();
		if (omp == null)
		{
			// 理论上到不了（MainWindow 一定在）：给个不至于崩的兜底。
			return new TextBlock
			{
				Text = AccountLibraryService.TitleOf(track),
				FontSize = 13,
				Margin = new Thickness(8, 4, 8, 4)
			};
		}
		return omp.BuildLibraryRow(track, highlight.Length > 0 ? highlight : null);
	}

	/// <summary>在线视图的计数文案。matched = 关键词命中的总数（-1 表示按列表
	/// 行数估算）。原上限提示（lib_pl_render_cap）随分页一起退役。</summary>
	private void UpdateOnlineCountText(int matched = -1)
	{
		string filter = _searchFilter;
		string text;
		if (filter.Length > 0)
		{
			text = Loc.Default.Format("lib_pl_filter_fmt",
				matched >= 0 ? matched : LibraryListBox.Items.Count,
				AccountLibraryService.IndexCount);
		}
		else if (_viewMode == "all")
		{
			text = Loc.Default.Format("lib_pl_all_fmt", AccountLibraryService.IndexCount);
		}
		else
		{
			// 具体歌单：以缓存里的真实条数为准（列表接口报的 TrackCount 可能对不上）。
			string key = _viewMode.Substring("playlist:".Length);
			int sep = key.IndexOf(':');
			int count = sep > 0
				? AccountLibraryService.GetCachedTracks(key.Substring(0, sep), key.Substring(sep + 1))?.Count ?? 0
				: 0;
			text = Loc.Default.Format("lib_pl_playlist_fmt", _currentPlaylistName, count);
		}
		CountText.Text = text;
	}

	/// <summary>提示统一走主窗口的 Toast（独立 Popup，不挡列表交互）。</summary>
	private void Toast(string msg, bool success = true) => MainWindow.Instance?.ShowToast(msg, success);
}
