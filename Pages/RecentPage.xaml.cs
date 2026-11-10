using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicBox;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class RecentPage : UserControl, IMusicPage, IMusicPageLeave
{
	// 列表是否过期，见 MainWindow.NavigateTo 的注释：导航回来时没变就别重建。
	private bool _needRefresh = true;

	// 搜索关键词和「清空」标志，见 OnNavigatedFrom 的注释。
	private string _searchFilter = "";
	private bool _suppressFilterSync;

	/// <summary>筛选后第一条命中的条目。回车 / 空格直接播它。</summary>
	private SavedTrack? _firstShown;

	// —— 增量渲染（最近播放可能几千条）——
	// 数据全量都在（搜索/计数都按全量算），只是「画」分批：一次铺几百个行控件
	// 的布局开销全砸在 UI 线程，首屏只需要最新的一批。
	private const int BatchSize = 100;

	/// <summary>距离底部多少像素内触发补画下一批。</summary>
	private const int NearBottomPixels = 200;

	/// <summary>本次筛选命中的完整列表（与显示顺序一致：最新在前）。</summary>
	private List<SavedTrack> _shown = new();

	/// <summary>已画到 _shown 的第几条（不含）。</summary>
	private int _renderedCount;

	public RecentPage()
	{
		InitializeComponent();
		UserLibrary.Changed += OnLibraryChanged;
		TrackRows.AccentChanged += OnAccentChanged;
		Loc.Default.LanguageChanged += OnLanguageChanged;
		// 后台把某首歌的时长探出来了：在屏幕上就重画一次，让它显示出来。
		LocalTrackMeta.Updated += OnLocalMetaUpdated;
		BuildPlatformFilter();
	}

	// ── 平台过滤 ────────────────────────────────────────────────
	// 勾选哪个平台就只显示哪个平台的记录（全勾=显示全部，默认态）。
	// 状态持久化在 Settings.RecentPlatformsOff：只记「被关掉」的平台，
	// 以后新增平台默认可见，不会被老配置顺手藏掉。

	/// <summary>过滤行里的平台与顺序（与 TrackRows 平台标签/搜索引擎下拉一致）。</summary>
	private static readonly string[] FilterPlatforms = { "netease", "qq", "kugou", "bili", "soda", "local" };

	/// <summary>平台 → 过滤勾选框。</summary>
	private readonly Dictionary<string, CheckBox> _platformChecks = new();

	/// <summary>生成过滤勾选框（只建一次），文案按当前语言。构造时没有列表数据
	/// 也照建：面板默认收起，RefreshList 里按有无记录再显隐。</summary>
	private void BuildPlatformFilter()
	{
		if (PlatformFilterChecks.Children.Count > 0)
		{
			return;
		}
		foreach (string p in FilterPlatforms)
		{
			var cb = new CheckBox
			{
				FontSize = 12.5,
				Margin = new Thickness(0, 0, 14, 0),
				VerticalAlignment = VerticalAlignment.Center,
				IsChecked = true
			};
			cb.Checked += (_, _) => OnPlatformFilterChanged();
			cb.Unchecked += (_, _) => OnPlatformFilterChanged();
			_platformChecks[p] = cb;
			PlatformFilterChecks.Children.Add(cb);
		}
		SyncPlatformFilterUi();
	}

	/// <summary>把持久化的「被关掉平台」刷回勾选框，并按当前语言补文案。
	/// 构造和语言切换都会走；不在屏幕上时文案下次 RefreshList 也会补。</summary>
	private void SyncPlatformFilterUi()
	{
		PlatformFilterLabel.Text = Loc.Default["recent_platform_filter"];
		HashSet<string> off = PlatformsOff();
		foreach (KeyValuePair<string, CheckBox> kv in _platformChecks)
		{
			string label = TrackRows.PlatformDisplayName(kv.Key) ?? kv.Key;
			kv.Value.Content = label;
			bool want = !off.Contains(kv.Key);
			if (kv.Value.IsChecked != want)
			{
				kv.Value.IsChecked = want; // 触发 Checked/Unchecked → OnPlatformFilterChanged
			}
		}
	}

	/// <summary>被关掉的平台集合（来自持久化设置）。</summary>
	private static HashSet<string> PlatformsOff()
	{
		var set = new HashSet<string>(StringComparer.Ordinal);
		string raw = SettingsManager.Instance.RecentPlatformsOff ?? "";
		foreach (string p in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			set.Add(p.ToLowerInvariant());
		}
		return set;
	}

	/// <summary>一条最近播放记录属于哪个平台（SavedTrack.Type，空/未知算本地）。</summary>
	private static string PlatformOfTrack(SavedTrack t)
	{
		string p = (t.Type ?? "").Trim().ToLowerInvariant();
		return p.Length == 0 ? "local" : p;
	}

	/// <summary>勾选变化：写回设置并重筛（在屏幕上才重画，脏标记兜底）。</summary>
	private void OnPlatformFilterChanged()
	{
		if (_platformChecks.Count == 0)
		{
			return;
		}
		var off = new List<string>();
		foreach (string p in FilterPlatforms)
		{
			if (_platformChecks.TryGetValue(p, out CheckBox? cb) && cb.IsChecked != true)
			{
				off.Add(p);
			}
		}
		AppSettings s = SettingsManager.Instance;
		string next = string.Join(",", off);
		if (string.Equals(s.RecentPlatformsOff ?? "", next, StringComparison.Ordinal))
		{
			return; // SyncPlatformFilterUi 程序赋值也会进来，没变化就不折腾
		}
		s.RecentPlatformsOff = next;
		SettingsManager.Save(s);
		_needRefresh = true;
		if (IsLoaded && IsOnScreen())
		{
			RefreshList();
		}
	}

	/// <summary>换强调色：行里抄的旧颜色要重画才能变过来。</summary>
	private void OnAccentChanged()
	{
		_needRefresh = true;
		// 只有这一页真的在前台才画。强调色一改会广播给所有订阅者，
		// 隐藏的页面跟着重画一遍纯属浪费。
		if (IsLoaded && IsOnScreen())
		{
			RefreshList();
		}
	}

	/// <summary>页面是不是当前显示的那一页。</summary>
	private bool IsOnScreen()
	{
		return MainWindow.Instance?.IsCurrentPage(this) ?? false;
	}

	public void OnNavigatedTo()
	{
		// 数据没变就别重建整张列表 —— 每点一次导航栏回来就把所有行拆了重造，
		// 快速切换时这段同步开销全砸在 UI 线程上。Changed 事件已经把列表刷过了。
		if (_needRefresh)
		{
			RefreshList();
		}
	}

	/// <summary>离开这一页就把搜索关键词清掉。
	/// 和音乐库页同一个规矩：不记住上一次筛了什么。
	/// 「看到一半跳去设置页，回来看列表突然全量了」比「每次都要重输一遍」更让人困惑，
	/// 而搜索框还在、字是空的这件事本身就是提示 —— 真的想筛一眼就能看出来。</summary>
	public void OnNavigatedFrom()
	{
		if (string.IsNullOrEmpty(_searchFilter) && string.IsNullOrEmpty(SearchBox.Text))
		{
			return;
		}
		_searchFilter = "";
		// 打标志摘掉 TextChanged：程序清 Text 和用户敲键盘走的是同一个事件。
		_suppressFilterSync = true;
		try
		{
			SearchBox.Text = "";
		}
		finally
		{
			_suppressFilterSync = false;
		}
		_needRefresh = true;
		// 页面马上要被换出内容区了，这里画不画都看不见；
		// 但 _needRefresh 已经置位，下次 OnNavigatedTo 会按全量重画。
	}

	/// <summary>换界面语言：标题/空提示/状态行是代码赋值的，得重刷一遍。</summary>
	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		if (!IsLoaded || !IsOnScreen())
		{
			return;
		}
		RefreshList();
	}

	private void OnLibraryChanged()
	{
		_needRefresh = true;
		// 隐藏时不重建。RecentlyPlayed 每次播放都会变，触发的是全量重画；
		// 用户在音乐库页听歌时，这页重建了几百行控件却一个都看不见。
		// _needRefresh 已经置位，OnNavigatedTo 会补上。
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(RefreshIfOnScreen));
			return;
		}
		RefreshIfOnScreen();
	}

	private void RefreshIfOnScreen()
	{
		if (IsOnScreen())
		{
			RefreshList();
		}
	}

	/// <summary>后台补到了某首歌的时长。事件来自后台线程，先切回 UI 线程。
	/// 不在屏幕上就只置脏标记，等切回来时补上。</summary>
	private void OnLocalMetaUpdated()
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(OnLocalMetaUpdated));
			return;
		}
		if (!IsLoaded)
		{
			return;
		}
		_needRefresh = true;
		RefreshIfOnScreen();
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		// 显隐在 _suppressFilterSync 的 return 之前更新，
		// 原因同音乐库/收藏页：离页清空走被压住的路径。
		ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
		if (_suppressFilterSync)
		{
			return;
		}
		_searchFilter = SearchBox.Text?.Trim() ?? "";
		_needRefresh = true;
		RefreshList();
		if (_searchFilter.Length > 0)
		{
			TrackRows.AnimateFilteredIn(RecentList);
		}
	}

	/// <summary>回车 / 空格 = 播第一条筛出来的。</summary>
	private void SearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter && e.Key != Key.Space)
		{
			return;
		}
		e.Handled = true;
		if (_firstShown is SavedTrack first)
		{
			Play(first);
		}
	}

	private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
	{
		SearchBox.Focus();
		if (string.IsNullOrEmpty(SearchBox.Text))
		{
			return;
		}
		SearchBox.Text = "";
	}

	private void RefreshList()
	{
		_needRefresh = false;
		var all = UserLibrary.RecentlyPlayed;
		string filter = _searchFilter;
		string filterKey = TrackRows.NormalizeKey(filter);
		// 平台过滤：只显示勾选平台的记录。勾选状态即设置（RecentPlatformsOff），
		// 过滤行文案也在这里顺手刷（语言切换/首次进入都能拿到最新 Loc）。
		SyncPlatformFilterUi();
		HashSet<string> off = PlatformsOff();
		PlatformFilterPanel.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

		// 有关键词时先筛一遍再决定空提示说什么：
		// 「列表本来是空的」和「你筛的那首不在这页里」是两回事，
		// 都显示同一句「暂无记录」会让人以为搜索坏了。
		var shown = new List<SavedTrack>(all.Count);
		foreach (SavedTrack t in all)
		{
			// 顺序保持 RecentlyPlayed 的倒序（最新在前），平台过滤只做剔除。
			if (off.Contains(PlatformOfTrack(t)))
			{
				continue;
			}
			if (filterKey.Length == 0 || TrackRows.MatchesTrack(t, filter, filterKey))
			{
				shown.Add(t);
			}
		}

		bool filtering = filterKey.Length > 0 || off.Count > 0;
		ClearRecentButton.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		SearchBox.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		EmptyText.Text = filtering ? Loc.Default["list_no_match"] : Loc.Default["recent_empty"];

		RecentList.Items.Clear();
		_firstShown = null;
		_shown = shown;
		_renderedCount = 0;
		// 首屏只画最新一批（默认 100 条）：几千条记录一次全铺，
		// UI 线程会卡出一眼可见的顿挫，而首屏根本看不到那么深。
		// 搜索命中的入场动画仍由外层（SearchBox_TextChanged）统一做，
		// 这里不重复触发。
		AppendNextBatch();
		// 计数只跟着筛选结果走：搜的时候显示「筛出几首」，让人知道列表被收窄了。
		StatusText.Text = filtering
			? string.Format(Loc.Default["list_count_shown"], shown.Count, all.Count)
			: (all.Count > 0 ? string.Format(Loc.Default["recent_count"], all.Count) : "");
	}

	private static void Play(SavedTrack t)
	{
		// 登记播放上下文：「启动时自动播放」按它找回最近播放列表随机续播。
		MusicService.PendingPlayContext = new PlayContext { Page = "recent", ListKey = "recent" };
		MusicService.PlayFavorite(t, err =>
		{
			if (!string.IsNullOrEmpty(err))
			{
				Application.Current.MainWindow?.Dispatcher.BeginInvoke(new Action(() =>
				{
					if (Application.Current.MainWindow is MainWindow mw)
					{
						mw.ShowToast(err, false);
					}
				}));
			}
		});
	}

	/// <summary>把 _shown 里下一批（最多 BatchSize 条）画进列表。
	/// 首屏与滚动补页共用这一条路径，画法与原来的全量循环完全一致。</summary>
	private void AppendNextBatch()
	{
		for (int end = Math.Min(_renderedCount + BatchSize, _shown.Count); _renderedCount < end; _renderedCount++)
		{
			SavedTrack t = _shown[_renderedCount];
			// 补作者 / 时长：本地文件那两条是后话（匹配到才有的歌手、
			// 解码一次才有的总长），画行之前统一补齐一次。
			var copy = TrackRows.PrepareRow(t);
			_firstShown ??= copy;
			// 最近播放的行上补一个心形：点一下就进「我的收藏」。
			// 收藏状态按 UserLibrary.IsFavorite 现取，所以取消后行上的心形会自动变回空心。
			// 同样走 BuildSavedRow 带平台标签，和搜索页/收藏页保持一致。
			// 本地文件额外给个删除键：最近播放是「今天听过的」最容易反悔的列表，
			// 当时没在音乐库里注意到的废文件，在这里顺手就能清掉。
			RecentList.Items.Add(TrackRows.BuildSavedRow(copy, () => Play(copy), UserLibrary.IsFavorite(copy),
				() => UserLibrary.ToggleFavorite(copy),
				string.Equals(copy.Type, "local", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(copy.Path)
					? () => DeleteLocalTrack(copy)
					: null));
		}
	}

	/// <summary>滚动接近底部（200px 内）就补画下一批。ScrollChanged 只做阈值
	/// 判断不存状态：补完一批布局变高，离底部远了自然不再满足条件，连滚连补；
	/// 全部画完后 _renderedCount 追平 _shown.Count，进来也是直接返回。</summary>
	private void RecentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (_renderedCount >= _shown.Count)
		{
			return;
		}
		var sv = RecentScroll;
		// 初次布局时 ExtentHeight/ViewportHeight 可能还是 0，等真正有内容再判断。
		if (sv.ExtentHeight <= 0 || sv.ViewportHeight <= 0)
		{
			return;
		}
		if (sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset < NearBottomPixels)
		{
			AppendNextBatch();
		}
	}

	private void ClearRecentButton_Click(object sender, RoutedEventArgs e)
	{
		UserLibrary.ClearRecent();
	}

	/// <summary>删一首最近播放里的本地音乐：文件、收藏、最近记录一起清掉。</summary>
	private void DeleteLocalTrack(SavedTrack t)
	{
		if (!TrackDelete.RequestDelete(Window.GetWindow(this), t.Path ?? "", t.Title))
		{
			return;
		}
		// UserLibrary.RemoveRecent 会广播 Changed，RefreshList 由它触发。
		_needRefresh = true;
	}
}