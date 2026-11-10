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
	// 当前媒体卡上展示的是背景视频（而非音乐曲目）时记下它的路径：
	// 背景视频停掉时靠它判断要不要清卡（音乐占卡时不能误清）。
	private static string? _activeVideoPath;
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
			// 万一上次 Attach 半途抛过（_attached 还是 false 但 _player 已经建出来），
			// 这里先把旧播放器拆干净再建新的：每个活动 MediaPlayer 都会在系统里
			// 单独占一张媒体卡，残留一个没有曲目信息的实例，浮层里就会多出一张
			// 「未知应用」的空卡（用户报的第二个媒体卡）。
			if (_player != null)
			{
				try
				{
					_player.SystemMediaTransportControls.ButtonPressed -= OnButtonPressed;
				}
				catch
				{
				}
				try
				{
					_player.Dispose();
				}
				catch
				{
				}
				_player = null;
				_smtc = null;
			}
			_player = new Windows.Media.Playback.MediaPlayer
			{
				// 声音由 NAudio 出，这里必须静音，否则会两路一起响
				IsMuted = true,
				AutoPlay = false
			};
			// 关掉 MediaPlayer 自带的 SMTC 命令接管（MediaPlaybackCommandManager）。
			// 这个管理器默认开着：系统媒体浮层的播放/暂停会被它拦截去操作影子播放器
			//（表现成"按键有反应"，其实是它在管），而上一首/下一首它找不到下一曲目、
			// 什么都不做也不把按键转发给 ButtonPressed —— 用户看到的正是
			// 「折叠态上一首/下一首点了没反应」。手动接管 SMTC 时官方要求
			// 把 CommandManager.IsEnabled 关掉，ButtonPressed 才会收齐全部按键。
			_player.CommandManager.IsEnabled = false;
			_smtc = _player.SystemMediaTransportControls;
			// CommandManager 一关，MediaPlayer 自带的 SMTC Enable/Update 集成链
			// 会跟着被停掉，必须显式把 IsEnabled 重新拉起来。漏了这步的话，
			// 下面所有按钮开关、DisplayUpdater.Update()、PlaybackStatus 上报
			// 都被系统静默忽略（不抛异常、日志无错）—— 表现正是「程序在播放，
			// Win11 音量浮层里却没有媒体卡片」。这就是浮册不显示的根因。
			_smtc.IsEnabled = true;
			_smtc.IsPlayEnabled = true;
			_smtc.IsPauseEnabled = true;
			_smtc.IsStopEnabled = false;
			_smtc.IsNextEnabled = true;
			_smtc.IsPreviousEnabled = true;
			_smtc.ButtonPressed += OnButtonPressed;
			_attached = true;
			// 提前把透明缩略图备好：第一首歌没封面时直接可用，
			// 不会有一瞬间用应用图标顶位的窗口。
			WarmBlankThumbnail();
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
			// 先报 PlaybackStatus 再报时间轴：浮层「有没有媒体卡片」只看状态
			// （Playing/Paused 才显示）。放前面保证即使时间轴更新出幺蛾子，
			// 状态这一最关键的信号也已经送出去了。
			_smtc.PlaybackStatus = isPlaying
				? MediaPlaybackStatus.Playing
				: MediaPlaybackStatus.Paused;
			_smtc.UpdateTimelineProperties(timeline);

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
	/// <summary>全透明缩略图。没有曲目封面时给系统这张，而不是留 null：
	/// **Thumbnail=null 时浮层会拿应用图标顶位**（进程 AUMID 能解析出开始菜单
	/// 条目之后尤其如此），用户明确要求这种情况右侧干脆空着。
	/// 在 Attach 里预热（见 WarmBlankThumbnail），播歌之前一定已经就绪。</summary>
	private static RandomAccessStreamReference? _blankThumbnail;

	/// <summary>预热全透明缩略图。全零 BGRA 的 PNG 编码是纯 WPF 同步操作，
	/// 代价可忽略；写进 WinRT 内存流那段丢到后台线程，避免在 UI 线程上等
	/// IAsyncOperation（哪怕只有几十字节，也没必要赌它同步完成）。
	/// 注意 DataWriter 销毁时会连底层流一起关，先 DetachStream 再 Dispose。
	/// 尺寸用 320×320 而不是 1×1：过小的缩略图实测会被系统当无效值丢掉，
	/// 然后照旧回退到「应用图标补位」（用户 2026-11-08 又截到应用图标）；
	/// 正常封面也是这个量级的尺寸，系统不会嫌弃。</summary>
	private static void WarmBlankThumbnail()
	{
		// 320×320 全零 BGRA：每个像素 alpha=0，编码出来就是一张全透明的 PNG。
		var wb = new System.Windows.Media.Imaging.WriteableBitmap(
			320, 320, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
		var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
		encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(wb));
		using var ms = new System.IO.MemoryStream();
		encoder.Save(ms);
		byte[] png = ms.ToArray();
		_ = Task.Run(() =>
		{
			try
			{
				var stream = new InMemoryRandomAccessStream();
				var writer = new DataWriter(stream);
				writer.WriteBytes(png);
				writer.StoreAsync().AsTask().Wait();
				writer.DetachStream();
				writer.Dispose();
				stream.Seek(0);
				_blankThumbnail = RandomAccessStreamReference.CreateFromStream(stream);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "构造 SMTC 透明缩略图失败");
			}
		});
	}

	public static void SetTrack(string? path, string? title, string? artist,
		string? album, string? coverPath, TimeSpan duration)
	{
		try
		{
			if (_smtc == null)
			{
				return;
			}
			// 音乐占卡：背景视频让位
			_activeVideoPath = null;
			LogHelper.LogDebug($"[SMTC] SetTrack path={path} title={title} artist={artist} cover={coverPath} dur={duration}");
			string shownTitle = ResolveTitle(path, title);
			var display = _smtc.DisplayUpdater;
			// ClearTrack 停播时把会话关掉了（避免系统浮层留空卡），
			// 这里必须重新拉起来，否则下一首播起来浮层不出现。
			_smtc.IsEnabled = true;
			display.Type = MediaPlaybackType.Music;
			// 浮层上行是大字标题（曲名；没曲名就用文件名）。
			display.MusicProperties.Title = shownTitle;
			// 浮层第二行（歌手位）：没拿到歌手名时显示文件存在路径，
			// 让用户知道这首歌是从哪儿来的（用户 2026-11-08 要求）。
			display.MusicProperties.Artist = ResolveArtist(artist, path);
			// 浮层下行：用户要求改回文件路径显示（原先固定成程序名）。
			// 不能留空——MediaPlaybackItem 会把别的东西顶上来。
			display.MusicProperties.AlbumTitle = ResolveSubtitle(album, path);
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
				// 没有曲目封面就不给图（用户要求：别拿程序图标顶上去，
				// 媒体卡右侧空着比挂一个「应用封面」更像正常的播放器）。
				// 只清旧图，不排任何补位任务。
				// ⚠ 不能只置 null：Thumbnail=null 时系统会拿「应用图标」顶位
				// （开始菜单身份解析成功之后必现，用户 2026-11-08 的截图）。
				// 所以这里给一张全透明图，浮层画出来就是空的一块。
				display.Thumbnail = _blankThumbnail;
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
					itemProps.MusicProperties.Artist = ResolveArtist(artist, path);
					itemProps.MusicProperties.AlbumTitle = ResolveSubtitle(album, path);
					// 播放项显示属性是系统浮层读媒体信息的主要来源，这里也得给
					// 透明图，否则浮层还是会拿应用图标补位（和 DisplayUpdater 同理）。
					if (string.IsNullOrWhiteSpace(coverPath) || !System.IO.File.Exists(coverPath))
					{
						itemProps.Thumbnail = _blankThumbnail;
					}
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

	/// <summary>画质/帧率标记（4k、60fps、1080p、60帧 这类，对齐 MhyunBox 的
	/// QualityTagRegex）。用户要求媒体卡标题里不要带这些，和「文件后缀名去掉」
	/// 是同一条动线。要点：
	/// - \d+\s*k 加 (?![a-z]) 负向断言：「4King」这种 K 后跟字母的不算画质；
	/// - 但 [248]k 不加断言——文件名经常连写（「Bad apple!!4k60fps」），
	///   加了「4k」后面紧跟「6」就匹配不上，只剩半截「60fps」更难看；
	/// - 标记前后的 _ - · 空白一起吃掉；「60帧」也是常见写法。</summary>
	private static readonly System.Text.RegularExpressions.Regex QualityTagRegex = new(
		@"[\s_\-·]*(?:[248]k|2160p?|1440p?|1080p?|720p?|480p?|\d{1,4}\s*fps|\d{1,4}\s*帧)(?![a-z])[\s_\-·]*",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase
		| System.Text.RegularExpressions.RegexOptions.Compiled);

	// 剥完标记后可能残留空括号（「名字（4K）」→「名字（）」），一并清掉。
	private static readonly System.Text.RegularExpressions.Regex EmptyBracketRegex = new(
		@"（\s*）|\(\s*\)|\[\s*\]|【\s*】|\{\s*\}",
		System.Text.RegularExpressions.RegexOptions.Compiled);

	/// <summary>剥掉画质/帧率标记，再把清理后悬在首尾的分隔符收掉。
	/// 只裁空白和常见分隔符，**不裁「!」**——「Bad apple!!」的感叹号是歌名的一部分。</summary>
	private static string CleanDisplayTitle(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return raw;
		}
		string cleaned = EmptyBracketRegex.Replace(QualityTagRegex.Replace(raw, " "), "").Trim();
		return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ")
			.Trim(' ', '\u3000', '-', '_', '·', '|', '/', ',', '，', '。', '~');
	}

	/// <summary>
	/// 浮层上行（大字）：优先用曲名，没有曲名就退回文件名，再没有就退回程序名。
	/// 用户要求上行只显示音乐名/文件名，不要路径；画质标记（4k/60fps/1080p）也一并剥掉。
	/// </summary>
	private static string ResolveTitle(string? path, string? title)
	{
		if (!string.IsNullOrWhiteSpace(title))
		{
			return CleanDisplayTitle(title.Trim());
		}
		if (!string.IsNullOrWhiteSpace(path))
		{
			try
			{
				string name = System.IO.Path.GetFileNameWithoutExtension(path);
				if (!string.IsNullOrWhiteSpace(name))
				{
					return CleanDisplayTitle(name);
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
	/// 浮层第二行（歌手位）：有歌手用歌手；没有歌手时按用户要求显示文件的
	/// 存在路径，让人知道这首歌在磁盘上的位置；连路径都没有就留空。
	/// </summary>
	private static string ResolveArtist(string? artist, string? path)
	{
		if (!string.IsNullOrWhiteSpace(artist))
		{
			return artist.Trim();
		}
		return string.IsNullOrWhiteSpace(path) ? "" : path!;
	}

	/// <summary>
	/// 浮层下行：用户要求显示文件路径（曾改成固定程序名，后要求改回来）。
	/// 有专辑名时仍优先专辑；没有就用完整路径，再没有才退程序名。
	/// </summary>
	private static string ResolveSubtitle(string? album, string? path)
	{
		if (!string.IsNullOrWhiteSpace(album))
		{
			return album.Trim();
		}
		if (!string.IsNullOrWhiteSpace(path))
		{
			return path!;
		}
		return AppIdentity.DisplayName;
	}

	/// <summary>
	/// 背景视频（自定义/目录轮播）上卡：音乐侧没有曲目可显时，把当前背景视频
	/// 作为媒体内容报给系统媒体卡。上行是文件名（去后缀），下行是完整路径。
	/// 类型仍用 Music：浮层只管显示字符串，MusicProperties 的 Title/AlbumTitle
	/// 两条链（DisplayUpdater + 播放项显示属性）都验证过能正常渲染。
	/// 音乐一起播，SetTrack 会自动把卡要回去，无需这里让位。
	/// </summary>
	public static void SetVideoTrack(string? path)
	{
		try
		{
			if (_smtc == null || string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
			{
				return;
			}
			string shownTitle = ResolveTitle(path, null);
			if (string.IsNullOrWhiteSpace(shownTitle))
			{
				return;
			}
			_smtc.IsEnabled = true;
			_activeVideoPath = path;
			var display = _smtc.DisplayUpdater;
			display.Type = MediaPlaybackType.Music;
			display.MusicProperties.Title = shownTitle;
			display.MusicProperties.Artist = path!;
			display.MusicProperties.AlbumTitle = path!;
			display.Thumbnail = _blankThumbnail;
			display.Update();
			// 影子播放器不接视频源（它的时间轴跟 NAudio 走），
			// 状态交给 SyncPlayback 的 Paused 兜底，浮层显示但不算在播。
			LogHelper.LogDebug($"[SMTC] 视频背景上卡: title={shownTitle} path={path}");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "更新 SMTC 视频背景信息失败");
		}
	}

	/// <summary>背景视频停掉时清卡：只在卡上确实是背景视频时才清，
	/// 音乐占卡时（StopBgVideo 与音乐状态变化撞在一起）不能误清。</summary>
	public static void ClearVideoTrack()
	{
		if (_activeVideoPath == null)
		{
			return;
		}
		_activeVideoPath = null;
		ClearTrack();
	}

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
			catch (Exception ex)
			{
				// 封面读不到就算了，不影响播放；但要留痕 —— 之前静默吞掉，
				// 出了「浮层一直没图 / Thumbnail 无效引用」根本无从查起。
				LogHelper.LogError(ex, "取 SMTC 封面缩略图失败: " + path);
				// 关键：失败时 Thumbnail 还停在 SetTrack 里设的 null，
				// 系统浮层会拿「应用图标」补位（用户 2026-11-08 又截到一次）。
				// 回退成透明图，保证没封面时右侧就是空的。
				try
				{
					var disp = _dispatcher;
					if (disp != null && ticket == _thumbTicket && _blankThumbnail != null)
					{
						await disp.InvokeAsync(() =>
						{
							if (ticket == _thumbTicket)
							{
								display.Thumbnail = _blankThumbnail;
								display.Update();
							}
						});
					}
				}
				catch
				{
					// 兜底失败就随它去，不影响播放
				}
			}
		});
	}

	/// <summary>清空曲目信息（没有在播的时候调用，避免系统浮层留着上一首）。</summary>
	public static void ClearTrack()
	{
		try
		{
			_mirroredPath = null;
			_activeVideoPath = null;
			if (_player != null)
			{
				_player.Source = null;
			}
			if (_smtc == null)
			{
				return;
			}
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
			// 停播后把会话整个关掉，别在系统浮层里留一张空卡：
			// 空会话没有曲目信息，浮层只能拿「应用名」顶上，而免安装程序的
			// 应用名解析本来就不稳（本机属性系统损坏时更甚），那张卡容易被
			// 显示成「未知应用」，并排在我们自己的卡后面（用户报的第二个媒体卡）。
			// IsEnabled=false 之后系统不再列它；下次 SetTrack 会重新拉起来。
			_smtc.IsEnabled = false;
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
		_activeVideoPath = null;
		_attached = false;
	}
}
