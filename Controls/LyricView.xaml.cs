using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MusicBox.Services;
using MusicBox.Settings;
using MusicBox.Views;

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
/// 4. 用户滚轮翻歌词时暂停自动跟随，停下 3 秒再接回来（点行跳转不算，
///    点了立刻交还视口）。</summary>
public partial class LyricView : UserControl
{
	// 行高/字号常量已集中到 Views/LyricVisualTheme（横竖排共用一份）。

	private readonly List<Row> _rows = new();
	private LyricBundle? _bundle;
	private DispatcherTimer? _timer;
	private int _activeIndex = -1;
	private double _lastProgress;
	/// <summary>ApplyRowVisuals 上次落位时的当前行。用来区分「换行过渡」
	/// 和「同状态重入」，字号/缩放过渡只在前者播。</summary>
	private int _appliedActiveIndex = int.MinValue;
	private bool _userScrolling;
	private bool _firstSync = true;
	private DateTime _lastUserScroll = DateTime.MinValue;
	private string? _lyricForPath;
	private bool _showTranslation = true;
	private double _rowHeightPx = LyricVisualTheme.LineHeightPx;

	/// <summary>歌词列表底部的垫高块。ScrollToVerticalOffset 滚不过内容末尾，
	/// 不垫的话「当前句 = 最后一行」时只能贴到内容最底端才看得见它——
	/// 屏幕上就是歌词沉到底边、半截泡在底部渐隐带里（用户报的「沉底」）。
	/// 垫上「半个视口 + 一行 + 下沉量」之后，最后一行也能滚到视口正中，
	/// 首行定位不受影响（顶部不垫，滚轮往上也翻不出一片空白）。</summary>
	private Border? _bottomPad;

	/// <summary>底部垫高要多高，按当前视口高度算。视口会随窗口缩放变，
	/// 所以量到新的 ActualHeight 时要重算（见 SizeChanged）。</summary>
	private double ComputeBottomPad()
	{
		double viewport = ActualHeight > 1 ? ActualHeight : 400;
		return viewport / 2 + _rowHeightPx + Math.Max(0, ActiveDropPx);
	}

	// ── 诊断日志（[Diag] 永远落盘，不看调试开关）──
	// 排「歌词反方向滚/换句往下跳」用：换行、滚动落位、视口跳变、
	// 每秒一行的状态（位置/行号/偏移）都进日志，方向一看便知。
	// 查完把 LogDiag 调用删掉即可。

	/// <summary>日志里的实例名：全屏页是 FsLyrics，桌面歌词窗是 VerticalView，
	/// 两个实例的日志靠它分开。</summary>
	private string DiagTag => string.IsNullOrEmpty(Name) ? "LyricView" : Name;

	private double _lastTickOff;
	private long _lastBadPos = long.MinValue;

	/// <summary>每拍开头扫一眼视口偏移：两次节拍之间偏移动了 25px 以上
	/// 就记一笔（Δ 正 = 内容上移，负 = 下移）。ScrollToActive 的日志
	/// 记「程序想去哪」，这条记「实际去了哪」——两行一对比，
	/// 是滚动逻辑的问题还是别的东西在抢滚动，立刻分得清。</summary>
	private void DiagJumpWatch()
	{
		try
		{
			double off = _offset;
			double d = off - _lastTickOff;
			if (Math.Abs(d) > 25)
			{
				LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 视口跳变 {_lastTickOff:F0}→{off:F0} Δ={d:+0;-0} lerp={(ScrollLerp != null)} idx={_activeIndex}");
			}
			_lastTickOff = off;
		}
		catch
		{
		}
	}

	/// <summary>当前的视口偏移（px，0 = 从顶开始）。
	///
	/// 为什么不用 ScrollViewer.ScrollToVerticalOffset 而自己算：沉浸式播放页里
	/// 日志实测 ScrollViewer 的 ExtentHeight 恒等于视口高度（ext=300 vp=300），
	/// ScrollableHeight 为 0，任何 ScrollToVerticalOffset 都被钳到 0 —— 表现就是
	/// 「当前句高亮/擦除都正常，歌词却永远不动」。外部因素（尺寸频繁变化、
	/// 外层布局）会把这套测高带偏，而行的几何是确定的（行数×行高+底部垫高），
	/// 用 TranslateTransform 按偏移直接平移整列，不依赖 ScrollViewer 的测高。</summary>
	private double _offset;

	/// <summary>整列歌词的平移基准。把 LinesHost 的 RenderTransform 设成它，
	/// SetOffset 只改 Y 一个分量，列表整体上下滑动。</summary>
	private readonly TranslateTransform Shift = new();

	/// <summary>把整列歌词平移 -v 像素。平移是列表整体，行自身的
	/// ScaleTransform（当前句 1.04）/TranslateTransform（冒头动画）叠在上面
	/// 互不干扰。</summary>
	private void SetOffset(double value)
	{
		_offset = value;
		Shift.Y = -value;
	}

	/// <summary>能滚到的最远偏移 = 内容高 - 视口高。内容高由已知几何算出：
	/// 行数×行高 + 底部垫高，不读 ScrollViewer 的 ExtentHeight。</summary>
	private double MaxOffset()
	{
		double content = _rows.Count * _rowHeightPx + (_bottomPad?.Height ?? 0);
		double viewport = ActualHeight > 1 ? ActualHeight : 400;
		return Math.Max(0, content - viewport);
	}

	/// <summary>额外行距（逻辑像素）：只加在每行的占位高度上，文字块仍是
	/// LyricVisualTheme.LineHeightPx 高，效果是行与行之间留出空隙。
	/// 桌面歌词窗默认 0（和原来一样）；全屏页要「字幕间距加大」，
	/// 在构造里设大一点即可。滚动定位用的 _rowHeightPx 会一并变大，
	/// 「下标 × 行高」的算法天然跟着走，不会错位。</summary>
	public double RowSpacing { get; set; }

	/// <summary>当前行相对视口正中往下偏多少。默认取主题常量（那是给
	/// 桌面歌词窗调的：上方要留歌名的位置）。全屏页的歌词控件上方本来
	/// 就压着标题和歌手，再往下顶 110px 会把当前句推进底部渐隐带
	/// （250px 高的视口里落在 94% 处，蒙版只剩四成不透明度），
	/// 看起来就是「歌词一开始见不到」。全屏页把它设小。</summary>
	public double ActiveDropPx { get; set; } = LyricVisualTheme.ActiveLineDropPx;

	/// <summary>字幕形态（沉浸页）切句动画总开关：挂上后换句直接落位，
	/// 不播 Float/PushUp 过渡。全屏页右下角面板的「隐藏动画」控制它。</summary>
	public bool DisableSubtitleAnim { get; set; }

	/// <summary>自动跟随时，唱过的行最多往回保留几句（当前句上面）。
	/// 原来是唱过一行藏一行：当前句一推进，上方整片变空白，
	/// 往回滚只能看到「还没唱的」，用户要的「唱完前几句」完全不存在；
	/// 歌词走到结尾时更是只剩最后一行，整屏看着像歌词消失了。</summary>
	// ── 字幕形态（UseSubtitle=true，沉浸式全屏页用）──
	// 桌面歌词窗的「横排字幕带」是现成、稳定的一套：上一句/当前句/下一句三行
	// + 译文一行，切句动画全部由 LyricAnimTemplates 模板参数驱动。
	// 这里把那一套直接搬进程序内 —— 用户拍板：列表式整屏滚动退场，
	// 字幕形态进屋。XAML 结构见 LyricView.xaml 的 SubtitlePanel。

	private bool _useSubtitle;

	// 当前句一格：上下摆动 / 选中缩放 / PushUp 竖向位移（顺序见 ctor）。
	private readonly TranslateTransform _subBob = new();
	private readonly ScaleTransform _subScale = new(1, 1);
	private readonly TranslateTransform _subCurShift = new();

	// 上/下一句：PushUp 位移 + 缩放。
	private readonly TranslateTransform _subPrevShift = new();
	private readonly ScaleTransform _subPrevScale = new(1, 1);
	private readonly TranslateTransform _subNextShift = new();
	private readonly ScaleTransform _subNextScale = new(1, 1);

	// 前三句/后三句的陪衬行：只挂竖向位移（字号本来就小，不缩放）。
	private readonly TranslateTransform _subPrev2Shift = new();
	private readonly TranslateTransform _subPrev3Shift = new();
	private readonly TranslateTransform _subNext2Shift = new();
	private readonly TranslateTransform _subNext3Shift = new();

	// 译文两层：横向推挤。
	private readonly TranslateTransform _subTrIn = new();
	private readonly TranslateTransform _subTrOut = new();

	private bool _subBobRunning;
	private int _subTrGen;

	// 沉浸式字幕「前三句/后三句」各行的静止透明度。
	// XAML 里给了同值作布局默认，切句/动画由代码按这些常量覆盖，
	// 两处必须一致，改这个就得同步改 XAML 的 Opacity。
	private const double SubPrev3Op = 0.10;
	private const double SubPrev2Op = 0.20;
	private const double SubPrevOp = 0.45;
	private const double SubNextOp = 0.78;
	private const double SubNext2Op = 0.32;
	private const double SubNext3Op = 0.16;

	/// <summary>当前生效的动画模板。每次切句时重新取（Resolve 有短路），
	/// 设置里换模板不重启就生效 —— 和桌面歌词窗同一个写法。</summary>
	private LyricAnimTemplate SubAnim => LyricAnimTemplates.Resolve(
		SettingsManager.Instance.LyricAnimTemplate,
		SettingsManager.Instance.LyricAnimTemplateFolder);

	private const int KeepSungLines = 3;

	// 列表形态当前行动效：唱到这句时向上抬起并额外放大（和上下行错开），
	// 入场从右下角往左上角放大，退场往左下角缩小再回到行位。
	private const double ActiveRowScale = 1.08;
	private const double ActiveRowLiftPx = 10.0;
	private const double EnterShiftX = 36.0;
	private const double EnterShiftY = 24.0;
	private const double EnterScaleFrom = 0.86;
	private const double ExitShiftX = 30.0;
	private const double ExitShiftY = 14.0;
	private const double ExitScaleTo = 0.88;

	private sealed class Row
	{
		public Grid Host = new();
		public TextBlock Base = new();
		public TextBlock Lit = new();
		/// <summary>擦除用的裁剪区域。UIElement.Clip 是 Geometry，
		/// 所以这里存 RectangleGeometry，每帧改它的 Rect 来推进。</summary>
		public RectangleGeometry Clip = new();
		public string Text = "";
		/// <summary>这一行的起始时间（毫秒）。点行跳转时按它 Seek。</summary>
		public long TimeMs;
		/// <summary>译文子行。没有译文的行是 null，刷色时跳过。</summary>
		public TextBlock? Trans;
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
		SizeChanged += (_, _) =>
		{
			// 视口高度变了，底部垫高要跟着换，否则最后一句的居中量是按旧
			// 视口算的，缩放窗口后又会沉底/够不着。换完立刻落位一次。
			if (_bottomPad != null)
			{
				_bottomPad.Height = ComputeBottomPad();
			}
			ScrollToActive(animate: false);
		};
		// 用户主动滚轮翻歌词 -> 让出视口。直接盯 MouseWheel 比从 ScrollChanged
		// 反推可靠得多：后者分不清是程序自己滚的还是用户滚的。
		PreviewMouseWheel += OnPreviewMouseWheel;
		LinesHost.RenderTransform = Shift;

		// 行颜色/辉光是代码里算死的刷子，不是 DynamicResource。
		// 竖排只活在桌面歌词窗里，配色按 LyricVisualTheme 的定值走
		// （不随应用主题）：所以这里刻意**不**订阅 ThemeManager.ThemeChanged——
		// 订阅的话主题一切颜色重算一遍，桌面歌词跟着应用主题变色，
		// 和「桌面歌词严格用配置色/定值、不随主题」的要求正好相反。
		// 原订阅防的「浅色主题下深灰字钉在深色窗上」问题，
		// 现在所有回退都是固定白系，该问题不存在了。

		// 字幕形态的变换装配（对照桌面歌词窗 LyricWindow.OnSourceInitialized）：
		// 当前句一格同时挂「上移位移 + 上下摆动 + 选中缩放」三个变换。
		// 顺序照着桌面窗那套：上移在前、摆动缩放在后，WPF 按顺序相乘，
		// 反过来的话上移会被摆动/缩放一起带着挪。
		SubWipeHost.RenderTransformOrigin = new Point(0.5, 0.5);
		SubWipeHost.RenderTransform = new TransformGroup
		{
			Children = { _subCurShift, _subBob, _subScale }
		};
		// 上/下一句各一组「竖向位移 + 缩放」，给 PushUp 三行错位动画用。
		SubPrev.RenderTransform = new TransformGroup
		{
			Children = { _subPrevShift, _subPrevScale }
		};
		SubPrev.RenderTransformOrigin = new Point(0.5, 0.5);
		SubNext.RenderTransform = new TransformGroup
		{
			Children = { _subNextShift, _subNextScale }
		};
		SubNext.RenderTransformOrigin = new Point(0.5, 0.5);
		// 前三句/后三句陪衬行各挂「竖向位移」：PushUp 整块错位上移时跟着挪。
		SubPrev2.RenderTransform = new TransformGroup { Children = { _subPrev2Shift } };
		SubPrev3.RenderTransform = new TransformGroup { Children = { _subPrev3Shift } };
		SubNext2.RenderTransform = new TransformGroup { Children = { _subNext2Shift } };
		SubNext3.RenderTransform = new TransformGroup { Children = { _subNext3Shift } };
		// 译文两层各挂一个横向位移，才能做出「新句子把旧句子往右顶」。
		SubTranslation.RenderTransform = _subTrIn;
		SubTranslationOut.RenderTransform = _subTrOut;
	}

	private Brush _brushBase = Brushes.Gray;
	private Brush _brushLit = Brushes.White;

	/// <summary>当前行的辉光。整行挂同一个冻结实例；只在当前行有，别的不挂。
	/// 逐个字的擦除变亮之外，整行带一层软光晕，跳行时焦点一眼就看出来。</summary>
	private System.Windows.Media.Effects.DropShadowEffect? _glow;

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

	/// <summary>字幕形态开关。开 = 隐藏整屏滚动列表，显示三行字幕带
	/// （当前句居中大字 / 上一句下一句淡显 / 译文单独一行），
	/// 切句动画走 LyricAnimTemplates。关 = 走原来的整屏列表（桌面歌词窗竖排）。</summary>
	public bool UseSubtitle
	{
		get => _useSubtitle;
		set
		{
			_useSubtitle = value;
			Scroller.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
			SubtitlePanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
			ResumeHint.Visibility = Visibility.Collapsed;
			// 字幕形态的上下边缘行是半显的「额外」行，不能再叠上滚动的渐隐；
			// 列表形态才要滚到边缘淡出。
			LvRoot.OpacityMask = value ? null : LvEdgeMask;
			// 列表形态的手动翻页/滚动插值在字幕形态下没有意义，先收拾干净。
			_userScrolling = false;
			ScrollLerp = null;
			if (value)
			{
				_firstSync = true;
				ApplySubtitleNow();
				if (IsVisible)
				{
					Start();
				}
			}
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
			_bottomPad = null;
			_activeIndex = -1;
			_firstSync = true;
			Stop();
			// 字幕形态同理：没歌词就清空三行，别留上一首的字幕。
			if (_useSubtitle)
			{
				SubCurBase.Text = "";
				SubCurLit.Text = "";
				SubPrev.Text = "";
				SubNext.Text = "";
				SubPrev2.Text = "";
				SubNext2.Text = "";
				CollapseSubTranslation();
				StopSubBob();
				ResetSubRowShifts();
			}
			return;
		}
		if (_bundle != null && string.Equals(prevPath, path, StringComparison.OrdinalIgnoreCase)
			&& SameLyricContent(_bundle, bundle))
		{
			// 同一首歌重开浮层，行还在，直接接着用。
			// 不能只比行数：同一路径重新拉到修正版歌词时，行数可能一样，
			// 但文本已经变了；复用旧行会留下上一版歌词。
			_bundle = bundle;
			_firstSync = true;
			if (_useSubtitle)
			{
				ApplySubtitleNow();
			}
			// 关键的一句：Load 可能发生在控件已经可见之后（全屏页先显示、
			// 歌词异步拉回来才送），而节拍器只在 IsVisibleChanged 里起过一次
			// 那时 _bundle 还是 null、Start() 直接 return——表就永远停着，
			// 当前句不亮也不滚，正是「一开始见不到」的另一半原因。
			// Start 幂等（已开会跳过），可见就补一次。
			if (IsVisible)
			{
				Start();
			}
			return;
		}
		_bundle = bundle;
		Rebuild();
		// 同上：重建完确保表在转，不能只靠可见性事件那一次机会。
		if (IsVisible)
		{
			Start();
		}
	}

	/// <summary>判断两份 bundle 是否可以直接复用已有行控件。
	/// 比行数、每行时间/文本/译文——只在 Load 时跑一次，几百行也是零头，
	/// 但能避免「同路径、同行数、歌词内容已变」时把旧文本留在屏幕上。</summary>
	private static bool SameLyricContent(LyricBundle a, LyricBundle b)
	{
		if (ReferenceEquals(a, b))
		{
			return true;
		}
		if (a.Count != b.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Count; i++)
		{
			LyricLine x = a.Lines[i];
			LyricLine y = b.Lines[i];
			if (x.TimeMs != y.TimeMs
				|| !string.Equals(x.Text, y.Text, StringComparison.Ordinal)
				|| !string.Equals(x.Translation, y.Translation, StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>按歌词内容重建全部行控件。</summary>
	private void Rebuild()
	{
		LinesHost.Children.Clear();
		_rows.Clear();
		_bottomPad = null;
		_activeIndex = -1;
		_firstSync = true;
		// 行全是新建的，别让 ApplyRowVisuals 以为「当前句没变」而跳过样式
		//（换歌后行号相同就会踩中，当前句没了辉光和放大）。
		_appliedActiveIndex = int.MinValue;
		if (_bundle == null || _bundle.Count == 0)
		{
			Stop();
			return;
		}

		// 行刷子初值按固定色（LyricVisualTheme.Dl*）落笔，
		// ApplyCustomColors 再按设置覆盖。这里同样不读主题：
		// 竖排的观感不该被应用主题左右。
		_brushBase = Frozen(LyricVisualTheme.DlUnsung);
		_brushLit = Frozen(LyricVisualTheme.DlSung);
		_brushTranslation = Frozen(LyricVisualTheme.DlTranslation);
		ApplyCustomColors();

		// 当前行的辉光固定白晕：竖排只活在桌面歌词窗的暗底语境里。
		RebuildGlow();

		// 有翻译就所有行都占两倍行高 —— 必须统一，否则 ScrollToActive
		// 里「下标×行高」的算法在遇到翻译行时就全错位了。
		// RowSpacing 是实例级的额外行距（全屏页用来把字幕行距拉开）。
		bool anyTranslation = _showTranslation && HasAnyTranslation();
		_rowHeightPx = (anyTranslation ? LyricVisualTheme.TwoLineHeightPx : LyricVisualTheme.LineHeightPx)
			+ RowSpacing;

		foreach (LyricLine line in _bundle.Lines)
		{
			var row = new Row { Text = line.Text ?? "", TimeMs = line.TimeMs };
			// 上下两层：字体、对齐、行高必须完全一致，擦出来的边缘才齐
			row.Base = MakeText(_brushBase, FontWeights.SemiBold);
			row.Lit = MakeText(_brushLit, FontWeights.Bold);
			row.Base.Text = row.Text;
			row.Lit.Text = row.Text;

			row.Clip = new RectangleGeometry(new Rect(0, 0, 0, LyricVisualTheme.LineHeightPx));
			// 裁剪挂在 Border 上，Border 紧贴文字大小，
			// 于是「从左边缘擦到 progress×文字宽」正好擦过一个字。
			var clipHost = new Border
			{
				Height = LyricVisualTheme.LineHeightPx,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Top,
				Child = row.Lit,
				ClipToBounds = true,
				Clip = row.Clip
			};

			var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
			var main = new Grid { Height = LyricVisualTheme.LineHeightPx, Children = { row.Base, clipHost } };
			panel.Children.Add(main);

			if (anyTranslation && !string.IsNullOrWhiteSpace(line.Translation))
			{
				row.Trans = new TextBlock
				{
					Text = line.Translation,
					FontSize = 12.5,
					Foreground = _brushTranslation,
					TextAlignment = TextAlignment.Center,
					HorizontalAlignment = HorizontalAlignment.Center,
					TextWrapping = TextWrapping.NoWrap,
					Height = LyricVisualTheme.LineHeightPx,
					Margin = new Thickness(0, 2, 0, 0)
				};
				panel.Children.Add(row.Trans);
			}

			row.Host = new Grid { Height = _rowHeightPx, Children = { panel }, RenderTransformOrigin = new Point(0.5, 0.5) };
			// 点一行 = 跳到这句唱的那一刻（用户要的「跳转到该歌词」）。
			// 空行（纯标记/间隔行）不给手型也不响应。
			if (!string.IsNullOrWhiteSpace(row.Text))
			{
				row.Host.Cursor = Cursors.Hand;
				Row captured = row;
				row.Host.MouseLeftButtonUp += (_, e) => RowHost_Click(captured, e);
			}
			_rows.Add(row);
			LinesHost.Children.Add(row.Host);
		}
		// 底部垫高必须在所有行之后：垫块在内容末尾才有意义。
		_bottomPad = new Border { Height = ComputeBottomPad() };
		LinesHost.Children.Add(_bottomPad);
		ApplyRowVisuals();
		// 行刚建好就落位一次：不然前奏期（_activeIndex 还是 -1）没人调
		// ScrollToActive，视口停在 0——底部垫高之后这个位置只剩空白。
		ScrollToActive(animate: false);
		// 字幕形态没有「列表需要落位」这回事，直接按当前进度画一帧。
		if (_useSubtitle)
		{
			ApplySubtitleNow();
		}

		// [Diag] 重建一次的几何快照：行要真占行高，内容高才撑得起来，
		// Scroller 测高失灵（ext=vp）时只靠这几个数就能分清单是布局问题
		// 还是滚动路径没走通。
		LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 重建 行数={_rows.Count} 行高={_rowHeightPx:F0} 内容={_rows.Count * _rowHeightPx + (_bottomPad?.Height ?? 0):F0} 视口={ActualHeight:F0} 可滚={MaxOffset():F0} 翻译={anyTranslation}");
	}

	private Brush _brushTranslation = Brushes.Gray;

	/// <summary>设置页改了歌词颜色就调这里。竖排以前完全不看这两个设置，
	/// 于是用户挑了颜色、在默认的竖排模式下却什么都没发生。
	///
	/// 两个主色分别是：「已唱的那一段」和「还没唱到的那一段」；
	/// 第三个是译文子行的颜色。
	/// 传进来的十六进制为空 = 该层跟随主题，所以下面会先把主题色重算回来，
	/// 再覆盖用户指定的那一层——不然清空设置之后颜色会卡在上一次的取值。</summary>
	public void ApplyColors(string? litHex, string? baseHex, string? trHex)
	{
		try
		{
			_litHex = litHex;
			_baseHex = baseHex;
			_trHex = trHex;
			ApplyCustomColors();
		}
		catch { }
	}

	private string? _litHex;

	private string? _baseHex;

	private string? _trHex;

	// ==================== 穿透态着色 ====================
	//
	// 2026-11-05 按用户要求收窄：穿透时只有 LyricWindow 的工具/播放按钮
	// 变黑（「选项变黑」），竖排文字颜色由 ApplyCustomColors 统一管理
	// （用户配置色优先，空则 Dl* 定值），不参与穿透着色。

	/// <summary>穿透着色统一入口。2026-11-05 起着色只作用于 LyricWindow 的
	/// 工具/播放按钮（「选项变黑」），竖排歌词文字不再参与——本方法保留
	/// 空实现仅为兼容调用点，文字颜色随时跟随用户配置/主题定值。
	/// 原深色系画刷/基线/辅助动画成员已随整窗变黑方案一并移除。</summary>
	public void SetPassthroughTint(bool on)
	{
	}

	/// <summary>把三支现役画刷铺到所有已建好的行与字幕元素上。
	/// ApplyCustomColors 与穿透着色共用：着色换画刷后也要重铺一遍。</summary>
	private void SpreadBrushes()
	{
		foreach (Row r in _rows)
		{
			if (r.Base != null) r.Base.Foreground = _brushBase;
			if (r.Lit != null) r.Lit.Foreground = _brushLit;
			if (r.Trans != null) r.Trans.Foreground = _brushTranslation;
		}
		// 字幕形态当前句两层也吃这份自定义色（和列表同一套语义：
		// 底层=还没唱到、亮层=已唱）。不刷的话沉浸页的字幕会一直停留在
		// 主题色上，改了设置毫无反应。
		if (_useSubtitle)
		{
			if (SubCurBase != null) SubCurBase.Foreground = _brushBase;
			if (SubCurLit != null) SubCurLit.Foreground = _brushLit;
			// 字幕条的颜色语义跟弹窗歌词一致，按「已唱/待唱」分层：
			//  上三行（P3/P2/P）是唱过的 → 用已唱色，唱完那句后颜色保持住；
			//  当前句底层是未唱色、亮层擦出已唱色；
			//  下三行（N/N2/N3）还没唱到 → 用未唱色。
			// 深浅全靠 XAML 自带 Opacity 分层（0.10/0.20/0.45 … 0.78/0.32/0.16）。
			if (SubPrev3 != null) SubPrev3.Foreground = _brushLit;
			if (SubPrev2 != null) SubPrev2.Foreground = _brushLit;
			if (SubPrev != null) SubPrev.Foreground = _brushLit;
			if (SubNext != null) SubNext.Foreground = _brushBase;
			if (SubNext2 != null) SubNext2.Foreground = _brushBase;
			if (SubNext3 != null) SubNext3.Foreground = _brushBase;
			if (SubTranslation != null) SubTranslation.Foreground = _brushTranslation;
			if (SubTranslationOut != null) SubTranslationOut.Foreground = _brushTranslation;
		}
	}

	private void ApplyCustomColors()
	{
		// 回退色全部是定值（LyricVisualTheme.Dl*），不读 ThemeManager.IsDark：
		// 竖排只出现在桌面歌词窗里，应用主题变来变去不该影响它的观感。
		// 三层颜色都先过一遍壁纸对比度自适应：竖排和横排一样浮在壁纸上，
		// 黑壁纸配深色未唱层就是「混一起看不清」；壁纸亮度取不到时
		// EnsureReadable 原样返回，跟以前完全一致。
		double bgLum = LyricBgSense.DesktopLuminance();
		Color litDefault = LyricVisualTheme.DlSung;
		Color litC = LyricBgSense.EnsureReadable(
			string.IsNullOrWhiteSpace(_litHex) ? litDefault : ParseHex(_litHex, litDefault), bgLum);
		// 用户指定的「未唱色」直接照用（对比不足才提亮），不按 0.72 压暗：
		// 他既然明确给了两层的颜色，我们再私自改一层就等于没让他选。
		Color baseDefault = LyricVisualTheme.DlUnsung;
		Color baseC = LyricBgSense.EnsureReadable(
			string.IsNullOrWhiteSpace(_baseHex) ? baseDefault : ParseHex(_baseHex, baseDefault), bgLum);
		// 译文子行：设了翻译色就用它，没设就回固定 65% 白
		// （和 LyricWindow 横排译文同一组色，两处保持一致）。
		Color trDefault = LyricVisualTheme.DlTranslation;
		Color trC = LyricBgSense.EnsureReadable(
			string.IsNullOrWhiteSpace(_trHex) ? trDefault : ParseHex(_trHex, trDefault), bgLum);

		// 穿透着色只染按钮、不染歌词文字（用户要求，2026-11-05）：
		// 换主题/改自定义色时文字画刷始终正常更新，不再有「只更新恢复基线」的分支。

		_brushLit = Frozen(litC);
		_brushBase = Frozen(baseC);
		_brushTranslation = Frozen(trC);

		// 已经建好的行也要跟着换。设置页改色时歌词早就加载好了，
		// 只改字段不刷控件的话界面看起来一点反应都没有。
		SpreadBrushes();
		LogHelper.LogDiag($"[Diag][歌词] ApplyCustomColors subtitle lit={_litHex} base={_baseHex} tr={_trHex}");
	}

	/// <summary>重建当前行的辉光。固定白晕：辉光服务的暗底语境
	/// （桌面歌词浮在桌面上）不会跟着应用主题变，灰色晕留给浅底
	/// 场景没有意义。</summary>
	private void RebuildGlow()
	{
		_glow = new System.Windows.Media.Effects.DropShadowEffect
		{
			Color = Colors.White,
			BlurRadius = 18,
			ShadowDepth = 0,
			Opacity = 0.42
		};
		_glow.Freeze();
		if (_activeIndex >= 0 && _activeIndex < _rows.Count)
		{
			_rows[_activeIndex].Base.Effect = _glow;
		}
	}

	private static Color ParseHex(string hex, Color fallback)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(hex);
		}
		catch
		{
			return fallback;
		}
	}

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
			FontSize = LyricVisualTheme.MaxFontSize,
			FontWeight = weight,
			Foreground = brush,
			TextAlignment = TextAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			TextWrapping = TextWrapping.NoWrap,
			Height = LyricVisualTheme.LineHeightPx
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
		// 自动跟随时，唱过很久的行整行藏掉，但当前句和它上面最近
		// KeepSungLines 句一直留着（用户明确要「唱完前几句」还在）。
		// 这些行还留在布局里，ScrollToActive 是按「行号 × 固定行高」算偏移的，
		// 一旦行被折叠，后面所有行都会往上挪，滚动定位就全错了
		// （跳进度时尤其明显，会停在差几行的位置）。
		// 所以用透明度而不是 Visibility.Collapsed。
		bool hidePast = !_userScrolling;
		bool anim = SettingsManager.Instance.LyricScrollAnim;
		int prevActive = _appliedActiveIndex;
		for (int i = 0; i < count; i++)
		{
			Row r = _rows[i];
			bool isActive = i == _activeIndex;
			bool wasActive = i == prevActive;
			bool hasText = !string.IsNullOrWhiteSpace(r.Text);

			int delta = Math.Abs(i - _activeIndex);
			// 距离衰减：约三行之外基本淡到最低
			double fade = Math.Clamp(1.0 - delta * 0.17, 0.20, 1.0);

			r.Host.Opacity = hasText ? fade : 0.0;
			if (isActive && wasActive)
			{
				// 当前句没变（重入：交还视口/换色这类），什么都不重播，
				// 免得进行中的过渡被打断重来。
			}
			else if (isActive)
			{
			// 当前行：偏上 + 额外放大，和上下行错开（用户要的「唱到这句时
			// 比别的歌词高一行、更大一号」）；字号同步过渡。
			var group = new TransformGroup();
			var scale = new ScaleTransform(ActiveRowScale, ActiveRowScale);
			var shift = new TranslateTransform(0, -ActiveRowLiftPx);
			group.Children.Add(scale);
			group.Children.Add(shift);
			r.Host.RenderTransform = group;
			r.Base.Effect = _glow;
			AnimateRowFontSize(r, LyricVisualTheme.MinFontSize, LyricVisualTheme.MaxFontSize, anim);
			if (anim)
			{
				// 入场：从右下角往左上角放大顶上来（右→左的斜向过渡）。
				// 基值先设终点，动画显式给 From，Stop 回落正好停在落位状态。
				var dur = TimeSpan.FromMilliseconds(320);
				var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
				shift.BeginAnimation(TranslateTransform.XProperty,
					new DoubleAnimation(EnterShiftX, 0.0, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
				shift.BeginAnimation(TranslateTransform.YProperty,
					new DoubleAnimation(EnterShiftY, -ActiveRowLiftPx, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
				foreach (DependencyProperty p in new[]
					{ ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
				{
					scale.BeginAnimation(p,
						new DoubleAnimation(EnterScaleFrom, ActiveRowScale, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
				}
			}
			}
			else if (wasActive)
			{
				// 刚退位的行：往左下角缩小，再回到行位。别把变换一把摘掉——
				// 它正停在放大状态，置 null 会当场弹回原状（用户报的瞬移）。
				// 走完再摘组；期间若这行又被点亮，RenderTransform 已被换新组，
				// 完成回调里按引用比对，不动新组。
				r.Base.Effect = null;
				AnimateRowFontSize(r, LyricVisualTheme.MaxFontSize, LyricVisualTheme.MinFontSize, anim);
				var group = r.Host.RenderTransform as TransformGroup;
				var scale = group?.Children.OfType<ScaleTransform>().FirstOrDefault();
				var shift = group?.Children.OfType<TranslateTransform>().FirstOrDefault();
				if (group != null && scale != null && anim)
				{
					// 基值 = 落位终点（0,0 / 1.0），关键帧先冲向左下再回位。
					scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
					scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
					scale.ScaleX = 1.0;
					scale.ScaleY = 1.0;
					if (shift != null)
					{
						shift.BeginAnimation(TranslateTransform.XProperty, null);
						shift.BeginAnimation(TranslateTransform.YProperty, null);
						shift.X = 0.0;
						shift.Y = 0.0;
					}
					var dur = TimeSpan.FromMilliseconds(560);
					var xAnim = SubKeyframes(0.0, -ExitShiftX, 0.0, dur);
					var yAnim = SubKeyframes(-ActiveRowLiftPx, ExitShiftY, 0.0, dur);
					var sxAnim = SubKeyframes(ActiveRowScale, ExitScaleTo, 1.0, dur);
					var syAnim = SubKeyframes(ActiveRowScale, ExitScaleTo, 1.0, dur);
					xAnim.Completed += (_, _) =>
					{
						if (ReferenceEquals(r.Host.RenderTransform, group))
						{
							r.Host.RenderTransform = null;
						}
					};
					if (shift != null)
					{
						shift.BeginAnimation(TranslateTransform.XProperty, xAnim);
						shift.BeginAnimation(TranslateTransform.YProperty, yAnim);
					}
					scale.BeginAnimation(ScaleTransform.ScaleXProperty, sxAnim);
					scale.BeginAnimation(ScaleTransform.ScaleYProperty, syAnim);
				}
				else
				{
					r.Host.RenderTransform = null;
				}
			}
			else
			{
				r.Host.RenderTransform = null;
				r.Base.Effect = null;
				if (r.Base.FontSize != LyricVisualTheme.MinFontSize)
				{
					r.Base.FontSize = LyricVisualTheme.MinFontSize;
					r.Lit.FontSize = LyricVisualTheme.MinFontSize;
				}
			}
			if (hidePast && !isActive && i < _activeIndex - KeepSungLines)
			{
				// 只藏「已经唱过好几句之前的」，最近 KeepSungLines 句留着
				//（距离越远越淡，由上面的 fade 统一管）。这样当前句上方
				// 永远有刚唱完的几句垫着，歌词走到结尾也不会整屏空白。
				r.Host.Opacity = 0.0;
			}
		}
		_appliedActiveIndex = _activeIndex;
	}

	/// <summary>单行字号平滑过渡（行高固定，字号变化不会带乱列表布局）。
	/// 基值先设终点、动画显式给 From，FillBehavior.Stop 回落正好是终点；
	/// anim 关掉时直接落位。两层（底衬/亮层）必须同步动，擦除边缘才对得齐。</summary>
	private static void AnimateRowFontSize(Row r, double from, double to, bool anim)
	{
		try
		{
			r.Base.BeginAnimation(TextBlock.FontSizeProperty, null);
			r.Lit.BeginAnimation(TextBlock.FontSizeProperty, null);
			r.Base.FontSize = to;
			r.Lit.FontSize = to;
			if (!anim || Math.Abs(from - to) < 0.01)
			{
				return;
			}
			var size = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(280))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
				FillBehavior = FillBehavior.Stop
			};
			r.Base.BeginAnimation(TextBlock.FontSizeProperty, size);
			r.Lit.BeginAnimation(TextBlock.FontSizeProperty, size);
		}
		catch
		{
		}
	}

	/// <summary>三段关键帧：起点 → 50% 处 mid → 终点，段段 EaseOut。
	/// 基值由调用方设成终点，FillBehavior.Stop 收尾正好落回终点。</summary>
	private static DoubleAnimationUsingKeyFrames SubKeyframes(double from, double mid, double to, TimeSpan dur)
	{
		var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
		var anim = new DoubleAnimationUsingKeyFrames
		{
			Duration = new Duration(dur),
			FillBehavior = FillBehavior.Stop
		};
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromPercent(0), ease));
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(mid, KeyTime.FromPercent(0.5), ease));
		anim.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromPercent(1), ease));
		return anim;
	}

	/// <summary>把当前行滚到视口正中偏下一点。行高固定，所以偏移可以直接算。</summary>
	/// <param name="animate">true = 平滑滑过去（换行时），false = 直接落位（首次打开/跳进度时）。</param>
	private void ScrollToActive(bool animate)
	{
		if (_rows.Count == 0)
		{
			return;
		}
		// 前奏期没有当前句（_activeIndex=-1），按第一行定位：
		// 底部垫高加进来之后，什么都不做的话视口会停在原地没人管。
		int idx = _activeIndex < 0 ? 0 : _activeIndex;
		double viewport = ActualHeight > 1 ? ActualHeight : 400;
		// 下沉量按视口收一收：桌面歌词窗默认 110（上方留歌名），窗口被拖矮后
		// 视口只剩 130 上下，110 会把当前句整个推出视口顶边，看起来就是
		// 「歌词窗里见不到歌词」。这里保证当前行（连同它唱到时的上抬量）
		// 完整留在视口内。
		double drop = Math.Clamp(ActiveDropPx, 0, Math.Max(0, viewport / 2 - _rowHeightPx / 2 - ActiveRowLiftPx - 4));
		double target = idx * _rowHeightPx
			+ _rowHeightPx / 2
			- viewport / 2
			+ drop;
		target = Math.Clamp(target, 0, MaxOffset());
		// [Diag] 记「程序想去哪」：Δ 正 = 内容该上移，负 = 该下移。
		// 和 DiagJumpWatch 的「实际去了哪」对照，方向问题一对便知。
		LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 定位 idx={idx} off={_offset:F0}→{target:F0} Δ={(target - _offset):+0;-0} V={viewport:F0} drop={drop:F0} pad={(_bottomPad?.Height ?? -1):F0} animate={animate}");
		// 设置里的「歌词滚动跟随动画」关掉时一律瞬移，不做插值。
		if (!animate || !SettingsManager.Instance.LyricScrollAnim)
		{
			ScrollLerp = null;
			SetOffset(target);
			return;
		}
		// 手动平移，用自插值平滑滑过去：记下起点/终点/起始时间，由同一个
		// 节拍器推进，每帧调一次 SetOffset，效果跟动画一样但不用碰
		// ScrollViewer.VerticalOffset（它是 IsAnimationProhibited，禁动画）。
		ScrollLerp = new ScrollAnim
		{
			From = _offset,
			To = target,
			Start = DateTime.Now,
			// 滚动时长固定：设置页那个滑块改的是颜色渲染速度，不再控制滚动。
			Duration = TimeSpan.FromMilliseconds(AppSettings.LyricScrollMs)
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
			SetOffset(a.To);
			return;
		}
		// EaseOut，跟原来 DoubleAnimation 用的 CubicEase 一致
		double e = 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
		SetOffset(a.From + (a.To - a.From) * e);
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
		// 露面时先把视口对回当前行：离开全屏期间歌还在走（或者用户滚过
		// 之后直接关了页），回来时视口停在哪全凭上次的残留，不校准就会
		// 一露面就是歪的，要等下一次换行才被拽回来。
		if (_userScrolling)
		{
			ResumeToActive();
		}
		else
		{
			ScrollToActive(animate: false);
		}
	}

	/// <summary>立刻交回视口：取消暂停标记，并立刻滚回当前行。</summary>
	private void ResumeToActive()
	{
		_userScrolling = false;
		ResumeHint.Visibility = Visibility.Collapsed;
		// 暂停的 3 秒里歌还在走：_activeIndex 停在让出视口那一刻的旧行。
		// 不对齐就直接滚，会先往旧位置跳一下（用户看到的「向下跳」），
		// 下一拍才被换行逻辑纠回来。交还视口前先把行号对到当前位置。
		try
		{
			if (_bundle != null && _bundle.Count > 0)
			{
				int idx = _bundle.IndexAt((long)MusicService.Position.TotalMilliseconds);
				if (idx >= 0)
				{
					_activeIndex = idx;
				}
			}
		}
		catch
		{
		}
		// 接回自动跟随时，把「藏上一句」重新生效。
		ApplyRowVisuals();
		if (_bundle != null && _bundle.Count > 0)
		{
			// 之前是「等下一行跨行时才 ScrollToActive」，
			// 于是在这一行还没唱完的这几秒里，看着像没恢复跟随。
			// 这里主动叫一次，立刻回到当前行。
			ScrollToActive(animate: true);
		}
		LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 交还视口 idx={_activeIndex} off={_offset:F0}");
	}

	/// <summary>停表。同时丢掉进行中的滚动插值：停表后没人再推进它，
	/// 留着会让下次 Start() 接着上次那个目标滑，看起来像自己动了。</summary>
	public void Stop()
	{
		_timer?.Stop();
		ScrollLerp = null;
		_userScrolling = false;
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
		// 字幕形态跟整屏列表是两套渲染：列表的滚动/擦除循环在字幕下
		// 没有意义（还会把三行字幕的坐标算乱），提前分流，各自走各的。
		if (_useSubtitle)
		{
			TickSubtitle();
			return;
		}
		// 每拍先看视口有没有被别的东西拽走（放最前面：下面有好几处提前返回，
		// 放后面会漏拍）。
		DiagJumpWatch();
		// 用户在手动翻歌词就先别抢视口，停手 3 秒后自动接回来
		//（用户定的窗口期；点行跳转或点提示按钮会立刻接回，不等 3 秒）。
		if (_userScrolling)
		{
			if ((DateTime.Now - _lastUserScroll).TotalSeconds < 3.0)
			{
				return;
			}
			ResumeToActive();
		}

		long posMs;
		try
		{
			posMs = (long)MusicService.Position.TotalMilliseconds;
			// 位置还没有（-1）就不动；位置比整首歌还长说明读到的是
			// 过期/串台的值——拿它去 IndexAt 会直接落到最后一行，
			// 表现就是「歌词沉底、只剩末尾几句」。等下一拍读到真的再对齐。
			if (posMs < 0)
			{
				if (posMs != _lastBadPos)
				{
					_lastBadPos = posMs;
					LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 位置异常 pos={posMs}，本拍不动");
				}
				return;
			}
			long durMs = (long)MusicService.Duration.TotalMilliseconds;
			if (durMs > 0 && posMs > durMs + 2000)
			{
				if (posMs != _lastBadPos)
				{
					_lastBadPos = posMs;
					LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 位置超长 pos={posMs} dur={durMs}，本拍不动");
				}
				return;
			}
		}
		catch
		{
			return;
		}
		_lastBadPos = long.MinValue;

		int idx = b.IndexAt(posMs);
		double progress = idx >= 0 ? b.ProgressOf(idx, posMs) : 0;

		if (idx != _activeIndex)
		{
			bool first = _firstSync;
			int prevIdx = _activeIndex;
			_activeIndex = idx;
			_lastProgress = progress;
			_firstSync = false;
			LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 换行 {prevIdx}→{idx} pos={posMs} first={first}");
			ApplyRowVisuals();
			ScrollToActive(animate: !first);
			// 「冒头」位移已撤：原来松手瞬间给新当前句强加 +26px 再弹回，
			// 和滚动插值叠在一起就是用户报的「顶下去不平滑」。现在纵向
			// 过渡全交给 ScrollToActive 的缓动，行的字号/缩放在
			// ApplyRowVisuals 里平滑过渡，两个方向都不瞬移。
		}
		else if (idx >= 0)
		{
			_lastProgress = progress;
		}
		// 滚动插值和擦除在同一个节拍里推进，不另开表：
		// 两个 45Hz 的表会互相错开半帧，看着像抖。
		StepScrollLerp();

		// 只擦当前行、往前 KeepSungLines 句、往后两行。全表每帧过一遍
		// 上百个控件，等于歌词控件自己在制造卡顿。
		if (_activeIndex < 0 || _activeIndex >= _rows.Count)
		{
			return;
		}
		UpdateWipe(_rows[_activeIndex], progress);
		// 往回：保留的那几句都是唱完的，擦除进度顶满。
		// 往前：还没唱到，擦除归零。窗口大小跟随 KeepSungLines / 2。
		for (int d = 1; d <= KeepSungLines; d++)
		{
			if (_activeIndex - d >= 0)
			{
				UpdateWipe(_rows[_activeIndex - d], 1);
			}
		}
		for (int d = 1; d <= 2; d++)
		{
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
			r.Clip.Rect = new Rect(0, 0, 0, LyricVisualTheme.LineHeightPx);
			return;
		}
		double textWidth = r.Lit.ActualWidth;
		if (textWidth <= 0)
		{
			// 还没量过：这行在虚拟化视口外，等它进视口自然会量到
			return;
		}
		double newWidth = Math.Clamp(progress, 0, 1) * textWidth;
		// 节流：旧写法每 22ms 无条件写一次 Clip.Rect，
		// 而一个字被「擦」出来的过程里，绝大多数 Tick 的宽度变化不足半个像素
		// ——等于每秒 45 次毫无意义的重绘。不到 0.5px 就放过这一拍。
		if (Math.Abs(r.Clip.Rect.Width - newWidth) < 0.5)
		{
			return;
		}
		r.Clip.Rect = new Rect(0, 0, newWidth, LyricVisualTheme.LineHeightPx);
	}

	// ── 字幕形态：驱动 + 动画（桌面歌词窗横排字幕带那套现成实现直接搬进来）──
	// 行为逐行对照 LyricWindow.ApplyHorizontal / AnimatePushUp / ApplyTranslation /
	// FitFontToWidth。仅去掉了「走马灯」（沉浸页列宽 660，长句缩字号即可）。

	private void TickSubtitle()
	{
		LyricBundle? b = _bundle;
		if (b == null || b.Count == 0)
		{
			return;
		}
		long posMs;
		try
		{
			posMs = (long)MusicService.Position.TotalMilliseconds;
			if (posMs < 0)
			{
				return;
			}
			long durMs = (long)MusicService.Duration.TotalMilliseconds;
			if (durMs > 0 && posMs > durMs + 2000)
			{
				return;
			}
		}
		catch
		{
			return;
		}

		int idx = b.IndexAt(posMs);
		double progress = idx >= 0 ? b.ProgressOf(idx, posMs) : 0;
		if (idx != _activeIndex)
		{
			bool first = _firstSync || DisableSubtitleAnim;
			int prev = _activeIndex;
			_activeIndex = idx;
			_lastProgress = progress;
			_firstSync = false;
			ApplySubtitle(idx, first ? -1 : prev);
		}
		else
		{
			_lastProgress = progress;
		}
		// 擦除进度每拍更新，不然只有「整句亮/整句暗」，没有卡拉OK式描字。
		if (_activeIndex >= 0 && _activeIndex < b.Count)
		{
			UpdateSubWipe(progress);
		}
	}

	/// <summary>按当前播放位置立刻画一帧字幕（不播切句过渡）。
	/// 首帧/切歌/切模板这类「没有上一句可过渡」的场合用。</summary>
	private void ApplySubtitleNow()
	{
		try
		{
			if (_bundle == null || _bundle.Count == 0)
			{
				return;
			}
			long pos = (long)MusicService.Position.TotalMilliseconds;
			_activeIndex = _bundle.IndexAt(Math.Max(0, pos));
			ApplySubtitle(_activeIndex, -1);
		}
		catch
		{
		}
	}

	/// <summary>把当前行放到中间，前后行各留一点淡的。对照 LyricWindow.ApplyHorizontal。
	/// <paramref name="prevIdx"/> &lt; 0 表示「没有上一句」：不播切句过渡。</summary>
	private void ApplySubtitle(int idx, int prevIdx)
	{
		if (_bundle == null)
		{
			return;
		}
		bool animate = prevIdx >= 0 && prevIdx != idx;
		bool showTr = SettingsManager.Instance.LyricShowTranslation;
		LyricAnimTemplate anim = SubAnim;

		// 换文本之前先把三行各自在面板里的纵向位置量下来，PushUp 要用。
		bool shift = animate && anim.NeedsRowShift;
		if (shift)
		{
			StopSubBob();
		}
		double[] oldTops = shift ? MeasureSubTops() : Array.Empty<double>();

		if (idx < 0 || idx >= _bundle.Count)
		{
			SubCurBase.Text = "";
			SubCurLit.Text = "";
			SubPrev.Text = "";
			SubNext.Text = "";
			SubPrev2.Text = "";
			SubNext2.Text = "";
			SubPrev3.Text = "";
			SubNext3.Text = "";
			CollapseSubTranslation();
			SettleSubLineSelected();
			StopSubBob();
			ResetSubRowShifts();
			return;
		}
		LyricLine cur = _bundle.Lines[idx];
		SubCurBase.Text = WrapSubLine(cur.Text);
		SubCurLit.Text = WrapSubLine(cur.Text);
		SubPrev.Text = WrapSubLine(idx > 0 ? _bundle.Lines[idx - 1].Text : "");
		SubNext.Text = WrapSubLine(idx + 1 < _bundle.Count ? _bundle.Lines[idx + 1].Text : "");
		// 「额外」的陪衬行：前三句/后三句，越远越淡（第三句是第 3 个）。
		SubPrev2.Text = WrapSubLine(idx > 1 ? _bundle.Lines[idx - 2].Text : "");
		SubNext2.Text = WrapSubLine(idx + 2 < _bundle.Count ? _bundle.Lines[idx + 2].Text : "");
		SubPrev3.Text = WrapSubLine(idx > 2 ? _bundle.Lines[idx - 3].Text : "");
		SubNext3.Text = WrapSubLine(idx + 3 < _bundle.Count ? _bundle.Lines[idx + 3].Text : "");

		// 字号自适在前、擦除矩形取高度在后：矩形高度要跟新字号的实际行高走。
		FitSubFont();
		SubCurLit.Visibility = Visibility.Visible;
		SubCurClip.Rect = new Rect(0, 0, 0, SubCurLit.ActualHeight);
		// 切句前的静止透明度：上面唱的保持已唱色、下面待唱保持未唱色。
		SubPrev3.Opacity = SubPrev3Op;
		SubPrev2.Opacity = SubPrev2Op;
		SubPrev.Opacity = SubPrevOp;
		SubWipeHost.Opacity = 1.0;
		SubNext.Opacity = SubNextOp;
		SubNext2.Opacity = SubNext2Op;
		SubNext3.Opacity = SubNext3Op;

		if (showTr && !string.IsNullOrWhiteSpace(cur.Translation))
		{
			ApplySubTranslation(cur.Translation, animate);
		}
		else
		{
			ApplySubTranslation(null, animate);
		}

		if (shift)
		{
			ResetSubCurSelect();
			StopSubBob();
			AnimateSubPushUp(anim, oldTops);
		}
		else if (animate)
		{
			ResetSubRowShifts();
			AnimateSubLineSelected(anim);
		}
		else
		{
			ResetSubRowShifts();
			SettleSubLineSelected();
		}
	}

	/// <summary>按 0~1 的进度把字幕当前句的亮色层「擦」出来。
	/// 亮色层永远 Visible，靠裁剪矩形做擦除——Visibility 按进度 toggle 会
	/// 有个天坑：Collapsed 的元素不参与布局，ActualHeight/Width 立刻归 0，
	/// 「h>0 才切成 Visible」的判决下 h 永远是 0 → 永远 Collapsed，
	/// 亮色层就再也不会出来（= 正在唱的颜色动态渲染没了）。</summary>
	private void UpdateSubWipe(double progress)
	{
		if (string.IsNullOrWhiteSpace(SubCurLit.Text))
		{
			return;
		}
		SubCurLit.Visibility = Visibility.Visible;
		double h = SubCurLit.ActualHeight;
		double w = SubCurLit.ActualWidth;
		if (h <= 0 || w <= 0)
		{
			return;
		}
		SubCurClip.Rect = new Rect(0, 0, Math.Clamp(progress, 0, 1) * w, h);
	}

	/// <summary>当前句字号自适：按这一句的实际宽度挑一个塞得下的字号。
	/// 沉浸页列宽固定较宽，长句缩字号就够用，不需要桌面窗那套走马灯。
	/// 两层字号必须一致，否则擦除的亮色层和底衬文字对不齐。</summary>
	private void FitSubFont()
	{
		if (string.IsNullOrEmpty(SubCurBase.Text))
		{
			SubCurBase.FontSize = 34;
			SubCurLit.FontSize = 34;
			return;
		}
		double avail = SubWipeHost.ActualWidth;
		if (avail <= 1)
		{
			SubCurBase.FontSize = 30;
			SubCurLit.FontSize = 30;
			return;
		}
		double budget = avail * 0.96;
		var typeface = new Typeface(
			SubCurBase.FontFamily,
			SubCurBase.FontStyle,
			SubCurBase.FontWeight,
			FontStretches.Normal);
		Brush brush = SubCurBase.Foreground;
		double size = 34;
		while (size > 18 && SubMeasure(SubCurBase.Text, typeface, size, brush) > budget)
		{
			size -= 1;
		}
		SubCurBase.FontSize = size;
		SubCurLit.FontSize = size;
	}

	/// <summary>FormattedText 量单行宽度。比 TextBlock.Measure 快（不做换行布局），
	/// 而字幕切句每帧都不走这里、只在换句时跑一次，够用。</summary>
	private static double SubMeasure(string text, Typeface typeface, double size, Brush brush)
	{
		var probe = new FormattedText(
			text,
			CultureInfo.CurrentUICulture,
			FlowDirection.LeftToRight,
			typeface,
			size,
			brush,
			1.0);
		return probe.Width;
	}

	/// <summary>字幕单行最多 40 个字符，超了自动换行。
	/// 换行点优先找 40 以内最近的空格（不劈单词）；嵌在文字中间的
	/// 空格就算在 20~40 之间也拿着当断点，太靠前的空格放弃、按 40 硬切。
	/// 换出来的尾巴继续递归，直到每行都不超过 40。</summary>
	private const int SubWrapChars = 40;

	private static string WrapSubLine(string text)
	{
		if (string.IsNullOrEmpty(text) || text.Length <= SubWrapChars)
		{
			return text;
		}
		int pivot = text.LastIndexOf(' ', SubWrapChars);
		if (pivot >= SubWrapChars / 2)
		{
			return text.Substring(0, pivot) + "\n" + WrapSubLine(text.Substring(pivot + 1).TrimStart());
		}
		return text.Substring(0, SubWrapChars) + "\n" + WrapSubLine(text.Substring(SubWrapChars));
	}

	// ── 字幕：切句动画（Float / PushUp 两套，参数全来自模板）──

	/// <summary>把一个 X 从 from 动画到 to，并稳稳停在 to。
	/// 这条注释是桌面窗里的原话，照着避免同一个坑：FillBehavior.Stop 会回到
	/// BeginAnimation 之前的基值，所以基值必须先设成终点，再由动画显式给 From。</summary>
	private static void SubAnimateXTo(TranslateTransform t, double from, double to,
		Duration dur, IEasingFunction ease, Action? done = null)
	{
		t.BeginAnimation(TranslateTransform.XProperty, null);
		t.X = to;
		var anim = new DoubleAnimation(from, to, dur)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		};
		if (done != null)
		{
			anim.Completed += (s, e) => done();
		}
		t.BeginAnimation(TranslateTransform.XProperty, anim);
	}

	/// <summary>当前句上下摆动的可用余量。SubWipeHost ClipToBounds="True"，
	/// 振幅超过上下留白字会被自己的容器切掉，所以先量再决定摆多大。</summary>
	private double SubBobAmplitude(LyricAnimTemplate anim)
	{
		try
		{
			if (anim.BobAmplitude <= 0)
			{
				return 0;
			}
			double slack = SubWipeHost.ActualHeight - SubCurLit.ActualHeight;
			if (slack <= 0)
			{
				return 0;
			}
			return Math.Min(anim.BobAmplitude, (slack / 2.0) - 1.0);
		}
		catch
		{
			return 0;
		}
	}

	private void StartSubBob(LyricAnimTemplate anim)
	{
		try
		{
			double amp = SubBobAmplitude(anim);
			if (amp <= 0.3)
			{
				StopSubBob();
				return;
			}
			if (_subBobRunning)
			{
				return;
			}
			_subBobRunning = true;
			var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
			// AutoReverse + Forever：一个来回就是一轮，SineEase 逼近正弦。
			_subBob.BeginAnimation(TranslateTransform.YProperty,
				new DoubleAnimation(0, amp, TimeSpan.FromSeconds(Math.Max(0.2, anim.BobPeriodSeconds)))
				{
					EasingFunction = ease,
					AutoReverse = true,
					RepeatBehavior = RepeatBehavior.Forever
				});
		}
		catch
		{
			// 摆动纯属装饰，失败就算了
		}
	}

	private void StopSubBob()
	{
		try
		{
			_subBob.BeginAnimation(TranslateTransform.YProperty, null);
			_subBob.Y = 0;
		}
		catch { }
		_subBobRunning = false;
	}

	/// <summary>切句的「选中」过渡：当前句从略小长到正常并轻轻浮动。</summary>
	private void AnimateSubLineSelected(LyricAnimTemplate anim)
	{
		try
		{
			double from = Math.Clamp(anim.SelectFromScale, 0.5, 1.5);
			if (anim.SelectMs > 0 && Math.Abs(1.0 - from) > 0.001)
			{
				var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
				_subScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				_subScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				_subScale.ScaleX = 1.0;
				_subScale.ScaleY = 1.0;
				var dur = new Duration(TimeSpan.FromMilliseconds(anim.SelectMs));
				foreach (DependencyProperty p in new[]
					{ ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
				{
					_subScale.BeginAnimation(p,
						new DoubleAnimation(from, 1.0, dur)
						{
							EasingFunction = ease,
							FillBehavior = FillBehavior.Stop
						});
				}
				// 颜色不在这动。以前这里调 SubAnimateCurrentLineColor：动画结束后
				// 又 SetResourceReference 回主题刷，把 ApplyCustomColors 刷上的
				// 自定义已唱/未唱色又顶掉了（正在唱的擦出层变成主题色）。
				// 现在当前句两层始终是 ApplyCustomColors 的冻结刷，擦除全靠裁剪矩形。
			}
		}
		catch
		{
		}
		StartSubBob(anim);
	}

	/// <summary>把当前句摆到已选中的静止状态（不播动画）。首帧/切歌/切模板用。</summary>
	private void SettleSubLineSelected()
	{
		ResetSubCurSelect();
		StartSubBob(SubAnim);
	}

	/// <summary>缩放落回静止（不带摆动）。PushUp 那套跑之前要先用它抹平
	/// Float 留下的缩放，两套变换同时写同一个属性会互相打架。</summary>
	private void ResetSubCurSelect()
	{
		try
		{
			_subScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			_subScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			_subScale.ScaleX = 1.0;
			_subScale.ScaleY = 1.0;
		}
		catch { }
	}

	// 字幕形态当前句的回退色：与列表同一组定值（LyricVisualTheme.Dl*），
	// 空 = 固定色，不再回退主题资源——字幕形态同样属于歌词窗的呈现层。

	private Brush SubCurSungBrush()
	{
		return Frozen(ParseHex(SettingsManager.Instance.LyricSungColor ?? "", LyricVisualTheme.DlSung));
	}

	private Brush SubCurUnsungBrush()
	{
		return Frozen(ParseHex(SettingsManager.Instance.LyricFontColor ?? "", LyricVisualTheme.DlUnsung));
	}

	/// <summary>把某个颜色压暗一档，给上一句/下一句用。
	/// 字幕带的背景永远是暗的（桌面歌词/沉浸页语境），固定往暗压，
	/// 不再看 ThemeManager.IsDark 换方向——否则主题一切，字幕层级跟着跳。</summary>
	private static SolidColorBrush SubDimmed(Brush b, double factor = 0.72)
	{
		Color c = b is SolidColorBrush sb ? sb.Color : Colors.White;
		double k = factor;
		return new SolidColorBrush(Color.FromRgb(
			(byte)Math.Clamp(c.R * k, 0, 255),
			(byte)Math.Clamp(c.G * k, 0, 255),
			(byte)Math.Clamp(c.B * k, 0, 255)));
	}

	/// <summary>一行画刷的颜色过渡（唱完那句由亮转暗、新句子由暗转亮）。
	/// 主题给的画刷是冻结共享的，不能改它的 Color，所以用终点色新建一支
	/// SolidColorBrush 当动画对象，基值设成终点，Stop 回落的正好是终点色。</summary>
	private static void SubAnimateRowColor(UIElement el, Brush to, Brush from,
		Duration dur, IEasingFunction ease, TimeSpan? begin = null)
	{
		try
		{
			if (el is not TextBlock tb || to is not SolidColorBrush dest)
			{
				return;
			}
			if (from is not SolidColorBrush src || src.Color == dest.Color)
			{
				return;
			}
			var brush = new SolidColorBrush(dest.Color);
			tb.BeginAnimation(TextBlock.ForegroundProperty, null);
			tb.Foreground = brush;
			var anim = new ColorAnimation(src.Color, dest.Color, dur)
			{
				EasingFunction = ease,
				FillBehavior = FillBehavior.Stop
			};
			if (begin.HasValue)
			{
				anim.BeginTime = begin.Value;
			}
			brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
		}
		catch { }
	}

	/// <summary>字幕形态的颜色过渡：从暗一下子到正常亮。用户自定义了当前行
	/// 颜色就不做这条过渡（终点是正常色，跑完会把自定义色冲掉），留给缩放和摆动。</summary>
	private void SubAnimateCurrentLineColor(Duration dur, IEasingFunction ease)
	{
		SubAnimateBrush(SubCurBase, LyricVisualTheme.DlUnsung, dur, ease);
		SubAnimateBrush(SubCurLit, LyricVisualTheme.DlSung, dur, ease);
	}

	private void SubAnimateBrush(TextBlock tb, Color restoreColor, Duration dur, IEasingFunction ease)
	{
		try
		{
			// 穿透着色只染按钮、不染歌词文字（用户要求），此动画无需让位。
			if (!string.IsNullOrWhiteSpace(SettingsManager.Instance.LyricFontColor))
			{
				return;
			}
			if (tb.Foreground is not SolidColorBrush sb || sb.IsFrozen)
			{
				return;
			}
			var to = sb.Color;
			Color from = Color.FromArgb(
				(byte)(to.A * 0.55), (byte)(to.R * 0.75), (byte)(to.G * 0.75), (byte)(to.B * 0.75));
			if (from == to)
			{
				return;
			}
			tb.BeginAnimation(TextBlock.ForegroundProperty, null);
			tb.Foreground = new SolidColorBrush(to);
			var brush = (SolidColorBrush)tb.Foreground;
			brush.BeginAnimation(SolidColorBrush.ColorProperty,
				new ColorAnimation(from, to, dur)
				{
					EasingFunction = ease,
					FillBehavior = FillBehavior.Stop
				});
			SubRestoreBrushLater(tb, restoreColor, (int)SubAnim.SelectMs + 60);
		}
		catch
		{
		}
	}

	/// <summary>动画结束后把官方定值刷装回 Foreground。
	/// 字幕带同属桌面歌词呈现层，「正常色」是 LyricVisualTheme.Dl* 定值，
	/// 不装 {DynamicResource}——装回去的话主题一切颜色就跟着变了。</summary>
	private void SubRestoreBrushLater(TextBlock el, Color restoreColor, int delayMs)
	{
		try
		{
			var timer = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(delayMs)
			};
			timer.Tick += (s, _) =>
			{
				((DispatcherTimer)s!).Stop();
				try
				{
					// 穿透着色只染按钮、不染歌词文字（用户要求），文字行随时
					// 可以正常装回官方色。
					el.BeginAnimation(TextBlock.ForegroundProperty, null);
					el.Foreground = new SolidColorBrush(restoreColor);
				}
				catch { }
			};
			timer.Start();
		}
		catch { }
	}

	// ── 字幕：PushUp 三行错位（对照 LyricWindow.AnimatePushUp）──

	/// <summary>元素顶边相对 SubtitlePanel 的纵向位置。没布局过（高度 0）返回 NaN。</summary>
	private double SubTopInPanel(FrameworkElement el)
	{
		try
		{
			if (el.ActualHeight <= 0)
			{
				return double.NaN;
			}
			return el.TransformToAncestor(SubtitlePanel).Transform(new Point(0, 0)).Y;
		}
		catch
		{
			return double.NaN;
		}
	}

	/// <summary>量三行各自在面板里的纵向位置（相对整个控件）。
	/// 量之前先抹平位移/缩放：TransformToAncestor 会把 RenderTransform 算进去，
	/// 带着动画残量量的 top = 布局位置 + 残量，每换一句多叠一层。
	/// 顺序和 AnimateSubPushUp 的索引约定一致：
	/// 0=P3, 1=P2, 2=P, 3=C, 4=N, 5=N2, 6=N3。</summary>
	private double[] MeasureSubTops()
	{
		ResetSubRowShifts();
		try
		{
			UpdateLayout();
		}
		catch { }
		return new[]
		{
			SubTopInPanel(SubPrev3),
			SubTopInPanel(SubPrev2),
			SubTopInPanel(SubPrev),
			SubTopInPanel(SubWipeHost),
			SubTopInPanel(SubNext),
			SubTopInPanel(SubNext2),
			SubTopInPanel(SubNext3)
		};
	}

	private static double SubPick(double[] arr, int i)
	{
		return i >= 0 && i < arr.Length ? arr[i] : double.NaN;
	}

	private void ResetSubRowShifts()
	{
		foreach (TranslateTransform? t in new[] { _subPrev3Shift, _subPrev2Shift, _subPrevShift, _subCurShift, _subNextShift, _subNext2Shift, _subNext3Shift })
		{
			if (t == null) continue;
			try
			{
				t.BeginAnimation(TranslateTransform.YProperty, null);
				t.Y = 0;
			}
			catch { }
		}
		ResetSubRowScales();
	}

	private void ResetSubRowScales()
	{
		SubResetOneScale(_subPrevScale);
		SubResetOneScale(_subNextScale);
		SubResetOneScale(_subScale);
	}

	private static void SubResetOneScale(ScaleTransform? s)
	{
		if (s == null) return;
		try
		{
			s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			s.ScaleX = 1.0;
			s.ScaleY = 1.0;
		}
		catch { }
	}

	private static double SubNextRiseStep(double curTop, double nextTop)
	{
		double step = nextTop - curTop;
		return double.IsNaN(step) || step <= 1 ? LyricVisualTheme.FallbackRowStep : step;
	}

	/// <summary>一行从 oldTop 滑到 newTop。位移太短就直接落位（避免 0.4px 的抖动）。
	/// 偏移量的终点永远是 0（相对自己布局位置的偏移），不能把基值设成 newTop。</summary>
	private static void SubSlideRowY(TranslateTransform t, double oldTop, double newTop,
		Duration dur, IEasingFunction ease, TimeSpan? begin = null)
	{
		t.BeginAnimation(TranslateTransform.YProperty, null);
		if (double.IsNaN(oldTop) || double.IsNaN(newTop) || Math.Abs(oldTop - newTop) < 1.0)
		{
			t.Y = 0;
			return;
		}
		t.Y = 0;
		var anim = new DoubleAnimation(oldTop - newTop, 0.0, dur)
		{
			EasingFunction = ease,
			FillBehavior = FillBehavior.Stop
		};
		if (begin.HasValue)
		{
			anim.BeginTime = begin.Value;
		}
		t.BeginAnimation(TranslateTransform.YProperty, anim);
	}

	/// <summary>一行的缩放过渡。to 一律是 1（落位状态），基值也设 1，
	/// FillBehavior.Stop 结束回落的正好是落位值。两轴各挂一份动画。
	/// 不能共用一个 Animation 实例挂两个属性：后挂的会顶掉先挂的时钟。</summary>
	private static void SubAnimateRowScale(ScaleTransform? s, double from, double to,
		Duration dur, IEasingFunction ease, TimeSpan? begin = null)
	{
		if (s == null) return;
		try
		{
			s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			s.ScaleX = to;
			s.ScaleY = to;
			if (double.IsNaN(from) || Math.Abs(to - from) < 0.001)
			{
				return;
			}
			foreach (DependencyProperty p in new[]
				{ ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
			{
				var anim = new DoubleAnimation(from, to, dur)
				{
					EasingFunction = ease,
					FillBehavior = FillBehavior.Stop
				};
				if (begin.HasValue)
				{
					anim.BeginTime = begin.Value;
				}
				s.BeginAnimation(p, anim);
			}
		}
		catch { }
	}

	private static void SubFadeRow(UIElement el, double to, double from, Duration dur,
		IEasingFunction ease, TimeSpan? begin = null)
	{
		try
		{
			el.BeginAnimation(UIElement.OpacityProperty, null);
			el.Opacity = to;
			if (double.IsNaN(from) || Math.Abs(to - from) < 0.005)
			{
				return;
			}
			var anim = new DoubleAnimation(from, to, dur)
			{
				EasingFunction = ease,
				FillBehavior = FillBehavior.Stop
			};
			if (begin.HasValue)
			{
				anim.BeginTime = begin.Value;
			}
			el.BeginAnimation(UIElement.OpacityProperty, anim);
		}
		catch { }
	}

	/// <summary>同 SubFadeRow，但 BeginTime 延迟期间保持隐藏（FadeRow 基值=终点的
	/// 规矩在有延迟时会让字提前露出来，和正在上移的那句叠在一起）。</summary>
	private static void SubFadeRowHidden(UIElement el, double to, Duration dur,
		IEasingFunction ease, TimeSpan begin)
	{
		try
		{
			el.BeginAnimation(UIElement.OpacityProperty, null);
			el.Opacity = 0.0;
			var anim = new DoubleAnimation(0.0, to, dur)
			{
				EasingFunction = ease,
				FillBehavior = FillBehavior.Stop,
				BeginTime = begin
			};
			anim.Completed += (_, _) => { el.Opacity = to; };
			el.BeginAnimation(UIElement.OpacityProperty, anim);
		}
		catch { }
	}

	/// <summary>切句时的「前三句/后三句」整体错位上移（对照桌面窗 AnimatePushUp）：
	/// ① 每句都往上一格走（P3 吃旧 P2、P2 吃旧 P、P 吃旧 C：上移 + 缩小 + 淡到
	///    已唱行的静止透明度），② 下一句升进中间过渡放大，③ N2 吃旧 N3 稍晚半步，
	/// ④ 第三句之后的全新一句从下面晚起步冒出来，⑤ 当前句照旧轻浮。
	/// 已唱/待唱的颜色不在这动（ApplyCustomColors 统一管：上边已唱色、下边未唱色）。</summary>
	private void AnimateSubPushUp(LyricAnimTemplate anim, double[] oldTops)
	{
		if (anim.PushUpMs <= 0)
		{
			ResetSubRowShifts();
			return;
		}
		try
		{
			double[] newTops = MeasureSubTops();
			double prev3Top = SubPick(newTops, 0);
			double prev2Top = SubPick(newTops, 1);
			double prevTop = SubPick(newTops, 2);
			double curTop = SubPick(newTops, 3);
			double nextTop = SubPick(newTops, 4);
			double next2Top = SubPick(newTops, 5);
			double next3Top = SubPick(newTops, 6);
			double oldPrev2 = SubPick(oldTops, 1);
			double oldPrev = SubPick(oldTops, 2);
			double oldCur = SubPick(oldTops, 3);
			double oldNext = SubPick(oldTops, 4);
			double oldNext2 = SubPick(oldTops, 5);
			double oldNext3 = SubPick(oldTops, 6);

			var move = new Duration(TimeSpan.FromMilliseconds(anim.PushUpMs));
			var moveEase = new CubicEase { EasingMode = EasingMode.EaseOut };
			var sizeEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

			// ① 前三行逐格上移：P3 吃旧 P2、P2 吃旧 P、P 吃旧 C（缩小 + 淡到位）。
			SubSlideRowY(_subPrev3Shift, oldPrev2, prev3Top, move, moveEase);
			SubFadeRow(SubPrev3, SubPrev3Op, SubPrev2Op, move, moveEase);
			SubSlideRowY(_subPrev2Shift, oldPrev, prev2Top, move, moveEase);
			SubFadeRow(SubPrev2, SubPrev2Op, SubPrevOp, move, moveEase);
			SubSlideRowY(_subPrevShift, oldCur, prevTop, move, moveEase);
			SubAnimateRowScale(_subPrevScale, anim.PrevShrinkFrom, 1.0, move, sizeEase);
			SubFadeRow(SubPrev, SubPrevOp, LyricVisualTheme.PrevArriveOpacity, move, moveEase);

			// ② 下一句升进中间，落位时过渡放大。
			var select = anim.SelectMs > 0
				? new Duration(TimeSpan.FromMilliseconds(anim.SelectMs))
				: move;
			double growFrom = Math.Clamp(anim.SelectFromScale, 0.5, 1.5);
			SubSlideRowY(_subCurShift, oldNext, curTop, move, moveEase);
			SubAnimateRowScale(_subScale, growFrom, 1.0, select, sizeEase);
			SubFadeRow(SubWipeHost, 1.0, LyricVisualTheme.CurArriveOpacity, select, moveEase);
			// 亮色层颜色永远吃 ApplyCustomColors 的冻结已唱刷，这里不碰。

			// ③ N 吃旧 N2、N2 吃旧 N3（稍晚半步跟上，像浪一样往下传）。
			SubSlideRowY(_subNextShift, oldNext2, nextTop, move, moveEase);
			SubFadeRow(SubNext, SubNextOp, SubNext2Op, move, moveEase);
			var n2Delay = new TimeSpan?(TimeSpan.FromMilliseconds(Math.Min(move.TimeSpan.TotalMilliseconds * 0.25, 100)));
			SubSlideRowY(_subNext2Shift, oldNext3, next2Top, move, moveEase, n2Delay);
			SubFadeRow(SubNext2, SubNext2Op, SubNext3Op, move, moveEase, n2Delay);

			// ④ 第三句之后的全新一句从下面冒出来。晚起步、只按 N2→N3 间距起步，
			// 否则会横扫过整个面板、和正在上移的①叠在同一段区间里。
			double step = SubNextRiseStep(next2Top, next3Top);
			var riseDelay = TimeSpan.FromMilliseconds(Math.Min(move.TimeSpan.TotalMilliseconds * 0.35, 150));
			var rise = new Duration(TimeSpan.FromMilliseconds(
				Math.Max(150, move.TimeSpan.TotalMilliseconds * 0.8)));
			var appearAt = riseDelay + TimeSpan.FromMilliseconds(rise.TimeSpan.TotalMilliseconds * 0.45);
			var appearDur = new Duration(TimeSpan.FromMilliseconds(
				Math.Max(60, rise.TimeSpan.TotalMilliseconds * 0.55)));
			SubSlideRowY(_subNext3Shift, next3Top + step, next3Top, rise, moveEase, riseDelay);
			SubFadeRowHidden(SubNext3, SubNext3Op, appearDur, moveEase, appearAt);

			// ⑤ 当前句照旧轻浮（浮动挂在自己身上，和位移不打架）。
			StartSubBob(anim);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "程序内歌词上移动画失败");
			ResetSubRowShifts();
		}
	}

	// ── 字幕：译文推挤（对照 LyricWindow.ApplyTranslation 一族）──

	private void ApplySubTranslation(string? text, bool animate)
	{
		string incoming = text ?? "";
		bool want = !string.IsNullOrWhiteSpace(incoming);
		string previous = SubTranslation.Text ?? "";

		if (!want)
		{
			CollapseSubTranslation();
			return;
		}

		bool had = SubTransHost.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(previous);
		SubTransHost.Visibility = Visibility.Visible;
		SubTranslation.Text = WrapSubLine(incoming);
		SubTranslation.Visibility = Visibility.Visible;

		if (!animate || !had || previous == incoming)
		{
			SettleSubTranslation();
			return;
		}

		AnimateSubTranslationPush(previous);
	}

	private void SettleSubTranslation()
	{
		try
		{
			_subTrGen++;
			_subTrIn.BeginAnimation(TranslateTransform.XProperty, null);
			_subTrIn.X = 0;
			_subTrOut.BeginAnimation(TranslateTransform.XProperty, null);
			_subTrOut.X = 0;
			SubTranslationOut.Text = "";
			SubTranslationOut.Opacity = 0;
		}
		catch { }
	}

	private void CollapseSubTranslation()
	{
		try
		{
			_subTrGen++;
			SettleSubTranslation();
			SubTransHost.Visibility = Visibility.Collapsed;
			SubTranslation.Visibility = Visibility.Collapsed;
			SubTranslation.Text = "";
		}
		catch { }
	}

	/// <summary>播「从左往右把上一句翻译顶掉」：旧层 0→+W，新层 -W→0。
	/// 回调里核对 _subTrGen：连着切句时前一次动画的完成事件会晚到，
	/// 不核对会把刚放进去的新译文清掉。</summary>
	private void AnimateSubTranslationPush(string previous)
	{
		try
		{
			double dist = SubTransHost.ActualWidth;
			if (dist <= 1)
			{
				SettleSubTranslation();
				return;
			}
			var dur = new Duration(TimeSpan.FromMilliseconds(Math.Max(0, SubAnim.TransPushMs)));
			if (SubAnim.TransPushMs <= 0)
			{
				SettleSubTranslation();
				return;
			}
			var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

			SubTranslationOut.Text = previous;
			SubTranslationOut.Opacity = 1.0;
			int gen = ++_subTrGen;
			SubAnimateXTo(_subTrOut, 0.0, dist, dur, ease, () =>
			{
				if (gen != _subTrGen) return;
				try
				{
					SubTranslationOut.Text = "";
					SubTranslationOut.Opacity = 0;
					_subTrOut.BeginAnimation(TranslateTransform.XProperty, null);
					_subTrOut.X = 0;
				}
				catch { }
			});
			SubAnimateXTo(_subTrIn, -dist, 0.0, dur, ease);
		}
		catch
		{
			SettleSubTranslation();
		}
	}

	private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		// 视口滚动换成手动平移（见 _offset 处的说明），滚轮也得自己动手：
		// 直接按「行高定标」的几何平移整列，不碰 ScrollViewer 自己的
		// VerticalOffset（它在沉浸式界面里被钳死在 0，动它等于白动）。
		// handled=true 挡住 ScrollViewer 自己的滚动处理，免得它在一旁边
		// 滚边被我们平移，出现双重位移。
		_userScrolling = true;
		_lastUserScroll = DateTime.Now;
		if (_rows.Count == 0)
		{
			e.Handled = true;
			return;
		}
		// 手动翻歌词时把藏掉的行放回来：不然用户往回滚只滚到一片空白，
		// 「看看上一句唱的什么」这件事就没法做了。
		ApplyRowVisuals();
		// 暂停期给个可见提示：3 秒不动自动接回（Tick 里判），
		// 点提示按钮可以立刻回去，点某一行则直接跳过去。
		ResumeHint.Visibility = Visibility.Visible;
		ResumeHintText.Text = Loc.Default["lyric_pause_follow"];
		double v = _offset - e.Delta;
		v = Math.Clamp(v, 0, MaxOffset());
		ScrollLerp = null;
		SetOffset(v);
		LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 滚轮让出视口 off={_offset:F0} Δ={e.Delta} max={MaxOffset():F0}");
		e.Handled = true;
	}

	/// <summary>点某一行：跳到这句开始唱的位置并立刻交还视口。
	/// 这是「跳转到该歌词」的语义——跳完目标行就是当前行，跟随从这里继续，
	/// 不需要再等 3 秒窗口。</summary>
	private void RowHost_Click(Row row, MouseButtonEventArgs e)
	{
		try
		{
			if (_bundle == null || _bundle.Count == 0 || MusicService.CurrentPath == null)
			{
				return;
			}
			if (!MusicService.Seek(row.TimeMs / 1000.0))
			{
				return;
			}
			LogHelper.LogDiag($"[Diag][歌词:{DiagTag}] 点行跳转 t={row.TimeMs}ms off={_offset:F0}");
			e.Handled = true;
			ResumeToActive();
		}
		catch
		{
		}
	}

	/// <summary>点「回到当前句」：交回视口。</summary>
	private void ResumeHint_Click(object sender, RoutedEventArgs e)
	{
		ResumeToActive();
		e.Handled = true;
	}

	/// <summary>重刷这句提示的字。切界面语言后调：
	/// 这行是代码赋的 Text，不会跟着 Loc 的 PropertyChanged 变。</summary>
	public void RefreshLocalizedText()
	{
		try
		{
			ResumeHintText.Text = Loc.Default["lyric_pause_follow"];
		}
		catch { }
	}
}
