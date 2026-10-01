using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public static class TrackRows
{
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

	private static string? _playingPath;

	/// <summary>当前正在播放的路径。各行自己订阅这个事件去改自己的描边/底色，
	/// 不用页面拿着行控件的引用到处手动刷新。</summary>
	public static event Action? PlayingChanged;

	/// <summary>切换/清空「正在播放」高亮。传 null 或不存在的路径等于取消高亮。</summary>
	public static void SetPlayingTrack(string? path)
	{
		_playingPath = string.IsNullOrWhiteSpace(path) ? null : path;
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
	/// 但 Type 字段记了来源，所以照样能显示平台。</summary>
	public static (string Label, Brush Brush)? SavedEngineTag(SavedTrack track)
	{
		if (track == null)
		{
			return null;
		}
		return track.Type switch
		{
			"netease" => ("网易云", new SolidColorBrush(Color.FromRgb(0xC2, 0x0C, 0x0C))),
			"kugou" => ("酷狗", new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x00))),
			"qq" => ("QQ音乐", new SolidColorBrush(Color.FromRgb(0x31, 0xC2, 0x7C))),
			"bili" => ("BiliBili", new SolidColorBrush(Color.FromRgb(0xFB, 0x72, 0x99))),
			"local" => ("本地", new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))),
			_ => null
		};
	}

	/// <summary>收藏/最近播放用的一行。跟搜索页一样给平台标签，
	/// 不然本地下载的和各平台在线的歌混在一起完全分不出来源。</summary>
	public static Grid BuildSavedRow(SavedTrack track, Action onPlay, bool isFav, Action? onToggleFav, Action? onRemove)
	{
		(string label, Brush brush)? tag = SavedEngineTag(track);
		// 副标题只用歌手。本地文件往往没有歌手字段，
		// 早先的写法是拿 Title 顶上，结果标题和副标题一模一样，白占一行。
		string sub = track.Artist ?? "";
		if (track.DurationMs > 0)
		{
			sub += (sub.Length > 0 ? "  ·  " : "") + FormatDuration(track.DurationMs);
		}
		return BuildRow(track, sub, onPlay, isFav, onToggleFav, onRemove, null, null,
			tag?.label, tag?.brush);
	}

	public static Grid BuildRow(SavedTrack track, string? subOverride, Action onPlay, bool isFav, Action? onToggleFav, Action? onRemove = null, Action? onComment = null, Action? onDownload = null, string? engineLabel = null, Brush? engineBrush = null, string? highlight = null, string? onCommentIcon = null, string? onCommentTip = null)
	{
		// 行高做矮一点：封面 46→38、上下间距 7→5，一屏能多露出三四行，
		// 搜索结果基本不用再拖滚动条（用户要求「高度小一点都展示在一个页内」）。
		var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
		// 明确要求拉满宽度。中间那列是 Star，靠它吃掉多余空间把右侧按钮推到行尾。
		// 一旦这行被放进没撑开的容器里（StackPanel、ListBoxItem 没设 Stretch），
		// Star 列会缩成标题的宽度，播放键就紧贴标题末尾跑，不再是整齐的一列。
		grid.HorizontalAlignment = HorizontalAlignment.Stretch;
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		Image cover = BuildCover(track.Cover, 38);
		Grid.SetColumn(cover, 0);
		grid.Children.Add(cover);

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
		var subBlock = new TextBlock
		{
			Text = sub,
			FontSize = 12.5,
			Foreground = ThemeManager.Secondary(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			Margin = new Thickness(0, 3, 0, 0)
		};
		var textPanel = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
		if (!string.IsNullOrEmpty(engineLabel))
		{
			var chip = new Border
			{
				Background = engineBrush ?? ThemeManager.Accent(),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(6, 1.5, 6, 1.5),
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 0, 6, 0),
				Child = new TextBlock
				{
					Text = engineLabel,
					FontSize = 10.5,
					FontWeight = FontWeights.SemiBold,
					Foreground = OnAccentBrush
				}
			};
			var headPanel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
			DockPanel.SetDock(chip, Dock.Left);
			headPanel.Children.Add(chip);
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

		var actions = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			// 操作区贴行尾，并和标题留出一段固定间距：
			// 标题很长被省略号截断时，播放键不会紧贴着那几个点。
			HorizontalAlignment = HorizontalAlignment.Right
		};
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
			commentBtn.Click += (_, _) => onComment();
			actions.Children.Add(commentBtn);
		}
		Button? heart = null;
		if (onToggleFav != null)
		{
			heart = IconButton("\uEB51", 32, Loc.Default["act_fav"]);
			heart.Foreground = ThemeManager.Secondary();
			heart.Click += (_, _) =>
			{
				onToggleFav();
				SetHeartVisual(heart, UserLibrary.IsFavorite(track));
			};
			actions.Children.Add(heart);
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
			Padding = new Thickness(14, 5, 14, 5),
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

		// 只淡化，不缩放：之前 MouseEnter 会把整行放大到 1.014，离开时又没把底色
		// 还原，于是鼠标扫过之后背景块一直挂着，看起来像「又冒出标签背景」。
		border.MouseEnter += (_, _) => ApplyState(true, IsPlaying(track));
		border.MouseLeave += (_, _) => ApplyState(false, IsPlaying(track));

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
			ApplyState(playing ? true : false, playing);
			playingBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(playing ? 1 : 0, TimeSpan.FromMilliseconds(150)));
		}
		// 行会离开可视树（翻页时 Items.Clear），之后还可能被缓存复用再放回来。
		// 所以必须成对地「Loaded 订阅 / Unloaded 退订」：
		// 以前只在 Unloaded 里 -= ，那么行离开一次可视树之后
		// 再放回来就再也不会收到 PlayingChanged，正在播放的强调条会失灵；
		// 而从没进过可视树的行又永远退不掉，会被静态事件一直钉住。
		host.Loaded += (_, _) => PlayingChanged += OnPlayingChanged;
		host.Unloaded += (_, _) => PlayingChanged -= OnPlayingChanged;
		// 已经在树里的话 Loaded 不会再触发，先补一次订阅。
		if (host.IsLoaded)
		{
			PlayingChanged += OnPlayingChanged;
		}

		ApplyState(false, IsPlaying(track));
		return host;
	}

	private static bool IsPlaying(SavedTrack track)
	{
		string? playing = _playingPath;
		return !string.IsNullOrEmpty(playing)
			&& string.Equals(playing, track.Path, StringComparison.OrdinalIgnoreCase);
	}

	private static Color AccentColor() =>
		AccentBrush is SolidColorBrush sb ? sb.Color : Color.FromRgb(0x3C, 0x8C, 0xFF);

	private static void SetHeartVisual(Button heart, bool fav)
	{
		if (heart.Content is TextBlock glyph)
		{
			glyph.Text = fav ? "\uEB52" : "\uEB51";
		}
		heart.Foreground = fav
			? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
			: ThemeManager.Secondary();
		heart.ToolTip = fav ? Loc.Default["act_unfav"] : Loc.Default["fav_save_tip"];
	}

	/// <summary>把标题里命中搜索词的部分拆成 Run，命中段用强调色加粗显示。
	/// 大小写不敏感，先定位再按原串切片，不会破坏原文。</summary>
	private static void ApplyHighlight(TextBlock block, string title, string keyword)
	{
		block.Inlines.Clear();
		if (string.IsNullOrEmpty(title))
		{
			return;
		}
		if (string.IsNullOrEmpty(keyword))
		{
			block.Inlines.Add(new Run(title));
			return;
		}
		int from = 0;
		while (from < title.Length)
		{
			int hit = title.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
			if (hit < 0)
			{
				block.Inlines.Add(new Run(title.Substring(from)));
				break;
			}
			if (hit > from)
			{
				block.Inlines.Add(new Run(title.Substring(from, hit - from)));
			}
			block.Inlines.Add(new Run(title.Substring(hit, keyword.Length))
			{
				Foreground = ThemeManager.Accent(),
				FontWeight = FontWeights.Bold
			});
			from = hit + keyword.Length;
		}
	}

	private static string BuildSub(SavedTrack t)
	{
		string parts = string.IsNullOrWhiteSpace(t.Artist) ? t.Title : t.Artist;
		if (t.DurationMs > 0)
		{
			parts += "  ·  " + FormatDuration(t.DurationMs);
		}
		return parts;
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
			return "00:00";
		}
	}

	public static Image BuildCover(string url, double size)
	{
		var img = new Image
		{
			Width = size,
			Height = size,
			Stretch = Stretch.UniformToFill,
			Clip = new RectangleGeometry(new Rect(0, 0, size, size), 7, 7)
		};
		SetCover(img, url);
		return img;
	}

	private static void SetCover(Image img, string url)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return;
			}
			if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				// 网络封面：按来源带 Referer 异步拉取，防盗链 CDN 也能正常显示
				CoverLoader.LoadAsync(img, url);
				return;
			}
			var bi = new BitmapImage();
			bi.BeginInit();
			bi.UriSource = new Uri(url);
			bi.DecodePixelWidth = 128;
			bi.EndInit();
			bi.Freeze();
			img.Source = bi;
		}
		catch
		{
		}
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
