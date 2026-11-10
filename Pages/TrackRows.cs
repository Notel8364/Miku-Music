using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public static class TrackRows
{
	// 行内尺寸只在这里定义一次：封面、行内边距、整行高度必须彼此自洽。
	// 各页原来各自写 54 / 38 之类的魔法数，改一处忘一处就会出现行与行错位。
	public const double CoverSize = 38;
	public const double RowContentPadding = 5;
	public const double RowVerticalMargin = 2;
	public const double RowHeight = CoverSize + RowContentPadding * 2 + RowVerticalMargin * 2; // = 54

	public static Brush AccentBrush { get; set; } = ThemeManager.Accent();

	/// <summary>画在强调色块上面的字色。强调色选纯白/纯黑时，
	/// 固定白字会变成白底白字，所以跟着强调色亮度走。</summary>
	public static Brush OnAccentBrush { get; set; } = Brushes.White;

	/// <summary>强调色变了之后要重建行、让它们换用新颜色。
	/// 行是 BuildRow 时把 AccentBrush 抄进去的，改静态字段改不到已经建好的行。
	/// 以前靠 OnNavigatedTo 无条件重建兜着；改成「数据没变就不重建」之后
	/// 就必须显式通知，否则换完强调色返回上一页还是旧颜色的播放键。</summary>
	public static event Action? AccentChanged;

	/// <summary>换完强调色后由主窗调用，通知所有用本类画行的页面重建。</summary>
	public static void RaiseAccentChanged() => AccentChanged?.Invoke();

	// ==================== 列表筛选关键词 ====================

	/// <summary>把字符串压成「只由字母数字和汉字组成」的匹配键。
	/// "C U Again (feat. X)" -> "cuagainfeatx"，于是搜 "cu" 能命中。
	/// 音乐库、最近播放、我的收藏三个页面的搜索框共用这一套，
	/// 免得每加一个搜索框就复制一份、再各改各的。</summary>
	/// <summary>筛选后新出来的行依次淡入、微微上浮。
	///
	/// 只做前 <paramref name="maxRows"/> 行：整屏一起淡入看着像「闪了一下」，
	/// 而一首一首点亮的顺序感才有「筛出东西了」的反馈。
	/// 每行延迟 14ms、自身 180ms，12 行封顶 —— 再多就变成等动画而不是看结果。
	///
	/// 用 Loaded 优先级推迟一帧执行：刚 Items.Add 完的那一刻
	/// ItemContainerGenerator 还没给这些条目生成容器，
	/// 这时候取 ContainerFromItem 全是 null，动画等于没做。</summary>
	public static void AnimateFilteredIn(ItemsControl list, int maxRows = 12)
	{
		if (list == null || !list.IsLoaded)
		{
			return;
		}
		list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
		{
			var rows = new List<FrameworkElement>();
			foreach (object item in list.Items)
			{
				if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement row)
				{
					rows.Add(row);
				}
			}
			AnimateRowsIn(rows, maxRows);
		});
	}

	/// <summary>同 <see cref="AnimateFilteredIn"/>，但直接收行元素本身。
	/// 收藏页把行按平台分组装进自己的 StackPanel（不再是 ItemsControl 的条目），
	/// 拿不到 ItemContainerGenerator，所以把动画体抽出来共用。
	/// 元素是代码里直接 new 出来的、不用等容器生成，因此这里不需要再推迟一帧。</summary>
	public static void AnimateRowsIn(IReadOnlyList<FrameworkElement> rows, int maxRows = 12)
	{
		if (rows == null || rows.Count == 0)
		{
			return;
		}
		int index = 0;
		foreach (FrameworkElement row in rows)
		{
			if (index++ >= maxRows)
			{
				break;
			}
			if (row == null)
			{
				continue;
			}
			// 上一轮动画还按在 Opacity 上（HoldEnd）的话不摘掉，
			// 新的那 0 就不会生效，行会停在透明状态直到新动画接管。
			row.BeginAnimation(UIElement.OpacityProperty, null);
			row.Opacity = 0;
			// RenderTransform 已经有别人的东西就不动它。上浮只是锦上添花，
			// 覆盖掉页面自己摆的变换属于副作用大于收益。
			TranslateTransform? slide = row.RenderTransform as TranslateTransform;
			if (row.RenderTransform == null)
			{
				slide = new TranslateTransform();
				row.RenderTransform = slide;
			}
			if (slide != null)
			{
				slide.BeginAnimation(TranslateTransform.YProperty, null);
				slide.Y = 10;
				slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200))
				{
					BeginTime = TimeSpan.FromMilliseconds(index * 14),
					EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
					FillBehavior = FillBehavior.HoldEnd,
				});
			}
			row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
			{
				BeginTime = TimeSpan.FromMilliseconds(index * 14),
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
				FillBehavior = FillBehavior.HoldEnd,
			});
		}
	}

	public static string NormalizeKey(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		var sb = new System.Text.StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				sb.Append(char.ToLowerInvariant(c));
			}
		}
		return sb.ToString();
	}

	/// <summary>关键词是否命中这段文字。
	/// 两条路都算命中：
	/// 1) 原样包含 —— 保留「搜 flac 能搜到 .flac」这类行为；
	/// 2) 压掉分隔符后包含 —— 让 "cu" 能命中 "C U Again"。
	/// 第 2 条只在第 1 条失败时才试，所以不会把行为弄反。
	/// filterKey 传 <see cref="NormalizeKey"/> 出来的结果，一行文本只算一次。</summary>
	public static bool MatchesText(string haystack, string filter, string filterKey)
	{
		if (string.IsNullOrEmpty(haystack))
		{
			return false;
		}
		if (filter.Length > 0 && haystack.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		if (filterKey.Length == 0)
		{
			return false;
		}
		return NormalizeKey(haystack).Contains(filterKey, StringComparison.Ordinal);
	}

	/// <summary>搜索框是否命中一首曲目。歌名、歌手、平台标签都算。</summary>
	public static bool MatchesTrack(SavedTrack t, string filter, string filterKey)
	{
		return MatchesText(t.Title, filter, filterKey)
			|| MatchesText(t.Artist, filter, filterKey)
			|| MatchesText(t.Type, filter, filterKey);
	}

	private static string? _playingPath;
	/// <summary>当前播放曲目的 Key（type:id / local:路径）。在线条目的行没有
	/// 稳定的本地路径（播的是流/临时文件，行里存的是平台条目），只按路径
	/// 匹配的话在线行永远亮不了 —— 路径、Key 任一命中就算「正在播放」。</summary>
	private static string? _playingKey;

	/// <summary>当前正在播放的路径。各行自己订阅这个事件去改自己的描边/底色，
	/// 不用页面拿着行控件的引用到处手动刷新。</summary>
	public static event Action? PlayingChanged;

	/// <summary>切换/清空「正在播放」高亮。传 null 或不存在的路径等于取消高亮。
	/// key 可选：给了就同时按 Key 匹配（在线行的 Path 对不上播放路径时的救星）。</summary>
	public static void SetPlayingTrack(string? path, string? key = null)
	{
		_playingPath = string.IsNullOrWhiteSpace(path) ? null : path;
		_playingKey = string.IsNullOrWhiteSpace(key) ? null : key;
		PlayingChanged?.Invoke();
	}

	/// <summary>从命中的元素往上找最近的 Button：用来判断这次点击是不是落在按钮上。
	/// 双击卡片时按钮自己响应，不然会「双击 = 按钮点两次 + 行播一次」。</summary>
	private static Button? FindAncestorButton(DependencyObject source)
	{
		DependencyObject? cur = source;
		while (cur != null)
		{
			if (cur is Button btn)
			{
				return btn;
			}
			cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(cur)
				: LogicalTreeHelper.GetParent(cur);
		}
		return null;
	}

	/// <summary>收藏/最近播放里 SavedTrack 的平台标签。
	/// 收藏和最近播放存的是 SavedTrack，拿不到原来的 Track 对象，
	/// 但 Type 字段记了来源，所以照样能显示平台。
	/// 文案统一走 Loc（6 语言），颜色跟平台品牌：汽水用「汽水绿」
	///（亮色主题用深青保证白底可读，深色/黑底用亮青）。</summary>
	public static (string Label, Brush Brush)? SavedEngineTag(SavedTrack track)
	{
		if (track == null)
		{
			return null;
		}
		return track.Type switch
		{
			"netease" => (Loc.Default["plat_netease"], new SolidColorBrush(Color.FromRgb(0xC2, 0x0C, 0x0C))),
			"kugou" => (Loc.Default["plat_kugou"], new SolidColorBrush(Color.FromRgb(0x2C, 0xA5, 0xF6))),
			"qq" => (Loc.Default["plat_qq"], new SolidColorBrush(Color.FromRgb(0x31, 0xC2, 0x7C))),
			"bili" => (Loc.Default["plat_bili"], new SolidColorBrush(Color.FromRgb(0xFB, 0x72, 0x99))),
			"soda" => (Loc.Default["plat_soda"], SodaAccentBrush()),
			"local" => (Loc.Default["plat_local"], new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))),
			_ => null
		};
	}

	/// <summary>汽水平台的品牌强调色（汽水绿）。
	/// 官方视觉是黑底 + 青绿：深色/黑底下用亮青 #24E3C5，
	/// 亮色（白底）下亮青对比度只有 ~1.6:1 太浅，换成同色相的深青 #0C7A6A
	///（对比度 ≈ 4.6:1）。文字标签/角标/勾选高亮都从这里拿，改一处全平台同步。</summary>
	public static Brush SodaAccentBrush()
	{
		bool dark = true;
		try
		{
			dark = ThemeManager.IsDark;
		}
		catch
		{
			// 主题管理器还没起来时按深色处理（启动窗口是深色底）。
		}
		return new SolidColorBrush(dark
			? Color.FromRgb(0x24, 0xE3, 0xC5)   // 汽水绿（暗底）
			: Color.FromRgb(0x0C, 0x7A, 0x6A)); // 深青（白底可读）
	}

	/// <summary>汽水平台的小图标（占位徽章）：圆角方块 + 青绿渐变底 +
	/// 白色音符（Segoe MDL2 \uE8D6，矢量字体符号，任意 DPI 不糊）。
	/// 取代之前通用的灰底音符占位 —— 歌单卡片角标/无封面歌单行都用它。
	/// 图标本身不透明、无网络请求，「显示封面图片」总闸关闭时也照常显示
	///（它是图标不是封面）。size 为像素边长。</summary>
	public static FrameworkElement SodaIconBadge(double size)
	{
		// 深浅主题下渐变两端都取汽水绿系：暗底亮青→深青收边，白底深青→深墨青，
		// 保证白色音符在两种主题下都有足够对比度。
		bool dark = true;
		try
		{
			dark = ThemeManager.IsDark;
		}
		catch
		{
			// 主题未就绪按深色（见 SodaAccentBrush）。
		}
		Color c1 = dark ? Color.FromRgb(0x2A, 0xF0, 0xD2) : Color.FromRgb(0x10, 0x8C, 0x7A);
		Color c2 = dark ? Color.FromRgb(0x0C, 0x6B, 0x5E) : Color.FromRgb(0x08, 0x4F, 0x45);
		return new Border
		{
			Width = size,
			Height = size,
			CornerRadius = new CornerRadius(size * 0.22),
			Background = new LinearGradientBrush(c1, c2, 90),
			Child = new TextBlock
			{
				Text = "\uE8D6", // Segoe MDL2 Assets: MusicNote
				FontFamily = new FontFamily("Segoe MDL2 Assets"),
				FontSize = size * 0.42,
				Foreground = Brushes.White,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
	}

	/// <summary>平台来源的展示名（播放条「来自 XX 平台」、最近播放过滤勾选共用）。
	/// type 与 SavedTrack.Type / TrackRows 平台标签同口径；
	/// 返回 null 表示未知平台（调用方自行隐藏该行）。</summary>
	public static string? PlatformDisplayName(string? type)
	{
		return type?.Trim().ToLowerInvariant() switch
		{
			"netease" => Loc.Default["plat_netease"],
			"qq" => Loc.Default["plat_qq"],
			"kugou" => Loc.Default["plat_kugou"],
			"bili" => Loc.Default["plat_bili"],
			"soda" => Loc.Default["plat_soda"],
			"local" => Loc.Default["plat_local"],
			_ => null
		};
	}

	/// <summary>收藏/最近播放用的一行。跟搜索页一样给平台标签，
	/// 不然本地下载的和各平台在线的歌混在一起完全分不出来源。</summary>
	public static Grid BuildSavedRow(SavedTrack track, Action onPlay, bool isFav, Action? onToggleFav, Action? onRemove, Action<string>? onRename = null)
	{
		(string label, Brush brush)? tag = SavedEngineTag(track);
		// 副标题只用歌手。本地文件往往没有歌手字段，
		// 早先的写法是拿 Title 顶上，结果标题和副标题一模一样，白占一行。
		string sub = track.Artist ?? "";
		// bilibili 收藏源存的是视频 UP 主名，显式标出来，跟歌手的含义区分开。
		if (sub.Length > 0 && string.Equals(track.Type, "bili", StringComparison.OrdinalIgnoreCase))
		{
			sub = "UP主 " + sub;
		}
		if (track.DurationMs > 0)
		{
			sub += (sub.Length > 0 ? "  ·  " : "") + FormatDuration(track.DurationMs);
		}
		return BuildRow(new TrackRowSpec
		{
			Track = track,
			SubOverride = sub,
			OnPlay = onPlay,
			IsFavorite = isFav,
			OnToggleFavorite = onToggleFav,
			OnRemove = onRemove,
			EngineLabel = tag?.label,
			EngineBrush = tag?.brush,
			OnRename = onRename
		});
	}

	/// <summary>把一条记录的「作者 / 时长」补齐，返回一份**副本**，原记录不动。
	///
	/// 本地文件那条记录里通常只有路径和文件名，作者和时长都是后来才有的：
	///   · 作者 —— 歌词 / 封面匹配到平台之后，那一版的歌手才写进 MusicService；
	///   · 时长 —— 播过的歌记在 SavedTrack 里，没播过的要解码文件才拿得到
	///     （见 LocalTrackMeta，探到之后会广播一次，让当前这一页重画）。
	/// 所以每画一行都要再问一次：这次可能已经有了。
	///
	/// 返回副本而不是直接改：收藏 / 最近播放的条目是共用的对象，
	/// 就地改会让别处（底栏、其它页面）看到改了一半的状态。</summary>
	public static SavedTrack PrepareRow(SavedTrack src)
	{
		// 调用方都是遍历集合拿到的条目，不会是 null；留着这判断只是别在
		// 渲染路径上抛出来。src! 是把「已知非空」说给编译器听。
		if (src == null)
		{
			// 以前写 return src!：用「非空断言」把一个 null 伪装成合法返回值，
			// 调用方拿到 null 之后在更远的地方炸，栈里已经看不出源头。
			throw new ArgumentNullException(nameof(src));
		}
		if (!string.Equals(src.Type, "local", StringComparison.OrdinalIgnoreCase)
			|| string.IsNullOrWhiteSpace(src.Path))
		{
			return src;
		}
		var copy = new SavedTrack
		{
			Type = src.Type,
			Id = src.Id,
			Title = src.Title,
			Artist = src.Artist,
			DurationMs = src.DurationMs,
			Cover = src.Cover,
			Path = src.Path
		};
		// 作者空着时那一行会退化成「只有标题」，这里补上匹配到的歌手；
		// 补不上就留空，绝不拿文件名顶替（那正是「歌名显示两遍」的来源）。
		if (string.IsNullOrWhiteSpace(copy.Artist))
		{
			copy.Artist = MusicService.GetLocalMatchedArtist(copy.Path);
		}
		if (copy.DurationMs <= 0)
		{
			copy.DurationMs = LocalTrackMeta.GetDuration(copy.Path);
		}
		return copy;
	}

	/// <summary>标题前的小标签（平台名 / VIP）。收藏页的分组头也用它当平台名，
	/// 保证分组头的标签和行里的标签一个样式。</summary>
	public static Border MakeChip(string text, Brush background)
	{
		return new Border
		{
			Background = background,
			CornerRadius = new CornerRadius(4),
			Padding = new Thickness(6, 1.5, 6, 1.5),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 6, 0),
			Child = new TextBlock
			{
				Text = text,
				FontSize = 10.5,
				FontWeight = FontWeights.SemiBold,
				Foreground = OnAccentBrush
			}
		};
	}

	/// <summary>BuildRow 的参数对象。
	///
	/// 原来是 16 个位置参数（track, subOverride, onPlay, isFav, onToggleFav,
	/// onRemove, onComment, onDownload, engineLabel, engineBrush, highlight,
	/// onCommentIcon, onCommentTip, secondChipLabel, secondChipBrush, onRename），
	/// 调用点要传一串 null 才能对齐到后面某个参数，读代码的人根本分不清
	/// 「第 9 个 null 是 onDownload 还是 engineBrush」。改成对象初始化器后，
	/// 每个可选行为都带名字，新增字段也不会再影响已有调用点。</summary>
	public sealed class TrackRowSpec
	{
		public required SavedTrack Track { get; init; }

		public required Action OnPlay { get; init; }

		public string? SubOverride { get; init; }

		public bool IsFavorite { get; init; }

		public Action? OnToggleFavorite { get; init; }

		public Action? OnRemove { get; init; }

		public Action? OnComment { get; init; }

		public Action? OnDownload { get; init; }

		public string? EngineLabel { get; init; }

		public Brush? EngineBrush { get; init; }

		public string? Highlight { get; init; }

		public string? OnCommentIcon { get; init; }

		public string? OnCommentTip { get; init; }

		public string? SecondChipLabel { get; init; }

		public Brush? SecondChipBrush { get; init; }

		public Action<string>? OnRename { get; init; }
	}

	public static Grid BuildRow(TrackRowSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		return BuildRowCore(spec.Track, spec.SubOverride, spec.OnPlay, spec.IsFavorite,
			spec.OnToggleFavorite, spec.OnRemove, spec.OnComment, spec.OnDownload,
			spec.EngineLabel, spec.EngineBrush, spec.Highlight, spec.OnCommentIcon,
			spec.OnCommentTip, spec.SecondChipLabel, spec.SecondChipBrush, spec.OnRename);
	}

	/// <summary>真正的建行实现：只由 <see cref="BuildRow(TrackRowSpec)"/> 调用。
	/// 对外统一走参数对象，类内保留扁平参数是为了不动这 300 多行控件树代码。</summary>
	private static Grid BuildRowCore(SavedTrack track, string? subOverride, Action onPlay, bool isFav, Action? onToggleFav, Action? onRemove = null, Action? onComment = null, Action? onDownload = null, string? engineLabel = null, Brush? engineBrush = null, string? highlight = null, string? onCommentIcon = null, string? onCommentTip = null, string? secondChipLabel = null, Brush? secondChipBrush = null, Action<string>? onRename = null)
	{
		// 行高做矮一点：封面 46→38、上下间距 7→5，一屏能多露出三四行，
		// 搜索结果基本不用再拖滚动条（用户要求「高度小一点都展示在一个页内」）。
		var grid = new Grid { Margin = new Thickness(0, RowVerticalMargin, 0, RowVerticalMargin) };
		// 明确要求拉满宽度。中间那列是 Star，靠它吃掉多余空间把右侧按钮推到行尾。
		// 一旦这行被放进没撑开的容器里（StackPanel、ListBoxItem 没设 Stretch），
		// Star 列会缩成标题的宽度，播放键就紧贴标题末尾跑，不再是整齐的一列。
		grid.HorizontalAlignment = HorizontalAlignment.Stretch;
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		// 本地文件本身没有封面字段；歌词匹配到网易云后会在 MusicService
		// 里留一份「路径 → 专辑图」，曲库/收藏/最近播放都靠它把音符占位换掉。
		string coverUrl = track.Cover;
		if (string.IsNullOrWhiteSpace(coverUrl)
			&& string.Equals(track.Type, "local", StringComparison.Ordinal))
		{
			coverUrl = MusicService.GetLocalMatchedCover(track.Path);
		}
		// 封面区只建「占位底色 + 空图片」，不在这里发请求：一页几百行
		// 同时打网络是封面最慢出现的根源。真正加载由 AttachLazyCover
		// 在行进入所属 ScrollViewer 的视口后发起（见类尾的懒加载区）。
		FrameworkElement coverHost = BuildCover(coverUrl, CoverSize, out Image cover);
		Grid.SetColumn(coverHost, 0);
		grid.Children.Add(coverHost);

		string sub = string.IsNullOrWhiteSpace(subOverride) ? BuildSub(track) : subOverride;
		var titleBlock = new TextBlock
		{
			FontSize = 14.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Primary(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		// 搜索命中的片段用强调色标出来，用户一眼能看出「为什么这条被筛出来」
		if (string.IsNullOrWhiteSpace(highlight))
		{
			titleBlock.Text = track.Title;
		}
		else
		{
			ApplyHighlight(titleBlock, track.Title, highlight);
		}
		string subText = sub ?? "";
		var subBlock = new TextBlock
		{
			Text = subText,
			FontSize = 12.5,
			Foreground = ThemeManager.Secondary(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 3, 0, 0),
			// 一个字都没有就整行收掉：留着一条空槽只会让标题偏上、行距看着不齐。
			Visibility = subText.Length > 0 ? Visibility.Visible : Visibility.Collapsed
		};
		var textPanel = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
		// 标题所在容器：有标签时是 headPanel，否则直接是 textPanel ——
		// 改名要拿它在容器里的下标，拿错容器会把标题从对方树里抽走。
		Panel titleHost = textPanel;
		// 标题前面最多两个小标签：平台 + VIP。
		// 两个都要的时候才套 DockPanel，一个都没有就直接放标题 ——
		// 收藏/最近播放这些行本来就没有平台标签，不该多出一层空容器。
		if (!string.IsNullOrEmpty(engineLabel) || !string.IsNullOrEmpty(secondChipLabel))
		{
			var headPanel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
			// 先加平台再加 VIP：DockPanel 按添加顺序从左往右排，
			// 所以谁先 Children.Add 谁就在左边（用户要求平台标签在 VIP 左侧）。
			// SetDock 只记停靠边，真正进布局的是 Children.Add ——
			// 少了那一句标签既不显示也不占位，编译期完全看不出来。
			if (!string.IsNullOrEmpty(engineLabel))
			{
				Border engine = MakeChip(engineLabel, engineBrush ?? ThemeManager.Accent());
				// 跟后面的 VIP 标签留一点缝，不然两个色块贴在一起像一整条。
				engine.Margin = new Thickness(0, 0, 6, 0);
				DockPanel.SetDock(engine, Dock.Left);
				headPanel.Children.Add(engine);
			}
			if (!string.IsNullOrEmpty(secondChipLabel))
			{
				Border vip = MakeChip(secondChipLabel, secondChipBrush ?? new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x1F)));
				DockPanel.SetDock(vip, Dock.Left);
				headPanel.Children.Add(vip);
			}
			// titleBlock 放最后：DockPanel 默认最后一个子元素占满剩余空间。
			titleHost = headPanel;
			headPanel.Children.Add(titleBlock);
			textPanel.Children.Add(headPanel);
		}
		else
		{
			textPanel.Children.Add(titleBlock);
		}
		textPanel.Children.Add(subBlock);
		Grid.SetColumn(textPanel, 1);
		grid.Children.Add(textPanel);

		// 双击歌名进改名已经撤销：双击整行 = 播放，标题右侧大片
		// 空白区命中不到 titleBlock，用户的「双击改名」十有八九
		// 变成了「双击播放」。改名改走操作区里的铅笔键（见下面）。

		var actions = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			// 操作区贴行尾，并和标题留出一段固定间距：
			// 标题很长被省略号截断时，播放键不会紧贴着那几个点。
			HorizontalAlignment = HorizontalAlignment.Right
		};
		if (onRename != null)
		{
			// 铅笔键改名：只给「有实体文件可改」的行开（本地文件 / B 站下载的收藏），
			// 平台在线的歌名是服务器上的，允许改只会让收藏对不上。
			// 双击整行的播放不会被铅笔键吞掉：border 的双击播放在
			// FindAncestorButton 命中时就直接返回。
			Button renameBtn = IconButton("\uE70F", 30, Loc.Default["rn_hint"]);
			renameBtn.Foreground = ThemeManager.Secondary();
			renameBtn.Click += (_, _) => BeginInlineRename(titleHost, titleBlock, track, onRename);
			actions.Children.Add(renameBtn);
		}
		if (onRemove != null)
		{
			Button removeBtn = IconButton("\uE711", 30, Loc.Default["act_remove"]);
			removeBtn.Foreground = ThemeManager.Secondary();
			removeBtn.Click += (_, _) => onRemove();
			actions.Children.Add(removeBtn);
		}
		if (onDownload != null)
		{
			Button dlBtn = IconButton("\uE896", 30, Loc.Default["act_download"]);
			dlBtn.Foreground = ThemeManager.Secondary();
			dlBtn.Click += (_, _) => onDownload();
			actions.Children.Add(dlBtn);
		}
		if (onComment != null)
		{
			// 图标可覆盖。音乐库借这个位置放「歌词」按钮时传别的字形，
			// 不然本地文件会显示一个点开全是空的评论按钮。
			Button commentBtn = IconButton(onCommentIcon ?? "\uE90A", 30,
				onCommentTip ?? Loc.Default["act_comment"]);
			commentBtn.Foreground = ThemeManager.Secondary();
			// 打标：主窗「点程序内收起评论抽屉」的失焦判定要认得这个键，
			// 否则点它会被当成「点了别处」先关再开，同曲收起就失效了。
			commentBtn.Tag = "CommentToggle";
			commentBtn.Click += (_, _) => onComment();
			actions.Children.Add(commentBtn);
		}
		// 复制键：把去重后的「歌名 - 歌手」拷进剪贴板并轻提示。位置钉在红心左边 ——
		// 复制和收藏一样是不打断播放的轻操作，相邻摆放顺手；IconButton 自带
		// 6px 左间距，与红心自然隔开。样式/悬停反馈与红心同一套（IconButton）。
		// 键是 Button：行点击播放的判定会跳过按钮（FindAncestorButton），
		// 所以点它不会误触整行播放。
		Button copyBtn = IconButton("\uE8C8", 32, Loc.Default["act_copy"]);
		copyBtn.Foreground = ThemeManager.Secondary();
		copyBtn.Click += (_, _) => CopyTrackText(track.Title, track.Artist);
		actions.Children.Add(copyBtn);
		Button? heart = null;
		if (onToggleFav != null)
		{
			heart = IconButton("\uEB51", 32, Loc.Default["act_fav"]);
			heart.Foreground = ThemeManager.Secondary();
			heart.Click += (_, _) =>
			{
				onToggleFav();
				SetHeartVisual(heart, UserLibrary.IsFavorite(track), animate: true);
				// 同一首歌可能在别的页面也有一行（比如搜索页和收藏页同时开着），
				// 底栏那颗心也要跟着变。登记过就能一次刷全。
				SyncHearts(track.Key);
			};
			actions.Children.Add(heart);
			// 红心登记改由行 Loaded/Unloaded 成对维护（见下面两个事件）：
			// 行活着才登记，行离树就摘除，静态表不再囤死行。
			RegisterHeart(heart, track.Key);
		}
		Button playBtn = IconButton("\uE768", 34, Loc.Default["act_play"]);
		playBtn.Background = AccentBrush;
		playBtn.Foreground = OnAccentBrush;
		playBtn.Click += (_, _) => onPlay();
		actions.Children.Add(playBtn);

		if (heart != null)
		{
			SetHeartVisual(heart, isFav);
		}
		Grid.SetColumn(actions, 2);
		grid.Children.Add(actions);

		// 底色和描边都用「每行独立」的画刷：ColorAnimation 必须作用在实例上，
		// 共享 ResourceDictionary 里那支笔的话整列会一起闪。
		var bg = new SolidColorBrush(Colors.Transparent);
		var borderBrush = new SolidColorBrush(Colors.Transparent);
		var border = new Border
		{
			Child = grid,
			CornerRadius = new CornerRadius(9),
			// 左内边距留给强调条。原来强调条是 border 的兄弟节点、压在圆角外面，
			// 方条戳出圆角，看着就像边框「沉」了一块。
			Padding = new Thickness(14, RowContentPadding, 14, RowContentPadding),
			Background = bg,
			BorderBrush = borderBrush,
			BorderThickness = new Thickness(1),
			Tag = track
		};

		// 播放进度用左右两条强调色竖条表示，不再给整行上底色。
		// 右侧同样给一条：原来只有左边有，右边只剩一条很淡的 1px 描边，
		// 选中与否在右边几乎看不出来。
		var playingBar = new Border
		{
			Width = 2.5,
			CornerRadius = new CornerRadius(1.25),
			Background = AccentBrush,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Stretch,
			// 落在 border 的左内边距里，不压圆角，也不改变内容位置
			Margin = new Thickness(5, 5, 0, 5),
			Opacity = 0
		};
		var playingBarRight = new Border
		{
			Width = 2.5,
			CornerRadius = new CornerRadius(1.25),
			Background = AccentBrush,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Stretch,
			Margin = new Thickness(0, 5, 5, 5),
			Opacity = 0
		};
		var host = new Grid();
		host.Children.Add(border);
		host.Children.Add(playingBar);
		host.Children.Add(playingBarRight);

		Color hoverBg = ThemeManager.RowHover().Color;
		Color hoverBorder = ThemeManager.RowSelectedFill().Color;
		Color selectedBg = ThemeManager.AccentSubtle().Color;

		void ApplyState(bool hovered, bool playing)
		{
			Color targetBg = playing ? selectedBg : hovered ? hoverBg : Colors.Transparent;
			Color targetBorder = playing ? AccentColor() : hovered ? hoverBorder : Colors.Transparent;
			TimeSpan span = TimeSpan.FromMilliseconds(150);
			bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(targetBg, span) { FillBehavior = FillBehavior.HoldEnd });
			borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(targetBorder, span) { FillBehavior = FillBehavior.HoldEnd });
			playingBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(playing ? 1 : 0, span));
			// 右侧那条必须和左边同步淡入淡出，否则只有半边响应
			playingBarRight.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(playing ? 1 : 0, span));
		}

		// 本行最近一次应用过的播放状态。切歌时 SetPlayingTrack 会广播给
		// 页面上所有活着的行（音乐库不虚拟化时几百行都在），绝大多数行
		// 的播放状态根本没变 —— 一行没变也照做 4 次 BeginAnimation 的
		// 话，一次切歌就是上千个动画时钟，主线程「播放状态处理」直接
		// 卡出 700ms~数秒。所以这里先比对再动手：真正变化的只有
		// 上一首的行和当前这一首，其余行直接跳过。
		bool lastPlaying = IsPlaying(track);

		// 只淡化，不缩放：之前 MouseEnter 会把整行放大到 1.014，离开时又没把底色
		// 还原，于是鼠标扫过之后背景块一直挂着，看起来像「又冒出标签背景」。
		// 顺手把 lastPlaying 对齐成当前真实状态：悬停分支会直接动动画，
		// 缓存不同步的话下一次真正的切歌广播可能被误判成「没变化」而跳过。
		border.MouseEnter += (_, _) => { lastPlaying = IsPlaying(track); ApplyState(true, lastPlaying); };
		border.MouseLeave += (_, _) => { lastPlaying = IsPlaying(track); ApplyState(false, lastPlaying); };

		// 双击整行 = 播放（用户要求「双击音乐标签卡片时候也能播放音乐」）。
		// Border 没有 MouseDoubleClick（那是 ItemsControl 才有的），所以在 MouseLeftButtonDown
		// 里数 ClickCount=2 自己触发。点中按钮时不响应，避免「双击 = 按钮点两遍 + 行播一次」。
		border.MouseLeftButtonDown += (_, args) =>
		{
			if (args.OriginalSource is DependencyObject src
				&& FindAncestorButton(src) != null)
			{
				return;
			}
			if (args.ClickCount >= 2)
			{
				onPlay();
			}
		};

		void OnPlayingChanged()
		{
			bool playing = IsPlaying(track);
			if (playing == lastPlaying)
			{
				return;
			}
			lastPlaying = playing;
			ApplyState(playing ? true : false, playing);
		}
		// 行会离开可视树（翻页时 Items.Clear），之后还可能被缓存复用再放回来。
		// 所以必须成对地「Loaded 订阅 / Unloaded 退订」：
		// 以前只在 Unloaded 里 -= ，那么行离开一次可视树之后
		// 再放回来就再也不会收到 PlayingChanged，正在播放的强调条会失灵；
		// 而从没进过可视树的行又永远退不掉，会被静态事件一直钉住。
		// 红心登记同样成对维护：在树里才参与全局同步，离树就摘除，
		// 静态字典（弱引用化之前）绝不能把离页的死行钉进内存。
		host.Loaded += (_, _) =>
		{
			PlayingChanged += OnPlayingChanged;
			RegisterHeart(heart, track.Key);
		};
		host.Unloaded += (_, _) =>
		{
			PlayingChanged -= OnPlayingChanged;
			UnregisterHeart(heart);
		};
		// 已经在树里的话 Loaded 不会再触发，先补一次订阅。
		if (host.IsLoaded)
		{
			PlayingChanged += OnPlayingChanged;
			RegisterHeart(heart, track.Key);
		}

		// 封面懒加载登记：行进入所属 ScrollViewer 的视口才真正发起请求，
		// 视口外只显示占位底色（说明见文件尾的懒加载区）。
		// 本地行没封面时把 track 一起递进去：探测开关开着的话登记成
		// 在线补封面的探测槽位。
		AttachLazyCover(host, cover, coverUrl, track);

		// 首帧状态：画刷本来就是 Transparent、竖条本来就是 Opacity=0，
		// 非播放行跑一遍 ApplyState 只是白造 4 个动画对象 —— 列表重建
		// 时几百行就是几百个无用动画，直接跳过；播放中的行才需要亮起来。
		if (lastPlaying)
		{
			ApplyState(false, true);
		}
		// 调试统计：累计建行数（调试面板「行容器」一项用）。
		Interlocked.Increment(ref _rowsBuiltTotal);
		return host;
	}

	private static bool IsPlaying(SavedTrack track)
	{
		if (!string.IsNullOrEmpty(_playingPath)
			&& string.Equals(_playingPath, track.Path, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		// 本地行路径就是唯一身份；在线行靠 Key（type:id）对上当前播放。
		return !string.IsNullOrEmpty(_playingKey)
			&& string.Equals(_playingKey, track.Key, StringComparison.Ordinal);
	}

	private static Color AccentColor() =>
		AccentBrush is SolidColorBrush sb ? sb.Color : Color.FromRgb(0x3C, 0x8C, 0xFF);

	/// <summary>所有还活着的红心按钮，按曲目 key 记一份。
	/// 底栏那颗心和列表里的是同一份收藏状态的两处显示，
	/// 不登记就没法互相通知 —— 底栏点了，列表里那颗还是空心。
	/// 用 Button 做键（值是曲目 key）：行 Unloaded 时 O(1) 摘除。
	/// 旧版是不修边的 List：列表每次重画每行都 Add 一条，摘除只发生在
	/// 恰好点红心触发 SyncHearts 的时候 —— 一次重建风暴就是几百条死引用，
	/// 每条都把整行控件树（连同闭包、动画）钉在静态表里，内存只涨不跌。</summary>
	private static readonly Dictionary<Button, string> LiveHearts = new();

	// 调试统计：累计建行数。配合 GetRowStats 给调试面板的「内存检测」区看
	// 行容器规模——存活行数远小于累计建行数说明行缓存/回收在正常工作。
	private static long _rowsBuiltTotal;

	/// <summary>行容器统计（调试面板用）：存活行 ≈ 红心登记数（行进/出可视树
	/// 成对登记/摘除，见 LiveHearts 注释），累计建行是进程启动以来的总数。</summary>
	public static (int LiveRows, long BuiltRows) GetRowStats() =>
		(LiveHearts.Count, Interlocked.Read(ref _rowsBuiltTotal));

	/// <summary>行进可视树时登记红心（与 UnregisterHeart 成对）。</summary>
	private static void RegisterHeart(Button? heart, string key)
	{
		if (heart == null || string.IsNullOrEmpty(key))
		{
			return;
		}
		LiveHearts[heart] = key;
	}

	/// <summary>行离可视树时摘除红心登记，别让静态表把死行钉在内存里。</summary>
	private static void UnregisterHeart(Button? heart)
	{
		if (heart != null)
		{
			LiveHearts.Remove(heart);
		}
	}

	/// <summary>把某一首歌在所有行里的红心刷成当前收藏状态。
	/// 底栏收藏/取消收藏之后由 MainWindow 调一次，
	/// 列表、收藏页、最近播放页里同一首歌的心就一起变了。</summary>
	public static void SyncHearts(string trackKey)
	{
		if (string.IsNullOrEmpty(trackKey)) return;
		bool fav = UserLibrary.IsFavoriteByKey(trackKey);
		List<Button>? dead = null;
		foreach (var kv in LiveHearts)
		{
			// 行控件被 Items.Clear() 丢掉的，Parent 会变成 null。
			// 顺手清掉，不然这个表只增不减，几轮搜索之后就是几千条死引用。
			if (kv.Key.Parent == null)
			{
				(dead ??= new List<Button>()).Add(kv.Key);
				continue;
			}
			if (string.Equals(kv.Value, trackKey, StringComparison.OrdinalIgnoreCase))
			{
				SetHeartVisual(kv.Key, fav, animate: true);
			}
		}
		if (dead != null)
		{
			foreach (Button btn in dead)
			{
				LiveHearts.Remove(btn);
			}
		}
	}

	private static void SetHeartVisual(Button heart, bool fav, bool animate = false)
	{
		if (heart.Content is TextBlock glyph)
		{
			glyph.Text = fav ? "\uEB52" : "\uEB51";
		}
		heart.Foreground = fav
			? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
			: ThemeManager.Secondary();
		heart.ToolTip = fav ? Loc.Default["act_unfav"] : Loc.Default["fav_save_tip"];
		if (!animate)
		{
			return;
		}
		// 收藏/取消时让心「弹一下」。
		// RenderTransform 是可冻结对象，动画可以直接作用在它上面；
		// 用 LayoutTransform 就不行 —— 那会让整行参与重新布局，
		// 一行一动周围全跟着跳。
		// 起点略大于 1：收藏时先鼓出来再缩回去，取消时也走同一条曲线，
		// 两个方向的手感一致，不用为「取消」单写一段。
		if (heart.RenderTransform is not ScaleTransform scale)
		{
			scale = new ScaleTransform(1.0, 1.0);
			heart.RenderTransform = scale;
			heart.RenderTransformOrigin = new Point(0.5, 0.5);
		}
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		var anim = new DoubleAnimation(fav ? 1.35 : 0.85, 1.0, TimeSpan.FromMilliseconds(260))
		{
			EasingFunction = new BackEase
			{
				Amplitude = 0.6,
				EasingMode = EasingMode.EaseOut
			},
			FillBehavior = FillBehavior.Stop
		};
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
	}

	/// <summary>把标题按命中片段拆成 Run，命中段用强调色加粗。
	/// 命中片段由 <see cref="SearchMatcher.FindRanges"/> 算，所以「云鹤」也能在
	/// 《雲鶴サクリファイス》里标出「雲鶴」两个字——只是把两个字符串字面对上
	/// 的话，这种繁简差一个字的情况会一个都标不出来。
	/// 找不到命中就整段普通文本，不做任何着色。
	/// 公开出来是为了让标题栏搜索建议那种「自己拼一行」的地方也能用同一套着色。</summary>
	public static void FillHighlight(TextBlock block, string title, string? keyword)
	{
		block.Inlines.Clear();
		if (string.IsNullOrEmpty(title))
		{
			return;
		}
		IReadOnlyList<(int Start, int Length)> ranges = SearchMatcher.FindRanges(title, keyword);
		if (ranges.Count == 0)
		{
			block.Inlines.Add(new Run(title));
			return;
		}
		int from = 0;
		foreach ((int start, int length) in ranges)
		{
			if (start > from)
			{
				block.Inlines.Add(new Run(title.Substring(from, start - from)));
			}
			block.Inlines.Add(new Run(title.Substring(start, length))
			{
				Foreground = ThemeManager.Accent(),
				FontWeight = FontWeights.Bold
			});
			from = start + length;
		}
		if (from < title.Length)
		{
			block.Inlines.Add(new Run(title.Substring(from)));
		}
	}

	private static void ApplyHighlight(TextBlock block, string title, string keyword)
		=> FillHighlight(block, title, keyword);

	private static string BuildSub(SavedTrack t)
	{
		// 副标题只放「作者 · 时长」，没有作者就只放时长，都没有就空着。
		//
		// 原来作者空着时会拿 Title 顶上，于是歌名在标题行和副标题行各出现一次 ——
		// 用户报的「音乐库显示两次音乐名称」就是这个。重复不如留白：
		// 空字符串会让下面那行直接不排版，标题自己垂直居中，不会多出一条空槽。
		string artist = t.Artist ?? "";
		string dur = t.DurationMs > 0 ? FormatDuration(t.DurationMs) : "";
		if (artist.Length > 0 && dur.Length > 0)
		{
			return artist + "  ·  " + dur;
		}
		return artist.Length > 0 ? artist : dur;
	}

	public static string FormatDuration(long ms)
	{
		try
		{
			TimeSpan t = TimeSpan.FromMilliseconds(ms);
			return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
		}
		catch
		{
			return "";
		}
	}

	// ==================== 「歌名 - 歌手」复制文本 ====================

	/// <summary>把「歌名」与「歌手」拼成复制用的「歌名 - 歌手」，歌名尾部已经
	/// 带同一段歌手后缀时不再重复追加。播放条复制键与列表行复制键都走这里，
	/// 保证两处行为一致。去重规则：大小写、空白、" - " 与 "-" 的差异都视为等价
	///（比较前统一小写并剔除全部空白，"A - B" 与 "A-B" 就成了同一个键）——
	/// 平台上歌手串常带空格变体（"LiLA'c Records" / "lilac records"），
	/// 只按原文比较会漏判，出现 "PHANTOM NIGHT - LiLA'c Records - LiLA'c Records"
	/// 这类「歌手粘两遍」的复制结果。</summary>
	public static string BuildCopyText(string? title, string? artist)
	{
		// 复制串同样剥掉本地文件扩展名：播放条复制键传进来的 title 可能是
		// 「胸の中で誰かが.m4a」这类本地文件名，粘贴去搜索时扩展名是噪音。
		// 在线曲目的平台标题不会以音频扩展名结尾，所以不受影响（万一真有
		// 「xxx.flac」这种 B 站标题，剥掉扩展名对「拿去搜索」这个用途反而更好）。
		string t = CleanTitleNoise(title);
		string a = (artist ?? "").Trim();
		if (t.Length == 0) return a;      // 没歌名就只给歌手，总比空串好
		if (a.Length == 0) return t;      // 本地无歌手文件：只复制歌名
		string tKey = CopyCompareKey(t);
		string aKey = CopyCompareKey(a);
		// 歌名以「歌手（含分隔符）」结尾即视为已带后缀，直接用歌名本体
		if (aKey.Length > 0 && tKey.EndsWith(aKey, StringComparison.Ordinal)) return t;
		return t + " - " + a;
	}

	/// <summary>复制去重的比较键：小写 + 剔除全部空白。
	/// 空白剔掉后 " - " 与 "-" 自然等价，不用再单独处理分隔符形态。</summary>
	private static string CopyCompareKey(string s)
	{
		var sb = new System.Text.StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (!char.IsWhiteSpace(c))
			{
				sb.Append(char.ToLowerInvariant(c));
			}
		}
		return sb.ToString();
	}

	/// <summary>播放条标题的展示串：平台标题常把歌手连在歌名后面
	///（「歌名 - 歌手」），下面歌手行又会把完整歌手显示一遍，
	/// 标题里再带一遍就是重复信息。规则与复制键同口径：按最后一个
	/// 「- / － / —」分隔符切开，尾部一段与歌手串比较相等
	///（CopyCompareKey：小写+去空白）才剥掉，只显示歌名本体；
	/// 不相等（标题尾部不是歌手串）就原样返回，绝不误伤标题里的连字符。
	/// 复制（BuildCopyText）仍用完整原始标题拼「歌名 - 歌手」：
	/// 标题剥掉后缀后复制天然不会出现「歌手粘两遍」。作者行照旧完整歌手。</summary>
	public static string DisplayTitle(string? title, string? artist)
	{
		string t = (title ?? "").Trim();
		string a = (artist ?? "").Trim();
		if (t.Length == 0 || a.Length == 0)
		{
			return t;
		}
		string aKey = CopyCompareKey(a);
		if (aKey.Length == 0)
		{
			return t;
		}
		// 从右往左扫每一个分隔符：哪个分隔符右边的后缀与歌手串相等，
		// 就把那一段整体剥掉。不能只查最后一个分隔符——歌手串自己可能
		// 带连字符（多歌手「LumynX / … / ASTRO-715 / AIR ALERT」里的
		// 「ASTRO-715」），最后一个「-」落在歌手串内部，只查它必然
		// 比对失败，整段歌手留在标题里跟下方歌手行重复。
		for (int i = t.Length - 1; i > 0; i--)
		{
			char c = t[i];
			if (c != '-' && c != '－' && c != '—')
			{
				continue;
			}
			// 分隔符后面就是候选后缀（跳过分隔符旁的空白）。
			int tailStart = i + 1;
			while (tailStart < t.Length && char.IsWhiteSpace(t[tailStart]))
			{
				tailStart++;
			}
			if (tailStart >= t.Length)
			{
				continue;
			}
			string tail = t[tailStart..].TrimEnd();
			if (string.Equals(CopyCompareKey(tail), aKey, StringComparison.Ordinal))
			{
				string head = t[..i].TrimEnd();
				return head.Length > 0 ? head : t;
			}
			// 这个分隔符的后缀不是歌手串，继续往左找下一个分隔符：
			// 「歌名 - 歌手」真正的分隔符可能在更左边。
		}
		return t;
	}

	/// <summary>展示层专用的扩展名剥离：本地文件名「胸の中で誰かが.m4a」这种
	/// 末段音频扩展名在界面上是噪音，播放条标题与复制串都走这里剥掉。
	/// 只认末段常见音频扩展名（大小写不敏感），其余一律原样返回 ——
	/// 在线曲目的平台标题正常不会这样结尾，等于不受影响。
	/// 底层文件名不动（播放、改名、收藏照旧用原名），只在展示层剥。</summary>
	public static string StripAudioExtension(string? title)
	{
		string s = (title ?? "").TrimEnd();
		if (s.Length < 5 || s[s.Length - 1] == '\\' || s[s.Length - 1] == '/')
		{
			return s; // 太短不可能是「名字.mp3」/ 以分隔符结尾的当目录名处理
		}
		int dot = s.LastIndexOf('.');
		if (dot <= 0 || dot < s.Length - 5)
		{
			return s; // 没有点，或点后超过 4 个字符（不是扩展名形态）
		}
		ReadOnlySpan<char> ext = s.AsSpan(dot + 1);
		foreach (string known in AudioExtensions)
		{
			if (ext.Equals(known, StringComparison.OrdinalIgnoreCase))
			{
				return s[..dot].TrimEnd();
			}
		}
		return s;
	}

	/// <summary>会出现在音频文件名末尾的常见扩展名（不含点，小写存）。</summary>
	private static readonly string[] AudioExtensions =
	{
		"mp3", "m4a", "flac", "wav", "ape", "ogg", "wma", "aac"
	};

	/// <summary>展示/复制层专用的「画质标签」剥离：B 站下载的视频文件名常连写
	/// 画质标识（「Bad apple!!4k60fps.mp4」），「4k60fps」这类对听歌毫无意义、
	/// 还会被误当成歌名一部分的噪音，在标题展示和复制串里都要清掉。
	/// 只认末尾明确是画质描述的 token（4k60fps / 1080p / 720p / 60fps …），
	/// 大小写不敏感；可包在 [](){} 里；不碰扩展名——
	/// 扩展名由 <see cref="StripAudioExtension"/> 管，二者职责分开，互不干扰。</summary>
	public static string StripQualityTag(string? title)
	{
		string s = (title ?? "").TrimEnd();
		if (s.Length == 0)
		{
			return s;
		}
		string cleaned = QualityTagRegex.Replace(s, "");
		return cleaned.TrimEnd();
	}

	/// <summary>标题清理合集：先剥音频扩展名、再剥画质标签。
	/// 歌词语窗大标题与复制串都走这里，做到「展示/复制都干净」一处定义，
	/// 不用在多处各写一遍剥离逻辑、再各改各的。</summary>
	public static string CleanTitleNoise(string? title)
		=> StripQualityTag(StripAudioExtension(title));

	private static readonly System.Text.RegularExpressions.Regex QualityTagRegex =
		new System.Text.RegularExpressions.Regex(
			@"(?i)\s*[\[\(\{]?\s*(?:4k60fps|2160p|1440p|1080p|720p|480p|360p|240p|144p|60fps|30fps|24fps|4k|2k|8k)\s*[\]\)\}]?\s*$",
			System.Text.RegularExpressions.RegexOptions.Compiled);

	/// <summary>行内/播放条复制动作：拼去重后的「歌名 - 歌手」进剪贴板并轻提示。
	/// 独立成方法是因为两个入口（TrackRows 行键、MainWindow 播放条键）都要
	/// 「复制 + Toast」，复制逻辑分散迟早长歪。剪贴板可能被别的进程占着
	///（SetText 抛 ExternalException），照播放条原样 try/catch 兜住。</summary>
	public static void CopyTrackText(string? title, string? artist)
	{
		try
		{
			string text = BuildCopyText(title, artist);
			if (text.Length == 0)
			{
				return;
			}
			Clipboard.SetText(text);
			if (Application.Current?.MainWindow is MainWindow mw)
			{
				mw.ShowToast(Loc.Default["copy_done"]);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "复制歌曲信息失败");
		}
	}

	// ==================== 折叠区动画（收藏夹收缩栏共用） ====================

	/// <summary>折叠组头的箭头字形（E70D 下箭头）。展开朝下、折叠时转 -90° 朝右
	///（WPF 正角是顺时针），与收藏页分组头同一套动效语言。</summary>
	public static TextBlock MakeChevron(bool expanded)
	{
		var chevron = new TextBlock
		{
			Text = "\uE70D",
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 12,
			Margin = new Thickness(8, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			// 旋转绕字形中心。默认绕左上角：折叠转 -90° 后字形会往上窜一个字宽，
			// 戳出组头上沿。
			RenderTransformOrigin = new Point(0.5, 0.5),
			RenderTransform = new RotateTransform(expanded ? 0 : -90)
		};
		chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
		return chevron;
	}

	/// <summary>折叠面板自己的代次（快速连点组头时作废上一轮动画的收尾回调）。</summary>
	private sealed class CollapseState
	{
		public int Gen;
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, CollapseState> CollapseStates = new();

	/// <summary>折叠/展开一个面板：MaxHeight 动画 + 箭头转向。音乐库左栏的平台
	/// 收缩栏和账号页「加载收藏夹」区共用 —— 两处都要「高度渐变 + 箭头转向」，
	/// 各写一份迟早漂移。写法与 FavoritesPage 的分组折叠一致：
	/// 只动 MaxHeight 这个布局量（直接切 Visibility 是瞬变），动画期间开
	/// ClipToBounds 收住超出的行，收尾先摘动画再放开上限/隐藏，不会闪一帧。</summary>
	public static void SetPanelExpanded(FrameworkElement panel, RotateTransform? chevron, bool expanded)
	{
		var st = CollapseStates.GetOrCreateValue(panel);
		int gen = ++st.Gen;

		if (chevron != null)
		{
			// Angle 上可能还挂着上一轮 HoldEnd 动画：先把当前显示值抄成基值再起新动画。
			double current = chevron.Angle;
			chevron.BeginAnimation(RotateTransform.AngleProperty, null);
			chevron.Angle = current;
			chevron.BeginAnimation(RotateTransform.AngleProperty,
				new DoubleAnimation(expanded ? 0 : -90, TimeSpan.FromMilliseconds(150))
				{
					From = current,
					EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
				});
		}

		panel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
		panel.ClipToBounds = true;
		if (expanded)
		{
			// 本来就是展开的（状态与视觉错位时），别重铺一遍闪一下。
			if (panel.Visibility == Visibility.Visible && panel.ActualHeight > 0
				&& panel.MaxHeight == double.PositiveInfinity)
			{
				panel.ClipToBounds = false;
				return;
			}
			// 起点 = 当前真实高度：从收了一半的状态接着铺，连点不跳。
			double start = panel.Visibility == Visibility.Visible ? panel.ActualHeight : 0;
			panel.Visibility = Visibility.Visible;
			// 量自然高度前先放开 MaxHeight：布局属性会把 DesiredSize 夹到上限，
			// 拿 0 去量只会量出 0。
			panel.MaxHeight = double.PositiveInfinity;
			panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			double target = panel.DesiredSize.Height;
			if (target <= 1)
			{
				panel.ClipToBounds = false;
				return;
			}
			if (start > target)
			{
				start = target;
			}
			panel.MaxHeight = start;
			var grow = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(180))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			grow.Completed += (_, _) =>
			{
				if (st.Gen != gen)
				{
					return;
				}
				// 先摘动画再放开上限：同一帧完成，中间没有回落帧。
				panel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
				panel.MaxHeight = double.PositiveInfinity;
				panel.ClipToBounds = false;
			};
			panel.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);
		}
		else
		{
			double from = panel.ActualHeight;
			if (panel.Visibility != Visibility.Visible || from <= 1)
			{
				panel.Visibility = Visibility.Collapsed;
				panel.MaxHeight = double.PositiveInfinity;
				panel.ClipToBounds = false;
				return;
			}
			panel.MaxHeight = from;
			var shrink = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(150))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			shrink.Completed += (_, _) =>
			{
				if (st.Gen != gen)
				{
					return;
				}
				// 先切 Collapsed 再摘动画：Visibility 一关高度就是 0，不会闪出来。
				panel.Visibility = Visibility.Collapsed;
				panel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
				panel.MaxHeight = double.PositiveInfinity;
				panel.ClipToBounds = false;
			};
			panel.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
		}
	}

	/// <summary>行封面：完全透明的占位 + 图片本体。
	/// 构建时不发任何请求 —— 一页几百行同时打网络是封面最慢出现的根源，
	/// 请求时机全部交给 <see cref="AttachLazyCover"/> 的视口调度。
	/// 占位必须全透明（不能蒙一层主题底色）：未加载的封面蒙黑底会变成
	/// 一排突兀的色块，行本身有斑马纹和间隔，透明占位视觉上就是"还没图"；
	/// 尺寸固定，所以行高不受影响。图片加载完成后 200ms 淡入，
	/// 与项目其它动效同一时长。</summary>
	public static FrameworkElement BuildCover(string url, double size, out Image img)
	{
		img = new Image
		{
			Width = size,
			Height = size,
			Stretch = Stretch.UniformToFill,
			Clip = new RectangleGeometry(new Rect(0, 0, size, size), 7, 7),
			// 基值 0：没加载的行露的是占位底色；加载完成后由 200ms 淡入接管。
			Opacity = 0
		};
		return new Border
		{
			Width = size,
			Height = size,
			CornerRadius = new CornerRadius(7),
			// 占位全透明：没图就是没图，不蒙一层底色（蒙底色在深色主题下像蒙了黑块）。
			Background = Brushes.Transparent,
			Child = img
		};
	}

	// ==================== 行封面懒加载 ====================
	//
	// 以前 BuildCover 在行构建时立刻请求封面：一页几百行同时打网络。
	// 现在改成「进入视口才加载」：
	//   · 行构建时只有占位底色，不发请求；
	//   · 行 Loaded 后找到所属 ScrollViewer 登记；ScrollChanged 用 50ms
	//     合帧（高频滚动不重复计算），对每行做 TransformToVisual 与视口
	//     的相交判断（外扩一圈做提前量，不必逐像素）；
	//   · 进入视口才发起加载，完成后 200ms 淡入；视口外的行不加载，
	//     只显示占位底色；
	//   · 字节/解码内存缓存都在 CoverLoader 里，缓存命中直接淡入、不再
	//     发网络请求，列表滚动复用不会重复下载。

	private sealed class CoverSlot
	{
		public FrameworkElement Row = null!;
		public Image Img = null!;
		public string Url = "";
		public bool Started;

		// 在线补封面的探测槽位（本地行没有已知封面时）：Url 留空、ProbeKey 非空。
		// 进视口后由 LocalCoverProbe 按「歌名+歌手」在网易云/QQ 找一张补上；
		// ProbeKey 即 CoverLoader 共用缓存的键（local:文件路径），命中后同一首
		// 不再二次请求。普通封面行这两个字段是空的，走原有加载路径。
		public string ProbeKey = "";
		public string ProbeTitle = "";
		public string ProbeArtist = "";
	}

	/// <summary>行 → 封面槽位。用 ConditionalWeakTable：行被页面丢弃后
	/// 条目随行一起被回收，不像普通 Dictionary 会把死行钉在内存里。</summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, CoverSlot> CoverSlots = new();

	private sealed class CoverViewer
	{
		public ScrollViewer Viewer = null!;
		public readonly List<CoverSlot> Pending = new();
		public DispatcherTimer? Timer;
	}

	private static readonly Dictionary<ScrollViewer, CoverViewer> CoverViewers = new();

	/// <summary>把行登记进懒加载调度。url 为空时占位底色就是终态，不必登记——
	/// 除非 track 是开了「主动探测」的本地行：那种登记成探测槽位，进视口后
	/// 在线补一张封面（同样受视口机制约束，视口外的行不探测）。
	/// Loaded/Unloaded 成对订阅：行会离开可视树（翻页 Items.Clear、搜索页
	/// 的行缓存挂起），再放回来时要重新参与可见性计算。
	///
	/// 行构建这一拍先同步查一遍解码缓存：封面已经在手（重建的行、翻回来的页）
	/// 就当场挂上、以不透明态显示，连视口调度都不必等 —— 列表重画时行从出生
	/// 就是带图状态，这就是「封面反复淡入」的第二道闸：第一道在 CoverLoader
	/// 的同步命中路径里（不再重播淡入动画）。</summary>
	private static void AttachLazyCover(FrameworkElement row, Image img, string url, SavedTrack? track = null)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			// 本地行无封面 + 探测开关开着 + 有可搜的歌名：登记探测槽位。
			// 开关关着就维持现状——占位底色就是终态。
			if (track != null
				&& string.Equals(track.Type, "local", StringComparison.Ordinal)
				&& VipProbe.Enabled
				&& !string.IsNullOrWhiteSpace(track.Title))
			{
				var probeSlot = new CoverSlot
				{
					Row = row,
					Img = img,
					Url = "",
					ProbeKey = "local:" + (track.Path ?? track.Title),
					ProbeTitle = track.Title,
					ProbeArtist = track.Artist ?? ""
				};
				CoverSlots.Remove(row);
				CoverSlots.Add(row, probeSlot);
				row.Loaded += OnCoverRowLoaded;
				row.Unloaded += OnCoverRowUnloaded;
				// 探测结论早就命中过（本地补封面进了共用缓存）：当场挂图。
				if (ShowCachedCoverImmediately(probeSlot, probeSlot.ProbeKey))
				{
					return;
				}
				if (row.IsLoaded)
				{
					OnCoverRowLoaded(row, new RoutedEventArgs());
				}
			}
			return;
		}
		var slot = new CoverSlot { Row = row, Img = img, Url = url };
		CoverSlots.Remove(row);
		CoverSlots.Add(row, slot);
		row.Loaded += OnCoverRowLoaded;
		row.Unloaded += OnCoverRowUnloaded;
		// 封面就在解码缓存里：当场挂上，不进视口调度。
		if (ShowCachedCoverImmediately(slot, url))
		{
			return;
		}
		// 已经在树里的话 Loaded 不会再触发，先补一次登记。
		if (row.IsLoaded)
		{
			OnCoverRowLoaded(row, new RoutedEventArgs());
		}
	}

	/// <summary>解码缓存同步命中：立刻摆图、顶满不透明度、标记已开始并钉住 LRU。
	/// 行重建/翻页回来时封面多半都在缓存里 —— 走这条路的行从出生就带图，
	/// 列表整页重画也不会再看到「占位 → 图」或重播淡入。</summary>
	private static bool ShowCachedCoverImmediately(CoverSlot s, string cacheKey)
	{
		if (!CoverLoader.TryGetDecoded(cacheKey, 128, out ImageSource? ready))
		{
			return false;
		}
		s.Img.Source = ready;
		if (s.Img.Opacity < 0.999)
		{
			s.Img.BeginAnimation(UIElement.OpacityProperty, null);
			s.Img.Opacity = 1;
		}
		s.Started = true;
		CoverLoader.PinDecoded(cacheKey);
		return true;
	}

	/// <summary>槽位对应的封面缓存键（LRU 钉住/解钉都用它）。</summary>
	private static string CoverKeyOf(CoverSlot s)
		=> s.ProbeKey.Length > 0 ? s.ProbeKey : s.Url;

	private static void OnCoverRowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement row || !CoverSlots.TryGetValue(row, out CoverSlot? slot))
		{
			return;
		}
		if (slot.Started)
		{
			// 已经加载过：图就在缓存里，行回来直接显示，不再进待加载表。
			// 重新钉住 LRU：行又可见了，逐出时继续跳过它。
			CoverLoader.PinDecoded(CoverKeyOf(slot));
			return;
		}
		CoverViewer? cv = EnsureCoverViewer(row);
		if (cv == null)
		{
			// 没有滚动容器可挂（理论上到不了）：退化为立即加载，别让封面永远空着。
			StartCoverLoad(slot);
			return;
		}
		if (!cv.Pending.Contains(slot))
		{
			cv.Pending.Add(slot);
		}
		QueueCoverCheck(cv);
	}

	private static void OnCoverRowUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement row || !CoverSlots.TryGetValue(row, out CoverSlot? slot))
		{
			return;
		}
		// 行离开可视树：从待加载表摘掉，同时解除 LRU 钉住 ——
		// 不在屏幕上的封面可以让位给可见的。
		CoverLoader.UnpinDecoded(CoverKeyOf(slot));
		// 已开始的加载不中断 —— 图已经落到缓存里，行回来时直接淡入。
		foreach (CoverViewer cv in CoverViewers.Values)
		{
			cv.Pending.Remove(slot);
		}
	}

	private static CoverViewer? EnsureCoverViewer(FrameworkElement row)
	{
		DependencyObject? cur = row;
		while (cur != null)
		{
			if (cur is ScrollViewer sv)
			{
				if (!CoverViewers.TryGetValue(sv, out CoverViewer? cv))
				{
					cv = new CoverViewer { Viewer = sv };
					CoverViewers[sv] = cv;
					sv.ScrollChanged += OnCoverScrollChanged;
				}
				return cv;
			}
			cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(cur)
				: LogicalTreeHelper.GetParent(cur);
		}
		return null;
	}

	private static void OnCoverScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (sender is ScrollViewer sv && CoverViewers.TryGetValue(sv, out CoverViewer? cv))
		{
			QueueCoverCheck(cv);
		}
	}

	/// <summary>ScrollChanged 一帧能触发几十次，可见性计算按 50ms 合帧：
	/// 已有一帧合并在路上时直接跳过，到点算的是当时的最新状态。</summary>
	private static void QueueCoverCheck(CoverViewer cv)
	{
		if (cv.Timer != null)
		{
			return;
		}
		var timer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(50)
		};
		cv.Timer = timer;
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			cv.Timer = null;
			EvaluateCovers(cv);
		};
		timer.Start();
	}

	private static void EvaluateCovers(CoverViewer cv)
	{
		ScrollViewer sv = cv.Viewer;
		// 视口外扩一圈做提前量：贴着边缘刚露头的行先把图备好，滚到时刚好淡入。
		double lead = 240;
		var viewport = new Rect(-lead, -lead, sv.ViewportWidth + lead * 2, sv.ViewportHeight + lead * 2);
		for (int i = cv.Pending.Count - 1; i >= 0; i--)
		{
			CoverSlot s = cv.Pending[i];
			if (!s.Row.IsLoaded)
			{
				// Unloaded 还没来得及触发的兜底：别对死行做变换。
				cv.Pending.RemoveAt(i);
				continue;
			}
			try
			{
				Rect bounds = s.Row.TransformToVisual(sv)
					.TransformBounds(new Rect(0, 0, s.Row.ActualWidth, s.Row.ActualHeight));
				if (bounds.IntersectsWith(viewport))
				{
					cv.Pending.RemoveAt(i);
					StartCoverLoad(s);
				}
			}
			catch
			{
				// 布局还没跑完时 TransformToVisual 可能抛：留给下一帧再算。
			}
		}
		if (cv.Pending.Count == 0)
		{
			// 登记的行都处理完了：退订滚动事件、拆掉定时器，别把 ScrollViewer 钉住。
			cv.Timer?.Stop();
			sv.ScrollChanged -= OnCoverScrollChanged;
			CoverViewers.Remove(sv);
		}
	}

	private static void StartCoverLoad(CoverSlot s)
	{
		if (s.Started)
		{
			return;
		}
		s.Started = true;
		// 「显示封面图片」关掉时整条短路：这是所有行封面（本地/网络/在线探测）
		// 的共同入口，在这拦一次等于全部拦住 —— 行上只留纯色占位，不发任何请求。
		// 已加载的行不动（不清 Source），新行/新批次从此不再请求；
		// 播放条主封面不走这里，不受影响（设置语义如此）。
		if (!SettingsManager.Instance.ShowListCovers)
		{
			return;
		}
		// 正在显示/加载中的封面钉进 LRU 的「可见」表：滚动加载一波新封面时，
		// 已经亮着的不被挤出去（被挤出去的封面在行重建时要整套重新装载）。
		string pinKey = CoverKeyOf(s);
		if (pinKey.Length > 0)
		{
			CoverLoader.PinDecoded(pinKey);
		}
		Image img = s.Img;
		if (s.ProbeKey.Length > 0)
		{
			// 本地无封面行的在线探测：进视口才会走到这里（视口机制把关，
			// 视口外的行不发请求）。并发限流与结果/负缓存都在 LocalCoverProbe
			//（复用 VIP 探测的并发闸门），命中后淡入；没命中静默留占位。
			LocalCoverProbe.BeginProbing(img, s.ProbeKey, s.ProbeTitle, s.ProbeArtist, () => FadeInCover(img));
			return;
		}
		if (s.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| s.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			// 网络封面：CoverLoader 自带「内存字节 + 解码位图 + 磁盘」三级缓存，
			// 缓存命中不碰网络 —— 列表滚动复用不会重复下载；解码统一在后台线程。
			CoverLoader.LoadAsync(img, s.Url, 128, () => FadeInCover(img));
			return;
		}
		// 本地文件封面：单张解码不贵，但一屏几十行同时进视口时同步解码
		// 会在 UI 线程上叠出可感知的顿挫 —— 统一丢后台解码，回 UI 线程再淡入。
		string localUrl = s.Url;
		Image localImg = img;
		_ = Task.Run(() =>
		{
			try
			{
				var bi = new BitmapImage();
				bi.BeginInit();
				bi.UriSource = new Uri(localUrl);
				bi.DecodePixelWidth = 128;
				bi.EndInit();
				// Freeze 必须有：跨线程回 UI 线程赋值，未冻结的位图会被 WPF 拒绝。
				bi.Freeze();
				var d = Application.Current?.Dispatcher;
				if (d != null && !d.HasShutdownStarted)
				{
					_ = d.BeginInvoke(new Action(() =>
					{
						localImg.Source = bi;
						FadeInCover(localImg);
					}));
				}
				else
				{
					localImg.Source = bi;
					FadeInCover(localImg);
				}
			}
			catch
			{
				// 本地封面读不到就算了，行上留透明占位，不影响列表其它部分
			}
		});
	}

	/// <summary>封面加载完成后的 200ms 淡入。图片基值是 0（BuildCover 里设的），
	/// 动画结束若掉回基值会闪回占位色，所以 FillBehavior 必须 HoldEnd。
	/// 已经不透明的行（缓存命中的重载路径再次回调）直接跳过：对着一整屏
	/// 已经亮着的封面再播 0→1，看见的就是「封面集体闪一下」。</summary>
	private static void FadeInCover(Image img)
	{
		if (img.Source == null)
		{
			return;
		}
		if (img.Opacity >= 0.999)
		{
			return;
		}
		img.BeginAnimation(UIElement.OpacityProperty, null);
		img.BeginAnimation(UIElement.OpacityProperty,
			new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
				FillBehavior = FillBehavior.HoldEnd
			});
	}

	// 图标字体每次 new 一个 FontFamily 实例的话，WPF 的字体缓存是按实例做键的，
	// 就没法共享字形解析。一行 4 个按钮 × 30 行 = 每页 120 个 FontFamily。共享一个。
	private static readonly FontFamily MdAssets = new FontFamily("Segoe MDL2 Assets");

	// 每种尺寸的按钮模板只建一次。以前 IconButton 每调用一次就 new 一份
	// Style + ControlTemplate：搜索结果 30 行 × 4 个按钮 = 120 份，
	// 而它们内容完全一样。模板对象各自唯一，WPF 也没法共享模板实例化结果。
	private static readonly Dictionary<double, ControlTemplate> IconTemplates = new();
	private static readonly Dictionary<double, Style> IconStyles = new();

	private static Style GetIconStyle(double size)
	{
		lock (IconTemplates)
		{
			if (IconStyles.TryGetValue(size, out Style? cached))
			{
				return cached;
			}
			var template = new ControlTemplate(typeof(Button));
			var border = new FrameworkElementFactory(typeof(Border), "Bd");
			border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
			border.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2.0));
			border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
			var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
			presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
			presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
			border.AppendChild(presenter);
			template.VisualTree = border;
			var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
			hover.Setters.Add(new Setter
			{
				TargetName = "Bd",
				Property = Border.BackgroundProperty,
				Value = new DynamicResourceExtension("SubtleFillHoverBrush")
			});
			template.Triggers.Add(hover);
			var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
			pressed.Setters.Add(new Setter
			{
				TargetName = "Bd",
				Property = Border.BackgroundProperty,
				Value = new DynamicResourceExtension("SubtleFillPressedBrush")
			});
			template.Triggers.Add(pressed);

			var style = new Style(typeof(Button));
			style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
			style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
			// 前景色原来在建模板时抓一次就写死，切主题后图标颜色不跟着变。
			// 改成 DynamicResource，每种尺寸建一次即可。
			style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
			style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
			style.Setters.Add(new Setter(Control.TemplateProperty, template));
			// 不显式 Freeze：WPF 真正启用一个模板时会自行封印。

			IconTemplates[size] = template;
			IconStyles[size] = style;
			return style;
		}
	}

	/// <summary>把标题 TextBlock 换成编辑框原地改名。Enter/失焦提交，Esc 取消。
	/// 提交前还原 TextBlock（行身自带的播放进度等效果还挂在原元素上）。
	/// 只有「真的有文件可改」的行才提供 onRename，这里不重复判断。
	/// public：底栏的「正在播放」歌名也用它做行内编辑，不必再重画一套。</summary>
	public static void BeginInlineRename(Panel host, TextBlock titleBlock, SavedTrack track, Action<string> onRename)
	{
		try
		{
			int idx = host.Children.IndexOf(titleBlock);
			if (idx < 0)
			{
				return;
			}
			var editor = new TextBox
			{
				Text = track.Title,
				FontSize = 14.5,
				FontWeight = FontWeights.SemiBold,
				Padding = new Thickness(3, 1, 3, 1),
				MinWidth = 180,
				VerticalAlignment = VerticalAlignment.Center,
				BorderBrush = ThemeManager.Accent(),
				BorderThickness = new Thickness(1)
			};
			// 直接 RemoveAt+Insert，不用 UIElementCollection 的索引器：
			// 索引器内部的 Set 校验链抛过「指定的元素已经是另一个元素的
			// 逻辑子元素」（首颗行就炸，编辑框根本开不出来），
			// Remove/Add 路径没有这层校验，实际行为就是安全换子。
			host.Children.RemoveAt(idx);
			host.Children.Insert(idx, editor);
			editor.Focus();
			editor.SelectAll();
			bool done = false;
			Action Restore = () =>
			{
				// 同样走 Remove+Insert：编辑框此刻是真子元素，先摘掉再放回标题。
				int i = host.Children.IndexOf(editor);
				if (i >= 0)
				{
					host.Children.RemoveAt(i);
				}
				int t = host.Children.IndexOf(titleBlock);
				if (t < 0)
				{
					host.Children.Insert(Math.Min(idx, host.Children.Count), titleBlock);
				}
			};
			Action finish = () =>
			{
				if (done)
				{
					return;
				}
				done = true;
				string name = editor.Text != null ? editor.Text.Trim() : "";
				Restore();
				if (name.Length > 0)
				{
					try { onRename(name); }
					catch { }
				}
			};
			Action cancel = () =>
			{
				if (done)
				{
					return;
				}
				done = true;
				Restore();
			};
			editor.KeyDown += (_, e) =>
			{
				if (e.Key == Key.Enter)
				{
					finish();
					e.Handled = true;
				}
				else if (e.Key == Key.Escape)
				{
					cancel();
					e.Handled = true;
				}
			};
			editor.LostKeyboardFocus += (_, _) => finish();
			// 行身绑了双击播歌，点编辑器里不能往下漏成一次播放。
			editor.PreviewMouseDown += (_, e) => e.Handled = true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "rename editor: 打开失败");
			if (Application.Current.MainWindow is MainWindow mw)
			{
				mw.ShowToast("改名编辑器失败: " + ex.Message, false);
			}
		}
	}

	public static Button IconButton(string glyphText, double size, string tooltip)
	{
		var glyph = new TextBlock
		{
			Text = glyphText,
			FontFamily = MdAssets,
			FontSize = size * 0.42,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		var btn = new Button
		{
			Width = size,
			Height = size,
			Margin = new Thickness(6, 0, 0, 0),
			ToolTip = tooltip,
			Content = glyph
		};
		btn.Style = GetIconStyle(size);
		return btn;
	}
}
