using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MusicBox.Pages;
using MusicBox.Services;
using MusicBox.Settings;

// 命名空间不能叫 MusicBox.Windows：在 MusicBox 里面写 Windows.Media.Playback
// （SmtcService 里就有）会被优先解析成 MusicBox.Windows，编译直接报
// 「MusicBox.Windows 中不存在 Media」。所以叫 Views。
namespace MusicBox.Views;

/// <summary>可拖动的桌面歌词窗口。
///
/// 两种布局：
/// - 竖排：整首歌词滚动，当前行居中并逐字擦色。适合盯着看。
/// - 横排：单行字幕带，上一句在左、下一句在右，当行大字居中。
///   适合放在屏幕角上当字幕用，不挡地方。
///
/// 位置/大小/模式都存进设置，下次开还是老地方。切歌由
/// <see cref="LyricFollowService"/> 推过来，这里不自己发请求。</summary>
public partial class LyricWindow : Window
{
	private readonly LyricFollowService _follow;
	private LyricBundle? _bundle;
	private string? _path;
	private int _activeIndex = -1;

	/// <summary>45Hz 的节拍器。竖排靠 <see cref="LyricView"/> 自己跑，
	/// 横排的当前行高亮需要自己定位置，所以合到同一个表里驱动。</summary>
	private readonly DispatcherTimer _timer;

	/// <summary>穿透态下鼠标离开上沿后，把淡入的标题栏收回去。</summary>
	private readonly DispatcherTimer _hideBarTimer = new()
	{
		Interval = TimeSpan.FromMilliseconds(1600)
	};

	private bool _closing;

	/// <summary>当前是不是穿透态。命中测试钩子里每条 WM_NCHITTEST 都要读它，
	/// 所以必须是字段而不是再去问设置（设置可能被别的线程写过）。</summary>
	private bool _passthrough;

	/// <summary>穿透态深色着色是否生效。切句动画、画刷还原定时器都要避让它，
	/// 否则会把深色着色冲掉、穿透态下字又变回白色。详见 ApplyPassthroughTint。</summary>
	private bool _passthroughTint;

	/// <summary>标题栏当前是否亮着。用它做 RevealButtonBar 的短路，
	/// 免得鼠标停在热区里时每一帧都重启一遍淡入动画。</summary>
	private bool _barVisible;

	/// <summary>横竖排模式缓存。OnTick（45Hz）、渲染帧钩子、布局回调都在读它，
	/// 每次都去 SettingsManager 拿对象没必要。竖/横切换键撤掉后设置没有
	/// 运行时写点（构造时初始化一次），所以字段缓存不会和设置脱钩。</summary>
	private bool _vertical;

	/// <summary>穿透态下靠轮询光标位置来发现「鼠标扫进窗口了」。
	///
	/// 为什么不能等鼠标消息：穿透态里除了那几个按钮以外，整片歌词区在
	/// WM_NCHITTEST 里回的是 HTTRANSPARENT，窗口压根收不到 WM_MOUSEMOVE，
	/// OnHoverMove / DragBar.MouseEnter 这类挂窗口或挂元素的事件永远不来。
	/// 之前的表现就是「开了穿透之后键位再也找不着」——不是没做淡入，
	/// 是根本没机会知道鼠标来过。
	///
	/// GetCursorPos 是不经过任何命中测试的全局光标位置，轮询它就能做到
	/// 「鼠标扫过歌词任意位置就点亮键位」，同时那一整片仍然点击穿透给
	/// 下层窗口，两件事互不冲突。</summary>
	private readonly DispatcherTimer _cursorWatchTimer = new()
	{
		Interval = TimeSpan.FromMilliseconds(60)
	};

	private HwndSource? _hwndSource;

	private TranslateTransform? _horizBaseX;
	private TranslateTransform? _horizLitX;

	/// <summary>当前句的「上下摆动」位移。</summary>
	private TranslateTransform? _curBob;

	/// <summary>当前句的「选中」缩放过渡。</summary>
	private ScaleTransform? _curScale;

	/// <summary>三行整体上移（PushUp 动画）时各自的竖向位移。</summary>
	private TranslateTransform? _prevShift;
	private TranslateTransform? _curShift;
	private TranslateTransform? _nextShift;

	/// <summary>上移那一格里每行各自的缩放。
	/// 正在唱的那句升进中间时从 SelectFromScale 放大到 1；
	/// 唱完退到「上一句」那格时从 PrevShrinkFrom 缩回 1（当前句复用 _curScale）。
	/// 用缩放而不是直接改字号：改字号会连着改布局，一帧一次重排；
	/// 而缩放只动 RenderTransform，每行仍然待在自己的格子里，横向永远居中对齐。</summary>
	private ScaleTransform? _prevScale;
	private ScaleTransform? _nextScale;

	/// <summary>当前生效的动画模板。每次切句时重新取（Resolve 有缓存式的短路，
	/// 而且读设置很快）；不缓存成字段是故意的：设置里换模板后
	/// 不重启就生效，不必等下次启动。</summary>
	private LyricAnimTemplate Anim => LyricAnimTemplates.Resolve(
		SettingsManager.Instance.LyricAnimTemplate,
		SettingsManager.Instance.LyricAnimTemplateFolder);

	/// <summary>翻译推入层（新译文，从左往右进来）。</summary>
	private TranslateTransform? _trIn;

	/// <summary>翻译被顶掉层（旧译文，往右被推出去）。</summary>
	private TranslateTransform? _trOut;

	/// <summary>上下摆动是否正在跑。用来避免每帧重复挂同一个循环动画。</summary>
	private bool _bobRunning;

	/// <summary>翻译推入/推出动画的代号。每次发起推挤 +1，
	/// 动画回调先比对自己是不是最新的那一次，不是就直接放弃——
	/// 连续快速切句时旧动画的回调会晚到，不挡一下就会把新译文清掉。</summary>
	private int _trGen;

	/// <summary>当前句是不是处于「单行放不下、走马灯平移」状态。</summary>
	private bool _marquee;

	/// <summary>走马灯状态下这一句在当前字号下的单行实际宽度。</summary>
	private double _marqueeTextWidth;

	/// <summary>走马灯平移量（像素）。匀速漂移的当前位移，
	/// 由经过的真实时间推出来，不依赖播放头位置。</summary>
	private double _panX;

	/// <summary>上一帧的时间戳（秒）。用来算这一帧过了多久。
	/// 用 Stopwatch 而不是 DateTime.Now：系统时间被校准时
	/// 差值会变负，位移就会反向跳一下。</summary>
	private double _panLastSec;

	/// <summary>一句话开始时留下的停留时间（秒）。一进来先停一下，
	/// 用户才看得清这句是什么字，不然一开口就漂走。</summary>
	private double _panHoldLeft;

	/// <summary>当前这句的最大漂移距离 = 单行宽 - 可用宽。
	/// 换句/换字号后要重算，ResetPan 靠它把起点摆对。</summary>
	private double _panMax;


	public LyricWindow()
	{
		InitializeComponent();
		_timer = new DispatcherTimer(DispatcherPriority.Render)
		{
			Interval = TimeSpan.FromMilliseconds(22)
		};
		_timer.Tick += OnTick;
		_hideBarTimer.Tick += HideButtonBarTimer_Tick;
		_cursorWatchTimer.Tick += CursorWatch_Tick;
		_saveBoundsTimer.Tick += (_, _) => { _saveBoundsTimer.Stop(); SaveBounds(); };


		AppSettings s = SettingsManager.Instance;
		_vertical = s.LyricWindowVertical;
		TranslationButton.IsChecked = s.LyricShowTranslation;
		PassthroughButton.IsChecked = s.LyricWindowClickThrough;
		Topmost = s.LyricWindowTopmost;
		VerticalView.ShowTranslation = s.LyricShowTranslation;
		ApplyFontFamily(s.LyricFontFamily);
		ApplyFontColor();
		ApplyMode(_vertical);
		// 底部播放键的图标要先摆对，否则刚显示出来的一瞬间
		// 是一支「播放」三角在放歌，看起来像暂停键按了没用。
		UpdatePlayGlyph();
		MusicService.StateChanged += OnMusicStateChanged;
		// 切界面语言后重刷弹窗里代码赋值的字（状态行、竖排那句提示）。
		Loc.Default.LanguageChanged += OnLanguageChanged;

		ApplySavedBounds(s);
		ApplyPassthrough(s.LyricWindowClickThrough);

		// 窗口拉宽拉窄之后，之前算的字号不再合适，按新宽度重算一次
		SizeChanged += (_, _) =>
		{
			FitFontToWidth();
			ScheduleSaveBounds();
		};
		// 拖动会带来一串 SizeChanged，中间的值都是过渡态。
		// LayoutUpdated 在稳定后才收敛，用来补最后一次真正的宽度。
		LayoutUpdated += (_, _) => FitFontToWidthOnce();

		_follow = LyricRuntime.Follow;
		_follow.LyricsChanged += OnLyricsChanged;
		Closed += OnClosed;
		// 拖动的窗口级订阅只挂一次（构造时）。原来这三行被误粘在右键菜单的
		// 「置顶」lambda 里：结果是没切过置顶就拖不动窗口，而且每切一次
		// 叠加一份订阅（_dragging 挡得住重复计算，挡不住订阅堆积）。
		// 抓鼠标抓在窗口上（BeginDrag），移动/抬手/丢捕获就得挂在窗口级。
		MouseMove += DragBar_MouseMove;
		MouseLeftButtonUp += DragBar_MouseLeftButtonUp;
		LostMouseCapture += DragBar_LostMouseCapture;
		// 隐藏时停动画。挂在 IsVisibleChanged 上而不是在每个 Hide() 调用点里写，
		// 是因为隐藏有四条路：X、Esc、底部开关、主窗最小化带过来的。
		// 漏掉任何一条的后果都一样——窗口看不见了，两个 45Hz 计时器还在烧 CPU。
		IsVisibleChanged += (_, e) =>
		{
			if (e.NewValue is bool vis && !vis)
			{
				StopAnimations();
			}
			else if (e.NewValue is bool v2 && v2)
			{
				SyncAnimationSources();
				// 重新显示时把键位复位成「默认收起、悬停显示」：
				// Hide 之前键位可能正亮着（鼠标压在窗口上时收的），
				// 直接 Show 会把一排按钮留在屏幕上，直到鼠标再动一下。
				ApplyHoverReveal();
			}
		};
	}

	/// <summary>从设置里恢复位置。没存过就放在主窗口右边、贴着屏幕边缘。</summary>
	private void ApplySavedBounds(AppSettings s)
	{
		// 老版本默认宽度是 460。改默认只对「装完就没动过」的人生效，
		// 已经在用的人设置里存的就是 460 这个旧默认值，光改属性没用，
		// 他们还是会开着一个切字的小窗。这里认一下这个特征值并放大。
		// 只认「正好等于旧默认值」而不是「小于某阈值」：后者会把
		// 用户自己认真拖窄的窗口也一起撑开。
		if (Math.Abs(s.LyricWindowWidth - 460) < 1)
		{
			s.LyricWindowWidth = 720;
			SettingsManager.Save(s);
		}

		Width = Math.Max(MinWidth, s.LyricWindowWidth);
		Height = Math.Max(MinHeight, s.LyricWindowHeight);

		double left = s.LyricWindowLeft;
		double top = s.LyricWindowTop;
		if (double.IsNaN(left) || double.IsNaN(top))
		{
			// 没存过：默认落在主窗右下方外面一点。
			// 拿工作区算而不是硬编码屏幕尺寸，免得多显示器或者缩放比例不对时跑到看不见的地方。
			Rect wa = SystemParameters.WorkArea;
			Window? owner = Application.Current?.MainWindow;
			if (owner != null && owner.IsLoaded)
			{
				left = owner.Left + owner.ActualWidth - Width - 16;
				top = owner.Top + owner.ActualHeight - Height - 16;
			}
			else
			{
				left = wa.Right - Width - 40;
				top = wa.Bottom - Height - 40;
			}
		}
		// 用户可能把窗口拖到了副屏、或者分辨率变小了。完全照搬存的值
		// 会让窗口开在屏幕外面，怎么都找不到，所以夹回工作区。
		Rect work = SystemParameters.WorkArea;
		if (left < work.Left - Width + 80) left = work.Left;
		if (top < work.Top) top = work.Top;
		if (left > work.Right - 80) left = work.Right - Width;
		if (top > work.Bottom - 40) top = work.Bottom - Height;
		Left = left;
		Top = top;
	}

	/// <summary>换歌词字族。设置页改完是直接调这个，不需要关窗重开。
	///
	/// 设在 Window 上是有意的：竖排的 LyricView 里那些行是代码 new 出来的
	/// TextBlock，本身不设 FontFamily，全靠属性继承拿字族。改在这两个具体
	/// TextBlock 上只会让横排变、竖排不变。</summary>
	internal void ApplyFontFamily(string? family)
	{
		try
		{
			string f = string.IsNullOrWhiteSpace(family) ? LyricFonts.Default : family;
			if (!LyricFonts.IsAvailable(f)) f = LyricFonts.Default;
			FontFamily = new FontFamily(f);
			// 字族换了，之前量出来的单行宽度和字号都不作数
			_marqueeTextWidth = 0;
			_lastFitWidth = -1;
			FitFontToWidth();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗切换字体失败");
		}
	}

	/// <summary>用户在设置里给歌词挑的颜色。空 = 跟随主题。
	///
	/// 两个色各管一层当前句：
	/// - <see cref="HorizCurLit"/> 是「已经唱过的那一段」，由擦除进度露出，
	///   对应设置里的「已唱歌词颜色」；
	/// - <see cref="HorizCurBase"/> 是「还没唱到的那一段」，铺满整句当底衬，
	///   对应设置里的「歌词颜色」（未唱/底衬）和「已唱歌词颜色」。
	///
	/// 两个色都空 = 跟随主题；只给一个 = 另一层还是主题色。
	/// 挑了就原样用，不做压暗——压暗是为了「只有一个颜色时让两层看得出区别」，
	/// 现在两层各有各的设置，再自作主张改一层会显得没生效。
	///
	/// 第三个色是译文行：<see cref="HorizTranslation"/> 和被顶掉的
	/// <see cref="HorizTranslationOut"/> 共用同一支笔，空 = 主题次级灰；
	/// 两层同色是必须的，推挤动画期间旧译文跟新译文不同色会闪一下。
	///
	/// 顺带把竖排也推一遍：竖排（默认模式）以前完全不看这两个设置，
	/// 于是「换了颜色竖排还是老样子」。见 <see cref="LyricView.ApplyColors"/>。</summary>
	internal void ApplyFontColor()
	{
		try
		{
			string? litHex = SettingsManager.Instance.LyricSungColor;
			string? baseHex = SettingsManager.Instance.LyricFontColor;

			// 壁纸亮度决定「挑的颜色在当前桌面上读不读得清」：
			// 浅色壁纸正常、黑壁纸糊成一团，就是没管对比度。这里把三层颜色
			// 都按壁纸亮度往可读方向推（色相不动，只提亮/压暗）；
			// 壁纸亮度取不到（纯色背景等）时 EnsureReadable 原样返回，行为不变。
			double bgLum = LyricBgSense.DesktopLuminance();

			if (string.IsNullOrWhiteSpace(litHex))
			{
				// 空 = 固定色（不随主题）：桌面歌词浮在桌面上，不是应用窗口的一部分。
				HorizCurLit.Foreground = FixedBrush(LyricBgSense.EnsureReadable(LyricVisualTheme.DlSung, bgLum));
			}
			else
			{
				HorizCurLit.Foreground = FixedBrush(LyricBgSense.EnsureReadable(ColorFromHex(litHex, Colors.White), bgLum));
			}

			if (string.IsNullOrWhiteSpace(baseHex))
			{
				HorizCurBase.Foreground = FixedBrush(LyricBgSense.EnsureReadable(LyricVisualTheme.DlUnsung, bgLum));
			}
			else
			{
				// 用户指定的「未唱色」照用，只在对比不足时按壁纸亮度提亮/压暗。
				// 以前这里是「未唱色 × 0.72」，理由是只给一个颜色时需要压暗一档
				// 好跟已唱色区分开；现在已唱色有了自己的设置项，
				// 再偷偷压暗一层就变成「我挑的颜色不是那个颜色」。
				HorizCurBase.Foreground = FixedBrush(LyricBgSense.EnsureReadable(ColorFromHex(baseHex, Colors.White), bgLum));
			}

			// 翻译行：正在显示的和被顶掉的两层同一支笔。
			string? trHex = SettingsManager.Instance.LyricTranslateColor;
			if (string.IsNullOrWhiteSpace(trHex))
			{
				// 空 = 固定 65% 白，不随主题（和竖排译文同一组值）。
				HorizTranslation.Foreground = FixedBrush(LyricBgSense.EnsureReadable(LyricVisualTheme.DlTranslation, bgLum));
				HorizTranslationOut.Foreground = FixedBrush(LyricBgSense.EnsureReadable(LyricVisualTheme.DlTranslation, bgLum));
			}
			else
			{
				var trColor = LyricBgSense.EnsureReadable(ColorFromHex(trHex, Colors.White), bgLum);
				HorizTranslation.Foreground = FixedBrush(trColor);
				HorizTranslationOut.Foreground = FixedBrush(trColor);
			}

			LogHelper.LogDiag($"[Diag][歌词窗] ApplyFontColor lit={litHex} base={baseHex} tr={trHex}");

			// 竖排读同一组设置。
			VerticalView.ApplyColors(litHex, baseHex, trHex);

			// 颜色变了，之前量过的走马灯轨道宽度不按新画刷算了，得重量一次
			_marqueeTextWidth = 0;
			_lastFitWidth = -1;
			FitFontToWidth();

			// 穿透着色只作用于工具/播放按钮（见 ApplyPassthroughTint），歌词文字
			// 始终保持用户配置色/固定色，这里正常刷完即可，不需要再盖深色。
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗切换颜色失败");
		}
	}

	/// <summary>把 #RRGGBB 变成一支画刷。解析不了就退回 <paramref name="fallback"/>，
	/// 绝不因为一个坏色值就让整句歌词变成透明（那比颜色不对糟得多）。</summary>
	private static System.Windows.Media.SolidColorBrush BrushFromHex(string hex, Color fallback)
	{
		try
		{
			return new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
		}
		catch
		{
			return new System.Windows.Media.SolidColorBrush(fallback);
		}
	}

	/// <summary>把 #RRGGBB 变成颜色值（对比度自适应要先拿 Color 再算亮度）。
	/// 解析不了退回 fallback，语义同 <see cref="BrushFromHex"/>。</summary>
	private static Color ColorFromHex(string hex, Color fallback)
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

	/// <summary>动画模板换了之后，把当前这句按新模板重新落位一次。
	/// 不播过渡（用户已经看到切换瞬间了，再播一遍反而晃），
	/// 主要是把旧模板留下的缩放/位移/摆动收拾干净。</summary>
	internal void ApplyAnimTemplate()
	{
		if (_closing)
		{
			return;
		}
		try
		{
			ResetRowShifts();
			ResetCurSelect();
			StopCurBob();
			ApplyHorizontal(_activeIndex);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词动画模板切换失败");
		}
	}

	/// <summary>位置和大小存回设置。关窗时和尺寸变化时都会调。</summary>
	private void SaveBounds()
	{
		if (_closing)
		{
			return;
		}
		AppSettings s = SettingsManager.Instance;
		// Normal 状态下 Left/Top 才有效；最大化/最小化时存的是 -32000 那样的垃圾值。
		if (WindowState == WindowState.Normal)
		{
			s.LyricWindowLeft = Left;
			s.LyricWindowTop = Top;
			s.LyricWindowWidth = ActualWidth > 0 ? ActualWidth : Width;
			s.LyricWindowHeight = ActualHeight > 0 ? ActualHeight : Height;
			SettingsManager.Save(s);
		}
	}

	private readonly DispatcherTimer _saveBoundsTimer = new()
	{
		Interval = TimeSpan.FromMilliseconds(400)
	};

	/// <summary>拉伸时防抖落盘。SaveBounds 每次都写 settings.json（同步 IO，
	/// 直接挂 SizeChanged 会在拖动时把磁盘写爆，而且拖动过程里大部分尺寸
	/// 都是马上会被下一次覆盖的过渡值。</summary>
	private void ScheduleSaveBounds()
	{
		_saveBoundsTimer.Stop();
		_saveBoundsTimer.Start();
	}


	private void OnLyricsChanged(LyricBundle? bundle)
	{
		Dispatcher.Invoke(() => Apply(bundle));
	}

	/// <summary>把当前正在播的歌的歌词拉出来显示。
	/// 窗口刚建好时订阅还来不及收到事件，所以要主动补一次。</summary>
	public void ApplyCurrentTrack()
	{
		string? path = MusicService.CurrentPath;
		if (string.IsNullOrEmpty(path))
		{
			Apply(null);
			return;
		}
		Apply(null);
		_ = LoadAsync(path);
	}

	private async Task LoadAsync(string path)
	{
		LyricBundle? b = await _follow.GetAsync(path);
		if (_closing)
		{
			return;
		}
		// 取的途中可能已经换歌了，这时候结果作废，
		// 交给 StateChanged 那条路去拉新歌的。
		if (!string.Equals(MusicService.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		Apply(b);
	}

	/// <summary>没歌词时那行状态字（正在加载 / 没找到 / 空闲）。
	/// 单独抽出来是因为切界面语言后要重刷它，
	/// 而它原来的赋值写在 Apply 里 —— 切语言不会重新 Apply，
	/// 于是弹窗上那行提示永远是上一个语言的。</summary>
	private void RefreshStatusText()
	{
		try
		{
			if (_bundle != null)
			{
				return;
			}
			if (_follow.IsLoading)
			{
				StatusText.Text = Loc.Default["lyric_win_loading"];
			}
			else if (!string.IsNullOrEmpty(MusicService.CurrentPath))
			{
				StatusText.Text = Loc.Default["lyric_win_none"];
			}
			else
			{
				StatusText.Text = Loc.Default["lyric_win_idle"];
			}
		}
		catch { }
	}

	/// <summary>界面语言换了：重刷代码里赋过值的字。
	/// 竖排那句「跟随播放」提示也是代码赋值，得一起刷。</summary>
	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		try
		{
			RefreshStatusText();
			VerticalView.RefreshLocalizedText();
		}
		catch { }
	}

	private void Apply(LyricBundle? bundle)
	{
		_bundle = bundle;
		_activeIndex = -1;
		_path = MusicService.CurrentPath;

		if (bundle == null)
		{
			VerticalView.Load(null, null);
			StatusText.Visibility = Visibility.Visible;
			SetTitle(TrackRows.CleanTitleNoise(MusicService.CurrentDisplayTitle) ?? Loc.Default["lyric_win_title"]);
			// 没歌词时两个动画源都得停，不然一个 45Hz 的空转计时器
			// 会在「找不到歌词」的状态下一直跑。
			StopAnimations();
			RefreshStatusText();
			UpdatePanelVisibility();
			return;
		}

		SetTitle(TrackRows.CleanTitleNoise(MusicService.CurrentDisplayTitle) ?? Loc.Default["lyric_win_title"]);
		StatusText.Visibility = Visibility.Collapsed;

		VerticalView.Load(bundle, _path);
		// 横排要立刻画一次，不然切到横排的瞬间是空的，要等下一个 tick。
		if (!_vertical)
		{
			int idx = bundle.IndexAt(LyricClock.PositionMs);
			_activeIndex = idx;
			ApplyHorizontal(idx);
		}
		// 歌词到位后要重新决定谁可见。构造函数里调 ApplyMode 时 _bundle 还是
		// null，那时会把两个面板都收起来（「没歌词」的布局）；而 Apply 以前
		// 只管改 StatusText、从不把它们放回来，于是窗口一片空白，
		// 要用户手动切一次竖/横模式才会出现歌词。
		ApplyVisualState();
	}

	/// <summary>可视状态的成对入口：面板显隐 + 动画源对齐，这两步永远一起走。
	/// 只改面板不换动画源，表现就是「歌词加载完了但它不动」。
	/// 唯一不走这里的调用方是 ApplyMode：横排切换时量位/定位必须夹在
	/// 面板显隐和动画源之间，顺序在那边的注释里写明了。</summary>
	private void ApplyVisualState()
	{
		UpdatePanelVisibility();
		SyncAnimationSources();
	}

	/// <summary>按「有没有歌词 + 当前模式」决定竖排/横排/提示文字谁可见。</summary>
	private void UpdatePanelVisibility()
	{
		bool hasLyric = _bundle != null;
		bool vertical = _vertical;
		VerticalView.Visibility = hasLyric && vertical ? Visibility.Visible : Visibility.Collapsed;
		HorizontalPanel.Visibility = hasLyric && !vertical ? Visibility.Visible : Visibility.Collapsed;
		StatusText.Visibility = hasLyric ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>标题栏不再显示歌名（那一块让给了按钮），这个方法只更新窗口标题。
	/// 留着它是因为「想知道在放哪首」仍然有用，而 Window.Title 走的是托盘气泡，
	/// 不占界面空间。</summary>
	private void SetTitle(string? text)
	{
		Title = text ?? "";
	}

	/// <summary>
	/// 让两个动画源（竖排自己那个 45Hz，窗口那个 45Hz）跟当前模式对齐。
	/// 这两个都挺贵，各自都在 tick UI 线程，同时跑就是白烧一倍 CPU；
	/// 而该跑的那个没跑，表现就是歌词不动/不擦除。集中在一处开关，
	/// 比在每个分支里各写一遍「Start + Stop」可靠得多。
	/// </summary>
	private void SyncAnimationSources()
	{
		bool vertical = _vertical;
		bool hasLyric = _bundle != null;

		if (vertical && hasLyric)
		{
			// 关键：竖排的逐字高亮和自动滚动是 LyricView 内部的计时器在驱动，
			// 窗口的 OnTick 只负责横排。以前这里只 Load 没 Start，
			// 于是竖排看起来「有歌词但不动」——加载完就死了。
			VerticalView.Start();
			if (_timer.IsEnabled)
			{
				_timer.Stop();
			}
		}
		else
		{
			VerticalView.Stop();
			if (hasLyric && !_timer.IsEnabled)
			{
				_timer.Start();
			}
			else if (!hasLyric && _timer.IsEnabled)
			{
				_timer.Stop();
			}
		}

		// 摆动要在这里补一次。窗口隐藏时 StopAnimations() 会把它掐掉，
		// 而重新显示时 OnTick 常常发现行号没变就直接 return，
		// 不会再走一遍 ApplyHorizontal —— 摆动就再也起不来了。
		//
		// 两种模板都起摆动：上移那一套现在是「三行各走各的轨迹」，
		// 摆动只作用在当前句自己身上，不再和整块位移抢同一个变换，
		// 所以 PushUp 也带得上「浮动顶出」里的浮动。
		if (!vertical && hasLyric && _activeIndex >= 0)
		{
			StartCurBob(Anim);
		}
	}

	private void StopAnimations()
	{
		VerticalView.Stop();
		if (_timer.IsEnabled)
		{
			_timer.Stop();
		}
		// 摆动是 RepeatBehavior.Forever 的循环动画，不掐掉就会在
		// 没有歌词（切歌中/暂停后）时一直空转，白烧一帧的变换计算。
		StopCurBob();
	}

	private void OnTick(object? sender, EventArgs e)
	{
		if (_bundle == null)
		{
			return;
		}
		long pos = LyricClock.PositionMs;
		if (pos < 0)
		{
			return;
		}
		int idx = _bundle.IndexAt(pos);
		if (idx != _activeIndex)
		{
			int old = _activeIndex;
			_activeIndex = idx;
			if (!_vertical)
			{
				ApplyHorizontal(idx, old);
			}
		}
		// 擦除每帧都要更新，不然横排只有「整句亮/整句暗」两态，没有卡拉OK效果
		if (!_vertical)
		{
			EnsureRenderHook();
		}
	}

	/// <summary>横排：把当前行放到中间，前后行各留一点淡的。
	///
	/// <paramref name="prevIdx"/> 是切句前的行号，只有从播放推进切过来时才传得进来。
	/// 它决定要不要播切句过渡：首帧、切歌、切排版时没有「上一句」可以滑，
	/// 硬播的话整块内容会从屏幕外飞进来，比不播还怪。</summary>
	private void ApplyHorizontal(int idx, int prevIdx = -1)
	{
		if (_bundle == null)
		{
			return;
		}
		bool animate = prevIdx >= 0 && prevIdx != idx;
		bool showTr = SettingsManager.Instance.LyricShowTranslation;
		LyricAnimTemplate anim = Anim;

		// 换文本之前先把三行各自在面板里的纵向位置量下来。
		// 有了这三个数，后面能算出「旧内容搬到了哪一行」，
		// 也就知道每一行该从哪儿滑到哪儿去。见 AnimatePushUp。
		bool shift = animate && anim.NeedsRowShift;
		// 只在真要播上移动画时才量：量一次要强制走一遍布局，
		// 首帧 / 切歌 / Float 那套都不需要它。
		// 量之前先掐摆动：TransformToAncestor 会把 RenderTransform 算进去，
		// 带着正在摆的那几像素量出来的「旧当前句位置」是偏的，
		// 偏多少就会有多少像素的错位被算进退场的起点里。
		if (shift)
		{
			StopCurBob();
		}
		double[] oldTops = shift ? MeasureRowTops() : Array.Empty<double>();

		if (idx < 0 || idx >= _bundle.Count)
		{
			HorizCurBase.Text = "";
			HorizCurLit.Text = "";
			HorizPrev.Text = "";
			HorizNext.Text = "";
			CollapseTranslation();
			// 空状态要一并把走马灯撤掉。不撤的话上一句的 NoWrap + 左对齐会
			// 留在这儿，等下一句进来时 SetMarquee 因为「状态没变」而跳过换回换行。
			SetMarquee(false);
			// 顺序要紧：SettleLineSelected() 末尾会启摆动，
			// 所以先把缩放摆平、再把摆动掐掉。反过来写就成了「停完又起」。
			SettleLineSelected();
			StopCurBob();
			ResetRowShifts();
			return;
		}
		LyricLine cur = _bundle.Lines[idx];
		// 两层文字内容必须完全一样，擦除才对得上
		HorizCurBase.Text = cur.Text;
		HorizCurLit.Text = cur.Text;
		HorizPrev.Text = idx > 0 ? _bundle.Lines[idx - 1].Text : "";
		HorizNext.Text = idx + 1 < _bundle.Count ? _bundle.Lines[idx + 1].Text : "";
		// 换行句子的高度要跟着变，否则擦除矩形还是上一句的高度，
		// 亮色层会从上半截突然整个消失，看起来像「字突然没了」。
		//
		// 但这一句必须放在 FitFontToWidth 之后：擦除矩形的高度取的是
		// HorizCurLit.ActualHeight，而这个高度是跟着字号走的。
		// 先量后改字号的话，量到的是「上一句的字号」下的高度，
		// 新字更大时矩形就盖不满新字，亮色层被从下半截裁掉；
		// 首帧那次 ActualHeight 是 0，矩形高度 0，亮色层整个不见了——
		// 只剩底层暗色字，看起来就像「动画结束后不显示字」。
		//
		// 字号不写死在 XAML 里，交给 FitFontToWidth 按这一句的实际长度算。
		FitFontToWidth();
		HorizCurClip.Rect = new Rect(0, 0, 0, HorizCurLit.ActualHeight);
		// 换句就把走马灯拨回句首，不管这一句是不是长句：
		// 短句虽然不会漂，但残留的位移会让它在切进来的瞬间从半截开始显示。
		ResetPan();
		// 已唱过的上一句压暗、还没唱的下一句保持稍亮，
		// 于是「已经唱到哪」一眼能看出来，而不是三行灰得一样。
		HorizPrev.Opacity = LyricVisualTheme.PrevPlayed;
		HorizNext.Opacity = LyricVisualTheme.NextPending;

		if (showTr && !string.IsNullOrWhiteSpace(cur.Translation))
		{
			ApplyTranslation(cur.Translation, animate);
		}
		else
		{
			ApplyTranslation(null, animate);
		}

		if (shift)
		{
			// 互斥：PushUp 那套是「三行各走各的轨迹」，Float 那套是「当前句原地动」。
			// 两套同时跑会互相拉扯，所以这里只跑一套，另一套先落位。
			ResetCurSelect();
			StopCurBob();
			AnimatePushUp(anim, oldTops);
		}
		else if (animate)
		{
			ResetRowShifts();
			AnimateLineSelected(anim);
		}
		else
		{
			ResetRowShifts();
			SettleLineSelected();
		}
	}

	/// <summary>把一个 X 从 <paramref name="from"/> 动画到 <paramref name="to"/>，
	/// 并在结束后稳稳停在 <paramref name="to"/>。
	///
	/// 这里有个很容易踩的坑：<see cref="System.Windows.Media.Animation.FillBehavior.Stop"/>
	/// 的语义不是「停在 ToValue」，而是「动画结束后把属性还原成基值」。
	/// WPF 里基值就是你 BeginAnimation 之前赋给属性的那个数。
	///
	/// 所以如果照直觉写成「先赋起点、再只给一个 ToValue 的动画」，
	/// 动画播完属性会弹回起点。翻译层就是这么写的：先 _trIn.X = -dist，
	/// 再动画到 0，播完就弹回 -dist——译文停在屏幕左边外，
	/// 表现是「只有动画在动的时候才看得见中文」。
	/// 缩放（基值 0.94）和颜色画刷（基值是暗色）也都中过同一个招。
	///
	/// 正确写法：先把基值设成终点，再让动画显式给 From/To。
	/// 这样 Stop 回落到的就是终点，既能停对，也抗中途打断
	/// （被打断时新动画会重设基值，不会停在半路）。</summary>
	private static void AnimateXTo(TranslateTransform t, double from, double to,
		Duration dur, System.Windows.Media.Animation.IEasingFunction ease,
		Action? done = null)
	{
		t.BeginAnimation(TranslateTransform.XProperty, null);
		t.X = to;
		var anim = new System.Windows.Media.Animation.DoubleAnimation(from, to, dur)
		{
			EasingFunction = ease,
			FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
		};
		if (done != null) anim.Completed += (s, e) => done();
		t.BeginAnimation(TranslateTransform.XProperty, anim);
	}

	/// <summary>上下摆动的可用余量。
	///
	/// HorizWipeHost 是 ClipToBounds="True" 的：摆动幅度一旦超过它上下
	/// 剩下的空白，字就会被自己的容器切掉。所以不能写死振幅，
	/// 得先量「容器比文字高多少」再决定能摆多大。
	///
	/// 窗口压得很矮、容器跟文字一样高时余量是 0，那就干脆不摆——
	/// 宁可没有这个效果，也不能为了摆动把字切一半。</summary>
	private double BobAmplitude(LyricAnimTemplate anim)
	{
		try
		{
			if (anim.BobAmplitude <= 0)
			{
				return 0;
			}
			double slack = HorizWipeHost.ActualHeight - HorizCurLit.ActualHeight;
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

	private void StartCurBob(LyricAnimTemplate anim)
	{
		if (_closing || _curBob == null)
		{
			return;
		}
		try
		{
			double amp = BobAmplitude(anim);
			if (amp <= 0.3)
			{
				StopCurBob();
				return;
			}
			// 已经在跑就不重挂。同一个 Forever 动画每切一句重挂一次
			// 会把相位重置，摆动看起来一顿一顿的。
			if (_bobRunning)
			{
				return;
			}
			_bobRunning = true;
			var ease = new System.Windows.Media.Animation.SineEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
			};
			// AutoReverse + Forever：一个来回就是一轮。用 SineEase 逼近正弦，
			// 视觉上比线性折返柔和，不会像机械臂一样一顿一顿。
			_curBob.BeginAnimation(TranslateTransform.YProperty,
				new System.Windows.Media.Animation.DoubleAnimation(0, amp,
					TimeSpan.FromSeconds(Math.Max(0.2, anim.BobPeriodSeconds)))
				{
					EasingFunction = ease,
					AutoReverse = true,
					RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
				});
		}
		catch
		{
			// 摆动纯属装饰，失败就算了
		}
	}

	private void StopCurBob()
	{
		if (_curBob == null)
		{
			return;
		}
		try
		{
			_curBob.BeginAnimation(TranslateTransform.YProperty, null);
			_curBob.Y = 0;
		}
		catch { }
		_bobRunning = false;
	}

	/// <summary>切句的「选中」过渡：当前句从略小、略暗，长到正常大小和正常亮度。
	/// 配合上下摆动一起看，就是「这一句被选中了并在轻轻浮动」。</summary>
	private void AnimateLineSelected(LyricAnimTemplate anim)
	{
		if (_closing)
		{
			return;
		}
		try
		{
			double from = Math.Clamp(anim.SelectFromScale, 0.5, 1.5);
			if (anim.SelectMs > 0 && _curScale != null && Math.Abs(1.0 - from) > 0.001)
			{
				var ease = new System.Windows.Media.Animation.CubicEase
				{
					EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
				};
				// 基值设成 1.0（终点），动画从 from 长到 1.0。
				// 反过来写（基值 from + 只给 ToValue）的话，播完会缩回 from 卡住，
				// 看起来就是「只放大了一下又缩回去」，而不是一次过渡。
				_curScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				_curScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				_curScale.ScaleX = 1.0;
				_curScale.ScaleY = 1.0;
				var dur = new Duration(TimeSpan.FromMilliseconds(anim.SelectMs));
				foreach (DependencyProperty p in new[]
					{ ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
				{
					_curScale.BeginAnimation(p,
						new System.Windows.Media.Animation.DoubleAnimation(from, 1.0, dur)
						{
							EasingFunction = ease,
							FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
						});
				}
				AnimateCurrentLineColor(dur, ease);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词选中过渡失败");
		}
		// 摆动必须在这里也起一次：正常推进是 animate == true 这条路，
		// 只在 animate == false（首帧/切歌）里启的话，
		// 一旦 StopAnimations() 把摆动掐掉就再也起不回来了。
		StartCurBob(anim);
	}

	/// <summary>把当前句直接摆到「已选中」的静止状态（不播动画）。
	/// 首帧、切歌、切排版时用——这些场合没有「上一句」可过渡。</summary>
	private void SettleLineSelected()
	{
		ResetCurSelect();
		StartCurBob(Anim);
	}

	/// <summary>缩放和颜色落回静止状态（不带摆动）。
	/// PushUp 那套动画跑的时候要先用它把 Float 留下的缩放抹平，
	/// 两套变换同时写同一个属性会互相打架。</summary>
	private void ResetCurSelect()
	{
		try
		{
			if (_curScale != null)
			{
				_curScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				_curScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				_curScale.ScaleX = 1.0;
				_curScale.ScaleY = 1.0;
			}
		}
		catch { }
	}

	/// <summary>元素顶边相对 <see cref="HorizontalPanel"/> 的纵向位置。
	/// 没布局过（高度 0）时返回 NaN，调用方要能识别。</summary>
	private static double TopInPanel(FrameworkElement el, FrameworkElement ancestor)
	{
		try
		{
			if (el.ActualHeight <= 0)
			{
				return double.NaN;
			}
			return el.TransformToAncestor(ancestor).Transform(new Point(0, 0)).Y;
		}
		catch
		{
			return double.NaN;
		}
	}

	private double TopInPanel(FrameworkElement el) => TopInPanel(el, HorizontalPanel);

/// <summary>三行的竖向位移与缩放全部落位。</summary>
	private void ResetRowShifts()
	{
		foreach (TranslateTransform? t in new[] { _prevShift, _curShift, _nextShift })
		{
			if (t == null)
			{
				continue;
			}
			try
			{
				t.BeginAnimation(TranslateTransform.YProperty, null);
				t.Y = 0;
			}
			catch { }
		}
		ResetRowScales();
	}

	/// <summary>一行的画刷颜色过渡（唱完那句由亮转暗、新句子由暗转亮）。
	///
	/// 为什么要单独做颜色：透明度只是「整体变淡」，前后两句即使透明度一样，
	/// 观感上也分不出「刚唱完」和「马上要唱」。真正的区别在颜色：
	/// 唱过的句子应该是冷的、暗的。所以退场那一下要真的把颜色走过去。
	///
	/// 坑：主题给的画刷是冻结共享的，不能改它的 Color（改一个全局变色）。
	/// 所以这里拿终点颜色新建一支 SolidColorBrush 当动画对象，
	/// 并先把它赋给元素当基值 —— FillBehavior.Stop 结束后回落的正好是终点色。
	private void AnimateRowColor(UIElement el, Brush to, Brush from,
		Duration dur, System.Windows.Media.Animation.IEasingFunction ease,
		TimeSpan? begin = null)
	{
		try
		{
			// 穿透着色只染按钮、不染歌词文字（用户要求），文字动画无需让位。
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
			var anim = new System.Windows.Media.Animation.ColorAnimation(src.Color, dest.Color, dur)
			{
				EasingFunction = ease,
				FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
			};
			if (begin.HasValue)
			{
				anim.BeginTime = begin.Value;
			}
			brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
		}
		catch { }
	}

/// <summary>三行的缩放落回 1（位移之外的另一半状态）。
	/// 当前句那份叫 ResetCurSelect，两边都归 1 才不会两套动画互相拉扯。</summary>
	private void ResetRowScales()
	{
		ResetOneScale(_prevScale);
		ResetOneScale(_nextScale);
		ResetOneScale(_curScale);
	}

	private static void ResetOneScale(ScaleTransform? s)
	{
		if (s == null)
		{
			return;
		}
		try
		{
			s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			s.ScaleX = 1.0;
			s.ScaleY = 1.0;
		}
		catch { }
	}

	/// <summary>量三行各自在面板里的纵向位置（相对 <see cref="HorizontalPanel"/>），
	/// 下标 0/1/2 = 上一句 / 当前句 / 下一句。量不到的那一行给 NaN。
	///
	/// 量之前必须先把位移和缩放抹平：TransformToAncestor 会把 RenderTransform
	/// 一起算进去，带着上一轮动画残量量出来的 top = 布局位置 + 残量，
	/// 拿它当起点就等于每换一句多叠一层，最后整块被推出窗口。</summary>
	private double[] MeasureRowTops()
	{
		ResetRowShifts();
		try
		{
			HorizontalPanel.UpdateLayout();
		}
		catch { }
		return new[]
		{
			TopInPanel(HorizPrev),
			TopInPanel(HorizWipeHost),
			TopInPanel(HorizNext)
		};
	}

	private static double Pick(double[] arr, int i)
	{
		return i >= 0 && i < arr.Length ? arr[i] : double.NaN;
	}

	/// <summary>切句时的三行错位动画：每一行各自走到自己该在的格子里。
	///
	/// 三行的来路和去处都是不一样的：
	/// - 上一句这一格：装的是刚才在唱的那句 → 从中间退到上面，边走边缩小、变淡；
	/// - 当前句这一格：装的是刚才的下一句 → 从下面升进中间，落位时过渡放大；
	/// - 下一句这一格：装的是刚冒出来的那句 → 从下面冒出来，晚一点起步。
	///
	/// 以前这里是「三行用同一个位移量上移」，看上去就是整块歌词一起挪，
	/// 既没有「下一句升进中间」的重点，也没有前一句退场的交代 ——
	/// 用户要的是每行各有各的轨迹，所以这里改成逐行算起点。
	private void AnimatePushUp(LyricAnimTemplate anim, double[] oldTops)
	{
		if (_closing || _prevShift == null || _curShift == null || _nextShift == null)
		{
			ResetRowShifts();
			return;
		}
		if (anim.PushUpMs <= 0)
		{
			ResetRowShifts();
			return;
		}
		try
		{
			// 文本、字号都已经换成新的了，重新量一次得到「各自的新格子」。
			double[] newTops = MeasureRowTops();
			double prevTop = Pick(newTops, 0);
			double curTop = Pick(newTops, 1);
			double nextTop = Pick(newTops, 2);
			double oldPrev = Pick(oldTops, 0);
			double oldCur = Pick(oldTops, 1);
			double oldNext = Pick(oldTops, 2);

			var move = new Duration(TimeSpan.FromMilliseconds(anim.PushUpMs));
			var moveEase = new System.Windows.Media.Animation.CubicEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
			};
			var sizeEase = new System.Windows.Media.Animation.CubicEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
			};

			// ① 唱完的那句退到「上一句」那一格：往上走，同时缩小、淡成已唱过。
			//    颜色是这一步的重点：它刚从中间那格过来，中间那格是「正在唱」
			//    的亮色。不给它一段由亮转暗的过程，它就是「亮着飞到上面、
			//    然后啪一下变灰」，看着像卡了一帧。透明度之外还要走一遍
			//    画刷颜色，「唱完了」这件事才是连续的。
			SlideRowY(_prevShift, oldCur, prevTop, move, moveEase);
			AnimateRowScale(_prevScale, anim.PrevShrinkFrom, 1.0, move, sizeEase);
			FadeRow(HorizPrev, LyricVisualTheme.PrevPlayed, LyricVisualTheme.PrevArriveOpacity, move, moveEase);
			AnimateRowColor(HorizPrev, Dimmed(CurSungBrush()),
				CurSungBrush(), move, moveEase);

			// ② 下一句升进中间：起点是它刚才待的下一格，落到位时过渡放大。
			//    放大用的是 SelectMs / SelectFromScale —— 那两个参数本来就是
			//    「切进当前句的过渡」，只是以前 PushUp 把它俩写成 0，等于关掉了。
			//    亮色层（唱过的那一段）由暗转亮：暗色底衬（DlUnsung 定值白）
			//    本来就是亮的，不用再动，动它只会让「未唱/已唱」的对比变弱。
			var select = anim.SelectMs > 0
				? new Duration(TimeSpan.FromMilliseconds(anim.SelectMs))
				: move;
			double growFrom = Math.Clamp(anim.SelectFromScale, 0.5, 1.5);
			SlideRowY(_curShift, oldNext, curTop, move, moveEase);
			AnimateRowScale(_curScale, growFrom, 1.0, select, sizeEase);
			FadeRow(HorizWipeHost, 1.0, LyricVisualTheme.CurArriveOpacity, select, moveEase);
			AnimateRowColor(HorizCurLit, CurSungBrush(),
				CurUnsungBrush(), select, moveEase);

			// ③ 再下一句从下面冒出来。晚起步、走得快，才有「冒头」而不是「又挪了一行」。
			//
			// 「冒多远」必须按相邻两格（②和③之间）的真实间距算，不能取
			// max(①到②, ②到③)。取 max 的话，弹窗高的时候 ①→② 那一段
			// （上一句高度 + 整块当前句高度）会远大于 ②→③，
			// 于是这一行从「面板下方大半个窗口之外」起步，
			// 一路横扫过整个面板才停到 ③ 的位置 ——
			// 横穿的过程中它和正在往上移的①（上一句）叠在同一段垂直区间里，
			// 两行字叠在一起读不出来，这就是「后一句冒出来和移上去的那句混一起」。
			//
			// 现在只按 ②→③ 的间距起步：行程被限制在一行之内，
			// 物理上就撞不到 ①。再加上透明度「最后 45% 才起来」，
			// 就算布局量歪了、行程偏大，露脸时也已经基本到位。
			double step = NextRiseStep(curTop, nextTop);
			var riseDelay = TimeSpan.FromMilliseconds(Math.Min(move.TimeSpan.TotalMilliseconds * 0.3, 140));
			var rise = new Duration(TimeSpan.FromMilliseconds(
				Math.Max(120, move.TimeSpan.TotalMilliseconds * 0.72)));
			// 淡入和缩放都推到行程后半段：前半程它在别的行附近飞，
			// 露脸就会和它们叠在一起。
			var appearAt = riseDelay + TimeSpan.FromMilliseconds(rise.TimeSpan.TotalMilliseconds * 0.45);
			var appearDur = new Duration(TimeSpan.FromMilliseconds(
				Math.Max(60, rise.TimeSpan.TotalMilliseconds * 0.55)));
			SlideRowY(_nextShift, nextTop + step, nextTop, rise, moveEase, riseDelay);
			AnimateRowScale(_nextScale, LyricVisualTheme.NextRiseScale, 1.0, appearDur, sizeEase, appearAt);
			FadeRowHidden(HorizNext, LyricVisualTheme.NextPending, appearDur, moveEase, appearAt);
			AnimateRowColor(HorizNext, Dimmed(CurUnsungBrush(), 0.82),
				Dimmed(CurUnsungBrush()), appearDur, moveEase, appearAt);

			// ④ 顶出：当前句照旧轻轻上下浮（用户要的「加上浮动顶出」就是这一项）。
			//    现在浮动挂在当前句自己身上，三行又是各走各的，
			//    所以它和上面的位移不再互相打架，PushUp 也能开着。
			StartCurBob(anim);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词上移动画失败");
			ResetRowShifts();
		}
	}

	/// <summary>新冒出来的那句要冒多远：只取「当前句 → 下一句」这一段的间距。
	///
	/// 不能拿相邻两段里较大的那个（见调用处的说明）：那样会让这一行
	/// 从面板下方很远的地方横扫上来，途中和正在上移的上一句叠在一起。
	/// 量不到就退回一个经验值。</summary>
	private static double NextRiseStep(double curTop, double nextTop)
	{
		double step = nextTop - curTop;
		return double.IsNaN(step) || step <= 1 ? LyricVisualTheme.FallbackRowStep : step;
	}

	/// <summary>一行从 oldTop 滑到 newTop。位移太短就直接落位，
	/// 不然一个 0.4px 的「滑一下」会变成一次肉眼可见的抖动。
	/// <paramref name="begin"/> 非空时这一行晚一点起步（用来做错位）。</summary>
	private static void SlideRowY(TranslateTransform t, double oldTop, double newTop,
		Duration dur, System.Windows.Media.Animation.IEasingFunction ease,
		TimeSpan? begin = null)
	{
		t.BeginAnimation(TranslateTransform.YProperty, null);
		if (double.IsNaN(oldTop) || double.IsNaN(newTop) || Math.Abs(oldTop - newTop) < 1.0)
		{
			t.Y = 0;
			return;
		}
		// 注意基值只能是 0，不能写成 newTop。
		//
		// TranslateTransform.Y 是「相对自己布局位置的偏移」，行归位时
		// 正确的偏移就是 0。这里曾经把基值写成 newTop（以为「基值=终点」），
		// 但那条规矩只适用于 Opacity 这类绝对值属性；偏移量这里终点就是 0。
		//
		// 后果是动画跑完的瞬间，每一行都还带着自己那格的高度：当前行被
		// 往下推两百多像素、下一句被推将近一千像素，而整个面板才 170 高，
		// 于是「正在唱的」和「下一句」直接被推出窗口外看不见，只有动画
		// 进行中那一下能看见——因为动画期间 Y 是动画值，落在可见范围内。
		t.Y = 0;
		var anim = new System.Windows.Media.Animation.DoubleAnimation(oldTop - newTop, 0.0, dur)
		{
			EasingFunction = ease,
			FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
		};
		if (begin.HasValue)
		{
			anim.BeginTime = begin.Value;
		}
		t.BeginAnimation(TranslateTransform.YProperty, anim);
	}

	/// <summary>一行的缩放过渡。<paramref name="to"/> 一律是 1（落位状态），
	/// 基值也设成 1，这样 FillBehavior.Stop 结束时回落的正好是落位值。
	/// 两个轴各挂一份动画而不是共用同一个实例：一个 Animation 实例
	/// 同时挂到两个属性上，后挂的那次会顶掉前一次的时钟。</summary>
	private static void AnimateRowScale(ScaleTransform? s, double from, double to,
		Duration dur, System.Windows.Media.Animation.IEasingFunction ease,
		TimeSpan? begin = null)
	{
		if (s == null)
		{
			return;
		}
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
				var anim = new System.Windows.Media.Animation.DoubleAnimation(from, to, dur)
				{
					EasingFunction = ease,
					FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
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

	/// <summary>一行的透明度过渡。和位移/缩放同理：基值设成终点 <paramref name="to"/>。</summary>
	private static void FadeRow(UIElement el, double to, double from, Duration dur,
		System.Windows.Media.Animation.IEasingFunction ease, TimeSpan? begin = null)
	{
		try
		{
			el.BeginAnimation(UIElement.OpacityProperty, null);
			el.Opacity = to;
			if (double.IsNaN(from) || Math.Abs(to - from) < 0.005)
			{
				return;
			}
			var anim = new System.Windows.Media.Animation.DoubleAnimation(from, to, dur)
			{
				EasingFunction = ease,
				FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
			};
			if (begin.HasValue)
			{
				anim.BeginTime = begin.Value;
			}
			el.BeginAnimation(UIElement.OpacityProperty, anim);
		}
		catch { }
	}

	/// <summary>同 <see cref="FadeRow"/>，但延迟（BeginTime）期间保持隐藏。
	///
	/// FadeRow 的规矩是「基值 = 终点」，这在没有延迟时是对的；可冒头动画里
	/// 下一句的淡入被推到 appearAt 才起步，那段延迟里基值 0.62 会直接显示——
	/// 而这正是新当前句从旧下一句槽位升上来的时段，两行字叠在同一格里
	/// 同时露脸，用户看到的就是「重合出现两次」。
	///
	/// 这里反过来：基值先置 0，动画 0→to 带 BeginTime 走，延迟期属性取基值
	/// （0，不可见）；结束时 FillBehavior.Stop 回落的还是 0，所以要在
	/// Completed 里把基值补成 to —— 动画被中途打断时旧时钟被摘掉、
	/// Completed 不会触发，基值留 0，由下一次换句的赋值接管，不会残留。</summary>
	private static void FadeRowHidden(UIElement el, double to, Duration dur,
		System.Windows.Media.Animation.IEasingFunction ease, TimeSpan begin)
	{
		try
		{
			el.BeginAnimation(UIElement.OpacityProperty, null);
			el.Opacity = 0.0;
			var anim = new System.Windows.Media.Animation.DoubleAnimation(0.0, to, dur)
			{
				EasingFunction = ease,
				FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
				BeginTime = begin
			};
			anim.Completed += (_, _) => { el.Opacity = to; };
			el.BeginAnimation(UIElement.OpacityProperty, anim);
		}
		catch { }
	}

	/// <summary>设置翻译文字，并按需播「从左往右把上一句顶掉」的推挤动画。
	///
	/// 三种情况：
	/// 1. 有新译文、且之前正显示着别的译文 → 播推挤：旧层往右退出、新层从左进入。
	/// 2. 有新译文、但之前没显示（首帧 / 之前那行没翻译）→ 直接落位，不播。
	/// 3. 没有新译文（这行没配到翻译 / 用户关了翻译）→ 直接收起。
	///
	/// 推挤距离取 TransHost 的整个宽度：新层从 -W 进、旧层到 +W 出，
	/// 走完一整格，两层文字必然完全在容器外，不会同时露半句。</summary>
	private void ApplyTranslation(string? text, bool animate)
	{
		string incoming = text ?? "";
		bool want = !string.IsNullOrWhiteSpace(incoming);
		string previous = HorizTranslation.Text ?? "";

		// 情况 3
		if (!want)
		{
			CollapseTranslation();
			return;
		}

		bool had = TransHost.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(previous);
		TransHost.Visibility = Visibility.Visible;
		HorizTranslation.Text = incoming;
		HorizTranslation.Visibility = Visibility.Visible;

		// 情况 2：没有可推的旧译文，直接落到中间
		if (!animate || !had || previous == incoming)
		{
			SettleTranslation();
			return;
		}

		// 情况 1：推挤
		AnimateTranslationPush(previous);
	}

	/// <summary>推到中间静止：取消动画、位移归零、清掉被顶掉的层。</summary>
	private void SettleTranslation()
	{
		try
		{
			_trGen++;
			if (_trIn != null)
			{
				_trIn.BeginAnimation(TranslateTransform.XProperty, null);
				_trIn.X = 0;
			}
			if (_trOut != null)
			{
				_trOut.BeginAnimation(TranslateTransform.XProperty, null);
				_trOut.X = 0;
			}
			HorizTranslationOut.Text = "";
			HorizTranslationOut.Opacity = 0;
		}
		catch { }
	}

	private void CollapseTranslation()
	{
		try
		{
			_trGen++;
			SettleTranslation();
			TransHost.Visibility = Visibility.Collapsed;
			HorizTranslation.Visibility = Visibility.Collapsed;
			HorizTranslation.Text = "";
		}
		catch { }
	}

	/// <summary>播「从左往右把上一句翻译顶掉」。
	///
	/// 旧层（<see cref="HorizTranslationOut"/>）停在 0 开始往 +W 走，
	/// 新层（<see cref="HorizTranslation"/>）停在 -W 走到 0。
	/// 两层同时在动，所以看起来是「新句子把旧句子往右顶」，
	/// 而不是「新句子盖住旧句子」。
	///
	/// 回调里清旧层之前先核对 <see cref="_trGen"/>：连着切句时前一次动画的
	/// 完成事件会晚到，不核对就会把刚放进去的新译文内容清掉。</summary>
	private void AnimateTranslationPush(string previous)
	{
		if (_closing || _trIn == null || _trOut == null) { SettleTranslation(); return; }
		try
		{
			double dist = TransHost.ActualWidth;
			if (dist <= 1)
			{
				// 还没量出宽度（刚显示就切句）。没有距离就没法推，
				// 直接落位，别拿 0 距离去跑一个看起来像卡住��动画。
				SettleTranslation();
				return;
			}

			int gen = ++_trGen;
			var dur = new Duration(TimeSpan.FromMilliseconds(Anim.TransPushMs));
			var ease = new System.Windows.Media.Animation.CubicEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
			};

			HorizTranslationOut.Text = previous;
			HorizTranslationOut.Opacity = 1.0;
			// 旧层：0 → +W。播完在回调里清掉内容并归零。
			AnimateXTo(_trOut, 0.0, dist, dur, ease, () =>
			{
				if (_closing || gen != _trGen) return;
				try
				{
					HorizTranslationOut.Text = "";
					HorizTranslationOut.Opacity = 0;
					_trOut.BeginAnimation(TranslateTransform.XProperty, null);
					_trOut.X = 0;
				}
				catch { }
			});
			// 新层：-W → 0，播完就停在 0（留在屏幕中间可见）。
			AnimateXTo(_trIn, -dist, 0.0, dur, ease);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "翻译推挤动画失败");
			SettleTranslation();
		}
	}

	// 当前句两层的回退色（空配置时）：LyricVisualTheme.Dl* 定值，不随主题。
	// 以前是 {DynamicResource TextTertiary/TextPrimary}：主题一切当前句跟着变色，
	// 桌面歌词窗的观感被应用主题牵着走。切句动画结束后也按这两个定值
	// 把画刷装回去（不再装资源引用），穿透态的恢复值同样取自这里，
	// 三处语义全局一致。

	/// <summary>「唱过的那一段」当前应该是什么颜色。
	///
	/// PushUp 的三条颜色过渡以前是写死主题刷的回退，
	/// 也就是说用户自定义了歌词颜色之后，切句动画还是按主题色跑：
	/// 动画中途会把行刷成主题亮/暗色再飞过去，闪一下才落回自定义色。
	/// 这里统一读设置：有自定义色就用自定义色，空了回固定色。</summary>
	private Brush CurSungBrush()
	{
		string? hex = SettingsManager.Instance.LyricSungColor;
		return string.IsNullOrWhiteSpace(hex)
			? FixedBrush(LyricVisualTheme.DlSung)
			: BrushFromHex(hex, LyricVisualTheme.DlSung);
	}

	/// <summary>「还没唱到的那一段」当前应该是什么颜色。见 <see cref="CurSungBrush"/>。</summary>
	private Brush CurUnsungBrush()
	{
		string? hex = SettingsManager.Instance.LyricFontColor;
		return string.IsNullOrWhiteSpace(hex)
			? FixedBrush(LyricVisualTheme.DlUnsung)
			: BrushFromHex(hex, LyricVisualTheme.DlUnsung);
	}

	/// <summary>定值色的画刷。每处现建一支：切句动画要改画刷的 Color，
	/// 冻结的共享刷会被动画撞坏。</summary>
	private static SolidColorBrush FixedBrush(Color c)
	{
		return new SolidColorBrush(c);
	}

	/// <summary>把某个颜色压暗一档，给上一句/下一句用。
	///
	/// 注意压暗的是「派生出来给旁边两行看的那个颜色」，
	/// 不是用户存的那个值：设置里选什么色就是什么色，
	/// 但三行必须再有明暗层级，不然刚唱完的上一句和正在唱的当前句一样亮，
	/// 不知道唱到哪了。桌面歌词永远在暗底语境里浮着，固定往上加亮度（>1）；
	/// 以前按 ThemeManager.IsDark 换方向，主题一切上一句/下一句
	/// 的颜色跟着跳——那正是「桌面歌词随主题」的漏网之鱼。
	///
	/// 亮度有下限：用户挑了深色（或解析出的色很深）时，乘出来的派生色
	/// 仍然接近黑，暗壁纸上一句/下一句就糊成一团看不清——这正是
	/// 「歌词文字跟着变黑」的一条来路。所以派生色的感知亮度一律抬到
	/// 不低于 0.45（向白色混合），保证它永远不会黑掉。</summary>
	private static SolidColorBrush Dimmed(Brush b, double factor = 0.72)
	{
		Color c = b is SolidColorBrush sb ? sb.Color : Colors.White;
		double k = 1 / factor;
		byte r = (byte)Math.Clamp(c.R * k, 0, 255);
		byte g = (byte)Math.Clamp(c.G * k, 0, 255);
		byte bl = (byte)Math.Clamp(c.B * k, 0, 255);
		// 感知亮度（Rec.601）。低于下限就向白色混合，把亮度抬回下限：
		// 混合比例 t 由「目标亮度 = 亮度 + t×(1−亮度)」反解。
		const double minLum = 0.45;
		double lum = (0.299 * r + 0.587 * g + 0.114 * bl) / 255.0;
		if (lum < minLum && lum < 1.0)
		{
			double t = (minLum - lum) / (1.0 - lum);
			r = (byte)Math.Clamp(r + (255 - r) * t, 0, 255);
			g = (byte)Math.Clamp(g + (255 - g) * t, 0, 255);
			bl = (byte)Math.Clamp(bl + (255 - bl) * t, 0, 255);
		}
		return new SolidColorBrush(Color.FromRgb(r, g, bl));
	}

	private void AnimateCurrentLineColor(Duration dur,
		System.Windows.Media.Animation.IEasingFunction ease)
	{
		AnimateBrush(HorizCurBase, LyricVisualTheme.DlUnsung, dur, ease);
		AnimateBrush(HorizCurLit, LyricVisualTheme.DlSung, dur, ease);
	}

	private void AnimateBrush(TextBlock tb, Color restoreColor, Duration dur,
		System.Windows.Media.Animation.IEasingFunction ease)
	{
		try
		{
			// 穿透着色只染按钮、不染歌词文字（用户要求），此动画无需让位。
			// 用户指定了当前行颜色就不做颜色过渡：这条动画的终点是
			// 「正常色」，跑完还会把画刷还原成官方画刷，
			// 正好把刚设进去的自定义色冲掉。留给尺寸过渡和摆动。
			if (!string.IsNullOrWhiteSpace(SettingsManager.Instance.LyricFontColor))
			{
				return;
			}
			if (tb.Foreground is not System.Windows.Media.SolidColorBrush sb
				|| sb.IsFrozen) return;
			// 正常色就是它自己当前的颜色（空配置时是 Dl* 定值刷）。
			var to = sb.Color;
			// 起点：压暗 + 稍微去饱和，模拟「还没唱到」的样子。
			Color from = System.Windows.Media.Color.FromArgb(
				(byte)(to.A * 0.55), (byte)(to.R * 0.75), (byte)(to.G * 0.75), (byte)(to.B * 0.75));
			if (from == to) return;
			tb.BeginAnimation(TextBlock.ForegroundProperty, null);
			// 自建画刷时就把基色设成终点。跟 AnimateXTo 同一个道理：
			// FillBehavior.Stop 结束后回落到基色，
			// 基色是暗色的话动画一结束当前句就又暗回去了。
			tb.Foreground = new System.Windows.Media.SolidColorBrush(to);
			var brush = (System.Windows.Media.SolidColorBrush)tb.Foreground;
			brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
				new System.Windows.Media.Animation.ColorAnimation(from, to, dur)
				{
					EasingFunction = ease,
					FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
				});
			// 过渡结束就还原成官方定值刷（桌面歌词不随主题，也就无需
			// 装回 {DynamicResource}），延时比动画本身长一点，
			// 避免动画还没停就把画刷换掉、闪一下。
			RestoreBrushLater(tb, restoreColor, (int)Anim.SelectMs + 60);
		}
		catch
		{
			// 颜色过渡纯属锦上添花，失败就只留位移
		}
	}

	/// <summary>动画结束后把官方定值刷装回 Foreground。
	/// 延后一点再装：FillBehavior.Stop 的回滚发生在动画最后一帧，
	/// 紧接着就换画刷会和那一帧打架，偶尔闪一下。
	/// 桌面歌词的「官方色」是 LyricVisualTheme.Dl* 定值，不是主题资源：
	/// 装资源引用的话主题一切颜色就跟着变了。</summary>
	private void RestoreBrushLater(TextBlock el, Color restoreColor, int delayMs)
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
					// 该定时器只服务歌词文字行的还原；穿透着色现在只染按钮，
					// 文字行随时可以正常装回官方色。
					el.BeginAnimation(TextBlock.ForegroundProperty, null);
					el.Foreground = FixedBrush(restoreColor);
				}
				catch { }
			};
			timer.Start();
		}
		catch { }
	}

	/// <summary>按当前句的实际宽度选一个能塞下的字号。
	///
	/// 之前字号写死 21，于是长句直接铺出窗口边缘被切掉半截。
	/// 思路是二分一个「能整句放进可用宽度」的最大字号，落在
	/// [LyricVisualTheme.CurFontMin, LyricVisualTheme.CurFontMax]。测宽度用 FormattedText 而不是
	/// TextBlock.Measure：后者还要跑一遍换行布局，一行超长句能拖到
	/// 十几毫秒，而这里是每次切行都调。
	/// </summary>
	/// <summary>字号随弹窗高度缩放的系数。
	///
	/// 原来字号只在 [12, 21] 里按「句长」挑，跟弹窗大小无关：
	/// 弹窗拉到 300px 高，字还是那么小，中间大片空白；缩到 80px 高，
	/// 字反而挤成三行。于是改用高度换算出一个系数，
	/// 基准高度 LyricVisualTheme.BaseWindowHeight 时正好是 1（行为跟以前一样），
	/// 整体再乘进字号上下限。
	///
	/// 夹在 LyricVisualTheme.FontScaleMin~LyricVisualTheme.FontScaleMax：太矮时字会小到读不清，
	/// 太高时三行加翻译会撑破窗口高度开始互顶。</summary>
	private double UpdateScaledFontSizes()
	{
		double h = ActualHeight;
		double scale = h > 1 ? h / LyricVisualTheme.BaseWindowHeight : 1.0;
		if (scale < LyricVisualTheme.FontScaleMin) scale = LyricVisualTheme.FontScaleMin;
		if (scale > LyricVisualTheme.FontScaleMax) scale = LyricVisualTheme.FontScaleMax;
		return scale;
	}

	/// <summary>把缩放系数应用到当前句之外的文字。
	///
	/// 这些字号原本是写死在 XAML 里的 12/13（上一句、下一句、翻译），
	/// 当前句走 FitFontToWidth 会跟着缩放、它们不跟，结果就是
	/// 弹窗一大「当前句很大、上下句还是小字」，层级全乱了。
	/// 这里让它们用同一个系数。</summary>
	private void ApplySecondaryFontSizes(double scale)
	{
		try
		{
			HorizPrev.FontSize = 12 * scale;
			HorizNext.FontSize = 12 * scale;
			HorizTranslation.FontSize = 13 * scale;
			HorizTranslationOut.FontSize = 13 * scale;
			// MaxHeight / MinHeight 也得跟着放大，否则字变大了、
			// 容器还按老高度裁，字会被截掉下半截。
			HorizNext.MaxHeight = 32 * scale;
			TransHost.MinHeight = 18 * scale;
			HorizTranslation.MaxHeight = 20 * scale;
		}
		catch { }
	}

	private void FitFontToWidth()
	{
		// 字号随弹窗大小缩放：先算出系数，再让 FitFontToWidth 和
		// 上一句/下一句/翻译共用它。这样拉大弹窗字跟着变大，
		// 缩小弹窗字跟着变小，而不是永远 12~21 不动。
		double scale = UpdateScaledFontSizes();
		ApplySecondaryFontSizes(scale);

		if (string.IsNullOrEmpty(HorizCurBase.Text))
		{
			return;
		}
		double avail = HorizWipeHost.ActualWidth;
		if (avail <= 1)
		{
			HorizCurBase.FontSize = ((LyricVisualTheme.CurFontMin + LyricVisualTheme.CurFontMax) / 2) * scale;
			HorizCurLit.FontSize = HorizCurBase.FontSize;
			return;
		}

		// 可用宽度的 96%，留一点左右呼吸位。
		double budget = avail * 0.96;
		var typeface = new Typeface(
			HorizCurBase.FontFamily,
			HorizCurBase.FontStyle,
			HorizCurBase.FontWeight,
			FontStretches.Normal);
		Brush brush = HorizCurBase.Foreground;

		double maxSize = LyricVisualTheme.CurFontMax * scale;
		double minSize = LyricVisualTheme.CurFontMin * scale;
		string text = HorizCurBase.Text;

		// 只有一档判断：这一句在当前字号下「宽度够不够」。
		//
		// 以前试过「先按可用高度换行把整句打出来，装不下才走马灯」，
		// 结果长句一换行就出新问题：换行后当前句会占两三行高，
		// 而擦除矩形的高度是按「一行高」写死的，亮色层和底衬层的
		// 行数对不上，擦除边界就横在字的中间，看起来像渲染坏了。
		// 而且换行之后「正在唱的那一段」在第二行，弹窗里那一行的
		// 上下留白全被当前句吃掉，上下句被挤出可视区。
		// 所以按长度判断、宽度不够就横着飘，是这里唯一稳的做法。
		double size = maxSize;
		while (size > minSize && Measure(text, typeface, size, brush) > budget)
		{
			size -= 1;
		}
		double textW = Measure(text, typeface, size, brush);
		bool useMarquee = textW > budget;

		HorizCurBase.FontSize = size;
		// 两层字号必须一致，否则擦除的亮色层和底层文字对不齐
		HorizCurLit.FontSize = size;

		// 走马灯的「轨道长度」= 这一句在当前字号下的单行宽度。
		_panMax = Math.Max(0, Measure(text, typeface, size, brush) - avail);
		_marqueeTextWidth = Measure(text, typeface, size, brush);
		SetMarquee(useMarquee);
	}

	private static double Measure(string text, Typeface typeface, double size, Brush brush)
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

	/// <summary>切换「走马灯 / 常规换行」。
	///
	/// 常规态是居中 + 换行，短句就这么显示。走马灯态改成靠左 + 不换行，
	/// 整句变成一条比窗口还宽的长条，再靠平移把正在唱的部分带进视野。
	/// 两层文字的排版属性必须一起改，否则擦除的两层宽度不一样、对不上。</summary>
	private void SetMarquee(bool on)
	{
		if (_marquee == on)
		{
			return;
		}
		_marquee = on;
		var wrap = on ? TextWrapping.NoWrap : TextWrapping.Wrap;
		var align = on ? HorizontalAlignment.Left : HorizontalAlignment.Center;
		foreach (TextBlock tb in new[] { HorizCurBase, HorizCurLit })
		{
			tb.TextWrapping = wrap;
			tb.HorizontalAlignment = align;
		}
		// 新句从最左边重新开始漂，同时把停留计时重置
		ResetPan();
	}

	/// <summary>把走马灯拨回句首。
	///
	/// 必须在「换句」时调，不能只靠 <see cref="SetMarquee"/>：那个函数
	/// 一进来就 if (_marquee == on) return，连着两句都是长句时
	/// marquee 一直是 true，第二句就会接着第一句停下的位置继续漂，
	/// 开头几个字直接看不见。</summary>
	/// <summary>走马灯漂移方向。+1 = 向右漂，-1 = 向左漂。
	/// 换方向只改这一处，下面所有位移计算都按「起点 → 终点」推，
	/// 不散落着写死正负号。</summary>
	private const double PanDir = 1.0;

	/// <summary>漂移起点偏移（像素）。向右漂时整句先退到窗口左边之外，
	/// 让句首从左侧进来；向左漂时从 0 出发。</summary>
	private static double PanStart(double maxPan) => -PanDir * maxPan;

	/// <summary>漂移终点偏移（像素）。</summary>
	private static double PanEnd(double maxPan) => PanDir > 0 ? 0 : maxPan;

	private void ResetPan()
	{
		_panPhase = PanPhase.HeadHold;
		_panHoldLeft = MarqueeHoldSeconds;
		_panLastSec = NowSeconds();
		double start = _panMax > 0 ? PanStart(_panMax) : 0;
		_panX = start;
		if (_horizBaseX != null) _horizBaseX.X = start;
		if (_horizLitX != null) _horizLitX.X = start;
	}

	// 视觉常量（三行明暗、字号自适应）已集中到 LyricVisualTheme，此处不再各写一份。

	/// <summary>上次算字号时的可用宽度。LayoutUpdated 每次布局都会调，
	/// 用它挡住「宽度没变就别重算」，否则纯白烧 CPU。</summary>
	private double _lastFitWidth = -1;

	/// <summary>只在可用宽度真的变了才重算字号。</summary>
	private void FitFontToWidthOnce()
	{
		if (_vertical)
		{
			return;
		}
		double w = HorizWipeHost.ActualWidth;
		if (w <= 1 || Math.Abs(w - _lastFitWidth) < 0.5)
		{
			return;
		}
		_lastFitWidth = w;
		FitFontToWidth();
	}

	/// <summary>横排当前行的逐字擦除 + 走马灯平移。走渲染帧（见 OnRendering）。</summary>
	private void UpdateHorizontalWipe()
	{
		if (_bundle == null || _activeIndex < 0 || _activeIndex >= _bundle.Count)
		{
			HorizCurClip.Rect = new Rect(0, 0, 0, 0);
			return;
		}
		double w = HorizCurLit.ActualWidth;
		if (w <= 0)
		{
			return;
		}
		double p = _bundle.ProgressOf(_activeIndex, SmoothedPositionMs());

		// 已播 / 未播的分色就是这两层：底下未唱色（DlUnsung 定值白）铺满整句，
		// 上面已唱色（用户配置色或 DlSung）只露出「已经唱过的那一段」。
		HorizCurClip.Rect = new Rect(0, 0, Math.Clamp(p, 0, 1) * w, HorizCurLit.ActualHeight);

	}

	// 播放位置是「按缓冲块」更新的：NAudio 每读完一块（几十到一百多毫秒）
	// 才把 CurrentTime 往前推一截，中间那些帧读到的都是同一个值。
	// 直接拿它画擦除，擦除矩形就是一格一格往前蹦的 ——
	// 这正是「动画不够平滑、有阶段被跳过」的另一半原因
	// （另一半是 ProgressOf 逐字跳，已在 Lyrics.cs 里改成线性）。
	//
	// 这里记住「上次读到的位置 + 读到它时的墙钟」，两次之间用真实经过的
	// 时间补上，最多外推 LeadMs。外推封顶是必须的：音频缓冲真的断了
	// （切设备、拖进度）时不能让它一直往前跑，凭空画出一段没唱的字。
	private long _clockPosMs = -1;
	private double _clockStamp;
	private const double LeadMs = 90.0;

	private long SmoothedPositionMs()
	{
		long pos = LyricClock.PositionMs;
		if (pos < 0)
		{
			return pos;
		}
		double now = NowSeconds();
		if (pos != _clockPosMs)
		{
			// 播放器真的往前了：以它为准，重新起算外推。
			_clockPosMs = pos;
			_clockStamp = now;
			return pos;
		}
		double lead = Math.Clamp(now - _clockStamp, 0.0, LeadMs / 1000.0);
		return pos + (long)(lead * 1000.0);
	}

	/// <summary>换歌、拖进度、切模式之后把外推的起点作废。
	/// 不作废的话，新歌的头几百毫秒会拿上一首的时间戳去外推，
	/// 擦除会先冲出去一截再回来。</summary>
	private void ResetWipeClock()
	{
		_clockPosMs = -1;
		_clockStamp = NowSeconds();
	}

	// 擦除原来挂在 22ms 的节拍器上，两个问题：
	// 一是节拍器和渲染不同步，画面会在两帧之间等一下再跳；
	// 二是 DispatcherTimer 有累积漂移，擦除的推进本身就是不匀的。
	// 换成 CompositionTarget.Rendering 就是「每个实际呈现的帧都更新一次」，
	// 60Hz 屏上每帧都动，观感上才是连续的。
	// 静止状态（没有横排歌词 / 竖排 / 窗口不可见）时自动摘掉，不空烧帧。
	private bool _renderHooked;

	private void EnsureRenderHook()
	{
		if (_renderHooked)
		{
			return;
		}
		_renderHooked = true;
		CompositionTarget.Rendering += OnRendering;
	}

	private void StopRenderHook()
	{
		if (!_renderHooked)
		{
			return;
		}
		_renderHooked = false;
		CompositionTarget.Rendering -= OnRendering;
	}

	private void OnRendering(object? sender, EventArgs e)
	{
		try
		{
			if (_closing || !IsVisible || _bundle == null || _activeIndex < 0)
			{
				StopRenderHook();
				return;
			}
			if (_vertical)
			{
				StopRenderHook();
				return;
			}
			UpdateHorizontalWipe();
		}
		catch
		{
			StopRenderHook();
		}
	}

	/// <summary>走马灯：整句匀速向左漂移。
	///
	/// 早先这里是「跟着播放头走」——把正在唱的位置锚在窗口固定比例处，
	/// 平移量由 ProgressOf 推出来。那种做法有两个毛病：一是句子开头结尾
	/// 那边播放头几乎不动，整句会长时间僵在那儿；二是插值出来的位移
	/// 会随句子长短忽快忽慢，读起来像卡带。
	///
	/// 现在改成经典走马灯：整条长句以固定像素/秒向左平移，从句首开始，
	/// 漂完整句停在句尾，句尾停够一会儿就回到句首循环。这样速度跟句子
	/// 长度、音量、播放进度全都无关，肉眼看到的永远是同一个速度。
	///
	/// 亮/暗两层必须同步平移，否则擦除的接缝会和底下的暗色层错开。
	///
	/// 位移用「上次时间戳到现在过了多少秒 × 速度」算，不累加 _panX：
	/// 累加的话掉一帧、卡一下、切后台一次，误差就永久留在位移里，
	/// 漂移会越走越偏速度。</summary>
	private const double MarqueeSpeedPxPerSec = 34.0;

	/// <summary>句首停留秒数。刚切进来先停一下才漂，不然第一个字
	/// 一闪而过根本读不到。</summary>
	private const double MarqueeHoldSeconds = 0.9;

	/// <summary>句尾停留秒数。漂到句尾之后停这么久再回到句首，
	/// 给「唱完了」一个明确的视觉句号，否则循环会显得很急。</summary>
	private const double MarqueeTailHoldSeconds = 1.4;

	private static readonly System.Diagnostics.Stopwatch _marqueeClock =
		System.Diagnostics.Stopwatch.StartNew();

	private static double NowSeconds() => _marqueeClock.Elapsed.TotalSeconds;

	/// <summary>走马灯的循环阶段。用显式状态机而不是拿「停留剩余时间」
	/// 去反推阶段——那种写法在句尾停留刚结束那一帧分不清「刚到句尾」
	/// 和「已经停完要回句首了」，于是整句会连着抖好几帧。</summary>
	private enum PanPhase { Idle, HeadHold, Drift, TailHold }

	private PanPhase _panPhase;

	private void UpdatePan()
	{
		if (!_marquee || _horizBaseX == null || _horizLitX == null)
		{
			// 非走马灯态也要把时钟戳推进，否则从长句切回短句时，
			// 下一句的第一帧会把中间那段空窗时长全算成漂移距离。
			_panLastSec = NowSeconds();
			_panPhase = PanPhase.Idle;
			return;
		}
		double avail = HorizWipeHost.ActualWidth;
		double lineW = _marqueeTextWidth > 0 ? _marqueeTextWidth : HorizCurLit.ActualWidth;
		if (avail <= 1 || lineW <= avail)
		{
			_panLastSec = NowSeconds();
			_panPhase = PanPhase.Idle;
			_panX = 0;
			_horizBaseX.X = 0;
			_horizLitX.X = 0;
			return;
		}

		double now = NowSeconds();
		// 夹一下：切后台/断点调试回来可能一次过了好几秒，
		// 不夹的话这一帧会直接漂出去半屏，看着像闪了一下。
		double dt = Math.Clamp(now - _panLastSec, 0.0, 0.1);
		_panLastSec = now;
		if (_panPhase == PanPhase.Idle) _panPhase = PanPhase.HeadHold;

		double maxPan = lineW - avail;
		double from = PanStart(maxPan);
		double to = PanEnd(maxPan);
		switch (_panPhase)
		{
			case PanPhase.HeadHold:
				_panHoldLeft -= dt;
				if (_panHoldLeft <= 0) _panPhase = PanPhase.Drift;
				break;

			case PanPhase.Drift:
				// 位移一律朝终点走，不看方向正负：
				// PanDir 决定起点/终点落在哪一边，这里只管逼近。
				_panX += Math.Sign(to - from) * MarqueeSpeedPxPerSec * dt;
				if (Math.Abs(_panX - to) <= MarqueeSpeedPxPerSec * dt)
				{
					_panX = to;
					_panHoldLeft = MarqueeTailHoldSeconds;
					_panPhase = PanPhase.TailHold;
				}
				break;

			case PanPhase.TailHold:
				_panHoldLeft -= dt;
				if (_panHoldLeft <= 0)
				{
					_panX = from;
					_panHoldLeft = MarqueeHoldSeconds;
					_panPhase = PanPhase.HeadHold;
				}
				break;
		}

		_panX = Math.Clamp(_panX, Math.Min(from, to), Math.Max(from, to));
		_horizBaseX.X = _panX;
		_horizLitX.X = _panX;
	}


	private void ApplyMode(bool vertical)
	{
		// 不能整体换成 ApplyVisualState()：横排分支的定位/量位/推帧
		// 必须夹在「面板显隐」（否则量到的是折叠态的 0 高度）和
		// 「动画源对齐」（否则 _timer 抢在 ApplyHorizontal 前开跑）之间。
		UpdatePanelVisibility();
		if (_bundle == null)
		{
			StopAnimations();
			return;
		}
		if (!vertical)
		{
			// 立刻定位到当前该显示的那行，不然切模式的这一帧是空白
			_activeIndex = _bundle.IndexAt(LyricClock.PositionMs);
			ApplyHorizontal(_activeIndex);
			// 手动推一帧：这一帧不能等渲染回调（模式刚切，钩子还没挂上），
			// 不然窗口会先空一下再亮。
			ResetWipeClock();
			UpdateHorizontalWipe();
			EnsureRenderHook();
		}
		// 换模式 = 换动画源。漏了这句，新模式要等到下次切歌才动。
		SyncAnimationSources();
	}

	// 竖/横切换键撤掉后，模式只在设置里改（LyricWindowVertical），窗口加载时读一次。

	private void TranslationButton_Click(object sender, RoutedEventArgs e)
	{
		bool show = TranslationButton.IsChecked == true;
		AppSettings s = SettingsManager.Instance;
		s.LyricShowTranslation = show;
		SettingsManager.Save(s);
		VerticalView.ShowTranslation = show;
		_activeIndex = -1;
		ApplyHorizontal(_activeIndex);
	}

	/// <summary>鼠标穿透开关。
	///
	/// 需求是「背景能点穿过去，只留字幕浮在上面」，但标题栏那排按钮不能一起消失，
	/// 否则穿透态下就再也点不回来了。
	///
	/// 最早试的是 WS_EX_TRANSPARENT：它按窗口粒度整体透传，没法留一块例外区域，
	/// 而且这个扩展样式位一旦置上就必须显式清掉，早先漏了 else 分支，
	/// 结果是「开一次穿透之后整程序生命周期内都点不中」，只能重启进程。
	///
	/// 现在改用 WM_NCHITTEST 自己做区域命中：鼠标落在按钮/把手上就回 HTCLIENT，
	/// 落在别处就回 HTTRANSPARENT 让消息穿透给下面的窗口。这样按钮在穿透态下
	/// 依然可点，而背景照样点得穿。 </summary>
	private void PassthroughButton_Click(object sender, RoutedEventArgs e)
	{
		bool on = PassthroughButton.IsChecked == true;
		SetPassthrough(on);
	}

	internal void SetPassthrough(bool on)
	{
		AppSettings s = SettingsManager.Instance;
		s.LyricWindowClickThrough = on;
		SettingsManager.Save(s);
		PassthroughButton.IsChecked = on;
		ApplyPassthrough(on);
	}

	/// <summary>把「穿透」这个外观+命中测试状态真正落到窗口上。</summary>
internal void ApplyPassthrough(bool on)
	{
		_passthrough = on;
		// 键位统一按「鼠标经过才显示」处理，穿透开关也一样。
		// 以前把穿透键留成常驻（怕开了穿透就再也关不掉），
		// 但穿透态下光标轮询本来就把整条标题栏当成热区，
		// 鼠标移上去它就会出现——不需要为它破例常驻。
		// 键位的具体显隐由 ApplyHoverReveal 统一管，这里只给初值。
		ApplyBarOpacity(0.0, animate: false);
		// 关闭键在穿透态下也显示了。
		//
		// 原来是隐藏的（CloseButton.Visibility = Collapsed），理由是
		// 「误点关闭会让人找不到窗口」。但键位已经重排成
		// 「竖横 → 翻译 → 穿透 → 关闭」，关闭和穿透不再挨在一起，
		// 误关的代价小了很多；而藏起来的代价是：真要关的时候发现
		// 关闭键不见了，还得先关穿透再关窗口。所以恢复显示。
		CloseButton.Visibility = Visibility.Visible;

		Root.BorderThickness = on ? new Thickness(0) : new Thickness(1);
		Root.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(12);
		Root.Background = on ? Brushes.Transparent
			: (Brush)TryFindResource("FlyoutBgBrush") ?? Brushes.Transparent;
		Root.BorderBrush = on ? Brushes.Transparent
			: (Brush)TryFindResource("FlyoutBorderBrush") ?? Brushes.Transparent;
		// 阴影和底色挂在外壳那层（ShadowShell），别挂在内容根上：
		// Effect 会把整棵子树先渲染成位图再模糊，歌词字边会跟着发虚。
		// 之前这里给 Root.Effect 赋值，等于每次退出穿透都把模糊又装回去一遍。
		Root.Effect = null;
		if (ShadowShell != null)
		{
			// 穿透态必须连外壳一起透明：整窗是 AllowsTransparency 的分层窗口，
			// 只要还有一层铺着 FlyoutBgBrush，看起来就还是一块不透明的板。
			ShadowShell.Background = on ? Brushes.Transparent
				: (Brush)TryFindResource("FlyoutBgBrush") ?? Brushes.Transparent;
			ShadowShell.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(12);
			ShadowShell.Effect = on ? null : new System.Windows.Media.Effects.DropShadowEffect
			{
				BlurRadius = 26,
				ShadowDepth = 4,
				Opacity = 0.5,
				Color = System.Windows.Media.Colors.Black
			};
		}

		// 穿透态下鼠标消息会落到下面的窗口，但 WM_MOUSEMOVE 仍然发给我们，
		// 靠它把标题栏「亮一下」，用户才知道这儿能点。不这么做的话穿透态
		// 就是一个纯文字浮层，恢复入口（除了热键）彻底不可见。
		ApplyHoverReveal();

		// WS_EX_NOACTIVATE：穿透态下点按钮不该把这个浮窗激活、抢走当前窗口的
		// 输入焦点（用户正在打字时焦点被抢走是很烦的）。
		// 穿透态的点击区域只有几个按钮，不需要焦点，所以开着是安全的。
		// 必须在句柄建好之后设置，否则 GetWindowLong 拿到的是 0。
		IntPtr h = new WindowInteropHelper(this).Handle;
		if (h != IntPtr.Zero)
		{
			int ex = GetWindowLong(h, GwlExStyle);
			SetWindowLong(h, GwlExStyle,
				on ? (ex | WsExNoActivate) : (ex & ~WsExNoActivate));
		}

		// 前景整体换深色系/换回主题色，与背景、边框、阴影一起过渡。
		// 放在最后：先落背景，前景动画紧随其后，观感是「同一瞬间」换装。
		ApplyPassthroughTint(on);
	}

	// ==================== 穿透态着色 ====================
	//
	// 穿透时窗口没有自己的底（Root.Background = Transparent），歌词直接浮在
	// 用户的桌面/壁纸/网页上 —— 那些多半是亮色的，主题默认的白字会糊成一片。
	// 所以穿透开启时把全部前景（歌词文字、右上角工具键、底部播放键）
	// 切到深色系，关闭时动画回主题/自定义色，两个方向都是 200ms 颜色过渡。
	//
	// 协调点（为什么不能只在这里改颜色）：
	//   · AnimateBrush / AnimateRowColor：切句动画会把主题画刷装到 Foreground 上；
	//   · RestoreBrushLater：动画收尾的定时器会把 {DynamicResource} 装回去；
	//   · ApplyFontColor：主题/设置变化时会重刷官方颜色。
	// 这三处都要让位于着色层（见各自的 _passthroughTint 短路），
	// 否则穿透开着开着字就变回白色了。

	/// <summary>穿透态主文字色：接近纯黑的深灰，亮色壁纸上足够清晰。</summary>
	private static readonly Color TintStrong = System.Windows.Media.Color.FromRgb(0x1C, 0x1C, 0x1C);

	/// <summary>穿透态次级文字色（未唱层/上一句/下一句/译文/状态行）：
	/// 带一点透明度的深色，保住「唱到哪了」的层级感。</summary>
	private static readonly Color TintDim = System.Windows.Media.Color.FromArgb(0xA6, 0x1C, 0x1C, 0x1C);

	private static readonly Duration TintDuration = new Duration(TimeSpan.FromMilliseconds(200));

	/// <summary>穿透着色总开关。on=true 全部前景动画到深色系；
	/// on=false 动画回主题/自定义色，并把官方前景（资源引用/样式/用户色）装回去。
	/// 着色已生效时再传 true 也没关系：逐元素 from==to 的会自己跳过。</summary>
	internal void ApplyPassthroughTint(bool on)
	{
		if (!on && !_passthroughTint)
		{
			// 本来就没着色，不用恢复。
			return;
		}
		_passthroughTint = on;

		// 2026-11-05 按用户要求收窄着色范围：只染右上角工具键/底部播放键/状态行
		// 这些「选项类」UI，歌词文字（已唱/未唱/上下句/译文）一律保持
		// 用户配置色或固定色——之前整窗变黑，歌词在亮壁纸外反而看不清层级。
		// 恢复目标色按 ApplyFontColor 同一套语义取（仅剩的状态行用固定色）。
		TintText(StatusText, on, TintDim, null, LyricVisualTheme.DlTertiary);

		// 右上角工具键 + 底部播放键：控件的 Foreground 平时由样式里的
		// DynamicResource 提供，着色时换成带动画的本地画刷压住样式，
		// 恢复时清掉本地值让样式重新接管。播放键图标写死了 AccentBrush，单独染。
		foreach (System.Windows.Controls.Control b in new System.Windows.Controls.Control[]
		{
			TranslationButton, PassthroughButton, CloseButton,
			LyricPrevButton, LyricPlayButton, LyricNextButton
		})
		{
			TintControl(b, on);
		}
		TintText(LyricPlayGlyph, on, TintStrong, null, LyricVisualTheme.DlSung);

		// 恢复方向：颜色动画跑完后把官方前景（资源引用/样式/用户色）装回。
		if (!on)
		{
			RestoreOfficialColorsLater();
		}

		// 竖排歌词行 + 字幕形态一起换（竖排是默认模式，多数人看到的其实是它）。
		VerticalView.SetPassthroughTint(on);
	}

	/// <summary>单个文字元素的前景色过渡。on=true 动画到深色；
	/// on=false 动画回恢复色（用户自定义色优先，否则固定色），
	/// 官方画刷的装回统一交给 RestoreOfficialColorsLater。
	/// <paramref name="userHex"/>/<paramref name="restColor"/> 只在恢复方向用。</summary>
	private void TintText(TextBlock tb, bool on, Color target, string? userHex, Color restColor)
	{
		try
		{
			Color from = SolidColor(tb.Foreground, target);
			Color to = on ? target : RestColor(userHex, restColor);
			if (from == to)
			{
				return;
			}
			tb.BeginAnimation(TextBlock.ForegroundProperty, null);
			// 基值必须是终点色：ColorAnimation 默认 FillBehavior=Stop，
			// 动画结束后回落到基值 —— 基值是起点色的话过渡等于白做。
			var brush = new System.Windows.Media.SolidColorBrush(to);
			tb.Foreground = brush;
			brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
				new System.Windows.Media.Animation.ColorAnimation(from, to, TintDuration)
				{
					EasingFunction = new System.Windows.Media.Animation.CubicEase
					{
						EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
					}
				});
		}
		catch
		{
			// 着色失败就保持原样，别让歌词窗整体坏掉
		}
	}

	/// <summary>控件（按钮）的前景色过渡，逻辑同 TintText。</summary>
	private void TintControl(System.Windows.Controls.Control b, bool on)
	{
		try
		{
			Color from = SolidColor(b.Foreground, TintStrong);
			Color to = on ? TintStrong : LyricVisualTheme.DlUnsung;
			if (from == to)
			{
				return;
			}
			b.BeginAnimation(System.Windows.Controls.Control.ForegroundProperty, null);
			var brush = new System.Windows.Media.SolidColorBrush(to);
			b.Foreground = brush;
			brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
				new System.Windows.Media.Animation.ColorAnimation(from, to, TintDuration)
				{
					EasingFunction = new System.Windows.Media.Animation.CubicEase
					{
						EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
					}
				});
		}
		catch
		{
		}
	}

	/// <summary>恢复方向的目标色：用户自定义了就用自定义色，空着用固定色（不随主题）。
	/// 与 ApplyFontColor 的语义保持一致，动画终点和稍后装回的官方画刷一致才不会跳色。</summary>
	private static Color RestColor(string? userHex, Color restColor)
	{
		return string.IsNullOrWhiteSpace(userHex)
			? restColor
			: BrushFromHex(userHex, System.Windows.Media.Colors.White).Color;
	}

	/// <summary>从画刷里读颜色，读不出（渐变/空）就给 fallback。</summary>
	private static Color SolidColor(System.Windows.Media.Brush? b, Color fallback)
	{
		return b is System.Windows.Media.SolidColorBrush sb ? sb.Color : fallback;
	}

	private DispatcherTimer? _tintRestoreTimer;

	/// <summary>着色退出后把「官方」前景装回去：资源引用 / 样式默认 / 用户自定义色。
	/// 延到颜色动画跑完再装，避免中途换画刷闪一下；执行时再核对一次
	/// _passthroughTint —— 「刚关又立刻开」时不能把深色刷掉。
	/// 多个元素共享一次装回，只排一个定时器。</summary>
	private void RestoreOfficialColorsLater()
	{
		if (_tintRestoreTimer != null)
		{
			return;
		}
		var t = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(TintDuration.TimeSpan.TotalMilliseconds + 40)
		};
		_tintRestoreTimer = t;
		t.Tick += (s, _) =>
		{
			t.Stop();
			_tintRestoreTimer = null;
			if (_passthroughTint)
			{
				return;
			}
			try
			{
				// 横排的上一句/下一句/状态行：装回固定色（与 XAML 默认一致）。
				// 不装资源引用：桌面歌词不随主题，装回 {DynamicResource} 的话
				// 主题一切这些行又跟着变色了。
				HorizPrev.Foreground = FixedBrush(LyricVisualTheme.DlSecondary);
				HorizNext.Foreground = FixedBrush(LyricVisualTheme.DlSecondary);
				StatusText.Foreground = FixedBrush(LyricVisualTheme.DlTertiary);
				// 按钮本地值清掉，样式里的 DynamicResource 重新接管。
				// （按钮是窗口 chrome，跟随主题是本来行为，不在「歌词不随主题」范围里。）
				foreach (System.Windows.Controls.Control b in new System.Windows.Controls.Control[]
				{
					TranslationButton, PassthroughButton,
					LyricPrevButton, LyricPlayButton, LyricNextButton
				})
				{
					b.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
				}
				// CloseButton 没有样式可回退，本地值必须先清掉再挂资源引用——
				// WPF 里本地值优先级高于资源引用，直接 SetResourceReference 会被
				// 着色时留下的那支本地画刷压住永远不生效，X 就一直停在着色退出
				// 的固定暗色上，和旁边两个键对不上（正是「X 颜色不对」的原因）。
				CloseButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
				CloseButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
				LyricPlayGlyph.Foreground = FixedBrush(LyricVisualTheme.DlSung);
				// 当前句两层 + 译文两层 + 竖排：ApplyFontColor 按「自定义色或固定色」
				// 重新落好，顺带同步竖排和走马灯测量。
				ApplyFontColor();
			}
			catch { }
		};
		t.Start();
	}

	/// <summary>淡入淡出所有键位（标题栏三个 + 底部播放三个）。
	///
	/// 全部键位一起淡：标题栏的键和底部的播放键本来就是一组，
	/// 分开淡会出现「上面出来了下面还没出来」的半截状态。
	///
	/// 为什么不淡 DragBar 本身：DragBar 是命中测试认定的可交互区域，
	/// 它的 Opacity 一旦动到 0，里面的键就跟着一起消失，
	/// 鼠标也就点不到了（看不见的东西点不着，用户只会以为程序卡住）。
	/// 只动这些键自己就没有这个问题。
	///
	/// animate:false 用于状态切换（开关穿透、切模式），直接落值不要动画。
	/// ms 是动画时长：显入 180ms 够跟手；收起走 420ms，账面可见地「慢慢收走」。
	///
	/// 为什么动画前要先读再设：直接 BeginAnimation(null) 会把 Opacity 打回基值
	/// （基值是收起态的 0），新动画拿到的起点就是 0，
	/// 看起来就是瞬间消失。先读当前值（挂着的旧动画还垫着它）设回本地值，
	/// 再挂新动画，淡出才是真正的「从当前透明度渐变到 0」。</summary>
	private void ApplyBarOpacity(double v, bool animate, int ms = 180)
	{
		foreach (UIElement el in KeyButtons())
		{
			// 淡到 0 之后按钮仍然是命中测试的可见元素，会照常吃掉点击。
			// 表现就是「键位看不见，但点它有反应」，鼠标在那一块怎么按都按不到
			// 底下的窗口。必须同时把命中测试关掉，那才是真隐藏。
			if (el is UIElement ue)
			{
				ue.IsHitTestVisible = v > 0.01;
			}
			if (animate)
			{
				double from = el.Opacity;
				el.BeginAnimation(UIElement.OpacityProperty, null);
				el.Opacity = from;
				el.BeginAnimation(UIElement.OpacityProperty,
					new System.Windows.Media.Animation.DoubleAnimation(from, v,
						new Duration(TimeSpan.FromMilliseconds(ms)))
					{
						EasingFunction = new System.Windows.Media.Animation.CubicEase
						{
							EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
						}
					});
			}
			else
			{
				el.BeginAnimation(UIElement.OpacityProperty, null);
				el.Opacity = v;
			}
		}
	}

	/// <summary>所有参与淡入淡出的键位。加新键只改这一处，
	/// 免得出现「某个新加的键忘了加进列表，于是永远常驻」这种漏网。</summary>
	private UIElement[] KeyButtons() => new UIElement[]
	{
		TranslationButton, PassthroughButton, CloseButton,
		LyricPrevButton, LyricPlayButton, LyricNextButton
	};

	/// <summary>键位的显隐策略。
	///
	/// 用户要的规则：「键位随时隐藏，鼠标经过就动画显示出来全部键位」——
	/// 穿透和非穿透一视同仁，不再有「非穿透常显」的分支：
	///
	/// - 默认收起（opacity 0 + 关命中测试），整片窗口露给歌词；
	/// - 鼠标进入窗口任何位置（穿透靠 60ms 光标轮询兜底，非穿透靠
	///   MouseMove）就淡入全部 7 个键（180ms）；
	/// - 鼠标离开窗口或离开后停留 1.6s 无移动，收回倒计时启动，再收起。
	///
	/// 纯装饰性逻辑，全部失败都吞掉：它出问题最多是键位不出现，不该影响播放。</summary>
	private void ApplyHoverReveal()
	{
		try
		{
			_hideBarTimer.Stop();
			_cursorWatchTimer.Stop();
			// 每切一次开关就跑一遍，这里必须先摘再加，
			// 否则 += 会把同一个处理器叠加上去（开穿透两次就触发两遍）。
			DragBar.MouseEnter -= DragBar_MouseEnter;
			DragBar.MouseLeave -= DragBar_MouseLeave;
			MouseMove -= OnHoverMove;
			MouseLeave -= OnWindowMouseLeave;

			ApplyBarOpacity(0.0, animate: false);
			_barVisible = false;
			// 两种模式都跑光标轮询：穿透态背景区是 HTTRANSPARENT，
			// 靠它兜「鼠标在窗口里但没压在任何控件上」的时刻；
			// 非穿透态虽然 MouseMove 收得到，但轮询还负责「鼠标早就在
			// 窗口外」时的收回倒计时（那时窗口根本收不到任何鼠标事件）。
			_cursorWatchTimer.Start();
			// 用 DragBar 自己的 MouseEnter/MouseLeave，不靠窗口级 MouseMove：
			// 穿透态下背景区是 HTTRANSPARENT，窗口根本收不到移动消息；
			// 只有「被判成客户区」的控件才收得到。标题栏既然已经是热区，
			// 直接在它身上挂事件最可靠，也不依赖 e.GetPosition(this) 的坐标换算。
			// 留着它是为了零延迟：轮询最快也要等一个 60ms 的间隔，
			// 而鼠标正好压在上沿那 30px 时不该有这种迟滞。
			DragBar.MouseEnter += DragBar_MouseEnter;
			DragBar.MouseLeave += DragBar_MouseLeave;
			// 键盘/手柄触发的合成鼠标移动没有 MouseEnter，补一份兜底。
			// 非穿透态整片窗口都能收到 MouseMove，也是靠它点亮的。
			MouseMove += OnHoverMove;
			// 离开窗口（只有非穿透态收得到）走同一个倒计时。
			MouseLeave += OnWindowMouseLeave;
			// 刚打开时鼠标可能正压在这个窗上，这时先亮着再倒计时，
			// 别让用户对着一个「键位不见了」的窗口愣一下才反应过来要动鼠标。
			RevealBarIfCursorInside();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗键位淡入淡出失败");
		}
	}

	/// <summary>光标此刻就落在窗口的热区里就直接点亮。切穿透、刚显示出来都用它兜一次。</summary>
	private void RevealBarIfCursorInside()
	{
		try
		{
			if (_closing || !TryGetCursorScreenPoint(out Point screen))
			{
				return;
			}
			Point loc = PointFromScreen(screen);
			if (IsKeyHotArea(loc))
			{
				ShowButtonBar();
			}
			else
			{
				// 不在热区：给一段宽限再收，避免用户正把鼠标移过来就消失。
				_hideBarTimer.Stop();
				_hideBarTimer.Start();
			}
		}
		catch { }
	}

	/// <summary>光标是不是在「键位该出现」的区域里。
	///
	/// 用户要的行为是：开了穿透之后，界面本身继续透明可穿，
	/// 但键位开始跟着鼠标走 —— 鼠标扫过歌词文字或者弹窗任意一块，
	/// 全部键位出来；鼠标完全不在这个窗上就一直藏着。
	///
	/// 所以穿透态的热区判据就是「光标在不在这个窗口的矩形里」，
	/// 整片都算，歌词区也算。以前只认标题栏那 40px 和底部播放键，
	/// 结果鼠标明明压在歌词上，键位还是不见 ——
	/// 而这恰恰是用户最常把鼠标停的位置。
	///
	/// 非穿透态整片窗口都算（反正窗口本身可点，鼠标消息全收得到）。</summary>
	private bool IsKeyHotArea(Point loc)
	{
		bool inside = loc.X >= -2.0 && loc.X <= ActualWidth + 2.0
			&& loc.Y >= -2.0 && loc.Y <= ActualHeight + 2.0;
		if (!inside)
		{
			return false;
		}
		// 尺寸还没量出来的时候不要判定成热区：
		// ActualWidth/Height 都是 0 时任何点都会「在窗口内」，
		// 于是刚显示出来的那一瞬不管鼠标在哪都判成在窗口里。
		if (ActualWidth <= 0 || ActualHeight <= 0)
		{
			return false;
		}
		return true;
	}

	/// <summary>轮询全局光标：进窗口就点亮键位，出窗口就开始收回倒计时。
	///
	/// 两种模式都跑：穿透态背景区收不到鼠标消息，全靠这里；
	/// 非穿透态靠它兜「鼠标在窗口外、窗口收不到任何事件」时的收回。
	///
	/// 判定用 PointFromScreen 而不是窗口的 Left/Top/ActualWidth：
	/// GetCursorPos 给的是物理像素，PointFromScreen 负责按当前 DPI 换算成
	/// 窗口内的逻辑坐标，DPI 缩放和副屏负坐标都由它处理，比自己算稳妥。</summary>
	private void CursorWatch_Tick(object? sender, EventArgs e)
	{
		if (_closing || !IsLoaded || !IsVisible) return;
		try
		{
			if (!TryGetCursorScreenPoint(out Point screen)) return;
			// 先转成窗口内坐标：留一点余量，免得窗口边缘那一像素
			// 因为取整被判成「在外面」，鼠标明明压着键位却点不亮。
			Point loc = PointFromScreen(screen);

			if (IsKeyHotArea(loc))
			{
				// 鼠标还在热区里：撤掉「收回」倒计时，别让它在用户
				// 正准备去点按钮的时候把键位抽走。
				_hideBarTimer.Stop();
				ShowButtonBar();
			}
			else if (_barVisible)
			{
				// 离开热区：给 1.6s 宽限再收，免得从歌词区移到键位
				// 的那一下路上就把按钮弄没了。
				// 只在倒计时没跑起来时启动一次。这里 60ms 一轮，
				// 之前每轮都 Stop+Start，倒计时永远被重置数不完，
				// 表现就是键位露过一次之后再也不收了。
				if (!_hideBarTimer.IsEnabled)
				{
					_hideBarTimer.Start();
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗光标轮询失败");
			_cursorWatchTimer.Stop();
		}
	}

	/// <summary>真正点亮键位（淡入）。已亮着就直接返回，
	/// 免得每 60ms 重启一次动画。</summary>
	private void ShowButtonBar()
	{
		if (_closing) return;
		try
		{
			if (_barVisible) return;
			_barVisible = true;
			ApplyBarOpacity(1.0, animate: true);
		}
		catch { }
	}

	/// <summary>鼠标进入标题栏热区：淡入并开始「离开后隐藏」倒计时。</summary>
	private void DragBar_MouseEnter(object sender, MouseEventArgs e)
	{
		RevealButtonBar();
	}

	/// <summary>鼠标离开标题栏：开始倒计时，鼠标还在附近就取消。
	/// 两种模式共用（键位现在都是悬停显隐，见 ApplyHoverReveal）。</summary>
	private void DragBar_MouseLeave(object sender, MouseEventArgs e)
	{
		if (_closing) return;
		try
		{
			_hideBarTimer.Stop();
			_hideBarTimer.Start();
		}
		catch { }
	}

	/// <summary>鼠标移出整个窗口（只有非穿透态收得到 MouseLeave）。
	/// 同样走倒计时而不是直接收：用户把鼠标从窗口里扫出去时
	/// 按一下就没，很难受。</summary>
	private void OnWindowMouseLeave(object sender, MouseEventArgs e)
	{
		if (_closing) return;
		try
		{
			_hideBarTimer.Stop();
			_hideBarTimer.Start();
		}
		catch { }
	}

	private void RevealButtonBar()
	{
		if (_closing) return;
		try
		{
			// 鼠标还在窗口里，所以撤掉「离开后隐藏」的倒计时。
			// 少了这一步，倒计时会在用户正要去点按钮的路上把键位抽走。
			_hideBarTimer.Stop();
			ShowButtonBar();
		}
		catch { }
	}

	private void OnHoverMove(object sender, MouseEventArgs e)
	{
		// 穿透态下这里只在「被判成客户区」的元素上才可能收到，
		// 也就是那几条按钮区域；整片歌词靠 CursorWatch_Tick 兜。
		// 所以这里不再按 Y 过滤：能收到就说明鼠标确实在本窗口的
		// 可交互区域里，点亮是对的。
		RevealButtonBar();
	}

	// ---- 底部播放键 ----
	// 直接调 MusicService，不去找主窗口：主窗口那几个 Click 是 private 的，
	// 而且歌词窗可能被主窗口最小化带着隐藏，走一遍它反而多一层状态同步。
	// MusicService 才是唯一的事实来源，主窗口底栏也是调它。

	private void LyricPrevButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			MusicService.PlayPrevious();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗上一首失败");
		}
	}

	private void LyricNextButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			MusicService.PlayNext();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗下一首失败");
		}
	}

	private void LyricPlayButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			// 没有在放歌时按播放键 = 从头播，别只 Resume 一个空的播放器。
			if (MusicService.NoMusicMode)
			{
				MusicService.Resume();
			}
			else if (MusicService.IsPlaying)
			{
				MusicService.Pause();
			}
			else
			{
				MusicService.Resume();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗播放暂停失败");
		}
		// 立刻改图标：等 StateChanged 回来会有一小段延迟（切歌要走网络），
		// 按下去的那一下看不到变化会让人以为没按到。
		UpdatePlayGlyph();
	}

	/// <summary>播放/暂停图标。字形和主窗口底栏一致（E769 暂停 / E768 播放），
	/// 两处用同一套图标是这个应用的一贯做法。</summary>
	private void UpdatePlayGlyph()
	{
		try
		{
			bool playing = MusicService.IsPlaying && !MusicService.NoMusicMode;
			// 用 \uE769/\uE768 转义而不是把字形直接写进源码：
			// 这是 Segoe MDL2 的私用区码位，粘进源码后在某些编辑器/
			// 代码页里会被换成问号，图标就变成空白方块了。
			LyricPlayGlyph.Text = playing ? "\uE769" : "\uE768";
			LyricPlayButton.ToolTip = playing ? "暂停" : "播放";
		}
		catch { }
	}

	private void OnMusicStateChanged()
	{
		// StateChanged 是从各种线程抛出来的（播放线程、定时器、UI 线程都有），
		// 这里必须自己切回 UI 线程。
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(UpdatePlayGlyph));
			return;
		}
		UpdatePlayGlyph();
	}

	private void HideButtonBarTimer_Tick(object? sender, EventArgs e)
	{
		_hideBarTimer.Stop();
		if (_closing) return;
		try
		{
			_barVisible = false;
			// 淡出而不是瞬间消失：和淡入对称，1.6s 静止后慢慢收走，
			// 比「一眨眼没了」更像「自动收起」而不是「渲染坏了」。
			// 420ms 收起比 180ms 显入慢一拍，收尾看得见。
			ApplyBarOpacity(0.0, animate: true, ms: 420);
		}
		catch { }
	}

	/// <summary>拉伸把手。改 Width/Height 而不是 ActualWidth，是让 WPF 自己
	/// 走布局，顺带把 MinWidth/MinHeight 一起夹住。</summary>
	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
		_hwndSource?.AddHook(WndProcHook);

		// 当前句的两层文字各挂一个平移变换，走马灯时同步改两个 X。
		// 不共用同一个 Transform 实例：WPF 的 Freezable 同时挂到两个元素上时
		// 变更通知链会互相干扰，这里只是每帧写两个属性，没必要省这点开销。
		_horizBaseX = new TranslateTransform();
		_horizLitX = new TranslateTransform();
		HorizCurBase.RenderTransform = _horizBaseX;
		HorizCurLit.RenderTransform = _horizLitX;

		// 切句过渡的变换。挂在「整格」上而不是挂在单行文字上：
		// 当前句那格是 HorizWipeHost（里面套着擦除用的底色层和亮色层），
		// 挂在它身上两层会一起动，不会出现亮暗两层错位。
		// 当前句要同时做「上下摆动」「选中缩放」「上移位移」，所以是 TransformGroup。
		// 三个变换必须分开：摆动是长驻循环、位移和缩放都是每轮动画重写一次，
		// 塞进同一个变换里会互相覆盖（谁后写谁生效，另一个直接不动）。
		_curBob = new TranslateTransform();
		_curScale = new ScaleTransform(1, 1);
		// 缩放要绕中心缩，不然字会朝左上角缩，看着像在往角上跑。
		HorizWipeHost.RenderTransformOrigin = new Point(0.5, 0.5);
		// 翻译两层各挂一个横向位移，才能做出「新句子把旧句子往右顶」。
		_trIn = new TranslateTransform();
		_trOut = new TranslateTransform();
		HorizTranslation.RenderTransform = _trIn;
		HorizTranslationOut.RenderTransform = _trOut;

		// 三行各挂一组「竖向位移 + 缩放」，给「顶上去」那套动画用。
		// 每行一组而不是共用一个位移：三行现在是各走各的轨迹
		//（上一句往上退并缩小、下一句升进中间并放大、再下一句从下面冒出来），
		// 共用一个位移就又退回「整块一起挪」了。
		_prevShift = new TranslateTransform();
		_curShift = new TranslateTransform();
		_nextShift = new TranslateTransform();
		_prevScale = new ScaleTransform(1, 1);
		_nextScale = new ScaleTransform(1, 1);
		HorizPrev.RenderTransform = new TransformGroup
		{
			Children = { _prevShift, _prevScale }
		};
		HorizPrev.RenderTransformOrigin = new Point(0.5, 0.5);
		HorizWipeHost.RenderTransform = new TransformGroup
		{
			Children =
			{
				// 上移在前、摆动缩放在后：WPF 按顺序相乘，
				// 顺序反过来的话上移会被摆动/缩放一起带着挪。
				_curShift,
				_curBob,
				_curScale
			}
		};
		HorizNext.RenderTransform = new TransformGroup
		{
			Children = { _nextShift, _nextScale }
		};
		HorizNext.RenderTransformOrigin = new Point(0.5, 0.5);
	}

/// <summary>窗口消息钩子。做两件事：四边伸缩的命中测试，
	/// 以及穿透态下的区域命中测试。</summary>
	private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WmNcHitTest && !_closing)
		{
			// 顺序有讲究：穿透态先问「在不在可交互区域」。
			// 那些区域必须回 HTCLIENT，不能给 HT*RESIZE，
			// 否则拖键位栏会变成改窗口大小、拖穿透开关会去拉窗口边。
			if (_passthrough)
			{
				handled = true;
				return HitTestPassthrough(lParam);
			}
			// 非穿透态：只在四边/四角回 HT*RESIZE。
			//
			// 键位栏这里必须「不处理」（返回 0 且 handled=false），让命中
			// 落到 WPF 自己身上，由 DragBar_MouseLeftButtonDown 调 DragMove()。
			// 之前这里对整条键位栏返回 HTCAPTION，想借系统的原生拖动，
			// 结果是按下变成了 WM_NCLBUTTONDOWN，WPF 的 MouseLeftButtonDown
			// 根本不触发——窗口直接拖不动了。
			// 而且键位栏里的四个键也会被系统当成标题栏的一部分，
			// 点键位变成「按标题栏」。所以这里不碰它。
			IntPtr edge = HitTestResizeEdges(lParam);
			if (edge != IntPtr.Zero)
			{
				handled = true;
				return edge;
			}
		}
		return IntPtr.Zero;
	}

	private static Point ScreenPointFromLParam(IntPtr lParam)
	{
		// WM_NCHITTEST 的 lParam 带的是屏幕坐标，16 位有符号。
		// 不做符号扩展的话，多屏副屏在左边/上边（负坐标）时算出来的点会错位。
		long v = lParam.ToInt64();
		return new Point(
			(short)(v & 0xFFFF),
			(short)((v >> 16) & 0xFFFF));
	}

	/// <summary>四边 + 四角的伸缩命中测试。返回 IntPtr.Zero 表示不在边上。
	///
	/// WindowStyle=None + AllowsTransparency=True 的窗口没有系统边框，
	/// WindowChrome 又要求非分层窗口所以也不生效，只能自己按屏幕坐标
	/// 算离每条边多远，命中就回对应的 HT*RESIZE——系统会自动换上双向光标、
	/// 并接管后续的拖拽缩放，比自己放 Thumb 稳，也不会在窗口里留一个
	/// 常驻的把手。
	///
	/// 边宽取 6px：Windows 原生无边框窗口也是这个量级。宽了会跟标题栏
	/// 按钮抢点击（这个标题栏只有 30px 高），窄了又难点中。</summary>
	private IntPtr HitTestResizeEdges(IntPtr lParam)
	{
		try
		{
			if (WindowState != WindowState.Normal) return IntPtr.Zero;
			Point loc = PointFromScreen(ScreenPointFromLParam(lParam));
			double w = ActualWidth, h = ActualHeight;
			if (w <= 0 || h <= 0) return IntPtr.Zero;

			const double border = 6.0;
			// 不在边上就早退：绝大多数命中测试都在窗口中间，
			// 少了这一步每帧都要算完八个布尔值。
			if (loc.X > border && loc.X < w - border && loc.Y > border && loc.Y < h - border)
			{
				return IntPtr.Zero;
			}

			bool left = loc.X <= border;
			bool right = loc.X >= w - border;
			bool top = loc.Y <= border;
			bool bottom = loc.Y >= h - border;

			if (top && left) return new IntPtr(HtTopLeft);
			if (top && right) return new IntPtr(HtTopRight);
			if (bottom && left) return new IntPtr(HtBottomLeft);
			if (bottom && right) return new IntPtr(HtBottomRight);
			if (left) return new IntPtr(HtLeft);
			if (right) return new IntPtr(HtRight);
			if (top) return new IntPtr(HtTop);
			if (bottom) return new IntPtr(HtBottom);
			return IntPtr.Zero;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗四边伸缩命中测试失败");
			return IntPtr.Zero;
		}
	}

	/// <summary>穿透态：命中可交互元素就当普通客户区，否则回 HTTRANSPARENT 给下面的窗口。</summary>
	private IntPtr HitTestPassthrough(IntPtr lParam)
	{
		try
		{
			Point screen = ScreenPointFromLParam(lParam);
			foreach (FrameworkElement el in InteractiveRegions())
			{
				Rect r = ScreenBounds(el);
				if (!r.IsEmpty && r.Contains(screen)) return new IntPtr(HtClient);
			}
			return new IntPtr(HtTransparent);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗穿透命中测试失败");
			return new IntPtr(HtTransparent);
		}
	}

	/// <summary>穿透态下仍然要能点到的元素。Collapsed 的元素 ActualWidth 是 0，
	/// 会被 ScreenBounds 判成空矩形自动跳过，所以不用在这里做可见性判断。
	///
	/// DragBar 必须在列表里（标题、竖横、翻译、关闭四个都在它里面）。
	/// 原来的实现只列了这四个按钮：按钮在 DragBar 内部，于是标题栏整条
	/// 除按钮那一小块都回 HTTRANSPARENT，窗口收不到 WM_MOUSEMOVE，
	/// OnHoverMove 永远不触发——表现就是「开了穿透标题栏就再也不出来」。
	/// 整条 30px 标题栏设成热区后，鼠标移到上沿就会点亮它；
	/// 代价是这 30px 不再穿透，属于刻意的取舍。
	///
	/// 底部播放键同理：整条 TransportBar 进列表，否则穿透态下
	/// 那三个键点了没反应（鼠标直接穿到下面的窗口去了）。</summary>
	private IEnumerable<FrameworkElement> InteractiveRegions()
	{
		// 穿透开关排最前：它在 DragBar 外面，少判一次就是少一次计算。
		yield return PassthroughButton;
		yield return DragBar;
		yield return TranslationButton;
		yield return CloseButton;
		// 底部播放键：连整条栏一起给，键之间那点缝隙也能点到。
		yield return TransportBar;
		yield return LyricPrevButton;
		yield return LyricPlayButton;
		yield return LyricNextButton;
	}

	/// <summary>元素在屏幕上的矩形。没布局过（宽度 0）时返回空矩形。</summary>
	private static Rect ScreenBounds(FrameworkElement el)
	{
		double w = el.ActualWidth, h = el.ActualHeight;
		if (w <= 0 || h <= 0) return Rect.Empty;
		Point p = el.PointToScreen(new Point(0, 0));
		return new Rect(p, new Size(w, h));
	}

	private const int WmNcHitTest = 0x0084;
	private const int HtClient = 1;
	private const int HtTransparent = -1;

	// 八条边的伸缩命中码。数值来自 winuser.h 的 WM_NCHITTEST 返回约定：
	// 单边在 HTLEFT~HTBOTTOM(10~12)，角在 HTTOPLEFT(13)~HTBOTTOMRIGHT(16)。
	private const int HtLeft = 10;
	private const int HtRight = 11;
	private const int HtTop = 12;
	private const int HtTopLeft = 13;
	private const int HtTopRight = 14;
	private const int HtBottom = 15;
	private const int HtBottomLeft = 16;
	private const int HtBottomRight = 17;
	private const int GwlExStyle = -20;
	private const int WsExNoActivate = 0x08000000;

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	/// <summary>读全局光标位置（物理像素）。
	///
	/// 用 SetCursorPos 的老式 P/Invoke 而不是 System.Windows.Forms.Cursor：
	/// 后者会把 WinForms 初始化链拖进来，而歌词窗不需要它。</summary>
	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out NativePoint lpPoint);

	private static bool TryGetCursorScreenPoint(out Point screen)
	{
		try
		{
			if (GetCursorPos(out NativePoint p))
			{
				screen = new Point(p.X, p.Y);
				return true;
			}
		}
		catch { }
		screen = default;
		return false;
	}


	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		// 关掉窗口 = 把功能开关置 false，让底部按钮的高亮也跟着灭。
		// 以前这里只 Hide()，开关还留在 true，于是用户「窗口没了、按钮还亮着」，
		// 再点按钮会先走「置 false」这一半，看起来就是点了没反应，得点第二下。
		// 走 Enabled=false 还有个好处：设���、服务、动画的收尾都归一处管。
		LyricRuntime.Enabled = false;
	}

	/// <summary>鼠标按在空白处就开始拖动整个窗口。
	///
	/// 原来只有 30px 的标题条能拖，那个条还只占标题栏中间一小段，
	/// 想挪窗口得先精准压在四个键的缝里。现在改成：
	/// 除了键位、输入框和竖排歌词列表（那些要留给点击/滚轮），
	/// 窗口里任何地方按住都能拖，包括底部那排控制栏的上沿和四周留白。</summary>
	private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ButtonState != MouseButtonState.Pressed || e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		if (!CanDragFrom(e.OriginalSource as DependencyObject))
		{
			return;
		}
		e.Handled = true;
		BeginDrag();
	}

	/// <summary>这个命中的东西能不能当拖动起点。</summary>
	private static bool CanDragFrom(DependencyObject? node)
	{
		while (node is not null)
		{
			// 键、输入框、可滚动/可点选的控件一律不拖，
			// 否则点一下按钮就变成「按住标题栏」，按钮永远按不动。
			if (node is ButtonBase or TextBoxBase
				or System.Windows.Controls.Primitives.ScrollBar
				or ScrollViewer)
			{
				return false;
			}
			node = node is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(node)
				: LogicalTreeHelper.GetParent(node);
		}
		return true;
	}

	/// <summary>标题条拖动。DragMove 要在鼠标按下时调，
	/// 而且按下之后不能再走别的逻辑，否则窗口会跟着跳一下。</summary>
	private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (IsInsideButton(e.OriginalSource as DependencyObject))
		{
			return;
		}
		if (e.ButtonState != MouseButtonState.Pressed || e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		BeginDrag();
	}

	/// <summary>开始拖动窗口。
	///
	/// 原来这里调的是 Window.DragMove()，它是「模态」的：内部发
	/// WM_NCLBUTTONDOWN(HTCAPTION) 然后在消息循环里等 WM_NCLBUTTONUP。
	/// 这个窗口是 AllowsTransparency + Topmost，穿透态还带
	/// WS_EX_NOACTIVATE，这条模态路径拿不到按下/抬起配对，
	/// DragMove() 直接抛 InvalidOperationException 被 catch 吞掉——
	/// 表现就是「按下去没反应，拖不动」。
	///
	/// 改成自己做：按下时抓一次鼠标并记下基准位置，移动时按位移
	/// 直接改 Left/Top。这条路径不依赖任何非客户区消息，
	/// 穿透态、置顶、透明窗口下都能用。
	///
	/// 抓鼠标抓在窗口上而不是 DragBar 上：现在可以从窗口任意位置发起
	/// （见 Surface_MouseLeftButtonDown），抓在 DragBar 上收不到后续移动事件。
	///
	/// 点位换算：PointToScreen 给的是物理像素，Left/Top 是 DIP，
	/// 所以位移要除以 DPI 缩放，否则高 DPI 下窗口会跑得比鼠标快。</summary>
	private void BeginDrag()
	{
		if (_closing || _dragging) return;
		try
		{
			Point grab = PointToScreen(new Point(
				Mouse.GetPosition(this).X, Mouse.GetPosition(this).Y));
			_dragOriginScreen = grab;
			_dragOriginLeft = Left;
			_dragOriginTop = Top;
			_dragging = true;
			CaptureMouse();
		}
		catch (Exception ex)
		{
			_dragging = false;
			LogHelper.LogError(ex, "歌词窗开始拖动失败");
		}
	}

	private void DragBar_MouseMove(object sender, MouseEventArgs e)
	{
		if (!_dragging || _closing) return;
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			EndDrag();
			return;
		}
		try
		{
			Point cur = PointToScreen(new Point(
				Mouse.GetPosition(this).X, Mouse.GetPosition(this).Y));
			double dxDevice = cur.X - _dragOriginScreen.X;
			double dyDevice = cur.Y - _dragOriginScreen.Y;
			DpiScale dpi = VisualTreeHelper.GetDpi(this);
			Left = _dragOriginLeft + (dpi.DpiScaleX > 0 ? dxDevice / dpi.DpiScaleX : dxDevice);
			Top = _dragOriginTop + (dpi.DpiScaleY > 0 ? dyDevice / dpi.DpiScaleY : dyDevice);
		}
		catch (Exception ex)
		{
			EndDrag();
			LogHelper.LogError(ex, "歌词窗拖动失败");
		}
	}

	private void EndDrag()
	{
		if (!_dragging) return;
		_dragging = false;
		try
		{
			if (IsMouseCaptured) ReleaseMouseCapture();
		}
		catch { }
		SaveBounds();
	}

	/// <summary>抬手：结束拖动并存位置。</summary>
	private void DragBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		EndDrag();
	}

	/// <summary>鼠标被系统抢走（切窗、按 Alt+Tab、弹出菜单）时收尾。
	/// 不处理的话 _dragging 会一直卡在 true，下次移动鼠标窗口就跟着跑。</summary>
	private void DragBar_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (_dragging)
		{
			_dragging = false;
			SaveBounds();
		}
	}

	private bool _dragging;
	private Point _dragOriginScreen;
	private double _dragOriginLeft;
	private double _dragOriginTop;

	/// <summary>命中点是否落在某个按钮里（从命中元素往上走到 DragBar 为止）。</summary>
	private bool IsInsideButton(DependencyObject? node)
	{
		while (node != null && node != DragBar)
		{
			if (node is ButtonBase)
			{
				return true;
			}
			node = node is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(node)
				: LogicalTreeHelper.GetParent(node);
		}
		return false;
	}


	/// <summary>右键标题条的菜单：置顶、翻译、穿透、刷新歌词、恢复默认位置。
	/// 从处理器里整个抽出来，只为让入口一眼看完；各项逻辑一字未改。</summary>
	private System.Windows.Controls.ContextMenu BuildDragBarContextMenu()
	{
		var menu = new System.Windows.Controls.ContextMenu();
		MenuItem topmost = new() { Header = Loc.Default["lyric_win_topmost"], IsCheckable = true, IsChecked = Topmost };
		topmost.Click += (_, _) =>
		{
			Topmost = topmost.IsChecked;
			AppSettings s = SettingsManager.Instance;
			s.LyricWindowTopmost = Topmost;
			SettingsManager.Save(s);
		};
		MenuItem tr = new() { Header = Loc.Default["lyric_win_translation"], IsCheckable = true, IsChecked = SettingsManager.Instance.LyricShowTranslation };
		tr.Click += (_, _) =>
		{
			TranslationButton.IsChecked = tr.IsChecked;
			TranslationButton_Click(TranslationButton, new RoutedEventArgs());
		};
		MenuItem pass = new() { Header = "Click-through", IsCheckable = true, IsChecked = SettingsManager.Instance.LyricWindowClickThrough };
		pass.Click += (_, _) => SetPassthrough(pass.IsChecked);
		MenuItem refresh = new() { Header = Loc.Default["lyric_win_refresh"] };
		refresh.Click += async (_, _) =>
		{
			string? p = MusicService.CurrentPath;
			if (string.IsNullOrEmpty(p))
			{
				return;
			}
			LyricBundle? b = await _follow.RetryAsync(p);
			Apply(b);
		};
		MenuItem reset = new() { Header = Loc.Default["lyric_win_reset_pos"] };
		reset.Click += (_, _) =>
		{
			WindowState = WindowState.Normal;
			Rect wa = SystemParameters.WorkArea;
			Left = wa.Right - ActualWidth - 40;
			Top = wa.Bottom - ActualHeight - 40;
			SaveBounds();
		};
		menu.Items.Add(topmost);
		menu.Items.Add(tr);
		menu.Items.Add(pass);
		menu.Items.Add(new System.Windows.Controls.Separator());
		menu.Items.Add(refresh);
		menu.Items.Add(reset);
		return menu;
	}

	/// <summary>右键标题条出菜单。</summary>
	private void DragBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
	{
		System.Windows.Controls.ContextMenu menu = BuildDragBarContextMenu();
		menu.IsOpen = true;
		e.Handled = true;
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		StopAnimations();
		_hideBarTimer.Stop();
		_cursorWatchTimer.Stop();
		_saveBoundsTimer.Stop();
		// 擦除改到渲染帧上推进（见 OnRendering），关窗必须摘掉：
		// 这是静态事件，挂着就把整个窗口钉在内存里。
		StopRenderHook();
		// 窗口快没了还留着订阅没有必要，而且它们捕获着 this
		MouseMove -= OnHoverMove;
		MouseLeave -= OnWindowMouseLeave;
		MouseMove -= DragBar_MouseMove;
		MouseLeftButtonUp -= DragBar_MouseLeftButtonUp;
		LostMouseCapture -= DragBar_LostMouseCapture;
		DragBar.MouseEnter -= DragBar_MouseEnter;
		DragBar.MouseLeave -= DragBar_MouseLeave;
		MusicService.StateChanged -= OnMusicStateChanged;
		Loc.Default.LanguageChanged -= OnLanguageChanged;
		// SaveBounds 里第一行就 if (_closing) return，所以必须先存再置位。
		// 顺序反了的话，App.Exit() 这条唯一的保存路径永远是空跑，
		// 表现就是「拖到别处，关掉，再开又回老地方」。
		SaveBounds();
		_closing = true;
		_hwndSource?.RemoveHook(WndProcHook);
		_hwndSource = null;
		_follow.LyricsChanged -= OnLyricsChanged;
		LyricRuntime.OnWindowClosed();
	}


	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		// Esc 收起窗口而不是关掉进程。歌词窗是挂在主程序上的附属窗口，
		// 在这里按 Esc 却把整个程序带走是最糟的交互。
		// 和 X 一样走 Enabled=false：按钮灭灯，再点一下就回来。
		if (e.Key == Key.Escape)
		{
			LyricRuntime.Enabled = false;
			e.Handled = true;
			return;
		}
		base.OnPreviewKeyDown(e);
	}
}

