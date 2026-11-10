using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MusicBox.Services.Effects;
using MusicBox.Settings;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicBox.Services;

public static class MusicService
{
	/// <summary>本地曲库认的媒体后缀。mp4 也在这里：带音轨的视频照样能当歌播，
	/// 声音交给 MediaFoundationReader（只读第一条音轨），画面由主窗口的背景
	/// 视频层跟播——所以「音乐库里的 mp4」既出声音也出画面，不是只播音频。</summary>
	private static readonly string[] AudioExtensions = new string[8] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".mp4" };

	private static SynchronizationContext? _ui;

	private static IWavePlayer? _out;

	private static WaveStream? _reader;

	private static VolumeSampleProvider? _vol;

	private static Thread? _watch;

	private static volatile bool _endFired;

	private static FileSystemWatcher? _libraryWatcher;

	private static Timer? _reloadDebounce;

	private static readonly object ReloadLock = new object();

	private static string? _scanKey;

	private static int _scanGeneration;

	private static List<NeteaseTrack> _onlineQueue = new List<NeteaseTrack>();

	// ---------- 在线会话队列（手动切歌的层级判定） ----------
	// 「会话」= 用户最近一次从某个在线列表（在线页搜索结果 / 歌单灌入 /
	// 音乐库收藏夹）点播的那一批歌。老逻辑只认网易云搜索页那份
	// _onlineQueue：在音乐库听在线歌时按「下一首」热键，当前歌要么被
	// ImportToLibrary 收进本地曲库（IndexOfCurrent 命中本地列表 → 播了
	// 本地文件），要么网易云缓存目录不在曲库里但收藏夹队列没人登记
	//（StepOnlineQueue 落空 → 掉到 Play(list[0])），总之都穿帮成
	// 「切到了本地音乐队列」。现在由点播方把整份列表登记成会话队列，
	// 手动上一首/下一首优先在会话里推进；会话只在播本地文件或 Stop 时撤销。
	private static IReadOnlyList<object> _sessionQueue = Array.Empty<object>();

	private static int _sessionIndex = -1;

	/// <summary>会话里某一条怎么播：由在线页注入（PlayExternal 全平台都能播）。</summary>
	public static Func<object, bool>? SessionPlayer { get; set; }

	/// <summary>会话里「下一首」怎么提前取到本地缓存：由在线页注入。
	/// 只负责下载（各平台 DownloadSongAsync 都是幂等缓存），绝不碰播放状态、
	/// 取流提示和 UI —— 切歌时命中缓存即可无缝衔接。</summary>
	public static Func<object, CancellationToken, Task>? SessionPrefetcher { get; set; }

	private static CancellationTokenSource? _prefetchCts;

	/// <summary>已为哪首当前曲目做过预取（一首只试一次，避免每 250ms 重复打网络）。</summary>
	private static string? _prefetchedFor;

	private static int _prefetchGeneration;

	/// <summary>距曲末多少秒开始预取。太早会白下（用户手动切歌），太晚不够下完一首 3~5MB 的歌。</summary>
	private const double PrefetchLeadSeconds = 25.0;

	/// <summary>点播方（在线页/音乐库收藏夹）登记会话队列：tracks 是用户
	/// 看到的那份列表，playedIndex 是这次点中的那条。队列和平台信息留在
	/// 数据对象里，MusicService 只管顺序推进，不关心具体平台。</summary>
	public static void SetSessionQueue(IReadOnlyList<object>? tracks, int playedIndex)
	{
		lock (QueueLock)
		{
			_sessionQueue = tracks ?? Array.Empty<object>();
			_sessionIndex = playedIndex;
		}
	}

	private static void ClearSessionQueue()
	{
		int count;
		lock (QueueLock)
		{
			count = _sessionQueue.Count;
			_sessionQueue = Array.Empty<object>();
			_sessionIndex = -1;
		}
		if (count > 0)
		{
			LogHelper.LogDebug("会话队列已撤销（切回本地播放/停止）");
		}
	}

	/// <summary>在会话队列里走一步。返回 false = 没有活动会话，调用方
	/// 再落到本地曲库/网易云搜索队列的老逻辑上。
	///
	/// 快捷键切歌必须「按一下就出声」：当前目标已被实测标记受限
	///（TrackAccess.Restricted，点过一次被平台拒过）时直接跳过它继续推进，
	/// 最多连跳 SkipBrokenMax 条，不在一首注定播不出来的歌上停住。
	/// 正常曲目仍然走 SessionPlayer 一次到位；点行播放的路径不经这里，行为不变。</summary>
	private static bool StepSessionQueue(int delta)
	{
		for (int attempt = 0; attempt < SkipBrokenMax; attempt++)
		{
			object track;
			int next;
			int count;
			Func<object, bool>? player;
			lock (QueueLock)
			{
				if (!HasActiveSessionLocked())
				{
					return false;
				}
				player = SessionPlayer;
				count = _sessionQueue.Count;
				next = ((_sessionIndex + delta) % count + count) % count;
				_sessionIndex = next;
				track = _sessionQueue[next];
			}
			if (TrackAccess.IsRestricted(track))
			{
				// 实测受限的队列条目：不启动播放器、不弹 VIP 提示，静默略过。
				// （上次点它时平台已经明确拒绝过，再点也是同样的结局。）
				LogHelper.Log($"会话切歌：跳过受限曲目 {next + 1}/{count}");
				continue;
			}
			LogHelper.Log($"会话切歌：{next + 1}/{count}");
			try
			{
				if (player != null && player(track))
				{
					return true;
				}
				// 会话播放器明确说播不动（同步异常等）：换下一条再试，
				// 不把「立即失败」当成「已经切过去了」。
				LogHelper.Log($"会话切歌：第 {next + 1} 条播放未启动，跳过");
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "会话切歌失败");
			}
		}
		return false;
	}

	private static bool HasActiveSession()
	{
		lock (QueueLock)
		{
			return HasActiveSessionLocked();
		}
	}

	private static bool HasActiveSessionLocked() =>
		SessionPlayer != null && _sessionQueue.Count > 0
		&& _sessionIndex >= 0 && _sessionIndex < _sessionQueue.Count;

	/// <summary>顺序播放：当前是否已处在会话队列的最后一首。</summary>
	private static bool SessionAtLast()
	{
		lock (QueueLock)
		{
			return _sessionIndex >= _sessionQueue.Count - 1;
		}
	}

	/// <summary>取会话里「下一首」的条目（不推进索引、不播放），供预加载用。
	/// 跳过实测受限的条目；单曲循环时不预取（下一首就是当前这首）。
	/// 返回 null 表示没有可预取的目标（无会话 / 会话到末尾且不循环 / 全受限）。</summary>
	private static object? PeekSessionNext()
	{
		object[] snapshot;
		int cursor;
		lock (QueueLock)
		{
			if (!HasActiveSessionLocked())
			{
				return null;
			}
			// 复制一份快照：后面要遍历 + 调 TrackAccess（可能不便宜），
			// 不能把锁一直攥在手里，也不能边遍历边被 UI 线程换掉队列。
			snapshot = new object[_sessionQueue.Count];
			for (int i = 0; i < snapshot.Length; i++)
			{
				snapshot[i] = _sessionQueue[i];
			}
			cursor = _sessionIndex; // 局部游标，绝不改 _sessionIndex（预取无权推进队列）
		}
		string mode = NormalizeMode(SettingsManager.Instance.PlaybackMode);
		if (mode == "loop")
		{
			return null;
		}
		int n = snapshot.Length;
		for (int attempt = 0; attempt < SkipBrokenMax; attempt++)
		{
			int next = ((cursor + 1) % n + n) % n;
			// 顺序播放（once）到最后一首就停，不该预取绕回开头的那首。
			if (mode == "once" && next == 0)
			{
				return null;
			}
			object track = snapshot[next];
			if (!TrackAccess.IsRestricted(track))
			{
				return track;
			}
			cursor = next;
		}
		return null;
	}

	/// <summary>由播放监视线程每 250ms 调一次：快到曲末时把下一首提前下进缓存。
	/// 只下载、绝不碰播放状态；切歌时各平台 DownloadSongAsync 命中缓存即可无缝衔接。
	/// 一首只触发一次（_prefetchedFor 记当前曲目），换曲由 ResetPrefetch 清掉。</summary>
	private static void MaybePrefetchNext(WaveStream reader)
	{
		try
		{
			if (SessionPrefetcher == null || _userPaused || NoMusicMode
				|| string.IsNullOrEmpty(CurrentPath))
			{
				return;
			}
			double remaining = (reader.TotalTime - reader.CurrentTime).TotalSeconds;
			if (remaining > PrefetchLeadSeconds || remaining <= 0)
			{
				return;
			}
			if (string.Equals(_prefetchedFor, CurrentPath, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			object? next = PeekSessionNext();
			// 先取消上一首遗留的预取（ResetPrefetch 会清标记），再立本首的标记。
			ResetPrefetch();
			_prefetchedFor = CurrentPath;
			if (next == null)
			{
				// 没有可预取目标也记下来，别每 250ms 白算一次。
				return;
			}
			var cts = new CancellationTokenSource();
			_prefetchCts = cts;
			int gen = ++_prefetchGeneration;
			Func<object, CancellationToken, Task> prefetch = SessionPrefetcher;
			_ = Task.Run(async () =>
			{
				try
				{
					await prefetch(next, cts.Token).ConfigureAwait(false);
					LogHelper.LogDebug($"[预加载] 下一首已就绪 gen={gen}");
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					LogHelper.LogDebug("[预加载] 下一首失败: " + ex.Message);
				}
				finally
				{
					if (ReferenceEquals(_prefetchCts, cts))
					{
						_prefetchCts = null;
					}
					cts.Dispose();
				}
			});
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("[预加载] 调度异常: " + ex.Message);
		}
	}

	/// <summary>换曲/停止：取消还没跑完的预取，清掉「本首已预取」标记。</summary>
	private static void ResetPrefetch()
	{
		CancellationTokenSource? cts = Interlocked.Exchange(ref _prefetchCts, null);
		if (cts != null)
		{
			try
			{
				cts.Cancel();
			}
			catch (Exception ex)
			{
				// Cancel 只会在 CTS 已释放/内部回调抛错时出事，此时本次预取本来就废了。
				LogHelper.LogDebug("取消预取失败: " + ex.Message);
			}
		}
		_prefetchedFor = null;
	}

	/// <summary>快捷键/自动推进一次最多连跳几条受限或立即失败的曲目。
	/// 队列整段都坏时也只试这么多，避免一次按键把几百条全扫一遍。</summary>
	private const int SkipBrokenMax = 6;

	private static int _onlineIndex = -1;
	private static string? _onlineTitle;

	private static SavedTrack? _onlineMeta;

	/// <summary>在线队列 / 会话队列的跨线程锁。UI 线程写（点播、登记会话），
	/// 播放监视线程读（预取下一首、判断会话是否还在），两边必须走同一把锁。
	/// 约定：锁内只做字段读写和快照复制，任何回调（SessionPlayer / PlayOnline）
	/// 一律放到锁外，避免持锁回调再进来到处加锁造成死锁。</summary>
	private static readonly object QueueLock = new object();

	// 当前曲目来源页标记（library/fav/online），见 PlayCore 的 source 参数。
	private static string? _currentSource;

	/// <summary>当前播放曲目是从哪个页面/入口入队的（library=音乐库、fav=我的收藏、
	/// online=搜索音乐）。定位小球单击时按它跳页；null = 未知来源（如启动恢复），
	/// 调用方自行兜底到最近播放。</summary>
	public static string? CurrentSource => _currentSource;

	/// <summary>本地文件按文件名匹配到网易云后补上的封面/歌名/歌手。
	/// 键是播放路径；只存内存，重开程序重新匹配一次就好，不值得落盘。
	/// 歌词在后台线程写、列表行在 UI 线程读，所以用并发字典。</summary>
	private static readonly ConcurrentDictionary<string, SavedTrack> LocalMatchedMeta =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>本地封面每补上一张就 +1。曲库页的列表指纹带上它，
	/// 正在看曲库时封面到了才会重建那些行（否则要等下次进页面）。</summary>
	public static int LocalCoverVersion { get; private set; }

	/// <summary>每首歌「歌词」和「封面」各自是从哪个平台来的。
	/// 底栏封面方块就用它画那圈双色框：网易云红、QQ 绿。
	/// 和 LocalMatchedMeta 一样只存内存，键是播放路径。</summary>
	private static readonly ConcurrentDictionary<string, MatchSources> MatchSourceMap =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>记下某首歌的歌词/封面各自来自哪个平台。只传要改的那一半，
	/// 另一半保持原值（封面往往比歌词先到，先记封面后记歌词）。</summary>
	public static void NoteMatchSource(string? path, MatchSource? lyric = null, MatchSource? cover = null)
	{
		if (string.IsNullOrWhiteSpace(path) || (lyric == null && cover == null))
		{
			return;
		}
		MatchSources cur = MatchSourceMap.TryGetValue(path!, out MatchSources old) ? old : default;
		MatchSources next = new MatchSources(lyric ?? cur.Lyric, cover ?? cur.Cover);
		MatchSourceMap[path!] = next;
		if (MatchSourceMap.Count > 500)
		{
			// 跟 LocalMatchedMeta 一样随手丢一批，这是纯展示用的记录，丢了无所谓
			int drop = MatchSourceMap.Count - 500;
			foreach (string k in MatchSourceMap.Keys)
			{
				if (drop-- <= 0)
				{
					break;
				}
				MatchSourceMap.TryRemove(k, out _);
			}
		}
	}

	/// <summary>取某首歌的歌词/封面来源；没记过就是两边都没有。</summary>
	public static MatchSources GetMatchSource(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return default;
		}
		return MatchSourceMap.TryGetValue(path!, out MatchSources s) ? s : default;
	}

	/// <summary>播放请求代号。每次发起播放 +1；所有 await 回来后先比对，
	/// 对不上说明用户已经改播别的歌，本次结果直接丢弃。
	/// 没有它就会出「随进度强覆盖」：先点的 A（VIP 要等下载）后完成，
	/// 会把后点的、已经在播的 B 顶掉。</summary>
	private static int _playToken;

	public const string NoMusicMarker = "<no-music>";

	public static List<string> KnownMusic { get; } = new List<string>();

	public static string? CurrentPath { get; private set; }

	public static bool NoMusicMode { get; private set; }

	/// <summary>用户主动暂停的逻辑状态位。
	/// Pause 是「250ms 淡出 + 淡出结束才 output.Pause()」，这段窗口里
	/// NAudio 的 PlaybackState 仍是 Playing；只靠底层状态判断的话，
	/// 播放/暂停按钮会一直显示「暂停图标 ‖」不变（截图里那个问题）。
	/// 这个标志由 Pause 立刻置位、Resume/起播立刻清除，UI 读它才是即时的。</summary>
	private static volatile bool _userPaused;

	/// <summary>对 UI 暴露的「是否在播放」：底层输出在播、且没有被用户暂停、且不是无音乐模式。
	/// 这里必须掺入逻辑暂停位：Pause 是 250ms 淡出，淡出窗口里 NAudio 的
	/// PlaybackState 仍是 Playing，只读底层会让播放/暂停按钮一直停在「‖」。</summary>
	public static bool IsPlaying
	{
		get
		{
			IWavePlayer? output = _out;
			return !_userPaused && !NoMusicMode
				&& output != null && output.PlaybackState == PlaybackState.Playing;
		}
	}

	public static bool IsSurroundActive => SettingsManager.Instance.SurroundEnable;

	public static TimeSpan Position
	{
		get
		{
			WaveStream? reader = _reader;
			try
			{
				return reader != null && reader.Position <= reader.Length ? reader.CurrentTime : TimeSpan.Zero;
			}
			catch
			{
				return TimeSpan.Zero;
			}
		}
	}

	public static TimeSpan Duration
	{
		get
		{
			WaveStream? reader = _reader;
			try
			{
				return reader != null ? reader.TotalTime : TimeSpan.Zero;
			}
			catch
			{
				return TimeSpan.Zero;
			}
		}
	}

	public static string DefaultMusicFolder => Path.Combine(AppContext.BaseDirectory, "Music");

	public static string MusicFolder
	{
		get
		{
			string folder = SettingsManager.Instance.MusicFolder;
			return string.IsNullOrWhiteSpace(folder) ? DefaultMusicFolder : folder;
		}
	}

	public static event Action? StateChanged;

	/// <summary>音频真正出声**之前**广播一次目标路径。给背景视频留一个预载窗口：
	/// 353MB 的 4K mp4 从设 Source 到能出首帧要几百毫秒，等 StateChanged 再去
	/// 挂 Source 就必然「音乐先跑、画面后追」。这里同步触发，处理器在
	/// output.Play() 之前跑完，Source 一挂上 MediaElement 就开始后台解码了。
	/// 处理器必须自己吞异常：预载失败只该少一帧画面，不能让整首歌播不出来。</summary>
	public static event Action<string>? TrackStarting;

	/// <summary>取直链/下载期间的状态：(是否正在取, 曲目显示名)。
	/// 取直链常常要好几秒甚至几十秒（VIP 更久），不给提示的话
	/// 点完歌整个界面像死了一样，用户会以为没点上而重复点击。</summary>
	public static event Action<bool, string?>? FetchStateChanged;

	private static void OnFetchStateChanged(bool fetching, string? title)
	{
		try
		{
			_ui?.Post(_ => FetchStateChanged?.Invoke(fetching, title), null);
		}
		catch
		{
		}
	}

	public static void Initialize()
	{
		_ui = SynchronizationContext.Current ?? new SynchronizationContext();
		EnsureLibraryWatcher();
		// 扫盘已经挪到后台了。迁移必须排在扫完之后，
		// 不然刚被搬走的文件会又被扫一遍，白做功。
		RefreshKnownMusicAsync();
		RunAfterScan(() =>
		{
			try
			{
				MigrateLegacyBbCache();
			}
			catch
			{
			}
		});
	}

	public static void ApplySettings()
	{
		_ui ??= SynchronizationContext.Current ?? new SynchronizationContext();
		EnsureLibraryWatcher();
		RefreshKnownMusicAsync();
		// 这段逻辑依赖 KnownMusic 已经填好（自动续播、库里空了要停），
		// 所以不能直接在这儿跑，得挂到扫盘完成之后，否则 KnownMusic 还是空的，
		// 自动续播会被无声跳过。
		RunAfterScan(() =>
		{
			if (KnownMusic.Count == 0)
			{
				if (CurrentPath != null)
				{
					Stop();
				}
			}
			// 新的「启动时自动播放（随机）」开着时，启动播放交给 MainWindow 按
			// 播放上下文随机恢复（TryStartupAutoPlay），老的「续播上一首」让位，
			// 避免两路启动行为同时开播、随机恢复永远抢不到先手。
			else if (!NoMusicMode && SettingsManager.Instance.AutoPlayOnStartup && !SettingsManager.Instance.MusicPaused
				&& !SettingsManager.Instance.StartupAutoPlay)
			{
				string? target = ResolveStartTarget();
				if (target != null && (CurrentPath == null || !string.Equals(CurrentPath, target, StringComparison.OrdinalIgnoreCase)))
				{
					Play(target);
				}
			}
		});
	}

	private static string? ResolveStartTarget()
	{
		if (CurrentPath != null && File.Exists(CurrentPath))
		{
			return CurrentPath;
		}
		string musicPath = SettingsManager.Instance.MusicPath;
		if (!string.IsNullOrWhiteSpace(musicPath))
		{
			foreach (string item in KnownMusic)
			{
				if (string.Equals(item, musicPath, StringComparison.OrdinalIgnoreCase))
				{
					return item;
				}
			}
		}
		return KnownMusic.Count > 0 ? KnownMusic[0] : null;
	}

	private static string BuildScanKey()
	{
		return MusicFolder;
	}

	public static void InvalidateScanCache()
	{
		_scanKey = null;
	}

	public static void ReloadLibrary()
	{
		InvalidateScanCache();
		// 递归扫盘 + 整表重建会卡住 UI 线程，交给后台做
		RefreshKnownMusicAsync();
	}

	/// <summary>页面被打开时调用：在后台重扫一遍，让「在文件资源管理器里做的事」
	/// 能被看到。已有扫描在跑就直接跳过 —— 快速切页时不必排一串重复的扫盘。
	///
	/// 监视器只在程序开着的时候管用，而用户经常是「开着音乐、把文件夹重命名、
	/// 再切回音乐库」—— 这一次切换就是这里补的刷新。
	/// </summary>
	public static void RefreshLibraryIfIdle()
	{
		if (_scanRunning)
		{
			return;
		}
		InvalidateScanCache();
		RefreshKnownMusicAsync();
	}

	private static void EnsureLibraryWatcher()
	{
		try
		{
			string folder = MusicFolder;
			if (_libraryWatcher != null)
			{
				if (string.Equals(_libraryWatcher.Path, folder, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				_libraryWatcher.Dispose();
				_libraryWatcher = null;
			}
			if (Directory.Exists(folder))
			{
				_libraryWatcher = CreateLibraryWatcher(folder);
			}
		}
		catch (Exception ex)
		{
			// 监视器建不起来只影响「外部改动自动刷新」，音乐库本身照常可用，
			// 所以不往上抛，但必须留下原因，否则以后排查「为什么改名不刷新」毫无线索。
			LogHelper.LogError(ex, "创建音乐库监视器失败");
		}
	}

	private static FileSystemWatcher CreateLibraryWatcher(string folder)
	{
		FileSystemWatcher watcher = new FileSystemWatcher(folder)
		{
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
		};
		watcher.Created += OnLibraryFileChanged;
		watcher.Changed += OnLibraryFileChanged;
		// Renamed 要单独挂：只有它带旧名（RenamedEventArgs.OldFullPath），
		// 统一丢给 OnLibraryFileChanged 就把「这文件被改名了」这个信息丢了。
		watcher.Renamed += OnLibraryRenamed;
		watcher.Deleted += OnLibraryFileChanged;
		watcher.Error += OnLibraryWatcherError;
		watcher.EnableRaisingEvents = true;
		return watcher;
	}

	private static void OnLibraryWatcherError(object sender, ErrorEventArgs e)
	{
		LogHelper.Log("音乐库监视器错误: " + (e.GetException()?.Message ?? "未知"));
	}

	/// <summary>外部改名：既要走常规重扫，也要把「旧路径 → 新路径」记下来。
	///
	/// 为什么必须记：改名之后 KnownMusic 里那条旧路径就失效了，
	/// 而收藏 / 最近播放 / 自定义顺序里存的全是路径。
	/// 光重扫只能让音乐库列表显示新名字，收藏那一页还是改名前的歌名，
	/// 而且点播放会报「文件不存在」——这正是「在文件资源管理器里改了名，
	/// 程序里还是老样子」的全部原因。改名事件是唯一能拿到新旧对应关系的地方，
	/// 错过就只能靠猜文件名了。
	/// </summary>
	private static void OnLibraryRenamed(object sender, RenamedEventArgs e)
	{
		try
		{
			string oldPath = e.OldFullPath;
			string newPath = e.FullPath;
			if (!string.IsNullOrEmpty(oldPath)
				&& !string.IsNullOrEmpty(newPath)
				&& !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
				&& AudioExtensions.Contains(Path.GetExtension(newPath).ToLowerInvariant()))
			{
				RememberRename(oldPath, newPath);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库改名事件处理失败: " + ex.Message);
		}
		OnLibraryFileChanged(sender, e);
	}

	private static readonly object RenameLock = new object();
	private static readonly Dictionary<string, string> RenameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	/// <summary>改名记录的先后顺序，用来给 RenameMap 封顶（防止长时间运行后无限增长）。</summary>
	private static readonly Queue<string> RenameOrder = new Queue<string>();
	private const int RenameMapLimit = 512;

	private static void RememberRename(string oldPath, string newPath)
	{
		try
		{
			lock (RenameLock)
			{
				// 连续改名（A → B → C）时链条会断：这里把链条压平，
				// 让 A 直接指到最终路径，免得调用方还要跟着跳。
				string tail = newPath;
				int guard = 0;
				while (guard++ < 8 && RenameMap.TryGetValue(tail, out string? next))
				{
					if (string.Equals(next, tail, StringComparison.OrdinalIgnoreCase)) break;
					tail = next;
				}
				if (!RenameMap.ContainsKey(oldPath))
				{
					RenameOrder.Enqueue(oldPath);
				}
				RenameMap[oldPath] = tail;
				while (RenameOrder.Count > RenameMapLimit)
				{
					string drop = RenameOrder.Dequeue();
					RenameMap.Remove(drop);
				}
			}
		}
		catch
		{
		}
	}

	/// <summary>把一个可能已经被改过名的路径映射到它现在的位置。映射不上返回 null。</summary>
	public static string? MapRenamedPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		try
		{
			string cur = path;
			lock (RenameLock)
			{
				for (int i = 0; i < 8; i++)
				{
					if (!RenameMap.TryGetValue(cur, out string? next)) break;
					if (string.Equals(next, cur, StringComparison.OrdinalIgnoreCase)) break;
					cur = next;
				}
			}
			return string.Equals(cur, path, StringComparison.OrdinalIgnoreCase) ? null : cur;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>旧路径已经不存在、而改名记录指向的新路径确实存在时，返回新路径；否则返回空串。</summary>
	private static string ResolveRenamedExisting(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		try
		{
			if (File.Exists(path))
			{
				return "";
			}
			string? mapped = MapRenamedPath(path);
			return mapped != null && File.Exists(mapped) ? mapped : "";
		}
		catch
		{
			return "";
		}
	}

	/// <summary>音乐库内容的指纹。
	///
	/// 只看「有几首歌」是抓不到改名的：改名 / 替换文件都不改条数，
	/// 于是列表的签名前后一样，音乐库页就认定「没变」而不重画，
	/// 屏幕上留着改名前的歌名。把全部文件名一起揉成一个数就躲不过去了。
	/// </summary>
	public static int LibraryFingerprint()
	{
		unchecked
		{
			int h = 17;
			foreach (string f in KnownMusic)
			{
				h = h * 31 + f.GetHashCode();
			}
			return h * 31 + KnownMusic.Count;
		}
	}

	/// <summary>扫盘落地后把「外部改名」同步到所有存了路径的地方：
	/// 自定义顺序、上次播到哪首、当前正在播的那首、收藏和最近播放的歌名。</summary>
	private static void ApplyRenamesToSavedData()
	{
		bool changed = false;
		try
		{
			AppSettings s = SettingsManager.Instance;
			// 1) 自定义顺序：里面存的是完整路径，改名后那一条会被当成
			//    「已经不存在的歌」丢掉，整个排序跟着乱。
			List<string>? order = s.MusicOrder;
			if (order != null && order.Count > 0)
			{
				for (int i = 0; i < order.Count; i++)
				{
					string mapped = ResolveRenamedExisting(order[i]);
					if (mapped.Length > 0)
					{
						order[i] = mapped;
						changed = true;
					}
				}
			}
			// 2) 「上次播到哪首」
			string mappedPath = ResolveRenamedExisting(s.MusicPath);
			if (mappedPath.Length > 0)
			{
				s.MusicPath = mappedPath;
				changed = true;
			}
			// 3) 当前正在播的那首。只在没在播的时候改：
			//    正在播时读流还握着旧文件句柄，硬换路径会让播放器和显示对不上，
			//    而改名本身在 Windows 上也多半会被占用挡下来。
			if (CurrentPath != null && _reader == null)
			{
				string mapped = ResolveRenamedExisting(CurrentPath);
				if (mapped.Length > 0)
				{
					CurrentPath = mapped;
					changed = true;
				}
			}
			if (changed)
			{
				SettingsManager.Save(s);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步改名到播放设置失败");
		}
		// 4) 收藏 / 最近播放。它们自己管落盘和 Changed 广播，
		//    这里只管把歌名和路径改成现在的。
		try
		{
			UserLibrary.ReconcileLibrary();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步改名到收藏失败");
		}
	}

	private static void OnLibraryFileChanged(object sender, FileSystemEventArgs e)
	{
		try
		{
			if (AudioExtensions.Contains(Path.GetExtension(e.FullPath).ToLowerInvariant()))
			{
				ScheduleLibraryReload();
			}
		}
		catch (Exception ex)
		{
			// 文件系统事件本身就是「尽力而为」，但要让排查有据可查。
			LogHelper.LogDebug("音乐库文件事件处理失败: " + ex.Message);
		}
	}

	private static void ScheduleLibraryReload()
	{
		lock (ReloadLock)
		{
			if (_reloadDebounce == null)
			{
				_reloadDebounce = new Timer(delegate
				{
					FireLibraryReload();
				}, null, 1500, -1);
			}
			else
			{
				_reloadDebounce.Change(1500, -1);
			}
		}
	}

	private static void FireLibraryReload()
	{
		bool fire = false;
		lock (ReloadLock)
		{
			if (_reloadDebounce != null)
			{
				_reloadDebounce.Dispose();
				_reloadDebounce = null;
				fire = true;
			}
		}
		if (fire)
		{
			PostToUi(ReloadLibrary);
		}
	}

	public static void RefreshKnownMusic()
	{
		string key = BuildScanKey();
		if (string.Equals(key, _scanKey, StringComparison.Ordinal))
		{
			return;
		}
		_scanKey = key;
		List<string> found = ScanMusicFiles(MusicFolder);
		ReplaceKnownMusic(found);
	}

	/// <summary>
	/// 后台扫描音乐目录，再回到 UI 线程整表替换。
	/// 递归枚举在网络盘/大目录下可能耗时数秒，放 UI 线程会直接卡成"未响应"。
	/// 用 _scanGeneration 丢弃过期结果，避免连续触发时多次重建列表。
	/// </summary>
	public static void RefreshKnownMusicAsync()
	{
		string key = BuildScanKey();
		if (string.Equals(key, _scanKey, StringComparison.Ordinal))
		{
			return;
		}
		_scanKey = key;
		int gen = Interlocked.Increment(ref _scanGeneration);
		_scanRunning = true;
		Task.Run(() =>
		{
			List<string> found = ScanMusicFiles(MusicFolder);
			PostToUi(() =>
			{
				// 只有最新一轮的结果才落地，旧的直接丢掉
				if (gen == _scanGeneration)
				{
					ReplaceKnownMusic(found);
					NotifyStateChanged();
				}
				_scanRunning = false;
				FlushAfterScan();
			});
		});
	}

	private static readonly List<Action> _afterScan = new();
	private static bool _scanRunning;

	/// <summary>把一段依赖 KnownMusic 的逻辑排到当前这轮扫盘之后。
	/// 正在扫就排队等，没在扫说明列表已经是最新的，直接执行。</summary>
	public static void RunAfterScan(Action action)
	{
		if (!_scanRunning)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "扫盘后回调失败");
			}
			return;
		}
		lock (_afterScan)
		{
			_afterScan.Add(action);
		}
	}

	private static void FlushAfterScan()
	{
		Action[] pending;
		lock (_afterScan)
		{
			if (_afterScan.Count == 0)
			{
				return;
			}
			pending = _afterScan.ToArray();
			_afterScan.Clear();
		}
		foreach (Action a in pending)
		{
			try
			{
				a();
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "扫盘后回调失败");
			}
		}
	}

	private static void ReplaceKnownMusic(List<string> found)
	{
		KnownMusic.Clear();
		KnownMusic.AddRange(found);
		// 顺序要紧：先同步改名，再套自定义顺序。
		//
		// 反过来的话，ApplySavedOrder 拿着 MusicOrder 里的旧路径去曲库里找，
		// 一个都找不到，会把这些条目当成失效项丢掉并且立刻存回去 ——
		// 等 ApplyRenamesToSavedData 再想改写成新路径时，列表里已经没这一项了，
		// 用户自定义的排序顺序就此丢掉。所以先把路径改对，再按它排序。
		ApplyRenamesToSavedData();
		ApplySavedOrder();
	}

	private static List<string> ScanMusicFiles(string folder)
	{
		List<string> list = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			Directory.CreateDirectory(folder);
			foreach (string item in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				if (!AudioExtensions.Contains(Path.GetExtension(item).ToLowerInvariant()))
				{
					continue;
				}
				try
				{
					string fullPath = Path.GetFullPath(item);
					if (seen.Add(fullPath))
					{
						list.Add(fullPath);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return list;
	}

	private static void ApplySavedOrder()
	{
		List<string>? order = SettingsManager.Instance.MusicOrder;
		if (order == null || order.Count <= 0)
		{
			return;
		}
		HashSet<string> remaining = new HashSet<string>(KnownMusic, StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>(KnownMusic.Count);
		foreach (string item in order)
		{
			if (remaining.Remove(item))
			{
				list.Add(item);
			}
		}
		list.AddRange(remaining);
		KnownMusic.Clear();
		KnownMusic.AddRange(list);
	}

	public static void ReorderKnownMusic(IReadOnlyList<string> ordered)
	{
		HashSet<string> remaining = new HashSet<string>(KnownMusic, StringComparer.OrdinalIgnoreCase);
		KnownMusic.Clear();
		foreach (string item in ordered)
		{
			if (remaining.Remove(item))
			{
				KnownMusic.Add(item);
			}
		}
		KnownMusic.AddRange(remaining);
		AppSettings settings = SettingsManager.Instance;
		settings.MusicOrder = new List<string>(KnownMusic);
		SettingsManager.Save(settings);
	}

	/// <summary>点播方（各页面）在发起播放前登记的「我来自哪个列表」提示，
	/// PlayCore 真正把歌播起来之后把它落盘成 <see cref="AppSettings.LastPlayContext"/>，
	/// 供「启动时自动播放」恢复列表。只在内存里传一次，开播即清；没登记的入口
	/// 播完后按 <c>source</c> 兜底记页面（listKey 记空，恢复时静默跳过）。</summary>
	public static PlayContext? PendingPlayContext { get; set; }

	/// <summary>切歌成功（输出真的 Play 起来）时把播放上下文落盘。
	/// 挂点放在 PlayCore 成功路径上：失败路径（文件坏、URL 拿不到）不记录，
	/// 保证 LastPlayContext 永远指向「上一次真的播出来的那首歌」所在的列表。</summary>
	private static void RecordLastPlayContext(string? source)
	{
		try
		{
			PlayContext? hint = PendingPlayContext;
			PendingPlayContext = null;
			string page = hint?.Page ?? source switch
			{
				"library" => "library",
				"fav" => "fav",
				"recent" => "recent",
				"online" => "online",
				_ => ""
			};
			if (string.IsNullOrEmpty(page))
			{
				return;
			}
			AppSettings settings = SettingsManager.Instance;
			settings.LastPlayContext = new PlayContext
			{
				Page = page,
				ListKey = hint?.ListKey ?? ""
			};
			SettingsManager.Save(settings);
		}
		catch
		{
			// 上下文记录失败绝不影响播放本身。
		}
	}

	public static void Play(string path)
	{
		Play(path, "library");
	}

	/// <summary>播本地文件。<paramref name="source"/> 是来源页标记（library/fav/online），
	/// 给定位小球跳页用；从收藏页点播的本地文件传 "fav"，其余调用方走默认 "library"。</summary>
	public static void Play(string path, string? source)
	{
		_playToken++;
		PlayCore(path, null, null, -1, source);
	}

	private static void PlayCore(string path, SavedTrack? onlineMeta, string? onlineTitle, int onlineIndex, string? source)
	{
		string? text = NormalizeAudioPath(path);
		if (text == null)
		{
			return;
		}
		StopCore();
		lock (QueueLock)
		{
			_onlineIndex = onlineIndex;
		}
		_onlineMeta = onlineMeta;
		// 来源标记跟着曲目走：定位小球靠它决定跳音乐库/收藏/搜索/最近播放。
		_currentSource = source;
		if (onlineMeta == null)
		{
			// 本地文件开始播：在线会话到此为止，之后的手动切歌回本地曲库语义。
			// 在线歌（各平台）播本地缓存/导入文件时 meta 不为空，会话得以延续。
			ClearSessionQueue();
		}
		_onlineTitle = onlineTitle;
		NoMusicMode = false;
		// 起播一首新歌：之前的逻辑暂停位一律清掉，图标立刻回到「‖」。
		_userPaused = false;
		CurrentPath = text;
		AppSettings settings = SettingsManager.Instance;
		settings.MusicPath = text;
		settings.MusicPaused = false;
		SettingsManager.Save(settings);
		_endFired = false;
		// 起播前先让背景视频把 Source 挂上（同步、在 output.Play() 之前）：
		// 这样解码器在音频出声的同时就开始工作，画面不用再等一个加载周期。
		try
		{
			TrackStarting?.Invoke(text);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("起播前预载背景视频失败: " + ex.Message);
		}
		try
		{
			WaveStream reader = CreateReader(text);
			_reader = reader;
			ISampleProvider provider = BuildFxChain(reader.ToSampleProvider());
			VolumeSampleProvider volume = new VolumeSampleProvider(provider)
			{
				Volume = (float)Math.Clamp(settings.MusicVolume, 0.0, 1.0)
			};
			_vol = volume;
			WasapiOut output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);
			output.Init(volume.ToWaveProvider());
			_out = output;
			_watch = new Thread(() => WatchLoop(reader))
			{
				IsBackground = true,
				Name = "MusicBoxWatch"
			};
			_watch.Start();
			output.Play();
			RecordLastPlayContext(source);
			NotifyStateChanged();
			LogHelper.Log("播放音乐" + (settings.SurroundEnable ? "(模拟Hifi环绕)" : "") + ": " + text);
			if (string.IsNullOrWhiteSpace(_onlineTitle) && _onlineMeta == null)
			{
				try
				{
					TimeSpan total = reader.TotalTime;
					UserLibrary.NotePlay(new SavedTrack
					{
						Type = "local",
						Title = Path.GetFileName(text),
						Path = text,
						DurationMs = (long)total.TotalMilliseconds
					});
				}
				catch
				{
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放音乐失败: " + text);
			StopCore();
			NotifyStateChanged();
		}
	}

	/// <summary>播网易云在线曲目。<paramref name="source"/> 为来源页标记（定位小球用）：
	/// 搜索页点播默认 "online"；从收藏页点播传 "fav"。</summary>
	public static void PlayOnline(NeteaseTrack track, Action<string?>? onError = null, string? source = "online")
	{
		if (track == null || track.Id <= 0)
		{
			onError?.Invoke("无效的曲目");
			return;
		}
		_ = PlayOnlineAsync(track, onError, source);
	}

	private static async Task PlayOnlineAsync(NeteaseTrack track, Action<string?>? onError, string? source = "online")
	{
		int token = ++_playToken;
		try
		{
			OnFetchStateChanged(true, track.DisplayName);
			string? url = await NeteaseApiClient.GetSongUrlAsync(track.Id);
			if (token != _playToken)
			{
				return;
			}
			string? local = await NeteaseApiClient.DownloadSongAsync(track.Id, url);
			// 关键：下载期间用户可能已经改播别的歌，这时候绝不能再抢占。
			// 缓存文件留着，下次点同一首可以直接命中。
			if (token != _playToken)
			{
				LogHelper.Log("放弃过期播放请求: " + track.DisplayName);
				return;
			}
			OnFetchStateChanged(false, null);
			if (string.IsNullOrEmpty(local))
			{
				onError?.Invoke("获取播放地址失败（可能为 VIP/版权受限）");
				return;
			}
			var meta = new SavedTrack
			{
				Type = "netease",
				Id = track.Id.ToString(),
				Title = track.Name,
				Artist = track.ArtistNames,
				DurationMs = track.DurationMs,
				Cover = track.AlbumInfo?.PicUrl ?? ""
			};
			int idx;
			lock (QueueLock)
			{
				idx = _onlineQueue.FindIndex(t => t.Id == track.Id);
				_onlineIndex = idx;
			}
			PlayCore(local, meta, track.DisplayName, idx, source);
			NotifyStateChanged();
			try
			{
				UserLibrary.NotePlay(meta);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			OnFetchStateChanged(false, null);
			LogHelper.LogError(ex, "在线播放失败: " + track.DisplayName);
			// 过期请求的失败不该在正在播别的歌时弹错误
			if (token == _playToken)
			{
				onError?.Invoke("在线播放失败");
			}
		}
	}

	public static void SetOnlineQueue(IReadOnlyList<NeteaseTrack> tracks)
	{
		lock (QueueLock)
		{
			_onlineQueue = tracks != null ? new List<NeteaseTrack>(tracks) : new List<NeteaseTrack>();
			_onlineIndex = -1;
		}
	}

	public static void SetOnlineTitle(string? title)
	{
		_onlineTitle = title;
		NotifyStateChanged();
	}

	/// <summary>从在线源下载到本地后再播（QQ/酷狗/B站）：把在线元数据一并带过去。
	/// 之前这几种源只塞显示名，<see cref="_onlineMeta"/> 一直是 null，
	/// 导致 BuildCurrentTrack 拿不到 Cover/Artist，唱片盘面没图。
	/// 网易的 PlayOnlineAsync 本来就是这套带元数据的流程。
	/// <paramref name="source"/> 为来源页标记（定位小球用）：页面点播默认 "online"，
	/// 从收藏页点播传 "fav"。</summary>
	public static void PlayOnlineLocal(string local, SavedTrack? meta, string? displayTitle, string? source = "online")
	{
		if (string.IsNullOrWhiteSpace(local) || meta == null)
		{
			return;
		}
		PlayCore(local, meta, displayTitle, -1, source);
		NotifyStateChanged();
		try
		{
			UserLibrary.NotePlay(meta);
		}
		catch
		{
		}
	}

	public static string? CurrentDisplayTitle
	{
		get
		{
			if (!string.IsNullOrEmpty(_onlineTitle))
			{
				return _onlineTitle;
			}
			return CurrentPath != null ? Path.GetFileName(CurrentPath) : null;
		}
	}

	/// <summary>播放世代：每发起一次播放就 +1（本地、在线、收藏都算）。
	/// 建议列表的取流保护期用它判断「点的这首歌是不是已经起来了」——
	/// 重复播放同一首歌时 CurrentPath 不变，只比路径会误判成还没开始，
	/// 保护期就一直挂着。</summary>
	public static int PlayGeneration => _playToken;

	/// <summary>当前这首歌用于「查歌词」的信息。
	///
	/// 在线歌曲优先用 <see cref="_onlineMeta"/>（搜索结果里带来的平台 ID、
	/// 标准歌名、歌手、时长），只有拿不到元数据时才退回解析文件名。
	/// 这个顺序很关键：缓存文件名是纯数字 ID，对任何一个歌词源都没有意义。</summary>
	public static LyricMatchContext CurrentLyricContext
	{
		get
		{
			string? path = CurrentPath;
			if (string.IsNullOrEmpty(path))
			{
				return default;
			}
			SavedTrack? meta = _onlineMeta;
			if (meta != null && meta.Type != "local")
			{
				string title = (meta.Title ?? "").Trim();
				if (title.Length > 0)
				{
					string? artist = string.IsNullOrWhiteSpace(meta.Artist) ? null : meta.Artist.Trim();
					if (string.Equals(meta.Type, "bili", StringComparison.OrdinalIgnoreCase))
					{
						// B 站源的 Artist 是 UP 主名，拿他去搜会把匹配带偏；标题是
						// 视频标题，先挑出真正的歌名（书名号/括号/说明块）。
						title = TitleTools.CleanBiliTitle(title);
						artist = null;
					}
					return new LyricMatchContext(
						title,
						artist,
						meta.DurationMs > 0 ? meta.DurationMs : (long)Duration.TotalMilliseconds,
						meta.Type,
						meta.Id);
				}
			}
			return FromFileName(path);
		}
	}

	/// <summary>给歌词匹配用的候选（歌名, 歌手）列表，按可信度从高到低。
	///
	/// 为什么要多个候选：文件名千奇百怪，单靠一套「歌手 - 歌名」拆分经常
	/// 把标题里的说明块当成歌手，而网易云「歌手对不上就宁可不给」，于是一首
	/// 其实有歌词的歌被判成没歌词。这里**不往命中白名单里加条目**，而是按特征
	/// 生成一批候选，让 LyricFollowService 依次去试，命中即用。
	///
	/// 在线直连曲（netease/qq/kugou）已经有干净的元数据，只需要主候选；
	/// B 站（视频标题）和本地文件才需要展开变体。</summary>
	public static IReadOnlyList<LyricMatchContext> CurrentLyricCandidates
	{
		get
		{
			var list = new List<LyricMatchContext>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			void Add(LyricMatchContext c)
			{
				if (string.IsNullOrWhiteSpace(c.Title))
				{
					return;
				}
				string key = c.Title + "\u0001" + (c.Artist ?? "");
				if (seen.Add(key))
				{
					list.Add(c);
				}
			}

			LyricMatchContext primary = CurrentLyricContext;
			Add(primary);

			string? path = CurrentPath;
			if (string.IsNullOrEmpty(path))
			{
				return list;
			}

			SavedTrack? meta = _onlineMeta;
			bool fromOnline = meta != null && meta.Type != "local"
				&& !string.IsNullOrWhiteSpace(meta.Title);
			if (fromOnline)
			{
				// B 站标题是视频标题（CleanBiliTitle 主候选里已挑过一次），
				// 再补「去括号块 / 去噪音词」的更干净变体。
				if (string.Equals(meta!.Type, "bili", StringComparison.OrdinalIgnoreCase))
				{
					foreach (string t in TitleTools.CandidateTitles(meta.Title))
					{
						Add(new LyricMatchContext(t, null, primary.DurationMs, meta.Type, meta.Id));
					}
				}
				return list;
			}

			// 本地文件：从文件名枚举候选。
			long dur = primary.DurationMs;
			string stem = Path.GetFileNameWithoutExtension(path);
			foreach (string t in TitleTools.CandidateTitles(stem))
			{
				Add(new LyricMatchContext(t, null, dur));
				if (!string.IsNullOrWhiteSpace(primary.Artist)
					&& !string.Equals(t, primary.Title, StringComparison.Ordinal))
				{
					Add(new LyricMatchContext(t, primary.Artist, dur));
				}
			}
			// 繁简变体：跨区曲库命名不同，两个都试。
			foreach (LyricMatchContext c in list.ToArray())
			{
				string simp = ChineseConvert.ToSimplified(c.Title);
				if (!string.Equals(simp, c.Title, StringComparison.Ordinal))
				{
					Add(new LyricMatchContext(simp, c.Artist, c.DurationMs));
				}
				string trad = ChineseConvert.ToTraditional(c.Title);
				if (!string.Equals(trad, c.Title, StringComparison.Ordinal))
				{
					Add(new LyricMatchContext(trad, c.Artist, c.DurationMs));
				}
			}
			// 候选太多会拖长「每首都没歌词」时的等待：封顶。
			const int MaxCandidates = 8;
			return list.Count > MaxCandidates ? list.GetRange(0, MaxCandidates) : list;
		}
	}

	/// <summary>从「歌手 - 歌名」这类文件名里拆出查歌词要的信息。
	/// 没有分隔符就整段当歌名，歌手留空交给搜索端去猜。
	/// 平台下载缓存里命中的文件不按「歌手 - 歌名」拆：它们的文件名是平台标题
	/// （B 站是视频标题），直接挑歌名、歌手留空。</summary>
	private static LyricMatchContext FromFileName(string path)
	{
		string stem = Path.GetFileNameWithoutExtension(path);
		string title = stem;
		string? artist = null;
		if (IsKnownDownload(path) || TitleTools.LooksLikePlatformTitle(stem))
		{
			// 平台下载缓存命中的文件按平台标题处理；没进过缓存、但名字一看
			// 就是平台/视频标题（带书名号/方头括号/噪音词）的也走同一条，
			// 不再只认白名单——用户反馈「有些匹配不到歌词的文件其实能匹配」。
			title = TitleTools.CleanBiliTitle(stem);
		}
		else
		{
			int sep = stem.IndexOf(" - ", StringComparison.Ordinal);
			if (sep > 0)
			{
				string a = stem.Substring(0, sep).Trim();
				string b = stem.Substring(sep + 3).Trim();
				if (a.Length > 0 && b.Length > 0)
				{
					artist = a;
					title = b;
				}
			}
			title = StripParenthetical(title);
			if (artist != null)
			{
				artist = StripParenthetical(artist);
			}
		}
		if (title.Length == 0)
		{
			return default;
		}
		return new LyricMatchContext(title, artist, (long)Duration.TotalMilliseconds);
	}

	/// <summary>去掉歌名/歌手里的括号备注。「(中文填词)」「【高音质】」这类尾巴
	/// 留着去搜会把匹配带偏，统一切掉。</summary>
	private static string StripParenthetical(string s)
	{
		s = s.Trim();
		int cut = s.IndexOfAny(new[] { '(', '（', '[', '【' });
		if (cut > 0)
		{
			s = s.Substring(0, cut).Trim();
		}
		return s;
	}

	public static void PlayFavorite(SavedTrack fav, Action<string?>? onError = null)
	{
		if (fav == null || string.IsNullOrWhiteSpace(fav.Title))
		{
			onError?.Invoke("无效的收藏");
			return;
		}
		switch (fav.Type)
		{
			case "netease":
			{
				long id = long.TryParse(fav.Id, out long parsed) ? parsed : 0;
				if (id <= 0)
				{
					onError?.Invoke("无效的收藏");
					return;
				}
				var track = new NeteaseTrack
				{
					Id = id,
					Name = fav.Title,
					DurationMsRaw = fav.DurationMs,
					Artists = string.IsNullOrEmpty(fav.Artist)
						? new List<NeteaseArtist>()
						: new List<NeteaseArtist> { new NeteaseArtist { Name = fav.Artist } },
					Album = string.IsNullOrEmpty(fav.Cover) ? null : new NeteaseAlbum { Name = "", PicUrl = fav.Cover }
				};
				PlayOnline(track, onError, "fav");
				break;
			}
			case "bili":
				_ = PlayFavoriteBiliAsync(fav, onError);
				break;
			case "kugou":
				_ = PlayFavoriteKugouAsync(fav, onError);
				break;
			case "qq":
				_ = PlayFavoriteQqAsync(fav, onError);
				break;
			default:
				Play(fav.Path, "fav");
				break;
		}
	}

	private static async Task PlayFavoriteBiliAsync(SavedTrack fav, Action<string?>? onError)
	{
		int token = ++_playToken;
		try
		{
			var track = new BiliTrack
			{
				Bvid = fav.Id,
				Title = fav.Title,
				Author = fav.Artist,
				Pic = fav.Cover
			};
			// 复用已下载到音乐库的同名文件，不重复从 B 站下载
			string? local = GetCachedDownload(fav.Title);
			if (string.IsNullOrEmpty(local))
			{
				OnFetchStateChanged(true, fav.Title);
				local = await BiliClient.DownloadAudioAsync(track, null);
				if (token != _playToken)
				{
					OnFetchStateChanged(false, null);
					return;
				}
				if (string.IsNullOrEmpty(local))
				{
					OnFetchStateChanged(false, null);
					onError?.Invoke("B站获取失败（可能触发验证码，稍后再试）");
					return;
				}
				local = ImportToLibrary(local, fav.Title);
				RememberDownload(fav.Title, local);
			}
			OnFetchStateChanged(false, null);
			_onlineIndex = -1;
			var biliMeta = new SavedTrack
			{
				Type = "bili",
				Id = fav.Id,
				Title = fav.Title,
				Artist = fav.Artist,
				DurationMs = fav.DurationMs,
				Cover = fav.Cover,
				Path = local
			};
			PlayCore(local, biliMeta, fav.Title, -1, "fav");
			NotifyStateChanged();
			try
			{
				UserLibrary.NotePlay(biliMeta);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			OnFetchStateChanged(false, null);
			LogHelper.LogError(ex, "收藏B站播放失败: " + fav.Title);
			if (token == _playToken)
			{
				onError?.Invoke("收藏B站播放失败");
			}
		}
	}

	private static async Task PlayFavoriteKugouAsync(SavedTrack fav, Action<string?>? onError)
	{
		int token = ++_playToken;
		try
		{
			var track = new KugouTrack
			{
				Hash = fav.Id,
				FileName = fav.Title,
				SingerName = string.IsNullOrEmpty(fav.Artist) ? "" : fav.Artist
			};
			OnFetchStateChanged(true, fav.Title);
			string? local = await KugouClient.DownloadSongAsync(track);
			if (token != _playToken)
			{
				OnFetchStateChanged(false, null);
				return;
			}
			OnFetchStateChanged(false, null);
			if (string.IsNullOrEmpty(local))
			{
				onError?.Invoke("酷狗获取失败（可能为 VIP/版权受限）");
				return;
			}
			string imported = ImportToLibrary(local, fav.Title);
			var kugouMeta = new SavedTrack
			{
				Type = "kugou",
				Id = fav.Id,
				Title = fav.Title,
				Artist = fav.Artist,
				DurationMs = fav.DurationMs,
				Cover = fav.Cover,
				Path = imported
			};
			_onlineIndex = -1;
			PlayCore(imported, kugouMeta, fav.Title, -1, "fav");
			NotifyStateChanged();
			try
			{
				UserLibrary.NotePlay(kugouMeta);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			OnFetchStateChanged(false, null);
			LogHelper.LogError(ex, "收藏酷狗播放失败: " + fav.Title);
			if (token == _playToken)
			{
				onError?.Invoke("收藏酷狗播放失败");
			}
		}
	}

	private static async Task PlayFavoriteQqAsync(SavedTrack fav, Action<string?>? onError)
	{
		int token = ++_playToken;
		try
		{
			OnFetchStateChanged(true, fav.Title);
			string? local = await QqClient.DownloadSongAsync(new QqTrack
			{
				Mid = fav.Id,
				Name = fav.Title,
				Singer = string.IsNullOrEmpty(fav.Artist) ? "" : fav.Artist
			});
			if (token != _playToken)
			{
				OnFetchStateChanged(false, null);
				return;
			}
			OnFetchStateChanged(false, null);
			if (string.IsNullOrEmpty(local))
			{
				onError?.Invoke("QQ获取失败（可能为 VIP 权限受限）；可尝试改用 B 站音源");
				return;
			}
			string imported = ImportToLibrary(local, fav.Title);
			RememberDownload(fav.Title, imported);
			var qqMeta = new SavedTrack
			{
				Type = "qq",
				Id = fav.Id,
				Title = fav.Title,
				Artist = fav.Artist,
				DurationMs = fav.DurationMs,
				Cover = fav.Cover,
				Path = imported
			};
			_onlineIndex = -1;
			PlayCore(imported, qqMeta, fav.Title, -1, "fav");
			NotifyStateChanged();
			try
			{
				UserLibrary.NotePlay(qqMeta);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			OnFetchStateChanged(false, null);
			LogHelper.LogError(ex, "收藏QQ播放失败: " + fav.Title);
			if (token == _playToken)
			{
				onError?.Invoke("收藏QQ播放失败");
			}
		}
	}

	/// <summary>把在线下载的缓存文件复制进音乐库的「B站下载」文件夹。传 <paramref name="displayName"/>
	/// 时用曲名/视频标题命名文件（而非缓存里的数字/哈希名）。</summary>
	public static string ImportToLibrary(string file, string? displayName = null)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
			{
				return file;
			}
			string full = Path.GetFullPath(file);
			string folder = MusicFolder;
			if (IsUnder(folder, full))
			{
				return full;
			}
			string dir = Path.Combine(folder, "B站下载");
			Directory.CreateDirectory(dir);
			string ext = Path.GetExtension(full);
			string baseName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(full) : SanitizeFileName(displayName);
			if (string.IsNullOrWhiteSpace(baseName))
			{
				baseName = Path.GetFileNameWithoutExtension(full);
			}
			string dest = Path.Combine(dir, baseName + ext);
			int n = 2;
			while (File.Exists(dest))
			{
				dest = Path.Combine(dir, baseName + "_" + (n++) + ext);
			}
			File.Copy(full, dest);
			InvalidateScanCache();
			RefreshKnownMusicAsync();
			NotifyStateChanged();
			return dest;
		}
		catch
		{
			return file;
		}
	}

	private static bool IsUnder(string root, string path)
	{
		try
		{
			string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
			return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>把标题/名称清洗成可安全用作文件名的字符串：去掉非法字符、控制字符，截断并去除首尾点。</summary>
	private static string SanitizeFileName(string name)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return "";
			}
			string invalid = new string(Path.GetInvalidFileNameChars()) + "\\/:*?\"<>|";
			var sb = new StringBuilder(name.Length);
			foreach (char c in name)
			{
				if (c < 32 || invalid.IndexOf(c) >= 0)
				{
					continue;
				}
				sb.Append(c);
			}
			string s = sb.ToString().Trim().Trim('.');
			const int maxLen = 120;
			if (s.Length > maxLen)
			{
				s = s.Substring(0, maxLen).Trim().Trim('.');
			}
			return s;
		}
		catch
		{
			return name;
		}
	}

	private static void MigrateLegacyBbCache()
	{
		string cache = BiliClient.CacheDir;
		if (!Directory.Exists(cache))
		{
			return;
		}
		string dir = Path.Combine(MusicFolder, "B站下载");
		try
		{
			Directory.CreateDirectory(dir);
		}
		catch
		{
			return;
		}
		bool moved = false;
		foreach (string f in Directory.EnumerateFiles(cache, "*.*", SearchOption.AllDirectories))
		{
			if (!AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
			{
				continue;
			}
			try
			{
				string dest = Path.Combine(dir, Path.GetFileName(f));
				if (File.Exists(dest))
				{
					continue;
				}
				File.Move(f, dest);
				moved = true;
			}
			catch
			{
			}
		}
		if (moved)
		{
			InvalidateScanCache();
			RefreshKnownMusic();
			NotifyStateChanged();
		}
	}

	private static ISampleProvider BuildFxChain(ISampleProvider source)
	{
		ISampleProvider provider = source;
		FxSettings fx = SettingsManager.Instance.Fx ?? new FxSettings();
		if (fx.EqEnabled && fx.EqGains != null)
		{
			bool any = false;
			foreach (float g in fx.EqGains)
			{
				if (Math.Abs(g) > 0.05f)
				{
					any = true;
					break;
				}
			}
			if (any)
			{
				provider = new EqualizerProvider(provider, fx.EqGains);
			}
		}
		if (fx.BassBoostEnabled && fx.BassBoostGain > 0f)
		{
			provider = new BassBoostProvider(provider, fx.BassBoostGain);
		}
		if (fx.ExciterEnabled && fx.ExciterAmount > 0f)
		{
			provider = new ExciterProvider(provider, fx.ExciterAmount);
		}
		if (fx.WidthEnabled && Math.Abs(fx.WidthAmount - 1f) > 0.01f)
		{
			provider = new StereoWidthProvider(provider, fx.WidthAmount);
		}
		if (fx.ReverbEnabled)
		{
			provider = new ReverbProvider(provider, fx.ReverbRoomSize, fx.ReverbWetLevel);
		}
		if (fx.CompressorEnabled)
		{
			provider = new CompressorProvider(provider, fx.CompressorThreshold, fx.CompressorRatio);
		}
		AppSettings settings = SettingsManager.Instance;
		if (settings.SurroundEnable)
		{
			if (provider.WaveFormat.Channels != 2)
			{
				provider = new AutoStereoSampleProvider(provider);
			}
			provider = new SurroundEffectSampleProvider(provider,
				(float)Math.Clamp(settings.SurroundStrength, 0.0, 1.0),
				NormalizeSurroundMode(settings.SurroundMode));
		}
		// 链末留头部空间：降低叠加后的峰值，使限制器几乎不介入（避免瞬态咔嚓/爆鸣/泵动）
		provider = new VolumeSampleProvider(provider) { Volume = 0.92f };
		// 链末峰值限制器：任何 EQ/低音/环绕增益叠加都不会在 0dBFS 硬削波（防炸麦）
		provider = new LimiterProvider(provider);
		return provider;
	}

	/// <summary>配置文件里存的是枚举的数字。旧版本没有这个字段时反序列化成 0，
	/// 正好就是 SamePhase；但如果以后往枚举中间插值，旧配置就会指到别的算法上，
	/// 所以这里显式夹到合法范围，非法值一律回落到默认。</summary>
	private static SurroundMode NormalizeSurroundMode(SurroundMode mode)
	{
		return mode switch
		{
			SurroundMode.SamePhase => SurroundMode.SamePhase,
			SurroundMode.HaasRear => SurroundMode.HaasRear,
			SurroundMode.Echo => SurroundMode.Echo,
			SurroundMode.Combined => SurroundMode.Combined,
			// 合并版是新加的末位值，显式列出而不是靠默认值兜：
			// 以后再往枚举中间插值时，这一行至少不会被误改。
			SurroundMode.HifiMerged => SurroundMode.HifiMerged,
			_ => SurroundMode.SamePhase
		};
	}

	public static void ApplyFxRestart()
	{
		if (CurrentPath == null)
		{
			NotifyStateChanged();
			return;
		}
		bool wasPlaying = IsPlaying;
		int onlineIdx;
		lock (QueueLock)
		{
			onlineIdx = _onlineIndex;
		}
		string? onlineTitle = _onlineTitle;
		SavedTrack? onlineMeta = _onlineMeta;
		WaveStream? reader = _reader;
		if (reader == null)
		{
			// 异常态下没有底层 reader → 走旧式完整重启（重读文件 + 续播进度）
			string path = CurrentPath;
			double pos = Position.TotalSeconds;
			StopCore();
			NoMusicMode = false;
			try
			{
				PlayCore(path, onlineMeta, onlineTitle, onlineIdx, _currentSource);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "重建音频链后重新起播失败");
				NotifyStateChanged();
				return;
			}
			if (pos > 0)
			{
				Seek(pos);
			}
			if (!wasPlaying)
			{
				// 内部「重启后立即静音」：必须 immediate，不能淡出——
				// 这条 Pause 不是用户按的暂停，是重建链路后的收尾动作，
				// 淡出只会让本该无声的间隙漏一截衰减中的声音。
				PauseImmediate();
			}
			else
			{
				NotifyStateChanged();
			}
			return;
		}

		// 无缝重建：保留同一个 reader（播放位置纹丝不动），只重建 FX 链 + 重启输出。
		// 省掉从磁盘重读与 Seek，切换停顿只剩输出设备重初始化那一点，几乎不卡顿。
		_endFired = true;
		IWavePlayer? output = _out;
		_out = null;
		Thread? thread = _watch;
		_watch = null;
		_vol = null;
		try
		{
			output?.Stop();
			output?.Dispose();
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("重建音频链时释放旧输出设备失败: " + ex.Message);
		}
		if (thread != null && Thread.CurrentThread != thread)
		{
			try
			{
				thread.Join(300);
			}
			catch
			{
			}
		}
		NoMusicMode = false;
		_onlineIndex = onlineIdx;
		_onlineTitle = onlineTitle;
		try
		{
			ISampleProvider provider = BuildFxChain(reader.ToSampleProvider());
			VolumeSampleProvider volume = new VolumeSampleProvider(provider)
			{
				Volume = (float)Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0)
			};
			_vol = volume;
			WasapiOut newOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);
			newOutput.Init(volume.ToWaveProvider());
			_out = newOutput;
			_endFired = false;
			_watch = new Thread(() => WatchLoop(reader))
			{
				IsBackground = true,
				Name = "MusicBoxWatch"
			};
			_watch.Start();
			newOutput.Play();
			if (!wasPlaying)
			{
				// 同上：重建后立即静音走 immediate，不做淡出。
				PauseImmediate();
			}
			else
			{
				NotifyStateChanged();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "无缝重建FX链失败，回退到完整重启");
			IWavePlayer? out2 = _out;
			_out = null;
			Thread? th2 = _watch;
			_watch = null;
			try
			{
				out2?.Stop();
				out2?.Dispose();
			}
			catch
			{
			}
			if (th2 != null && Thread.CurrentThread != th2)
			{
				try
				{
					th2.Join(200);
				}
				catch
				{
				}
			}
			try
			{
				reader.Dispose();
			}
			catch
			{
			}
			_reader = null;
			ApplyFxRestart();
		}
	}

	/// <summary>按后缀挑解码器。对外开是因为列表要显示时长时也得开一次文件
	/// 读总长，走同一条路才能保证「列表里的时长」和「真正播的时长」一致。</summary>
	internal static WaveStream CreateReader(string path)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext == ".wav" || ext == ".mp3" || ext == ".aiff" || ext == ".aif")
		{
			return new AudioFileReader(path);
		}
		return new MediaFoundationReader(path);
	}

	/// <summary>位置连续多少个 250ms 采样没动才认定「真的卡死」。
	/// 原来取 6（约 1.5 秒）：4K/大文件解码抖动、系统忙、临时切输出设备时
	/// reader.Position 本来就可能一两秒不更新，那样会被误判成播完而跳歌。
	/// 6 秒足够覆盖这类短时顿挫，真卡死时用户也只是多等几秒。</summary>
	private const int StallTicksBeforeAdvance = 24;

	private static void WatchLoop(WaveStream reader)
	{
		try
		{
			long lastPos = -1L;
			int stall = 0;
			while (reader != null && !_endFired && ReferenceEquals(_watch, Thread.CurrentThread))
			{
				Thread.Sleep(250);
				if (reader == null || _endFired || !ReferenceEquals(_watch, Thread.CurrentThread))
				{
					break;
				}
			long pos;
			long length;
			long avgBytes;
			try
			{
				pos = reader.Position;
				length = reader.Length;
				avgBytes = reader.WaveFormat.AverageBytesPerSecond;
			}
			catch
			{
				// 换曲路径已经把 reader 释放了：直接退出，别刷「线程异常」错误日志。
				break;
			}

			// 曲末判定必须放在输出状态判定「之前」。
			// WasapiOut 读完最后一个缓冲会自行 Stop（PlaybackState=Stopped），
			// 项目没有订阅 PlaybackStopped，自动切歌完全靠这里；原来的顺序会在
			// Stopped 分支 continue 掉，永远走不到 pos>=Length，而且 stall 被
			// 清零，连「位置停滞」兜底也救不回来 —— 表现为「播完停住不动」。
			if (pos >= length)
			{
				FireAdvance();
				return;
			}

			IWavePlayer? output = _out;
			// 只有输出设备明确处于 Playing 时才统计停滞。暂停状态下位置
			// 不动是正常现象，不该被当成卡死推进下一首。
			if (output == null || output.PlaybackState != PlaybackState.Playing)
			{
				// 设备自行停在曲末（最后一个缓冲没写满、pos 差一点点到 Length）
				// 时仍算播完。容差取 0.5 秒的字节数，只在「已停在末尾附近」生效；
				// 中途的设备异常或用户暂停不会被误判成结束。
				long tailBytes = Math.Max(1L, avgBytes / 2);
				if (output != null && output.PlaybackState == PlaybackState.Stopped
					&& length - pos <= tailBytes)
				{
					FireAdvance();
					return;
				}
				stall = 0;
				lastPos = pos;
				continue;
			}
			// 快到曲末时把会话里下一首提前下进缓存（只下载，不碰播放状态）。
			// 放在曲末判定之后：本首要结束就别再为它启动了。
			MaybePrefetchNext(reader);
			if (pos == lastPos)
			{
				if (++stall >= StallTicksBeforeAdvance)
				{
					LogHelper.Log($"播放监视：位置停滞约 {StallTicksBeforeAdvance * 250 / 1000} 秒，按卡死推进下一首");
					FireAdvance();
					return;
				}
			}
			else
			{
				stall = 0;
			}
			lastPos = pos;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "播放监视线程异常");
		}
	}

	private static void FireAdvance()
	{
		_endFired = true;
		// 判「还是不是同一首」而不是「还是不是同一个监视线程」：
		// 音效无缝重建（ApplyFxRestart）会换掉 _watch 但沿用同一个 reader，
		// 用线程引用判所有权会把这次曲末结束误吞掉，之后 _endFired 一直为
		// true、监视线程也已 return，就永久停在曲末不切歌。换曲会换 reader，
		// 仍能正确忽略过期事件。
		WaveStream? endedReader = _reader;
		PostToUi(() =>
		{
			if (endedReader == null || !ReferenceEquals(endedReader, _reader))
			{
				LogHelper.Log("播放结束上报: 曲目已变更，忽略");
				return;
			}
			FinishAndAdvance();
		});
	}

	private static void FinishAndAdvance()
	{
		StopCore();
		AdvanceToNext();
	}

	private static void StopCore()
	{
		_endFired = true;
		// 换曲/停止：取消还没跑完的预取，并允许新曲重新触发一次。
		ResetPrefetch();
		WaveStream? reader = _reader;
		_reader = null;
		IWavePlayer? output = _out;
		_out = null;
		_vol = null;
		Thread? thread = _watch;
		_watch = null;
		try
		{
			reader?.Dispose();
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("释放音频读取器失败: " + ex.Message);
		}
		try
		{
			output?.Stop();
			output?.Dispose();
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("释放音频输出设备失败: " + ex.Message);
		}
		if (thread != null && Thread.CurrentThread != thread)
		{
			try
			{
				thread.Join(300);
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("等待播放监视线程退出失败: " + ex.Message);
			}
		}
	}

	// —— 暂停/恢复音量包络（淡出/淡入）——
	// 直接 Pause 的坏处：声音「啪」一下断掉，听感像设备掉电；
	// 直接 Resume 的坏处：满音量瞬间炸出来。250ms 线性包络够柔，
	// 又不至于慢到让人以为点了没反应。
	private const int FadeMs = 250;

	/// <summary>当前正在跑的包络任务。新包络启动时把旧的取消，
	/// 保证任意时刻最多一条补间在写 _vol.Volume（否则淡出被淡入覆盖、
	/// 两条补间互相拽，音量会抖）。</summary>
	private static CancellationTokenSource? _fadeCts;

	/// <summary>停掉正在跑的包络。取消即交权：接管方（新包络/音量滑条）
	/// 自己负责把音量写成它想要的值，被取消的一方不再碰 _vol。</summary>
	private static void CancelFade()
	{
		CancellationTokenSource? cts = Interlocked.Exchange(ref _fadeCts, null);
		cts?.Cancel();
		// 不在这里 Dispose：被取消的任务可能正拿着 Token，用完由它自己释放。
	}

	/// <summary>在 durationMs 内把 _vol.Volume 从 from 线性推到 to。
	/// 后台线程跑补间，不占 UI 线程；结束时回调 onDone（被取消则不回调）。</summary>
	private static void StartFade(float from, float to, int durationMs, Action? onDone = null)
	{
		VolumeSampleProvider? vol = _vol;
		if (vol == null || durationMs <= 0)
		{
			onDone?.Invoke();
			return;
		}
		CancelFade();
		CancellationTokenSource cts = new();
		_fadeCts = cts;
		CancellationToken token = cts.Token;
		long start = Environment.TickCount64;
		_ = Task.Run(async () =>
		{
			bool finished = false;
			try
			{
				while (true)
				{
					token.ThrowIfCancellationRequested();
					double t = (Environment.TickCount64 - start) / (double)durationMs;
					if (t >= 1.0)
					{
						break;
					}
					// 目标音量固定取调用时刻的快照（to）。这里刻意不实时读设置：
					// 用户拖音量条走的是 SetVolume（会取消本包络并直接写值），
					// 两条路各管各的，不会互相覆盖。
					vol.Volume = from + (to - from) * (float)t;
					await Task.Delay(10, token).ConfigureAwait(false);
				}
				vol.Volume = to;
				finished = true;
			}
			catch (OperationCanceledException)
			{
				// 被新包络/暂停/音量调节接管：现状由接管方负责，这里什么都不写。
			}
			finally
			{
				if (ReferenceEquals(_fadeCts, cts))
				{
					_fadeCts = null;
				}
				cts.Dispose();
			}
			if (finished)
			{
				onDone?.Invoke();
			}
		});
	}

	public static void Pause()
	{
		IWavePlayer? output = _out;
		VolumeSampleProvider? vol = _vol;
		// 用户主动暂停：逻辑状态立刻置位，UI 不必陪 250ms 淡出等。
		// 以前只在这条路的末尾 NotifyStateChanged（那时底层还在 Playing），
		// 淡出结束真正 Pause 后却不再通知 —— 播放/暂停按钮就一直停在「‖」。
		_userPaused = true;
		if (output == null || vol == null || vol.Volume <= 0.001f)
		{
			PauseImmediate();
			return;
		}
		// 状态位与 UI 立刻切到「已暂停」：淡出只是耳朵里的余音，
		// 逻辑状态不该陪它等 250ms。
		if (!SettingsManager.Instance.MusicPaused)
		{
			AppSettings settings = SettingsManager.Instance;
			settings.MusicPaused = true;
			SettingsManager.Save(settings);
		}
		NotifyStateChanged();
		float from = vol.Volume;
		// 回调里只压自己这一次启动时抓住的输出实例：淡出期间用户切歌
		// 会整链重建，_out 已经不是它了，绝不能去 Pause 新链路。
		StartFade(from, 0f, FadeMs, () =>
		{
			if (ReferenceEquals(_out, output))
			{
				output.Pause();
				// 底层真的停了，再广播一次：SMTC、托盘、歌词窗都跟着落到暂停态。
				NotifyStateChanged();
			}
		});
	}

	/// <summary>立即暂停，不做淡出。
	/// 仅供内部「重建音频链后马上静音」这类场景（ApplyFxRestart）：
	/// 那里的输出刚重建完，此时再来 250ms 淡出只会让本该无声的间隙
	/// 漏出一截衰减中的声音。</summary>
	public static void PauseImmediate()
	{
		CancelFade();
		IWavePlayer? output = _out;
		if (output != null)
		{
			output.Pause();
		}
		// 无论用户按的还是内部重建后的静音，输出最终都是 Paused，
		// 逻辑位跟着置 true，UI 图标才不会停在「‖」。
		_userPaused = true;
		if (!SettingsManager.Instance.MusicPaused)
		{
			AppSettings settings = SettingsManager.Instance;
			settings.MusicPaused = true;
			SettingsManager.Save(settings);
		}
		NotifyStateChanged();
	}

	private static void ClearPausedFlag()
	{
		if (SettingsManager.Instance.MusicPaused)
		{
			AppSettings settings = SettingsManager.Instance;
			settings.MusicPaused = false;
			SettingsManager.Save(settings);
		}
	}

	public static void Resume()
	{
		// 用户主动恢复：逻辑暂停位先清，UI 立刻切回「‖」，不等底层 Play 生效。
		_userPaused = false;
		ClearPausedFlag();
		IWavePlayer? output = _out;
		if (output != null)
		{
			VolumeSampleProvider? vol = _vol;
			// 暂停（或被打断的淡出）把音量压在了设定值以下：先 Play 起来
			// 再从当前音量淡入到设定值。先 Play 是关键——淡入期间就得
			// 有声，等淡完再 Play 反而多静 250ms。
			if (vol != null && vol.Volume < Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0) - 0.001f)
			{
				output.Play();
				NotifyStateChanged();
				StartFade(vol.Volume, (float)Math.Clamp(SettingsManager.Instance.MusicVolume, 0.0, 1.0), FadeMs);
				return;
			}
			output.Play();
			NotifyStateChanged();
			return;
		}
		if (NoMusicMode)
		{
			NoMusicMode = false;
			string? target = ResolveStartTarget();
			if (target != null)
			{
				Play(target);
			}
			return;
		}
		if (CurrentPath != null)
		{
			Play(CurrentPath);
		}
		else
		{
			RefreshKnownMusic();
			var list = KnownMusic.Where(f => !IsSkipped(f)).ToList();
			if (list.Count > 0)
			{
				Play(list[Random.Shared.Next(list.Count)]);
			}
			else
			{
				NotifyStateChanged();
			}
		}
	}

	/// <summary>没有播放目标时从音乐库随机选一首。返回是否成功开始播放。</summary>
	public static bool RandomPlay()
	{
		RefreshKnownMusic();
		var list = KnownMusic.Where(f => !IsSkipped(f)).ToList();
		if (list.Count == 0)
		{
			return false;
		}
		string pick = list[Random.Shared.Next(list.Count)];
		Play(pick);
		return true;
	}

	/// <summary>根据当前播放内容构成本地收藏条目（供快捷键收藏用）。</summary>
	public static SavedTrack? BuildCurrentTrack()
	{
		if (NoMusicMode || CurrentPath == null)
		{
			return null;
		}
		if (_onlineMeta != null)
		{
			return _onlineMeta;
		}
		string title = string.IsNullOrWhiteSpace(_onlineTitle)
			? Path.GetFileNameWithoutExtension(CurrentPath)
			: _onlineTitle;
		var track = new SavedTrack
		{
			Type = "local",
			Title = title,
			Path = CurrentPath
		};
		// 歌词匹配成功后补上的网易云封面/歌名/歌手，优先于文件名。
		if (LocalMatchedMeta.TryGetValue(CurrentPath, out SavedTrack? matched))
		{
			if (!string.IsNullOrWhiteSpace(matched.Cover))
			{
				track.Cover = matched.Cover;
			}
			if (!string.IsNullOrWhiteSpace(matched.Title))
			{
				track.Title = matched.Title;
			}
			if (!string.IsNullOrWhiteSpace(matched.Artist))
			{
				track.Artist = matched.Artist;
			}
		}
		return track;
	}

	/// <summary>歌词匹配到平台后，把那一版的封面（以及更准的歌名/歌手）
	/// 记到本地文件上，沉浸页的音乐图标就会换成专辑封面。
	/// 封面没变就不重复通知，避免一次播放里反复触发界面刷新。
	/// <paramref name="source"/> 记下封面来自哪个平台，底栏的双色框用它。</summary>
	public static void ApplyLocalMatchedMeta(string path, string? title, string? artist, string? cover,
		MatchSource source = MatchSource.Netease)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(cover))
		{
			return;
		}
		// 来源先记：哪怕封面和上次一样（早 return 的那条路）也要记，
		// 否则「同一张封面、但这次是从 QQ 拿到的」会被漏掉。
		NoteMatchSource(path, cover: source);
		if (LocalMatchedMeta.TryGetValue(path, out SavedTrack? old)
			&& string.Equals(old.Cover, cover, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		LocalMatchedMeta[path] = new SavedTrack
		{
			Type = "local",
			Path = path,
			Title = title ?? "",
			Artist = artist ?? "",
			Cover = cover
		};
		LocalCoverVersion++;
		if (LocalMatchedMeta.Count > 500)
		{
			foreach (string key in LocalMatchedMeta.Keys.ToArray())
			{
				if (LocalMatchedMeta.Count <= 500)
				{
					break;
				}
				LocalMatchedMeta.TryRemove(key, out _);
			}
		}
		// 只有正在播的这首才需要刷新界面；后台匹配到的别惊动 UI。
		if (string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
		{
			NotifyStateChanged();
		}
	}

	/// <summary>取一首本地文件匹配到的网易云封面；没匹配过返回空串。
	/// 曲库/收藏这些行手里只有路径，用它补封面。</summary>
	public static string GetLocalMatchedCover(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		return LocalMatchedMeta.TryGetValue(path, out SavedTrack? matched)
			&& !string.IsNullOrWhiteSpace(matched.Cover)
			? matched.Cover
			: "";
	}

	/// <summary>取一首本地文件匹配到平台后补上的歌手名；没匹配过返回空串。
	///
	/// 和封面同源：歌词匹配成功时那一版的歌手会一并记下来，
	/// 曲库 / 最近播放 / 我的收藏的行手上只有路径，靠它把作者那一栏填上。
	/// 填不上就留空，让行按「只有时长」或「只有歌名」的排版自己收着，
	/// 别拿文件名去顶替——那就是用户看到的「歌名显示两遍」。</summary>
	public static string GetLocalMatchedArtist(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		return LocalMatchedMeta.TryGetValue(path!, out SavedTrack? matched)
			&& !string.IsNullOrWhiteSpace(matched.Artist)
			? matched.Artist!
			: "";
	}

	/// <summary>取一首本地文件匹配到平台后补上的**更准**的歌名；没匹配过返回空串。
	/// 曲库行原本直接用文件名（带一堆下载后缀），有匹配结果时优先显示它。</summary>
	public static string GetLocalMatchedTitle(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "";
		}
		return LocalMatchedMeta.TryGetValue(path!, out SavedTrack? matched)
			&& !string.IsNullOrWhiteSpace(matched.Title)
			? matched.Title!
			: "";
	}

	public static void Stop()
	{
		StopCore();
		// 手动停止 = 会话结束：之后的手动下一首不该再走进先前的在线列表。
		ClearSessionQueue();
		_userPaused = false;
		CurrentPath = null;
		NoMusicMode = false;
		NotifyStateChanged();
	}

	public static void EnterNoMusicMode()
	{
		StopCore();
		_userPaused = false;
		NoMusicMode = true;
		CurrentPath = null;
		LogHelper.Log("不播放任何音乐");
		NotifyStateChanged();
	}

	private static string? NormalizeAudioPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		try
		{
			string fullPath = Path.GetFullPath(path);
			if (!File.Exists(fullPath) || !AudioExtensions.Contains(Path.GetExtension(fullPath).ToLowerInvariant()))
			{
				return null;
			}
			return fullPath;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>自然播放结束后的下一首怎么挑。
	/// once       = 顺序播放，播到最后一首就停
	/// sequential = 列表循环，播完绕回第一首
	/// loop       = 单曲循环，一直重播当前这首
	/// random     = 随机播放（其他值一律当随机）</summary>
/// <summary>当前这首在候选列表里的下标；不在列表里返回 -1。
	///
	/// 先按完整路径找，找不到再按文件名找。
	/// 只认路径是不够的：同一首歌从搜索结果点进来、从「最近播放」点进来、
	/// 或者是子文件夹里的歌，曲库里的路径字符串未必和 <see cref="CurrentPath"/>
	/// 一个字不差，但文件名是一样的。</summary>
	private static int IndexOfCurrent(List<string> list)
	{
		string? cur = CurrentPath;
		if (string.IsNullOrWhiteSpace(cur))
		{
			return -1;
		}
		for (int i = 0; i < list.Count; i++)
		{
			if (string.Equals(list[i], cur, StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
		}
		string name = Path.GetFileName(cur);
		if (name.Length > 0)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (string.Equals(Path.GetFileName(list[i]), name, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}
		}
		return -1;
	}

	/// <summary>重播当前这首，并保留它的在线曲目信息。返回 false 表示重播不了。
	///
	/// 这里不能直接调 <see cref="Play"/>：Play 会把 _onlineMeta / _onlineTitle
	/// 清成 null，网易云的歌重播一次就会在「最近播放」里退化成一条本地文件记录。
	/// 也不重复 NotePlay：单曲循环每三分钟一次，照记的话最近播放会被同一首刷屏。</summary>
	private static bool ReplayCurrent()
	{
		try
		{
			string? cur = CurrentPath;
			if (!string.IsNullOrWhiteSpace(cur) && File.Exists(cur))
			{
				int onlineIndex;
				lock (QueueLock)
				{
					onlineIndex = _onlineIndex;
				}
				LogHelper.Log("单曲循环：重播 " + Path.GetFileNameWithoutExtension(cur));
				PlayCore(cur, _onlineMeta, _onlineTitle, onlineIndex, _currentSource);
				return true;
			}
			// 缓存文件已经被清了，但还知道它是队列里的第几首 —— 重新取一次地址。
			NeteaseTrack? queued = null;
			lock (QueueLock)
			{
				if (_onlineQueue.Count > 0 && _onlineIndex >= 0 && _onlineIndex < _onlineQueue.Count)
				{
					queued = _onlineQueue[_onlineIndex];
				}
			}
			if (queued != null)
			{
				LogHelper.Log("单曲循环：缓存已清理，重新拉取 " + queued.DisplayName);
				PlayOnline(queued);
				return true;
			}
			return false;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "单曲循环重播失败");
			return false;
		}
	}

	/// <summary>顺着列表走完，所有���进都只看当前这首在不在列表里。</summary>
	private static void AdvanceToNext()
	{
		if (NoMusicMode)
		{
			LogHelper.Log("单曲循环：处于无音乐模式，不自动切歌");
			return;
		}
		string mode0 = NormalizeMode(SettingsManager.Instance.PlaybackMode);
		// 在线会话还活跃时（非单曲循环），自然播完也顺会话推进 ——
		// 不然收藏夹里听完一首就掉回本地曲库，会话语义就断了。
		// 放在曲库检查之前：用户可能一首本地歌都没有，全靠在线会话在播。
		//
		// 顺序播放（once）以前被排除在会话推进之外：会话里的在线歌不在
		// 本地曲库列表中，IndexOfCurrent 返回 -1，once 分支把它当成
		// 「已经是最后一首」直接 Stop —— 表现为顺序播放在线歌单只播一首。
		// 这里改成：once 也顺会话推进，但到会话最后一首时按语义停止、
		// 不绕回开头。其余模式（列表循环/随机）维持原来的绕回行为。
		if (mode0 == "once" && HasActiveSession())
		{
			if (SessionAtLast())
			{
				LogHelper.Log($"顺序播放：会话《{CurrentDisplayTitle ?? Path.GetFileNameWithoutExtension(CurrentPath ?? "")}》已是最后一首，播放结束");
				Stop();
				return;
			}
			if (StepSessionQueue(1))
			{
				return;
			}
		}
		else if (mode0 != "loop" && StepSessionQueue(1))
		{
			return;
		}
		RefreshKnownMusic();
		if (KnownMusic.Count == 0)
		{
			LogHelper.Log("单曲循环：曲库为空，停止");
			Stop();
			return;
		}
		List<string> list = KnownMusic.Where(f => !IsSkipped(f)).ToList();
		if (list.Count == 0)
		{
			LogHelper.Log("单曲循环：全部被跳过，停止");
			Stop();
			return;
		}
		int current = IndexOfCurrent(list);
		string mode = mode0;
		string ended = CurrentDisplayTitle ?? Path.GetFileNameWithoutExtension(CurrentPath ?? "");
		int index;

		if (mode == "loop")
		{
			// 单曲循环的答案就是「当前这首」，哪怕它压根不在曲库列表里。
			//
			// 以前这里写的是 mode == "loop" && current >= 0：当前这首不在列表里时
			// （current == -1）条件不成立，会一路掉到下面的 current < 0 分支，
			// 直接 index = 0 —— 单曲循环就变成了「每次播完都跳回曲库第一首」。
			// 网易云/QQ 的在线歌是下载到缓存目录播的，不在曲库里，必然踩中；
			// 从子文件夹直接播放的本地歌、以及被「跳过」过滤掉的歌也会踩中。
			if (ReplayCurrent())
			{
				return;
			}
			// 连当前这首都重播不了（路径没了、也没队列信息），退而求其次
			// 放列表第一首，总比什么都不放强。
			index = current >= 0 ? current : 0;
		}
		else if (mode == "once")
		{
			// 顺序播放：到最后一首就停，不再绕回去
			if (current < 0 || current >= list.Count - 1)
			{
				LogHelper.Log($"顺序播放：{ended} 是最后一首，播放结束");
				Stop();
				return;
			}
			index = current + 1;
		}
		else
		{
			if (current < 0)
			{
				index = 0;
			}
			else if (mode == "random" && list.Count > 1)
			{
				int next;
				do
				{
					next = Random.Shared.Next(list.Count);
				}
				while (next == current);
				index = next;
			}
			else
			{
				// 列表循环：next 绕回开头
				index = (current + 1) % list.Count;
			}
		}

		LogHelper.Log($"自动切歌：模式={mode} 《{ended}》-> 《{Path.GetFileNameWithoutExtension(list[index])}》"
			+ $"（{index + 1}/{list.Count}）");
		Play(list[index]);
	}

	/// <summary>把播放模式收敛成四种，空值/历史遗留值一律当随机。</summary>
	private static string NormalizeMode(string? mode) => mode switch
	{
		"once" or "sequential" or "loop" or "random" => mode,
		_ => "random"
	};

	/// <summary>对外暴露的规范化播放模式（主窗口切模式按钮要用）。</summary>
	public static string NormalizePlaybackMode() => NormalizeMode(SettingsManager.Instance.PlaybackMode);

	/// <summary>手动下一首。始终按列表顺序走，不受播放模式影响（随机模式下用户主动点
	/// 下一首应该听到的是队列里那一首，而不是又随机一次）。</summary>
	public static void PlayNext()
	{
		if (NoMusicMode)
		{
			Resume();
			return;
		}
		// 层级 1：活动中的在线会话（在线搜索结果 / 歌单 / 音乐库收藏夹点播的列表）
		// 还在播，下一首必须推进会话队列。会话不优先的话：QQ/酷狗/B 站的歌
		// 会被 ImportToLibrary 收进曲库 → IndexOfCurrent 命中本地列表 →
		// 直接播了本地下一首；网易云缓存目录不在曲库里 → StepOnlineQueue
		// 又落空 → 掉到 Play(list[0])。两条路都是「热键切到了本地音乐」。
		if (StepSessionQueue(1))
		{
			return;
		}
		RefreshKnownMusic();
		List<string> list = KnownMusic.Where(f => !IsSkipped(f)).ToList();
		if (list.Count == 0)
		{
			LogHelper.Log("手动下一首：没有可用曲目");
			return;
		}
int current = IndexOfCurrent(list);
		// 层级 2：网易云搜索页登记的队列（当前这首不在曲库里，但队列位置还在）。
		if (current < 0 && StepOnlineQueue(1))
		{
			return;
		}
		int index = current >= 0 ? (current + 1) % list.Count : 0;
		LogHelper.Log($"手动切歌：{current + 1}/{list.Count} -> {index + 1}/{list.Count}");
		Play(list[index]);
	}

	/// <summary>在线队列里前后挪一格。返回 false 表示当前不在队列里。</summary>
	private static bool StepOnlineQueue(int delta)
	{
		NeteaseTrack t;
		int next;
		int n;
		lock (QueueLock)
		{
			if (_onlineQueue.Count == 0 || _onlineIndex < 0 || _onlineIndex >= _onlineQueue.Count)
			{
				return false;
			}
			n = _onlineQueue.Count;
			next = ((_onlineIndex + delta) % n + n) % n;
			t = _onlineQueue[next];
		}
		LogHelper.Log("在线队列：" + (next + 1) + "/" + n + " " + t.DisplayName);
		PlayOnline(t);
		return true;
	}

	/// <summary>手动上一首，同样按列表顺序回退。</summary>
	public static void PlayPrevious()
	{
		if (NoMusicMode)
		{
			Resume();
			return;
		}
		// 层级 1：在线会话还在播就先走会话（和 PlayNext 的层级判定同构，
		// 不然「上一首」会把在线会话穿帮回本地曲库）。
		if (StepSessionQueue(-1))
		{
			return;
		}
		RefreshKnownMusic();
		List<string> list = KnownMusic.Where(f => !IsSkipped(f)).ToList();
		if (list.Count == 0)
		{
			LogHelper.Log("手动上一首：没有可用曲目");
			return;
		}
		int current = IndexOfCurrent(list);
		if (current < 0 && StepOnlineQueue(-1))
		{
			return;
		}
		int index = current < 0 ? list.Count - 1 : (current - 1 + list.Count) % list.Count;
		LogHelper.Log($"手动上一首：{(current + 1).ToString()}/{list.Count} -> {index + 1}/{list.Count}");
		Play(list[index]);
	}

	public static bool IsSkipped(string path)
	{
		List<string>? skipped = SettingsManager.Instance.SkippedMusic;
		if (skipped == null || skipped.Count == 0)
		{
			return false;
		}
		return skipped.Contains(path, StringComparer.OrdinalIgnoreCase);
	}

	public static void ToggleSkip(string path)
	{
		if (path == NoMusicMarker)
		{
			return;
		}
		string? text = NormalizeAudioPath(path);
		if (text != null)
		{
			AppSettings settings = SettingsManager.Instance;
			HashSet<string> set = new HashSet<string>(settings.SkippedMusic ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
			bool nowSkipped;
			if (set.Contains(text))
			{
				set.Remove(text);
				nowSkipped = false;
			}
			else
			{
				set.Add(text);
				nowSkipped = true;
			}
			settings.SkippedMusic = new List<string>(set);
			SettingsManager.Save(settings);
			LogHelper.Log((nowSkipped ? "标记跳过" : "取消跳过") + ": " + text);
			if (nowSkipped && CurrentPath != null && string.Equals(CurrentPath, text, StringComparison.OrdinalIgnoreCase))
			{
				LogHelper.Log("当前歌曲被标记跳过，自动切换下一首");
				AdvanceToNext();
			}
			else
			{
				NotifyStateChanged();
			}
		}
	}

	/// <summary>
	/// 广播播放状态变了。
	///
	/// 这里统一做「切回 UI 线程 + 合并同一轮里的重复通知」。
	/// 之前是直接 Invoke：NAudio 的回调和后台扫描都从工作线程打过来，
	/// 每个订阅者（主窗、音乐库、音效页、歌词服务）各自再 BeginInvoke 一次，
	/// 一次状态变化就变成 4 个调度项，而且主窗那个还挂在 Background 优先级上、
	/// 排在渲染之后——表现就是切歌时界面先卡一下、进度条再跳。
	///
	/// 合并是安全的：订阅者读的是 MusicService 上的当前状态，不是事件参数，
	/// 所以一轮里通知几次结果一样，只通知一次反而更省。
	/// 已经在 UI 线程上时仍然同步发，避免打断「改完状态马上看界面」的调用方。
	/// </summary>
	public static void NotifyStateChanged()
	{
		// 这里用全名而不是加 using System.Windows / System.Windows.Threading：
		// 这个工程里 MusicBox.Views 是故意避开 MusicBox.Windows 命名的
		//（后者会遮蔽 Services/SmtcService.cs 里的 Windows.Media.Playback）。
		// 在文件头引入 System.Windows 等于把那个雷重新埋一遍。
		System.Windows.Threading.Dispatcher? d = UiThread;
		if (d == null)
		{
			// 还没有 UI 线程（启动早期或单元测试），直接发。
			StateChanged?.Invoke();
			return;
		}
		if (d.CheckAccess())
		{
			StateChanged?.Invoke();
			return;
		}
		if (Interlocked.Exchange(ref _stateNotifyQueued, 1) == 1)
		{
			// 已经排了一个在等着了，这次直接并进去。
			return;
		}
		try
		{
			d.BeginInvoke(new Action(() =>
			{
				Interlocked.Exchange(ref _stateNotifyQueued, 0);
				StateChanged?.Invoke();
			}), System.Windows.Threading.DispatcherPriority.Background);
		}
		catch (Exception)
		{
			// 程序正在退出时 Dispatcher 已经被 BeginInvokeShutdown 了，这时再排队会抛。
			// 这是退出路径上的最后一件事，通知发不出去也无所谓，但不能因此崩在关窗那一刻。
			Interlocked.Exchange(ref _stateNotifyQueued, 0);
		}
	}

	private static int _stateNotifyQueued;

	/// <summary>UI 线程的 Dispatcher。缓存下来，省得每次摸 Application.Current。</summary>
	private static System.Windows.Threading.Dispatcher? UiThread =>
		_uiThread ??= System.Windows.Application.Current?.Dispatcher;

	private static System.Windows.Threading.Dispatcher? _uiThread;

	public static void ApplySurroundToggle()
	{
		ApplyFxRestart();
	}

	public static void RestartSurround()
	{
		ApplyFxRestart();
	}

	public static void SetVolume(double volume)
	{
		SetVolumeLive(volume);
		SettingsManager.Save(SettingsManager.Instance);
	}

	/// <summary>只改音量、不落盘。给滑条拖动用：一次拖动会经过上百个像素点，
	/// 每点都序列化一遍 settings.json（含最近播放列表）会明显拖慢 UI，
	/// 松手时再落一次就够了。热键/键盘等一次性变更仍走 <see cref="SetVolume"/>。</summary>
	public static void SetVolumeLive(double volume)
	{
		double value = Math.Clamp(volume, 0.0, 1.0);
		AppSettings settings = SettingsManager.Instance;
		settings.MusicVolume = value;
		// 用户拖音量条是「最终权威」：立刻停掉正在跑的淡入/淡出，
		// 不然补间会按旧目标继续写 _vol.Volume，把滑条刚设的值又拽回去。
		CancelFade();
		if (_vol != null)
		{
			_vol.Volume = (float)value;
		}
	}

	/// <summary>跳转到指定秒数播放（进度条点击定位）。返回是否真的生效。</summary>
	public static bool Seek(double seconds)
	{
		WaveStream? reader = _reader;
		if (reader == null) return false;
		try
		{
			double duration = reader.TotalTime.TotalSeconds;
			if (duration <= 0) return false;
			double t = Math.Max(0.0, Math.Min(seconds, duration - 0.05));
			reader.CurrentTime = TimeSpan.FromSeconds(t);
			// 挪完解码位置后把输出缓冲里残留的旧位置音频冲掉：
			// WasapiOut 以 200ms 延迟缓冲工作，只改 reader 位置的话，
			// 缓冲里还压着约 200ms 旧位置的音频，点完会先补一小段旧位置
			// 的声音再切过去（NAudio 社区的标准做法：重定位后 Stop+Play
			// 强制冲缓冲）。参考：
			// https://exchangetuts.com/c-naudio-changing-audio-playback-position-still-plays-a-small-buffer-of-old-position-1641305284840204
			IWavePlayer? output = _out;
			if (output != null && output.PlaybackState == PlaybackState.Playing)
			{
				try
				{
					output.Stop();
					output.Play();
				}
				catch
				{
					// 冲缓冲失败不影响定位本身（位置已经改了），别让耳机里的
					// 一声杂音升级成整首歌中断。
				}
			}
			NotifyStateChanged();
			return true;
		}
		catch (Exception ex)
		{
			// 以前这里是空 catch，Seek 静默失败，UI 侧就表现为"点了没反应又弹回去"
			LogHelper.LogError(ex, $"Seek 到 {seconds:0.00}s 失败");
			return false;
		}
	}

	// ---------- 下载到音乐库的缓存（按规范化歌名复用文件，不重复下载） ----------

	public static string? GetCachedDownload(string title)
	{
		try
		{
			var s = SettingsManager.Instance;
			if (s.DownloadCache == null || string.IsNullOrWhiteSpace(title)) return null;
			string k = NormalizeDlKey(title);
			if (k.Length == 0) return null;
			if (s.DownloadCache.TryGetValue(k, out string? p) && !string.IsNullOrEmpty(p) && File.Exists(p))
				return p;
		}
		catch
		{
		}
		return null;
	}

	/// <summary>该文件是不是之前从平台下载到音乐库缓存过的（B 站/QQ/酷狗）。
	/// 命中说明文件名的格式是平台标题（B 站是视频标题）而非「歌手 - 歌名」，
	/// 歌词匹配那边要先挑歌名再搜。</summary>
	public static bool IsKnownDownload(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return false;
			}
			var s = SettingsManager.Instance;
			if (s.DownloadCache == null || s.DownloadCache.Count == 0)
			{
				return false;
			}
			string full = Path.GetFullPath(path);
			return s.DownloadCache.Values.Any(v => !string.IsNullOrEmpty(v)
				&& string.Equals(Path.GetFullPath(v), full, StringComparison.OrdinalIgnoreCase));
		}
		catch
		{
			return false;
		}
	}

	public static void RememberDownload(string title, string localPath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
				return;
			var s = SettingsManager.Instance;
			s.DownloadCache ??= new Dictionary<string, string>();
			string k = NormalizeDlKey(title);
			if (k.Length == 0) return;
			s.DownloadCache[k] = Path.GetFullPath(localPath);
			if (s.DownloadCache.Count > 300)
			{
				var drop = s.DownloadCache.OrderBy(kv => kv.Value.Length).Take(100).Select(kv => kv.Key).ToList();
				foreach (string dk in drop) s.DownloadCache.Remove(dk);
			}
			SettingsManager.Save(s);
		}
		catch
		{
		}
	}

	// ---------- 重命名本地文件 / 平台下载文件 ----------

	/// <summary>把文件名里的非法字符去掉，压掉首尾空白和点号。返回 "" 表示无效。</summary>
	public static string CleanFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "";
		}
		var sb = new StringBuilder(name.Trim().TrimEnd('.').Trim());
		foreach (char c in sb.ToString())
		{
			if (Path.GetInvalidFileNameChars().Contains(c))
			{
				return "";
			}
		}
		int max = 180;
		string v = sb.ToString();
		if (v.Length > max)
		{
			v = v.Substring(0, max).TrimEnd();
		}
		return v;
	}

	/// <summary>重命名音乐库里的本地文件：改文件、同步下载缓存路径、若正在播放
	/// 则把 CurrentPath 指到新路径，最后触发一次库重扫。成功返回新路径，
	/// 名字非法 / 目标已存在 / 移动失败返回 null。</summary>
	public static string? RenameFile(string oldPath, string newName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newName) || !File.Exists(oldPath))
			{
				return null;
			}
			string clean = CleanFileName(newName);
			if (clean.Length == 0)
			{
				return null;
			}
			string dir = Path.GetDirectoryName(oldPath) ?? "";
			if (dir.Length == 0)
			{
				return null;
			}
			string fullOld = Path.GetFullPath(oldPath);
			string fullNew = Path.GetFullPath(Path.Combine(dir, clean + Path.GetExtension(oldPath)));
			if (string.Equals(fullOld, fullNew, StringComparison.OrdinalIgnoreCase))
			{
				return fullOld;
			}
			if (File.Exists(fullNew))
			{
				return null;
			}
			File.Move(fullOld, fullNew);
			RemapDownloadCachePath(fullOld, fullNew, clean);
			if (string.Equals(CurrentPath, fullOld, StringComparison.OrdinalIgnoreCase))
			{
				CurrentPath = fullNew;
			}
			ReloadLibrary();
			return fullNew;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>重命名「平台下载」的曲目：按旧标题在下载缓存里找文件，改名后
	/// 让旧标题还指向新文件（收藏里的旧名字还能播），并把新标题也登记进去。
	/// 旧标题没有对应本地文件（还没下载过）返回 null。</summary>
	public static string? RenameDownloaded(string oldTitle, string newName)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(oldTitle) || string.IsNullOrWhiteSpace(newName))
			{
				return null;
			}
			string? path = GetCachedDownload(oldTitle);
			if (string.IsNullOrEmpty(path))
			{
				return null;
			}
			string clean = CleanFileName(newName);
			if (clean.Length == 0)
			{
				return null;
			}
			string dir = Path.GetDirectoryName(path) ?? "";
			string fullNew = Path.GetFullPath(Path.Combine(dir, clean + Path.GetExtension(path)));
			string fullOld = Path.GetFullPath(path);
			if (!string.Equals(fullOld, fullNew, StringComparison.OrdinalIgnoreCase) && File.Exists(fullNew))
			{
				return null;
			}
			if (!string.Equals(fullOld, fullNew, StringComparison.OrdinalIgnoreCase))
			{
				File.Move(fullOld, fullNew);
			}
			RemapDownloadCachePath(fullOld, fullNew, clean);
			if (string.Equals(CurrentPath, fullOld, StringComparison.OrdinalIgnoreCase))
			{
				CurrentPath = fullNew;
			}
			ReloadLibrary();
			return fullNew;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "重命名下载文件失败: " + oldTitle);
			return null;
		}
	}

	/// <summary>把下载缓存里指到旧文件路径的条目改成新路径；新标题也登记一条
	/// （旧标题保留，收藏里的旧名字在新同步完成前仍能播）。</summary>
	private static void RemapDownloadCachePath(string oldPath, string newPath, string newName)
	{
		try
		{
			var s = SettingsManager.Instance;
			if (s.DownloadCache == null)
			{
				return;
			}
			bool changed = false;
			foreach (KeyValuePair<string, string> kv in s.DownloadCache.ToList())
			{
				if (!string.IsNullOrEmpty(kv.Value)
					&& string.Equals(Path.GetFullPath(kv.Value), oldPath, StringComparison.OrdinalIgnoreCase))
				{
					s.DownloadCache[kv.Key] = newPath;
					changed = true;
				}
			}
			string kNew = NormalizeDlKey(newName);
			if (kNew.Length > 0)
			{
				s.DownloadCache[kNew] = newPath;
				changed = true;
			}
			if (changed)
			{
				SettingsManager.Save(s);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "同步下载缓存路径失败");
		}
	}

	private static string NormalizeDlKey(string title)
	{
		var sb = new StringBuilder(title.Length);
		foreach (char c in title.ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(c)) sb.Append(c);
		}
		return sb.ToString();
	}

	public static void OnAppExit()
	{
		LogHelper.Log("程序退出，清理播放管线");
		StopCore();
		// 退出时把长期驻留的 OS 资源显式释放：监视器占用目录句柄，
		// 防抖 Timer 持有回调，二者都不随进程内静态字段自然回收。
		lock (ReloadLock)
		{
			_reloadDebounce?.Dispose();
			_reloadDebounce = null;
		}
		try
		{
			_libraryWatcher?.Dispose();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "释放音乐库监视器失败");
		}
		_libraryWatcher = null;
		LogHelper.Flush();
	}

	private static void PostToUi(Action action)
	{
		SynchronizationContext? context = _ui;
		if (context != null && SynchronizationContext.Current != context)
		{
			context.Post(_ => action(), null);
		}
		else
		{
			action();
		}
	}
}