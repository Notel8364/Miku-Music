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

	/// <summary>标题栏当前是否亮着。用它做 RevealButtonBar 的短路，
	/// 免得鼠标停在热区里时每一帧都重启一遍淡入动画。</summary>
	private bool _barVisible;

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
		_saveBoundsTimer.Tick += (_, _) => { _saveBoundsTimer.Stop(); SaveBounds(); };


		AppSettings s = SettingsManager.Instance;
		ModeButton.IsChecked = s.LyricWindowVertical;
		TranslationButton.IsChecked = s.LyricShowTranslation;
		PassthroughButton.IsChecked = s.LyricWindowClickThrough;
		Topmost = s.LyricWindowTopmost;
		VerticalView.ShowTranslation = s.LyricShowTranslation;
		ApplyFontFamily(s.LyricFontFamily);
		ApplyFontColor();
		ApplyMode(s.LyricWindowVertical);

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

	/// <summary>用户在设置里给歌词挑的当前行颜色。空 = 跟随主题。
	/// 挑了色就整句用这个色（含擦除的底色层），不再做「暗→亮」的颜色过渡——
	/// 用户既然明确指定了颜色，动画再把它压暗一下会显得没生效。</summary>
	internal void ApplyFontColor()
	{
		try
		{
			string? hex = SettingsManager.Instance.LyricFontColor;
			if (string.IsNullOrWhiteSpace(hex))
			{
				// 还原成主题资源引用，色值和换主题都重新跟着走
				HorizCurBase.SetResourceReference(TextBlock.ForegroundProperty, CurBaseBrushKey);
				HorizCurLit.SetResourceReference(TextBlock.ForegroundProperty, CurLitBrushKey);
			}
			else
			{
				var c = (Color)ColorConverter.ConvertFromString(hex);
				// 底色层是亮色层擦完之前的底，亮度要压低一档，
				// 擦除动画才看得出「描边变粗」而不是两层完全同色。
				var b = new System.Windows.Media.SolidColorBrush(c);
				HorizCurBase.Foreground = new System.Windows.Media.SolidColorBrush(
					Color.FromRgb(
						(byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72)));
				HorizCurLit.Foreground = b;
			}
			// 颜色变了，之前量过的走马灯轨道宽度不按新画刷算了，得重量一次
			_marqueeTextWidth = 0;
			_lastFitWidth = -1;
			FitFontToWidth();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗切换颜色失败");
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

	private void Apply(LyricBundle? bundle)
	{
		_bundle = bundle;
		_activeIndex = -1;
		_path = MusicService.CurrentPath;

		if (bundle == null)
		{
			VerticalView.Load(null, null);
			StatusText.Visibility = Visibility.Visible;
			SetTitle(MusicService.CurrentDisplayTitle ?? Loc.Default["lyric_win_title"]);
			// 没歌词时两个动画源都得停，不然一个 45Hz 的空转计时器
			// 会在「找不到歌词」的状态下一直跑。
			StopAnimations();
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
			UpdatePanelVisibility();
			return;
		}

		SetTitle(MusicService.CurrentDisplayTitle ?? Loc.Default["lyric_win_title"]);
		StatusText.Visibility = Visibility.Collapsed;

		VerticalView.Load(bundle, _path);
		// 横排要立刻画一次，不然切到横排的瞬间是空的，要等下一个 tick。
		if (!SettingsManager.Instance.LyricWindowVertical)
		{
			int idx = bundle.IndexAt(LyricClock.PositionMs);
			_activeIndex = idx;
			ApplyHorizontal(idx);
		}
		// 歌词到位后要重新决定谁可见。构造函数里调 ApplyMode 时 _bundle 还是
		// null，那时会把两个面板都收起来（「没歌词」的布局）；而 Apply 以前
		// 只管改 StatusText、从不把它们放回来，于是窗口一片空白，
		// 要用户手动切一次竖/横模式才会出现歌词。
		UpdatePanelVisibility();
		SyncAnimationSources();
	}

	/// <summary>按「有没有歌词 + 当前模式」决定竖排/横排/提示文字谁可见。</summary>
	private void UpdatePanelVisibility()
	{
		bool hasLyric = _bundle != null;
		bool vertical = SettingsManager.Instance.LyricWindowVertical;
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
		bool vertical = SettingsManager.Instance.LyricWindowVertical;
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
		// 两套动画互斥：PushUp 不摆（整块上移时再摆会跟位移打架）。
		if (!vertical && hasLyric && _activeIndex >= 0 && !Anim.NeedsRowShift)
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
			if (!SettingsManager.Instance.LyricWindowVertical)
			{
				ApplyHorizontal(idx, old);
			}
		}
		// 擦除每帧都要更新，不然横排只有「整句亮/整句暗」两态，没有卡拉OK效果
		if (!SettingsManager.Instance.LyricWindowVertical)
		{
			UpdateHorizontalWipe();
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
		double oldPrev = 0, oldCur = 0, oldNext = 0;
		if (shift)
		{
			oldPrev = TopInPanel(HorizPrev);
			oldCur = TopInPanel(HorizWipeHost);
			oldNext = TopInPanel(HorizNext);
		}

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
		HorizCurClip.Rect = new Rect(0, 0, 0, HorizCurLit.ActualHeight);
		// 字号不写死在 XAML 里，交给 FitFontToWidth 按这一句的实际长度算。
		FitFontToWidth();
		// 换句就把走马灯拨回句首，不管这一句是不是长句：
		// 短句虽然不会漂，但残留的位移会让它在切进来的瞬间从半截开始显示。
		ResetPan();
		// 已唱过的上一句压暗、还没唱的下一句保持稍亮，
		// 于是「已经唱到哪」一眼能看出来，而不是三行灰得一样。
		HorizPrev.Opacity = PrevPlayed;
		HorizNext.Opacity = NextPending;

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
			// 互斥：PushUp 那套是「三行整块挪」，Float 那套是「当前句原地动」。
			// 两套同时跑会互相拉扯，所以这里只跑一套，另一套先落位。
			ResetCurSelect();
			StopCurBob();
			AnimatePushUp(oldPrev, oldCur, oldNext, anim);
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

	/// <summary>三行的竖向位移全部落位。</summary>
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
	}

	/// <summary>「下面歌词顶上去，下一句也冒出来」：三行整体上移一格。
	///
	/// 关键在于别去量「同一个格子换句前后差多少」——三行的行高是
	/// Auto / * / Auto，差值恒为 0，量了也是白量（这是之前那版动画
	/// 「看着没反应」的原因）。要量的是「内容从哪个格子搬到了哪个格子」：
	///
	///   旧内容          →  换句后所在格子      →  位移 = 新位置 - 旧位置
	///   -------------------------------
	///   当前句（中间）  →  上一句（顶格）      →  newPrev - oldCur
	///   下一句（底格）  →  当前句（中间）      →  newCur  - oldNext
	///   （新出现的）    →  下一句（底格）      →  从下面一格外冒出来
	///
	/// 三条都是「上面那句往下长出来一条缝」的效果，叠起来就是整块上移。</summary>
	private void AnimatePushUp(double oldPrev, double oldCur, double oldNext, LyricAnimTemplate anim)
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
			// 文本刚换过，得强制走一次布局才量得到新位置。
			HorizontalPanel.UpdateLayout();
			double newPrev = TopInPanel(HorizPrev);
			double newCur = TopInPanel(HorizWipeHost);
			double newNext = TopInPanel(HorizNext);
			// 量不到（三行里有 Height=0）就别硬滑，落位更安全。
			if (double.IsNaN(newPrev) || double.IsNaN(newCur) || double.IsNaN(newNext))
			{
				ResetRowShifts();
				return;
			}

			var dur = new Duration(TimeSpan.FromMilliseconds(anim.PushUpMs));
			var ease = new System.Windows.Media.Animation.CubicEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
			};

			// 旧中间 → 新顶格
			SlideRowY(_prevShift, double.IsNaN(oldCur) ? newCur : oldCur, newPrev, dur, ease);
			// 旧底格 → 新中间
			SlideRowY(_curShift, double.IsNaN(oldNext) ? newNext : oldNext, newCur, dur, ease);
			// 新底格：从下面一格外冒出来。间距取中间格和底格的距离，
			// 拿不到就退回 40px。
			double pitch = newCur - newNext;
			if (double.IsNaN(pitch) || pitch <= 1)
			{
				pitch = 40;
			}
			SlideRowY(_nextShift, newNext + pitch, newNext, dur, ease);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词上移动画失败");
			ResetRowShifts();
		}
	}

	/// <summary>一行从 oldTop 滑到 newTop。位移太短就直接落位，
	/// 不然一个 0.4px 的「滑一下」会变成一次肉眼可见的抖动。</summary>
	private static void SlideRowY(TranslateTransform t, double oldTop, double newTop,
		Duration dur, System.Windows.Media.Animation.IEasingFunction ease)
	{
		t.BeginAnimation(TranslateTransform.YProperty, null);
		if (double.IsNaN(oldTop) || double.IsNaN(newTop) || Math.Abs(oldTop - newTop) < 1.0)
		{
			t.Y = 0;
			return;
		}
		// 基值 = 终点，From = 起点。跟 AnimateXTo 同一个理由：
		// FillBehavior.Stop 结束后回落到基值，基值是终点才停得对。
		t.Y = newTop;
		t.BeginAnimation(TranslateTransform.YProperty,
			new System.Windows.Media.Animation.DoubleAnimation(oldTop - newTop, 0.0, dur)
			{
				EasingFunction = ease,
				FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
			});
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

	/// <summary>当前句的颜色过渡：从「下一句」那种暗色渐变到正常色。
	/// 拿不到 SolidColorBrush（主题刷可能是渐变或画刷）就整段跳过，
	/// 只保留位移，不影响可用性。
	///
	/// 动画期间是把 Foreground 整个换成一个自建画刷的，这会顶掉 XAML 里的
	/// {DynamicResource}——不还原的话，之后换主题时当前句的颜色就再也不跟着变了
	/// （它已经变成一个写死的普通画刷）。所以动画结束后必须把资源引用装回去。
	/// 装回去的键要跟 XAML 里写的一致：底色层 TextTertiaryBrush、亮色层 TextPrimaryBrush。</summary>
	private const string CurBaseBrushKey = "TextTertiaryBrush";
	private const string CurLitBrushKey = "TextPrimaryBrush";

	private void AnimateCurrentLineColor(Duration dur,
		System.Windows.Media.Animation.IEasingFunction ease)
	{
		AnimateBrush(HorizCurBase, CurBaseBrushKey, dur, ease);
		AnimateBrush(HorizCurLit, CurLitBrushKey, dur, ease);
	}

	private void AnimateBrush(TextBlock tb, string resourceKey, Duration dur,
		System.Windows.Media.Animation.IEasingFunction ease)
	{
		try
		{
			// 用户指定了当前行颜色就不做颜色过渡：这条动画的终点是
			// 「主题正常色」，跑完还会把画刷还原成主题资源引用，
			// 正好把刚设进去的自定义色冲掉。留给尺寸过渡和摆动。
			if (!string.IsNullOrWhiteSpace(SettingsManager.Instance.LyricFontColor))
			{
				return;
			}
			if (tb.Foreground is not System.Windows.Media.SolidColorBrush sb
				|| sb.IsFrozen) return;
			// 正常色就是它自己当前的颜色（XAML 里是 TextPrimary/TextTertiary）。
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
			// 过渡结束就还原成资源引用（换主题还能跟上），
			// 延时比动画本身长一点，避免动画还没停就把画刷换掉、闪一下。
			RestoreBrushLater(tb, resourceKey, (int)Anim.SelectMs + 60);
		}
		catch
		{
			// 颜色过渡纯属锦上添花，失败就只留位移
		}
	}

	/// <summary>动画结束后把 {DynamicResource} 装回 Foreground。
	/// 延后一点再装：FillBehavior.Stop 的回滚发生在动画最后一帧，
	/// 紧接着就换画刷会和那一帧打架，偶尔闪一下。
	/// 参数用 TextBlock：SetResourceReference 在 FrameworkElement 上（可用），
	/// 但 ForegroundProperty 只定义在 TextBlock 上，两边都得满足。</summary>
	private void RestoreBrushLater(TextBlock el, string resourceKey, int delayMs)
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
					el.BeginAnimation(TextBlock.ForegroundProperty, null);
					el.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
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
	/// [CurFontMin, CurFontMax]。测宽度用 FormattedText 而不是
	/// TextBlock.Measure：后者还要跑一遍换行布局，一行超长句能拖到
	/// 十几毫秒，而这里是每次切行都调。
	/// </summary>
	private void FitFontToWidth()
	{
		if (string.IsNullOrEmpty(HorizCurBase.Text))
		{
			return;
		}
		double avail = HorizWipeHost.ActualWidth;
		if (avail <= 1)
		{
			// 布局还没量过（刚切到横排/窗口刚显示）。先给个中间值，
			// SizeChanged 和下一帧会用真实宽度重算。
			HorizCurBase.FontSize = (CurFontMin + CurFontMax) / 2;
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

		HorizCurBase.FontSize = CurFontMax;
		while (HorizCurBase.FontSize > CurFontMin)
		{
			if (Measure(HorizCurBase.Text, typeface, HorizCurBase.FontSize, brush) <= budget)
			{
				break;
			}
			// 收一档再来。每档 1px 足够细腻。
			HorizCurBase.FontSize -= 1;
		}
		// 两层字号必须一致，否则擦除的亮色层和底层文字对不齐
		HorizCurLit.FontSize = HorizCurBase.FontSize;

		// 缩到下限还是放不下，就别再硬塞了：改成走马灯。
		// 这一句现在的单行宽度就是走马灯的「轨道长度」。
		_marqueeTextWidth = Measure(HorizCurBase.Text, typeface, HorizCurBase.FontSize, brush);
		// 轨道长度在这里一次算好。放在 FitFontToWidth 而不是 UpdatePan 里，
		// 是因为 SetMarquee→ResetPan 那一刻就要用到它，
		// 而那会儿 UpdatePan 还没跑，_panMax 还是上一句的残留值。
		_panMax = Math.Max(0, _marqueeTextWidth - avail);
		SetMarquee(_marqueeTextWidth > avail);
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

	/// <summary>上一句（已唱过）的透明度。</summary>
	private const double PrevPlayed = 0.22;

	/// <summary>下一句（还没唱到）的透明度。刻意比上一句亮一点，
	/// 形成「已唱—正在唱—待唱」三档，扫一眼就知道进度。</summary>
	private const double NextPending = 0.42;

	/// <summary>字号上限。横排当前行的基准字号，长的会往下缩、短的不超过它。</summary>
	private const double CurFontMax = 21;

	/// <summary>字号下限。再长的句子也保留这个大小，
	/// 再缩下去在大屏上就读不清了，宁可让它换行。</summary>
	private const double CurFontMin = 12;

	/// <summary>上次算字号时的可用宽度。LayoutUpdated 每次布局都会调，
	/// 用它挡住「宽度没变就别重算」，否则纯白烧 CPU。</summary>
	private double _lastFitWidth = -1;

	/// <summary>只在可用宽度真的变了才重算字号。</summary>
	private void FitFontToWidthOnce()
	{
		if (SettingsManager.Instance.LyricWindowVertical)
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

	/// <summary>横排当前行的逐字擦除 + 走马灯平移。45Hz 调一次。</summary>
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
		double p = _bundle.ProgressOf(_activeIndex, LyricClock.PositionMs);

		// 已播 / 未播的分色就是这两层：底下 TextTertiary（暗）铺满整句，
		// 上面 TextPrimary（亮）只露出「已经唱过的那一段」。
		HorizCurClip.Rect = new Rect(0, 0, Math.Clamp(p, 0, 1) * w, HorizCurLit.ActualHeight);

		UpdatePan();
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
			UpdateHorizontalWipe();
		}
		// 换模式 = 换动画源。漏了这句，新模式要等到下次切歌才动。
		SyncAnimationSources();
	}

	private void ModeButton_Click(object sender, RoutedEventArgs e)
	{
		// ToggleButton 自己已经切了 IsChecked，这里读新值
		bool vertical = ModeButton.IsChecked == true;
		AppSettings s = SettingsManager.Instance;
		s.LyricWindowVertical = vertical;
		SettingsManager.Save(s);
		ApplyMode(vertical);
		ModeGlyph.Text = vertical ? "\uE8D2" : "\uE8D1";
	}

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
		// 穿透态下整条标题栏透明：平时靠悬停才显出来，
		// 只有需要的时候才出现，不遮歌词。
		DragBar.Visibility = Visibility.Visible;
		DragBar.Opacity = on ? 0.0 : 1.0;
		// 穿透态把关闭键也收掉。
		//
		// 留着它有两个问题：一是它跟穿透开关贴在一起，穿透态下这两个键
		// 挤在正中，误点比误点角落的把手容易得多；二是这个窗口宽，
		// 看着像一块贴在桌面上的东西，「顺手关一下」会以为关的是它。
		// 关闭有别的路：托盘、快捷键、歌词窗口右键菜单。
		CloseButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
		// 穿透开关本身永远不参与淡入淡出——它是「开穿透之后唯一还能
		// 不靠悬停就点到的东西」。跟着 DragBar 一起透明的话，
		// 用户就只剩快捷键和托盘两条救生索，窗口自己没有任何出路。

		Root.BorderThickness = on ? new Thickness(0) : new Thickness(1);
		Root.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(12);
		Root.Background = on ? Brushes.Transparent
			: (Brush)TryFindResource("FlyoutBgBrush") ?? Brushes.Transparent;
		Root.BorderBrush = on ? Brushes.Transparent
			: (Brush)TryFindResource("FlyoutBorderBrush") ?? Brushes.Transparent;
		// 阴影会让「只剩字幕」的观感完全不成立，跟着一起关
		Root.Effect = on ? null : new System.Windows.Media.Effects.DropShadowEffect
		{
			BlurRadius = 26,
			ShadowDepth = 4,
			Opacity = 0.5,
			Color = System.Windows.Media.Colors.Black
		};

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
	}

	/// <summary>鼠标停在窗口上沿附近时把标题栏淡入，离开再淡出。
	/// 纯装饰，全部失败都吞掉：它出问题最多是标题栏不出现，不该影响播放。</summary>
	private void ApplyHoverReveal()
	{
		try
		{
			_hideBarTimer.Stop();
			// ApplyPassthrough 每切一次开关就跑一遍，这里必须先摘再加，
			// 否则 += 会把同一个处理器叠加上去（开穿透两次就触发两遍）。
			DragBar.MouseEnter -= DragBar_MouseEnter;
			DragBar.MouseLeave -= DragBar_MouseLeave;
			MouseMove -= OnHoverMove;
			if (!_passthrough)
			{
				DragBar.Opacity = 1.0;
				return;
			}
			DragBar.Opacity = 0.0;
			// 用 DragBar 自己的 MouseEnter/MouseLeave，不靠窗口级 MouseMove。
			// 穿透态下背景区是 HTTRANSPARENT，窗口根本收不到移动消息；
			// 只有「被判成客户区」的控件才收得到。标题栏既然已经是热区，
			// 直接在它身上挂事件最可靠，也不依赖 e.GetPosition(this) 的坐标换算。
			DragBar.MouseEnter += DragBar_MouseEnter;
			DragBar.MouseLeave += DragBar_MouseLeave;
			// 键盘/手柄触发的合成鼠标移动没有 MouseEnter，补一份兜底。
			MouseMove += OnHoverMove;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "歌词窗标题栏淡入淡出失败");
		}
	}

	/// <summary>鼠标进入标题栏热区：淡入并开始「离开后隐藏」倒计时。</summary>
	private void DragBar_MouseEnter(object sender, MouseEventArgs e)
	{
		RevealButtonBar();
	}

	/// <summary>鼠标离开标题栏：开始倒计时，鼠标还在附近就取消。</summary>
	private void DragBar_MouseLeave(object sender, MouseEventArgs e)
	{
		if (!_passthrough || _closing) return;
		try
		{
			_hideBarTimer.Stop();
			_hideBarTimer.Start();
		}
		catch { }
	}

	private void RevealButtonBar()
	{
		if (!_passthrough || _closing) return;
		try
		{
			if (_barVisible) return;
			_barVisible = true;
			DragBar.BeginAnimation(UIElement.OpacityProperty, null);
			var ease = new System.Windows.Media.Animation.CubicEase
			{
				EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
			};
			// 淡入用 130ms：再慢就让人觉得「鼠标都过去了按钮才出现」，
			// 反而不如不出现。
			DragBar.BeginAnimation(UIElement.OpacityProperty,
				new System.Windows.Media.Animation.DoubleAnimation(1.0,
					new Duration(TimeSpan.FromMilliseconds(130))) { EasingFunction = ease });
			_hideBarTimer.Stop();
			_hideBarTimer.Start();
		}
		catch { }
	}

	private void OnHoverMove(object sender, MouseEventArgs e)
	{
		if (!_passthrough || _closing) return;
		Point p = e.GetPosition(this);
		bool near = p.Y <= 34.0;
		if (near)
		{
			RevealButtonBar();
		}
	}

	private void HideButtonBarTimer_Tick(object? sender, EventArgs e)
	{
		_hideBarTimer.Stop();
		if (!_passthrough || _closing) return;
		try
		{
			_barVisible = false;
			DragBar.BeginAnimation(UIElement.OpacityProperty, null);
			DragBar.Opacity = 0.0;
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

		// 切句过渡的位移。挂在「整格」上而不是挂在单行文字上：
		// 当前句那格是 HorizWipeHost（里面套着擦除用的底色层和亮色层），
		// 挂在它身上两层会一起动，不会出现亮暗两层错位。
		// 当前句要同时做「上下摆动」和「选中缩放」，所以是 TransformGroup。
		// 摆动和缩放必须分成两个独立变换：两者都要长驻循环/长驻写值，
		// 塞进同一个变换里会互相覆盖（谁后写谁生效，另一个直接不动）。
		_curBob = new TranslateTransform();
		_curScale = new ScaleTransform(1, 1);
		var curTransforms = new TransformGroup();
		curTransforms.Children.Add(_curBob);
		curTransforms.Children.Add(_curScale);
		HorizWipeHost.RenderTransform = curTransforms;
		// 缩放要绕中心缩，不然字会朝左上角缩，看着像在往角上跑。
		HorizWipeHost.RenderTransformOrigin = new Point(0.5, 0.5);
		// 翻译两层各挂一个横向位移，才能做出「新句子把旧句子往右顶」。
		_trIn = new TranslateTransform();
		_trOut = new TranslateTransform();
		HorizTranslation.RenderTransform = _trIn;
		HorizTranslationOut.RenderTransform = _trOut;

		// 三行各挂一个竖向位移，给「顶上去」那套动画用。
		// 挂在行上（而不是像当前句那样挂在整格 TransformGroup 里）是因为
		// 上移动画和当前句的缩放不同时跑，分开挂互不干扰；
		// 只有 PushUp 生效时这几个变换才真的会被写值。
		_prevShift = new TranslateTransform();
		_curShift = new TranslateTransform();
		_nextShift = new TranslateTransform();
		HorizPrev.RenderTransform = _prevShift;
		HorizWipeHost.RenderTransform = new TransformGroup
		{
			Children =
			{
				// 上移在前、摆动缩放在后：WPF 按顺序相乘，
				// 顺序反过来的话整块上移会被缩放中心一起带着挪。
				_curShift,
				_curBob,
				_curScale
			}
		};
		HorizNext.RenderTransform = _nextShift;
	}

/// <summary>窗口消息钩子。做两件事：四边伸缩的命中测试，
	/// 以及穿透态下的区域命中测试。</summary>
	private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WmNcHitTest && !_closing)
		{
			// 顺序有讲究：穿透态先问「在不在可交互区域」。
			// 那些区域必须回 HTCAPTION/HTCLIENT，不能给 HT*RESIZE，
			// 否则拖标题栏会变成改窗口大小、拖穿透开关会去拉窗口边。
			if (_passthrough)
			{
				handled = true;
				return HitTestPassthrough(lParam);
			}
			// 非穿透态：边上给 HT*RESIZE，中间若落在标题栏上给 HTCAPTION。
			IntPtr edge = HitTestResizeEdges(lParam);
			if (edge != IntPtr.Zero)
			{
				handled = true;
				return edge;
			}
			if (IsOverDragBar(lParam))
			{
				handled = true;
				return new IntPtr(HtCaption);
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

	/// <summary>鼠标是否落在标题栏那条可拖动区域上。
	/// 拖窗口本来是靠 DragBar 自己的 MouseLeftButtonDown；这里让系统命中测试
	/// 也把标题栏认成 HTCAPTION，是为了顺带拿到原生行为，
	/// 比如拖到屏幕边缘时不再有吸附、而是正常移动（这个窗口是跟随歌词的，
	/// 吸附反而会把它拽到不该在的位置）。</summary>
	private bool IsOverDragBar(IntPtr lParam)
	{
		try
		{
			Rect r = ScreenBounds(DragBar);
			if (r.IsEmpty) return false;
			return r.Contains(ScreenPointFromLParam(lParam));
		}
		catch
		{
			return false;
		}
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
	/// PassthroughButton 在 DragBar 外面（XAML 里单独一层），
	/// 所以它不受 DragBar 透明度影响，这里要单独列出来才能点到。</summary>
	private IEnumerable<FrameworkElement> InteractiveRegions()
	{
		// 穿透开关排最前：它在 DragBar 外面，永远是客户区，
		// 而且在 DragBar 的右边。先判它等于少算一次。
		yield return PassthroughButton;
		yield return DragBar;
		yield return ModeButton;
		yield return TranslationButton;
		// 穿透态下 CloseButton 被 Collapsed 掉了，ScreenBounds 会返回
		// Rect.Empty 自动跳过，所以不用在这里再判一次可见性。
		yield return CloseButton;
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
	private const int HtCaption = 2;

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


	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		// 关掉窗口 = 把功能开关置 false，让底部按钮的高亮也跟着灭。
		// 以前这里只 Hide()，开关还留在 true，于是用户「窗口没了、按钮还亮着」，
		// 再点按钮会先走「置 false」这一半，看起来就是点了没反应，得点第二下。
		// 走 Enabled=false 还有个好处：设���、服务、动画的收尾都归一处管。
		LyricRuntime.Enabled = false;
	}

	/// <summary>标题条拖动。DragMove 要在鼠标按下时调，
	/// 而且按下之后不能再走别的逻辑，否则窗口会跟着跳一下。</summary>
	private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		// 标题条上那排按钮是它的子元素。Button 正常情况下会把这个事件标记成
		// 已处理，DragMove 不会跑；但那排按钮用的是自定义 ControlTemplate，
		// 合成点击/某些输入路径下标记会丢，结果是「点一下模式按钮，
		// 顺手把窗口也拖走了」——点歪一次窗口就跳到别处，用户根本不知道是哪一步。
		// 所以不指望事件标记，直接看命中的是不是按钮。
		if (IsInsideButton(e.OriginalSource as DependencyObject))
		{
			return;
		}
		if (e.ButtonState == MouseButtonState.Pressed && e.ChangedButton == MouseButton.Left)
		{
			try
			{
				DragMove();
			}
			catch (InvalidOperationException)
			{
				// 鼠标已释放时才会抛，忽略即可
			}
			SaveBounds();
		}
	}

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


	/// <summary>右键标题条出菜单：置顶、翻译、刷新歌词、恢复默认位置。</summary>
	private void DragBar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
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
		menu.IsOpen = true;
		e.Handled = true;
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		StopAnimations();
		_hideBarTimer.Stop();
		_saveBoundsTimer.Stop();
		// 窗口快没了还留着订阅没有必要，而且它们捕获着 this
		MouseMove -= OnHoverMove;
		DragBar.MouseEnter -= DragBar_MouseEnter;
		DragBar.MouseLeave -= DragBar_MouseLeave;
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

