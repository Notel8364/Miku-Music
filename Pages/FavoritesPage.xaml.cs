using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicBox;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class FavoritesPage : UserControl, IMusicPage, IMusicPageLeave
{
	// 列表是否过期，见 MainWindow.NavigateTo 的注释：导航回来时没变就别重建。
	private bool _needRefresh = true;

	// 搜索关键词和「清空」标志，见 OnNavigatedFrom 的注释。
	private string _searchFilter = "";
	private bool _suppressFilterSync;

	/// <summary>筛选后第一条命中的条目。回车 / 空格直接播它。</summary>
	private SavedTrack? _firstShown;

	/// <summary>按平台分出来的组（组头 + 行面板）。顺序 = 显示顺序，
	/// 吸顶和折叠都按这份表算。</summary>
	private readonly List<GroupUi> _groups = new();

	/// <summary>被用户折叠掉的组（按平台 Type 存）。页缓存在 MainWindow._pages 里，
	/// 所以翻页回来折叠状态还在；不落盘 —— 这是浏览状态，不是设置。</summary>
	private readonly HashSet<string> _collapsedGroups = new();

	/// <summary>当前是否处于搜索中。搜索时强制全部展开，折叠点击全部忽略。</summary>
	private bool _filtering;

	/// <summary>上一次重建时的收藏集合指纹。库变更事件（直拉流下载并入曲库、
	/// 切歌写播放记录）在收藏本身没变时也照发；指纹相同就跳过整页重建 ——
	/// 以前不判断，用户在收藏页点一首建议歌就看到列表白闪 + 滚动回弹一下。</summary>
	private string _lastFavSignature = "";

	/// <summary>上一次重建时生效的筛选词。只有筛选词没变的重建才保留滚动偏移；
	/// 敲键盘筛出来的新列表该从头看，照旧回顶。</summary>
	private string _lastBuiltFilter = "";

	/// <summary>一个平台分组的 UI。组头挂着位移变换做吸顶，行面板挂裁剪做「钻头下」。</summary>
	private sealed class GroupUi
	{
		public string Key = "";
		public StackPanel Root = null!;
		public Border Header = null!;
		public StackPanel Rows = null!;
		/// <summary>组头的吸顶位移。只动它不改布局，所以滚动时不会触发重排。</summary>
		public TranslateTransform HeaderShift = null!;
		public RotateTransform ChevronRotation = null!;
		/// <summary>行面板当前是否挂着「被组头盖住」的那条裁剪，用来避免反复赋 null。</summary>
		public bool Clipped;

		/// <summary>行收高/铺开动画的代数。连点组头时只有最新一轮的
		/// Completed 能收尾，上一轮被摘掉的时钟就算补发也不许碰状态。</summary>
		public int RowsAnimGen;
	}

	/// <summary>分组的显示顺序。没列到的类型（以后新加的来源）统一排最后，
	/// 同类型的组之间靠 OrderBy 的稳定性保持「谁先出现谁在前」。</summary>
	private static readonly string[] GroupOrder = { "local", "netease", "qq", "kugou", "bili" };

	public FavoritesPage()
	{
		InitializeComponent();
		UserLibrary.Changed += OnLibraryChanged;
		// 封面是歌词匹配完成后异步补上的，收藏条目本身没变，
		// UserLibrary.Changed 不会发；靠播放状态事件回来补画封面行。
		MusicService.StateChanged += OnMusicStateChanged;
		TrackRows.AccentChanged += OnAccentChanged;
		Loc.Default.LanguageChanged += OnLanguageChanged;
		// 后台把某首歌的时长探出来了：在屏幕上就重画一次，让它显示出来。
		LocalTrackMeta.Updated += OnLocalMetaUpdated;
		// 首次布局完成后算一次吸顶（ScrollChanged 在内容量出来之前不会开张）。
		Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateStickyHeaders));
	}

	/// <summary>换强调色：行里抄的旧颜色要重画才能变过来。</summary>
	private void OnAccentChanged()
	{
		_needRefresh = true;
		// 只有这一页真的在前台才画，强调色广播时隐藏页不用跟着重画。
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
		else
		{
			// 藏在后台时窗口可能被改过大小，吸顶位置会过期；重建被跳过时在这儿补算。
			Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateStickyHeaders));
		}
	}

	/// <summary>离开这一页就把搜索关键词清掉，规矩和音乐库页、最近播放页一致。</summary>
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
	}

	/// <summary>换界面语言：标题/按钮/空提示都是代码赋值的，得重刷一遍。</summary>
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
		// 收藏变动在播放、切歌、导入时都会触发，但多数时候收藏集合
		// 根本没变（下载并入曲库、写播放记录都会发 Changed）。
		// 以前不判断就整页重建：FavList.Items.Clear() 会把 extent 瞬间
		// 压到 0、滚动偏移被钳，再摆回行又吸顶重算 —— 用户看到的就是
		// 「收藏页回弹一下」。指纹相同直接收工，_needRefresh 也不置位
		// （下次导航同样不用重建）。
		// 事件可能从后台线程来，指纹要在 UI 线程读（Favorites 不加锁）。
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(RefreshIfOnScreen));
			return;
		}
		if (FavSignature() == _lastFavSignature)
		{
			return;
		}
		_needRefresh = true;
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
	/// 指纹里带着时长，所以 RefreshList 只在真的有变化时才会重建整页。</summary>
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
		if (FavSignature() == _lastFavSignature)
		{
			return;
		}
		_needRefresh = true;
		RefreshIfOnScreen();
	}

	/// <summary>本地文件的网易云封面到了之后补画一次。收藏条目本身没动，
	/// 所以还是先比指纹（里面带了封面版本号），没变化就什么都不做。</summary>
	private void OnMusicStateChanged()
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(OnMusicStateChanged));
			return;
		}
		if (FavSignature() == _lastFavSignature)
		{
			return;
		}
		_needRefresh = true;
		RefreshIfOnScreen();
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		// 显隐要在 _suppressFilterSync 的 return 之前更新：
		// 离页清空走的就是这条被压住的路径，漏掉的话回到本页时
		// 搜索框是空的、清除按钮却还挂着。
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
			// 行现在挂在各组的行面板里、不再是 FavList 的条目，
			// 所以自己收齐行再交给 TrackRows 画淡入。
			var rows = new List<FrameworkElement>();
			foreach (GroupUi g in _groups)
			{
				foreach (UIElement child in g.Rows.Children)
				{
					if (child is FrameworkElement fe)
					{
						rows.Add(fe);
					}
				}
			}
			TrackRows.AnimateRowsIn(rows);
		}
	}

	/// <summary>回车 / 空格 = 播第一条筛出来的。
	/// 这一页用的是 ItemsControl 没有选中态，所以直接拿第一条命中的播，
	/// 和音乐库里「第一条被选中」是同一个意思。</summary>
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
		// 重建前记下滚动位置：Items.Clear() 会把 extent 压到 0，偏移被钳成
		// 0，行摆回去也回不到原来的位置。布局跑完（Loaded 优先级）再还原。
		double keepOffset = FavScroll.VerticalOffset;
		var all = UserLibrary.Favorites;
		string filter = _searchFilter;
		string filterKey = TrackRows.NormalizeKey(filter);

		// 先筛再判空：「列表本来是空的」和「你筛的那首不在这页里」是两回事。
		var shown = new List<SavedTrack>(all.Count);
		foreach (SavedTrack t in all)
		{
			if (filterKey.Length == 0 || TrackRows.MatchesTrack(t, filter, filterKey))
			{
				shown.Add(t);
			}
		}

		bool filtering = filterKey.Length > 0;
		_filtering = filtering;
		SearchBox.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		EmptyText.Text = filtering ? Loc.Default["list_no_match"] : Loc.Default["fav_empty"];

		FavList.Items.Clear();
		_groups.Clear();
		_firstShown = shown.Count > 0 ? shown[0] : null;

		// 按平台分组：每组一个根 StackPanel（组头 + 行面板），整根作为 FavList 的条目。
		// 组内顺序就是收藏顺序；组间用固定顺序（local → netease → qq → kugou → bili），
		// 没列到的类型靠 OrderBy 的稳定性按首次出现排在已知平台后面。
		//
		// 行还是走 BuildSavedRow：带上平台标签，本地下载的和各平台在线的才分得出来；
		// 收藏列表里不放 ×，取消收藏点行上的心形（下面 onToggleFav），取消后这行自然就没了。
		// 但本地文件要留删除键 —— 收藏同步进来的本地条目只有这页能碰到，
		// 在线条目删不了平台服务器上的东西，不给。
		var grouped = shown
			.GroupBy(t => t.Type ?? "", StringComparer.Ordinal)
			.OrderBy(g => PlatformRank(g.Key))
			.ToList();
		foreach (IGrouping<string, SavedTrack> grp in grouped)
		{
			var g = new GroupUi { Key = grp.Key };
			g.Header = BuildGroupHeader(g, grp.Count(), grp.First());
			g.Rows = new StackPanel();
			// 折叠状态跟着重建落地：chevron 在 BuildGroupHeader 里已按
			// _collapsedGroups 摆好了，行的可见性要在同一步对齐，
			// 不然列表一重建就出「箭头朝右、行还展开着」的错位。
			g.Rows.Visibility = _filtering || !_collapsedGroups.Contains(g.Key)
				? Visibility.Visible
				: Visibility.Collapsed;
			foreach (SavedTrack t in grp)
			{
				// 补作者 / 时长：本地文件那两条是后话（匹配到才有的歌手、
				// 解码一次才有的总长），画行之前统一补齐一次。
				var copy = TrackRows.PrepareRow(t);
g.Rows.Children.Add(TrackRows.BuildSavedRow(copy, () => Play(copy), true, () => UserLibrary.ToggleFavorite(copy),
					string.Equals(copy.Type, "local", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(copy.Path)
						? () => DeleteLocalTrack(copy)
						: null,
					CanRenameSaved(copy) ? (newName) => RenameSaved(copy, newName) : null));
			}
			// 组头要压在行上面：吸顶时它是浮着的，行从它身下滚过去。
			Panel.SetZIndex(g.Header, 1);
			g.Root = new StackPanel();
			g.Root.Children.Add(g.Header);
			g.Root.Children.Add(g.Rows);
			_groups.Add(g);
			FavList.Items.Add(g.Root);
		}
		// 清空键只在真的有收藏时出现：空列表挂一个清空键既无意义，
		// 点了也只会把「本来就空」再清一遍。计数不挂按钮上（原同步按钮
		// 会显示「从云端同步 · N」），列表数量由分组头与状态行自己表达。
		ClearFavButton.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		// 筛不出东西的时候在底部补一句，别让人对着空列表猜是不是坏了。
		if (filtering && shown.Count == 0 && all.Count > 0)
		{
			StatusText.Text = Loc.Default.Format("list_count_shown", 0, all.Count);
		}
		// 整张列表刚重建过，量出来的组位置全作废；布局跑完再算一次吸顶。
		_lastFavSignature = FavSignature();
		// 筛选词没变的重建（库变更、语言、强调色、导航回来）保留原滚动位置；
		// 筛选词变了（敲键盘）照旧回顶 —— 新筛出来的列表该从头看。
		bool keep = string.Equals(filter, _lastBuiltFilter, StringComparison.Ordinal);
		_lastBuiltFilter = filter;
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
		{
			if (keep)
			{
				FavScroll.ScrollToVerticalOffset(keepOffset);
			}
			UpdateStickyHeaders();
		}));
	}

	/// <summary>收藏集合的显示相关指纹（含顺序）：Key 定身份，Title/Artist/
	/// DurationMs/Cover 是行上要画的字段 —— 元数据刷新时 Key 不变也得触发重建。</summary>
	private static string FavSignature()
	{
		var all = UserLibrary.Favorites;
		var sb = new System.Text.StringBuilder(all.Count * 40);
		// 本地文件的封面不落在条目上（按路径去 MusicService 里查），
		// 所以用全局版本号把「刚补到封面」也算进指纹。
		sb.Append(MusicService.LocalCoverVersion).Append('\n');
		foreach (SavedTrack t in all)
		{
			// 时长也进指纹：后台探出总长之后，这一页才会重画把时长显示出来。
			// 记录里没有时长时（没播过的本地文件）去 LocalTrackMeta 问一次，
			// 那儿有缓存，探不到就记 0，不会反复去开文件。
			long dur = t.DurationMs > 0 ? t.DurationMs : LocalTrackMeta.GetDuration(t.Path);
			sb.Append(t.Key).Append('|').Append(t.Title).Append('|')
			  .Append(t.Artist).Append('|').Append(dur).Append('|')
			  .Append(t.Cover).Append('\n');
		}
		return sb.ToString();
	}

	private static int PlatformRank(string type)
	{
		int i = Array.IndexOf(GroupOrder, type);
		return i >= 0 ? i : GroupOrder.Length;
	}

	/// <summary>分组头：[折叠箭头][平台标签][右侧曲数]，整条可点 = 折叠/展开该组。</summary>
	private Border BuildGroupHeader(GroupUi g, int count, SavedTrack first)
	{
		var chevron = new TextBlock
		{
			// E70D = 下箭头。展开朝下（0°），折叠逆时针转 90° 指右。
			Text = "\uE70D",
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 12,
			Margin = new Thickness(0, 0, 8, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
		bool expanded = _filtering || !_collapsedGroups.Contains(g.Key);
		g.ChevronRotation = new RotateTransform(expanded ? 0 : -90);
		chevron.RenderTransform = g.ChevronRotation;
		// 旋转绕字形中心转。默认绕 (0,0) = 左上角：折叠转 -90° 后字形整体
		// 往上窜一个字宽、往右窜一个字高，戳出组头上沿悬到上一组的行上。
		chevron.RenderTransformOrigin = new Point(0.5, 0.5);

		// 标签和颜色直接沿用行里的平台标签：分组头和行看起来就是一套东西。
		(string label, Brush brush)? tag = TrackRows.SavedEngineTag(first);
		string label = tag?.label ?? "";
		if (string.IsNullOrEmpty(label))
		{
			// 以后新增的来源没配标签时不给空块，把 Type 原样摆上去兜底。
			label = string.IsNullOrEmpty(g.Key) ? "?" : g.Key;
		}
		Border chip = TrackRows.MakeChip(label, tag?.brush ?? new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));

		// 曲数不占 loc key：就是个数字，翻译成「12 首」反而和右边按钮上的计数重复。
		var countText = new TextBlock
		{
			Text = count.ToString(),
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Right
		};
		countText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");

		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		Grid.SetColumn(chevron, 0);
		Grid.SetColumn(chip, 1);
		Grid.SetColumn(countText, 2);
		grid.Children.Add(chevron);
		grid.Children.Add(chip);
		grid.Children.Add(countText);

		var header = new Border
		{
			Child = grid,
			Padding = new Thickness(10, 6, 10, 6),
			CornerRadius = new CornerRadius(6),
			Cursor = Cursors.Hand,
			Background = Brushes.Transparent,
			// 上边距给组与组之间留缝；吸顶的「原位」计算也认这个边距。
			Margin = new Thickness(0, 6, 0, 2)
		};
		g.HeaderShift = new TranslateTransform(0, 0);
		header.RenderTransform = g.HeaderShift;
		header.MouseEnter += (_, _) => header.SetResourceReference(Border.BackgroundProperty, "SubtleFillHoverBrush");
		header.MouseLeave += (_, _) => header.Background = Brushes.Transparent;
		header.MouseLeftButtonDown += (_, _) => ToggleGroup(g.Key);
		return header;
	}

	/// <summary>折叠 / 展开一个平台组。搜索中强制全展开，这时点组头不响应。</summary>
	private void ToggleGroup(string key)
	{
		if (_filtering)
		{
			return;
		}
		GroupUi? g = _groups.Find(x => x.Key == key);
		if (g == null)
		{
			return;
		}
		bool collapse = !_collapsedGroups.Contains(key);
		if (collapse)
		{
			_collapsedGroups.Add(key);
		}
		else
		{
			_collapsedGroups.Remove(key);
		}
		SetGroupExpanded(g, !collapse);
		// 折叠改的是可见性，布局下一帧才跑完，吸顶得等量完再算。
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateStickyHeaders));
	}

	private static void SetGroupExpanded(GroupUi g, bool expanded)
	{
		// WPF 正角是顺时针，下箭头 -90° 就朝右。Angle 上可能还挂着上一轮
		// HoldEnd 的动画，先把当前显示值抄成基值再起新动画，转起来才不跳。
		double current = g.ChevronRotation.Angle;
		g.ChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
		g.ChevronRotation.Angle = current;
		g.ChevronRotation.BeginAnimation(RotateTransform.AngleProperty,
			new DoubleAnimation(expanded ? 0 : -90, TimeSpan.FromMilliseconds(150))
			{
				From = current,
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			});

		// 行面板的收高/铺开动画（用户要的「折叠缩进」）：只动 MaxHeight 这个
		// 布局量，不直接切 Visibility —— 直接切是瞬变，加了动画才看得出
		// 「行缩进组头底下」。ClipToBounds 只在动画期间开着：收高过程中
		// 子行超出布局边界，不裁的话会悬在半空；静止状态关掉，
		// 免得搜索淡入那几像素的上浮位移被边界裁到；动画收尾处再关回去。
		var rows = g.Rows;
		int gen = ++g.RowsAnimGen;
		rows.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
		rows.ClipToBounds = true;
		if (expanded)
		{
			// 本来就是展开的（状态和折叠表错位时），别铺一次闪一下。
			if (rows.Visibility == Visibility.Visible && rows.ActualHeight > 0
				&& rows.MaxHeight == double.PositiveInfinity)
			{
				rows.ClipToBounds = false;
				return;
			}
			// 起点 = 当前真实高度：从收了一半的状态接着铺就是从一半铺到满，
			// 连点组头不会跳。完全折叠时 ActualHeight 是 0，正好从 0 长起来。
			double start = rows.Visibility == Visibility.Visible ? rows.ActualHeight : 0;
			rows.Visibility = Visibility.Visible;
			// 量自然高度。MaxHeight 得先放开 —— 布局属性会把 DesiredSize
			// 夹到自己的上限，拿 0 去量只会量出 0。
			rows.MaxHeight = double.PositiveInfinity;
			rows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			double target = rows.DesiredSize.Height;
			if (target <= 1)
			{
				rows.ClipToBounds = false;
				return;
			}
			if (start > target)
			{
				start = target;
			}
			rows.MaxHeight = start;
			var grow = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(180))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			grow.Completed += (_, _) =>
			{
				if (g.RowsAnimGen != gen)
				{
					return;
				}
				// 先摘动画再放开上限：两句在同一帧里跑完，中间不会渲染一次，
				// 不存在「回落到基值 start 那一帧」。
				rows.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
				rows.MaxHeight = double.PositiveInfinity;
				rows.ClipToBounds = false;
			};
			rows.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);
		}
		else
		{
			double from = rows.ActualHeight;
			if (rows.Visibility != Visibility.Visible || from <= 1)
			{
				rows.Visibility = Visibility.Collapsed;
				rows.MaxHeight = double.PositiveInfinity;
				rows.ClipToBounds = false;
				return;
			}
			rows.MaxHeight = from;
			var shrink = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(150))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			shrink.Completed += (_, _) =>
			{
				if (g.RowsAnimGen != gen)
				{
					return;
				}
				// 先切 Collapsed 再摘动画：Visibility 一关高度就是 0，
				// 摘动画回落到基值 from 也不会闪出来。
				rows.Visibility = Visibility.Collapsed;
				rows.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
				rows.MaxHeight = double.PositiveInfinity;
				rows.ClipToBounds = false;
			};
			rows.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
		}
	}

	private void FavScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		UpdateStickyHeaders();
	}

	/// <summary>分组头吸顶：要滚出视野的组头停在内容区顶部；下一个组的头升到
	/// 槽位上方后停在前一个组头的下沿，一个接一个向下叠起来，谁也不会被谁盖住。
	/// 只有往回滚时才按「后停的先走」依次放行，组头跟着内容自然滑下去。
	///
	/// 全程只做两件不改布局的事：给组头加位移、给行面板挂裁剪。
	/// 行被按住的组头盖住的那一条不画，看起来就是「行从头底下钻过去」；
	/// 因为不动布局，这段和滚动条互不触发、不会打架。</summary>
	private void UpdateStickyHeaders()
	{
		if (_groups.Count == 0 || !FavScroll.IsLoaded || FavScroll.ActualHeight <= 0)
		{
			return;
		}
		// 吸在「内容区」顶端，不是控件最外沿：滚出视口的内容会被内容区的
		// 裁剪线切掉，贴到 y=0 的话组头的上半截就没了（本页 Padding=6）。
		double stickyTop = FavScroll.Padding.Top + FavScroll.BorderThickness.Top;
		// 叠放槽位：第一个组头顶到 stickyTop，往后每个组头的槽位起点 =
		// 上一个组头的下沿再留一个上边距的缝（各组边距一致，取当前组的）。
		double stackY = stickyTop;
		foreach (GroupUi g in _groups)
		{
			double headerH = g.Header.ActualHeight;
			double rootH = g.Root.ActualHeight;
			if (headerH <= 0 || rootH <= 0)
			{
				continue;
			}
			// 量根面板（它自己没有变换）再加上组头的上边距 = 组头的原位。
			// 直接量组头会把上一轮加的位移又算进去，一轮轮地漂。
			double naturalTop = g.Root.TranslatePoint(new Point(0, 0), FavScroll).Y + g.Header.Margin.Top;
			// 原位还没升到槽位上方就保持自然排列；升上去了才压进槽位。
			// 这里刻意不设「最多推到本组底部」的上限：设了的话，下一个组头
			// 刚够到槽位的那一刻恰好是本组组头被上限收走的那一刻，两个头
			// 永远凑不到一起，叠不起来（旧版组头顶着视口逐个滑走就是这么来的）。
			//
			// 槽位排不进视口（叠得太高 + 窗口很小）就不叠：硬压过去会把组头
			// 推到下沿以外，它的裁剪带跟着盖满整屏，行全没了。不叠就回到
			// 自然排列（位移 0、摘裁剪），最坏难看一帧，不会丢内容。
			bool canPark = stackY + headerH <= FavScroll.ActualHeight;
			double y = canPark && naturalTop < stackY ? stackY : naturalTop;
			double dy = y - naturalTop;
			g.HeaderShift.Y = dy;
			UpdateGroupClip(g, dy, y + headerH);
			stackY = y + headerH + g.Header.Margin.Top;
		}
	}

	/// <summary>组头被按住（dy>0）时，把行面板上被组头盖住的那一条裁掉；
	/// 够不着或没被盖就摘掉裁剪（挂 null 比留着一条空几何省一次重绘）。</summary>
	private void UpdateGroupClip(GroupUi g, double dy, double headerBottom)
	{
		if (dy <= 0)
		{
			if (g.Clipped)
			{
				g.Rows.Clip = null;
				g.Clipped = false;
			}
			return;
		}
		double rowsTop = g.Rows.TranslatePoint(new Point(0, 0), FavScroll).Y;
		double w = g.Rows.ActualWidth;
		double h = g.Rows.ActualHeight;
		// 组头下沿还没够到行面板顶部 = 没有行被盖住，不用裁。
		// headerBottom ≤ 0 说明组头整个在视口上方，同样没得裁；顺带保证下面
		// band 的高度非负 —— Rect 的高度是负数会直接抛异常。
		if (h <= 0 || w <= 0 || headerBottom <= Math.Max(0.0, rowsTop))
		{
			if (g.Clipped)
			{
				g.Rows.Clip = null;
				g.Clipped = false;
			}
			return;
		}
		// 屏幕坐标 [0, 组头下沿] 换算成行面板自己的坐标系再挖掉。
		// 从 0 起裁而不是从内容区顶端起：多裁那一小条反正也看不见，
		// 而且不依赖「视口到底在哪条线上裁」这种模板细节。
		//
		// Rect 第四个参数是「高度」不是「终点」：起点 y = -rowsTop，
		// 要覆盖到屏幕 headerBottom 处，高度就等于 headerBottom 本身。
		// 以前写成 headerBottom - rowsTop（把终点 y 当成了高度），rowsTop < 0
		// （组头吸住、面板顶端滚出视口）时会多裁 |rowsTop| 那么大一条，
		// 而 |rowsTop| 随滚动越滚越大 —— 滚深一点整屏的行都被裁没了。
		var band = new Rect(-1000, -rowsTop, w + 2000, headerBottom);
		g.Rows.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
			new RectangleGeometry(new Rect(0, 0, w, h)),
			new RectangleGeometry(band));
		g.Clipped = true;
	}

	/// <summary>删一首收藏里的本地音乐：文件、收藏、最近播放里的残条目一起清掉。</summary>
	private void DeleteLocalTrack(SavedTrack t)
	{
		if (!TrackDelete.RequestDelete(Window.GetWindow(this), t.Path ?? "", t.Title))
		{
			return;
		}
// UserLibrary.RemoveFavorite 会广播 Changed，RefreshList 由它触发；
		// 这里只补一个脏标记，万一广播在别的线程上被跳过也不会漏画。
		_needRefresh = true;
	}

	/// <summary>只有本地文件（有实体路径）和 B 站下载（在下载缓存里有实体文件）
	/// 才能双击改名。平台在线的歌挂在服务器上，改了名字只会让收藏里对不上。</summary>
	private static bool CanRenameSaved(SavedTrack t)
	{
		if (t == null)
		{
			return false;
		}
		if (string.Equals(t.Type, "local", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Path) && System.IO.File.Exists(t.Path))
		{
			return true;
		}
		return string.Equals(t.Type, "bili", StringComparison.Ordinal)
			&& string.IsNullOrWhiteSpace(MusicService.GetCachedDownload(t.Title)) == false;
	}

	/// <summary>重命名一条收藏：B 站下载按旧标题找缓存文件改名，本地文件直接改路径。
	/// 成功后同步 UserLibrary 里的 Title / 路径并重刷列表。</summary>
	private void RenameSaved(SavedTrack t, string newName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, t.Title, StringComparison.Ordinal))
			{
				return;
			}
			string? created;
			bool isBili = string.Equals(t.Type, "bili", StringComparison.OrdinalIgnoreCase);
			if (isBili)
			{
				created = MusicService.RenameDownloaded(t.Title, newName);
			}
			else
			{
				created = MusicService.RenameFile(t.Path, newName);
			}
			string clean = MusicService.CleanFileName(newName);
			if (string.IsNullOrEmpty(created) || clean.Length == 0)
			{
				Toast("重命名失败：名称无效或目标已存在");
				return;
			}
			if (isBili)
			{
				UserLibrary.Retitle(t.Key, clean);
			}
			else
			{
				UserLibrary.ApplyLocalRename(t.Path, created, clean);
			}
			Toast("已重命名为 " + clean);
			_needRefresh = true;
			RefreshList();
		}
		catch (Exception ex)
		{
			Toast("重命名失败");
			LogHelper.LogError(ex, "收藏重命名失败: " + t.Title);
		}
	}

	private static void Toast(string msg)
	{
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(msg, false);
		}
	}

	private static void Play(SavedTrack t)
	{
		// 登记播放上下文：「启动时自动播放」按它找回收藏列表随机续播。
		MusicService.PendingPlayContext = new PlayContext { Page = "fav", ListKey = "fav" };
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

	private void ClearFavButton_Click(object sender, RoutedEventArgs e)
	{
		// UserLibrary.ClearFavorites 落盘并广播 Changed，列表由 OnLibraryChanged 重建。
		UserLibrary.ClearFavorites();
	}

}