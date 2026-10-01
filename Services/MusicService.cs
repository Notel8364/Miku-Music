using System;
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
	private static readonly string[] AudioExtensions = new string[7] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma" };

	private static readonly Random Random = new Random();

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

	private static int _onlineIndex = -1;

	private static string? _onlineTitle;

	private static SavedTrack? _onlineMeta;

	/// <summary>播放请求代号。每次发起播放 +1；所有 await 回来后先比对，
	/// 对不上说明用户已经改播别的歌，本次结果直接丢弃。
	/// 没有它就会出「随进度强覆盖」：先点的 A（VIP 要等下载）后完成，
	/// 会把后点的、已经在播的 B 顶掉。</summary>
	private static int _playToken;

	public const string NoMusicMarker = "<no-music>";

	public static List<string> KnownMusic { get; } = new List<string>();

	public static string? CurrentPath { get; private set; }

	public static bool NoMusicMode { get; private set; }

	public static bool IsPlaying
	{
		get
		{
			IWavePlayer? output = _out;
			return output != null && output.PlaybackState == PlaybackState.Playing;
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
			else if (!NoMusicMode && SettingsManager.Instance.AutoPlayOnStartup && !SettingsManager.Instance.MusicPaused)
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
		catch
		{
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
		watcher.Renamed += OnLibraryFileChanged;
		watcher.Deleted += OnLibraryFileChanged;
		watcher.Error += OnLibraryWatcherError;
		watcher.EnableRaisingEvents = true;
		return watcher;
	}

	private static void OnLibraryWatcherError(object sender, ErrorEventArgs e)
	{
		LogHelper.Log("音乐库监视器错误: " + (e.GetException()?.Message ?? "未知"));
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
		catch
		{
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

	public static void Play(string path)
	{
		_playToken++;
		PlayCore(path, null, null, -1);
	}

	private static void PlayCore(string path, SavedTrack? onlineMeta, string? onlineTitle, int onlineIndex)
	{
		string? text = NormalizeAudioPath(path);
		if (text == null)
		{
			return;
		}
		StopCore();
		_onlineIndex = onlineIndex;
		_onlineMeta = onlineMeta;
		_onlineTitle = onlineTitle;
		NoMusicMode = false;
		CurrentPath = text;
		AppSettings settings = SettingsManager.Instance;
		settings.MusicPath = text;
		settings.MusicPaused = false;
		SettingsManager.Save(settings);
		_endFired = false;
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

	public static void PlayOnline(NeteaseTrack track, Action<string?>? onError = null)
	{
		if (track == null || track.Id <= 0)
		{
			onError?.Invoke("无效的曲目");
			return;
		}
		_ = PlayOnlineAsync(track, onError);
	}

	private static async Task PlayOnlineAsync(NeteaseTrack track, Action<string?>? onError)
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
				Cover = track.Album?.PicUrl ?? ""
			};
			int idx = _onlineQueue.FindIndex(t => t.Id == track.Id);
			_onlineIndex = idx;
			PlayCore(local, meta, track.DisplayName, idx);
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
		_onlineQueue = tracks != null ? new List<NeteaseTrack>(tracks) : new List<NeteaseTrack>();
		_onlineIndex = -1;
	}

	public static void SetOnlineTitle(string? title)
	{
		_onlineTitle = title;
		NotifyStateChanged();
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

	/// <summary>从「歌手 - 歌名」这类文件名里拆出查歌词要的信息。
	/// 没有分隔符就整段当歌名，歌手留空交给搜索端去猜。</summary>
	private static LyricMatchContext FromFileName(string path)
	{
		string stem = Path.GetFileNameWithoutExtension(path);
		string title = stem;
		string? artist = null;
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
					DurationMs = fav.DurationMs,
					Artists = string.IsNullOrEmpty(fav.Artist)
						? new List<NeteaseArtist>()
						: new List<NeteaseArtist> { new NeteaseArtist { Name = fav.Artist } },
					Album = string.IsNullOrEmpty(fav.Cover) ? null : new NeteaseAlbum { Name = "", PicUrl = fav.Cover }
				};
				PlayOnline(track, onError);
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
				Play(fav.Path);
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
			PlayCore(local, biliMeta, fav.Title, -1);
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
			PlayCore(imported, kugouMeta, fav.Title, -1);
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
			PlayCore(imported, qqMeta, fav.Title, -1);
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
		int onlineIdx = _onlineIndex;
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
				PlayCore(path, onlineMeta, onlineTitle, onlineIdx);
			}
			catch
			{
				NotifyStateChanged();
				return;
			}
			if (pos > 0)
			{
				Seek(pos);
			}
			if (!wasPlaying)
			{
				Pause();
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
		catch
		{
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
				Pause();
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

	private static WaveStream CreateReader(string path)
	{
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext == ".wav" || ext == ".mp3" || ext == ".aiff" || ext == ".aif")
		{
			return new AudioFileReader(path);
		}
		return new MediaFoundationReader(path);
	}

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
				IWavePlayer? output = _out;
				if (output != null && output.PlaybackState == PlaybackState.Paused)
				{
					stall = 0;
					lastPos = reader.Position;
					continue;
				}
				long pos = reader.Position;
				if (pos >= reader.Length)
				{
					FireAdvance();
					return;
				}
				if (pos == lastPos)
				{
					if (++stall >= 6)
					{
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
		Thread owner = Thread.CurrentThread;
		PostToUi(() =>
		{
			if (!ReferenceEquals(owner, _watch))
			{
				LogHelper.Log("播放结束上报: 所有权已变更，忽略");
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
		catch
		{
		}
		try
		{
			output?.Stop();
			output?.Dispose();
		}
		catch
		{
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
	}

	public static void Pause()
	{
		IWavePlayer? output = _out;
		if (output != null)
		{
			output.Pause();
		}
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
		ClearPausedFlag();
		IWavePlayer? output = _out;
		if (output != null)
		{
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
				Play(list[Random.Next(list.Count)]);
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
		string pick = list[Random.Next(list.Count)];
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
		return new SavedTrack
		{
			Type = "local",
			Title = title,
			Path = CurrentPath
		};
	}

	public static void Stop()
	{
		StopCore();
		CurrentPath = null;
		NoMusicMode = false;
		NotifyStateChanged();
	}

	public static void EnterNoMusicMode()
	{
		StopCore();
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
				LogHelper.Log("单曲循环：重播 " + Path.GetFileNameWithoutExtension(cur));
				PlayCore(cur, _onlineMeta, _onlineTitle, _onlineIndex);
				return true;
			}
			// 缓存文件已经被清了，但还知道它是队列里的第几首 —— 重新取一次地址。
			if (_onlineQueue.Count > 0 && _onlineIndex >= 0 && _onlineIndex < _onlineQueue.Count)
			{
				NeteaseTrack t = _onlineQueue[_onlineIndex];
				LogHelper.Log("单曲循环：缓存已清理，重新拉取 " + t.DisplayName);
				PlayOnline(t);
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
		string mode = NormalizeMode(SettingsManager.Instance.PlaybackMode);
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
		else if (current < 0)
		{
			index = 0;
		}
		else if (mode == "random" && list.Count > 1)
		{
			int next;
			do
			{
				next = Random.Next(list.Count);
			}
			while (next == current);
			index = next;
		}
		else
		{
			// 列表循环：next 绕回开头
			index = (current + 1) % list.Count;
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
		RefreshKnownMusic();
		List<string> list = KnownMusic.Where(f => !IsSkipped(f)).ToList();
		if (list.Count == 0)
		{
			LogHelper.Log("手动下一首：没有可用曲目");
			return;
		}
int current = IndexOfCurrent(list);
		// 当前这首不在曲库里，但正放着在线队列里的某一首：
		// 那就顺着队列走，别跳去曲库第一首。在线的歌本来就不在曲库里，
		// 硬按列表算等于每次「下一首」都回到 8D Sorry。
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
		if (_onlineQueue.Count == 0 || _onlineIndex < 0 || _onlineIndex >= _onlineQueue.Count)
		{
			return false;
		}
		int n = _onlineQueue.Count;
		int next = ((_onlineIndex + delta) % n + n) % n;
		NeteaseTrack t = _onlineQueue[next];
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
		double value = Math.Clamp(volume, 0.0, 1.0);
		AppSettings settings = SettingsManager.Instance;
		settings.MusicVolume = value;
		SettingsManager.Save(settings);
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