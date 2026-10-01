using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MusicBox.Services;

namespace MusicBox.Controls;

/// <summary>歌词字幕视图。用法：<see cref="Load"/> 塞一份歌词，再用 <see cref="Start"/> 跟着播放位置走。
///
/// 渲染策略（细节写在 LyricView.xaml 顶部注释里）：
/// 1. 所有行等高。这样「把当前行滚到视口正中」可以直接用 下标×行高 算出来，
///    不必每帧调 BringIntoView 去等布局测量 —— 那样会反复触发布局，滚起来一顿一顿的。
/// 2. 每行叠两层同样的文字，底层是暗色整行，上层是亮色，再用裁剪矩形按进度
///    把上层「擦」出来 —— 这就是卡拉OK式的逐字描色，比逐个 Run 改颜色顺滑。
/// 3. 离当前行越远越淡、字号越小。按距离分层是这类实现里最影响观感的一条：
///    全部行同时动会显得很吵，有层次之后滚动才有「景深」。
/// 4. 用户滚轮翻歌词时暂停自动跟随，停下 2.5 秒再接回来。</summary>
public partial class LyricView : UserControl
{
	/// <summary>单行文字的高度（逻辑像素）。有翻译时行高翻倍。</summary>
	private const double LineHeightPx = 34;

	/// <summary>当前行相对视口正中往下偏多少。留出上方空间放歌名，
	/// 视觉重心才在下方，跟网易云桌面歌词的排布一致。</summary>
	private const double ActiveLineDropPx = 110;

	private const int MaxFontSize = 19;
	private const double MinFontSize = 13.5;

	/// <summary>两行（正文+翻译）时的行高。</summary>
	private const double TwoLineHeightPx = LineHeightPx * 2;

	private readonly List<Row> _rows = new();
	private LyricBundle? _bundle;
	private DispatcherTimer? _timer;
	private int _activeIndex = -1;
	private double _lastProgress;
	private bool _userScrolling;
	/// <summary>用户明确要求「先别跟」。滚轮只是临时让出视口，
	/// 但点了「回到当前句」之后就该一直保持，直到他再点一次或者重新打开窗口。</summary>
	private bool _holdFollow;
	private bool _firstSync = true;
	private DateTime _lastUserScroll = DateTime.MinValue;
	private string? _lyricForPath;
	private bool _showTranslation = true;
	private double _rowHeightPx = LineHeightPx;

	private sealed class Row
	{
		public Grid Host = new();
		public TextBlock Base = new();
		public TextBlock Lit = new();
		/// <summary>擦除用的裁剪区域。UIElement.Clip 是 Geometry，
		/// 所以这里存 RectangleGeometry，每帧改它的 Rect 来推进。</summary>
		public RectangleGeometry Clip = new();
		public string Text = "";
	}

	public LyricView()
	{
		InitializeComponent();
		// 22ms 一次≈45Hz，够顺滑又不至于把 CPU 吃满。
		// 歌词本来就是一秒推进几行，60Hz 纯属浪费。
		_timer = new DispatcherTimer(DispatcherPriority.Render)
		{
			Interval = TimeSpan.FromMilliseconds(22)
		};
		_timer.Tick += (_, _) => Tick();
		// 计时器的生死只听 IsVisibleChanged，不再挂在 Unloaded 上。
		//
		// WPF 里把元素 Visibility 设成 Collapsed 同样会走一遍 Loaded/Unloaded，
		// 而竖排/横排切换正是靠改 Visibility 做的。于是旧写法
		//   Loaded   -> Start
		//   Unloaded -> Stop
		// 会在切到横排时把表停掉；再切回竖排虽然会重新 Loaded，但那时
		// 歌词可能还没到（_bundle == null），Start() 直接 return，
		// 表就再也没起来。表现就是「不关掉重开就不跟随了」。
		// 改成跟着可见性走之后，只有真的看不见才停，一露面就恢复。
		IsVisibleChanged += (_, e) =>
		{
			if (e.NewValue is bool vis && vis)
			{
				Start();
			}
			else
			{
				_timer?.Stop();
			}
		};
		SizeChanged += (_, _) => ScrollToActive(animate: false);
		// 用户主动滚轮翻歌词 -> 让出视口。直接盯 MouseWheel 比从 ScrollChanged
		// 反推可靠得多：后者分不清是程序自己滚的还是用户滚的。
		PreviewMouseWheel += OnPreviewMouseWheel;
	}

	private Brush _brushBase = Brushes.Gray;
	private Brush _brushLit = Brushes.White;

	/// <summary>是否在有翻译时把翻译贴在下一行。</summary>
	public bool ShowTranslation
	{
		get => _showTranslation;
		set
		{
			if (_showTranslation == value || _bundle == null)
			{
				return;
			}
			_showTranslation = value;
			Rebuild();
		}
	}

	/// <summary>换一首歌的歌词。path 用来判断是不是同一首，避免重复重建。</summary>
	public void Load(LyricBundle? bundle, string? path)
	{
		// 必须在覆盖 _lyricForPath 之前把旧值抓出来。
		// 原来这里是先赋 _lyricForPath = path，再拿它跟 path 比——自己跟自己比，
		// 永远相等，于是「同一首歌」的判断形同虚设：结果就是只要两首歌行数相同，
		// 切歌时旧行控件直接被复用，屏幕上留着一首上一首的词。
		string? prevPath = _lyricForPath;
		_lyricForPath = path;
		if (bundle == null)
		{
			_bundle = null;
			LinesHost.Children.Clear();
			_rows.Clear();
			_activeIndex = -1;
			_firstSync = true;
			Stop();
			return;
		}
		if (_bundle != null && string.Equals(prevPath, path, StringComparison.OrdinalIgnoreCase)
			&& _rows.Count == bundle.Count)
		{
			// 同一首歌重开浮层，行还在，直接接着用
			_bundle = bundle;
			_firstSync = true;
			return;
		}
		_bundle = bundle;
		Rebuild();
	}

	/// <summary>按歌词内容重建全部行控件。</summary>
	private void Rebuild()
	{
		LinesHost.Children.Clear();
		_rows.Clear();
		_activeIndex = -1;
		_firstSync = true;
		if (_bundle == null || _bundle.Count == 0)
		{
			Stop();
			return;
		}

		bool dark = ThemeManager.IsDark;
		// 「还没唱到」的部分：深色底下要更暗，浅色底下要更淡
		_brushBase = Frozen(dark ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x8A, 0x1A, 0x1A, 0x1A));
		// 「已唱」的部分：跟正文同一个颜色强度
		_brushLit = Frozen(dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x14, 0x14, 0x14));
		_brushTranslation = Frozen(dark ? Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x1A, 0x1A, 0x1A));

		// 有翻译就所有行都占两倍行高 —— 必须统一，否则 ScrollToActive
		// 里「下标×行高」的算法在遇到翻译行时就全错位了。
		bool anyTranslation = _showTranslation && HasAnyTranslation();
		_rowHeightPx = anyTranslation ? TwoLineHeightPx : LineHeightPx;

		foreach (LyricLine line in _bundle.Lines)
		{
			var row = new Row { Text = line.Text ?? "" };
			// 上下两层：字体、对齐、行高必须完全一致，擦出来的边缘才齐
			row.Base = MakeText(_brushBase, FontWeights.SemiBold);
			row.Lit = MakeText(_brushLit, FontWeights.Bold);
			row.Base.Text = row.Text;
			row.Lit.Text = row.Text;

			row.Clip = new RectangleGeometry(new Rect(0, 0, 0, LineHeightPx));
			// 裁剪挂在 Border 上，Border 紧贴文字大小，
			// 于是「从左边缘擦到 progress×文字宽」正好擦过一个字。
			var clipHost = new Border
			{
				Height = LineHeightPx,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Top,
				Child = row.Lit,
				ClipToBounds = true,
				Clip = row.Clip
			};

			var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
			var main = new Grid { Height = LineHeightPx, Children = { row.Base, clipHost } };
			panel.Children.Add(main);

			if (anyTranslation && !string.IsNullOrWhiteSpace(line.Translation))
			{
				panel.Children.Add(new TextBlock
				{
					Text = line.Translation,
					FontSize = 12.5,
					Foreground = _brushTranslation,
					TextAlignment = TextAlignment.Center,
					HorizontalAlignment = HorizontalAlignment.Center,
					TextWrapping = TextWrapping.NoWrap,
					Height = LineHeightPx,
					Margin = new Thickness(0, 2, 0, 0)
				});
			}

			row.Host = new Grid { Height = _rowHeightPx, Children = { panel } };
			_rows.Add(row);
			LinesHost.Children.Add(row.Host);
		}
		ApplyRowVisuals();
	}

	private Brush _brushTranslation = Brushes.Gray;

	private bool HasAnyTranslation()
	{
		if (_bundle == null)
		{
			return false;
		}
		foreach (LyricLine l in _bundle.Lines)
		{
			if (!string.IsNullOrWhiteSpace(l.Translation))
			{
				return true;
			}
		}
		return false;
	}

	private static TextBlock MakeText(Brush brush, FontWeight weight)
	{
		return new TextBlock
		{
			FontSize = MaxFontSize,
			FontWeight = weight,
			Foreground = brush,
			TextAlignment = TextAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			TextWrapping = TextWrapping.NoWrap,
			Height = LineHeightPx
		};
	}

	private static Brush Frozen(Color c)
	{
		var b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	/// <summary>按「离当前行多远」给每一行定透明度/字号/缩放。
	/// 只在当前行变化时跑一次，不是每帧。</summary>
	private void ApplyRowVisuals()
	{
		int count = _rows.Count;
		if (count == 0)
		{
			return;
		}
		for (int i = 0; i < count; i++)
		{
			Row r = _rows[i];
			bool isActive = i == _activeIndex;
			bool hasText = !string.IsNullOrWhiteSpace(r.Text);

			int delta = Math.Abs(i - _activeIndex);
			// 距离衰减：约三行之外基本淡到最低
			double fade = Math.Clamp(1.0 - delta * 0.17, 0.20, 1.0);

			r.Host.Opacity = hasText ? fade : 0.0;
			if (isActive)
			{
				r.Host.RenderTransform = new ScaleTransform(1.04, 1.04);
				r.Base.FontSize = MaxFontSize;
				r.Lit.FontSize = MaxFontSize;
			}
			else
			{
				r.Host.RenderTransform = null;
				r.Base.FontSize = MinFontSize;
				r.Lit.FontSize = MinFontSize;
			}
		}
	}

	/// <summary>把当前行滚到视口正中偏下一点。行高固定，所以偏移可以直接算。</summary>
	/// <param name="animate">true = 平滑滑过去（换行时），false = 直接落位（首次打开/跳进度时）。</param>
	private void ScrollToActive(bool animate)
	{
		if (_activeIndex < 0 || _rows.Count == 0)
		{
			return;
		}
		double viewport = ActualHeight > 1 ? ActualHeight : 400;
		double target = _activeIndex * _rowHeightPx
			+ _rowHeightPx / 2
			- viewport / 2
			+ ActiveLineDropPx;
		target = Math.Clamp(target, 0, Math.Max(0, Scroller.ScrollableHeight));
		if (Math.Abs(Scroller.VerticalOffset - target) < 0.5 && !animate)
		{
			return;
		}
		if (!animate)
		{
			ScrollLerp = null;
			Scroller.ScrollToVerticalOffset(target);
			return;
		}
		// 这里不能用 BeginAnimation(ScrollViewer.VerticalOffsetProperty, …)：
		// VerticalOffset 带的是 UIPropertyMetadata 且 IsAnimationProhibited=true，
		// WPF 明确禁止对它做动画，BeginAnimation 会抛 ArgumentException。
		// 而且这个调用在 45Hz 的节拍器里，抛一次就是整个进程崩掉。
		// 所以改成自己插值：记下起点/终点/起始时间，由同一个节拍器推进，
		// ScrollToVerticalOffset 每帧调一次，效果一样但不会抛。
		ScrollLerp = new ScrollAnim
		{
			From = Scroller.VerticalOffset,
			To = target,
			Start = DateTime.Now,
			Duration = TimeSpan.FromMilliseconds(420)
		};
	}

	private sealed class ScrollAnim
	{
		public double From;
		public double To;
		public DateTime Start;
		public TimeSpan Duration;
	}

	/// <summary>正在进行的滚动插值，null = 不在动。</summary>
	private ScrollAnim? ScrollLerp;

	/// <summary>推进滚动插值。由节拍器每帧调一次。</summary>
	private void StepScrollLerp()
	{
		ScrollAnim? a = ScrollLerp;
		if (a == null)
		{
			return;
		}
		double t = (DateTime.Now - a.Start).TotalMilliseconds / a.Duration.TotalMilliseconds;
		if (t >= 1)
		{
			ScrollLerp = null;
			Scroller.ScrollToVerticalOffset(a.To);
			return;
		}
		// EaseOut，跟原来 DoubleAnimation 用的 CubicEase 一致
		double e = 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
		Scroller.ScrollToVerticalOffset(a.From + (a.To - a.From) * e);
	}

	/// <summary>启动跟随。播放位置由 MusicService 提供，控件自己按节拍取。</summary>
	public void Start()
	{
		if (_bundle == null || _bundle.Count == 0 || !IsVisible)
		{
			return;
		}
		if (!_timer!.IsEnabled)
		{
			_timer.Start();
		}
		// 表能转了，之前挂起的「已暂停跟随」就没必要继续挂着。
		// 否则会出现提示消失了但视口还没回来的中间态。
		if (_userScrolling && !_holdFollow)
		{
			ResumeToActive();
		}
	}

	/// <summary>立刻交回视口：取消暂停标记，并立刻滚回当前行。</summary>
	private void ResumeToActive()
	{
		_userScrolling = false;
		_holdFollow = false;
		ResumeHint.Visibility = Visibility.Collapsed;
		if (_bundle != null && _bundle.Count > 0)
		{
			// 之前是「等下一行跨行时才 ScrollToActive」，
			// 于是在这一行还没唱完的这几秒里，看着像没恢复跟随。
			// 这里主动叫一次，立刻回到当前行。
			ScrollToActive(animate: true);
		}
	}

	/// <summary>停表。同时丢掉进行中的滚动插值：停表后没人再推进它，
	/// 留着会让下次 Start() 接着上次那个目标滑，看起来像自己动了。</summary>
	public void Stop()
	{
		_timer?.Stop();
		ScrollLerp = null;
		_userScrolling = false;
		_holdFollow = false;
		// 顺手把提示收掉。以前 Stop() 只清了标记没收控件，
		// 于是「关掉窗口再打开」会带着一条陈旧的「已暂停跟随」。
		ResumeHint.Visibility = Visibility.Collapsed;
	}

	/// <summary>每次节拍：算当前行 → 更新擦除进度 → 跨行了才重排。
	/// 擦除进度每帧都要更新（那正是卡拉OK效果的来源），
	/// 而重排/改样式只在跨行时做，所以开销可控。</summary>
	private void Tick()
	{
		LyricBundle? b = _bundle;
		if (b == null || b.Count == 0)
		{
			return;
		}
		// 用户在手动翻歌词就先别抢视口。
		// _holdFollow 是用户明确「按住不放」的意思：点了回到当前句、或关掉重开才放。
		// 单纯滚一下（没按住）仍旧 2.5 秒后自动接回来。
		if (_userScrolling)
		{
			if (_holdFollow)
			{
				return;
			}
			if ((DateTime.Now - _lastUserScroll).TotalSeconds < 2.5)
			{
				return;
			}
			ResumeToActive();
		}

		long posMs;
		try
		{
			posMs = (long)MusicService.Position.TotalMilliseconds;
		}
		catch
		{
			return;
		}

		int idx = b.IndexAt(posMs);
		double progress = idx >= 0 ? b.ProgressOf(idx, posMs) : 0;

		if (idx != _activeIndex)
		{
			bool first = _firstSync;
			_activeIndex = idx;
			_lastProgress = progress;
			_firstSync = false;
			ApplyRowVisuals();
			ScrollToActive(animate: !first);
		}
		else if (idx >= 0)
		{
			_lastProgress = progress;
		}
		// 滚动插值和擦除在同一个节拍里推进，不另开表：
		// 两个 45Hz 的表会互相错开半帧，看着像抖。
		StepScrollLerp();

		// 只擦当前行和紧邻两行。全表每帧过一遍上百个控件，
		// 等于歌词控件自己在制造卡顿。
		if (_activeIndex < 0 || _activeIndex >= _rows.Count)
		{
			return;
		}
		UpdateWipe(_rows[_activeIndex], progress);
		for (int d = 1; d <= 2; d++)
		{
			if (_activeIndex - d >= 0)
			{
				UpdateWipe(_rows[_activeIndex - d], 1);
			}
			if (_activeIndex + d < _rows.Count)
			{
				UpdateWipe(_rows[_activeIndex + d], 0);
			}
		}
	}

	/// <summary>按 0~1 的进度把亮色层「擦」出来。</summary>
	private void UpdateWipe(Row r, double progress)
	{
		bool wantVisible = progress > 0.0005 && !string.IsNullOrWhiteSpace(r.Text);
		r.Lit.Visibility = wantVisible ? Visibility.Visible : Visibility.Collapsed;
		if (!wantVisible)
		{
			r.Clip.Rect = new Rect(0, 0, 0, LineHeightPx);
			return;
		}
		double textWidth = r.Lit.ActualWidth;
		if (textWidth <= 0)
		{
			// 还没量过：这行在虚拟化视口外，等它进视口自然会量到
			return;
		}
		r.Clip.Rect = new Rect(0, 0, Math.Clamp(progress, 0, 1) * textWidth, LineHeightPx);
	}

	private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		// 原来这里有一句 Scroller.BeginAnimation(ScrollViewer.VerticalOffsetProperty, null)，
		// 想着「先掐掉进行中的滚动动画」。但 VerticalOffset 是 UIPropertyMetadata
		// 且 IsAnimationProhibited=true，压根不能拿来做动画，BeginAnimation 直接抛
		// ArgumentException。它跑在鼠标输入管线里，没人接得住 → 整个进程崩掉。
		// 滚轮只是设个「用户接管了」的标记，本来就不需要动动画。
		_userScrolling = true;
		_lastUserScroll = DateTime.Now;
		// 手动翻过之后就别再用 2.5 秒自动抢回来了——用户可能是在看上一句，
		// 突然被拽回当前行很烦。改成给一个可点的开关，由他决定什么时候回来。
		_holdFollow = true;
		ResumeHint.Visibility = Visibility.Visible;
		ResumeHintText.Text = Loc.Default["lyric_pause_follow"];
	}

	/// <summary>点「回到当前句」：交回视口。</summary>
	private void ResumeHint_Click(object sender, RoutedEventArgs e)
	{
		ResumeToActive();
		e.Handled = true;
	}
}
