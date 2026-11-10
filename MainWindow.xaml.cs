using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
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
		// 起播前预载背景视频：必须在音频出声之前把 Source 挂上，
		// 否则 4K mp4 的首帧解码总是慢半拍，看起来就是音乐先跑、画面后追。
		MusicService.TrackStarting += PrepareTrackVideo;
		MusicService.FetchStateChanged += OnFetchStateChanged;
		SourceInitialized += OnSourceInitialized;
		StateChanged += OnWindowStateChanged;
		UserLibrary.Changed += OnLibraryChangedForDrawer;

		// 滚轮命中缓存的失效信号。LayoutUpdated 每次布局都会来，这里只置一个
		// 布尔位（比遍历视觉树便宜几个数量级），真正重走树留到下一次滚轮。
		// 滚轮命中缓存见 FindScrollViewerUnderCursor 的注释。
		LayoutUpdated += (_, _) => _svCacheDirty = true;

		// 底栏曲名宽度钳制：曲名与复制键同处一个水平 StackPanel（键紧贴曲名
		// 尾部，左间距 8px，见 XAML）。StackPanel 会给曲名「无限宽」，
		// 不钳的话长曲名会把复制键顶进右侧时间区。钳制逻辑收在
		// ClampMiniTitle()，走马灯每次重估（含窗口变窄触发的 SizeChanged）
		// 都先钳一遍再判定，曲名先截断、复制键跟在省略号后面，
		// 始终不与时间文字重叠。
		MiniTitleRow.SizeChanged += (_, _) => RefreshTitleMarquee();

		// 标题栏搜索建议的 Popup 是独立顶层窗口：拖动主窗时 WPF 不会给它
		// 重算位置，列表会脱锚留在原地悬着。搬窗中途没人在看列表，
		// 直接收掉，落点稳定比「跟着抖」舒服，也省掉候选过期的问题。
		// 窗口被拖走时收列表。走带保护的版本：正在取流时别把列表抽掉，
		// 否则用户拖一下窗口，等了半天的那首又成了「点了没反应」。
		LocationChanged += (_, _) => CloseSuggest();

		// 系统媒体控制：音量飞轮 / 耳机按键 / 媒体浮层都转发给 NAudio 这条链
		SmtcService.PlayRequested += () => MusicService.Resume();
		SmtcService.PauseRequested += () => MusicService.Pause();
		SmtcService.NextRequested += () => MusicService.PlayNext();
		SmtcService.PrevRequested += () => MusicService.PlayPrevious();
		// 底栏播放顺序按钮的右键模式菜单（左键循环切换不变）。
		InitMiniOrderContextMenu();
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

		// 歌词缓存更新（用户改了歌词/重试拉取）时刷新迷你播放器来源框。
		LyricRuntime.Follow.LyricsChanged += OnFsLyricsChanged;

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

	/// <summary>歌词匹配/重试成功后刷新迷你播放器来源框（网易云/QQ 这时才定，
	/// 双色框跟着刷一次）。事件不一定在 UI 线程，切回 UI 再动控件。</summary>
	private void OnFsLyricsChanged(LyricBundle? bundle)
	{
		try
		{
			Dispatcher.Invoke(() =>
			{
				UpdateMiniSourceFrame();
			});
		}
		catch
		{
		}
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
				case HotkeyAction.VolumeDown:
				case HotkeyAction.VolumeUp:
					if (_volHoldTimer != null)
					{
						// 长按步进期间又来的 WM_HOTKEY（手抖重复按下）直接忽略：
						// 定时器和热键各步一份的话音量会翻倍地跳。
						break;
					}
					// 首条消息立即步进一次（单击=一步），按住不放由定时器接管加速。
					// 步长 5%，与音量条拖动的最小可感刻度一致（音量条 0-100）。
					StepVolume(action == HotkeyAction.VolumeDown ? -0.05 : 0.05);
					StartVolumeHold(action);
					break;
				case HotkeyAction.LyricsToggle:
					// 与迷你栏歌词按钮同一条路：LyricRuntime 总开关 + 图标同步。
					LyricRuntime.Toggle();
					SyncLyricButton();
					break;
			}
			LogHelper.LogDebug("全局快捷键触发: " + action);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "全局快捷键执行失败: " + action);
		}
	}

	/// <summary>音量热键的统一入口：改 MusicService 音量并同步所有音量 UI
	/// （弹出的音量条、迷你栏音量填充条与百分比）。delta 为负即减。
	/// 现值读设置里的 MusicVolume（MusicService 只存写，不暴露读）。</summary>
	private void StepVolume(double delta)
	{
		double target = Math.Clamp(SettingsManager.Instance.MusicVolume + delta, 0.0, 1.0);
		MusicService.SetVolume(target);
		UpdateVolumeSlider();
		UpdateMiniVolumeText();
		LogHelper.Log($"音量热键: {(delta < 0 ? "降低" : "增加")} -> {(int)Math.Round(target * 100)}%");
	}

	// ── 音量热键长按连续步进（仅 VolumeDown/VolumeUp，其他热键不受影响）──
	// WM_HOTKEY 挂了 MOD_NOREPEAT：按住不放也只来一条消息，
	// 「长按越调越快」只能自己起定时器接着步进，每个 tick 轮询
	// GetAsyncKeyState（经 HotkeyManager.IsComboDown）看组合键是否已松开。
	private DispatcherTimer? _volHoldTimer;
	private HotkeyAction _volHoldAction;
	private DateTime _volHoldStart;

	private void StartVolumeHold(HotkeyAction action)
	{
		StopVolumeHold();
		_volHoldAction = action;
		_volHoldStart = DateTime.UtcNow;
		_volHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
		_volHoldTimer.Tick += VolHold_Tick;
		_volHoldTimer.Start();
	}

	private void VolHold_Tick(object? sender, EventArgs e)
	{
		if (_volHoldTimer == null)
		{
			return;
		}
		// 组合键已松开就停：连续步进只该发生在按住期间。
		// 轮询用的是注册时生效的组合（含用户改过的修饰键），不是写死的 Ctrl+Alt。
		if (!HotkeyManager.IsComboDown(_volHoldAction))
		{
			StopVolumeHold();
			return;
		}
		double heldMs = (DateTime.UtcNow - _volHoldStart).TotalMilliseconds;
		// 间隔随已按住时长递减：220 → 140 → 90 → 60（封底）。
		// 按住时长比步进次数更贴近「越来越快」的手感：同样是按了 2 秒，
		// 不管中途定时器走了几步，落点档位一致。
		_volHoldTimer.Interval = TimeSpan.FromMilliseconds(
			heldMs < 600 ? 220 : heldMs < 1300 ? 140 : heldMs < 2300 ? 90 : 60);
		StepVolume(_volHoldAction == HotkeyAction.VolumeDown ? -0.05 : 0.05);
	}

	private void StopVolumeHold()
	{
		if (_volHoldTimer != null)
		{
			_volHoldTimer.Stop();
			_volHoldTimer.Tick -= VolHold_Tick;
			_volHoldTimer = null;
		}
	}

	/// <summary>程序内快捷键（Q/E/1/2/3）。挂在 Window 上用 PreviewKeyDown：
	/// 隧道阶段先于页面拿到键，列表项按钮、Slider 之类聚焦在身时也能收到，
	/// 不用每个页面各接一遍。
	///
	/// 光标在输入框里时一律放行。Q/E/1/2/3 都是能打的字符，
	/// 在搜索框里截住的话用户连「qe」这个词都打不出来——
	/// 所以列表页自己的搜索框是按「Enter / 空格 播第一条」处理的，
	/// 不走这里。</summary>
	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		try
		{
			// Esc 只管三件事：关评论抽屉、关建议下拉、退全屏。顺序很关键——
			// 评论抽屉的 Esc 咬死在这里：它能盖在主界面上，Esc 先收它。
			if (e.Key == Key.Escape && CommentsPanel != null && CommentsPanel.IsOpen)
			{
				CommentsPanel.Close();
				e.Handled = true;
				return;
			}
			if (IsTextInputFocused())
			{
				return;
			}
			LocalAction? action = LocalHotkeys.Resolve(e.Key, Keyboard.Modifiers);
			if (action == null)
			{
				return;
			}
			e.Handled = true;
			switch (action.Value)
			{
				case LocalAction.ListPrev:
				case LocalAction.Prev:
					MusicService.PlayPrevious();
					break;
				case LocalAction.ListNext:
				case LocalAction.Next:
					MusicService.PlayNext();
					break;
				case LocalAction.PlayPause:
					TogglePlayPause();
					break;
			}
			LogHelper.LogDebug("程序内快捷键触发: " + action + " key=" + e.Key);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "程序内快捷键执行失败");
		}
	}

	/// <summary>焦点是不是落在能打字的控件上。</summary>
	private static bool IsTextInputFocused()
	{
		DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
		while (focused != null)
		{
			switch (focused)
			{
				case TextBox:
				case PasswordBox:
				case RichTextBox:
				case ComboBox { IsEditable: true }:
					return true;
			}
			// 焦点可能在窗口里（焦点丢失时 Keyboard.FocusedElement 为 null），
			// 往上找一层看看是不是嵌在可编辑控件里（比如 DataGrid 单元格）。
			focused = focused is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(focused)
				: LogicalTreeHelper.GetParent(focused);
		}
		return false;
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			LogHelper.Log("程序启动，主窗口加载");
			LoadTitleIcon();
			BuildNavItems();
			// 切界面语言后重刷导航栏文字。
			// BuildNavItems 只在启动时跑一次，里面的字是那时的语言；
			// 不订这个事件的话，切完语言左栏还是旧语言的字，
			// 而页面里用 Binding 写的标题已经换了 —— 一新一旧混着看。
			Loc.Default.LanguageChanged -= OnLanguageChanged;
			Loc.Default.LanguageChanged += OnLanguageChanged;
			MusicService.Initialize();
			MusicService.ApplySettings();
			ApplyBackground();
			ApplyAccentTheme();
			StartGagTitle();
			PlayStartupAnimation();
			UpdateMiniVolumeText();
			UpdateMiniFav();
			UpdateMiniOrder();
			// 启动时先把当前曲目同步给行列表，这样重开程序后正在播的那首
			// 在音乐库/最近/收藏里立刻就有强调条（带 Key，在线行也能亮）
			TrackRows.SetPlayingTrack(MusicService.CurrentPath, MusicService.BuildCurrentTrack()?.Key);
			UpdateProgressUi();

			// 进度条每 300ms 走一次，顺带刷一下封面方块的来源框：
			// 「还在找歌词/封面」这个状态没有自己的事件（后台在跑），
			// 靠它才能把呼吸的那半边及时点亮/收掉。
			// UpdateMiniSourceFrame 内部比对了上次的状态，没变化就直接返回，
			// 不会把呼吸动画掐断重来。
			_tick.Tick += (_, _) =>
			{
				UpdateProgressUi();
				UpdateMiniSourceFrame();
			};
			_tick.Start();

			_seekThrottleTimer.Tick += OnSeekThrottleTick;

			UpdateVolumeSlider();

			// 定位小球：启动恢复显隐与上次停放位置；窗口缩放时把球夹回可视范围。
			ApplyLocateBallVisibility();
			SizeChanged += (_, _) =>
			{
				if (PlayLocateBall.Visibility == Visibility.Visible)
				{
					RestoreLocateBallPosition();
				}
			};

			TrayIconService.Create();
			// 内存守护：工作集连续超过设置阈值（默认 150MB）时自动释放一次。
			// 开关和阈值都在设置页里，这里只管把它跑起来。
			MemoryGuard.Start();
			// 音画漂移校正：音频（NAudio）和视频（MediaElement）各跑各的时钟，
			// 4K 掉帧时越差越远。0.7 秒看一眼，落后 0.2s 带提前量追、
			// 超前 0.45s 才拽（见 VideoSyncTick 里的参数说明）。
			_videoSyncTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(700)
			};
			_videoSyncTimer.Tick += VideoSyncTick;
			_videoSyncTimer.Start();
			NavigateTo("library");
			// 启动自动播放（随机）：窗口已显示、播放引擎已 Initialize，进程冷启动
			// 跑一次（托盘恢复走 ShowFromTray，不经过 Window_Loaded）。
			TryStartupAutoPlay();
			LogHelper.Log("界面初始化完成");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "主窗口加载失败");
		}
	}

	// ==================== 启动自动播放（随机） ====================

	/// <summary>防重入：启动自动播放整个进程生命周期只允许跑一次。</summary>
	private bool _startupAutoPlayDone;

	/// <summary>「启动时自动播放」入口。窗口加载完（Window_Loaded 末尾）调一次：
	/// 开关没开直接走人；开关开着就等扫盘完成（本地列表恢复要 KnownMusic 就绪，
	/// 与自动续播同一个挂点），再按上次播放上下文随机挑一首开播。
	/// 任何一步失败静默降级为不播，只记 Debug 日志，绝不弹 Toast。</summary>
	private void TryStartupAutoPlay()
	{
		try
		{
			if (_startupAutoPlayDone)
			{
				return;
			}
			_startupAutoPlayDone = true;
			AppSettings s = SettingsManager.Instance;
			if (!s.StartupAutoPlay)
			{
				return;
			}
			PlayContext? ctx = s.LastPlayContext;
			if (ctx == null || string.IsNullOrWhiteSpace(ctx.Page))
			{
				LogHelper.LogDebug("启动自动播放: 没有可用的播放上下文，跳过");
				return;
			}
			MusicService.RunAfterScan(() => Dispatcher.BeginInvoke(
				DispatcherPriority.ApplicationIdle, () => _ = RestoreAndRandomPlayAsync(ctx)));
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("启动自动播放调度失败: " + ex.Message);
		}
	}

	/// <summary>按播放上下文恢复列表数据源，从恢复出的列表里随机选一首自动播放。
	/// 本地/收藏/最近直接用内存数据；在线歌单走收藏夹缓存、没有缓存就异步拉一次
	/// （失败静默放弃）；搜索上下文按关键词重搜一次。随机挑而不是固定第一首：
	/// 每次开机的「开场曲」不同，续播感更自然。</summary>
	private async Task RestoreAndRandomPlayAsync(PlayContext ctx)
	{
		try
		{
			// 用户已经手动点了歌（或别的启动行为起了声）就退位：自动行为不抢人。
			if (MusicService.CurrentPath != null)
			{
				LogHelper.LogDebug("启动自动播放: 已有播放，跳过");
				return;
			}
			object? pick = ctx.Page switch
			{
				"library" => PickRandomLocal(),
				"fav" => PickRandomFrom(UserLibrary.Favorites),
				"recent" => PickRandomFrom(UserLibrary.RecentlyPlayed),
				"online" => await PickRandomOnlineAsync(ctx.ListKey),
				_ => null
			};
			if (pick == null)
			{
				LogHelper.LogDebug("启动自动播放: 列表为空或恢复失败(" + ctx.Page + "/" + ctx.ListKey + ")，不播");
				return;
			}
			// await 期间用户可能已经点了歌：真正开播前再挡一次。
			if (MusicService.CurrentPath != null)
			{
				LogHelper.LogDebug("启动自动播放: 等待期间用户已点歌，跳过");
				return;
			}
			switch (pick)
			{
				case string localPath:
					MusicService.Play(localPath);
					break;
				case SavedTrack fav:
					// onError 只记日志：自动播放失败不该在开机时弹 Toast。
					MusicService.PlayFavorite(fav, err => LogHelper.LogDebug("启动自动播放失败: " + err));
					break;
				case NeteaseTrack net:
					// 不传 onError：失败静默（日志由 MusicService 记）。
					MusicService.PlayOnline(net);
					break;
				default:
					await PlayStartupOnlineTrackAsync(pick);
					break;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("启动自动播放失败: " + ex.Message);
		}
	}

	/// <summary>本地曲库随机挑一首（跳过用户标记「跳过」的）。</summary>
	private static string? PickRandomLocal()
	{
		List<string> list = MusicService.KnownMusic
			.Where(f => !MusicService.IsSkipped(f))
			.ToList();
		return list.Count == 0 ? null : list[Random.Shared.Next(list.Count)];
	}

	private static SavedTrack? PickRandomFrom(List<SavedTrack> list)
	{
		return list.Count == 0 ? null : list[Random.Shared.Next(list.Count)];
	}

	/// <summary>恢复在线列表并随机挑一首。listKey 由在线页点播时登记：
	/// playlist:&lt;平台&gt;:&lt;歌单id&gt; / search:&lt;引擎&gt;:&lt;关键词&gt; / libfav。</summary>
	private static async Task<object?> PickRandomOnlineAsync(string listKey)
	{
		try
		{
			if (listKey.StartsWith("playlist:", StringComparison.Ordinal))
			{
				// 在线歌单：先查收藏夹缓存，没命中再异步拉一次（AccountLibraryService
				// 自带内存缓存）；拉取失败静默放弃，不弹错误 Toast。
				string[] parts = listKey.Split(':', 3);
				if (parts.Length < 3)
				{
					return null;
				}
				List<object>? tracks = AccountLibraryService.GetCachedTracks(parts[1], parts[2]);
				if (tracks == null || tracks.Count == 0)
				{
					var (status, fetched) = await AccountLibraryService.FetchPlaylistTracksAsync(parts[1], parts[2]);
					if (status != AccountPlaylistStatus.Ok || fetched.Count == 0)
					{
						LogHelper.LogDebug("启动自动播放: 歌单恢复失败 " + parts[1] + ":" + parts[2] + " status=" + status);
						return null;
					}
					tracks = fetched;
				}
				return tracks[Random.Shared.Next(tracks.Count)];
			}
			if (listKey.StartsWith("search:", StringComparison.Ordinal))
			{
				string[] parts = listKey.Split(':', 3);
				if (parts.Length < 3)
				{
					return null;
				}
				string engine = parts[1];
				string keyword = parts[2];
				List<object> results = engine switch
				{
					"netease" => (await NeteaseApiClient.SearchAsync(keyword, 30)).Cast<object>().ToList(),
					"qq" => (await QqClient.SearchAsync(keyword, 30)).Cast<object>().ToList(),
					"kugou" => (await KugouClient.SearchAsync(keyword, 30)).Cast<object>().ToList(),
					"bili" => (await BiliClient.SearchAsync(keyword)).Cast<object>().ToList(),
					_ => new List<object>()
				};
				return results.Count == 0 ? null : results[Random.Shared.Next(results.Count)];
			}
			if (listKey == "libfav")
			{
				// 音乐库收藏夹视图：直接用收藏夹的内存缓存，空了不拉网（开机不发大请求）。
				List<object> all = AccountLibraryService.AllTracks();
				return all.Count == 0 ? null : all[Random.Shared.Next(all.Count)];
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("启动自动播放: 在线列表恢复失败 " + listKey + " - " + ex.Message);
		}
		return null;
	}

	/// <summary>启动自动播放对 QQ/酷狗/B 站条目的开播：与在线页同一条
	/// 「下载 → 并入曲库 → 播」链路，但失败一律静默（只记 Debug 日志），
	/// 不弹 Toast —— 自动行为不该在开机时打扰人。</summary>
	private async Task PlayStartupOnlineTrackAsync(object item)
	{
		try
		{
			string? local = item switch
			{
				QqTrack qq => await QqClient.DownloadSongAsync(qq),
				KugouTrack kg => await KugouClient.DownloadSongAsync(kg),
				BiliTrack bili => await BiliClient.DownloadAudioAsync(bili, null),
				_ => null
			};
			if (string.IsNullOrEmpty(local))
			{
				LogHelper.LogDebug("启动自动播放: 在线取流失败，不播");
				return;
			}
			string display = item switch
			{
				QqTrack q2 => q2.Display,
				KugouTrack k2 => k2.Display,
				BiliTrack b2 => b2.Display,
				_ => ""
			};
			string imported = MusicService.ImportToLibrary(local, display);
			MusicService.RememberDownload(display, imported);
			// ToSavedTrack 可能映射不出元数据（未知条目形态）：PlayOnlineLocal 对
			// null meta 自带防御，直接透传即可。
			MusicService.PlayOnlineLocal(imported, OnlineMusicPage.ToSavedTrack(item), display);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("启动自动播放在线开播失败: " + ex.Message);
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
			// 关掉动画时不能直接 return：上一次淡出（收进托盘 / 关窗）留下的
			// HoldEnd 动画还挂在 Opacity 上，值被钉死在 0，这时候不给回来，
			// 窗口下次打开就是一整块全黑。见 ResetOpacity 的注释。
			ResetOpacity();
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
				// 窗口还藏着（收进托盘）时当然不播淡入，但必须把不透明度归位：
				// 这一趟「藏起来→再打开」就是靠这里把 Opacity 还原的，
				// 不还的话窗口重新可见时还是全黑。
				ResetOpacity();
				return;
			}
			// 先摘掉可能还挂着的旧动画再设 0，否则这句赋值会被旧的 HoldEnd 动画盖掉。
			BeginAnimation(OpacityProperty, null);
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
			ResetOpacity();
			LogHelper.LogError(ex, "播放启动动画失败");
		}
	}

	/// <summary>把窗口的淡入淡出动画彻底摘掉，并把不透明度归位到 1。
	///
	/// 关键在第一句。淡出用的 DoubleAnimation 默认 FillBehavior=HoldEnd：
	/// 它播完之后不会自己摘掉，动画时钟仍然挂在 Opacity 上并一直输出终值 0。
	/// 只要它还在，直接写 Opacity = 1.0 是不生效的（动画的优先级高于本地赋值），
	/// 窗口就永远停在 Opacity=0 —— 关掉再打开看到的就是一块全黑/全透明。
	/// 必须先 BeginAnimation(prop, null) 把动画摘干净，再赋值。
	/// 和上面 <see cref="SwapPageContentNow"/> 是同一个坑，只是页面那次的动画
	/// 用了 FillBehavior.Stop 才没出事，窗口这边漏了。</summary>
	private void ResetOpacity()
	{
		BeginAnimation(OpacityProperty, null);
		Opacity = 1.0;
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
					// 先把淡出动画摘干净并归位。只写 Opacity = 1.0 是不够的：
					// 这条 HoldEnd 动画还挂在属性上，一直把值钉在 0（见 ResetOpacity）。
					ResetOpacity();
					// 再按设置决定要不要补一次淡入
					if (SettingsManager.Instance.ShowStartupAnimation)
					{
						PlayStartupAnimation();
					}
				}
			};
			// 用 Stop 而不是默认的 HoldEnd：动画播完就自己摘掉，
			// 不会留下一条永远输出 0 的时钟把窗口钉死。
			o.FillBehavior = FillBehavior.Stop;
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
			// 淡出没播成也不能把窗口留在 Opacity=0：那样窗口还在任务栏里，
			// 点开却是一块全黑。归位后让它按正常逻辑关掉。
			ResetOpacity();
			_exitFaded = true;
			Close();
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
			AddNavItem("library", "\uE8B7");
			AddNavItem("fav", "\uEB51");
			AddNavItem("recent", "\uE81C");
			AddNavItem("online", "\uE721");
			// 「账号登录」钉在「关于」上面：各平台 Cookie/登录卡是常用入口，
			// 埋在关于页里太深。图标 E77B = 联系人，与「账号」语义一致。
			AddNavItem("account", "\uE77B");
			AddNavItem("about", "\uE946");
			AddBottomNavItem("settings", "\uE713");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "构建导航栏失败");
		}
	}

	private void AddNavItem(string key, string glyph)
	{
		NavList.Items.Add(BuildNavItem(key, glyph));
	}

	private void AddBottomNavItem(string key, string glyph)
	{
		NavBottomList.Items.Add(BuildNavItem(key, glyph));
	}

	private ListBoxItem BuildNavItem(string key, string glyph)
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
			// 这里放 Loc 的 key，不是翻译好的句子。
			// 存句子的话只有启动那一刻是新的，切语言时导航栏还是旧语言的字。
			// key 交给 RefreshNavLabels 去查，BuildNavItem 只管长相。
			Tag = "nav_" + key,
			FontSize = 13.5,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
		panel.Children.Add(label);
		var item = new ListBoxItem { Tag = key, Content = panel };
		RefreshNavLabel(item);
		return item;
	}

	private static void RefreshNavLabel(ListBoxItem item)
	{
		if (item.Content is not Panel panel || panel.Children.Count < 2)
		{
			return;
		}
		if (panel.Children[1] is TextBlock { Tag: string locKey } label)
		{
			label.Text = Loc.Default[locKey];
		}
	}

	/// <summary>切语言后重刷左右两串导航的文字。</summary>
	private void RefreshNavLabels()
	{
		try
		{
			foreach (object o in NavList.Items)
			{
				if (o is ListBoxItem li)
				{
					RefreshNavLabel(li);
				}
			}
			foreach (object o in NavBottomList.Items)
			{
				if (o is ListBoxItem li)
				{
					RefreshNavLabel(li);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "刷新导航栏文字失败");
		}
	}

	/// <summary>界面语言换了：把代码里赋过值的字重刷一遍。
	/// 导航栏的 label 存的是 Loc 的 key（见 BuildNavItem），
	/// 这里按新语言重新查一次。</summary>
	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		try
		{
			RefreshNavLabels();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "界面语言切换后刷新主窗口文案失败");
		}
	}

	public void ApplyBackground()
	{
		try
		{
			var s = SettingsManager.Instance;
			bool wantVideo = string.Equals(s.BackgroundType, "video", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(s.BackgroundPath) && File.Exists(s.BackgroundPath);
			// 当前曲目本身就是视频（音乐库里的 mp4）：不管背景设置的是图片还是视频，
			// 都优先把这段视频铺到背景上。声音仍由 NAudio 播它的音轨，BgVideo 静音。
			string? current = MusicService.CurrentPath;
			bool trackVideo = IsBgVideoPath(current) && File.Exists(current!);
			if (wantVideo)
			{
				// 视频背景交给 SyncBackgroundVideo 统一处理（单曲跟播 / 目录轮播），
				// 这里只负责把图片侧收干净，避免两条路径互相打架。
				// 例外：当前已经在跟播这段曲目视频、源也对得上，就别 Stop 了 ——
				// Stop 会清掉 Source，紧接着 SyncBackgroundVideo 又重挂一遍，
				// 一次重挂就是一次解码器重建，画面上看是黑闪 + 顿一下。
				bool keepTrackVideo = _bgVideoFromTrack && trackVideo
					&& string.Equals(_appliedBgVideoPath, current, StringComparison.OrdinalIgnoreCase);
				if (!keepTrackVideo)
				{
					StopBgVideo();
				}
				ClearStillBackground();
				SyncBackgroundVideo();
			}
			else if (trackVideo)
			{
				ClearStillBackground();
				SyncBackgroundVideo();
			}
			else
			{
				StopBgVideo();
				ShowStillBackground(s);
			}

			ApplyBackgroundChrome(s);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "应用背景失败");
		}
	}

	/// <summary>把图片侧收干净：视频要占背景时不光隐藏 Image，还把 Source 置空
	/// （一张 1920 背景图解码后就是 8MB，留着不用纯属浪费）。</summary>
	private void ClearStillBackground()
	{
		try
		{
			BgImage.BeginAnimation(UIElement.OpacityProperty, null);
			BgImage.Visibility = Visibility.Collapsed;
			BgImage.Source = null;
			BgImage.Effect = null;
		}
		catch
		{
		}
	}

	/// <summary>按设置铺静态背景图（没图时清空）。视频侧由调用方先 StopBgVideo。</summary>
	private void ShowStillBackground(AppSettings s)
	{
		BitmapSource? bgImage = LoadBackgroundImage(s.BackgroundPath);
		double bgOpacity = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
		BgImage.BeginAnimation(UIElement.OpacityProperty, null);
		BgImage.Visibility = Visibility.Visible;
		BgImage.Source = bgImage;
		BgImage.Opacity = bgImage != null ? bgOpacity : 1.0;
		// 背景模糊：已把低内存作为默认基线，背景不再挂高斯模糊
		// （模糊会在 GPU 上留一份整屏尺寸的渲染目标，是背景最占内存的一环）。
		// 视觉层次靠背景透明度 / 亮度替代，内存占用明显下降。
		double blur = 0.0;
		BgImage.Effect = blur > 0.0
			? new BlurEffect
			{
				Radius = blur,
				KernelType = KernelType.Gaussian,
				RenderingBias = RenderingBias.Performance
			}
			: null;
	}

	/// <summary>卡片蒙版 + 压暗层：跟着「现在背景到底在放什么」走。
	/// 图片侧/视频侧切换、曲目视频跟播进出都要重算，所以单独拆出来。</summary>
	private void ApplyBackgroundChrome(AppSettings s)
	{
		try
		{
			// 卡片蒙版按 MhyunBox 的规则分两种（见 MhyunBox\MainWindow.cs:420-450）：
			//   视频背景 -> 卡片换成 75% 不透明近黑 Argb(192,14,14,14)，视频被刻意压暗当动态纹理
			//   图片/无背景 -> 卡片保持 WinUI 正常的「半透明白 5%」，图片要看得清
			// 浅色主题下文字是深色，这两步都得反过来：视频卡片压成半透明白，普通卡片用近白。
			bool videoActive = BgVideo.Visibility == Visibility.Visible;
			bool imageActive = !videoActive
				&& BgImage.Visibility == Visibility.Visible
				&& BgImage.Source != null;
			BgCardMask.Background = videoActive
				? ThemeManager.VideoCardMaskBrush()
				: ThemeManager.CardFill();

			// 「背景明暗」是 Music_Box 自己多出来的旋钮，压的是背景素材本身：
			//   有图 -> 图压暗，字和卡片不受影响
			//   视频 -> 卡片蒙版已经在压暗了，再叠一层会把画面压到全黑，所以不重复压
			//   没图 -> 什么都不压。这一条以前是漏的：没选背景图时这层黑还照铺，
			//          压在本来就黑的页面上，于是「拉一下背景明暗，整个界面全黑」。
			//          这个旋钮叫「背景」，就不该动到没有背景时的界面。
			// 浅色主题里这个旋钮改成把背景「洗白」，否则深色字压在一张深色照片上依旧读不出来。
			bool dimTargetExists = imageActive || videoActive;
			double darkness = videoActive ? 0.0 : Math.Clamp(s.BackgroundDarkness, 0.0, 0.95);
			byte wash = (byte)Math.Round(darkness * 255.0);
			BgDark.Visibility = dimTargetExists ? Visibility.Visible : Visibility.Collapsed;
			BgDark.Background = ThemeManager.IsDark
				? new SolidColorBrush(Color.FromArgb(wash, 0x0F, 0x0F, 0x0F))
				: new SolidColorBrush(Color.FromArgb(wash, 0xFF, 0xFF, 0xFF));
			ApplyBgMotionState();
		}
		catch
		{
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
					SyncTrackVideoPlayback();
					SyncSmtcTrack();
					TrackRows.SetPlayingTrack(MusicService.CurrentPath, MusicService.BuildCurrentTrack()?.Key);
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
				// 音乐侧没东西可显时，把正在播的背景视频顶上媒体卡（名称去后缀、下行路径）
				UpdateSmtcForBackgroundVideo();
				return;
			}
			SavedTrack? track = MusicService.BuildCurrentTrack();
			if (track == null)
			{
				SmtcService.ClearTrack();
				UpdateSmtcForBackgroundVideo();
				TrayIconService.SetNowPlaying(null);
				return;
			}
			string? cover = track.Cover;
			// 「显示封面图片」总闸关闭：SMTC 缩略图一并停（SetTrack 收到 null
			// 会自动退回程序图标），不给它读磁盘缓存/喂位图的机会。
			if (!SettingsManager.Instance.ShowListCovers)
			{
				cover = null;
			}
			// 网络封面是 URL，SMTC 只吃本地文件：优先复用封面磁盘缓存
			//（行显示过就有），没缓存过才置空 —— SetTrack 会退回程序图标补位。
			else if (!string.IsNullOrWhiteSpace(cover)
				&& cover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				cover = CoverLoader.GetCachedFilePath(cover);
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

	/// <summary>
	/// 背景视频（自定义/目录轮播）在播、而音乐侧没有曲目可显时，
	/// 把当前视频作为媒体内容报给系统媒体卡：上行文件名（去后缀）、下行完整路径。
	/// 音乐一起播，SyncSmtcTrack 会自动接管卡片，这里不用做让位处理。
	/// </summary>
	private void UpdateSmtcForBackgroundVideo()
	{
		try
		{
			if (!SmtcService.IsAttached)
			{
				return;
			}
			if (BgVideo.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(_appliedBgVideoPath))
			{
				return;
			}
			// 音乐侧还有曲可显（在播，或暂停但已走到半路）就让音乐占卡
			string? musicPath = MusicService.CurrentPath;
			bool musicOwnsCard = !string.IsNullOrWhiteSpace(musicPath)
				&& (MusicService.IsPlaying || MusicService.Position > TimeSpan.Zero);
			if (musicOwnsCard)
			{
				return;
			}
			SmtcService.SetVideoTrack(_appliedBgVideoPath);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步背景视频到系统媒体卡失败");
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
		catch (Exception ex)
		{
			// 之前这里静默吞掉：SMTC 状态同步挂了的话浮层没卡片，日志里却一行都没有，
			// 根本无从排查。留痕不抛 —— 进度同步失败不该打断播放。
			LogHelper.LogError(ex, "同步 SMTC 播放进度失败");
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
			// 背景只用作铺底，没必要按原图分辨率解码：
			// 封顶降低常驻位图内存（低内存已作为默认基线，统一压到 1280）。
			bi.DecodePixelWidth = 1280;
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
	/// 视频跟播：当前曲目本身是视频就直接拿它当背景（音乐库里的 mp4 走这条）；
	/// 否则按播放模式轮播背景视频目录。
	/// 音频始终由 NAudio 走，这里只负责画面：曲目视频时 BgVideo 强制静音，
	/// 目录轮播时用设置里的「视频背景音量」。
	/// </summary>
	private void SyncBackgroundVideo()
	{
		try
		{
			var s = SettingsManager.Instance;
			string? current = MusicService.CurrentPath;
			bool trackVideo = IsBgVideoPath(current) && File.Exists(current!);
			bool videoBg = string.Equals(s.BackgroundType, "video", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(s.BackgroundPath) && File.Exists(s.BackgroundPath);

			if (trackVideo)
			{
				// 当前播放的就是视频文件：画面优先，不管背景设置选的是图片还是视频。
				_bgVideoFromTrack = true;
				PlayBgVideo(current!, loopPlaylist: false, mute: true);
				ApplyBackgroundChrome(s);
				return;
			}

			if (_bgVideoFromTrack)
			{
				// 从「视频曲目」切回普通音频：撤掉跟播画面，恢复用户自己的背景。
				_bgVideoFromTrack = false;
				StopBgVideo();
				if (!videoBg)
				{
					ShowStillBackground(s);
					ApplyBackgroundChrome(s);
					return;
				}
			}

			if (!videoBg)
			{
				if (BgVideo.Visibility == Visibility.Visible)
				{
					StopBgVideo();
					ApplyBackgroundChrome(s);
				}
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
				ApplyBackgroundChrome(s);
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

	/// <summary>曲目视频跟播时，画面跟着播放器启停：暂停音乐画面也停在那一帧，
	/// 继续播时画面接着走。背景目录轮播（独立装饰）不受影响，一直循环。
	///
	/// 只在该变状态时才动 MediaElement：重复 Play()/Pause() 会让 EVR 重新
	/// 协商一次时钟，表现就是播着播着顿一下（每次状态刷新都会中招）。
	/// WPF 的 MediaElement 不暴露播放状态，所以自己拿 _bgVideoPlaying 记。</summary>
	private void SyncTrackVideoPlayback()
	{
		if (!_bgVideoFromTrack || BgVideo.Visibility != Visibility.Visible)
		{
			return;
		}
		try
		{
			if (MusicService.IsPlaying)
			{
				if (!_bgVideoPlaying)
				{
					// 先补一次进度再起播：视频比音频晚加载，不补就一直落后。
					SeekVideoToAudio();
					BgVideo.Play();
					_bgVideoPlaying = true;
				}
			}
			else if (_bgVideoPlaying)
			{
				BgVideo.Pause();
				_bgVideoPlaying = false;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("[背景视频] 同步启停失败: " + ex.Message);
		}
	}

	/// <summary>音频起播前的预载：只要当前要播的是视频，就把 Source 先挂上。
	/// MediaElement 一拿到 Source 就开始在后台打开文件、初始化解码器，
	/// 这几百毫秒正好和 NAudio 起播并行 —— 不预载就只能等 StateChanged
	/// 回来再挂，画面必然比声音慢半拍。</summary>
	private void PrepareTrackVideo(string? path)
	{
		if (path == null || !IsBgVideoPath(path) || !File.Exists(path))
		{
			return;
		}
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(() => PrepareTrackVideo(path)));
			return;
		}
		try
		{
			if (string.Equals(_appliedBgVideoPath, path, StringComparison.OrdinalIgnoreCase))
			{
				// 同一首 mp4 被再次点播：不重设 Source（省一次打开），但音频是从 0
				// 重新开始的，画面必须一起归零。否则画面停在上一轮进度上，
				// _bgVideoPlaying 又是 true，同步逻辑会认为「已经在播」而不再对齐，
				// 只能等 700ms 的定时器按 0.45s 容差追——起播瞬间就有明显偏差。
				ResetTrackVideoAlignment();
				return;
			}
			_appliedBgVideoPath = path;
			_bgIndex = BgVideoPlaylist.FindIndex(p =>
				string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
			BgImage.Visibility = Visibility.Collapsed;
			BgImage.Source = null;
			// 新 Source 还没 MediaOpened，旧的「在播」状态必须作废，
			// 否则 SyncTrackVideoPlayback 会跳过 seek/Play。
			_bgVideoPlaying = false;
			_trackVideoAlignedAtTick = Environment.TickCount64;
			// 预载阶段先透明：等真正起播时再淡入，避免先闪一张黑底。
			BgVideo.BeginAnimation(UIElement.OpacityProperty, null);
			BgVideo.Opacity = 0.0;
			BgVideo.Visibility = Visibility.Visible;
			BgVideo.Source = new Uri(path);
			ApplyBgVideoVolume();
			LogHelper.LogDebug("[背景视频] 预载 " + Path.GetFileName(path));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "预载背景视频失败");
		}
	}

	/// <summary>重播同一段曲目视频：音频回到 0，画面跟着归零并作废「在播」标记，
	/// 让下一次同步重新 seek + Play。</summary>
	private void ResetTrackVideoAlignment()
	{
		_bgVideoPlaying = false;
		_trackVideoAlignedAtTick = Environment.TickCount64;
		try
		{
			BgVideo.Position = TimeSpan.Zero;
		}
		catch (Exception ex)
		{
			// 还没 MediaOpened 时设 Position 会抛，交给 MediaOpened 补。
			LogHelper.LogDebug("[背景视频] 重播归零失败: " + ex.Message);
		}
	}

	/// <summary>把画面拉到音频当前进度。视频从「设 Source」到「出首帧」要几百毫秒，
	/// 这段时间音频已经跑掉了，不补这一下就是永久的音画不同步。
	/// 容差内不动：每次 seek 都要重建一次渲染管线，抖一下就 seek 反而更卡。</summary>
	private void SeekVideoToAudio(double toleranceSeconds = 0.12)
	{
		if (!_bgVideoFromTrack)
		{
			return;
		}
		try
		{
			TimeSpan audio = MusicService.Position;
			if (audio < TimeSpan.Zero)
			{
				return;
			}
			TimeSpan video = BgVideo.Position;
			if (audio <= TimeSpan.FromMilliseconds(20))
			{
				// 音频刚归零（重播同一首 / 刚要起播）：原来这里直接 return，
				// 结果画面停在上一轮进度上没人管。此时把画面归零即可，
				// 不要加追赶提前量——音频本身就在 0，加了反而让画面前跳。
				if (Math.Abs(video.TotalSeconds) > toleranceSeconds)
				{
					BgVideo.Position = TimeSpan.Zero;
				}
				return;
			}
			double drift = (video - audio).TotalSeconds;
			if (Math.Abs(drift) > toleranceSeconds)
			{
				// 带追赶提前量：设完 Position 到真把帧摆上屏还有一段
				// 解码/渲染延迟，不补的话画面起播就落后。
				BgVideo.Position = audio + TimeSpan.FromSeconds(VideoCatchUpLeadSeconds);
			}
		}
		catch (Exception ex)
		{
			// 还没 MediaOpened 时设 Position 会抛，交给 MediaOpened 补。
			LogHelper.LogDebug("[背景视频] 对齐画面失败: " + ex.Message);
		}
	}

	// ---- 音画校正参数（都是秒）----
	// 追赶提前量：设 Position 之后 MediaElement 还要解码 + 渲染才把新帧摆上屏，
	// 4K 上这一步一两百毫秒。校正时把视频放到「音频 + 提前量」，
	// 等它渲染出来正好对上；不加的话每次校正完立刻又落后，
	// 定时器就只能反复追，表现就是「音乐总比画面快半拍」。
	private const double VideoCatchUpLeadSeconds = 0.12;
	// 非对称容差：视频落后 0.2s 就追——人耳对「声音先到」远比对
	// 「画面先到」敏感；视频超前 0.45s 才拽回去，画面等声音不刺眼，
	// 容差给大些免得 seek 太勤把 4K 播成一卡一卡的幻灯片。
	private const double VideoBehindToleranceSeconds = 0.20;
	private const double VideoAheadToleranceSeconds = 0.45;
	// 起播后的紧校正窗口：起播是最容易留下固定偏差的时刻（解码器预热、音频已先行），
	// 前 3 秒用更紧的容差，把这口气追平后就回到宽松阈值，避免持续 seek 造成卡顿。
	private const int StartupAlignWindowMs = 3000;
	private const double StartupToleranceSeconds = 0.12;

	/// <summary>最近一次「给视频挂新 Source / 重播归零 / MediaOpened」的时刻，
	/// 用于判断是否还在起播的紧校正窗口内。</summary>
	private long _trackVideoAlignedAtTick;

	/// <summary>视频文件打开完成：解码器就绪，这时候才能安全 seek，
	/// 把画面一次对齐到音频进度上（预载到出首帧期间音频已经跑了一段）。</summary>
	private void BgVideo_MediaOpened(object? sender, RoutedEventArgs e)
	{
		try
		{
			// 解码器就绪这一刻才开始能可靠 seek，紧校正窗口也从这里重新计时。
			_trackVideoAlignedAtTick = Environment.TickCount64;
			SeekVideoToAudio(0.05);
			// 预载时如果音乐已经被暂停（用户在加载途中按了暂停），
			// 这里就只对齐画面、不要抢着播。
			if (MusicService.IsPlaying)
			{
				BgVideo.Play();
				_bgVideoPlaying = true;
			}
			else
			{
				// 没在播就明确标记「未在播」，否则切歌后这个状态位会停在旧值，
				// 起播时 SyncTrackVideoPlayback 会误以为画面已经在走。
				_bgVideoPlaying = false;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("[背景视频] MediaOpened 对齐失败: " + ex.Message);
		}
	}

	/// <summary>定期校正：音频和视频各跑各的时钟，久了必然漂（尤其 4K 掉帧时）。
	/// 非对称容差 + 追赶提前量：视频落后一点点就带提前量追上去，
	/// 超前则放得更宽，避免校正本身变成卡顿源。</summary>
	private void VideoSyncTick(object? sender, EventArgs e)
	{
		if (!_bgVideoFromTrack || BgVideo.Visibility != Visibility.Visible || !MusicService.IsPlaying)
		{
			return;
		}
		// 切歌的那一瞬间：音频已经归零，视频还停在上一首的进度上。
		// 这时去校正就是拿新歌的 0 秒去拽旧画面，白跳一次（画面会抖）。
		// 只认「当前曲目就是这段视频」的情况，其余交给 SyncBackgroundVideo。
		string? cur = MusicService.CurrentPath;
		if (cur == null || !string.Equals(cur, _appliedBgVideoPath, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		try
		{
			// 起播后 3 秒内收紧容差：这段窗口内留下的偏差会一路带到整首，
			// 而窗口之后又回到宽松阈值，避免反复 seek 把画面拖卡。
			bool startup = Environment.TickCount64 - _trackVideoAlignedAtTick <= StartupAlignWindowMs;
			double behindTolerance = startup ? StartupToleranceSeconds : VideoBehindToleranceSeconds;
			double aheadTolerance = startup ? StartupToleranceSeconds : VideoAheadToleranceSeconds;
			if (!_bgVideoPlaying)
			{
				// 状态位说没在播（换源后 / 重播后 / 用户暂停后再恢复），
				// 先对齐再起播；只 Play 不 seek 会让画面从 0 开始追。
				SeekVideoToAudio(startup ? 0.05 : 0.12);
				BgVideo.Play();
				_bgVideoPlaying = true;
				return;
			}
			double drift = (BgVideo.Position - MusicService.Position).TotalSeconds;
			if (drift < -behindTolerance)
			{
				// 视频落后（音乐快了）：追到音频前面一点，抵消解码/渲染延迟。
				BgVideo.Position = MusicService.Position + TimeSpan.FromSeconds(VideoCatchUpLeadSeconds);
				LogHelper.LogDiag($"[音画] 视频落后 {-drift:F2}s，已带提前量校正");
			}
			else if (drift > aheadTolerance)
			{
				// 视频超前（画面等声音）：直接对齐即可。
				BgVideo.Position = MusicService.Position;
				LogHelper.LogDiag($"[音画] 视频超前 {drift:F2}s，已校正");
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("[背景视频] 音画校正失败: " + ex.Message);
		}
	}

	private System.Windows.Threading.DispatcherTimer? _videoSyncTimer;

	/// <summary>BgVideo 当前是否处于「我们在播」的状态。WPF 的 MediaElement
	/// 没有 CurrentState，用它挡住重复 Play/Pause（重复调用会顿一下）。</summary>
	private bool _bgVideoPlaying;

	/// <summary>换背景视频。真正挂 Source 的是 <see cref="SwapBgVideo"/>，
	/// 这里只负责「要不要先淡出旧画面」这一层：
	/// 上一段还亮着就直接换 Source，会看到上一帧硬切到新视频第一帧，
	/// 尤其从 4K 慢镜头切到快节奏时特别跳。先 170ms 淡出（缓入），
	/// 换源后再 420ms 淡入（缓出），两段加起来不到 0.6 秒，观感是「化」过去的。</summary>
	private void PlayBgVideo(string path, bool loopPlaylist, bool mute = false)
	{
		try
		{
			bool changed = !string.Equals(_appliedBgVideoPath, path, StringComparison.OrdinalIgnoreCase);
			if (changed && BgVideo.Visibility == Visibility.Visible && BgVideo.Opacity > 0.02)
			{
				// 淡出期间又切了下一段：老回调靠代数作废，只让最后一次落地。
				int gen = ++_bgSwapGen;
				var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(170))
				{
					EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
				};
				fadeOut.Completed += (_, _) =>
				{
					if (gen != _bgSwapGen)
					{
						return;
					}
					SwapBgVideo(path, loopPlaylist, mute);
				};
				BgVideo.BeginAnimation(UIElement.OpacityProperty, fadeOut);
				return;
			}
			++_bgSwapGen;
			SwapBgVideo(path, loopPlaylist, mute);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放背景视频失败");
		}
	}

	/// <summary>换源 + 淡入 + 起播。淡出由 PlayBgVideo 负责，这里只管后半程。</summary>
	private void SwapBgVideo(string path, bool loopPlaylist, bool mute)
	{
		try
		{
			_bgLoopPlaylist = loopPlaylist;
			_bgVideoMuted = mute;
			bool changed = !string.Equals(_appliedBgVideoPath, path, StringComparison.OrdinalIgnoreCase);
			double bgOpacity = Math.Clamp(SettingsManager.Instance.BackgroundOpacity, 0.0, 1.0);
			if (changed)
			{
				_appliedBgVideoPath = path;
				_bgIndex = BgVideoPlaylist.FindIndex(p =>
					string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
				BgImage.Visibility = Visibility.Collapsed;
				BgImage.Source = null;
				// 新 Source 尚未打开，旧的「在播」状态必须作废；
				// 否则下面的对齐/Play 判断会以为画面已经在走。
				_bgVideoPlaying = false;
				_trackVideoAlignedAtTick = Environment.TickCount64;
				// 先摘掉淡出动画再换源：留着它的话新画面一出来就被拉到 0，再也回不来。
				BgVideo.BeginAnimation(UIElement.OpacityProperty, null);
				BgVideo.Opacity = 0.0;
				BgVideo.Visibility = Visibility.Visible;
				BgVideo.Source = new Uri(path);
				// 目录轮播用设置里的「视频背景音量」；曲目视频必须静音——
				// 它的声音已经由 NAudio 播出，MediaElement 再放一遍就是双份。
				ApplyBgVideoVolume();
				// 有底图时卡片要当蒙版用：MhyunBox 是 Color.FromArgb(192,14,14,14)（深色主题），
				// 这样即使底图是纯白，卡片里的白字依然读得出来。
				BgCardMask.Background = new SolidColorBrush(Color.FromArgb(192, 0x0E, 0x0E, 0x0E));
				BgVideo.BeginAnimation(UIElement.OpacityProperty,
					new DoubleAnimation(bgOpacity, TimeSpan.FromMilliseconds(420))
					{
						From = 0.0,
						EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
					});
			}
			else
			{
				// 同一段视频被重复「播放」（每次状态刷新都会走到这里）：
				// 画面已经亮着就一个字都别动，从 0 重启淡入会看到反复闪一下。
				ApplyBgVideoVolume();
				if (BgVideo.Opacity < bgOpacity - 0.01)
				{
					BgVideo.BeginAnimation(UIElement.OpacityProperty,
						new DoubleAnimation(bgOpacity, TimeSpan.FromMilliseconds(420)));
				}
				else if (BgVideo.HasAnimatedProperties == false && Math.Abs(BgVideo.Opacity - bgOpacity) > 0.01)
				{
					BgVideo.Opacity = bgOpacity;
				}
			}
			// 起播前先把画面拉到音频进度：预载再快也会差几十上百毫秒。
			SeekVideoToAudio();
			BgVideo.Play();
			_bgVideoPlaying = true;
			ApplyBackgroundChrome(SettingsManager.Instance);
			// 媒体卡：背景视频轮播在播、音乐侧没曲目可显时让视频上卡。
			// 曲目视频跟播（mute=true）的卡已由 SyncSmtcTrack 用曲目信息处理，跳过。
			if (!mute)
			{
				UpdateSmtcForBackgroundVideo();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "切换到背景视频失败");
		}
	}

	/// <summary>换片代数：淡出动画是异步收尾的，连着切歌时用它对掉过期的回调。</summary>
	private int _bgSwapGen;

	private void StopBgVideo()
	{
		try
		{
			_bgLoopPlaylist = false;
			_bgVideoMuted = false;
			_appliedBgVideoPath = null;
			_bgVideoPlaying = false;
			BgVideo.BeginAnimation(UIElement.OpacityProperty, null);
			BgVideo.Stop();
			BgVideo.Source = null;
			BgVideo.Visibility = Visibility.Collapsed;
			// 视频没了，卡上若还是这段视频就一并清掉（音乐占卡时内部会跳过）
			SmtcService.ClearVideoTrack();
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("[背景视频] 停止失败: " + ex.Message);
		}
	}

	private bool _bgLoopPlaylist;

	/// <summary>true = 现在铺的是「当前曲目本身就是视频」的跟播画面。
	/// 切回普通音频时靠它判断要不要把静态背景图重新铺回来。</summary>
	private bool _bgVideoFromTrack;

	/// <summary>BgVideo 是否静音。曲目视频跟播时为 true：声音由 NAudio 播，
	/// MediaElement 再放一份就是双声；背景目录轮播时按设置里的音量出声。</summary>
	private bool _bgVideoMuted;

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
			// 曲目视频：停在末帧，等音频自己走完（两者时长一致，通常几乎同时结束）。
			// 这里跳回 0 重播的话，最后那一小段会看到画面突然闪回开头。
			if (_bgVideoFromTrack)
			{
				_bgVideoPlaying = false;
				return;
			}
			// 单段背景视频：原地循环
			BgVideo.Position = TimeSpan.Zero;
			BgVideo.Play();
			_bgVideoPlaying = true;
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
		// 评论抽屉是挂在窗体上的全局层，不随页面走：换页就收，
		// 否则它会飘在新页面上、评论对象还是上一页那首歌。
		CloseComments();
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

	/// <summary>取（必要时先建）在线音乐页实例，不跳页。
	/// 音乐库「收藏夹」要复用它的行构建与播放/下载链路（BuildLibraryRow /
	/// PlayExternal / DownloadExternalAsync），但用户可能一次都没进过在线页，
	/// 页面还没被创建 —— 这里有需求就先建出来。原来这段逻辑内联在
	/// OpenPlaylistInOnlinePage 里，收藏夹复用后提成公共方法。</summary>
	public OnlineMusicPage? EnsureOnlinePage()
	{
		if (_pages.TryGetValue("online", out UserControl? page) && page is OnlineMusicPage omp)
		{
			return omp;
		}
		page = CreatePage("online");
		if (page == null)
		{
			return null;
		}
		_pages["online"] = page;
		return (OnlineMusicPage)page;
	}

	/// <summary>账号页收藏夹点开某个歌单后走这里：把整份曲目灌进在线搜索页的结果列表，
	/// 再跳到在线页展示。NavigateTo 是私有的，页面内部要跳转只能借这个公开入口，
	/// 顺便把「先备好数据再导航」的顺序也封装住——在线页 OnNavigatedTo 里的
	/// 随机自动搜索已经被 ShowExternalPlaylist 置了 _autoSearched 挡掉，不会冲掉歌单。
	/// playlistId 一并传下去：播放上下文要靠「平台+歌单 id」才能在启动时找回这份歌单。</summary>
	public void OpenPlaylistInOnlinePage(string engine, string playlistName, List<object> tracks, string? playlistId = null)
	{
		OnlineMusicPage? omp = EnsureOnlinePage();
		if (omp == null)
		{
			return;
		}
		omp.ShowExternalPlaylist(engine, playlistName, tracks, playlistId);
		NavigateTo("online");
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
			"account" => new AccountPage(),
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
			// 先从事件源（鼠标底下最深的那个元素）沿祖先链找最近的
			// 可滚 ScrollViewer——这正是 WPF 自己滚轮语义的来源，
			// 不依赖任何矩形几何，压着第几行就滚哪棵树，最稳。
			// 找不到（源在 Popup 里、或祖先链断了）再退回矩形命中。
			ScrollViewer? sv = FindScrollFromSource(e.OriginalSource as DependencyObject)
				?? FindScrollViewerUnderCursor(root, e.GetPosition(this));
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
	/// 再把事件吃掉，那样表现出来就是滚轮完全失灵。
	///
	/// 视觉树只走一遍，之后复用缓存。这里原来每来一个滚轮刻度就把整棵
	/// 视觉树递归走一遍：搜索页那棵树里有一屏（几十行）× 每行十几个元素，
	/// 加上评论区、候选源列表、两个导航栏，几千个 Visual 要走几千次
	/// GetChild。滚轮一秒钟能出十几个刻度，于是每秒几万次遍历全花在
	/// 「鼠标压在哪个滚动条上」这一个问题上 —— 表现就是滚轮明显发涩、
	/// 行数越多越卡。
	///
	/// 缓存什么时候失效靠 <see cref="LayoutUpdated"/> 置脏：布局真变了
	/// （换页、评论区展开、列表增删行）才会重走，纯滚动不触发布局，
	/// 于是一整串滚轮刻度只走一次树。</summary>
	private ScrollViewer? FindScrollViewerUnderCursor(DependencyObject root, Point mousePos)
	{
		if (!ReferenceEquals(_svCacheRoot, root))
		{
			_svCache.Clear();
			_svCacheRoot = root;
			_svCacheDirty = true;
		}
		if (_svCacheDirty)
		{
			_svCache.Clear();
			WalkScrollViewers(root, sv => _svCache.Add(sv));
			_svCacheDirty = false;
		}

		ScrollViewer? under = null;
		double underArea = double.MaxValue;
		foreach (ScrollViewer sv in _svCache)
		{
			if (sv.ScrollableHeight <= 0.0)
			{
				continue;
			}
			// RenderSize 为 0 说明这一帧还没量过布局（页面刚换、或者
			// ScrollViewer 是 Collapsed）。这时候算出来的矩形是空的，
			// 命中测试必然失败 —— 所以必须先挡掉，不能拿空矩形去 Contains。
			Size size = sv.RenderSize;
			if (size.Width <= 0 || size.Height <= 0)
			{
				continue;
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
				continue;
			}
			Rect r = new Rect(origin, size);
			if (!r.Contains(mousePos))
			{
				continue;
			}
			double area = r.Width * r.Height;
			if (under == null || area <= underArea)
			{
				under = sv;
				underArea = area;
			}
		}
		return under;
	}

	// 滚轮命中用的 ScrollViewer 缓存，见 FindScrollViewerUnderCursor 的注释。
	private readonly List<ScrollViewer> _svCache = new List<ScrollViewer>();
	private DependencyObject? _svCacheRoot;
	private bool _svCacheDirty = true;

	/// <summary>从滚轮事件的源元素沿视觉树向上，找最近的「真能滚」的
	/// ScrollViewer（ScrollableHeight&gt;0 且已布局）。找不到返回 null。
	///
	/// 相比纯矩形命中，这条路径不看坐标：鼠标压着哪一行，源就是那行里的
	/// 元素，向上走必然穿过它所属的那棵树——所以「压着列表中部滚轮没反应」
	/// 这类几何命中漏判（缓存陈旧、矩形不重叠、遮挡层）在这里不存在。
	/// 内层还有滚动条但当前滚不动的（例如行内某个区域）会继续往上走，
	/// 由外层列表接管，和原生滚轮语义一致。</summary>
	private static ScrollViewer? FindScrollFromSource(DependencyObject? source)
	{
		DependencyObject? node = source;
		int guard = 0;
		while (node != null && guard++ < 80)
		{
			if (node is ScrollViewer sv)
			{
				Size size = sv.RenderSize;
				if (sv.ScrollableHeight > 0.0 && size.Width > 0 && size.Height > 0 && sv.IsVisible)
				{
					return sv;
				}
			}
			DependencyObject? next = null;
			if (node is Visual)
			{
				try
				{
					next = VisualTreeHelper.GetParent(node);
				}
				catch (InvalidOperationException)
				{
					next = null;
				}
			}
			if (next == null)
			{
				// 内容元素（Run 等非 Visual）走逻辑树；逻辑树也没有就到头了。
				next = node is ContentElement ? LogicalTreeHelper.GetParent(node) : null;
			}
			node = next;
		}
		return null;
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

	public void UpdateMiniAll()
	{
		UpdateMiniTrack();
		UpdatePlayGlyph();
		UpdateProgressUi();
		UpdateMiniFav();
		UpdateMiniOrder();
	}

	// ===== 底栏标题走马灯 =====
	// 标题被 MaxWidth 截断（见 ClampMiniTitle）时启动唯一一种效果：
	// 整行文本以恒速持续左移，尾部快走出左缘时，头部副本从右缘重新
	// 冒出来（与尾部之间隔一段空白），循环往复、无停顿无空窗。
	// 实现靠裁剪容器里的两个文本副本（MiniTrackText / MiniTrackText2）：
	// 副本 2 恒定平移到「主文本位置 + 周期」，两者同步左移；位移滑过
	// 一整个周期（自然宽 + 间隔）后回卷，副本 2 恰好补到主文本原位，
	// 画面逐像素一致、看不到接缝。
	// 60fps 走 DispatcherTimer(16ms)，持续滚动不做暂停；
	// 标题变短立即复位停止。
	private DispatcherTimer? _titleMarqueeTimer;

	/// <summary>滚动周期像素数：标题自然宽 + 裁剪窗宽（0 = 不需要滚动）。</summary>
	private double _titleMarqueeCycle;

	/// <summary>恒定滚动速度：30px/s。太慢显得卡，太快读不完一屏。</summary>
	private const double TitleMarqueeSpeed = 30.0;

	/// <summary>底栏曲名宽度钳制：曲名与复制键同处一个水平 StackPanel
	/// （键紧贴曲名尾部，左间距 8px，见 XAML）。StackPanel 会给曲名
	/// 「无限宽」，不钳的话长曲名会把复制键顶进右侧时间区。可用宽度
	/// 从外层 MiniTitleDock（DockPanel，宽度只随窗口走、与曲名内容无关）
	/// 反推：DockPanel 宽 - 右侧停靠的时间/来源区（含左间距、取流提示
	/// 显示时再叠加）- 复制键占位 - 恒留 12px 安全间距。
	/// 注意不能拿 MiniTitleRow 自己的 ActualWidth 当基准——行宽就是
	/// 「文本按上次 MaxWidth 钳制后的宽度 + 按钮」，拿它钳 MaxWidth 会
	/// 每轮再缩 12px，回缩循环把标题一路挤到下限，字体被截断。</summary>
	private void ClampMiniTitle()
	{
		double dock = MiniTitleDock.ActualWidth;
		if (dock <= 0)
		{
			return; // 首次布局前 DockPanel 宽度为 0，不钳，保留 NaN 让重估按「放得下」处理。
		}
		double btn = MiniCopyButton.Visibility == Visibility.Visible
			? MiniCopyButton.ActualWidth + 8 : 0;
		// 右侧 DockPanel.Dock=Right 的区域：时间/来源两行 + 左间距 10px；
		// 取流提示显示时它也停靠在右侧，叠加实宽与右间距 8px。
		double rightDocked = MiniTimePanel.ActualWidth + MiniTimePanel.Margin.Left;
		if (FetchIndicator.Visibility == Visibility.Visible)
		{
			rightDocked += FetchIndicator.ActualWidth + FetchIndicator.Margin.Right;
		}
		MiniTrackText.MaxWidth = Math.Max(60, dock - rightDocked - btn - 12);
	}

	/// <summary>重估标题是否需要走马灯并复位状态。切歌、窗口尺寸变化、
	/// 布局变化都会走到；文字变短立即把平移归零停表、撤掉副本。</summary>
	public void RefreshTitleMarquee()
	{
		ClampMiniTitle();
		// 先按当前文本量一次自然宽度（不受钳制），决定要不要滚。
		double natural = MeasureMiniTitleWidth();
		double available = double.IsNaN(MiniTrackText.MaxWidth) ? natural : MiniTrackText.MaxWidth;
		if (natural - available <= 2)
		{
			StopTitleMarquee();
			return;
		}
		// 滚动期间不能用省略号截断（截断后移动的是带省略号的渲染结果）。
		// 全文本来就画在 NoWrap 的一行里，由 MiniTitleClip 裁掉超宽部分。
		MiniTrackText.TextTrimming = TextTrimming.None;
		// 副本 2 与主文本同样式、同钳制宽（不钳会把裁剪容器撑到自然宽），
		// 常驻主文本后方一个周期处，只在滚动时可见。
		MiniTrackText2.Text = MiniTrackText.Text;
		MiniTrackText2.MaxWidth = MiniTrackText.MaxWidth;
		MiniTrackText2.TextTrimming = TextTrimming.None;
		MiniTrackText2.Visibility = Visibility.Visible;
		// 周期 = 自然宽 + 裁剪窗宽：尾部走出左缘的那一刻，头部副本恰好
		// 顶到右缘开始往左走，两份文本之间隔着整窗宽的空白——按用户
		// 要求只做这一种效果，头部永远从最右缘冒出，不在尾部后面贴着。
		_titleMarqueeCycle = natural + available;
		MiniTitleShift.X = 0;
		MiniTitleShift2.X = _titleMarqueeCycle;
		if (_titleMarqueeTimer == null)
		{
			_titleMarqueeTimer = new DispatcherTimer(DispatcherPriority.Render)
			{
				Interval = TimeSpan.FromMilliseconds(16)
			};
			_titleMarqueeTimer.Tick += TitleMarqueeTick;
		}
		_titleMarqueeTimer.Start();
	}

	/// <summary>停表复位：省略号恢复、平移归零、副本撤掉。标题变短/无曲目时走这里。</summary>
	private void StopTitleMarquee()
	{
		_titleMarqueeTimer?.Stop();
		MiniTrackText.TextTrimming = TextTrimming.CharacterEllipsis;
		MiniTitleShift.X = 0;
		MiniTrackText2.Visibility = Visibility.Collapsed;
		MiniTitleShift2.X = 0;
		_titleMarqueeCycle = 0;
	}

	/// <summary>测量标题的自然像素宽（不受 MaxWidth/截断影响）。
	/// 用影子 TextBlock 走 Measure 而非 FormattedText：前者与真实显示
	/// 完全同一条排版管线（含字体回退），中西文混排的宽度也分毫不差；
	/// FormattedText 对回退字体的宽度有偏差，会把刚好放得下的标题
	/// 误判成超宽、启动不必要的走马灯。</summary>
	private double MeasureMiniTitleWidth()
	{
		string text = MiniTrackText.Text ?? "";
		if (text.Length == 0)
		{
			return 0;
		}
		try
		{
			var probe = new TextBlock
			{
				Text = text,
				FontFamily = MiniTrackText.FontFamily,
				FontStyle = MiniTrackText.FontStyle,
				FontWeight = MiniTrackText.FontWeight,
				FontStretch = MiniTrackText.FontStretch,
				FontSize = MiniTrackText.FontSize,
				TextTrimming = TextTrimming.None,
				TextWrapping = TextWrapping.NoWrap,
				Padding = new Thickness(0),
			};
			probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			double w = probe.DesiredSize.Width;
			return w > 0 ? w : MiniTrackText.ActualWidth;
		}
		catch
		{
			return MiniTrackText.ActualWidth;
		}
	}

	/// <summary>走马灯帧步进：恒速左移，位移滑过一整个周期后回卷。
	/// 回卷前后画面逐像素一致（副本 2 恰好补到主文本原位），看不到接缝。
	/// 不做悬停暂停——用户要求文本持续往左走，停在半路会让人误以为
	/// 卡死、等不到头部副本从右缘冒出来。</summary>
	private void TitleMarqueeTick(object? sender, EventArgs e)
	{
		if (_titleMarqueeCycle <= 0)
		{
			StopTitleMarquee();
			return;
		}
		const double dt = 16.0 / 1000.0;
		double x = MiniTitleShift.X - TitleMarqueeSpeed * dt;
		if (x <= -_titleMarqueeCycle)
		{
			x += _titleMarqueeCycle;
		}
		MiniTitleShift.X = x;
		MiniTitleShift2.X = x + _titleMarqueeCycle;
	}

	private void UpdateMiniTrack()
	{
		// 没在播的时候留空，不显示「不播放任何音乐」这种占位提示
		// 播放条标题（展示层）：本地文件把末段音频扩展名剥掉再显示
		//（「胸の中で誰かが.m4a」→「胸の中で誰かが」）；在线曲目走平台标题。
		// 平台标题常把歌手连在歌名后面（「歌名 - 歌手」），下面歌手行又会
		// 显示一遍完整歌手 —— 这里按 TrackRows.DisplayTitle 的去重口径把
		// 尾部的歌手后缀剥掉，只显示歌名本体；复制键（BuildCopyText）仍拿
		// 完整标题拼「歌名 - 歌手」，复制出来的串不受影响、天然不重复。
		// 判断口径用 BuildCurrentTrack().Type：只有真本地文件是 "local"，
		// 在线歌（含 B 站缓存直链）是平台类型，它们的标题不受影响。
		// 底层文件名不动：播放、更名、收藏仍用原名。
		string miniTitle = MusicService.CurrentDisplayTitle ?? "";
		SavedTrack? miniTrackMeta = MusicService.BuildCurrentTrack();
		string? miniArtist = miniTrackMeta?.Artist;
		if (miniTrackMeta == null || string.Equals(miniTrackMeta.Type, "local", StringComparison.Ordinal))
		{
			// 迷你栏标题保留 .mp4（不剥扩展名），只清掉 4k60fps 这类画质标签；
			// 复制串走 BuildCopyText，会同时剥掉扩展名和画质标签（见 TrackRows.CleanTitleNoise）。
			miniTitle = TrackRows.StripQualityTag(miniTitle);
			miniArtist = TrackRows.StripAudioExtension(miniArtist);
		}
		else
		{
			// 平台标题剥「- 歌手」后缀（尾部与歌手串相等才剥，不会误伤带连字符的歌名）。
			miniTitle = TrackRows.DisplayTitle(miniTitle, miniArtist);
		}
		MiniTrackText.Text = miniTitle;
		// 复制键只在真的有曲目时出现：空底栏挂一枚复制键既无意义，
		// 又会让「复制了空内容」这种怪提示成为可能。
		bool hasTrack = !string.IsNullOrWhiteSpace(MiniTrackText.Text);
		MiniCopyButton.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
		// 平台来源行：时长下方的小字「来自 XX 平台」（本地音乐显示「本地音乐」）。
		// 平台映射用 TrackRows.PlatformDisplayName（与列表平台标签同口径），
		// 未知平台整行收起，不留空行。
		try
		{
			string? plat = TrackRows.PlatformDisplayName(miniTrackMeta?.Type);
			if (plat != null && hasTrack)
			{
				MiniSourceText.Text = string.Format(Loc.Default["mini_from_platform"], plat);
				MiniSourceText.Visibility = Visibility.Visible;
			}
			else
			{
				MiniSourceText.Visibility = Visibility.Collapsed;
			}
		}
		catch
		{
			MiniSourceText.Visibility = Visibility.Collapsed;
		}
		// 曲名下面补一行歌手：在线歌有平台给的 Artist，
		// 本地歌走网易云匹配置顶后也带_artist；拿不到就整行收起。
		try
		{
			string artist = miniArtist ?? "";
			MiniArtistText.Text = artist;
			MiniArtistText.Visibility = string.IsNullOrWhiteSpace(artist)
				? Visibility.Collapsed : Visibility.Visible;
			// ⋯列表里「更名」只在本地文件上可用：在线歌（含 B 站缓存直链
			// 这类挂着在线标题的）改了名字只会让收藏和平台条目对不上。
			// （改名笔从常驻位挪进列表，可用性跟随曲目实时刷新。）
			bool renameable = MusicService.BuildCurrentTrack() is SavedTrack t
				&& string.Equals(t.Type, "local", StringComparison.Ordinal)
				&& !string.IsNullOrEmpty(t.Path) && File.Exists(t.Path);
			MiniMenuRename.IsEnabled = renameable;
		}
		catch
		{
			MiniMenuRename.IsEnabled = false;
		}
		RefreshTitleMarquee();
		UpdateMiniCover();
		UpdateMiniSourceFrame();
	}

	/// <summary>底栏复制键：把「歌名 - 歌手」整串拷进剪贴板。
	/// 歌手串沿用平台原始分隔（多歌手用「/」连着），粘贴到别处的搜索框
	/// （网易云/YouTube 等）都能一次命中，不用再手动挑名字。
	/// 拼串统一走 TrackRows.BuildCopyText：歌名尾部已带同一段歌手后缀时
	/// 不再重复追加（去重规则与列表行复制键完全一致，两处不各拼各的）。</summary>
	private void MiniCopyButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string title = MusicService.CurrentDisplayTitle ?? "";
			string artist = MusicService.BuildCurrentTrack()?.Artist ?? "";
			string text = TrackRows.BuildCopyText(title, artist);
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			Clipboard.SetText(text);
			ShowToast(Loc.Default["copy_done"]);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "复制歌曲信息失败");
		}
	}

	/// <summary>上一首已经画到底栏方块上的封面地址。
	/// 只有它变了才重新取图，避免每次刷新（进度 tick 也走 UpdateMiniAll）
	/// 都去 CoverLoader 走一遍——命中缓存倒还好，没命中就是一次无谓的请求。</summary>
	private string? _miniCoverUrl;

	/// <summary>底栏左侧的封面方块：优先用曲目自带的封面（在线歌的平台图、
	/// 本地歌匹配到歌词后补上的网易云封面），拿不到就留着音符占位图标。
	/// 方块 55px（44×1.25），按 128px 解码保证高分屏下不糊。
	/// 「显示封面图片」总开关关闭时（设置里那把闸现在管**全部**封面位，
	/// 不只是列表行）：这里直接停在纯色占位（不透明主题底色），
	/// 连 CoverLoader 都不碰 —— 网络请求全停、内存里不留解码位图。</summary>
	private void UpdateMiniCover()
	{
		try
		{
			if (!SettingsManager.Instance.ShowListCovers)
			{
				// 总闸关闭：清掉已显示的图（释放引用让位图可回收），只留音符占位。
				if (_miniCoverUrl != null || MiniCoverImage.Source != null)
				{
					_miniCoverUrl = null;
					MiniCoverImage.Source = null;
					MiniCoverImage.Opacity = 0;
				}
				MiniCoverGlyph.Visibility = Visibility.Visible;
				return;
			}
			SavedTrack? track = MusicService.BuildCurrentTrack();
			string? cover = track?.Cover;
			if (string.IsNullOrWhiteSpace(cover))
			{
				cover = MusicService.GetLocalMatchedCover(track?.Path);
			}
			if (string.Equals(cover, _miniCoverUrl, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			_miniCoverUrl = cover;
			// 换图先把上一张淡出归零，等新图到位再淡入，
			// 不然会看到上一首的封面残在新歌上闪一下。
			MiniCoverImage.Opacity = 0;
			if (string.IsNullOrWhiteSpace(cover))
			{
				MiniCoverImage.Source = null;
				MiniCoverGlyph.Visibility = Visibility.Visible;
				return;
			}
			if (cover!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
			{
				// 网络图：带 Referer 异步拉，防盗链 CDN 也能出图。
				// 只有真拿到图才收占位图标（onLoaded 只在成功时回调），
				// 拉不到就继续显示音符，不留空白方块。
				CoverLoader.LoadAsync(MiniCoverImage, cover, 128, () => MiniCoverLoaded());
				// 命中缓存是同步赋值的，这时回调已经在上面走过了，
				// 这里再兜一次（Source 有值 = 图已经在手）。
				if (MiniCoverImage.Source != null)
				{
					MiniCoverLoaded();
				}
				return;
			}
			// 本地图片路径
			var bi = new BitmapImage();
			bi.BeginInit();
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.DecodePixelWidth = 128;
			bi.UriSource = new Uri(cover);
			bi.EndInit();
			bi.Freeze();
			MiniCoverImage.Source = bi;
			MiniCoverLoaded();
		}
		catch
		{
			// 封面出不来不影响播放：留音符占位即可
			MiniCoverGlyph.Visibility = Visibility.Visible;
		}
	}

	/// <summary>封面到位：收掉音符占位，图片淡入 200ms。
	/// 图是从网络异步回来的，直接赋值会「啪」地跳出来，淡一下更顺眼。</summary>
	private void MiniCoverLoaded()
	{
		try
		{
			MiniCoverGlyph.Visibility = Visibility.Collapsed;
			MiniCoverImage.BeginAnimation(OpacityProperty, null);
			MiniCoverImage.Opacity = 0;
			var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			MiniCoverImage.BeginAnimation(OpacityProperty, fade);
		}
		catch
		{
			MiniCoverImage.Opacity = 1;
		}
	}

	// 来源框的配色。描边用**淡色**，光晕（投影）用饱和的品牌色：
	// 淡色的边压在封面图上不刺眼，光晕往外散出去才看得出是「发着光」。
	// 网易云红、QQ 绿，其它来源（LRCLIB/酷狗/B站）灰，跟主题无关，不做动态资源。
	private static readonly SolidColorBrush LineNetease = new(Color.FromRgb(0xF5, 0x8A, 0x8A));
	private static readonly SolidColorBrush LineQq = new(Color.FromRgb(0x5C, 0xDD, 0xA6));
	private static readonly SolidColorBrush LineOther = new(Color.FromRgb(0xB8, 0xB8, 0xB8));
	private static readonly SolidColorBrush LinePending = new(Color.FromRgb(0x8E, 0x8E, 0x8E));
	private static readonly SolidColorBrush SrcNoneBrush = new(Colors.Transparent);

	private static readonly Color GlowNetease = Color.FromRgb(0xEC, 0x41, 0x41);
	private static readonly Color GlowQq = Color.FromRgb(0x31, 0xC2, 0x7C);
	private static readonly Color GlowOther = Color.FromRgb(0x8A, 0x8A, 0x8A);
	private static readonly Color GlowPending = Color.FromRgb(0x7A, 0x7A, 0x7A);

	private static SolidColorBrush BrushForSource(MatchSource s) => s switch
	{
		MatchSource.Netease => LineNetease,
		MatchSource.Qq => LineQq,
		MatchSource.Other => LineOther,
		_ => SrcNoneBrush
	};

	private static Color GlowForSource(MatchSource s) => s switch
	{
		MatchSource.Netease => GlowNetease,
		MatchSource.Qq => GlowQq,
		MatchSource.Other => GlowOther,
		_ => GlowPending
	};

	/// <summary>画半个来源框。
	/// 到位了就淡色描边 + 同色投影发光；另一半还在找的时候（loading）给一段
	/// 呼吸式明暗：淡灰、半透明，慢慢亮起来又暗下去，一眼看得出「这一边还没结果」。
	/// 两边都没戏也不在加载中就整个透明，不留痕。</summary>
	private static void PaintHalf(Border half, MatchSource src, bool loading)
	{
		half.BeginAnimation(OpacityProperty, null);
		if (src != MatchSource.None)
		{
			half.Opacity = 1.0;
			half.BorderBrush = BrushForSource(src);
			half.Effect = new DropShadowEffect
			{
				Color = GlowForSource(src),
				BlurRadius = 7,
				ShadowDepth = 0,
				Opacity = 0.85
			};
			return;
		}
		if (loading)
		{
			half.BorderBrush = LinePending;
			half.Effect = new DropShadowEffect
			{
				Color = GlowPending,
				BlurRadius = 6,
				ShadowDepth = 0,
				Opacity = 0.5
			};
			// 呼吸：1100ms 一个来回，慢一点才像「在等」，不像报错闪烁
			var breathe = new DoubleAnimation(0.18, 0.62, TimeSpan.FromMilliseconds(1100))
			{
				AutoReverse = true,
				RepeatBehavior = RepeatBehavior.Forever,
				EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
			};
			half.BeginAnimation(OpacityProperty, breathe);
			return;
		}
		half.Opacity = 1.0;
		half.BorderBrush = SrcNoneBrush;
		half.Effect = null;
	}

	private static string NameForSource(MatchSource s) => s switch
	{
		MatchSource.Netease => "网易云",
		MatchSource.Qq => "QQ音乐",
		MatchSource.Other => "其它来源",
		_ => "未匹配"
	};

	// 上一次画上去的状态。进度 tick 每 300ms 也会调一次这个方法，
	// 状态没变就别重画 —— 否则呼吸动画每 300ms 被重启一次，
	// 看起来就是卡在起始透明度上不动。
	private MatchSource _paintedLyric = MatchSource.None;
	private MatchSource _paintedCover = MatchSource.None;
	private bool _paintedLoading;
	private bool _framePainted;

	/// <summary>给封面方块画那圈双色框：左半圈是歌词来源，右半圈是封面来源。
	/// 在线歌的封面是平台自带的，不用等歌词匹配结果，直接按平台着色。</summary>
	private void UpdateMiniSourceFrame()
	{
		try
		{
			SavedTrack? track = MusicService.BuildCurrentTrack();
			string? path = track?.Path ?? MusicService.CurrentPath;
			MatchSources src = MusicService.GetMatchSource(path);
			MatchSource cover = src.Cover;
			if (cover == MatchSource.None && track != null && !string.IsNullOrWhiteSpace(track.Cover))
			{
				cover = track.Type switch
				{
					"netease" => MatchSource.Netease,
					"qq" => MatchSource.Qq,
					_ => MatchSource.Other
				};
			}
			// 歌词那一半优先问歌词包自己：来源是拉到歌词的那一刻就写在包上的，
			// 比外部那张「路径 → 来源」的表更可靠（重播命中缓存时那张表里
			// 可能还没有这一首的记录，于是左半圈会一直空着）。
			MatchSource lyric = src.Lyric;
			if (lyric == MatchSource.None && !string.IsNullOrWhiteSpace(path))
			{
				LyricBundle? cached = LyricRuntime.Follow?.TryGetCached(path!);
				if (cached != null && cached.Count > 0)
				{
					lyric = cached.LyricSource == MatchSource.None ? MatchSource.Other : cached.LyricSource;
				}
			}
			// 歌词/封面还在后台找的时候，没结果的那半边呼吸一下
			bool loading = false;
			try
			{
				loading = LyricRuntime.Follow?.IsLoading ?? false;
			}
			catch
			{
			}
			if (_framePainted && _paintedLyric == lyric && _paintedCover == cover
				&& _paintedLoading == loading)
			{
				return;
			}
			_framePainted = true;
			_paintedLyric = lyric;
			_paintedCover = cover;
			_paintedLoading = loading;
			// 底栏来源框（发光色环）已按需求移除，不再绘制色环；
			// 仅保留来源提示（Tooltip）的刷新。
			if (MiniCoverTip != null)
			{
				MiniCoverTip.Text = track == null
					? "封面"
					: $"歌词：{NameForSource(lyric)}　封面：{NameForSource(cover)}";
			}
		}
		catch
		{
		}
	}

	// 封面框里的迷你时长条已经整条撤除（用户反馈「发光边框没什么效果」），
	// 进度指示回到底栏滑块一处，原有的发光边框呼吸动画保留。

	// 底栏「⋯」键的浮现/隐身逻辑已整体移除（用户要求去掉淡化隐藏动画）：
	// 键现在常显常可点，见 MainWindow.xaml 的 MiniMoreWrap 注释。

	private void MiniMoreButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			OpenFlyoutInward(MiniMorePopup, MiniMoreWrap);
		}
		catch
		{
		}
	}

	/// <summary>⋯列表「更名」：本地歌行内改名，拿不到入口（在线歌）就提示，
	/// 语义跟原来那颗常驻改名笔一模一样，只是搬进了列表。</summary>
	private void MiniMenuRename_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			MiniMorePopup.IsOpen = false;
			if (!BeginCurrentTitleRename())
			{
				ShowToast("正在播放的不是本地文件，不能改名", false);
			}
		}
		catch
		{
		}
	}

	/// <summary>⋯列表「音效」：不再跳音效页，改为弹出程序内音效预设浮窗
	///（用户要求在弹窗里直接选效果）。当前预设打勾，点选即应用。</summary>
	private void MiniMenuFx_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			MiniMorePopup.IsOpen = false;
			BuildFxPresetList();
			OpenFlyoutInward(MiniFxPopup, MiniMoreWrap);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开音效预设弹窗失败");
		}
	}

	/// <summary>重建音效预设列表：全部预设 + 当前项打勾。
	/// 每次打开都重建，保证勾选状态跟着设置走。</summary>
	private void BuildFxPresetList()
	{
		MiniFxList.Children.Clear();
		string current = SettingsManager.Instance.Fx?.PresetName ?? "";
		foreach (FxPreset preset in FxPreset.Presets)
		{
			bool selected = string.Equals(preset.Name, current, StringComparison.OrdinalIgnoreCase);

			var check = new TextBlock
			{
				Text = selected ? "\uE73E" : "",
				FontFamily = new FontFamily("Segoe MDL2 Assets"),
				FontSize = 13,
				VerticalAlignment = VerticalAlignment.Center,
				MinWidth = 18,
				Margin = new Thickness(0, 0, 6, 0)
			};
			check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

			var label = new TextBlock
			{
				Text = preset.Name,
				FontSize = 13,
				VerticalAlignment = VerticalAlignment.Center
			};
			label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

			var row = new StackPanel { Orientation = Orientation.Horizontal };
			row.Children.Add(check);
			row.Children.Add(label);

			var item = new Button
			{
				Style = (Style)FindResource("GhostButtonStyle"),
				// 上下留 3px 外距 + 8px 内距：原来上下全贴着（外距 0、内距 6），
				// 多行时行与行几乎连成一片，hover 高亮块也挤成一坨；
				// 留出呼吸感之后每行的点击热区和悬停反馈都清晰可辨。
				Margin = new Thickness(2, 3, 2, 3),
				Padding = new Thickness(8, 8, 12, 8),
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Left,
				Content = row,
				Tag = preset.Name,
				ToolTip = preset.Description
			};
			item.Click += MiniFxItem_Click;
			MiniFxList.Children.Add(item);
		}
	}

	/// <summary>点了某个预设：应用并关浮窗。应用逻辑与音效页同一份
	///（FxPresetPage.ApplyPreset：写设置 + 保存 + ApplyFxRestart 重挂音频链）。</summary>
	private void MiniFxItem_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (sender is not Button { Tag: string name })
			{
				return;
			}
			MiniFxPopup.IsOpen = false;
			FxPresetPage.ApplyPreset(name);
			ShowToast($"音效已切换：{name}");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "应用音效预设失败");
		}
	}

	/// <summary>行内评论键：对正在播的曲目开/收评论抽屉。</summary>
	private void MiniCommentButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			object? track = MusicService.BuildCurrentTrack();
			if (track != null)
			{
				ToggleComments(track);
			}
		}
		catch
		{
		}
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
		// 时间文本与滑块同一条防回写规则：拖动预览、Seek 等待窗口期一律不动。
		// _tick 每 0.5s 拿真实位置回写，刚点的落点会被旧位置顶回去——
		// 滑块看着是跳过去了、时间却又「复原」，拖动中的预览时间也会被打断。
		// 到位判定/超时把 _seekPending 撤掉之后，回写自然恢复。
		if (!_progressDragging && !_seekPending)
		{
			MiniTimeText.Text = FormatTime(pos) + " / " + FormatTime(dur);
		}
		// 窗口期的撤除以「位置真的到了」为准，不再靠定时器猜：
		// 播放器报出的位置贴近这次 Seek 的目标，说明跳过去生效了；
		// 连等 2.5 秒还没到就放行兜底（Seek 被拒绝/流不支持时不能永远挂着）。
		// 原先定时 340ms 一到就撤，常常早于 Seek 真正生效——_tick 拿旧位置
		// 把滑块弹回去、新位置来了又跳过去，就是「点一下缩回去又弹过来」。
		if (_seekPending && dur <= TimeSpan.Zero)
		{
			// 连判定基准（总时长）都没有（换歌瞬间），挂不住，直接放行。
			_seekPending = false;
		}
		if (_seekPending && dur > TimeSpan.Zero)
		{
			double eps = Math.Max(1.0, dur.TotalSeconds * 0.01);
			// 「到位」必须发生在 Seek 真正派发之后（_seekIssued）。
			// 根因：Seek 请求是异步派发的，从松手到后台线程真正挪 reader
			// 之间有一个窗口，此间 pos 还是旧位置——旧位置若恰好落在
			// 目标 ±eps（eps 为总时长 1%，四分钟的歌约 2.4 秒）内，
			// 旧逻辑就误判「已到位」提前撤窗，下一拍 _tick 拿旧位置把
			// 滑块写回去，等 Seek 落地又跳到新值。首次点击最容易中招：
			// 开播不久 pos≈0，用户在开头附近点一下，|pos-target| 必然
			// 小于 eps——正是「第一次点击滑块弹一下又跳过去」的来源。
			// 挂上 _seekIssued 门：没派发的 Seek 没资格谈「到位」。
			bool arrived = _seekIssued
				&& Math.Abs(Math.Clamp(pos.TotalSeconds, 0.0, dur.TotalSeconds) - _seekTarget) <= eps;
			if (arrived || (DateTime.UtcNow - _seekPendingSince).TotalMilliseconds > 2500.0)
			{
				_seekPending = false;
			}
		}
		// _seekPending 期间绝对不回写滑块：Seek 内部的 NotifyStateChanged 会重入到这里，
		// 那时 reader 报的还是旧位置，回写就会把用户刚点/刚拖到的位置原样弹回去。
		if (!_progressDragging && !_seekPending && dur > TimeSpan.Zero)
		{
			double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
			MiniProgressSlider.Value = ratio;
		}
		// 拖动/定位窗口期里绝对不回写滑块：防回弹。
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

	// ===== 第三界面：右侧评论抽屉（Views/CommentsPanel）=====
	// 行内评论键走这里；面板挂在 RootGrid 的 Row1，能盖住内容页也能盖全屏页。

	/// <summary>行内评论键的统一入口：开 / 收评论抽屉（Toggle 语义：
	/// 面板隐藏时同曲也重开；面板开着且同曲则收列表回空态）。</summary>
	internal void ToggleComments(object item)
	{
		try
		{
			CommentsPanel?.Toggle(item);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "开合评论面板失败");
		}
	}

	/// <summary>收起评论抽屉（切页、进全屏时调）。没开着就不动。</summary>
	internal void CloseComments()
	{
		try
		{
			if (CommentsPanel != null && CommentsPanel.IsOpen)
			{
				CommentsPanel.Close();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "关闭评论面板失败");
		}
	}




	/// <summary>底栏对正在播的歌名进入行内改名（入口已挪进 ⋯ 列表的「更名」）。
	/// 只在本地文件上可用：平台在线歌的标题在服务器上，
	/// 改了只会让收藏对不上；时间文本那半边也不给改——
	/// 那是进度区，双击它还是开全屏。</summary>
	private bool BeginCurrentTitleRename()
	{
		try
		{
			SavedTrack? track = MusicService.BuildCurrentTrack();
			string? path = track?.Path;
			if (track == null
				|| !string.Equals(track.Type, "local", StringComparison.Ordinal)
				|| string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				return false;
			}
			if (MiniTrackText.Parent is not Panel host || host.Children.IndexOf(MiniTrackText) < 0)
			{
				return false;
			}
			// 编辑器由 TrackRows 统一画：Enter/失焦提交，Esc 取消。
			// 提交回调里拿着旧路径去真改名，这样换歌/停止也不会对错文件。
			string oldPath = path;
			Pages.TrackRows.BeginInlineRename(host, MiniTrackText, track,
				name => RenameCurrentLocal(oldPath, name));
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把正在播的本地文件真正改名：MusicService.RenameFile 负责搬文件、
	/// 同步下载缓存路径与正在播放的 CurrentPath；UserLibrary 同步收藏/最近播放；
	/// 最后刷底栏标题（也会带刷全屏页的标题）。失败给 toast 并还原标题显示。</summary>
	private void RenameCurrentLocal(string oldPath, string newName)
	{
		try
		{
			string? created = MusicService.RenameFile(oldPath, newName);
			string clean = MusicService.CleanFileName(newName);
			if (string.IsNullOrEmpty(created) || clean.Length == 0)
			{
				ShowToast("重命名失败：名称无效或目标已存在", false);
			}
			else
			{
				UserLibrary.ApplyLocalRename(oldPath, created, clean);
				ShowToast("已重命名为 " + clean);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "正在播放改名失败: " + newName);
			ShowToast("重命名失败", false);
		}
		finally
		{
			UpdateMiniTrack();
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
			// 只有状态真的翻转了才弹那一下、并把列表里的心一起刷掉。
			// 这个方法跟着进度刷新每秒都会调一次，
			// 无条件弹的话这颗心就会一直在跳，像坏了。
			if (_miniFavState != fav)
			{
				_miniFavState = fav;
				PopMiniFav(fav);
				if (track != null)
				{
					// 底栏和行是同一份收藏的两处显示。行里那颗心是建行时按
					// IsFavorite 画上的，底栏改了它不会自己知道，得主动通知。
				Pages.TrackRows.SyncHearts(track.Key);
			}
		}
	}
		catch
		{
		}
	}

	/// <summary>底栏红心自己那下弹动。
	/// 用 RenderTransform（可冻结、能直接动画），不用 LayoutTransform ——
	/// 后者会让整个底栏跟着重新布局。</summary>
	private bool? _miniFavState;

	private void PopMiniFav(bool fav)
	{
		if (MiniFavGlyph.RenderTransform is not ScaleTransform scale)
		{
			scale = new ScaleTransform(1.0, 1.0);
			MiniFavGlyph.RenderTransform = scale;
			MiniFavGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
		}
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		var anim = new DoubleAnimation(fav ? 1.4 : 0.85, 1.0, TimeSpan.FromMilliseconds(260))
		{
			EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		};
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
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

	/// <summary>播放顺序按钮的右键菜单：四种模式全列出来，当前模式打勾，
	/// 点哪项切哪项。左键保持原来的循环切换不变。条目在每次右键弹出时
	/// 重建（勾选状态才不会停在上一首歌那会儿）。样式与「更多」浮层同款：
	/// FlyoutBg 圆角卡 + 图标文字项（图1 的样子）。文案复用现成的 mode_*
	/// 多语言键，不新增键。</summary>
	private void InitMiniOrderContextMenu()
	{
		// 条目构建放在 MiniOrderButton_RightClick（每次弹出重建）；
		// 这里什么都不用预建，Popup 的空 StackPanel 就是占位。
	}

	/// <summary>播放顺序键右键：重建模式条目并按「向内打开」弹出。</summary>
	private void MiniOrderButton_RightClick(object sender, MouseButtonEventArgs e)
	{
		try
		{
			string cur = MusicService.NormalizePlaybackMode();
			MiniOrderList.Children.Clear();
			foreach (string mode in ModeCycle)
			{
				MiniOrderList.Children.Add(BuildOrderModeItem(mode, string.Equals(mode, cur, StringComparison.Ordinal)));
			}
			OpenFlyoutInward(MiniOrderPopup, MiniOrderWrap);
			e.Handled = true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "构建播放顺序菜单失败");
		}
	}

	/// <summary>播放模式菜单的一项：模式图标 + 名称 + 当前模式的勾。
	/// 样式对齐「更多」浮层（GhostButtonStyle 圆角项、图标 13px、左对齐），
	/// 当前模式额外铺一层强调色淡底，勾用 E73E 放右侧。</summary>
	private FrameworkElement BuildOrderModeItem(string mode, bool isCurrent)
	{
		var row = new StackPanel { Orientation = Orientation.Horizontal };
		row.Children.Add(new TextBlock
		{
			Text = mode switch
			{
				"once" => "\uE8F1",
				"sequential" => "\uE8EE",
				"loop" => "\uE8ED",
				_ => "\uE8B1"
			},
			FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
			FontSize = 13,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 8, 0),
			Foreground = (Brush)Application.Current.Resources["AccentBrush"]
		});
		row.Children.Add(new TextBlock
		{
			Text = Loc.Default["mode_" + mode],
			FontSize = 13,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
		});
		if (isCurrent)
		{
			row.Children.Add(new TextBlock
			{
				Text = "\uE73E",
				FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 13,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(10, 0, 0, 0),
				Foreground = (Brush)Application.Current.Resources["AccentBrush"]
			});
		}
		var btn = new Button
		{
			Style = (Style)FindResource("GhostButtonStyle"),
			Margin = new Thickness(2, 0, 2, 0),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(8, 6, 8, 6),
			Cursor = Cursors.Hand,
			Content = row,
			Background = isCurrent
				? (Brush)Application.Current.Resources["AccentSubtleBrush"]
				: Brushes.Transparent
		};
		string m = mode;
		btn.Click += (_, _) =>
		{
			try
			{
				MiniOrderPopup.IsOpen = false;
				var st = SettingsManager.Instance;
				st.PlaybackMode = m;
				SettingsManager.Save(st);
				UpdateMiniOrder();
				ShowToast(Loc.Default["mode_" + m]);
				LogHelper.Log("切换播放模式: " + m);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "切换播放顺序失败");
			}
		};
		return btn;
	}

	/// <summary>把弹层改成「向内打开」：整体在锚按钮上方，右缘与锚按钮右缘
	/// 对齐。锚按钮贴着窗口右缘，默认 Top 放置的浮层会朝窗外伸、被 WPF
	/// 整个推回来，看着就是飘到按钮外面去了；右缘对齐后弹层朝窗口内部展开。
	/// 宽高在打开前量一次（浮层内容都是固定小卡片，量得准）。</summary>
	private static void OpenFlyoutInward(Popup popup, FrameworkElement anchor)
	{
		try
		{
			if (popup.Child is FrameworkElement fe)
			{
				fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				popup.Placement = PlacementMode.RelativePoint;
				popup.HorizontalOffset = Math.Max(0, anchor.ActualWidth - fe.DesiredSize.Width);
				popup.VerticalOffset = -(fe.DesiredSize.Height + 6);
			}
			popup.IsOpen = true;
		}
		catch
		{
			popup.IsOpen = true;
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
		try
		{
			// 浮层里百分比用固定 MinWidth 的右对齐文本，宽度不随位数变化，
			// 不会出现「99% → 100% 时整个浮层抖一下」。
			VolumePopupText.Text = pct + "%";
		}
		catch
		{
		}
		MiniVolumeGlyph.Text = pct <= 0 ? "\uE74F" : "\uE995";
	}

	private void UpdateVolumeSlider()
	{
	// 初始化时同步滑条：ValueChanged 会跟着触发，
	// 不挡住的话会把同一份设置再写一遍盘。
	_syncVolumeSlider = true;
	try
	{
		VolumePopupLabel.Text = Loc.Default["mt_volume"];
		VolumePopupSlider.Value = Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0) * 100.0;
	}
		catch
		{
		}
		finally
		{
			_syncVolumeSlider = false;
		}
		UpdateMiniVolumeText();
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
			MiniMorePopup.IsOpen = false;
			// 最小化一律进任务栏。以前这里跟「启动时自动隐藏到托盘」共用一个开关，
		// 那个选项删掉以后这里没有分支了。
		WindowState = WindowState.Minimized;
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
		// 最小化时建议列表还挂在标题栏下面会跟着一起消失/留在任务栏预览上，
		// 恢复后又是错位的（Popup 的位置是按还原时算的），索性收掉。
		if (WindowState != WindowState.Normal)
		{
			CloseSuggest();
		}
		if (WindowState == WindowState.Minimized)
		{
			TryTrimWorkingSet();
		}
	}

	/// <summary>最小化时把工作集换出物理内存。「最小化时释放物理内存」已按产品
	/// 决策恒开（设置页不再提供入口，配置读取处强制 true），这里不再判断开关。
	/// WPF 应用的托管堆不及时归还物理页：挂后台听歌一晚，任务管理器里
	/// 常驻一两百 MB。EmptyWorkingSet 强制把页面换到待机列表，占用立降；
	/// 页面本身都在待机列表里没丢，还原窗口时按需换回来，只是慢半拍。</summary>
	private void TryTrimWorkingSet()
	{
		try
		{
			// 延到下一帧：窗口还没真正从屏幕摘掉时清工作集，
			// 紧跟其后的渲染又会把一批页换回来，白清。
			// EmptyWorkingSet 的 P/Invoke 收进了 MemoryRelease（三处共用）。
			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, MemoryRelease.TrimWorkingSet);
		}
		catch
		{
			// 只是优化项，绝不影响最小化本身。
		}
	}

	/// <summary>
	/// 收进托盘时做一次彻底的内存释放，让后台驻留以最低内存跑。
	/// 为什么只在托盘化时做：
	/// - 真退出路径（CloseBehavior=exit / 托盘 Exit）马上就撕进程，GC 压缩纯属浪费时间；
	/// - 普通最小化窗口随时会被拖回来，清掉封面缓存重建会闪，那边只做 EmptyWorkingSet；
	/// - 托盘驻留动辄几十分钟，此时清最划算：封面解码缓存清掉后没人看着，
	///   回到前台视口判定会自动重载（字节层没了走磁盘缓存，不发网络）。
	/// 与 TryTrimWorkingSet 的关系：内存整理已按产品决策恒开（设置页不再提供
	/// 入口，配置读取处强制 true），最小化路径和托盘路径对同一次驻留天然不会
	/// 重复弹释放（最小化只换页、托盘才清缓存）。
	/// 释放三步（封面缓存→GC 压缩→EmptyWorkingSet）收在 Services/MemoryRelease
	/// 里，调试面板「立即释放」和底栏「内存释放」键走的是同一份实现。
	/// </summary>
	private void ReleaseMemoryForTrayResidency()
	{
		try
		{
			MemoryRelease.ReleaseNow();
			// 二段延迟换出：EmptyWorkingSet 之后，托盘里仍在跑的后台活动
			// （0.5s 进度回写定时器、SMTC 进度同步、歌词窗口刷新）会立刻把
			// 刚换出的页面重新换回工作集——表现为"释放完自己涨回 100MB"。
			// 等 20 秒让这些唤醒点跑完一遍，再把页补换出一次，驻留占用才
			// 真正落底。窗口中途恢复也无害：TrimWorkingSet 只是换页，
			// 不清任何缓存，恢复使用只会把正在用的页换回来。
			_ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
			{
				if (!IsVisible || WindowState == WindowState.Minimized)
				{
					MemoryRelease.TrimWorkingSet();
				}
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "托盘驻留内存释放失败");
		}
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
			// 音画校正定时器跟着窗口一起收，不然关窗后还在空转读 MediaElement。
			_videoSyncTimer?.Stop();
			_videoSyncTimer = null;
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
		if (SettingsManager.Instance.CloseBehavior == "tray")
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
				// 用户要求：收托盘不再弹右下角通知（「已最小化到系统托盘」）。
				// ShowBalloon 方法保留，托盘菜单里的手动刷新气泡等场景还能用。
				// 托盘化是长期驻留的起点，在这里做一次内存释放；真退出路径
				// 不经过这里，所以不会在退出前白做一遍 GC 压缩。
				ReleaseMemoryForTrayResidency();
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
			// 这里也正是「全黑」的重灾区：以前只有开启动动画才救得回来，
			// 关掉动画时这个判断走进来什么也不做，窗口就一直是 0。
			// 现在 PlayStartupAnimation 自己会负责归位。
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

	/// <summary>标题栏「选项」：和左侧导航点「设置」一样，直接切到内置设置页。
	/// 不再弹浮层列表——设置项都在设置页里，进去就能看到全部。</summary>
	private void TitleOptionsButton_Click(object sender, RoutedEventArgs e) => NavigateTo("settings");

	/// <summary>标题栏「账号」：直接切到独立的账号页（登录卡已从设置页拆到那里）。</summary>
	private void TitleAccountsButton_Click(object sender, RoutedEventArgs e) => NavigateTo("account");

	private void TitleSearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		CommitTitleSearchPlaceholder();
		// 用户自己动过输入框，历史指针就作废，下次按 ↑ 重新从最近的一条开始。
		// 程序自己填的（↑/↓/Ctrl+Z 取历史）不算，所以要用 _suppressSearchHistory 挡一下。
		if (!_suppressSearchHistory)
		{
			_searchHistoryIndex = -1;
		}
		// 取历史也会改 Text，走的是 suppress 分支，但列表还是该跟着换一批，
		// 所以这里无条件排一次。输入框空的时候不该弹列表——
		// 空关键词的推荐只在「对焦」那一刻给一次（见 ShowHobbySuggest），
		// 不然用户删字删到空还会被推荐列表糊一下，很烦。
		if (TitleSearchBox.Text?.Trim().Length > 0)
		{
			RequestSuggestUpdate();
		}
		else
		{
			CloseSuggest();
		}
		UpdateSearchGhost();
	}

	// ===== 输入框的淡色补全 =====
	// 敲「东方」后面淡出「Project」、敲「pho」淡出「nk」、敲「So」淡出「 Far Away」。
	// Tab = 采纳补全并立刻搜索；Enter = 按原样搜索（不补全）。

	/// <summary>当前正显示着的补全；没有就是 null。</summary>
	private Guess? _ghost;

	/// <summary>按输入框里已有的字算补全，并把那一截淡色文字摆在字的末尾。
	/// 光标不在末尾时不补：光标停在中间说明用户在改前面的字，
	/// 这时候后面挂一串补全只会碍事。</summary>
	private void UpdateSearchGhost()
	{
		try
		{
			if (TitleSearchGhost == null)
			{
				return;
			}
			string text = TitleSearchBox.Text ?? "";
			if (text.Length == 0 || !TitleSearchBox.IsFocused
				|| TitleSearchBox.CaretIndex != text.Length)
			{
				TitleSearchGhost.Visibility = Visibility.Collapsed;
				_ghost = null;
				return;
			}
			Guess g = QueryBrain.Complete(text);
			if (g.Suffix.Length == 0)
			{
				TitleSearchGhost.Visibility = Visibility.Collapsed;
				_ghost = null;
				return;
			}
			_ghost = g;
			TitleSearchGhost.Text = g.Suffix;
			// 顶到已敲文字的末尾：12 是输入框自己的左内边距
			TitleSearchGhost.Margin = new Thickness(12 + MeasureSearchTextWidth(text), 0, 0, 0);
			TitleSearchGhost.Visibility = Visibility.Visible;
		}
		catch
		{
		}
	}

	/// <summary>量一段文字在输入框字号下的宽度，用来给补全定位。</summary>
	private double MeasureSearchTextWidth(string text)
	{
		var face = new Typeface(TitleSearchBox.FontFamily, TitleSearchBox.FontStyle,
			TitleSearchBox.FontWeight, TitleSearchBox.FontStretch);
		var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
			face, TitleSearchBox.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
		return formatted.WidthIncludingTrailingWhitespace;
	}

	/// <summary>采纳补全：整词填进输入框并立即搜索。
	/// 采纳过的词记 3 分（普通搜索记 1 分），下次再敲同一个前缀会优先补它 ——
	/// 「So」补成「So Far Away」就是这么越用越准的。</summary>
	private void AcceptGhost()
	{
		if (_ghost == null || _ghost.Suffix.Length == 0)
		{
			return;
		}
		string full = _ghost.Full;
		_suppressSearchHistory = true;
		try
		{
			TitleSearchBox.Text = full;
			TitleSearchBox.CaretIndex = full.Length;
		}
		finally
		{
			_suppressSearchHistory = false;
		}
		_ghost = null;
		TitleSearchGhost.Visibility = Visibility.Collapsed;
		QueryBrain.NoteAccepted(full);
		SubmitTitleSearch();
	}

	/// <summary>对焦且输入框为空时，从设置里的「爱好关键词」里挑一个真的去搜一遍。
	///
	/// 只在「对焦」这一刻弹，不跟着 TextChanged 弹：删字删空了还弹推荐很烦。
	/// 关键词直接复用设置页那一份（HobbyKeywords），不给推荐单开一套配置——
	/// 用户在设置里改的就是这里想看到的。</summary>
	private void ShowHobbySuggest()
	{
		string keyword = CurrentSearchQuery();
		if (keyword.Length > 0)
		{
			// 同一个词、而且已经搜出结果了：直接把现有列表重新弹出来。
			// 别又从头联网拉一遍 —— 那会先把用户滑到底追加出来的结果清掉，
			// 「点一下输入框想找回刚才那批」反而变成清空重来。
			if (string.Equals(keyword, _suggestKeyword, StringComparison.Ordinal)
				&& _suggestLocal.Count + _suggestOnline.Count > 0)
			{
				RenderSuggest(_suggestLocal.Concat(_suggestOnline).ToList(), false);
				return;
			}
			RequestSuggestUpdate();
			return;
		}
		// 输入框空着：第一次随机搜出的结果直接复用，不再每次点击都换词重搜。
		// 每点一下就换一个爱好词重新联网拉一遍，用户想找回刚才那批也找不回，
		// 而且白白多打几次接口。这里只在「还没有随机结果」时才真的去搜，
		// 之后每次点击都把已缓存的那批重新弹出来（含滑到底追加出来的部分）。
		if (_hobbySuggestActive
			&& (_suggestLoadingMore || _suggestLocal.Count + _suggestOnline.Count > 0))
		{
			if (_suggestLocal.Count + _suggestOnline.Count > 0)
			{
				RenderSuggest(_suggestLocal.Concat(_suggestOnline).ToList(), false);
			}
			return;
		}
		// 从爱好关键词里挑一个**真的去搜一遍**，把结果列出来。
		// 原来这里是把关键词本身当建议行列出来（副标题写「爱好关键词」），
		// 看着像「这些词可以直接搜」，但要点一下才真搜，而且点的还是关键词、
		// 不是歌 —— 列表里一堆词看着很干，而且双击也播不了。
		// 改成直接出结果：每行都是能点的歌，平台标签和双击播放都照常。
		string hobby = PickHobbyKeyword();
		if (hobby.Length == 0)
		{
			CloseSuggest();
			return;
		}
		_hobbySuggestActive = true;
		_ = UpdateSuggestAsync(false, hobby);
	}

	/// <summary>从设置里的爱好关键词里挑一个来搜。
	///
	/// 不是每次都取第一个：老盯同一个词，连着点几次输入框看到的永远是同一批。
	/// 这里按调用次数轮着走，下一次自动换下一个。
	/// 一个词都没配就返回空串，调用方负责关弹层。</summary>
	private string PickHobbyKeyword()
	{
		string raw = SettingsManager.Instance.HobbyKeywords ?? "";
		string[] parts = raw.Split(new[] { ',', '，', ';', '；', '\n', '\r' },
			StringSplitOptions.RemoveEmptyEntries);
		var words = new List<string>();
		foreach (string part in parts)
		{
			string kw = part.Trim();
			if (kw.Length > 0)
			{
				words.Add(kw);
			}
		}
		if (words.Count == 0)
		{
			return "";
		}
		return words[_hobbyPickIndex++ % words.Count];
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
			// Esc 先收建议，再清空并让出焦点，别把用户困在输入框里。
			// 只有关着列表时才清：否则用户想「关掉列表接着看输入框里的词」
			// 一按 Esc 词也被抹了，得重新敲一遍。
			if (TitleSuggestPopup.IsOpen)
			{
				CloseSuggest();
			}
			else
			{
				TitleSearchBox.Text = "";
				TitleSearchBox.Focus();
				CommitTitleSearchPlaceholder();
			}
			e.Handled = true;
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

	// ===== 标题栏搜索的「词 → 标签卡」输入 =====
	/// <summary>当前搜索词：就是输入框里的原样文字。
	/// 以前按空格会把敲好的词包成一张标签卡，那是设置页「爱好关键词」该有的效果，
	/// 误接到搜索框上之后，想搜「东方 红魔乡」这种带空格的词根本敲不进去
	/// （空格直接被吃掉了）。搜索框里空格就是空格，多词搜索照常。</summary>
	private string CurrentSearchQuery()
		=> (TitleSearchBox.Text ?? "").Trim();

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

	/// <summary>点搜索框（或按 Ctrl+F 聚焦过来）时，如果框是空的就把爱好关键词给出来。
	/// 有关键词的老实走正常建议：那是搜索建议，不是推荐，两者混在一起会误导。</summary>
	private void TitleSearchBox_GotFocus(object sender, RoutedEventArgs e)
	{
		if (!IsLoaded)
		{
			return;
		}
		ShowHobbySuggest();
		UpdateSearchGhost();
	}

	private void TitleSearchBox_LostFocus(object sender, RoutedEventArgs e)
	{
		// 焦点走了就别留一串淡字挂在框里，看着像框里还有没搜的东西
		_ghost = null;
		try
		{
			TitleSearchGhost.Visibility = Visibility.Collapsed;
		}
		catch
		{
		}
	}

	/// <summary>每次点搜索框都重弹一次结果，哪怕它早就有焦点了。
	///
	/// 只靠 GotFocus 是不够的：WPF 的 GotFocus 只在「从无焦点变成有焦点」时来一次，
	/// 框已经获得焦点之后用户再点它，事件根本不触发 —— 表现就是「点输入框没反应、
	/// 得先点别处再点回来」。PreviewMouseDown 每一下点击都有，所以拿它兜住。</summary>
	private void TitleSearchBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (!IsLoaded)
		{
			return;
		}
		ShowHobbySuggest();
	}

	/// <summary>建议列表的滚轮走平滑滚动。
	///
	/// 主窗口那个 Content_PreviewMouseWheel 只在 ContentHost 的视觉树里找 ScrollViewer，
	/// 而建议列表长在 Popup 的独立 HWND 上，根本不在那棵树里，所以它够不着这里，
	/// 只能自己挂一份。XAML 里把 CanContentScroll 设成 False 就是为了这个：
	/// 按像素滚才能和 SmoothScroll 的偏移对上。</summary>
	private void TitleSuggestScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (TitleSuggestScroll.ScrollableHeight <= 0.0)
		{
			return;
		}
		e.Handled = true;
		double delta = e.Delta > 0 ? -_mouseWheelStep : _mouseWheelStep;
		SmoothScroll.Animate(TitleSuggestScroll, delta);
	}

	/// <summary>滚到底就再要一批在线结果接在后面。</summary>
	private void TitleSuggestScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		UpdateSuggestSlider();
		if (e.ExtentHeightChange == 0.0 && e.VerticalChange == 0.0)
		{
			return;
		}
		double scrollable = TitleSuggestScroll.ScrollableHeight;
		if (scrollable <= 0.5 || TitleSuggestList.Items.Count == 0)
		{
			return;
		}
		// 离底还差几像素就不算到底：手抖一下就发请求的话，网络会被反复打。
		if (TitleSuggestScroll.VerticalOffset < scrollable - 8.0)
		{
			return;
		}
		// 渲染期间一律不判「到底」：Items 增删会连带响ScrollChanged，那几帧的
		// 偏移和 ScrollableHeight 都还是中间态（比如内容被清空到一半时
		// ScrollableHeight 一度是 0），拿中间态判到底就会自己追加自己。
		if (_suggestRendering || _suggestLoadingMore || _suggestExhausted)
		{
			return;
		}
		// 记下「是停在哪儿触发的」，追加完要弹回这个位置（见 UpdateSuggestAsync 末尾）。
		// 记触发点而不是直接用追加后的当前偏移：请求要飞几百毫秒，这期间
		// 用户可能又滚了一截，回来按旧位置复原才不会把他从新加的内容上拽走。
		_suggestTriggerOffset = TitleSuggestScroll.VerticalOffset;
		// 追加要接着上一批的词往下翻，所以用 _suggestKeyword 而不是现读输入框：
		// 爱好推荐那条路输入框是空的，现读会把词算成空、这一批就白拉了。
		_ = UpdateSuggestAsync(true, _suggestKeyword);
	}

	/// <summary>把右侧滑栏对上当前状态：上半格写条数，滑块高度按可视比例、位置按滚动比例。</summary>
	private void UpdateSuggestSlider()
	{
		if (TitleSuggestScroll == null || TitleSuggestThumb == null || TitleSuggestTrack == null)
		{
			return;
		}
		int total = TitleSuggestList.Items.Count;
		TitleSuggestCount.Text = total > 0 ? total.ToString() : "";
		double view = TitleSuggestScroll.ViewportHeight;
		double scrollable = TitleSuggestScroll.ScrollableHeight;
		bool canScroll = scrollable > 0.5 && view > 0.0;
		TitleSuggestTrack.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
		TitleSuggestThumb.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
		if (!canScroll)
		{
			return;
		}
		// 轨道上下留白：上面给条数让位，下面贴边。
		double trackH = Math.Max(1.0, view - 27.0);
		double thumbH = Math.Clamp(trackH * (view / (view + scrollable)), 18.0, trackH);
		TitleSuggestThumb.Height = thumbH;
		double t = Math.Clamp(TitleSuggestScroll.VerticalOffset / scrollable, 0.0, 1.0);
		TitleSuggestThumb.Margin = new Thickness(0, 17 + (trackH - thumbH) * t, 0, 0);
	}

	private void TitleSuggestThumb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (TitleSuggestScroll.ScrollableHeight <= 0.5)
		{
			return;
		}
		_suggestDragStartY = e.GetPosition(TitleSuggestThumb).Y + TitleSuggestThumb.Margin.Top;
		_suggestDragStartOffset = TitleSuggestScroll.VerticalOffset;
		// 抓住鼠标：拖到滑块外面（甚至拖出弹窗）也得继续跟手，
		// 只靠 Thumb 自己的 MouseMove 会在指针滑出去时卡住。
		TitleSuggestThumb.CaptureMouse();
		e.Handled = true;
	}

	private void TitleSuggestThumb_MouseMove(object sender, MouseEventArgs e)
	{
		if (TitleSuggestScroll.ScrollableHeight <= 0.5 || !TitleSuggestThumb.IsMouseCaptured)
		{
			return;
		}
		double trackH = Math.Max(1.0, TitleSuggestScroll.ViewportHeight - 27.0);
		double travel = Math.Max(1.0, trackH - TitleSuggestThumb.Height);
		double now = e.GetPosition(TitleSuggestThumb).Y + TitleSuggestThumb.Margin.Top;
		double ratio = (now - _suggestDragStartY) / travel;
		double offset = _suggestDragStartOffset + ratio * TitleSuggestScroll.ScrollableHeight;
		TitleSuggestScroll.ScrollToVerticalOffset(
			Math.Clamp(offset, 0.0, TitleSuggestScroll.ScrollableHeight));
	}

	private void TitleSuggestThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (TitleSuggestThumb.IsMouseCaptured)
		{
			TitleSuggestThumb.ReleaseMouseCapture();
		}
	}

	private void TitleSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		// Tab = 采纳淡色补全并搜索。必须抢在 TextBox 前面：默认的 Tab 是
		// 把焦点移走，一旦移走用户敲的东西就白费了。
		// Enter 不在这里处理，维持原样：按已经敲进去的字搜，不补全。
		if (e.Key == Key.Tab && _ghost != null && _ghost.Suffix.Length > 0)
		{
			AcceptGhost();
			e.Handled = true;
			return;
		}
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
		// 完整搜索词 = 已包的标签卡 + 输入框里没包的词。卡片也是搜索词的一部分。
		string keyword = CurrentSearchQuery();
		if (keyword.Length == 0)
		{
			return;
		}
		CloseSuggest();
		CommitTitleSearchPlaceholder();
		PushSearchHistory(keyword);
		// 记一次搜索：补全排序和「猜你想找哪个圈子」都靠这个次数
		QueryBrain.NoteQuery(keyword);
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
		// 整词留在输入框里并全选：搜完还能一眼看到搜的是什么，
		// 想换词直接敲（会盖掉选中的那几个字）。
		SetTitleSearchText(keyword);
	}

	/// <summary>输入框空着才显示提示文字，否则挡住真实内容。</summary>
	private void CommitTitleSearchPlaceholder()
	{
		if (TitleSearchHint != null)
		{
			TitleSearchHint.Visibility = string.IsNullOrEmpty(TitleSearchBox.Text)
				? Visibility.Visible
				: Visibility.Collapsed;
		}
		// 清除键只在有内容时露出：空框给个 × 只会让人疑惑「清的是什么」。
		if (TitleSearchClear != null)
		{
			TitleSearchClear.Visibility = string.IsNullOrEmpty(TitleSearchBox.Text)
				? Visibility.Collapsed
				: Visibility.Visible;
		}
	}

	/// <summary>搜索框右侧清除键：一键清空并把焦点还回输入框，接着就能敲新词。</summary>
	private void TitleSearchClear_Click(object sender, RoutedEventArgs e)
	{
		TitleSearchBox.Text = "";
		CloseSuggest();
		TitleSearchBox.Focus();
		TitleSearchBox.CaretIndex = 0;
	}

	// ---- 标题栏搜索建议 ---------------------------------------------------
	//
	// 输入框只留用户敲的那个词（「默认展示词」），下面这个列表才是「都有哪种歌」，
	// 命中的关键词在歌名里高亮。
	//
	// 建议来源两处，各管一段：
	//   本地音乐库 —— 本地列表一筛就有，零延迟，先显示；
	//   网易云     —— 补上网才能搜到的歌，只打一个接口、限 6 条，
	//                而且是可取消的：用户还在打字就把上一次的请求撤掉，
	//                不然「云」「云鹤」「云鹤 live」会排着队一个个回来把界面刷乱。
	//
	// 方向键不接管：↑/↓ 在这里已经表示翻搜索历史（用户明确要的），
	// 列表只用鼠标点，别把两个语义搅在一起。

	// 本地命中上限。弹层可视高约 286px，一行 20px，14 行刚好塞满。
	private const int SuggestMaxRows = 14;
	// 在线结果一次要多少条。必须明显大于一屏能显示的行数（14），否则列表不溢出，
	// 「滑到底追加」永远触发不了 —— 详见 UpdateSuggestAsync 里 want 那段。
	private const int SuggestChunk = 30;

	// 行尾平台标签的文案。这些是各平台自己的名字，不跟着界面语言变。
	private const string PlatformLocal = "本地";
	private const string PlatformNetease = "网易云";
	private const string PlatformQq = "QQ";
	private const string PlatformKugou = "酷狗";

	private CancellationTokenSource? _suggestCts;
	private int _suggestToken;
	// 正在往弹层里增删行。增删会连带响 ScrollChanged，那几帧的偏移和
	// ScrollableHeight 都是中间态，靠这个标志挡住「滑到底就追加」的误触发。
	private bool _suggestRendering;
	// 爱好关键词轮着挑的游标，见 PickHobbyKeyword。
	private int _hobbyPickIndex;
	// 当前建议列表是不是那次「随机爱好词搜索」的结果。是的话空框点击直接复用，
	// 不再换词重搜（见 ShowHobbySuggest）；用户敲了真实关键词就作废。
	private bool _hobbySuggestActive;
	// 单击的待触发动作。双击会先把它作废（见 SuggestRow_DoubleClick）。
	private DispatcherTimer? _pendingSuggestClick;
	// 最近一次双击的时刻，用来挡掉双击之后 WPF 补发的那一下 Click。
	private DateTime _lastSuggestDoubleClickAt = DateTime.MinValue;

	// 追加用的状态：本地命中先存着，在线结果按页往里加，滑到底再加一批。
	private string _suggestKeyword = "";
	private readonly List<SuggestItem> _suggestLocal = new List<SuggestItem>();
	private readonly List<SuggestItem> _suggestOnline = new List<SuggestItem>();
	private bool _suggestLoadingMore;
	private bool _suggestExhausted;
	// 本次取之前在线有多少条，用来算这一批实际加进来多少条（决定要不要回弹动画）。
	private int _suggestOnlineBeforeFetch;
	// 触发这次追加时的滚动偏移，追加完弹回这里。
	private double _suggestTriggerOffset;
	// 拖滑块时记起点，MouseMove 里按位移换算偏移。
	private double _suggestDragStartY;
	private double _suggestDragStartOffset;

	/// <summary>排一次建议更新。**不等、不节流**：用户每敲一个字符都该立刻出结果。
	///
	/// 原来这里挂了个 260ms 的 DispatcherTimer 做防抖，敲「东象」要等 260ms
	/// 才动 —— 现在要的正好相反：不等。真正要防的是「旧结果盖掉新结果」，
	/// 那个由 _suggestToken + CancellationTokenSource 解决（见 UpdateSuggestAsync），
	/// 和防抖是两回事，不该拿防抖当挡箭牌。</summary>
	private void RequestSuggestUpdate()
	{
		if (!IsLoaded)
		{
			return;
		}
		// 真实关键词的搜索会把缓存列表整个换掉，随机结果标记随之作废。
		_hobbySuggestActive = false;
		_ = UpdateSuggestAsync(false, CurrentSearchQuery());
	}

	/// <param name="extend">true = 滑到底追加一批；false = 从头搜。</param>
	/// <param name="keyword">要搜的词。爱好推荐那条路传的是从设置里挑的词，
	/// 和输入框里的字没关系，所以从参数进而不是直接从输入框读。</param>
	private async Task UpdateSuggestAsync(bool extend, string keyword)
	{
		keyword = keyword.Trim();
		if (keyword.Length == 0)
		{
			CloseSuggest();
			return;
		}

		// 兴趣展开：敲的是简称就去搜它背后那一整片。
		// 敲「东方」要的是东方 Project 的同人曲，不是恰好叫「东方」的那一首，
		// 所以这里换成规范词「东方Project」再去搜；敲「pho」同理换成「phonk」。
		// 概率不够高（QueryBrain 里的 softmax 低于阈值）就原样搜，不自作主张。
		keyword = QueryBrain.Expand(keyword);

		// 换了词（或不是追加）就从头数：本地重扫、在线偏移清零。
		bool fresh = !extend || !string.Equals(keyword, _suggestKeyword, StringComparison.Ordinal);
		// 必须在下面那次 RenderSuggest 之前就置位：渲染会连带触发 ScrollChanged，
		// 滑到底的判断正看这个标志，晚一步就会在首屏渲染里又发一次追加请求。
		_suggestLoadingMore = true;
		if (fresh)
		{
			_suggestKeyword = keyword;
			_suggestOnline.Clear();
			_suggestLocal.Clear();
			_suggestLocal.AddRange(BuildLocalSuggestions(keyword));
			_suggestExhausted = false;
			// 本地先把列表铺上，用户马上能看见东西，不用等网络。
			// 本地一条都没有时**不要**渲染空列表：RenderSuggest 见到空列表会
			// CloseSuggest，于是弹层先关一次、等在线结果回来再开一次 ——
			// 用户看到的是「闪一下就没了」。这时候宁可先空着，等在线那半边。
			if (_suggestLocal.Count > 0)
			{
				RenderSuggest(_suggestLocal, true);
			}
		}

		// 上一轮的建议请求直接作废：返回时对一下 token，不是自己的结果就丢掉。
		int token = ++_suggestToken;
		_suggestCts?.Cancel();
		_suggestCts?.Dispose();
		var cts = new CancellationTokenSource();
		_suggestCts = cts;

		// 在线一次要多少条。首屏和追加都用同一个数。
		//
		// 原来首屏是「剩下的名额」：Math.Max(0, SuggestMaxRows - 本地命中数)，
		// 也就是不管搜什么，本地 + 在线永远刚好凑满 14 条。14 行正好等于弹层
		// 能显示的行数（286px / 20px），于是 ScrollableHeight 恒为 0：
		// 列表永远不溢出 → 滑不到底 → 追加那段代码永远进不去 →
		// 右侧滑块也永远只有一条短轨。「滑到底加载更多」等于没接上。
		//
		// 现在首屏就拿满 SuggestChunk 条，本地最多 14 + 在线 30，合计能到 44 行，
		// 明确超过一屏，滚动和追加才真的会发生。
		int want = SuggestChunk;
		_suggestOnlineBeforeFetch = _suggestOnline.Count;
		if (want <= 0)
		{
			_suggestLoadingMore = false;
			if (!extend)
			{
				RenderSuggest(_suggestLocal, true);
			}
			return;
		}

		try
		{
			List<SuggestItem> online = await FetchSuggestOnlineAsync(
				keyword, want, _suggestOnline.Count, cts.Token);
			if (token != _suggestToken || cts.IsCancellationRequested)
			{
				return;
			}
			int before = _suggestOnline.Count;
			_suggestOnline.AddRange(online);
			// 不满要的数量 = 接口后面没有了，下次滑到底就别再发请求了。
			if (_suggestOnline.Count - before < want)
			{
				_suggestExhausted = true;
			}
		}
		catch (OperationCanceledException)
		{
			// 用户还在打字，正常情况，不用管。
			return;
		}
		catch (Exception ex)
		{
			// 建议是锦上添花，接口挂了不该有任何动静，也不能影响打字。
			LogHelper.LogError(ex, "搜索建议失败: " + keyword);
			return;
		}
		finally
		{
			if (token == _suggestToken)
			{
				_suggestLoadingMore = false;
			}
		}
		if (token != _suggestToken)
		{
			return;
		}
		int appended = _suggestOnline.Count - _suggestOnlineBeforeFetch;
		RenderSuggest(_suggestLocal.Concat(_suggestOnline).ToList(), fresh);
		// 追加完把列表平滑弹回「触发这次追加的那个位置」。
		//
		// 追加是滑到底触发的，触发那一刻人正盯着列表最下面。新行接在后面，
		// 于是列表一下子变长、ScrollableHeight 跟着涨：滑块会按新比例缩窄挪位。
		// 偏移本身多半停在原处（刚才那几条不挪位，正是想要的），只有滑块在动，
		// 所以真正的复原动画是 AnimateSuggestThumb 那个补间；
		// 这里再兜一次底：要是内容重排把偏移带跑了（个别情况下 WPF 会在
		// 增删行后重新贴底），就把它平滑送回触发点。
		//
		// 回触发点而不是回顶部：回顶部会把人从刚才看的那一段整个掀走，
		// 而新加的那批本来就在下面，回到原处正好是「刚才那些不动、下面又多了几条」。
		if (extend && appended > 0)
		{
			SmoothScroll.AnimateTo(TitleSuggestScroll, _suggestTriggerOffset, 260);
		}
	}

	/// <summary>在线建议的数据源：网易云 → QQ → 酷狗，取第一个有结果的。
	///
	/// 原来只走网易云。它的 /api/search/get/web 现在稳定回 code=405
	/// （HTTP 200 但一条 songs 都取不到），于是「在线建议」永远是空的：
	/// 搜索框只列本地那几首，滑到底追加也永远没东西可加 ——
	/// 看着像功能没接上，其实是被一个死接口卡住了。
	///
	/// 换源不影响「双击能播」：建议行只带歌名/歌手，真正播放走的是
	/// <see cref="FindSuggestPlayableAsync"/>，它本来就是跨源找可播的那一条。</summary>
	private static async Task<List<SuggestItem>> FetchSuggestOnlineAsync(
		string keyword, int want, int offset, CancellationToken ct)
	{
		var acc = new List<SuggestItem>();
		if (want <= 0)
		{
			return acc;
		}
		async Task Try(Func<Task<List<SuggestItem>>> run)
		{
			if (acc.Count >= want)
			{
				return;
			}
			try
			{
				foreach (SuggestItem s in await run())
				{
					if (acc.Count >= want)
					{
						break;
					}
					acc.Add(s);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				// 单个源挂掉不影响下一个源，按「这个源没结果」处理。
			}
		}

		async Task<List<SuggestItem>> NeteaseRun() => (await NeteaseApiClient.SearchAsync(keyword, want, offset, ct))
			.Where(t => !string.IsNullOrWhiteSpace(t.Name))
			.Select(t => new SuggestItem(t.Name, t.ArtistNames, t.DurationMs, onlineTitle: t.Name,
				platform: PlatformNetease, vip: TrackAccess.IsRestricted(t)))
			.ToList();
		async Task<List<SuggestItem>> QqRun() => (await QqClient.SearchAsync(keyword, want, offset, ct))
			.Where(t => !string.IsNullOrWhiteSpace(t.Name))
			.Select(t => new SuggestItem(t.Name, t.Singer, t.DurationMs, onlineTitle: t.Name,
				platform: PlatformQq, vip: TrackAccess.IsRestricted(t)))
			.ToList();
		async Task<List<SuggestItem>> KugouRun() => (await KugouClient.SearchAsync(keyword, want, offset, ct))
			.Where(t => !string.IsNullOrWhiteSpace(t.FileName))
			.Select(t => new SuggestItem(
				Path.GetFileNameWithoutExtension(t.FileName), t.SingerName, t.Duration, onlineTitle: t.FileName,
				platform: PlatformKugou, vip: TrackAccess.IsRestricted(t)))
			.ToList();

		foreach (Func<Task<List<SuggestItem>>> run in OrderedSuggestSources(NeteaseRun, QqRun, KugouRun))
		{
			await Try(run);
		}
		return acc;
	}

	/// <summary>三个在线源按「首选平台」排尝试顺序。
	/// 设置里选的平台排队首，其余保持默认顺序（网易云 → QQ → 酷狗）：
	/// 常用平台在最前意味着大多数请求第一发就命中，省掉前面几个源
	/// 各自一次尝试的时间；首选源挂了仍然顺次落到后面，兜底语义不变。</summary>
	private static List<Func<Task<List<SuggestItem>>>> OrderedSuggestSources(
		Func<Task<List<SuggestItem>>> netease, Func<Task<List<SuggestItem>>> qq, Func<Task<List<SuggestItem>>> kugou)
	{
		string first = (SettingsManager.Instance.FirstSearchPlatform ?? "").Trim().ToLowerInvariant();
		return first switch
		{
			"netease" => new List<Func<Task<List<SuggestItem>>>> { netease, qq, kugou },
			"qq" => new List<Func<Task<List<SuggestItem>>>> { qq, netease, kugou },
			"kugou" => new List<Func<Task<List<SuggestItem>>>> { kugou, netease, qq },
			_ => new List<Func<Task<List<SuggestItem>>>> { netease, qq, kugou }
		};
	}

	/// <summary>从本地音乐库里挑出最像的几首。</summary>
	private List<SuggestItem> BuildLocalSuggestions(string keyword)
	{
		var rows = new List<SuggestItem>();
		List<string> library = MusicService.KnownMusic;
		if (library.Count == 0)
		{
			return rows;
		}
		string filterKey = SearchMatcher.Normalize(keyword);
		if (filterKey.Length == 0)
		{
			return rows;
		}
		foreach (string file in library)
		{
			string full = Path.GetFileName(file);
			if (!TrackRows.MatchesText(full, keyword, filterKey))
			{
				continue;
			}
			string name = Path.GetFileNameWithoutExtension(file);
			rows.Add(new SuggestItem(name, "", 0, localPath: file, platform: PlatformLocal));
			if (rows.Count >= SuggestMaxRows)
			{
				break;
			}
		}
		return rows;
	}

	/// <summary>按「跟猜中的那个圈子贴不贴」排序。
	/// 数的是圈子里的社团名/艺人名/曲名在歌名和歌手里命中了几个；
	/// 一个都没命中的保持原来的先后顺序，不会把搜索结果搅乱。</summary>
	private static IReadOnlyList<SuggestItem> RankByInterest(IReadOnlyList<SuggestItem> rows)
	{
		try
		{
			var (topic, prob) = QueryBrain.Last;
			if (topic == null || prob < QueryBrain.TrustThreshold || rows.Count < 2)
			{
				return rows;
			}
			var scored = new List<(SuggestItem Item, int Index, int Score)>(rows.Count);
			for (int i = 0; i < rows.Count; i++)
			{
				scored.Add((rows[i], i, QueryBrain.RelatedScore(rows[i].Title, rows[i].Artist)));
			}
			// 稳定排序：同分的按原来的先后排，接口给的顺序本身就是相关性顺序
			var ordered = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index).ToList();
			return ordered.Select(x => x.Item).ToList();
		}
		catch
		{
			return rows;
		}
	}

	private void RenderSuggest(IReadOnlyList<SuggestItem> rows, bool fresh)
	{
		// 高亮用的词要跟这批结果实际是搜什么对上。爱好推荐那条路里输入框是空的，
		// 现读 TitleSearchBox.Text 只会拿到空串，命中高亮就全丢了。
		string keyword = _suggestKeyword;
		// 命中某个圈子时按「跟这个圈子有多贴」重排：敲「东方」时，上海アリス幻樂団、
		// IOSYS 这些社团的曲子排在前面，恰好叫「东方」的那一首往后靠。
		// 只在新起一批时排：追加那批是接着已有行往下接的，
		// 这时重排会把已经显示的行整个打乱，看着像列表自己在跳。
		if (fresh)
		{
			rows = RankByInterest(rows);
			// 这一批结果里的歌名记进补全引擎：下次敲开头几个字就补出整条歌名。
			// 只在新起一批时记 —— 追加那批是同一批结果的后半截，再记一遍没意义。
			QueryBrain.NoteTitles(rows.Select(r => r.Title));
		}
		// 追加只往末尾接新行、绝不碰已有行。之前不管哪种情况都是
		// Items.Clear() 再把全部重灌一遍，追加时等于把整张列表拆了重建：
		// ScrollViewer 的偏移跟着被拽回底部，内容重建到一半时 ScrollableHeight
		// 一度变 0，「滑到底就追加」在渲染过程里被反复命中，于是追加→重建→
		// 又判定到底，滚一次能拉出 150 行，右侧滑块和复原动画一起被废掉。
		_suggestRendering = true;
		int added = 0;
		try
		{
			int existing = TitleSuggestList.Items.Count;
			if (fresh || existing > rows.Count)
			{
				TitleSuggestList.Items.Clear();
				existing = 0;
			}
			for (int i = existing; i < rows.Count; i++)
			{
				TitleSuggestList.Items.Add(BuildSuggestRow(rows[i], keyword));
				added++;
			}
		}
		finally
		{
			_suggestRendering = false;
		}

		if (TitleSuggestList.Items.Count == 0)
		{
			CloseSuggest();
			return;
		}
		if (!TitleSuggestPopup.IsOpen)
		{
			TitleSuggestPopup.IsOpen = true;
		}
		// 每次真打开都推一代号：CloseSuggest 的淡出走了 140ms，
		// 期间用户接着打字又弹出新列表时，淡出完成不能把新列表误关掉。
		_suggestOpenGen++;
		// 追加前先把滑块现在长什么样记下来：它待会儿要按新比例缩窄上移，
		// 这一下直接设看着很生硬，补间过去顺眼得多。必须在 UpdateSuggestSlider
		// 之前读 —— 那一步就是改它几何的地方。
		double fromHeight = TitleSuggestThumb.Height;
		double fromTop = TitleSuggestThumb.Margin.Top;
		// 条数变了，滑栏的比例和数字要跟着重算。
		// Popup 刚开的那一帧 ViewportHeight 还是 0，ScrollChanged 会在
		// 布局完成后补一次 UpdateSuggestSlider，所以这里先算一遍也不冲突。
		UpdateSuggestSlider();
		// 只在真追加（接了新行、且不是整表重建）时补间。换词是重建，那不是「复原」。
		if (added > 0 && !fresh)
		{
			AnimateSuggestThumb(fromHeight, fromTop);
		}
	}

	/// <summary>把右侧滑块从 <paramref name="fromHeight"/>/<paramref name="fromTop"/> 补间到当前几何。
	///
	/// 追加是在底部触发的，偏移本身停在原处不动（这正是想要的：新行接在下面，
	/// 刚才看的那几条不挪位），所以真正「跳」的是滑块 —— 一条几乎占满可视区的
	/// 滑块唰一下缩成一小截并跑上去。只补间滑块，列表和滚动条全程不抖。</summary>
	private void AnimateSuggestThumb(double fromHeight, double fromTop)
	{
		if (TitleSuggestThumb == null)
		{
			return;
		}
		double toHeight = TitleSuggestThumb.Height;
		double toTop = TitleSuggestThumb.Margin.Top;
		if (Math.Abs(toHeight - fromHeight) < 0.5 && Math.Abs(toTop - fromTop) < 0.5)
		{
			return;
		}
		try
		{
			var duration = TimeSpan.FromMilliseconds(260);
			// 两个动画各给一个自己的 EasingFunction 实例：QuadraticEase 是 Freezable，
			// 共用一个会被第一个动画的时钟冻上，第二个再拿去用就抛异常。
			TitleSuggestThumb.BeginAnimation(FrameworkElement.HeightProperty,
				new DoubleAnimation(fromHeight, toHeight, duration)
				{
					EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
				});
			TitleSuggestThumb.BeginAnimation(FrameworkElement.MarginProperty,
				new ThicknessAnimation(new Thickness(0, toTop, 0, 0), duration)
				{
					EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
				});
		}
		catch (Exception ex)
		{
			// 补间是纯装饰，失败就停在那儿，绝不能让它把 UI 线程带崩。
			LogHelper.LogError(ex, "建议滑块复原补间失败");
		}
	}


	private FrameworkElement BuildSuggestRow(SuggestItem item, string keyword)
	{
		// 重做成「小卡片」：每条建议都是一张带圆角边框的小卡片，
		// 浮层底上卡片与卡片之间留 2px 缝，视觉上一行一张卡，
		// 不会再像第一版那样跟弹层底色糊成一片。
		var panel = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };

		// 第一行：左边歌名，右边平台 / VIP 小标签。
		// 用 Grid 而不是把标签拼进歌名里 —— 标签要钉在右边，歌名再长也挤不掉它；
		// 而且歌名要继续吃 TextTrimming，挤长了就省略，不能把标签顶到第二行去。
		var head = new Grid();
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		var title = new TextBlock
		{
			FontSize = 12.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Primary(),
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		TrackRows.FillHighlight(title, item.Title, keyword);
		Grid.SetColumn(title, 0);
		head.Children.Add(title);

		FrameworkElement? badges = BuildSuggestBadges(item);
		if (badges != null)
		{
			Grid.SetColumn(badges, 1);
			head.Children.Add(badges);
		}
		panel.Children.Add(head);

		string sub = item.LocalPath.Length > 0
			? Loc.Default["sug_local"]
			: item.Artist.Length > 0
				? item.Artist + (item.DurationMs > 0 ? "  ·  " + TrackRows.FormatDuration(item.DurationMs) : "")
				: "";
		if (sub.Length > 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = sub,
				FontSize = 11,
				Foreground = ThemeManager.Tertiary(),
				TextTrimming = TextTrimming.CharacterEllipsis,
				Margin = new Thickness(0, 1, 0, 0)
			});
		}

		var row = new Button
		{
			Content = panel,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			Padding = new Thickness(0),
			Margin = new Thickness(5, 3, 5, 0),
			BorderThickness = new Thickness(1),
			Cursor = Cursors.Hand,
			Tag = item
		};
		// 卡片底和卡片描边走资源引用而不是固定笔刷：切主题时
		// DynamicResource 自己就会换，行还没重生也不至于用错一时
		// 的配色。SetResourceReference 是运行时挂 DynamicResource 的唯一入口。
		row.SetResourceReference(Control.BackgroundProperty, "CardBgBrush");
		row.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");
		// 建议行不吃焦点：不然点一下输入框就丢了焦点，接着打字没地方打。
		row.Focusable = false;
		row.Click += SuggestRow_Click;
		row.MouseDoubleClick += SuggestRow_DoubleClick;
		return row;
	}

	/// <summary>行尾那排小标签：平台一定打，VIP 只在接口说了要会员时才打。
	///
	/// 配色对齐全站行内标签（TrackRows.MakeChip）：平台用各家品牌实色、
	/// VIP 用金色、字用 OnAccent 白。之前用的是主题文字色 + 16~20% 低透明底，
	/// 深色弹层上整条标签看起来就是灰白一片 ——「颜色没渲染」。</summary>
	private FrameworkElement? BuildSuggestBadges(SuggestItem item)
	{
		var box = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(6, 0, 0, 0)
		};
		if (item.Platform.Length > 0)
		{
			box.Children.Add(MakeSuggestTag(item.Platform, SuggestPlatformBrush(item.Platform)));
		}
		if (item.Vip)
		{
			box.Children.Add(MakeSuggestTag("VIP", SuggestVipBrush));
		}
		return box.Children.Count == 0 ? null : box;
	}

	// 品牌色和 TrackRows.SavedEngineTag / VIP 金保持同一套值，
	// 建议行和列表行的标签才不会一边有色一边没色。
	private static readonly Brush SuggestVipBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x1F));

	private static Brush SuggestPlatformBrush(string platform) => platform switch
	{
		PlatformNetease => new SolidColorBrush(Color.FromRgb(0xC2, 0x0C, 0x0C)),
		PlatformQq => new SolidColorBrush(Color.FromRgb(0x31, 0xC2, 0x7C)),
		PlatformKugou => new SolidColorBrush(Color.FromRgb(0x2C, 0xA5, 0xF6)),
		PlatformLocal => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
		_ => ThemeManager.Accent()
	};

	private Border MakeSuggestTag(string text, Brush background)
	{
		return new Border
		{
			CornerRadius = new CornerRadius(3),
			Background = background,
			Padding = new Thickness(4, 1, 4, 1),
			Margin = new Thickness(3, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			SnapsToDevicePixels = true,
			Child = new TextBlock
			{
				Text = text,
				FontSize = 9,
				FontWeight = FontWeights.SemiBold,
				Foreground = TrackRows.OnAccentBrush,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
	}


	// 单击和双击要分开处理，但 Button 在双击时会先连发两次 Click 再报 MouseDoubleClick，
	// 直接各挂一个处理器的话双击会「先播一次、再跳转」。
	// 所以单击不立刻执行，压一个短延时；真等到双击就把这次单击作废。
	private const int SuggestClickDelayMs = 220;

	private void SuggestRow_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button button || button.Tag is not SuggestItem item)
		{
			return;
		}
		// 连点两下不同的行时，后一次单击要顶掉前一次排队的动作，
		// 不然 220ms 后激活的还是上一次点的那一行。同一行连点两次则由
		// MouseDoubleClick 接手（见下），那里会把队列作废。
		// 但 WPF 的事件顺序是 Click → MouseDoubleClick → Click：
		// 双击判定之后还会**再补一次 Click**，这一次必须丢掉，
		// 否则它会重新排一个单击队列，220ms 后照样把这首播出去，
		// 双击跳搜索页就白做了。用时间戳挡掉这一下，窗口取得太宽也会误伤正常单击。
		if ((DateTime.Now - _lastSuggestDoubleClickAt).TotalMilliseconds < 400)
		{
			return;
		}
		// 保护期里再点别的行：不丢这一次点击，记下来当「追击」——
		// 上一首取完流就接力播它（见 EndSuggestProtect 里的 _suggestChaseItem）。
		// 手快连点两三下只留最后一下，免得排一队流互相抢播。
		// 这正是「结果出来了但过早点击没反应」的解药：点击不再被吞。
		if (_suggestProtectActive)
		{
			_suggestChaseItem = item;
			string chaseName = item.OnlineTitle.Length > 0 ? item.OnlineTitle : item.Title;
			ShowToast("已排上：" + chaseName + "，上一首出来接着播");
			return;
		}
		_pendingSuggestClick?.Stop();
		var timer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(SuggestClickDelayMs)
		};
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			_pendingSuggestClick = null;
			ActivateSuggest(item);
		};
		_pendingSuggestClick = timer;
		timer.Start();
	}

	private void SuggestRow_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		// 记下时间，好把 WPF 事后补的那一下 Click 挡掉。
		_lastSuggestDoubleClickAt = DateTime.Now;
		if (sender is not Button button || button.Tag is not SuggestItem item)
		{
			return;
		}
		// 双击优先：把还没触发的单击作废，避免「先播一遍再跳页」。
		_pendingSuggestClick?.Stop();
		_pendingSuggestClick = null;
		// 双击 = 单击直拉流之前的老行为：跳到在线搜索页。
		// 下载、评论这些操作都还在搜索页里，留一条能过去的路。
		// 这也说明用户不打算等刚才那首了，保护期直接作废。
		CancelSuggestProtect();
		CloseSuggest();
		if (item.LocalPath.Length > 0)
		{
			MusicService.Play(item.LocalPath);
			return;
		}
		SetTitleSearchText(item.OnlineTitle);
		SubmitTitleSearch();
	}

	/// <summary>单击 = 直接播：本地立刻播，网上的按启用源搜真实曲目拉流，
	/// 不再先跳搜索页重搜一遍 —— 用户明确要「点一下直接拉音乐流不用再搜一次」。
	/// 想要下载 / 评论这些操作就双击进搜索页（见 SuggestRow_DoubleClick）。</summary>
	private void ActivateSuggest(SuggestItem item)
	{
		// 本地歌马上就响，不用保护；在线的进取流保护期：
		// 列表留着并显示「正在取流」，取到流播起来才收（见 BeginSuggestProtect）。
		// 保护期不设超时——成没成由结果说话；期间再点别的行会记成
		// 「追击」，上一首出来接着播，点击不会被吞。
		if (item.LocalPath.Length > 0)
		{
			CloseSuggest();
		}
		else
		{
			string label = item.OnlineTitle.Length > 0 ? item.OnlineTitle : item.Title;
			BeginSuggestProtect(label);
		}
		PlaySuggestDirect(item);
	}

	/// <summary>直接播，不跳页面。本地条目立刻播；网上的条目手上只有一个标题、
	/// 没有平台 ID，所以先按启用中的源搜一遍拿真实曲目再播 —— 走的是和各平台
	/// 客户端一样的「下载到缓存 → 并入曲库 → 播放」，跟在线搜索页里点那一下
	/// 是同一条路径。全部只用公开 API，不去碰 OnlineMusicPage 的私有状态。</summary>
	private async void PlaySuggestDirect(SuggestItem item)
	{
		// 播了的这首记 3 分：出现在结果里只是「见过」，播下去才是「要的就是它」。
		// 下次敲同样的前缀，这条歌名在补全里的权重因此压过其它候选。
		QueryBrain.NoteTitle(item.Title, 3);
		if (item.LocalPath.Length > 0)
		{
			MusicService.Play(item.LocalPath);
			return;
		}
		string keyword = item.OnlineTitle;
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return;
		}
		try
		{
			object? best = await FindSuggestPlayableAsync(keyword);
			if (best == null)
			{
				ShowToast(Loc.Default["online_none"]);
				// 没超时机制了，失败这条路必须自己收尾：不收的话状态条一直转、
				// 列表一直开着，后面点的每一首都只排队不见播。
				EndSuggestProtect(false);
				return;
			}
			await PlaySuggestTrackAsync(best);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "建议双击播放失败: " + keyword);
			// 抛到这儿也是失败：保护期照样要收（见上）。
			// 丢弃返回值是故意的：收尾只要排队执行，不用等它。
			_ = Dispatcher.BeginInvoke(new Action(() => EndSuggestProtect(false)));
		}
	}

	/// <summary>按设置里勾选的源并行搜一遍，挑一个最贴题且不受限的结果。</summary>
	private static async Task<object?> FindSuggestPlayableAsync(string keyword)
	{
		var engines = SettingsManager.Instance.OnlineEngines ?? new List<string>();
		var wanted = new HashSet<string>(engines, StringComparer.OrdinalIgnoreCase);
		var tasks = new List<Task<List<object>>>();

		async Task<List<object>> Wrap(Func<Task<List<object>>> run)
		{
			try
			{
				return await run();
			}
			catch
			{
				// 单个源挂掉不影响其它源，按「这个源没结果」处理。
				return new List<object>();
			}
		}

		if (wanted.Count == 0 || wanted.Contains("netease"))
			tasks.Add(Wrap(async () => (await NeteaseApiClient.SearchAsync(keyword, 10)).Cast<object>().ToList()));
		if (wanted.Count == 0 || wanted.Contains("qq"))
			tasks.Add(Wrap(async () => (await QqClient.SearchAsync(keyword, 10)).Cast<object>().ToList()));
		if (wanted.Count == 0 || wanted.Contains("kugou"))
			tasks.Add(Wrap(async () => (await KugouClient.SearchAsync(keyword, 10)).Cast<object>().ToList()));

		List<object>[] all = await Task.WhenAll(tasks);
		object? best = null;
		double bestScore = double.MinValue;
		foreach (object candidate in all.SelectMany(x => x))
		{
			// 受限条目往后压而不是直接跳过：平台字段错得挺多，一票否决的话
			// 一首其实能听的歌就永远选不上，最后凑合用的反倒是最不相干的那条。
			double s = SearchMatcher.Score(SuggestTitle(candidate), SuggestArtist(candidate), keyword);
			if (SuggestIsRestricted(candidate))
			{
				s -= 1000;
			}
			if (s > bestScore)
			{
				bestScore = s;
				best = candidate;
			}
		}
		return best;
	}

	private static bool SuggestIsRestricted(object item) => TrackAccess.IsRestricted(item);

	private static string SuggestTitle(object item) => item switch
	{
		NeteaseTrack t => t.Name,
		KugouTrack k => k.Display,
		QqTrack q => q.Name,
		_ => ""
	};

	private static string SuggestArtist(object item) => item switch
	{
		NeteaseTrack t => t.ArtistNames,
		KugouTrack k => k.SingerName,
		QqTrack q => q.Singer,
		_ => ""
	};

	/// <summary>下载 → 并入库 → 播放。和在线搜索页里 PlayQQ/PlayKugou 同一套做法。</summary>
	private async Task PlaySuggestTrackAsync(object item)
	{
		string title = SuggestTitle(item);
		switch (item)
		{
		case NeteaseTrack net:
			MusicService.PlayOnline(net, err =>
			{
				LogHelper.Log("建议播放失败(网易云): " + (err ?? ""));
				// 取流失败也要收掉保护期（见 PlaySuggestDirect 里的同一处理）。
				// 回调可能来自后台线程，EndSuggestProtect 要动界面，切回 UI 线程。
				Dispatcher.BeginInvoke(new Action(() => EndSuggestProtect(false)));
			});
			return;
		case QqTrack qq:
		{
			string? local = await QqClient.DownloadSongAsync(qq);
			if (string.IsNullOrEmpty(local))
			{
				ShowToast(Loc.Default["online_vip"]);
				EndSuggestProtect(false);
				return;
			}
				string imported = MusicService.ImportToLibrary(local, qq.Display);
				MusicService.RememberDownload(qq.Display, imported);
				var qqMeta = new SavedTrack
				{
					Type = "qq",
					Id = qq.Mid,
					Title = qq.Display,
					Artist = qq.Singer,
					DurationMs = qq.DurationMs,
					Cover = qq.CoverUrl
				};
				MusicService.PlayOnlineLocal(imported, qqMeta, qq.Display);
				return;
			}
		case KugouTrack kg:
		{
			string? local = await KugouClient.DownloadSongAsync(kg);
			if (string.IsNullOrEmpty(local))
			{
				ShowToast(Loc.Default["online_vip"]);
				EndSuggestProtect(false);
				return;
			}
				string imported = MusicService.ImportToLibrary(local, kg.Display);
				var kgMeta = new SavedTrack
				{
					Type = "kugou",
					Id = kg.Hash,
					Title = kg.Display,
					Artist = kg.SingerName,
					DurationMs = kg.DurationMs,
					Cover = kg.ImgUrl
				};
				MusicService.PlayOnlineLocal(imported, kgMeta, kg.Display);
				return;
			}
		}
	}

	/// <summary>程序化写输入框：走 _suppressSearchHistory，免得把历史指针搅乱。</summary>
	private void SetTitleSearchText(string text)
	{
		_suppressSearchHistory = true;
		try
		{
			TitleSearchBox.Text = text;
			TitleSearchBox.SelectAll();
			TitleSearchBox.CaretIndex = text.Length;
		}
		finally
		{
			_suppressSearchHistory = false;
		}
		CommitTitleSearchPlaceholder();
	}

	/// <summary>收起建议列表的对外入口：带 140ms 淡出，看见的收尾匀一点。
	/// 不带过程开销的场合（拖窗、进全屏、开始上拉）直接调 CloseSuggestNow。
	/// 防护：淡出期间列表可能又被打开（用户继续打字），那些场合不收尾。</summary>
	private bool _suggestFadeClosing;
	private int _suggestOpenGen;

	// ===== 建议列表的「取流保护期」=====
	// 点一条在线结果要走「搜真实曲目 → 拉直链 → 下载 → 入库 → 播放」，
	// 快的两三秒，慢的（VIP、冷门、接口抽风）十几秒甚至更久。
	// 原来点完立刻 CloseSuggest：列表当场消失，界面上又没有任何动静，
	// 看着就是「点了没反应、列表还自己跑了」。
	// 现在点完进保护期：列表留着、底下挂一条「正在取流：xxx」，
	// 歌真的播起来才收。
	//
	// 刻意**不设超时**：以前守 30 秒，到点就把列表留下并弹一句
	// 「等了 30 秒还没取到」，可很多时候只是接口慢一点，再等两秒就成了。
	// 什么时候结束由结果说话：播起来了、接口明确失败、或者用户自己
	// 去做别的事（CancelSuggestProtect），三条路之外就一直等着。
	private DispatcherTimer? _suggestProtectTimer;
	private bool _suggestProtectActive;
	private bool _suggestProtectCloseRequested;
	private string? _suggestProtectBaseline;
	private int _suggestProtectBaselineGen;
	/// <summary>保护期里又被点了一次的那一条。上一首取完流就接力播它，
/// 手快点两下不该变成「第二下白点」。</summary>
	private SuggestItem? _suggestChaseItem;

	private void BeginSuggestProtect(string label)
	{
		_suggestProtectActive = true;
		_suggestProtectCloseRequested = false;
		_suggestChaseItem = null;
		_suggestProtectBaseline = MusicService.CurrentPath;
		_suggestProtectBaselineGen = MusicService.PlayGeneration;
		try
		{
			if (TitleSuggestStatus != null)
			{
				TitleSuggestStatus.Visibility = Visibility.Visible;
				TitleSuggestStatusText.Text = "正在取流：" + label;
			}
			var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
			{
				RepeatBehavior = RepeatBehavior.Forever
			};
			TitleSuggestSpinRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
		}
		catch
		{
		}
		_suggestProtectTimer?.Stop();
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		timer.Tick += (_, _) => SuggestProtectTick();
		_suggestProtectTimer = timer;
		timer.Start();
	}

	private void SuggestProtectTick()
	{
		if (!_suggestProtectActive)
		{
			_suggestProtectTimer?.Stop();
			_suggestProtectTimer = null;
			return;
		}
		bool playing = false;
		try
		{
			playing = MusicService.IsPlaying && !MusicService.NoMusicMode;
		}
		catch
		{
		}
		string? now = MusicService.CurrentPath;
		// 换了歌、或者同一首歌重新起了一轮播放，而且在播 = 那一首真的起来了。
		// 带上播放世代是为了兜住「点的就是正在播的那首」：路径没变，
		// 但 PlayGeneration 变了，说明确实重拉了一次流并播起来了。
		bool started = MusicService.PlayGeneration != _suggestProtectBaselineGen;
		if (playing && now != null && (started
			|| !string.Equals(now, _suggestProtectBaseline, StringComparison.OrdinalIgnoreCase)))
		{
			EndSuggestProtect(true);
		}
	}

	/// <summary>保护期结束。成了就收列表（歌已经播了，不用再看着）；
	/// 没成就把列表留着，只提示一句，用户可以直接再点一次或换一条。
	///
	/// 收尾前先看一眼保护期里有没有被再点过一次（_suggestChaseItem）：
	/// 有就接力播它，手快连点两下不会变成「第二下白点」。</summary>
	private void EndSuggestProtect(bool ok)
	{
		// 幂等：取消过（双击跳搜索页）之后迟到的失败回调也要能安全返回，
		// 不能把用户已经翻过篇的状态又拖回来弹一次提示。
		if (!_suggestProtectActive)
		{
			return;
		}
		_suggestProtectActive = false;
		_suggestProtectTimer?.Stop();
		_suggestProtectTimer = null;
		try
		{
			TitleSuggestSpinRotate.BeginAnimation(RotateTransform.AngleProperty, null);
			if (TitleSuggestStatus != null)
			{
				TitleSuggestStatus.Visibility = Visibility.Collapsed;
				TitleSuggestStatusText.Text = "";
			}
		}
		catch
		{
		}
		bool closeIt = ok || _suggestProtectCloseRequested;
		if (closeIt)
		{
			CloseSuggest(force: true);
		}
		SuggestItem? chase = _suggestChaseItem;
		_suggestChaseItem = null;
		if (chase != null)
		{
			// 追击：接着拉这一首的流，保护期重新开始计时。
			ActivateSuggest(chase);
			return;
		}
		if (!closeIt)
		{
			ShowToast("这首歌没取到流，列表给你留着，可以再点一次或换一条");
		}
	}

	/// <summary>用户主动去做别的事（比如双击另一行跳搜索页）：保护期作废，列表照收。</summary>
	private void CancelSuggestProtect()
	{
		if (!_suggestProtectActive)
		{
			return;
		}
		_suggestProtectActive = false;
		_suggestProtectCloseRequested = false;
		// 用户主动翻篇：排着队的追击也一起作废，不然切完源回来
		// 突然又播一首早就被放弃的歌。
		_suggestChaseItem = null;
		_suggestProtectTimer?.Stop();
		_suggestProtectTimer = null;
		try
		{
			TitleSuggestSpinRotate.BeginAnimation(RotateTransform.AngleProperty, null);
			if (TitleSuggestStatus != null)
			{
				TitleSuggestStatus.Visibility = Visibility.Collapsed;
				TitleSuggestStatusText.Text = "";
			}
		}
		catch
		{
		}
	}

	private void CloseSuggest(bool force = false)
	{
		if (_suggestProtectActive && !force)
		{
			// 歌还在路上，列表先别收：记下来，保护期结束时再收。
			_suggestProtectCloseRequested = true;
			return;
		}
		CloseSuggestCore();
	}

	private void CloseSuggestCore()
	{
		if (!TitleSuggestPopup.IsOpen || TitleSuggestPopup.Child is not UIElement child)
		{
			CloseSuggestNow();
			return;
		}
		if (_suggestFadeClosing)
		{
			// 淡出已经在路上，Completed 会负责收尾，这里别叠第二层。
			return;
		}
		try
		{
			child.BeginAnimation(OpacityProperty, null);
			double from = child.Opacity;
			if (from <= 0.02)
			{
				CloseSuggestNow();
				return;
			}
			_suggestFadeClosing = true;
			int gen = _suggestOpenGen;
			var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(140))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
			};
			fade.Completed += (_, _) =>
			{
				child.BeginAnimation(OpacityProperty, null);
				child.Opacity = 1.0;
				_suggestFadeClosing = false;
				// 淡出这 140ms 里如果又弹出了新的一轮（gen 推进了），
				// 现在挂着的是新列表，不能顺手关掉。
				if (_suggestOpenGen == gen)
				{
					CloseSuggestNow();
				}
			};
			child.BeginAnimation(OpacityProperty, fade);
		}
		catch
		{
			_suggestFadeClosing = false;
			CloseSuggestNow();
		}
	}

	private void CloseSuggestNow()
	{
		// 收起列表时把没触发的单击也撤掉：否则延迟里存的旧条目会在列表
		// 都已经关掉之后才生效，跳到一个用户早就没在看的搜索上去。
		_pendingSuggestClick?.Stop();
		_pendingSuggestClick = null;
		_suggestFadeClosing = false;
		if (TitleSuggestPopup.IsOpen)
		{
			var child = TitleSuggestPopup.Child as UIElement;
			if (child != null)
			{
				child.BeginAnimation(OpacityProperty, null);
				child.Opacity = 1.0;
			}
			TitleSuggestPopup.IsOpen = false;
		}
		TitleSuggestList.Items.Clear();
		_suggestOnline.Clear();
		_suggestLocal.Clear();
		_suggestKeyword = "";
		_suggestExhausted = false;
	}

	/// <summary>点主窗口上搜索框以外的地方就收起建议。
	///
	/// 用「主窗口上的按下事件」而不是 Window.Deactivated 来判断：
	/// Popup 用的是 StaysOpen=True，自己不会在点外面时消失，所以得有人管；
	/// 而一旦改成靠失焦来判断，只要 Popup 抢走激活（不同窗口、不同 WPF 版本
	/// 行为不完全一致），点建议行就会先触发关闭、Click 根本等不到，表现为「点了没反应」。
	/// 主窗口收不到 Popup 里的按下事件，所以列表内部的点击不会误关自己。
	/// 点搜索框本身也不关：用户可能只是回来接着改词。</summary>
	private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		DependencyObject? source = e.OriginalSource as DependencyObject;
		if (TitleSuggestPopup.IsOpen && !IsInsideTitleSearch(source))
		{
			CloseSuggest();
		}
		// 「点程序里任何地方都收起评论抽屉」：点抽屉内部、或点开关键本身除外。
		// 开关键（行内评论键、全屏页评论键）留着自己的 Toggle 语义（同曲收起），
		// 不在这里先关掉，否则点击会被拆成「先关再开」，同曲收起就失灵了。
		if (CommentsPanel != null && CommentsPanel.IsOpen
			&& !IsInsideCommentsPanel(source) && !IsCommentToggle(source))
		{
			CloseComments();
		}
		// 音量浮层改成了 StaysOpen=True（否则它自带的鼠标捕获会和滑条的拖拽
		// 抢同一份捕获），所以要在这里补「点程序别处就收起」：
		// 浮层是独立 HWND，点在它里面的事件根本不会走到窗口这一层，
		// 能进到这里就说明点在窗口自己身上 —— 除了音量键本身（它负责开关切换）。
		// 另外再用光标矩形兜一道：失活/捕获切换那一瞬间会有些边角事件漏进来，
		// 光标明明还在浮层里却被判成「点外面」，浮层就当场消失了。
		if (VolumePopup.IsOpen && !MiniVolumeButton.IsMouseOver && !CursorInsideVolumePopup())
		{
			CloseVolumePopup();
		}
	}

	/// <summary>这一下按下的原始元素是不是长在评论抽屉里（含滑入动画期间的子树）。</summary>
	private bool IsInsideCommentsPanel(DependencyObject? node)
	{
		while (node != null)
		{
			if (ReferenceEquals(node, CommentsPanel))
			{
				return true;
			}
			node = node is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(node)
				: null;
		}
		return false;
	}

	/// <summary>是不是点在「评论开关键」上（行内评论键 / 全屏页评论键都带了
	/// Tag=CommentToggle）。这类键自己会处理开合，失焦收起要让开。</summary>
	private static bool IsCommentToggle(DependencyObject? node)
	{
		while (node != null)
		{
			if (node is FrameworkElement fe && fe.Tag is string tag && tag == "CommentToggle")
			{
				return true;
			}
			node = node is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(node)
				: null;
		}
		return false;
	}

	/// <summary>主窗口失去激活时把浮层收掉。
	///
	/// Popup 是独立的顶层 HWND，WPF 建它的时候带 WS_EX_NOACTIVATE，
	/// 所以切到别的程序之后它不会跟着主人一起沉下去 —— 表现就是
	/// 「搜索列表卡在别的程序上面」，人已经切走了它还杵在屏幕上。
	/// 设置页那些下拉列表是同一个道理（都走 Popup）。
	///
	/// 这里不是「一律关掉」：点建议行时不同 WPF 版本对 Popup 会不会短暂
	/// 抢激活的行为并不一致，一律关会把行的 Click 吃掉，表现为点了没反应。
	/// 所以鼠标还停在浮层里就留着，停在别处才收。</summary>
	private void Window_Deactivated(object? sender, EventArgs e)
	{
		// 失活时收掉搜索建议浮层，免得它留在别的程序上面。
		if (TitleSuggestPopup.IsOpen && !CursorInsideSuggestPopup())
		{
			CloseSuggest();
		}
		// 设置页的下拉/勾选浮层同样收掉，否则会留在别的程序上面。
		if (ContentHost.Content is SettingsPage sp)
		{
			sp.CloseFloatingDropdowns();
		}
		// 音量浮层是 StaysOpen=True：切到别的程序时不会自己关，这里收掉。
		// 光标还在浮层里就不收：点浮层里的滑条也会让主窗口短暂失活
		// （Popup 抢一次激活），不收才拖得动，见 CursorInsideVolumePopup。
		if (VolumePopup.IsOpen && !CursorInsideVolumePopup())
		{
			CloseVolumePopup();
		}
	}

	/// <summary>光标是不是停在建议浮层那一块屏幕上。
	///
	/// 这里**不能**用 <c>TitleSuggestList.IsMouseOver</c>：主窗口一失活，WPF 会把
	/// 所有元素的 IsMouseOver 一起清掉（它把「失活」当成指针已经离开）。于是
	/// 「点列表里的某一行」这个动作本身就会先把窗口置为非激活 ——
	/// 失活 → IsMouseOver 读到 false → 判定成「鼠标不在列表上」→ 把 Popup 关掉 →
	/// 那一行的 Click 根本等不到，表现出来就是「换个词之后点别的结果没反应」。
	/// 刚改过词的时候最容易撞上，因为这时用户的手正要往列表里去。
	///
	/// 改成拿真实光标位置去比对浮层的屏幕矩形：光标位置不经过任何命中测试，
	/// 也不受失活影响，指哪就是哪。</summary>
	private bool CursorInsideSuggestPopup()
	{
		try
		{
			if (!TitleSuggestPopup.IsOpen || TitleSuggestPopup.Child == null)
			{
				return false;
			}
			var src = PresentationSource.FromVisual(TitleSuggestPopup.Child) as HwndSource;
			if (src == null || !GetCursorPos(out NativePoint pt))
			{
				return false;
			}
			if (!GetWindowRect(src.Handle, out NativeRect rect))
			{
				return false;
			}
			return pt.X >= rect.Left && pt.X <= rect.Right
				&& pt.Y >= rect.Top && pt.Y <= rect.Bottom;
		}
		catch
		{
			return false;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out NativePoint lpPoint);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

	private bool IsInsideTitleSearch(DependencyObject? node)
	{
		while (node != null)
		{
			// 建议列表本身也要算「在搜索区里」。
			// Popup 的内容虽然长在独立 HWND 上，但冒泡路由事件照样会回到 Popup 的
			// 逻辑父级，也就是主窗口 —— 所以这里这个「点外面才收起」的处理器
			// 也会收到列表内部的按下事件。之前只认搜索框，列表里的按下就被当成
			// 「点到外面」，在 Preview（隧道）阶段先把 Popup 关了，
			// 行的Click 永远等不到，表现就是点建议项没反应。
			// 行到 TitleSuggestList 的视觉树是走得通的：
			// 行 → ContentPresenter → ItemsControl。
			if (ReferenceEquals(node, TitleSearchBox) || ReferenceEquals(node, TitleSuggestList))
			{
				return true;
			}
			node = node is Visual or System.Windows.Media.Media3D.Visual3D
				? VisualTreeHelper.GetParent(node)
				: null;
		}
		return false;
	}

	/// <summary>一条建议。本地歌给路径，网上的歌给标题（点它等于搜这一首）。</summary>
	private sealed class SuggestItem
	{
		public SuggestItem(string title, string artist, long durationMs, string localPath = "", string onlineTitle = "",
			string platform = "", bool vip = false)
		{
			Title = title;
			Artist = artist;
			DurationMs = durationMs;
			LocalPath = localPath;
			OnlineTitle = onlineTitle;
			Platform = platform;
			Vip = vip;
		}

		public string Title { get; }
		public string Artist { get; }
		public long DurationMs { get; }
		public string LocalPath { get; }
		public string OnlineTitle { get; }

		/// <summary>这一行是从哪儿来的：本地 / 网易云 / QQ / 酷狗。行尾那个小标签就是它。</summary>
		public string Platform { get; }

		/// <summary>接口明确说了要会员才能听时才为真。
		///
		/// 查不到就老实留 false：宁可这一行没有标签，也不要打上「VIP」结果点进去
		/// 发现本来就能播 —— 那比没有标签更误导。QQ / 酷狗的搜索结果里不带付费标记，
		/// 只有网易云的 Fee 字段能判，所以目前只有网易云那几行会亮这个标签。</summary>
		public bool Vip { get; }
	}

	// 拖动进度条时缓存的总时长。
	// 之前每收到一次鼠标移动就去读一次 MusicService.Duration（要走 NAudio reader），
	// 大幅拖动时事件非常密集，就会卡；这里在按下那一刻读一次，拖动过程只用缓存值。
	private TimeSpan _scrubDuration;

	// Seek 已发出但播放位置尚未确认的窗口期。这段时间内禁止定时器回写滑块。
	private bool _seekPending;

	// 这次窗口期里的 Seek 是否已经真正派发（DoSeek 把定位请求交给后台线程那一刻置真）。
	// 到达判定必须以它为前提——见 UpdateProgressUi 里的「假到位」注释。
	private bool _seekIssued;

	// 本次 Seek 的目标位置（秒）与发起时刻。窗口期什么时候撤不再靠定时猜：
	// UpdateProgressUi 里做「到达判定」——播放器报出的位置贴近目标才算真的
	// 跳过去了，到点撤窗；拖着不判到就挂上兜底超时放行。
	// 之前是 340ms 定时一到就无脑放行：一来 Seek 真正生效时往往晚于定时，
	// _tick 拿着旧位置先把滑块弹回去、等位置更新又跳到新值（「点一下
	// 缩回去再弹过来」）；二来上一次 Seek 的定时恰好落在下一次
	// 连点中间，会把还没发的定位的窗口期提前掐掉，同样回弹。
	private double _seekTarget;

	private DateTime _seekPendingSince = DateTime.MinValue;

	// 上一次 Seek 的时刻。Seek 会同步改 reader.CurrentTime，
	// 大跨度定位在压缩音频上要丢弃并重解一大段数据，是同步耗时操作；
	// 连着点几下就会把 UI 线程堵死（表现就是「程序未响应」）。
	// 这里做节流：太密集的连续点击只保留最后一次目标位置。
	private DateTime _lastSeekAt = DateTime.MinValue;

	// 延后执行被节流的 Seek。
	private readonly DispatcherTimer _seekThrottleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

	// 所谓「等播放器确认」不再用定时器猜：到达判定在 UpdateProgressUi 里
	// 以播放位置为准（见 _seekTarget 注释）。定时一旦填错节奏就会回弹。

	private bool _hasDeferredSeek;

	// 节流窗口内最后一次请求的定位目标（秒）。不回读某个滑块——
	// 全屏页拖动期间底栏滑块还在按真实位置走，读它会定位错。
	private double _deferredSeekTarget;

	// 「长按未拖动 = 直接跳转」的定时器已移除。
	// 为什么删：它会在按住不放时**中途**发一次 Seek——按住的那一下
	// 大跨度定位是同步耗时操作（重解一大段压缩数据），用户还按着
	// 鼠标想继续拖，UI 却先被这次中途 Seek 卡住，表现就是
	// 「长按再拖不顺」。现在的交互收敛成一条不动脑筋的规则：
	// 按下→值跟落点（只改 UI），拖→跟手预览时间，松手→才真正 Seek。
	// 中途不再有第二次定位，卡顿源头随之消失。
	// （按住不松也不再提前跳：想定住听开头，松手即是定位。）

	private void MiniProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		_progressDragging = true;
		_scrubDuration = MusicService.Duration;
		// 按下即由我们接管捕获：轨道上按下时 Thumb 拿不到原生捕获，
		// 之后的移动跟随和松手定位都靠这条捕获把事件续住。
		MiniProgressSlider.CaptureMouse();
		// 必须把事件标记为已处理：不标记时，按在 12px 圆点上会让 Thumb
		// 自己再抢一次捕获（Slider 的捕获被顶掉），随后
		// PreviewMouseMove 里的 IsMouseCaptured 判定失败 —— 表现就是
		// 「按在圆点上拖不动，只有按轨道空白处能拖」。这正是用户反馈的
		// 「长按拖动不顺」的根因。Handled 之后 Thumb / RepeatButton
		// 都不再参与，值完全由下面这套映射接管。
		e.Handled = true;
		// 点击落点我们自己算，不依赖 Slider.IsMoveToPointEnabled 的模板行为：
		// 它只在「落点不在 Thumb 上」时生效，而且我们的 CaptureMouse 已经
		// 把 Thumb 的原生交互整个摁掉了 —— 之前就是靠它把值送到点击位置，
		// 一旦它没跑（点在圆点上/模板时序差异），值停在原地，
		// 松手时 SeekToSliderValue 定位到的还是旧位置 = 「点击跳转失效」。
		// 映射与 Thumb 半宽行程一致（见 FollowPointerToSliderX），两种来源算出的值相同。
		// 「点击即落点、松手才定位、拖动只改 UI」的交互按社区通行做法实现：
		// https://social.msdn.microsoft.com/forums/vstudio/en-us/5fa7cbc2-c99f-4b71-b46c-f156bdf0a75a/making-the-slider-slide-with-one-click-anywhere-on-the-slider
		// https://learn.microsoft.com/dotnet/api/system.windows.controls.slider.ismovetopointenabled
		FollowPointerToSliderX(e.GetPosition(MiniProgressSlider));
		// 按下只把值送到落点（改 UI 不定位）：真正的 Seek 统一留给松手，
		// 按住期间绝不上半路发定位请求。
	}

	private void MiniProgressSlider_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_progressDragging || !MiniProgressSlider.IsMouseCaptured)
		{
			return;
		}
		// 拖动就是拖动：落点跟随、只改 UI 与预览时间，
		// 真正的定位留给松手那一刻（快速拖动期间播放不断）。
		FollowPointerToSliderX(e.GetPosition(MiniProgressSlider));
	}

	/// <summary>指针 X 轴 → 滑条值。映射交给 SetSliderValueFromPointer：
	/// 它按模板里的 PART_Track 与真实 Thumb 宽度反算，换主题/换圆点尺寸都不会偏。</summary>
	private void FollowPointerToSliderX(Point p)
	{
		SetSliderValueFromPointer(MiniProgressSlider, p);
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
		// 顺序三步走：先定位，再清「拖动中」，最后释放捕获。
		// 1) 先 Seek：RequestSeek 一发出去 _seekPending 就同步挂上了，
		//    UpdateProgressUi 在播放器确认位置前不会回写滑块，
		//    不存在「Seek 引发的刷新用旧位置盖掉点击值」的窗口。
		// 2) 清 _progressDragging 必须早于 ReleaseMouseCapture：释放捕获
		//    会同步触发 LostMouseCapture 兜底，标志还挂着的话它会再定位
		//    一次 —— 双重 Seek 互相落进对方的节流窗口，松手定位就慢半拍。
		// 3) 捕获释放后标志已是 false，LostMouseCapture 只做兜底复位。
		SeekToSliderValue();
		_progressDragging = false;
		if (MiniProgressSlider.IsMouseCaptured)
		{
			MiniProgressSlider.ReleaseMouseCapture();
		}
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
			RequestSeek(MiniProgressSlider.Value * total);
		}
		catch (Exception ex)
		{
			_seekPending = false;
			LogHelper.LogError(ex, "定位失败");
		}
	}

	/// <summary>带节流的定位请求。底栏和全屏页两条滑块链共用这一条。
	/// 大跨度 Seek 是同步耗时操作，连点几下会把 UI 线程堵死，
	/// 所以 260ms 内的连续请求只保留最后一次。</summary>
	private void RequestSeek(double target)
	{
		try
		{
			// 无论走哪条分支，在播放器确认新位置之前都不许回写滑块，
			// 视觉上就始终停在用户点到的位置（修「点一下又弹回去」）。
			// 窗口期什么时候撤交给 UpdateProgressUi 的到达判定/兜底超时。
			// 新窗口期开始时 Seek 必然还没派发（含节流延后的那次），先清掉：
			// 到达判定只有看见 _seekIssued 才允许「到位」收窗。
			_seekPending = true;
			_seekIssued = false;
			_seekTarget = target;
			_seekPendingSince = DateTime.UtcNow;
			_deferredSeekTarget = target;

			// 离上一次真正的 Seek 太近：先记着，节流窗口结束后再发。
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
		// 用请求时记下来的目标，别再回读滑块：节流期间每次新请求都会刷新它
		// （「拖到别处」仍然取到最新值），而且全屏页拖动时底栏滑块还在按
		// 真实位置走，读底栏会把定位发到错误的地方。
		double total = MusicService.Duration.TotalSeconds;
		if (total <= 0)
		{
			_seekPending = false;
			return;
		}
		DoSeek(_deferredSeekTarget);
	}

	/// <summary>真正执行 Seek。调用方已保证节流窗口已过。
	/// Seek 挪到后台线程跑：reader.CurrentTime 赋值对压缩音频要丢弃并重解
	/// 一段数据，同步做会把 UI 线程卡出肉眼可见的顿挫（「点了跳一卡一卡」）。
	/// NAudio 的 CurrentTime 赋值本身线程可用；MusicService.Seek 里唯一碰 UI 的
	/// NotifyStateChanged 全部走 Dispatcher 转 UI 线程，后台调用是安全的。
	/// 两次请求间用锁串行，避免两次赋值交叠把解码位置写花。</summary>
	private readonly object _seekGate = new object();

	private void DoSeek(double seconds)
	{
		try
		{
			_lastSeekAt = DateTime.UtcNow;
			double target = Math.Max(0.0, seconds);
			// 曲目本身就是视频时，画面跟着定位走。不跟的话声音跳到 3 分钟、
			// 背景还在第 5 秒，看起来像「拖了没反应」。
			if (_bgVideoFromTrack && BgVideo.Visibility == Visibility.Visible)
			{
				try
				{
					BgVideo.Position = TimeSpan.FromSeconds(target);
				}
				catch
				{
				}
			}
			// 从这一刻起「到达判定」才允许收窗口期：pos≈target 才是 Seek 真落了地，
			// 派发前 pos≈target 只是旧位置恰好挨着目标（假到位），见 UpdateProgressUi。
			_seekIssued = true;
			_ = System.Threading.Tasks.Task.Run(() =>
			{
				lock (_seekGate)
				{
					bool ok;
					try
					{
						ok = MusicService.Seek(target);
					}
					catch (Exception ex)
					{
						ok = false;
						LogHelper.LogError(ex, "进度条定位失败");
					}
					if (!ok)
					{
						// 被拒绝说明这次定位没生效：立刻撤窗口，_tick 下一拍
						// 会把滑块按真实位置画回去（写个 bool 字段与线程无关）。
						_seekPending = false;
					}
				}
			});
			// 成功路径不在这里收窗口期：到达判定/2.5s 兜底超时在
			// UpdateProgressUi 里以播放位置为准收掉（见 _seekTarget 注释）。
		}
		catch (Exception ex)
		{
			_seekPending = false;
			LogHelper.LogError(ex, "进度条定位失败");
		}
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
			// 再点一次同一个键就是收起（StaysOpen=True 之后要自己管这个语义）。
			if (VolumePopup.IsOpen)
			{
				CloseVolumePopup();
				return;
			}
			UpdateVolumeSlider();
			// 推迟到本次鼠标交互彻底结束（Input 优先级，即当前输入队列跑完）再打开：
			// 这次点击的收尾还在路上，立刻开会赶上 Popup 的定位/动画抽一帧。
			Dispatcher.BeginInvoke(new Action(OpenVolumePopup),
				System.Windows.Threading.DispatcherPriority.Input);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开音量弹窗失败");
		}
	}

	/// <summary>初始化/程序回写滑条时置 true：压掉 ValueChanged 里的落盘与音频重设。</summary>
	private bool _syncVolumeSlider;

	/// <summary>当前正被指针按住拖动的音量滑条（主音量或视频背景音量）。
	/// 拖动期间走「只改内存值、不落盘」的路径，松手统一保存一次。</summary>
	private Slider? _dragVolumeSlider;

	/// <summary>滚轮调音量：一格 5%，与系统音量合成器的步进一致。
	/// 图标上和浮层里都挂：鼠标停在音量键上直接滚就行，不用先点开。</summary>
	private void MiniVolumeButton_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (e.Delta == 0)
		{
			return;
		}
		ChangeMusicVolumeBy(e.Delta > 0 ? 0.05 : -0.05);
		e.Handled = true;
	}

	/// <summary>浮层里的滚轮统一调主音量（视频背景音量行已撤，不再分流）。</summary>
	private void VolumePopup_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (e.Delta == 0)
		{
			return;
		}
		ChangeMusicVolumeBy(e.Delta > 0 ? 0.05 : -0.05);
		e.Handled = true;
	}

	/// <summary>按步进改主音量。浮层开着时滑条与百分比会一起刷新（UpdateVolumeSlider
	/// 内部有 _syncVolumeSlider 保护，不会反过来再触发一次设置）。</summary>
	private void ChangeMusicVolumeBy(double delta)
	{
		try
		{
			double v = Math.Clamp(SettingsManager.Instance.MusicVolume + delta, 0.0, 1.0);
			MusicService.SetVolume(v);
			UpdateVolumeSlider();
			UpdateMiniVolumeText();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "滚轮调整音量失败");
		}
	}

	/// <summary>说明：**拖动窗口时**浮层必须跟主窗口一起消失。
	/// Popup 是独立 HWND，主窗口移动时它不会跟着走（窗口拖动用的是
	/// WM_NCLBUTTONDOWN + 系统循环，WPF 收不到持续的布局更新），
	/// 结果就是「浮层留在原地，看上去从程序上飘出去了」。
	/// 这里在窗口位置/尺寸变化时直接收起，和右上角搜索建议列表的处理一致。
	/// 收起统一走 CloseVolumePopup：带 90ms 的淡出，不会「啪」一下断掉。</summary>
	private void Window_LocationChanged(object? sender, EventArgs e)
	{
		if (VolumePopup != null && VolumePopup.IsOpen)
		{
			CloseVolumePopup();
		}
	}

	// ── 音量浮层的开关（带淡出） ──────────────────────────────

	/// <summary>正在做收起动画。动画期间再点开要先撤掉，否则淡出结束的回调
	/// 会把刚打开的浮层又关一次。</summary>
	private bool _volumePopupClosing;

	private static readonly Duration VolumePopupFadeOut = new(TimeSpan.FromMilliseconds(90));

	private void OpenVolumePopup()
	{
		if (!IsVisible || WindowState == WindowState.Minimized)
		{
			return;
		}
		_volumePopupClosing = false;
		// 上一次淡出把透明度按到 0 上了，这里必须先把动画摘掉再复位，
		// 否则 BeginAnimation(null) 之前读到的仍是动画值。
		if (VolumePopup.Child is FrameworkElement child)
		{
			child.BeginAnimation(UIElement.OpacityProperty, null);
			child.Opacity = 1.0;
		}
		VolumePopup.IsOpen = true;
	}

	/// <summary>收起浮层。先淡出再真正关掉 IsOpen。
	///
	/// 直接 IsOpen=false 是瞬间消失，拖窗口的时候尤其突兀（窗口在动、浮层
	/// 一帧内整块没掉）。加一层 90ms 的 Opacity 动画，观感和搜索建议列表一致。
	/// 淡出期间只置 _volumePopupClosing，不重复起动画，避免连点抖两下。</summary>
	private void CloseVolumePopup()
	{
		if (VolumePopup == null || !VolumePopup.IsOpen || _volumePopupClosing)
		{
			return;
		}
		if (VolumePopup.Child is not FrameworkElement child)
		{
			VolumePopup.IsOpen = false;
			return;
		}
		_volumePopupClosing = true;
		var fade = new DoubleAnimation(0.0, VolumePopupFadeOut)
		{
			EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
		};
		fade.Completed += (_, _) =>
		{
			// 动画走完才真正关；期间如果又被点开（_volumePopupClosing 被复位）
			// 就什么都不做，别把用户刚打开的浮层吃掉。
			if (_volumePopupClosing)
			{
				VolumePopup.IsOpen = false;
			}
		};
		child.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	/// <summary>光标是不是停在音量浮层那一块屏幕上。
	///
	/// 和 CursorInsideSuggestPopup 同一个思路：用 GetCursorPos 读真实光标位置，
	/// 再拿浮层自己那个 HWND 的屏幕矩形比对。**不能**用滑条的 IsMouseOver ——
	/// 主窗口一失活 WPF 会把所有 IsMouseOver 清成 false，那么「点浮层里的滑条」
	/// 这个动作本身就会被判成「点在浮层外面」，浮层当场被收掉。
	/// 这就是「点一下改音量列表就消失」的根因。
	///
	/// Popup 是独立 HWND，它的鼠标事件根本不会传到主窗口，能进到外面那些
	/// 处理器里的点击，要么真点在窗口上，要么是失活/位置变化带出来的 ——
	/// 所以这里唯一要做的是把「光标还在浮层里」这种情况排除掉。</summary>
	private bool CursorInsideVolumePopup()
	{
		try
		{
			if (VolumePopup == null || !VolumePopup.IsOpen || VolumePopup.Child == null)
			{
				return false;
			}
			if (PresentationSource.FromVisual(VolumePopup.Child) is not HwndSource src)
			{
				return false;
			}
			if (!GetCursorPos(out NativePoint pt) || !GetWindowRect(src.Handle, out NativeRect rect))
			{
				return false;
			}
			return pt.X >= rect.Left && pt.X <= rect.Right
				&& pt.Y >= rect.Top && pt.Y <= rect.Bottom;
		}
		catch
		{
			return false;
		}
	}

	private void VolumePopupSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncVolumeSlider)
		{
			return;
		}
		try
		{
			double v = Math.Clamp(e.NewValue, 0.0, 100.0) / 100.0;
			if (_dragVolumeSlider != null)
			{
				MusicService.SetVolumeLive(v);
			}
			else
			{
				MusicService.SetVolume(v);
			}
			UpdateMiniVolumeText();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "设置音量失败");
		}
	}

	/// <summary>把当前设置里的视频背景音量推给 BgVideo。
	/// 当前曲目本身是视频时保持静音——声音已经由 NAudio 播了，MediaElement 再放一遍就是双声。</summary>
	private void ApplyBgVideoVolume()
	{
		try
		{
			if (BgVideo.Visibility != Visibility.Visible)
			{
				return;
			}
			BgVideo.Volume = _bgVideoMuted
				? 0.0
				: (float)Math.Clamp(SettingsManager.Instance.BgVideoVolume, 0.0, 1.0);
		}
		catch
		{
		}
	}

	// ── 音量浮层滑条的「按住即拖」 ──────────────────────────────
	// 现在的做法是社区通行的那一套（见 https://stackoverflow.com/q/9724766 与
	// WPF Slider 的 IsMoveToPointEnabled 实现）：
	//   按下 → 自己 CaptureMouse + 立刻把值落到指针处；
	//   移动 → 捕获在手，指针跑到浮层外面也照样跟；
	//   抬起 → 释放捕获、落一次盘。
	// 关键前提是 Popup 不再用 StaysOpen=False（那会在 PopupRoot 上抓一层
	// SubTree 捕获，我们一 CaptureMouse 就把它顶掉，之后的关闭逻辑和第二条
	// 滑条都会失灵）。外部点击由 Window_PreviewMouseDown 负责收。

	private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Slider slider || e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		_dragVolumeSlider = slider;
		// 捕获挂在滑条自己身上：Thumb / RepeatButton 的原生拖拽被下面的
		// e.Handled 挡住，整套交互只有这一份，不会出现两套值互相打架。
		slider.CaptureMouse();
		FollowVolumeSliderPointer(slider, e.GetPosition(slider));
		e.Handled = true;
	}

	private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (sender is not Slider slider || !ReferenceEquals(_dragVolumeSlider, slider))
		{
			return;
		}
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		FollowVolumeSliderPointer(slider, e.GetPosition(slider));
		e.Handled = true;
	}

	private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Slider slider || !ReferenceEquals(_dragVolumeSlider, slider))
		{
			return;
		}
		_dragVolumeSlider = null;
		if (slider.IsMouseCaptured)
		{
			slider.ReleaseMouseCapture();
		}
		// 拖动期间只改了内存值，松手落一次盘。
		SettingsManager.Save(SettingsManager.Instance);
		e.Handled = true;
	}

	/// <summary>拖到一半捕获被系统收走（Alt+Tab、弹窗抢焦点等）时收尾并落盘，
	/// 否则值改了却没保存，而且 _dragVolumeSlider 会一直挂着。</summary>
	private void VolumeSlider_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (sender is Slider slider && ReferenceEquals(_dragVolumeSlider, slider))
		{
			_dragVolumeSlider = null;
			SettingsManager.Save(SettingsManager.Instance);
		}
	}

	private void VolumePopup_Closed(object? sender, EventArgs e)
	{
		_volumePopupClosing = false;
		// 把淡出动画摘干净并把透明度复位：下一次打开是 OpenVolumePopup 里
		// 重新置 1，这里也复位一次，避免某些路径（比如系统直接关掉 Popup）
		// 留下一个「打开就是全透明」的浮层。
		if (VolumePopup.Child is FrameworkElement child)
		{
			child.BeginAnimation(UIElement.OpacityProperty, null);
			child.Opacity = 1.0;
		}
		// 浮层关掉时如果手还在拖（比如点了外面），这里收尾并落盘。
		if (_dragVolumeSlider != null)
		{
			if (_dragVolumeSlider.IsMouseCaptured)
			{
				_dragVolumeSlider.ReleaseMouseCapture();
			}
			_dragVolumeSlider = null;
			SettingsManager.Save(SettingsManager.Instance);
		}
	}

	/// <summary>指针位置 → 滑条值。
	///
	/// 用模板里的 PART_Track 反算，而不是拿滑条宽度硬算：轨道可能带 Margin、
	/// 圆点尺寸也会换主题，硬编码半宽会让「指到哪就是哪」在两端偏几像素。
	/// 圆点中心行程按 [半宽, 轨道宽-半宽] 铺满，和 WPF Track 的排布一致。</summary>
	private static void SetSliderValueFromPointer(Slider slider, Point pInSlider)
	{
		try
		{
			if (slider.Template?.FindName("PART_Track", slider) is Track track
				&& track.ActualWidth > 2)
			{
				Point p = slider.TranslatePoint(pInSlider, track);
				double half = track.Thumb != null && track.Thumb.ActualWidth > 0
					? track.Thumb.ActualWidth / 2.0
					: 0.0;
				double span = track.ActualWidth - 2 * half;
				if (span <= 0.5)
				{
					return;
				}
				double ratio = Math.Clamp((p.X - half) / span, 0.0, 1.0);
				if (track.IsDirectionReversed)
				{
					ratio = 1.0 - ratio;
				}
				slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
				return;
			}
			// 模板还没实例化的极端时序：退回整宽映射，至少不会没反应。
			double w = slider.ActualWidth;
			if (w <= 1)
			{
				return;
			}
			slider.Value = slider.Minimum
				+ (slider.Maximum - slider.Minimum) * Math.Clamp(pInSlider.X / w, 0.0, 1.0);
		}
		catch
		{
		}
	}

	private static void FollowVolumeSliderPointer(Slider slider, Point p)
	{
		SetSliderValueFromPointer(slider, p);
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
			// 原样选的那个只是「用户挑了什么」，真正拿去画的是夹过亮度的那份：
			// 深色主题下选「墨黑」#1C1C1C、浅色主题下选「纯白」#FFFFFF，
			// 原样画出来等于和背景同色 —— 播放键、进度条、选中项一起消失，
			// 整块界面看着像「变了色」。这里只补亮度，不动色相。
			// 用户挑的那个值本来就存在 SettingsManager 里，AccentColor 这个资源
			// 全局只有 ApplyAccentDerived 读，所以这里存「画出来那份」不影响回显。
			var draw = ThemeManager.ReadableAccent(color);
			Application.Current.Resources["AccentColor"] = draw;
			var brush = Frozen(draw);
			Application.Current.Resources["AccentBrush"] = brush;
			// 压强调色上的字色，按「画出来那份」的亮度决定用黑字还是白字。
			var onAccent = Frozen(OnAccentColor(draw));
			Application.Current.Resources["OnAccentColor"] = onAccent.Color;
			Application.Current.Resources["OnAccentBrush"] = onAccent;
			// 悬停/按下：从画出来那份出发提亮或压暗。
			Application.Current.Resources["AccentHoverColor"] = Shift(draw, 0.18);
			Application.Current.Resources["AccentHoverBrush"] = Frozen(Shift(draw, 0.18));
			Application.Current.Resources["AccentPressedColor"] = Shift(draw, -0.12);
			Application.Current.Resources["AccentPressedBrush"] = Frozen(Shift(draw, -0.12));
			// AccentSubtle* 由主题系统统一算（浅色下透明度不一样），换色时一起刷新
			ThemeManager.ApplyAccentDerived(Application.Current.Resources);
			TrackRows.AccentBrush = brush;
			TrackRows.OnAccentBrush = onAccent;
			// 通知用 TrackRows 画行的页面（音乐库/最近/收藏/在线搜索）把行重建，
			// 否则它们缓存的旧行还是换色前的播放键和标签颜色。
			TrackRows.RaiseAccentChanged();
			UpdateMiniOrder();
			// 定位小球的半透明底色跟着强调色走，换色/启动都刷一次。
			UpdateLocateBallTheme();
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

	// ==================== 播放定位小球 ====================
	// 拖动/惯性/缩放/落位保存与启动恢复已抽到 MainWindow.LocateBall.cs（partial）。
	// 单击定位入口 LocatePlayingTrack 仍留在本文件：它要跨页导航、找行、闪进度条。

	/// <summary>定位当前播放歌曲：按入队时记的来源跳页，把正在播放的行
	/// 滚到视口中央并做高亮脉冲，底部进度条同时闪一下当前位置。</summary>
	private void LocatePlayingTrack()
	{
		string? path = MusicService.CurrentPath;
		if (string.IsNullOrWhiteSpace(path))
		{
			ShowToast(Loc.Default["locate_no_playing"], false);
			return;
		}
		LogHelper.Log($"[定位] 入口：source={MusicService.CurrentSource} path={Path.GetFileName(path)}");
		// 目标页由来源标记决定（MusicService.CurrentSource，见 MusicService 各播放入口）；
		// 没有标记（比如启动恢复的最后一首）退回「最近播放」——刚播过的歌一定在那。
		string target = MusicService.CurrentSource switch
		{
			"library" => "library",
			"fav" => "fav",
			"online" => "online",
			_ => "recent"
		};
		if (target == "online")
		{
			// 进在线页前压住随机自动搜索：那发搜索会 ResetEngineCaches，
			// 把定位要用的缓存/记忆页码冲掉，定位必败（历史失败点之一）。
			EnsureOnlinePage()?.SuppressAutoSearchOnce();
		}
		NavigateTo(target);
		// 行查找推迟一帧：NavigateTo 里 OnNavigatedTo 是同步建行，但容器生成/布局
		// 要到 Loaded 优先级之后才稳定，立刻遍历可视树会漏刚挂上的行。
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async void () =>
		{
			FrameworkElement? row = FindPlayingRow(path);
			OnlineMusicPage? onlinePage = EnsureOnlinePage();
			if (row == null && target == "online" && onlinePage != null)
			{
				// 搜索页的行容器在分页里：正在播放的那首不在当前页时，
				// 先翻到对应页再拿行；搜索结果里没有（换过搜索词）就返回 null。
				row = onlinePage.RevealPlayingRow();
				LogHelper.Log($"[定位] 在线页恢复：{(row != null ? "命中" : (onlinePage.LocateRestorePending ? "重搜异步恢复中" : "未命中"))}");
				if (row != null)
				{
				// 翻页刚重建过列表、布局还没跑，居中/脉冲再推一拍等新行排版完。
				_ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FinishLocateReveal(row));
					return;
				}
				if (onlinePage.LocateRestorePending)
				{
					// 搜索页正按「定位记忆」恢复现场（切源/重搜关键词→翻到记忆页码
					// →居中+高亮，见 CompletePendingLocateRestore），完成后它自己收尾，
					// 这里静默等待，别急着报「定位不到」。
					return;
				}
				// 搜索页没记过这首歌 ≠ 没救：它也可能是从**音乐库收藏夹**的
				// 在线歌单里播的（那时 CurrentSource 也是 online，target 才会落到
				// online）。音乐库有它的视图记忆就跳回去做库内恢复。
				if (MusicLibraryPage.HasLocateMemo(MusicService.BuildCurrentTrack()?.Key))
				{
					NavigateTo("library");
				}
			}
			if (row == null && ContentHost.Content is MusicLibraryPage libPage2)
			{
				// 音乐库恢复：切回这首歌播放时的视图（本地/全部歌曲/歌单）、
				// 翻到所在页、把行滚进视口。可视树没命中时的兜底主力。
				// 不限 target：本地歌（library）、收藏夹歌（fav）之外，
				// 在线歌的库内记忆（上面跳回来）也走这里。
				try
				{
					row = await libPage2.RevealPlayingRowAsync();
					LogHelper.Log($"[定位] 音乐库恢复：{(row != null ? "命中" : "未命中")}");
					if (row != null)
					{
						FinishLocateReveal(row);
						return;
					}
				}
				catch (Exception ex)
				{
					LogHelper.LogError(ex, "[定位] 音乐库恢复失败");
				}
			}
			if (row == null)
			{
				// 行还没生成（在线页还没搜过/库还没加载完/行不在当前筛选里）：
				// 提示并放弃，绝不为此强制全量加载列表。
				LogHelper.Log("[定位] 全部路径未命中，提示定位不到");
				ShowToast(Loc.Default["locate_not_found"], false);
				return;
			}
			LogHelper.Log("[定位] 可视树命中");
			FinishLocateReveal(row);
		});
	}

	/// <summary>定位收尾统一入口（定位球直找/搜索页记忆恢复共用）：
	/// 确认音效 + 行居中 + 高亮脉冲 + 底部进度条闪位置标记；行没找到给提示。</summary>
	internal void FinishLocateReveal(FrameworkElement? row)
	{
		if (row == null)
		{
			ShowToast(Loc.Default["locate_not_found"], false);
			return;
		}
		UiSfx.PlayLocateConfirm();
		// 行可能刚重建、布局还没跑，居中/脉冲推一拍等新行排版完。
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
		{
			CenterRowIntoView(row);
			PulsePlayingRow(row);
			FlashProgressMarker();
		});
	}

	/// <summary>在当前页可视树里找正在播放的行。行的 Border 上 Tag 挂着 SavedTrack
	/// （见 TrackRows.BuildRow），Path 与正在播放的路径一致即命中；
	/// 在线行（搜索页/收藏夹在线视图）的 Tag 挂的是平台条目（NeteaseTrack 等），
	/// 没有本地路径，用当前播放的 Key（type:id，见 SavedTrack.Key）对上。
	/// 只遍历已经生成的行（可视树里有的），不触发任何数据加载。</summary>
	private FrameworkElement? FindPlayingRow(string path)
	{
		if (ContentHost.Content is not DependencyObject root)
		{
			return null;
		}
		string? currentKey = MusicService.BuildCurrentTrack()?.Key;
		FrameworkElement? found = null;
		void Walk(DependencyObject d)
		{
			if (found != null)
			{
				return;
			}
			int count = VisualTreeHelper.GetChildrenCount(d);
			for (int i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(d, i);
				if (child is FrameworkElement fe && IsPlayingRowTag(fe.Tag, path, currentKey))
				{
					found = fe;
					return;
				}
				Walk(child);
			}
		}
		Walk(root);
		return found;
	}

	/// <summary>行容器 Tag 与正在播放曲目的匹配规则：本地行按路径，在线行按 Key。</summary>
	private static bool IsPlayingRowTag(object? tag, string path, string? currentKey)
	{
		switch (tag)
		{
			case SavedTrack st:
				if (!string.IsNullOrEmpty(st.Path)
					&& string.Equals(st.Path, path, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				// 在线收藏/搜索结果行没有本地路径，用 Key（type:id）认。
				return !string.IsNullOrEmpty(currentKey)
					&& string.Equals(st.Key, currentKey, StringComparison.OrdinalIgnoreCase);
			case not null when currentKey != null:
				// 平台条目对象（OnlineMusicPage.BuildEngineRowCore 把它挂在行 Tag 上）。
				var online = OnlineMusicPage.ToSavedTrack(tag);
				return online != null
					&& string.Equals(online.Key, currentKey, StringComparison.OrdinalIgnoreCase);
			default:
				return false;
		}
	}

	/// <summary>把行滚到所属 ScrollViewer 的视口中央；本来就在中央附近（±60px）就不动，
	/// 免得好好的页面被拽得一晃。</summary>
	private static void CenterRowIntoView(FrameworkElement row)
	{
		var scroll = FindAncestor<ScrollViewer>(row);
		if (scroll == null)
		{
			row.BringIntoView();
			return;
		}
		double rowTop = row.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
		double centered = (scroll.ViewportHeight - row.ActualHeight) / 2.0;
		if (Math.Abs(rowTop - centered) <= 60)
		{
			return;
		}
		// TransformToAncestor 给的是「含滚动偏移」的视口坐标，
		// 要换算回内容坐标才能交给 ScrollToVerticalOffset。
		scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + rowTop - centered));
	}

	private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
	{
		DependencyObject? cur = start;
		while (cur != null)
		{
			if (cur is T hit)
			{
				return hit;
			}
			cur = VisualTreeHelper.GetParent(cur);
		}
		return null;
	}

	/// <summary>高亮脉冲：整行透明度两下快闪再渐回，共约 1.2s。
	/// 不碰行自己的底色/描边画刷——那套归 TrackRows 的状态机管，动了会打架。</summary>
	private static void PulsePlayingRow(FrameworkElement row)
	{
		row.BeginAnimation(UIElement.OpacityProperty, null);
		row.Opacity = 1;
		var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1200) };
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.Zero));
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, TimeSpan.FromMilliseconds(150)));
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(300)));
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, TimeSpan.FromMilliseconds(450)));
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(600)));
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.65, TimeSpan.FromMilliseconds(900)));
		anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1200)));
		anim.Completed += (_, _) => row.BeginAnimation(UIElement.OpacityProperty, null);
		row.BeginAnimation(UIElement.OpacityProperty, anim);
	}

	/// <summary>底部迷你进度条短暂标注当前播放位置：一颗点在当前位置闪现约 1.2s 后淡出。
	/// 进度条不可见（窗口收窄到底栏藏了）就跳过。</summary>
	private void FlashProgressMarker()
	{
		// 底栏被收起（滑块还没量出宽度）时无从标注，直接跳过。
		if (MiniProgressMarker == null || MiniProgressSlider.ActualWidth <= 0)
		{
			return;
		}
		try
		{
			double total = MusicService.Duration.TotalSeconds;
			if (total <= 0)
			{
				return;
			}
			double frac = Math.Clamp(MusicService.Position.TotalSeconds / total, 0.0, 1.0);
			// 轨道两端给拇指留的空档近似为 4px（与标注点初始 Margin 一致），
			// 标注是「闪一下」的辅助提示，不追求和拖块像素级对齐。
			double lane = Math.Max(0, MiniProgressSlider.ActualWidth - 8);
			MiniProgressMarker.Margin = new Thickness(4 + lane * frac, 0, 0, 0);
			MiniProgressMarker.BeginAnimation(UIElement.OpacityProperty, null);
			var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1200) };
			anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
			anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120)));
			anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(700)));
			anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1200)));
			anim.Completed += (_, _) => MiniProgressMarker.BeginAnimation(UIElement.OpacityProperty, null);
			MiniProgressMarker.BeginAnimation(UIElement.OpacityProperty, anim);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "标注播放位置失败");
		}
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

	/// <summary>底栏「内存释放」键：执行一轮完整内存释放并 Toast 反馈。
	/// 释放三步（封面缓存→GC 压缩→EmptyWorkingSet）与托盘驻留、调试面板
	/// 「立即释放」共用 Services/MemoryRelease.ReleaseNow，不在这里复制一份。</summary>
	private void MiniMemButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			(int decoded, int bytes) = MemoryRelease.ReleaseNow();
			ShowToast(string.Format(Loc.Default["toast_mem_released"], decoded, bytes));
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "底栏内存释放失败");
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
			DrawerList.Children.Add(TrackRows.BuildRow(new TrackRows.TrackRowSpec
			{
				Track = copy,
				OnPlay = () => DrawerPlay(copy),
				IsFavorite = fav,
				OnToggleFavorite = () => UserLibrary.ToggleFavorite(copy)
			}));
		}
	}

	private void DrawerPlay(SavedTrack t)
	{
		// 抽屉里点播也登记播放上下文：按当前所在 tab 记成「我的收藏 / 最近播放」，
		// 「启动时自动播放」据此随机恢复。
		MusicService.PendingPlayContext = _drawerTab == "fav"
			? new PlayContext { Page = "fav", ListKey = "fav" }
			: new PlayContext { Page = "recent", ListKey = "recent" };
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
