using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicBox.Controls;
using MusicBox.Pages;
using MusicBox.Services;
using MusicBox.Services.Effects;
using MusicBox.Settings;

namespace MusicBox;

public partial class MainWindow : Window
{
	private const double _mouseWheelStep = 88;

	private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();

	private readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };

	private HwndSource? _hwndSource;

	private string? _currentKey;

	// 上一个停留的页面键，用于在切页时先回调它的 OnNavigatedFrom
	private string? _previousKey;

	private bool _navigating;

	private bool _closingConfirmed;

	private bool _progressDragging;

	private bool _suspendSelection;

	private bool _fadingOut;

	private bool _exitFaded;

	private DispatcherTimer? _gagTimer;

	/// <summary>当前唯一的主窗口实例，供单实例唤醒回调使用。</summary>
	public static MainWindow? Instance { get; private set; }

	public MainWindow()
	{
		InitializeComponent();
		Instance = this;
		TrayIconService.ShowWindowRequested = ShowFromTray;
		TrayIconService.ToggleWindowRequested = ToggleWindowVisibility;
		TrayIconService.PlayPauseRequested = TogglePlayPause;
		TrayIconService.PrevRequested = () => MusicService.PlayPrevious();
		TrayIconService.NextRequested = () => MusicService.PlayNext();
		TrayIconService.ExitRequested = ExitFromTray;
		MusicService.StateChanged += OnMusicStateChanged;
		MusicService.FetchStateChanged += OnFetchStateChanged;
		SourceInitialized += OnSourceInitialized;
		StateChanged += OnWindowStateChanged;
		UserLibrary.Changed += OnLibraryChangedForDrawer;

		// 系统媒体控制：音量飞轮 / 耳机按键 / 媒体浮层都转发给 NAudio 这条链
		SmtcService.PlayRequested += () => MusicService.Resume();
		SmtcService.PauseRequested += () => MusicService.Pause();
		SmtcService.NextRequested += () => MusicService.PlayNext();
		SmtcService.PrevRequested += () => MusicService.PlayPrevious();
		SmtcService.SeekRequested += pos =>
		{
			try
			{
				// MusicService.Seek 收的是秒
				MusicService.Seek(pos.TotalSeconds);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "SMTC 跳转失败");
			}
		};
		SmtcService.Attach();

		// 桌面歌词。必须等 Loaded 之后再开：歌词窗要用 Owner，
		// 而 Owner 的窗口这时还没显示，拿不到最终位置，窗口会闪一下再跳。
		// 这里必须自己 try/catch：Loaded 是在渲染回调里触发的，
		// 里面抛的异常 WPF 只会吞掉，界面上什么都看不出来，日志里也一行都没有。
		Loaded += (_, _) =>
		{
			try
			{
				LyricRuntime.VisibilityChanged += SyncLyricButton;
				SyncLyricButton();
				if (LyricRuntime.Enabled)
				{
					LyricRuntime.Show();
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "开启桌面歌词失败");
			}
		};
	}

	private void OnSourceInitialized(object? sender, EventArgs e)
	{
		try
		{
			IntPtr hwnd = new WindowInteropHelper(this).Handle;
			_hwndSource = HwndSource.FromHwnd(hwnd);
			_hwndSource?.AddHook(WndProcHook);
			HotkeyManager.Bind(hwnd);
			HotkeyManager.Triggered += OnHotkey;
			LogHelper.Log("窗口句柄已就绪，全局快捷键已绑定");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "窗口句柄/快捷键初始化失败");
		}
	}

	private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == HotkeyManager.WM_HOTKEY)
		{
			HotkeyManager.HandleHotkey(wParam, lParam);
			handled = true;
		}
		return IntPtr.Zero;
	}

	private void OnHotkey(HotkeyAction action)
	{
		try
		{
			switch (action)
			{
				case HotkeyAction.Next:
					MusicService.PlayNext();
					break;
				case HotkeyAction.Prev:
					MusicService.PlayPrevious();
					break;
				case HotkeyAction.PlayPause:
					TogglePlayPause();
					break;
				case HotkeyAction.Favorite:
					ToggleFavCurrent();
					break;
				case HotkeyAction.ToggleWindow:
					ToggleWindowVisibility();
					break;
				case HotkeyAction.ToggleClickThrough:
					// 必须能在歌词窗自己点不中时还够得着，所以这个走全局键。
					LyricRuntime.ToggleClickThrough();
					break;
			}
			LogHelper.LogDebug("全局快捷键触发: " + action);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "全局快捷键执行失败: " + action);
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			LogHelper.Log("程序启动，主窗口加载");
			LoadTitleIcon();
			BuildNavItems();
			MusicService.Initialize();
			MusicService.ApplySettings();
			ApplyBackground();
			ApplyAccentTheme();
			StartGagTitle();
			PlayStartupAnimation();
			UpdateMiniVolumeText();
			UpdateMiniFav();
			UpdateMiniFx();
			UpdateMiniOrder();
			// 启动时先把当前曲目同步给行列表，这样重开程序后正在播的那首
			// 在音乐库/最近/收藏里立刻就有强调条
			TrackRows.SetPlayingTrack(MusicService.CurrentPath);
			UpdateProgressUi();

			_tick.Tick += (_, _) => UpdateProgressUi();
			_tick.Start();

			_seekThrottleTimer.Tick += OnSeekThrottleTick;
			_seekSettleTimer.Tick += OnSeekSettledTick;

			UpdateVolumeSlider();

			TrayIconService.Create();

			NavigateTo("library");

			if (SettingsManager.Instance.StartMinimizedToTray)
			{
				Dispatcher.BeginInvoke(new Action(HideToTray), DispatcherPriority.ApplicationIdle);
			}
			LogHelper.Log("主窗口初始化完成");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "主窗口加载失败");
		}
	}

	private void StartGagTitle()
	{
		try
		{
			SetTitleText("Baka Music");
			_gagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
			_gagTimer.Tick += (_, _) =>
			{
				_gagTimer.Stop();
				SetTitleText(Loc.Default["about_title"]);
			};
			_gagTimer.Start();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "设置启动标题失败");
		}
	}

	private void SetTitleText(string text)
	{
		AppTitleTextBlock.Text = text;
		Title = text;
	}

	/// <summary>启动淡入。原来还叠了一个 1.03->1.0 的缩放，用户要的是「给个淡化」，
	/// 缩放会让整窗内容在淡入时一起往前顶一下，观感发飘，这里只留透明度。</summary>
	private void PlayStartupAnimation()
	{
		if (!SettingsManager.Instance.ShowStartupAnimation)
		{
			return;
		}
		try
		{
			// 原来这里还要求 WindowState 必须是 Normal，结果「从托盘/最小化恢复」时
			// 状态还是 Minimized，函数直接 return，从托盘点开就是硬闪一下、没有动画。
			// 现在自己先把状态归位，再播。
			if (WindowState == WindowState.Minimized)
			{
				WindowState = WindowState.Normal;
			}
			if (Visibility != Visibility.Visible)
			{
				return;
			}
			Opacity = 0.0;
			var sb = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(40) };
			var o = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			sb.Children.Add(o);
			Storyboard.SetTarget(o, this);
			Storyboard.SetTargetProperty(o, new PropertyPath(Window.OpacityProperty));
			sb.Completed += (_, _) => Opacity = 1.0;
			sb.Begin();
		}
		catch (Exception ex)
		{
			Opacity = 1.0;
			LogHelper.LogError(ex, "播放启动动画失败");
		}
	}

	/// <summary>淡出后执行动作。收进托盘也走这里，
	/// 以前 HideToTray 是一句 Hide() 直接消失，所以点 X 看着就是「啪」一下没了。</summary>
	private void FadeOutThen(Action done)
	{
		try
		{
			if (Opacity <= 0.01)
			{
				done();
				return;
			}
			var o = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(150))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
			};
			o.Completed += (_, _) =>
			{
				Opacity = 0.0;
				try
				{
					done();
				}
				finally
				{
					// 恢复时从 0 开始淡入；关掉动画开关时直接给回 1
					if (SettingsManager.Instance.ShowStartupAnimation)
					{
						PlayStartupAnimation();
					}
					else
					{
						Opacity = 1.0;
					}
				}
			};
			BeginAnimation(Window.OpacityProperty, o);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放淡出动画失败");
			done();
		}
	}

	private void FadeOutAndClose()
	{
		try
		{
			if (_fadingOut)
			{
				return;
			}
			_fadingOut = true;
			var o = new DoubleAnimation(Opacity, 0.0, TimeSpan.FromMilliseconds(200))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
			};
			o.Completed += (_, _) =>
			{
				_exitFaded = true;
				Close();
			};
			BeginAnimation(Window.OpacityProperty, o);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放关闭动画失败");
		}
	}

	private void LoadTitleIcon()
	{
		try
		{
			string exeDir = AppContext.BaseDirectory;
			string png = Path.Combine(exeDir, "Assets", "app_icon.png");
			if (File.Exists(png))
			{
				var bi = new BitmapImage();
				bi.BeginInit();
				bi.UriSource = new Uri(png);
				// 只给一个解码尺寸。两个都设等于告诉 WPF「缩成 44x44」，
				// 非方形的图会被硬挤变形（标题栏这里看着就是图被左右压扁了）。
				bi.DecodePixelWidth = 44;
				bi.CacheOption = BitmapCacheOption.OnLoad;
				bi.EndInit();
				bi.Freeze();
				AppTitleIcon.Source = bi;
				return;
			}
			string ico = Path.Combine(exeDir, "Assets", "app.ico");
			if (File.Exists(ico))
			{
				var bi = new BitmapImage();
				bi.BeginInit();
				bi.UriSource = new Uri(ico);
				bi.CacheOption = BitmapCacheOption.OnLoad;
				bi.EndInit();
				bi.Freeze();
				AppTitleIcon.Source = bi;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "加载标题栏图标失败");
		}
	}

	/// <summary>导航项顺序。「我的收藏」和「最近播放」按用户要求换了位置，
	/// 「设置」不放在这一串里，而是钉在左下角的 NavBottomList。</summary>
	private void BuildNavItems()
	{
		try
		{
			NavList.Items.Clear();
			NavBottomList.Items.Clear();
			AddNavItem("library", Loc.Default["nav_library"], "\uE8B7");
			AddNavItem("fav", Loc.Default["nav_fav"], "\uEB51");
			AddNavItem("recent", Loc.Default["nav_recent"], "\uE81C");
			AddNavItem("online", Loc.Default["nav_online"], "\uE721");
			AddNavItem("fx", Loc.Default["nav_fx"], "\uE8A7");
			AddNavItem("about", Loc.Default["nav_about"], "\uE946");
			AddBottomNavItem("settings", Loc.Default["nav_settings"], "\uE713");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "构建导航栏失败");
		}
	}

	private void AddNavItem(string key, string text, string glyph)
	{
		NavList.Items.Add(BuildNavItem(key, text, glyph));
	}

	private void AddBottomNavItem(string key, string text, string glyph)
	{
		NavBottomList.Items.Add(BuildNavItem(key, text, glyph));
	}

	private ListBoxItem BuildNavItem(string key, string text, string glyph)
	{
		var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		var icon = new TextBlock
		{
			Text = glyph,
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 15,
			// 图标色跟随导航项的选中态：未选中用次要文字色，选中提亮。
			Foreground = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 10, 0)
		};
		panel.Children.Add(icon);
		var label = new TextBlock
		{
			Text = text,
			FontSize = 13.5,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		panel.Children.Add(label);
		return new ListBoxItem { Tag = key, Content = panel };
	}

	public void ApplyBackground()
	{
		try
		{
			var s = SettingsManager.Instance;
			BitmapSource? bgImage = LoadBackgroundImage(s.BackgroundPath);
			bool haveImage = bgImage != null;
			bool wantVideo = string.Equals(s.BackgroundType, "video", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(s.BackgroundPath) && File.Exists(s.BackgroundPath);
			double bgOpacity = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
			if (wantVideo)
			{
				// 视频背景交给 SyncBackgroundVideo 统一处理（单曲跟播 / 目录轮播），
				// 这里只负责把图片侧收干净，避免两条路径互相打架。
				StopBgVideo();
				BgImage.Visibility = Visibility.Collapsed;
				BgImage.Source = null;
				SyncBackgroundVideo();
			}
			else if (haveImage)
			{
				StopBgVideo();
				BgImage.Visibility = Visibility.Visible;
				BgImage.Source = bgImage;
				BgImage.BeginAnimation(UIElement.OpacityProperty, null);
				BgImage.Opacity = bgOpacity;
			}
			else
			{
				StopBgVideo();
				BgImage.Visibility = Visibility.Visible;
				BgImage.Source = null;
				BgImage.BeginAnimation(UIElement.OpacityProperty, null);
				BgImage.Opacity = 1.0;
			}

			// 卡片蒙版按 MhyunBox 的规则分两种（见 MhyunBox\MainWindow.cs:420-450）：
			//   视频背景 -> 卡片换成 75% 不透明近黑 Argb(192,14,14,14)，视频被刻意压暗当动态纹理
			//   图片/无背景 -> 卡片保持 WinUI 正常的「半透明白 5%」，图片要看得清
			// 浅色主题下文字是深色，这两步都得反过来：视频卡片压成半透明白，普通卡片用近白。
			bool videoActive = BgVideo.Visibility == Visibility.Visible;
			bool imageActive = haveImage && !videoActive;
			BgCardMask.Background = videoActive
				? ThemeManager.VideoCardMaskBrush()
				: ThemeManager.CardFill();

			// 「背景暗度」是 Music_Box 自己多出来的旋钮。视频背景下卡片蒙版已经在压暗了，
			// 再叠一层会把画面压到全黑，所以视频时不再重复压。
			// 浅色主题里这个旋钮改成把背景「洗白」，否则深色字压在一张深色照片上依旧读不出来。
			double darkness = videoActive ? 0.0 : Math.Clamp(s.BackgroundDarkness, 0.0, 0.95);
			byte wash = (byte)Math.Round(darkness * 255.0);
			BgDark.Background = ThemeManager.IsDark
				? new SolidColorBrush(Color.FromArgb(wash, 0x0F, 0x0F, 0x0F))
				: new SolidColorBrush(Color.FromArgb(wash, 0xFF, 0xFF, 0xFF));
			ApplyBgMotionState();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "应用背景失败");
		}
	}

	/// <summary>背景摆动上次用的参数。摆动动画一旦 Stop 再 Begin，X 会从 From(-swing) 重来，
	/// 表现出来就是背景图片「莫名其妙往右窜一下」。所以只在参数真的变了的时候重启动画，
	/// 切歌/启停（OnMusicStateChanged 也会调这里）保持当前相位继续播。</summary>
	private double _bgMotionSwing = double.NaN;
	private double _bgMotionScaleTo = double.NaN;
	private bool _bgMotionRunning;

	private void ApplyBgMotionState()
	{
		try
		{
			var s = SettingsManager.Instance;
			if (FindResource("BgMotion") is not Storyboard motion)
			{
				LogHelper.LogDebug("[BgMotion] 找不到 BgMotion Storyboard，跳过");
				return;
			}

			// 摆动幅度按 SwingAmplitude 等比缩放：以 50（MhyunBox 默认值）为基准，
			// 位移 ±26、缩放 1.16→1.22 都是 amplitude=50 时的效果。
			double k = Math.Clamp(s.SwingAmplitude, 0.0, 100.0) / 50.0;
			double swing = 26.0 * k;
			const double ScaleBase = 1.16;
			double scaleTo = ScaleBase + 0.06 * k;
			// 幅度为 0 时位移和缩放都是 0（1.16→1.16），等于不动，
			// 这时候没必要跑一个 Forever 动画空转 CPU。
			bool wantRunning = s.BackgroundSwing && swing > 0.001;

			// 这里必须显式判 NaN，不能只比差值：
			// Math.Abs(double.NaN - x) > eps 恒为 false，所以原来用 NaN 当「未初始化」
			// 的话首次调用永远算出 paramsChanged=false，幅度旋钮从此完全不生效，
			// _bgMotionSwing 也永远停在 NaN。
			bool paramsChanged = double.IsNaN(_bgMotionSwing)
				|| double.IsNaN(_bgMotionScaleTo)
				|| Math.Abs(_bgMotionSwing - swing) > 0.001
				|| Math.Abs(_bgMotionScaleTo - scaleTo) > 0.0001;
			bool stateChanged = _bgMotionRunning != wantRunning;

			LogHelper.LogDebug(
				$"[BgMotion] apply setting={s.BackgroundSwing} amp={s.SwingAmplitude} "
				+ $"→ swing=±{swing:0.##} scale {ScaleBase}→{scaleTo:0.####} "
				+ $"| 上次 running={_bgMotionRunning} paramsChanged={paramsChanged} stateChanged={stateChanged}");

			if (paramsChanged)
			{
				// 只有幅度变了才重设 From/To，并且必须先停：正在播的动画不会自动采用新的 From/To
				motion.Stop(this);
				foreach (var child in motion.Children)
				{
					if (child is not DoubleAnimation da)
					{
						continue;
					}
					string? prop = Storyboard.GetTargetProperty(da)?.Path;
					if (prop == "X")
					{
						da.From = -swing;
						da.To = swing;
					}
					else if (prop is "ScaleX" or "ScaleY")
					{
						da.From = ScaleBase;
						da.To = scaleTo;
					}
				}
				_bgMotionSwing = swing;
				_bgMotionScaleTo = scaleTo;
				// 幅度变了意味着刚才那个 Stop 让相位/位移都变了，回到中位重来，
				// 否则用户会看到背景先跳一下再开始摆。
				ResetBgTransforms(ScaleBase);
			}

			if (wantRunning)
			{
				// 幅度也变了就必须重启：上面那个 motion.Stop() 已经把动画掐了，
				// 光改 From/To 不会自己接着播，会永远停在原地。
				if (stateChanged || paramsChanged)
				{
					motion.Stop(this);
					motion.Begin(this);
					LogHelper.LogDebug("[BgMotion] Begin（开始摆动）");
				}
				_bgMotionRunning = true;
			}
			else
			{
				// 关掉摆动：无条件 Stop + 归位。
				// 之前只在 stateChanged 时才做，一旦 _bgMotionRunning 因为
				// 幅度变更那条路径失步成 true，就会跳过这里，动画照旧在跑
				// —— 表现就是「我明明关掉了，背景还在动」。
				motion.Stop(this);
				ResetBgTransforms(ScaleBase);
				_bgMotionRunning = false;
				LogHelper.LogDebug("[BgMotion] Stop（摆动已关，回到中位）");
			}

			LogHelper.LogDebug(
				$"[BgMotion] 状态 running={_bgMotionRunning} "
				+ $"X={BgTranslate?.X:0.##} scaleX={BgScale?.ScaleX:0.####}");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "应用背景摆动状态失败");
		}
	}

	/// <summary>把背景的位移/缩放动画彻底摘掉并摆回基准值。
	/// 单纯 motion.Stop() 只会冻结在当前帧，图片会歪在一边，所以还要
	/// BeginAnimation(prop, null) 把动画摘干净，再 SetCurrentValue 归位。</summary>
	private void ResetBgTransforms(double scaleBase)
	{
		// 注意：这里必须直接用 XAML 生成的字段，不能走 FindName。
		// BgTranslate / BgScale 是 Image.RenderTransform 里的 Transform 对象，
		// 不在 FrameworkElement 的名字作用域里，FindName("BgTranslate") 恒为 null，
		// 之前就是因此「归位」这步静默失败，图片停在偏移位置停不下来。
		ApplyTransform(BgTranslate, TranslateTransform.XProperty, 0.0);
		ApplyTransform(BgScale, ScaleTransform.ScaleXProperty, scaleBase);
		ApplyTransform(BgScale, ScaleTransform.ScaleYProperty, scaleBase);
	}

	private static void ApplyTransform(DependencyObject? target, DependencyProperty prop, double value)
	{
		if (target is not Animatable animatable)
		{
			return;
		}
		animatable.BeginAnimation(prop, null);
		animatable.SetCurrentValue(prop, value);
	}

	private FrameworkElement? FindElement(string name) =>
		FindName(name) as FrameworkElement;

	private System.Windows.Media.Animation.DoubleAnimation? _fetchSpin;

	/// <summary>取直链/下载期间显示「正在取流」+ 转圈。
	/// 原来这段时间标题完全不变，看着像卡死，用户就反复点同一首 ——
	/// 那正好又触发上面那个「过期请求抢占正在播放」的 bug。</summary>
	private void OnFetchStateChanged(bool fetching, string? title)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(() => OnFetchStateChanged(fetching, title)));
			return;
		}
		if (FetchIndicator == null)
		{
			return;
		}
		if (!fetching)
		{
			FetchIndicator.Visibility = Visibility.Collapsed;
			_fetchSpin = null;
			return;
		}
		FetchText.Text = Loc.Default["mt_fetching"] + (string.IsNullOrEmpty(title) ? "" : "  " + title);
		if (FetchIndicator.Visibility != Visibility.Visible)
		{
			FetchIndicator.Visibility = Visibility.Visible;
		}
		try
		{
			_fetchSpin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
			{
				RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
			};
			FetchSpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, _fetchSpin);
		}
		catch
		{
		}
	}

	private void OnMusicStateChanged()
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(OnMusicStateChanged));
			return;
		}
		// 切一首歌 MusicService 会连着抛好几次 StateChanged（起播、缓冲、状态位翻转）。
		// 每次都把下面 5 件事重做一遍纯属浪费，而且全在 UI 线程上 ——
		// 一首歌播放过程中会累积出可观的卡顿。
		// 合并成一次，并且用后台优先级：让渲染和输入先过，积压的一串通知只跑最后一遍。
		if (_stateCoalescePending)
		{
			return;
		}
		_stateCoalescePending = true;
		Dispatcher.BeginInvoke(new Action(() =>
		{
			_stateCoalescePending = false;
			HandleMusicStateChanged();
		}), DispatcherPriority.Background);
	}

	private bool _stateCoalescePending;

	private void HandleMusicStateChanged()
	{
		if (!Dispatcher.CheckAccess())
		{
			return;
		}
		try
		{
			UiPerf.Measure("播放状态处理", () =>
			{
				ApplyBgMotionState();
				SyncBackgroundVideo();
				SyncSmtcTrack();
				TrackRows.SetPlayingTrack(MusicService.CurrentPath);
				UpdateMiniAll();
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "处理播放状态变化失败");
		}
	}

	/// <summary>换曲/启停时把曲目信息推给系统媒体控制（只推元数据，不逐帧推）。</summary>
	private void SyncSmtcTrack()
	{
		try
		{
			if (!SmtcService.IsAttached)
			{
				return;
			}
			string? path = MusicService.CurrentPath;
			if (string.IsNullOrWhiteSpace(path) || !MusicService.IsPlaying && MusicService.Position <= TimeSpan.Zero)
			{
				SmtcService.ClearTrack();
				return;
			}
			SavedTrack? track = MusicService.BuildCurrentTrack();
			if (track == null)
			{
				SmtcService.ClearTrack();
				TrayIconService.SetNowPlaying(null);
				return;
			}
			string? cover = track.Cover;
			// 网络封面是 URL，SMTC 只吃本地文件
			if (!string.IsNullOrWhiteSpace(cover)
				&& cover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				cover = null;
			}
			SmtcService.SetTrack(path, track.Title, track.Artist, null, cover,
				MusicService.Duration);
			// 托盘气泡跟着换曲更新：上行曲名、下行程序名
			TrayIconService.SetNowPlaying(track.Title);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步 SMTC 曲目信息失败");
		}
	}

	/// <summary>进度刷新时把播放状态和时间轴推给系统媒体控制。</summary>
	private void SyncSmtcProgress()
	{
		try
		{
			if (!SmtcService.IsAttached)
			{
				return;
			}
			SmtcService.SyncPlayback(MusicService.IsPlaying,
				MusicService.Position, MusicService.Duration);
		}
		catch
		{
		}
	}

	private static BitmapSource? LoadBackgroundImage(string? path)
	{
		try
		{
			string? file = null;
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				file = path;
			}
			else
			{
				string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds");
				if (Directory.Exists(dir))
				{
					foreach (string f in Directory.EnumerateFiles(dir, "*.*"))
					{
						string ext = Path.GetExtension(f).ToLowerInvariant();
						if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp")
						{
							file = f;
							break;
						}
					}
				}
			}
			if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
			{
				return null;
			}
			var bi = new BitmapImage();
			bi.BeginInit();
			bi.UriSource = new Uri(file);
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.EndInit();
			bi.Freeze();
			return bi;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>背景视频轮播列表。留空时用程序目录下 Assets\Backgrounds，和 MhyunBox 一致。</summary>
	private static readonly List<string> BgVideoPlaylist = new List<string>();

	private static int _bgIndex = -1;
	private static readonly Random BgRandom = new Random();
	private static string? _appliedBgVideoPath;

	/// <summary>后台视频扩展名，与 MhyunBox 的 VideoExtensions 保持一致。</summary>
	private static readonly string[] BgVideoExtensions =
		{ ".mp4", ".mkv", ".mov", ".wmv", ".webm" };

	private static bool IsBgVideoPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		string ext = Path.GetExtension(path).ToLowerInvariant();
		foreach (string allowed in BgVideoExtensions)
		{
			if (ext == allowed)
			{
				return true;
			}
		}
		return false;
	}

	private static void BuildBgVideoPlaylist()
	{
		BgVideoPlaylist.Clear();
		var s = SettingsManager.Instance;
		string dir = string.IsNullOrWhiteSpace(s.VideoFolder)
			? Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds")
			: s.VideoFolder;
		if (Directory.Exists(dir))
		{
			try
			{
				foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
				{
					if (IsBgVideoPath(file))
					{
						BgVideoPlaylist.Add(Path.GetFullPath(file));
					}
				}
			}
			catch
			{
			}
		}
		// 单独选中的那个视频也要进列表，否则它播完就没下一段了
		if (IsBgVideoPath(s.BackgroundPath))
		{
			bool already = false;
			foreach (string p in BgVideoPlaylist)
			{
				if (string.Equals(p, s.BackgroundPath, StringComparison.OrdinalIgnoreCase))
				{
					already = true;
					break;
				}
			}
			if (!already)
			{
				BgVideoPlaylist.Add(s.BackgroundPath!);
			}
		}
		BgVideoPlaylist.Sort(StringComparer.OrdinalIgnoreCase);
	}

	private static string? PickInitialBgVideo()
	{
		if (BgVideoPlaylist.Count == 0)
		{
			return null;
		}
		string preferred = SettingsManager.Instance.BackgroundPath ?? "";
		int at = BgVideoPlaylist.FindIndex(p =>
			string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase));
		if (at < 0)
		{
			at = string.Equals(SettingsManager.Instance.PlaybackMode, "random", StringComparison.OrdinalIgnoreCase)
				? BgRandom.Next(BgVideoPlaylist.Count)
				: 0;
		}
		return BgVideoPlaylist[at];
	}

	private static string? PickNextBgVideo()
	{
		if (BgVideoPlaylist.Count == 0)
		{
			return null;
		}
		if (string.Equals(SettingsManager.Instance.PlaybackMode, "random", StringComparison.OrdinalIgnoreCase))
		{
			_bgIndex = BgRandom.Next(BgVideoPlaylist.Count);
		}
		else
		{
			_bgIndex = (_bgIndex + 1) % BgVideoPlaylist.Count;
		}
		return BgVideoPlaylist[Math.Max(0, _bgIndex)];
	}

	/// <summary>
	/// 视频跟播：当前曲目本身是视频就直接拿它当背景；否则按播放模式轮播背景视频目录。
	/// 音频始终由 NAudio 走，这里只负责画面，所以 BgVideo 静音。
	/// </summary>
	private void SyncBackgroundVideo()
	{
		try
		{
			var s = SettingsManager.Instance;
			if (!string.Equals(s.BackgroundType, "video", StringComparison.OrdinalIgnoreCase))
			{
				_appliedBgVideoPath = null;
				return;
			}

			string? current = MusicService.CurrentPath;
			if (IsBgVideoPath(current) && File.Exists(current!))
			{
				// 当前就是视频：直接把它当背景
				PlayBgVideo(current!, loopPlaylist: false);
				return;
			}

			// 普通音频：走轮播列表
			if (_appliedBgVideoPath == null || BgVideoPlaylist.Count == 0)
			{
				BuildBgVideoPlaylist();
			}
			if (BgVideoPlaylist.Count == 0)
			{
				StopBgVideo();
				return;
			}
			string? next = PickInitialBgVideo();
			if (next == null)
			{
				return;
			}
			// 已经在播列表里的某一段就别重头再播
			if (string.Equals(_appliedBgVideoPath, next, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			PlayBgVideo(next, loopPlaylist: true);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步背景视频失败");
		}
	}

	private void PlayBgVideo(string path, bool loopPlaylist)
	{
		try
		{
			_bgLoopPlaylist = loopPlaylist;
			if (!string.Equals(_appliedBgVideoPath, path, StringComparison.OrdinalIgnoreCase))
			{
				_appliedBgVideoPath = path;
				_bgIndex = BgVideoPlaylist.FindIndex(p =>
					string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
				BgImage.Visibility = Visibility.Collapsed;
				BgImage.Source = null;
				BgVideo.Visibility = Visibility.Visible;
				BgVideo.Source = new Uri(path);
				// 沿用设置里的「视频背景音量」，不擅自静音：原来这个滑块就是给背景视频用的
				BgVideo.Volume = (float)Math.Clamp(SettingsManager.Instance.BgVideoVolume, 0.0, 1.0);
			}
			// 有底图时卡片要当蒙版用：MhyunBox 是 Color.FromArgb(192,14,14,14)（深色主题），
			// 这样即使底图是纯白，卡片里的白字依然读得出来。
			BgCardMask.Background = new SolidColorBrush(Color.FromArgb(192, 0x0E, 0x0E, 0x0E));
			double bgOpacity = Math.Clamp(SettingsManager.Instance.BackgroundOpacity, 0.0, 1.0);
			BgVideo.BeginAnimation(UIElement.OpacityProperty, null);
			BgVideo.Opacity = 0.0;
			BgVideo.BeginAnimation(UIElement.OpacityProperty,
				new DoubleAnimation(bgOpacity, TimeSpan.FromMilliseconds(450)) { From = 0.0 });
			BgVideo.Play();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放背景视频失败");
		}
	}

	private void StopBgVideo()
	{
		try
		{
			_bgLoopPlaylist = false;
			_appliedBgVideoPath = null;
			BgVideo.BeginAnimation(UIElement.OpacityProperty, null);
			BgVideo.Stop();
			BgVideo.Source = null;
			BgVideo.Visibility = Visibility.Collapsed;
		}
		catch
		{
		}
	}

	private bool _bgLoopPlaylist;

	private void BgVideo_MediaEnded(object sender, RoutedEventArgs e)
	{
		try
		{
			if (_bgLoopPlaylist)
			{
				string? next = PickNextBgVideo();
				if (next != null)
				{
					PlayBgVideo(next, loopPlaylist: true);
					return;
				}
			}
			// 单段视频：原地循环
			BgVideo.Position = TimeSpan.Zero;
			BgVideo.Play();
		}
		catch
		{
		}
	}

	/// <summary>这个页面是不是当前正显示的那一页。
	/// 各内容页靠它判断「要不要现在就重画」：不在前台的页面等切回来时再画，
	/// 免得切歌/刷新时把看不见的列表也重造一遍。
	/// 比 IsVisible 可靠 —— 窗口最小化时 IsVisible 是 false，但那一页就是当前页。</summary>
	public bool IsCurrentPage(FrameworkElement page)
	{
		return ReferenceEquals(ContentHost?.Content, page);
	}

	private void NavigateTo(string key)
	{
		if (_navigating || key == _currentKey)
		{
			return;
		}
		_navigating = true;
		try
		{
			if (!_pages.TryGetValue(key, out UserControl? page))
			{
				// 第一次进这一页要现场 new 整个页面（XAML 解析 + 控件树），
				// 明显比复用已有页面慢，单独量一下好知道首次进入的代价有多大。
				page = UiPerf.Measure("首次建页 " + key, () => CreatePage(key));
				if (page == null)
				{
					return;
				}
				_pages[key] = page;
			}
			_currentKey = key;
			// 先通知上一页要走了，再通知新页进来。顺序不能反：
			// 音乐库靠 OnNavigatedFrom 判断要不要清掉筛选关键词，
			// 得在它被换出内容区之前问。
			if (!string.IsNullOrEmpty(_previousKey)
				&& _pages.TryGetValue(_previousKey!, out UserControl? prev) && prev is IMusicPageLeave leave)
			{
				try
				{
					leave.OnNavigatedFrom();
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "页面离开回调失败: " + _previousKey);
				}
			}
			_previousKey = key;
			if (page is IMusicPage mp)
			{
				UiPerf.Measure("进页刷新 " + key, mp.OnNavigatedTo);
			}
			SwapPageContent(page);
			SyncNavSelection(key);
			LogHelper.LogDebug("导航到页面: " + key);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "页面导航失败: " + key);
		}
		finally
		{
			_navigating = false;
		}
	}

	/// <summary>换页带一层淡入（不做位移，观感更稳）。先淡出旧页内容再换，再把新页淡入。</summary>
	private void SwapPageContent(UserControl page)
	{
		// 连续快速点导航栏时，130ms 的淡入淡出会排成一串动画：用户点 5 下，
		// 画面还在按第 1、2、3 次的节奏淡，看起来就像「卡住不动」。
		// 于是这里记一下上一次换页的时间：离得近就直接换、不播动画，
		// 让界面立刻跟到最后一次点击，比老老实实排队快得多。
		bool rapid = _lastSwapUtc.HasValue
			&& (DateTime.UtcNow - _lastSwapUtc.Value).TotalMilliseconds < 250;
		_lastSwapUtc = DateTime.UtcNow;

		if (rapid)
		{
			SwapPageContentNow(page);
			return;
		}

		Duration fade = TimeSpan.FromMilliseconds(130);
		if (ContentHost.Content is UIElement oldEl)
		{
			oldEl.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.0, fade));
		}
		ContentHost.Content = page;
		if (page.Opacity < 1.0)
		{
			page.Opacity = 0.0;
		}
		page.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, fade)
		{
			FillBehavior = FillBehavior.Stop
		});
		page.Opacity = 1.0;
	}

	/// <summary>不做淡入的换页。必须先把可能还挂着的动画摘掉，
	/// 否则上一轮淡出用的 HoldEnd 动画会把新页钉在 Opacity=0。</summary>
	private void SwapPageContentNow(UserControl page)
	{
		if (ContentHost.Content is UIElement oldEl)
		{
			oldEl.BeginAnimation(OpacityProperty, null);
		}
		page.BeginAnimation(OpacityProperty, null);
		page.Opacity = 1.0;
		ContentHost.Content = page;
	}

	private DateTime? _lastSwapUtc;

	private void SyncNavSelection(string key)
	{
		_suspendSelection = true;
		foreach (ListBox list in new[] { NavList, NavBottomList })
		{
			ListBoxItem? match = null;
			foreach (object item in list.Items)
			{
				if (item is ListBoxItem li && string.Equals(li.Tag as string, key, StringComparison.Ordinal))
				{
					match = li;
					break;
				}
			}
			list.SelectedItem = match;
		}
		_suspendSelection = false;
	}

	private static UserControl? CreatePage(string key)
	{
		return key switch
		{
			"library" => new MusicLibraryPage(),
			"recent" => new RecentPage(),
			"fav" => new FavoritesPage(),
			"online" => new OnlineMusicPage(),
			"fx" => new FxPresetPage(),
			"settings" => new SettingsPage(),
			"about" => new AboutPage(),
			_ => null
		};
	}

	private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_suspendSelection || _navigating)
		{
			return;
		}
		// NavList 和左下角的 NavBottomList 共用这个处理器，
		// 所以先把另一个列表的选中清掉，避免两处同时高亮。
		if (sender is ListBox box)
		{
			ListBox other = ReferenceEquals(box, NavList) ? NavBottomList : NavList;
			if (other.SelectedItem != null)
			{
				_suspendSelection = true;
				other.SelectedItem = null;
				_suspendSelection = false;
			}
		}
		// 两个列表共用处理器，key 从当前真正被选中的那个里取
		string? key = (NavList.SelectedItem as ListBoxItem)?.Tag as string
			?? (NavBottomList.SelectedItem as ListBoxItem)?.Tag as string;
		if (!string.IsNullOrEmpty(key))
		{
			NavigateTo(key);
		}
	}

	private void Content_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		try
		{
			if (ContentHost.Content is not DependencyObject root)
			{
				return;
			}
			ScrollViewer? sv = FindScrollViewerUnderCursor(root, e.GetPosition(this));
			// 脚下没有可滚的就什么都不做，把滚轮交回 WPF 默认处理。
			//
			// 以前这里是「脚下没有就退而取剩余高度最大的那个」，然后照样
			// e.Handled = true。这是最坏的一种错：它会去滚另一个列表
			// （搜索页同时有结果区和评论区），并且把事件吃掉，
			// 于是鼠标明明压在结果列表上、滚轮却完全不动。
			// 命中测试失败时老老实实不处理，最多是没有平滑动画，
			// 不会出现「滚轮失灵」。
			if (sv == null || sv.ScrollableHeight <= 0.0)
			{
				return;
			}
			// 列表类是按「项」滚动的（CanContentScroll=True）。
			// 这时再去改 VerticalOffset 会和项滚动打架，表现为
			// 「滚一下直接跳到顶/跳到底」。这种就交回 WPF 自己处理。
			if (sv.CanContentScroll)
			{
				return;
			}
			e.Handled = true;
			double delta = e.Delta > 0 ? -_mouseWheelStep : _mouseWheelStep;
			SmoothScroll.Animate(sv, delta);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "内容区滚轮处理异常");
		}
	}

	/// <summary>找出鼠标真正压着的那个 ScrollViewer。找不到就返回 null。
	///
	/// 只认命中测试的结果，不做「挑一个最大的当备选」。理由见
	/// Content_PreviewMouseWheel 里的说明：宁可不做平滑，也不能滚错列表
	/// 再把事件吃掉，那样表现出来就是滚轮完全失灵。</summary>
	private ScrollViewer? FindScrollViewerUnderCursor(DependencyObject root, Point mousePos)
	{
		ScrollViewer? under = null;
		double underArea = double.MaxValue;
		WalkScrollViewers(root, sv =>
		{
			if (sv.ScrollableHeight <= 0.0)
			{
				return;
			}
			// RenderSize 为 0 说明这一帧还没量过布局（页面刚换、或者
			// ScrollViewer 是 Collapsed）。这时候算出来的矩形是空的，
			// 命中测试必然失败 —— 所以必须先挡掉，不能拿空矩形去 Contains。
			Size size = sv.RenderSize;
			if (size.Width <= 0 || size.Height <= 0)
			{
				return;
			}
			// 命中测试：鼠标点落在哪个 ScrollViewer 的矩形里
			Point origin;
			try
			{
				origin = sv.TransformToAncestor(this).Transform(new Point(0, 0));
			}
			catch (InvalidOperationException)
			{
				// 元素还没接上视觉树，或不在同一棵树里（Popup/模板内部）
				return;
			}
			Rect r = new Rect(origin, size);
			if (!r.Contains(mousePos))
			{
				return;
			}
			double area = r.Width * r.Height;
			if (under == null || area <= underArea)
			{
				under = sv;
				underArea = area;
			}
		});
		return under;
	}

	private static void WalkScrollViewers(DependencyObject root, Action<ScrollViewer> onFound)
	{
		if (root is ScrollViewer sv)
		{
			onFound(sv);
		}
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			if (VisualTreeHelper.GetChild(root, i) is DependencyObject child)
			{
				WalkScrollViewers(child, onFound);
			}
		}
	}

	private void UpdateMiniAll()
	{
		UpdateMiniTrack();
		UpdatePlayGlyph();
		UpdateProgressUi();
		UpdateMiniFav();
		UpdateMiniFx();
		UpdateMiniOrder();
	}

	private void UpdateMiniTrack()
	{
		// 没在播的时候留空，不显示「不播放任何音乐」这种占位提示
		MiniTrackText.Text = MusicService.CurrentDisplayTitle ?? "";
	}

	private void UpdatePlayGlyph()
	{
		bool playing = MusicService.IsPlaying && !MusicService.NoMusicMode;
		MiniPlayGlyph.Text = playing ? "\uE769" : "\uE768";
		MiniPlayPauseButton.ToolTip = Loc.Default[playing ? "mt_pause" : "mt_play"];
	}

	private void UpdateProgressUi()
	{
		TimeSpan pos = MusicService.Position;
		TimeSpan dur = MusicService.Duration;
		MiniTimeText.Text = FormatTime(pos) + " / " + FormatTime(dur);
		// _seekPending 期间绝对不回写滑块：Seek 内部的 NotifyStateChanged 会重入到这里，
		// 那时 reader 报的还是旧位置，回写就会把用户刚点/刚拖到的位置原样弹回去。
		if (!_progressDragging && !_seekPending && dur > TimeSpan.Zero)
		{
			double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
			MiniProgressSlider.Value = ratio;
		}
		SyncSmtcProgress();
	}

	private static string FormatTime(TimeSpan t)
	{
		try
		{
			return t.TotalHours >= 1.0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
		}
		catch
		{
			return "00:00";
		}
	}

	private void UpdateMiniFav()
	{
		try
		{
			SavedTrack? track = MusicService.BuildCurrentTrack();
			bool fav = track != null && UserLibrary.IsFavorite(track);
			MiniFavGlyph.Text = fav ? "\uEB52" : "\uEB51";
			MiniFavGlyph.Foreground = fav
				? new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))
				: ThemeManager.Secondary();
			MiniFavButton.ToolTip = Loc.Default[fav ? "act_unfav" : "act_fav"];
		}
		catch
		{
		}
	}

	private void UpdateMiniFx()
	{
		try
		{
			bool active = FxActive();
			MiniFxGlyph.Foreground = active
				? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4D))
				: ThemeManager.Secondary();
		}
		catch
		{
		}
	}

	/// <summary>四种模式循环：随机 -> 顺序 -> 列表循环 -> 单曲循环 -> 随机。
	/// 之前只有三种，缺了「顺序播放（播完就停）」，所以用户点不到那个状态。</summary>
	private static readonly string[] ModeCycle = { "random", "once", "sequential", "loop" };

	private void UpdateMiniOrder()
	{
		try
		{
			string mode = MusicService.NormalizePlaybackMode();
			MiniOrderGlyph.Text = mode switch
			{
				"once" => "\uE8F1",
				"sequential" => "\uE8EE",
				"loop" => "\uE8ED",
				_ => "\uE8B1"
			};
			MiniOrderGlyph.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
			MiniOrderButton.ToolTip = Loc.Default["mt_mode"] + "：" +
				Loc.Default["mode_" + mode]
				+ "（点击切换）";
		}
		catch
		{
		}
	}

	private void MiniOrderButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			var s = SettingsManager.Instance;
			int idx = Array.IndexOf(ModeCycle, MusicService.NormalizePlaybackMode());
			string next = ModeCycle[(idx + 1 + ModeCycle.Length) % ModeCycle.Length];
			s.PlaybackMode = next;
			SettingsManager.Save(s);
			UpdateMiniOrder();
			ShowToast(Loc.Default["mode_" + next]);
			LogHelper.Log("切换播放模式: " + next);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "切换播放顺序失败");
		}
	}

	private static bool FxActive()
	{
		var s = SettingsManager.Instance;
		FxSettings fx = s.Fx ?? new FxSettings();
		return fx.EqEnabled || fx.BassBoostEnabled || fx.ReverbEnabled || fx.ExciterEnabled
			|| fx.CompressorEnabled || fx.WidthEnabled || fx.SurroundEnabled || s.SurroundEnable;
	}

	private void UpdateMiniVolumeText()
	{
		int pct = (int)Math.Round(Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0) * 100.0);
		// 弹层底部那条横向进度条宽度跟着音量走，替代原来的百分比文字
		try
		{
			VolumeFillBar.Width = 150.0 * Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0);
		}
		catch
		{
		}
		MiniVolumeGlyph.Text = pct <= 0 ? "\uE74F" : "\uE995";
	}

	private void UpdateVolumeSlider()
	{
		VolumePopupSlider.Value = Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0) * 100.0;
	}

	private void TogglePlayPause()
	{
		try
		{
			if (MusicService.NoMusicMode)
			{
				MusicService.Resume();
				return;
			}
			if (MusicService.IsPlaying)
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
			LogHelper.LogError(ex, "播放/暂停失败");
		}
	}

	private void ToggleFavCurrent()
	{
		try
		{
			SavedTrack? track = MusicService.BuildCurrentTrack();
			if (track == null)
			{
				// 没有当前曲目，收藏键静默忽略即可，不用弹「不播放任何音乐」打扰
				return;
			}
			bool fav = UserLibrary.IsFavorite(track);
			if (fav)
			{
				UserLibrary.RemoveFavorite(track.Key);
			}
			else
			{
				UserLibrary.ToggleFavorite(track);
			}
			UpdateMiniFav();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "收藏当前歌曲失败");
		}
	}

	private void MiniMinButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			VolumePopup.IsOpen = false;
			FxPopup.IsOpen = false;
			if (SettingsManager.Instance.StartMinimizedToTray)
			{
				HideToTray();
			}
			else
			{
				WindowState = WindowState.Minimized;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "最小化失败");
		}
	}

	private void MaxRestoreButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "最大化/还原失败");
		}
	}

	private void OnWindowStateChanged(object? sender, EventArgs e)
	{
		bool max = WindowState == WindowState.Maximized;
		MaxRestoreGlyph.Text = max ? "\uE923" : "\uE922";
		MaxRestoreButton.ToolTip = max ? "Restore" : "Maximize";
	}

	/// <summary>标题栏的 X。默认收进托盘，不再直接杀进程 —— 之前这里无条件
	/// _closingConfirmed = true 会跳过 Window_Closing 里的收托盘分支，程序说关就没了。
	/// 真要退出走托盘右键菜单的 ExitFromTray。</summary>
	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		if (s.CloseBehavior == "exit")
		{
			_closingConfirmed = true;
			Close();
			return;
		}
		// "tray"、以及还没设置过的默认值（ask / 空）都先收进托盘
		HideToTray();
	}

	private void Window_Closing(object sender, CancelEventArgs e)
	{
		if (_closingConfirmed)
		{
			if (!_exitFaded && !_fadingOut)
			{
				e.Cancel = true;
				FadeOutAndClose();
				return;
			}
			TrayIconService.Dispose();
			SmtcService.Dispose();
			// 歌词窗和它的跟随服务要在主窗真的要走的时候收掉。
			// 收进托盘那种情况不能动：托盘状态下窗口还开着，歌词得继续跟。
			LyricRuntime.Shutdown();
			try
			{
				HotkeyManager.UnregisterAll();
			}
			catch
			{
			}
			return;
		}
		e.Cancel = true;
		if (SettingsManager.Instance.CloseBehavior == "tray" || SettingsManager.Instance.StartMinimizedToTray)
		{
			HideToTray();
		}
		else
		{
			_closingConfirmed = true;
			FadeOutAndClose();
		}
	}

	private void ExitFromTray()
	{
		try
		{
			_closingConfirmed = true;
			Dispatcher.BeginInvoke(new Action(Close));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "托盘退出失败");
		}
	}

	private void HideToTray()
	{
		try
		{
			// 收进托盘也淡出，点 X 才不是「啪」一下直接消失
			FadeOutThen(() =>
			{
				Hide();
				TrayIconService.ShowBalloon(Loc.Default["tray_start_min"]);
				LogHelper.LogDebug("已最小化到系统托盘");
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "隐藏到托盘失败");
		}
	}

	private void ShowFromTray()
	{
		try
		{
			Show();
			if (WindowState == WindowState.Minimized)
			{
				WindowState = WindowState.Normal;
			}
			Topmost = true;
			Topmost = false;
			Activate();
			// 从托盘点开要有淡入，不然就是硬闪一下
			PlayStartupAnimation();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "从托盘恢复窗口失败");
		}
	}

	/// <summary>被后开的实例唤醒：从托盘/最小化恢复并置顶到前台。播放不中断。</summary>
	public void WakeToFront()
	{
		try
		{
			if (!IsVisible)
			{
				Show();
			}
			if (WindowState == WindowState.Minimized)
			{
				WindowState = WindowState.Normal;
			}
			if (!IsActive)
			{
				// Topmost 抖一下把焦点抢回来，这是 WPF 里最稳的置顶做法。
				Topmost = true;
				Topmost = false;
				Activate();
			}
			// 被后开的实例唤醒时窗口可能刚从托盘出来（Opacity 还是 0），
			// 这里补一次淡入，否则就是硬闪。
			if (Opacity < 1.0)
			{
				PlayStartupAnimation();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "唤醒置顶失败");
		}
	}

	private void ToggleWindowVisibility()
	{
		if (IsVisible && WindowState != WindowState.Minimized)
		{
			HideToTray();
		}
		else
		{
			ShowFromTray();
		}
	}

	/// <summary>标题栏搜索框：回车或点右边的放大镜都走这里，切到「搜索音乐」页并发起搜索。</summary>
	private void TitleSearchButton_Click(object sender, RoutedEventArgs e)
	{
		SubmitTitleSearch();
	}

	private void TitleSearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		CommitTitleSearchPlaceholder();
		// 用户自己动过输入框，历史指针就作废，下次按 ↑ 重新从最近的一条开始。
		// 程序自己填的（↑/↓/Ctrl+Z 取历史）不算，所以要用 _suppressSearchHistory 挡一下。
		if (!_suppressSearchHistory)
		{
			_searchHistoryIndex = -1;
		}
	}

	private void TitleSearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			SubmitTitleSearch();
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			// Esc 清空并让出焦点，别把用户困在输入框里
			TitleSearchBox.Text = "";
			TitleSearchBox.Focus();
			CommitTitleSearchPlaceholder();
		}
		else if (e.Key == Key.Up)
		{
			// 往上翻一次搜过的词。放在 KeyDown 而不是 PreviewKeyDown：
			// 光标在中间时用户多半是想按 ↑ 回到上一行，语义冲突，
			// 但搜索框里没有「上一行」这回事，↑ 当历史更实用。
			if (RecallSearchKeyword(older: true))
			{
				e.Handled = true;
			}
		}
		else if (e.Key == Key.Down)
		{
			// 往下翻回更新的词；已经到最新的那条就还回默认空内容
			if (RecallSearchKeyword(older: false))
			{
				e.Handled = true;
			}
		}
	}

	// 顶部搜索框的历史关键词。存的是「最近一次搜过」倒序，越靠前越新。
	private readonly List<string> _searchHistory = new List<string>();
	private int _searchHistoryIndex = -1;

	/// <summary>在搜索历史里上下走一步。older=true 往回翻（↑）。
	/// 走一步就把 Text 换掉并全选，用户直接改就能接着搜。
	/// 没有历史、或已经到边界就返回 false，交还给 TextBox 自己处理按键。</summary>
	private bool RecallSearchKeyword(bool older)
	{
		if (_searchHistory.Count == 0)
		{
			return false;
		}
		int next = _searchHistoryIndex;
		if (next < 0)
		{
			// 第一次按：从「当前输入」（可能是新的、还没搜过的）跳进历史
			next = older ? 0 : _searchHistory.Count - 1;
		}
		else
		{
			next += older ? 1 : -1;
		}
		if (next < 0 || next >= _searchHistory.Count)
		{
			// ↓ 已经走到最新的那条就别再动了，让它保持原样
			return false;
		}
		_searchHistoryIndex = next;
		string kw = _searchHistory[next];
		_suppressSearchHistory = true;
		try
		{
			TitleSearchBox.Text = kw;
			TitleSearchBox.SelectAll();
			TitleSearchBox.CaretIndex = kw.Length;
		}
		finally
		{
			_suppressSearchHistory = false;
		}
		CommitTitleSearchPlaceholder();
		return true;
	}

	// 程序自己在改 TitleSearchBox.Text 时置位，免得把历史指针又搅乱。
	private bool _suppressSearchHistory;

	/// <summary>记一次搜过的词。连续搜同一个词不重复入栈。</summary>
	private void PushSearchHistory(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return;
		}
		keyword = keyword.Trim();
		_searchHistory.RemoveAll(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase));
		_searchHistory.Insert(0, keyword);
		// 只留最近 20 条，别让列表无上限地长
		if (_searchHistory.Count > 20)
		{
			_searchHistory.RemoveRange(20, _searchHistory.Count - 20);
		}
		_searchHistoryIndex = -1;
	}

	private void TitleSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		// TextBox 里 Ctrl+F / Ctrl+L 之类还是给全局热键，先拦下来免得两边都响应
		if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
		{
			TitleSearchBox.SelectAll();
			e.Handled = true;
		}
		else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
		{
			// Ctrl+Z 当「找回上一个搜过的词」。这里必须用 PreviewKeyDown 抢先：
			// TextBox 自己会把 Ctrl+Z 当撤销编辑，走 KeyDown 就晚了。
			// 用户在搜索框里最想撤的往往不是刚敲错的一个字，而是「搜错了词」，
			// 整段换回上一次的更省事。
			// Shift 加 Ctrl+Z 留给 TextBox 原本的重做，不然撤销键就再也够不着了。
			if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
			{
				if (RecallSearchKeyword(older: true))
				{
					e.Handled = true;
				}
			}
		}
	}

	private void SubmitTitleSearch()
	{
		string keyword = TitleSearchBox.Text?.Trim() ?? "";
		if (keyword.Length == 0)
		{
			return;
		}
		CommitTitleSearchPlaceholder();
		PushSearchHistory(keyword);
		// 先把词递进去再跳转。页面没建的话，OnlineMusicPage.OnNavigatedTo 会
		// 取出来跑，并且因此跳过随机自动搜索；已经建好了就直接开搜。
		if (_pages.TryGetValue("online", out UserControl? page) && page is OnlineMusicPage existing)
		{
			existing.QueueSearch(keyword);
		}
		else if (CreatePage("online") is OnlineMusicPage fresh)
		{
			_pages["online"] = fresh;
			fresh.QueueSearch(keyword);
		}
		NavigateTo("online");
		TitleSearchBox.Clear();
	}

	/// <summary>输入框为空时把提示文字显示出来，否则挡住真实内容。</summary>
	private void CommitTitleSearchPlaceholder()
	{
		if (TitleSearchHint != null)
		{
			TitleSearchHint.Visibility = string.IsNullOrEmpty(TitleSearchBox.Text)
				? Visibility.Visible
				: Visibility.Collapsed;
		}
	}

	// 拖动进度条时缓存的总时长。
	// 之前每收到一次鼠标移动就去读一次 MusicService.Duration（要走 NAudio reader），
	// 大幅拖动时事件非常密集，就会卡；这里在按下那一刻读一次，拖动过程只用缓存值。
	private TimeSpan _scrubDuration;

	// Seek 已发出但播放位置尚未确认的窗口期。这段时间内禁止定时器回写滑块。
	private bool _seekPending;

	// 上一次 Seek 的时刻。Seek 会同步改 reader.CurrentTime，
	// 大跨度定位在压缩音频上要丢弃并重解一大段数据，是同步耗时操作；
	// 连着点几下就会把 UI 线程堵死（表现就是「程序未响应」）。
	// 这里做节流：太密集的连续点击只保留最后一次目标位置。
	private DateTime _lastSeekAt = DateTime.MinValue;

	// 延后执行那次被节流的 Seek。
	private readonly DispatcherTimer _seekThrottleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

	// 等播放器确认新位置之后再放开回写。
	// 每次 Seek 都 new 一个 DispatcherTimer 会攒下一堆没停的定时器白白占着消息泵，
	// 所以复用一个。
	private readonly DispatcherTimer _seekSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(340) };

	private bool _hasDeferredSeek;

	private void MiniProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_progressDragging = true;
		_scrubDuration = MusicService.Duration;
		// 这里绝对不能 e.Handled = true：吃掉事件的话 Slider 内部的 Thumb
		// 拿不到鼠标、也就不会去捕获鼠标，于是鼠标一拖出轨道范围就再也收不到
		// PreviewMouseMove，表现为「大幅度拖动 / 拖出框就卡住没反应」。
	}

	private void MiniProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_progressDragging)
		{
			return;
		}
		// 拖动中只更新预览时间，不碰 MusicService，保证跟手。
		TimeSpan dur = _scrubDuration;
		if (dur > TimeSpan.Zero)
		{
			try
			{
				MiniTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue * dur.TotalSeconds)) + " / " + FormatTime(dur);
			}
			catch
			{
			}
		}
	}

	private void MiniProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		// 注意顺序：必须先 Seek 再清 _progressDragging。
		// 反过来先清标志的话，Seek 触发的 UpdateProgressUi 会认为"没在拖动"，
		// 拿旧 Position 覆盖掉用户刚点到的值 —— 这就是「点一下又弹回去」的直接原因。
		SeekToSliderValue();
		_progressDragging = false;
		// _seekPending 不能在这里清：它要一直挂到播放器确认新位置为止，
		// 中途被清掉的话 _tick 就会拿旧位置把滑块拽回去。
	}

	// 鼠标在窗口外松开时收不到 PreviewMouseLeftButtonUp，
	// 标志会永久卡在 true 导致进度条再也不刷新。捕获丢失时兜底复位。
	private void MiniProgressSlider_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (_progressDragging)
		{
			SeekToSliderValue();
		}
		_progressDragging = false;
	}

	private void SeekToSliderValue()
	{
		try
		{
			double total = MusicService.Duration.TotalSeconds;
			if (total <= 0)
			{
				return;
			}
			double target = MiniProgressSlider.Value * total;

			// 无论走哪条分支，在播放器确认新位置之前都不许回写滑块，
			// 视觉上就始终停在用户点到的位置（修「点一下又弹回去」）。
			_seekPending = true;

			// 离上一次真正的 Seek 太近：先记着，节流窗口结束后再发。
			// 否则每次点击都同步做一次大跨度 CurrentTime 赋值，
			// 连点几下直接把 UI 线程堵死。
			if ((DateTime.UtcNow - _lastSeekAt).TotalMilliseconds < 260.0)
			{
				_hasDeferredSeek = true;
				_seekThrottleTimer.Stop();
				_seekThrottleTimer.Start();
				return;
			}
			DoSeek(target);
		}
		catch (Exception ex)
		{
			_seekPending = false;
			LogHelper.LogError(ex, "定位失败");
		}
	}

	private void OnSeekThrottleTick(object? sender, EventArgs e)
	{
		_seekThrottleTimer.Stop();
		if (!_hasDeferredSeek)
		{
			return;
		}
		_hasDeferredSeek = false;
		// 期间用户可能已经拖到别处，所以取滑块的最新值而不是记下来的那个
		double total = MusicService.Duration.TotalSeconds;
		if (total <= 0)
		{
			_seekPending = false;
			return;
		}
		DoSeek(MiniProgressSlider.Value * total);
	}

	/// <summary>真正执行 Seek。调用方已保证节流窗口已过。</summary>
	private void DoSeek(double seconds)
	{
		try
		{
			_lastSeekAt = DateTime.UtcNow;
			if (!MusicService.Seek(seconds))
			{
				// Seek 失败就恢复原位，别把滑块留在一个假位置上
				LogHelper.Log($"进度条定位失败：播放器拒绝了这次 Seek（{seconds:0.000}）");
			}
			_seekSettleTimer.Stop();
			_seekSettleTimer.Start();
		}
		catch (Exception ex)
		{
			_seekPending = false;
			LogHelper.LogError(ex, "进度条定位失败");
		}
	}

	private void OnSeekSettledTick(object? sender, EventArgs e)
	{
		_seekSettleTimer.Stop();
		// 播放器已经报出新位置了，可以交回 _tick 正常回写
		_seekPending = false;
	}

	private void MiniPrevButton_Click(object sender, RoutedEventArgs e)
	{
		MusicService.PlayPrevious();
	}

	private void MiniPlayPauseButton_Click(object sender, RoutedEventArgs e)
	{
		TogglePlayPause();
	}

	private void MiniNextButton_Click(object sender, RoutedEventArgs e)
	{
		MusicService.PlayNext();
	}

	private void MiniFavButton_Click(object sender, RoutedEventArgs e)
	{
		ToggleFavCurrent();
	}

	private void MiniVolumeButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			UpdateVolumeSlider();
			// VolumePopup 是 StaysOpen=False：Popup 一打开就会接管鼠标捕获做「点外面关闭」，
			// 而这次打开它的正是同一次点击的收尾，于是它立刻把自己当成外部点击关掉——
			// 表现就是音量条「闪一下就没」，根本来不及拖。
			// 推迟到本次鼠标交互彻底结束（Input 优先级，即当前输入队列跑完）再打开。
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (!IsVisible || WindowState == WindowState.Minimized)
				{
					return;
				}
				VolumePopup.IsOpen = true;
				LogHelper.LogDebug($"[VolumePopup] 已打开 childH={((FrameworkElement?)VolumePopup.Child)?.ActualHeight}");
			}), System.Windows.Threading.DispatcherPriority.Input);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开音量弹窗失败");
		}
	}

	private void VolumePopupSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		try
		{
			double v = Math.Clamp(e.NewValue, 0.0, 100.0) / 100.0;
			MusicService.SetVolume(v);
			UpdateMiniVolumeText();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "设置音量失败");
		}
	}

	// 音量滑杆原来也有同样的毛病：PreviewMouseLeftButtonDown / PreviewMouseMove 里
	// 自己算位置并 e.Handled=true，把 Slider 自己的 Thumb 逻辑挡掉了，
	// 鼠标一拖出滑杆范围就收不到事件。现在完全交给 Slider：
	// XAML 里开了 IsMoveToPointEnabled，点击/拖动都由 Thumb 自己捕获鼠标处理，
	// 这三个手动处理器就不需要了（留着空壳反而容易让人以为还有逻辑）。
	private void MiniFxButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			BuildFxPopup();
			FxPopup.IsOpen = true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开音效弹窗失败");
		}
	}

	private void BuildFxPopup()
	{
		FxPopupPanel.Children.Clear();
		string current = SettingsManager.Instance.Fx?.PresetName ?? "关闭";
		foreach (FxPreset preset in FxPreset.Presets)
		{
			string name = preset.Name;
			var rb = new RadioButton
			{
				Content = name,
				IsChecked = string.Equals(name, current, StringComparison.OrdinalIgnoreCase),
				Tag = name
			};
			rb.Click += (_, _) => ApplyFxPreset(name);
			FxPopupPanel.Children.Add(rb);
		}
		FxPopupPanel.Children.Add(new Separator { Margin = new Thickness(6, 4, 6, 8) });
		FxPopupEqButton.Click += FxPopupEqButton_Click;
		FxPopupPanel.Children.Add(FxPopupEqButton);
	}

	private void ApplyFxPreset(string name)
	{
		try
		{
			FxPresetPage.ApplyPreset(name);
			FxPopup.IsOpen = false;
			UpdateMiniFx();
			UpdateMiniFav();
			ShowToast("音效: " + name);
			LogHelper.Log("音效预设切换为: " + name);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "切换音效预设失败");
		}
	}

	private void FxPopupEqButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			FxPopup.IsOpen = false;
			NavigateTo("fx");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "跳转均衡器页面失败");
		}
	}

	public void ShowToast(string text, bool success = true)
	{
		try
		{
			// 最多同时显示 3 条，多出来的从最旧的开始丢，避免连点时糊满屏幕。
			while (ToastPanel.Children.Count >= 3)
			{
				ToastPanel.Children.RemoveAt(0);
			}
			var tb = new TextBlock
			{
				Text = text,
				FontSize = 13,
				Foreground = Brushes.White,
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 560
			};
			Brush accent = Application.Current.TryFindResource("AccentBrush") as Brush
				?? ThemeManager.Accent();
			Color accentColor = accent is SolidColorBrush absb ? absb.Color : Color.FromRgb(0x3C, 0x8C, 0xFF);
			var border = new Border
			{
				Background = new SolidColorBrush(success ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0x6B, 0x1F, 0x24)),
				BorderBrush = success
					? new SolidColorBrush(Color.FromArgb(0x80, accentColor.R, accentColor.G, accentColor.B))
					: new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(16),
				Padding = new Thickness(16, 9, 16, 9),
				Margin = new Thickness(0, 0, 0, 8),
				MaxWidth = 620,
				HorizontalAlignment = HorizontalAlignment.Center,
				Child = tb
			};
			ToastPanel.Children.Add(border);
			var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
			border.BeginAnimation(UIElement.OpacityProperty, fadeIn);

			var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
			timer.Tick += (_, _) =>
			{
				timer.Stop();
				var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
				fadeOut.Completed += (_, _) => ToastPanel.Children.Remove(border);
				border.BeginAnimation(UIElement.OpacityProperty, fadeOut);
			};
			timer.Start();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "显示提示失败");
		}
	}

	/// <summary>按比例调整强调色明度：amount 为正提亮、为负压暗。</summary>
	private static Color Shift(Color c, double amount)
	{
		// 强调色加到纯白/纯黑之后，按常规算法再「提亮一点/压暗一点」
		// 结果还是同一个值，悬停时按钮看上去完全没反应。
		// 所以颜色已经贴边时就反过来调，让悬停一定看得见。
		bool atMax = c.R >= 250 && c.G >= 250 && c.B >= 250;
		bool atMin = c.R <= 5 && c.G <= 5 && c.B <= 5;
		if (amount > 0 && atMax)
		{
			amount = -0.12;
		}
		else if (amount < 0 && atMin)
		{
			amount = 0.22;
		}
		byte Channel(byte v) => (byte)Math.Clamp(Math.Round(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount)), 0, 255);
		return Color.FromRgb(Channel(c.R), Channel(c.G), Channel(c.B));
	}

	/// <summary>算一个压在强调色上能看清的颜色。
	/// 以前强调色上恒定写死白字，加进纯白/纯黑这两档后就会变成白底白字、黑底黑字，
	/// 按钮上的字直接消失。改用相对亮度决定用近黑还是近白。</summary>
	private static Color OnAccentColor(Color accent)
	{
		// sRGB 相对亮度（够用，不需要完整的 WCAG 线性化）
		double lum = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255.0;
		return lum > 0.62
			? Color.FromRgb(0x1A, 0x1A, 0x1A)
			: Color.FromRgb(0xFF, 0xFF, 0xFF);
	}

	private static SolidColorBrush Frozen(Color c)
	{
		var b = new SolidColorBrush(c);
		b.Freeze();
		return b;
	}

	/// <summary>按设置里的强调色刷新全局资源（基色 + 悬停/按下），并同步收藏高亮、播放模式图标与当前页。</summary>
	public void ApplyAccentTheme()
	{
		try
		{
			string hex = (SettingsManager.Instance.AccentColor ?? "#1C1C1C").Trim();
			if (!hex.StartsWith("#", StringComparison.Ordinal))
			{
				hex = "#" + hex;
			}
			var color = (Color)ColorConverter.ConvertFromString(hex);
			var brush = new SolidColorBrush(color);
			brush.Freeze();
			Application.Current.Resources["AccentColor"] = color;
			Application.Current.Resources["AccentBrush"] = brush;
			// 压在强调色上的字色。纯白/纯黑这两档强调色全靠它才看得清字。
			var onAccent = Frozen(OnAccentColor(color));
			Application.Current.Resources["OnAccentColor"] = onAccent.Color;
			Application.Current.Resources["OnAccentBrush"] = onAccent;
			// 悬停/按下色原先是写死的，自定义主题色时按钮反馈对不上，这里按基色推算后一并写入。
			Application.Current.Resources["AccentHoverColor"] = Shift(color, 0.18);
			Application.Current.Resources["AccentHoverBrush"] = Frozen(Shift(color, 0.18));
			Application.Current.Resources["AccentPressedColor"] = Shift(color, -0.12);
			Application.Current.Resources["AccentPressedBrush"] = Frozen(Shift(color, -0.12));
			// AccentSubtle* 由主题系统统一算（浅色下透明度不一样），换色时一起刷新
			ThemeManager.ApplyAccentDerived(Application.Current.Resources);
			TrackRows.AccentBrush = brush;
			TrackRows.OnAccentBrush = onAccent;
			// 通知用 TrackRows 画行的页面（音乐库/最近/收藏/在线搜索）把行重建，
			// 否则它们缓存的旧行还是换色前的播放键和标签颜色。
			TrackRows.RaiseAccentChanged();
			UpdateMiniOrder();
			// 背景那张卡片蒙版跟着强调色/主题重画一次，视频和图片两条路径都要覆盖
			ApplyBackground();
			if (_pages.TryGetValue(_currentKey ?? "", out UserControl? page) && page is IMusicPage mp)
			{
				mp.OnNavigatedTo();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "应用主题色失败");
		}
	}

	// ==================== 最近播放 / 我的收藏 右滑抽屉 ====================

	private string _drawerTab = "recent";

	private void OnLibraryChangedForDrawer()
	{
		if (HistoryDrawer.Visibility != Visibility.Visible)
		{
			return;
		}
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(BuildDrawerRows));
			return;
		}
		BuildDrawerRows();
	}

	private void MiniLyricButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			LyricRuntime.Toggle();
			SyncLyricButton();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "切换桌面歌词失败");
		}
	}

	/// <summary>歌词开关状态变了就换按钮外观。
	/// 「总开关开着但窗口被 Esc 收起来」和「彻底关掉」在能力上是一回事，
	/// 所以统一用 Enabled 来决定高亮，不去区分窗口是不是真的可见。</summary>
	private void SyncLyricButton()
	{
		bool on = LyricRuntime.Enabled;
		MiniLyricGlyph.Foreground = on
			? (Brush)FindResource("AccentBrush")
			: (Brush)FindResource("TextSecondaryBrush");
		MiniLyricButton.ToolTip = Loc.Default[on ? "lyric_toggle_on" : "lyric_toggle_off"];
	}

	private void MiniHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		if (HistoryDrawer.Visibility == Visibility.Visible)
		{
			HideDrawer();
			return;
		}
		ShowDrawer();
	}

	private void ShowDrawer()
	{
		try
		{
			HistoryDrawer.Visibility = Visibility.Visible;
			double from = Math.Max(1, HistoryDrawer.ActualWidth > 0 ? HistoryDrawer.ActualWidth : 360);
			HistoryDrawerTranslate.X = from;
			var slide = new DoubleAnimation(from, 0.0, TimeSpan.FromMilliseconds(260))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			var sb = new Storyboard();
			sb.Children.Add(slide);
			Storyboard.SetTarget(slide, HistoryDrawerTranslate);
			Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));
			sb.Begin();
			// 播放栏那个「最近播放」按钮必须直接落在最近播放页。
			// 之前这里读的是上次记住的 DrawerTab，上次停在收藏的话，
			// 点最近播放却弹出一个收藏列表，看起来就是点错了。
			SetDrawerTab("recent");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开最近播放抽屉失败");
		}
	}

	private void HideDrawer()
	{
		try
		{
			double from = HistoryDrawerTranslate.X;
			double to = HistoryDrawer.ActualWidth > 0 ? HistoryDrawer.ActualWidth : 360;
			var slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
			};
			var sb = new Storyboard();
			sb.Completed += (_, _) => HistoryDrawer.Visibility = Visibility.Collapsed;
			sb.Children.Add(slide);
			Storyboard.SetTarget(slide, HistoryDrawerTranslate);
			Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));
			sb.Begin();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "关闭抽屉失败");
		}
	}

	private void HistoryDrawerCloseButton_Click(object sender, RoutedEventArgs e)
	{
		HideDrawer();
	}

	private void DrawerRecentTab_Click(object sender, RoutedEventArgs e)
	{
		SetDrawerTab("recent");
	}

	private void DrawerFavTab_Click(object sender, RoutedEventArgs e)
	{
		SetDrawerTab("fav");
	}

	private void HistoryClearButton_Click(object sender, RoutedEventArgs e)
	{
		UserLibrary.ClearRecent();
		BuildDrawerRows();
	}

	private void SetDrawerTab(string tab)
	{
		_drawerTab = tab;
		var s = SettingsManager.Instance;
		if (!string.Equals(s.DrawerTab, tab, StringComparison.Ordinal))
		{
			s.DrawerTab = tab;
			SettingsManager.Save(s);
		}
		ApplyDrawerTabStyle();
		HistoryDrawerTitle.Text = Loc.Default[tab == "fav" ? "nav_fav" : "nav_recent"];
		HistoryClearButton.Visibility = tab == "recent" ? Visibility.Visible : Visibility.Collapsed;
		HistoryClearButton.Content = Loc.Default["drawer_clear"];
		BuildDrawerRows();
	}

	private void ApplyDrawerTabStyle()
	{
		try
		{
			var accent = (Brush)Application.Current.Resources["AccentBrush"];
			var idle = ThemeManager.Secondary();
			DrawerRecentTab.Content = Loc.Default["nav_recent"];
			DrawerFavTab.Content = Loc.Default["nav_fav"];
			DrawerRecentTab.Background = _drawerTab == "recent" ? accent : Brushes.Transparent;
			DrawerFavTab.Background = _drawerTab == "fav" ? accent : Brushes.Transparent;
			DrawerRecentTab.Foreground = _drawerTab == "recent" ? Brushes.White : idle;
			DrawerFavTab.Foreground = _drawerTab == "fav" ? Brushes.White : idle;
		}
		catch
		{
		}
	}

	private void BuildDrawerRows()
	{
		DrawerList.Children.Clear();
		List<SavedTrack> items = _drawerTab == "fav"
			? UserLibrary.Favorites
			: UserLibrary.RecentlyPlayed;
		DrawerEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		DrawerEmptyText.Text = Loc.Default[_drawerTab == "fav" ? "drawer_empty_fav" : "drawer_empty_recent"];
		foreach (SavedTrack t in items)
		{
			var copy = t;
			bool fav = UserLibrary.IsFavorite(copy);
			DrawerList.Children.Add(TrackRows.BuildRow(copy, null, () => DrawerPlay(copy), fav, () => UserLibrary.ToggleFavorite(copy)));
		}
	}

	private void DrawerPlay(SavedTrack t)
	{
		MusicService.PlayFavorite(t, err =>
		{
			if (string.IsNullOrEmpty(err))
			{
				return;
			}
			Dispatcher.BeginInvoke(new Action(() => ShowToast(err, false)));
		});
	}
}
