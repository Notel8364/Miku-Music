using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MusicBox.Services;

/// <summary>
/// 系统媒体控制（SMTC）：把 Windows 音量飞轮 / 耳机按键 / 媒体浮层接到 Music_Box 上。
///
/// 实际出声还是 NAudio（DSP 效果链都在那边），这里的 WinRT MediaPlayer 只当
/// 「静音影子播放器」：它跟着 NAudio 同步播放，负责把曲目信息、时间轴、播放状态
/// 报给系统；系统按键再转发回 NAudio。这样 SMTC 状态天然跟得住，不用手动补时间轴。
/// </summary>
public static class SmtcService
{
	private static Windows.Media.Playback.MediaPlayer? _player;
	private static SystemMediaTransportControls? _smtc;
	// 上一次给影子播放器用的 MediaSource。MediaSource 实现了 IDisposable，
	// 每次换曲都 new 一个又不释放的话，MF 的源读取器会一个个堆着不放，
	// 连播/自动切歌久了会内存涨、CPU 空转。这里换源前显式 Dispose 掉旧的。
	private static MediaSource? _shadowSource;
	private static string? _mirroredPath;
	// 封面代次：防止慢的那次异步取图把新曲目的封面盖掉（见 QueueThumbnail）。
	private static long _thumbTicket;
	private static System.Windows.Threading.Dispatcher? _dispatcher;
	private static bool _suppressButtonEcho;
	private static bool _attached;

	/// <summary>系统按了播放键（应转给 NAudio）。</summary>
	public static event Action? PlayRequested;

	/// <summary>系统按了暂停键（应转给 NAudio）。</summary>
	public static event Action? PauseRequested;

	public static event Action? NextRequested;

	public static event Action? PrevRequested;

	/// <summary>系统拖动了进度条，参数是目标位置。</summary>
	public static event Action<TimeSpan>? SeekRequested;

	/// <summary>系统点了上一首/下一首之外的 stop。</summary>
	public static event Action? StopRequested;

	/// <summary>
	/// SMTC 会话是否已经建起来。
	/// 不用 ApiInformation.IsTypePresent 去探测：那个探测对
	/// Windows.Media.SystemMediaTransportControls 这个运行时类名恒返回 false，
	/// 会让 SMTC 静默地整个不启用。TargetFramework 已经要求 10.0.19041，
	/// 直接建、失败就当不支持，最可靠。
	/// </summary>
	public static bool IsAttached => _smtc != null;

	/// <summary>建立 SMTC 会话。重复调用安全。</summary>
	public static void Attach()
	{
		if (_attached)
		{
			return;
		}
		try
		{
			// 记住 UI 线程，异步取封面后要切回来刷显示。
			_dispatcher ??= System.Windows.Threading.Dispatcher.CurrentDispatcher;
			_player = new Windows.Media.Playback.MediaPlayer
			{
				// 声音由 NAudio 出，这里必须静音，否则会两路一起响
				IsMuted = true,
				AutoPlay = false
			};
			_smtc = _player.SystemMediaTransportControls;
			_smtc.IsPlayEnabled = true;
			_smtc.IsPauseEnabled = true;
			_smtc.IsStopEnabled = false;
			_smtc.IsNextEnabled = true;
			_smtc.IsPreviousEnabled = true;
			_smtc.ButtonPressed += OnButtonPressed;
			_attached = true;
			LogHelper.Log("[SMTC] 会话已建立");
		}
		catch (Exception ex)
		{
			_smtc = null;
			_attached = false;
			LogHelper.LogError(ex, "创建 SMTC 会话失败");
		}
	}

	private static void OnButtonPressed(SystemMediaTransportControls sender,
		SystemMediaTransportControlsButtonPressedEventArgs args)
	{
		try
		{
			if (_suppressButtonEcho)
			{
				return;
			}
			switch (args.Button)
			{
				case SystemMediaTransportControlsButton.Play:
					PlayRequested?.Invoke();
					break;
				case SystemMediaTransportControlsButton.Pause:
					PauseRequested?.Invoke();
					break;
				case SystemMediaTransportControlsButton.Stop:
					StopRequested?.Invoke();
					break;
				case SystemMediaTransportControlsButton.Next:
					NextRequested?.Invoke();
					break;
				case SystemMediaTransportControlsButton.Previous:
					PrevRequested?.Invoke();
					break;
				case SystemMediaTransportControlsButton.Rewind:
					SeekRequested?.Invoke(TimeSpan.Zero);
					break;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "处理 SMTC 按键失败");
		}
	}

	/// <summary>把 NAudio 侧的真实播放状态同步到 SMTC，并让影子播放器跟上时间轴。</summary>
	public static void SyncPlayback(bool isPlaying, TimeSpan position, TimeSpan duration)
	{
		try
		{
			if (_smtc == null)
			{
				return;
			}
			var timeline = new SystemMediaTransportControlsTimelineProperties
			{
				StartTime = TimeSpan.Zero,
				EndTime = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1),
				Position = position < TimeSpan.Zero ? TimeSpan.Zero : position,
				MinSeekTime = TimeSpan.Zero,
				MaxSeekTime = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1)
			};
			_smtc.UpdateTimelineProperties(timeline);
			_smtc.PlaybackStatus = isPlaying
				? MediaPlaybackStatus.Playing
				: MediaPlaybackStatus.Paused;

			// 影子播放器静音同步播放：这样进度条由系统自己推进，不会和 NAudio 越走越偏。
			// 用户按键时 _suppressButtonEcho 挡住回环，避免两边互相触发。
			if (_player != null && _mirroredPath != null)
			{
				_suppressButtonEcho = true;
				try
				{
					if (isPlaying)
					{
						if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
						{
							_player.Play();
						}
					}
					else if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
					{
						_player.Pause();
					}
					TimeSpan target = position;
					TimeSpan length = _player.NaturalDuration;
					if (length > TimeSpan.Zero)
					{
						TimeSpan drift = (_player.Position - target).Duration();
						// 偏差超过 1.5 秒才纠正，避免每 tick 抖动
						if (drift > TimeSpan.FromSeconds(1.5))
						{
							_player.Position = target < length ? target : TimeSpan.Zero;
						}
					}
				}
				finally
				{
					_suppressButtonEcho = false;
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步 SMTC 播放状态失败");
		}
	}

	/// <summary>换曲时换影子播放器的源，同时更新系统显示的曲目信息。</summary>
	public static void SetTrack(string? path, string? title, string? artist,
		string? album, string? coverPath, TimeSpan duration)
	{
		try
		{
			if (_smtc == null)
			{
				return;
			}
			LogHelper.LogDebug($"[SMTC] SetTrack path={path} title={title} artist={artist} cover={coverPath} dur={duration}");
			string shownTitle = ResolveTitle(path, title);
			var display = _smtc.DisplayUpdater;
			display.Type = MediaPlaybackType.Music;
			// 浮层上行是大字标题（曲名；没曲名就用文件名）。
			display.MusicProperties.Title = shownTitle;
			display.MusicProperties.Artist = string.IsNullOrWhiteSpace(artist) ? "" : artist!;
			// 浮层下行：不要让系统回落到文件路径，直接固定成程序名。
			display.MusicProperties.AlbumTitle = ResolveSubtitle(album);
			if (!string.IsNullOrWhiteSpace(coverPath) && System.IO.File.Exists(coverPath))
			{
				// 这里绝对不能阻塞：SetTrack 是在 UI 线程上被调用的（每次换曲一次）。
				// 取缩略图要走 StorageFile 的 WinRT 异步，同步 GetResult() 会把 UI 线程卡住，
				// 表现就是切歌时整个窗口假死。改成丢到后台取，回来再补一次 display.Update()。
				display.Thumbnail = null;
				QueueThumbnail(display, coverPath!);
			}
			else
			{
				display.Thumbnail = null;
			}
			display.Update();

			// 只有换曲才换源，避免每 tick 重建解码器
			if (!string.Equals(_mirroredPath, path, StringComparison.OrdinalIgnoreCase))
			{
				_mirroredPath = path;
				if (_player == null)
				{
					return;
				}
				if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
				{
					// 走 MediaPlaybackItem + ApplyDisplayProperties：
					// 系统浮层读的媒体信息主要来自播放项的显示属性，
					// 只填 _smtc.DisplayUpdater 的话系统那边经常还是空的。
					// MediaItemDisplayProperties 在这个投影里没有无参构造，
					// 得从播放项上取一份现成的再改。
					// 换源前先把旧的拆干净：MediaSource 是 IDisposable，
					// 不释放的话每次换曲都漏一个解码管线，连播久了会内存涨/CPU 空转。
					ReleaseShadowSource();
					_shadowSource = MediaSource.CreateFromUri(new Uri(path!));
					var item = new Windows.Media.Playback.MediaPlaybackItem(_shadowSource);
					var itemProps = item.GetDisplayProperties();
					itemProps.Type = MediaPlaybackType.Music;
					itemProps.MusicProperties.Title = shownTitle;
					itemProps.MusicProperties.Artist = string.IsNullOrWhiteSpace(artist)
						? ""
						: artist!;
					itemProps.MusicProperties.AlbumTitle = ResolveSubtitle(album);
					item.ApplyDisplayProperties(itemProps);
					_player.Source = item;
					LogHelper.LogDebug($"[SMTC] shadow source set: title={shownTitle} sub={itemProps.MusicProperties.AlbumTitle}");
				}
				else
				{
					ReleaseShadowSource();
					_player.Source = null;
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "更新 SMTC 曲目信息失败");
		}
	}

	/// <summary>
	/// 浮层上行（大字）：优先用曲名，没有曲名就退回文件名，再没有就退回程序名。
	/// 用户要求上行只显示音乐名/文件名，不要路径。
	/// </summary>
	private static string ResolveTitle(string? path, string? title)
	{
		if (!string.IsNullOrWhiteSpace(title))
		{
			return title.Trim();
		}
		if (!string.IsNullOrWhiteSpace(path))
		{
			try
			{
				string name = System.IO.Path.GetFileNameWithoutExtension(path);
				if (!string.IsNullOrWhiteSpace(name))
				{
					return name;
				}
			}
			catch
			{
				// 非法路径就当没给
			}
		}
		return AppIdentity.DisplayName;
	}

	/// <summary>
	/// 浮层下行：用户明确要求「不要显示路径，显示程序名称」。
	/// 所以 album 为空时也照样写程序名，否则 MediaPlaybackItem 会把文件全路径顶上来。
	/// </summary>
	private static string ResolveSubtitle(string? album) =>
		string.IsNullOrWhiteSpace(album) ? AppIdentity.DisplayName : album.Trim();

	/// <summary>拆掉影子播放器上一个源，避免 MediaSource / 解码管线堆积。</summary>
	private static void ReleaseShadowSource()
	{
		try
		{
			_player!.Source = null;
		}
		catch
		{
			// 换源失败不影响后面的流程
		}
		try
		{
			_shadowSource?.Dispose();
		}
		catch
		{
		}
		_shadowSource = null;
	}

	/// <summary>
	/// 后台取封面，取完再回 UI 线程刷一次显示。
	/// 原来这里是 StorageFile...AsTask().GetAwaiter().GetResult()，同步等 WinRT 异步，
	/// 而 SetTrack 又是 UI 线程调的 —— 切歌时会把窗口卡住。改成真异步。
	/// </summary>
	private static void QueueThumbnail(SystemMediaTransportControlsDisplayUpdater display, string path)
	{
		// 记录代次：快速连点/连切歌时，只有最后一次取回来的封面才允许写进显示，
		// 否则慢的那次会把新曲目的封面覆盖成旧的。
		long ticket = ++_thumbTicket;
		_ = Task.Run(async () =>
		{
			try
			{
				var file = await StorageFile.GetFileFromPathAsync(path);
				if (file is null)
				{
					return;
				}
				var thumb = RandomAccessStreamReference.CreateFromFile(file);
				var disp = _dispatcher;
				if (disp is null)
				{
					return;
				}
				await disp.InvokeAsync(() =>
				{
					if (ticket != _thumbTicket)
					{
						return;
					}
					display.Thumbnail = thumb;
					display.Update();
				});
			}
			catch
			{
				// 封面读不到就算了，不影响播放
			}
		});
	}

	/// <summary>清空曲目信息（没有在播的时候调用，避免系统浮层留着上一首）。</summary>
	public static void ClearTrack()
	{
		try
		{
			_mirroredPath = null;
			if (_player != null)
			{
				_player.Source = null;
			}
			if (_smtc != null)
			{
				var display = _smtc.DisplayUpdater;
				display.Type = MediaPlaybackType.Music;
				display.MusicProperties.Title = AppIdentity.DisplayName;
				display.MusicProperties.Artist = "";
				display.MusicProperties.AlbumTitle = "";
				display.Thumbnail = null;
				display.Update();
				_smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
				_smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
				{
					StartTime = TimeSpan.Zero,
					EndTime = TimeSpan.Zero,
					Position = TimeSpan.Zero,
					MinSeekTime = TimeSpan.Zero,
					MaxSeekTime = TimeSpan.Zero
				});
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "清空 SMTC 曲目失败");
		}
	}

	public static void Dispose()
	{
		try
		{
			if (_smtc != null)
			{
				_smtc.ButtonPressed -= OnButtonPressed;
			}
			_player?.Dispose();
		}
		catch
		{
		}
		_player = null;
		_smtc = null;
		_mirroredPath = null;
		_attached = false;
	}
}
