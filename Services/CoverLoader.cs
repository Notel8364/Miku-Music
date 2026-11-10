using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>
/// 搜索结果封面加载：按图片来源设置 Referer，异步下载；
/// 两级缓存——内存(压缩原始字节) + 磁盘持久化，同一 URL 只走一次网络。
///
/// 为什么这样设计（对比旧版）：
/// 1) 旧版只在内存里放「解码后的 BitmapImage」，进程一关就没了，
///    下次开程序 / 缓存被挤掉又得重新向 CDN 发请求。
///    现在多一层磁盘缓存（%LocalAppData%\MikuMusic\cover_cache），
///    重启、反复点同一首歌都不会再请求网络，直接从本地读。
/// 2) 旧版内存里常驻的是解码后的位图（128px 也要 ~64KB 且带 WPF 开销）。
///    现在内存里只留「压缩后的原始图片字节」（JPEG/PNG 自身已压缩，
///    通常几 KB~十几 KB），显示时再按目标宽度解码。
///    几百张封面只占用几 MB，而不是几十 MB——这就是内存压缩的关键。
/// 3) 解码后的位图另开一个小 LRU（最近显示过的 N 张），
///    来回滚动时不至于反复解码同一张导致闪烁 / 吃 CPU。
///
/// LRU 实现注意（2024 修闪屏时定下的规矩）：
/// - 「最近使用」以**命中/写入时刻**为准（TicksLastUse 每次命中都续期），
///   不再沿用旧版 ConcurrentQueue 的「先进先出队列」——那个队列同一 key
///   会堆出大量重复条目，队首弹出来的常常是刚用过的 key，等于随机逐出；
/// - 逐出时跳过**当前可见行**的条目（PinDecoded 登记）。正在显示的封面
///   虽然被 Image 元素自己引用着不会消失，但一旦被逐出，行重建时要重新
///   解码、重新走一遍加载管线，来回滚动的大列表就会一直重复这套开销。
/// </summary>
public static class CoverLoader
{
	private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

	// 内存字节缓存：存「压缩后的原始图片字节」，不解码。
	// 这是内存占用的大头压缩——字节比解码位图小一个数量级。
	private const int MaxMemoryBytes = 512;
	private static readonly ConcurrentDictionary<string, byte[]> BytesCache = new();
	private static readonly ConcurrentDictionary<string, long> BytesLastUse = new();

	// 解码后的位图小缓存：最近显示过的 N 张直接复用，避免重复解码。
	private const int MaxDecoded = 192;
	private static readonly ConcurrentDictionary<string, ImageSource> DecodedCache = new();
	private static readonly ConcurrentDictionary<string, long> DecodedLastUse = new();

	// 磁盘缓存：跨会话保留，彻底免去重复网络请求。
	private static readonly string DiskDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MikuMusic", "cover_cache");

	// 磁盘缓存软上限：单台机器封面数量有限，超过就清掉最旧的，避免无限涨。
	private const int MaxDiskFiles = 5000;

	// ── 可见行钉住表 ──
	// 视口里正在显示（或行控件还活着）的封面键。LRU 逐出时跳过这些键：
	// 宁可挤掉不可见的，也不动用户眼前这张。带锁的 HashSet 足够——
	// 增删只发生在 UI 线程的行 Loaded/Unloaded，逐出扫一遍是 O(n)。
	private static readonly object PinLock = new();
	private static readonly HashSet<string> PinnedKeys = new(StringComparer.Ordinal);

	// ── 在飞装载去重 ──
	// 同一 cacheKey#width 的装载（字节获取 + 解码）同时只放一个在飞，
	// 并发调用共享同一个 Task。旧版只有预取去重、行加载没有：同一屏里
	// 同一张封面出现两次（或列表重建撞上在飞请求）就会重复走网络和解码。
	private static readonly ConcurrentDictionary<string, Task<ImageSource?>> InflightLoads = new();

	// ── 诊断计数 ──
	private static long _statHits;        // 解码缓存同步命中
	private static long _statMisses;      // 解码缓存未命中（要走字节/磁盘/网络）
	private static long _statFetches;     // 真正发了网络的次数
	private static long _statDecodes;     // 完成解码的次数
	private static long _statEvictions;   // LRU 逐出总次数（含字节与解码两级）
	private static long _statDecodedPixels; // 累计解码像素量（估算位图内存用）
	private static long _loadsSinceLog;

	// 周期诊断日志：每 30s 一条，另外每完成 200 次装载强制插一条。
	// 用户复现「闪封面/涨内存」后拿日志就能确认缓存收敛还是一直在循环。
	private static readonly Timer DiagTimer = new(_ => LogDiagnostics("30s"), null,
		TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

	static CoverLoader()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
		try
		{
			Directory.CreateDirectory(DiskDir);
		}
		catch
		{
			// 创建失败（极少见，比如目录不可写）就退化成纯内存缓存，不影响主流程。
		}
	}

	/// <summary>封面显示**总闸**（设置「显示封面图片」）。
	/// 以前这只闸只拦列表行（各调用点自己判断），播放条/SMTC/沉浸页各管各的，
	/// 挂漏一处用户就能看到封面漏网。现在收口在入口服务：闸一关，
	/// 任何取图调用（行、播放条、SMTC、卡片…）都在这里被拦下 ——
	/// 网络/磁盘/解码全停，磁盘缓存保留（开回来不用重新下载），
	/// 调用方拿 null/不动作自然回落到各自的纯色占位。
	/// 用户要看的「关掉后的实际内存底线」由这条保证。</summary>
	public static bool CoversEnabled => SettingsManager.Instance.ShowListCovers;

	/// <summary>decodeWidth 是解码像素宽：列表行默认 128 够用，迷你播放器传 96。
	/// 缓存键带宽度（同一张图两种尺寸各解码一份），但磁盘只存一份原始字节，
	/// 任意宽度都能从同一份字节重新解码，省磁盘。</summary>
	public static void LoadAsync(Image img, string url, int decodeWidth = 128, Action? onLoaded = null)
	{
		if (img == null)
		{
			return;
		}
		if (!CoversEnabled)
		{
			// 总闸关闭：不碰缓存也不发请求，把已显示的图撤成空（调用方控件回到占位态）。
			img.Source = null;
			return;
		}
		string? fixedUrl = NormalizeUrl(url);
		if (string.IsNullOrWhiteSpace(fixedUrl))
		{
			return;
		}
		url = fixedUrl;

		string memKey = url + "#" + decodeWidth;

		// 1) 内存解码缓存（最近显示过的，直接复用，零解码）。唯一留在调用线程的快路径。
		if (DecodedCache.TryGetValue(memKey, out ImageSource? hot))
		{
			Interlocked.Increment(ref _statHits);
			// 命中即续期（真 LRU），并且立刻以不透明态摆上：重建出来的行封面
			// 已经在手，再播一遍 0→1 淡入就是列表反复重画时「封面集体闪一下」
			// 的直接来源。这里先把不透明度顶满，onLoaded 里的淡入会自然跳过。
			img.Source = hot;
			ShowImmediately(img);
			onLoaded?.Invoke();
			return;
		}

		LoadIntoAsync(img, url, url, decodeWidth, onLoaded);
	}

	/// <summary>按自定义缓存键加载封面：内存字节 / 磁盘 / 解码三层缓存全部以
	/// cacheKey 为键，仅当缓存未命中时才从 networkUrl 拉字节落进这套缓存。
	/// 给本地音乐的「在线补封面」用 —— 本地文件没有现成的封面 URL，探测命中
	/// 一张后按「local:文件路径」这样的稳定键存进共用缓存，同一首歌下次
	/// （乃至重启后，走磁盘层）直接命中，不再重复请求。
	/// cacheKey 未必是合法 URL，所以这里不走 NormalizeUrl，由调用方保证非空。</summary>
	public static void LoadByKeyAsync(Image img, string cacheKey, string networkUrl, int decodeWidth = 128, Action? onLoaded = null)
	{
		if (img == null || string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(networkUrl))
		{
			return;
		}
		if (!CoversEnabled)
		{
			// 总闸关闭：同 LoadAsync —— 撤图、停请求，控件自然回占位态。
			img.Source = null;
			return;
		}
		// 内存解码缓存快路径与 LoadAsync 一致：命中同步摆上、不播淡入。
		if (DecodedCache.TryGetValue(cacheKey + "#" + decodeWidth, out ImageSource? hot))
		{
			Interlocked.Increment(ref _statHits);
			img.Source = hot;
			ShowImmediately(img);
			onLoaded?.Invoke();
			return;
		}
		LoadIntoAsync(img, cacheKey, networkUrl, decodeWidth, onLoaded);
	}

	/// <summary>同步查解码缓存：命中返回 true 并把位图交出，同时续期 LRU。
	/// 给行构建方用——重建行的时候封面多半还在缓存里，当场挂上就不必
	/// 等视口调度的那一拍，行从出生起就是带图状态，翻页/重画绝不闪。</summary>
	public static bool TryGetDecoded(string cacheKeyOrUrl, int decodeWidth, out ImageSource source)
	{
		source = null!;
		string? key = cacheKeyOrUrl;
		if (!string.IsNullOrWhiteSpace(cacheKeyOrUrl))
		{
			// 常规 URL 先归一化，保证和 LoadAsync 用的是同一个键；
			// 「local:路径」这类自定义键 NormalizeUrl 会拒，原样用。
			string? normalized = NormalizeUrl(cacheKeyOrUrl);
			key = normalized ?? cacheKeyOrUrl;
		}
		if (string.IsNullOrEmpty(key)
			|| !DecodedCache.TryGetValue(key + "#" + decodeWidth, out ImageSource? hit))
		{
			return false;
		}
		DecodedLastUse[key + "#" + decodeWidth] = Environment.TickCount64;
		source = hit;
		return true;
	}

	/// <summary>把一个封面键登记为「当前可见」：LRU 逐出时跳过。
	/// 行进入视口开始加载时登记，行离开可视树解除。</summary>
	public static void PinDecoded(string cacheKeyOrUrl)
	{
		string key = PinKeyOf(cacheKeyOrUrl);
		if (key.Length == 0)
		{
			return;
		}
		lock (PinLock)
		{
			PinnedKeys.Add(key);
		}
	}

	/// <summary>解除可见登记（与 <see cref="PinDecoded"/> 成对调用）。</summary>
	public static void UnpinDecoded(string cacheKeyOrUrl)
	{
		string key = PinKeyOf(cacheKeyOrUrl);
		if (key.Length == 0)
		{
			return;
		}
		lock (PinLock)
		{
			PinnedKeys.Remove(key);
		}
	}

	private static string PinKeyOf(string cacheKeyOrUrl)
	{
		if (string.IsNullOrWhiteSpace(cacheKeyOrUrl))
		{
			return "";
		}
		return NormalizeUrl(cacheKeyOrUrl) ?? cacheKeyOrUrl.Trim();
	}

	/// <summary>同步命中时把不透明度顶满并摘掉残留动画。
	/// 只在 < 0.999 时动手：本来就亮的行一个动画对象都不造。</summary>
	private static void ShowImmediately(Image img)
	{
		if (img.Opacity < 0.999)
		{
			img.BeginAnimation(UIElement.OpacityProperty, null);
			img.Opacity = 1;
		}
	}

	/// <summary>LoadAsync / LoadByKeyAsync 共用的后台装载入口：同一张封面
	/// （cacheKey#width 相同）的并发装载共享一个在飞任务，完成后各自回
	/// UI 线程摆图。</summary>
	private static void LoadIntoAsync(Image img, string cacheKey, string? networkUrl, int decodeWidth, Action? onLoaded)
	{
		Interlocked.Increment(ref _statMisses);
		string mk = cacheKey + "#" + decodeWidth;
		Task<ImageSource?> pipeline = BeginInflightLoad(mk, cacheKey, networkUrl, decodeWidth);
		_ = pipeline.ContinueWith(t =>
		{
			ImageSource? src = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
			var d = Application.Current?.Dispatcher;
			if (d != null && !d.HasShutdownStarted)
			{
				_ = d.BeginInvoke(new Action(() =>
				{
					if (src != null)
					{
						img.Source = src;
					}
					onLoaded?.Invoke();
				}));
			}
			else
			{
				if (src != null)
				{
					img.Source = src;
				}
				onLoaded?.Invoke();
			}
		});
	}

	/// <summary>登记一条在飞装载并返回共享任务；登记成功后负责「完成即摘除」。
	///
	/// 为什么不能用 ConcurrentDictionary.GetOrAdd(key, _ => LoadPipelineAsync(...))：
	/// GetOrAdd 的工厂是在**条目插入之前**执行的——而 LoadPipelineAsync 在
	/// 字节缓存/磁盘命中时全程没有 await，会**同步跑完**：工厂返回时任务已经
	/// 完成、finally 里的自摘除也已经跑过（此时条目还没插进去，摘除自然扑空），
	/// 随后插入的条目就再也没人摘了。
	///
	/// 实测后果（日志）：「在飞装载」只增不减，一个晚上从 22 一路涨到 546 后
	/// 恒定不落；每个僵尸条目都用 Task.Result 钉着一张解码位图——LRU 把
	/// DecodedCache 逐出了，位图本体却还被攥着，永远 GC 不掉。搜索页每翻一页
	/// 都是几十张新封面，这正是搜索页内存持续上涨的主根因；音乐库页同样受益。
	///
	/// 修法：TryGetValue 查重 + TryAdd 登记；登记成功后由**这里**挂一段
	/// 「完成即摘除」的续体——续体一定在条目插入之后才可能跑，同步完成的
	/// 任务也摘得掉。摘除用 (键, 值) 成对 TryRemove（.NET 5+ 重载）：并发下
	/// 装载函数可能被启动两次，只有「值还是自己」的条目才允许摘，避免把别人
	/// 刚登记、还在飞的任务误摘掉导致第三次重复装载。</summary>
	private static Task<ImageSource?> BeginInflightLoad(string mk, string cacheKey, string? networkUrl, int decodeWidth)
	{
		if (InflightLoads.TryGetValue(mk, out Task<ImageSource?>? existing))
		{
			return existing;
		}
		Task<ImageSource?> fresh = LoadPipelineAsync(cacheKey, networkUrl, decodeWidth);
		if (!InflightLoads.TryAdd(mk, fresh))
		{
			// 并发撞车：别人已经登记了自己的在飞任务，跟它合流。最坏情况是
			// 同一张封面重复拉一次网络（闸门限流兜底），功能不受影响。
			return InflightLoads.TryGetValue(mk, out Task<ImageSource?>? winner) ? winner : fresh;
		}
		// 完成即摘除。同步完成的任务（字节缓存/磁盘命中）续体会立刻在插入之后
		// 执行，摘除一定生效；异步任务则等真正跑完再摘。
		_ = fresh.ContinueWith(_ =>
			InflightLoads.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(mk, fresh)),
			TaskScheduler.Default);
		return fresh;
	}

	/// <summary>一次完整装载：字节缓存（内存/磁盘）→ 需要时走网络 → 解码。
	/// 在飞表条目的摘除由 BeginInflightLoad 挂的完成续体负责（在这里摘的话，
	/// 同步完成的管线会在登记插入之前自摘扑空，造成僵尸条目——见那边注释）。</summary>
	private static async Task<ImageSource?> LoadPipelineAsync(string cacheKey, string? networkUrl, int decodeWidth)
	{
		string mk = cacheKey + "#" + decodeWidth;
		try
		{
			byte[]? bytes = BytesCache.TryGetValue(cacheKey, out byte[]? b) ? b : ReadDisk(cacheKey);
			if (bytes != null)
			{
				// 字节层命中：续期 LRU。磁盘命中顺手回填内存字节层，
				// 免得同一张封面每轮滚动都重读一次盘。
				BytesLastUse[cacheKey] = Environment.TickCount64;
				BytesCache[cacheKey] = bytes;
			}
			else
			{
				// 兜底：网络拉取（受全局并发闸门限制），拉到后写磁盘 + 进内存字节缓存。
				if (string.IsNullOrWhiteSpace(networkUrl))
				{
					return null;
				}
				await FetchIntoCacheAsync(cacheKey, networkUrl);
				Interlocked.Increment(ref _statFetches);
				bytes = BytesCache.TryGetValue(cacheKey, out b) ? b : null;
			}
			if (bytes == null)
			{
				return null;
			}

			ImageSource? src = TryDecode(bytes, decodeWidth);
			if (src == null)
			{
				return null;
			}
			if (src is BitmapSource bs)
			{
				Interlocked.Add(ref _statDecodedPixels, (long)bs.PixelWidth * bs.PixelHeight);
			}
			DecodedCache[mk] = src;
			DecodedLastUse[mk] = Environment.TickCount64;
			Interlocked.Increment(ref _statDecodes);
			TrimCaches();
			return src;
		}
		catch (Exception ex)
		{
			// 封面加载失败是常态（图床 404/断网），Debug 级留痕即可。
			LogHelper.LogDebug("封面装载管线失败: " + ex.Message);
			return null;
		}
		finally
		{
			// 注意：在飞条目不在这里摘（改由 BeginInflightLoad 的完成续体摘）。
			// 旧版在这里 TryRemove：同步完成的管线执行 finally 时条目还没被
			// GetOrAdd 插进去，摘除扑空，条目就成了永不清理的僵尸——那是
			// 内存持续上涨的主根因，见 BeginInflightLoad 的注释。
			// 每 200 次装载插一条诊断，用户复现后看这个频率就知道装载收敛与否。
			if (Interlocked.Increment(ref _loadsSinceLog) >= 200)
			{
				Interlocked.Exchange(ref _loadsSinceLog, 0);
				LogDiagnostics("200loads");
			}
		}
	}

	// 预取去重：同一个 URL 同时只放一个在飞的预取请求，重复调用直接复用。
	private static readonly ConcurrentDictionary<string, Task> Preloading = new();

	// 全局网络并发闸门：同时最多 4 个封面在下载。列表快速滚动/翻页时会瞬间产生
	// 几十个请求，不设闸门的话带宽被摊薄、真正进视口的行反而等最久，对图床也不友好。
	// 预取和正常加载共用同一道闸门，保证进视口的加载优先级不被预取挤占带宽。
	private static readonly SemaphoreSlim NetGate = new(4, 4);

	/// <summary>
	/// 释放全部内存层封面缓存（解码位图 + 压缩原始字节），磁盘缓存原样保留。
	/// 给「收进托盘长期驻留」用：托盘状态下没人看列表，这两层内存白占着；
	/// 清掉后窗口回到前台时，视口里的行会按正常装载管线自动重载——
	/// 字节层没了先查磁盘缓存（不重新走网络），解码一次又回到热路径。
	/// 只清内存层：磁盘是跨会话持久缓存，清了只会让回归体验变差。
	/// 钉住表（可见行登记）不动：它只是 LRU 逐出的跳过名单，不持有位图引用。
	/// </summary>
	public static void ReleaseDecodedCache()
	{
		try
		{
			int decoded = DecodedCache.Count;
			int bytes = BytesCache.Count;
			DecodedCache.Clear();
			DecodedLastUse.Clear();
			BytesCache.Clear();
			BytesLastUse.Clear();
			LogHelper.Log($"[托盘驻留] 封面内存缓存已释放：解码 {decoded} 项、字节 {bytes} 项（磁盘缓存保留）");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "释放封面内存缓存失败");
		}
	}

	/// <summary>把封面提前拉进缓存（内存字节 + 磁盘），不解码、不碰任何 Image。
	/// 给「渲染第 N 页后把第 N+1 页前几张封面预取进缓存」用：点下一页时
	/// 前几首的封面即现。已有缓存（内存/磁盘命中）就不发网络请求；
	/// 预取失败静默吞掉 —— 预取是锦上添花，行真正滚进视口时还有一次正常加载兜底。</summary>
	public static Task PreloadAsync(string? url)
	{
		// 总闸关闭：预取直接停（磁盘缓存不动，开闸后无需重下）。
		if (!CoversEnabled)
		{
			return Task.CompletedTask;
		}
		string? fixedUrl = NormalizeUrl(url ?? "");
		if (fixedUrl == null
			|| !(fixedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
				|| fixedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
		{
			// 只预取网络封面；本地文件封面解码本来就便宜，不值得预取。
			return Task.CompletedTask;
		}
		if (BytesCache.ContainsKey(fixedUrl) || File.Exists(DiskPath(fixedUrl)))
		{
			return Task.CompletedTask;
		}
		return Preloading.GetOrAdd(fixedUrl, u => FetchToCacheAsync(u));
	}

	/// <summary>URL 已缓存到磁盘时返回本地文件路径；没缓存过返回 null，绝不发起网络请求。
	/// 给 SMTC 这类「只吃本地文件」的场景用：行显示过的封面，系统媒体浮层直接复用。</summary>
	public static string? GetCachedFilePath(string? url)
	{
		// 总闸关闭：SMTC 等取图方一律拿不到文件（上层同时也会主动传 null）。
		if (!CoversEnabled)
		{
			return null;
		}
		string? fixedUrl = NormalizeUrl(url ?? "");
		if (fixedUrl == null)
		{
			return null;
		}
		try
		{
			string path = DiskPath(fixedUrl);
			return File.Exists(path) ? path : null;
		}
		catch
		{
			return null;
		}
	}

	private static async Task FetchToCacheAsync(string url)
	{
		try
		{
			// 预取也走同一道并发闸门：预取是锦上添花，不能抢占进视口加载的带宽。
			await NetGate.WaitAsync();
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Referrer = new Uri(RefererFor(url));
				using var resp = await Http.SendAsync(req);
				resp.EnsureSuccessStatusCode();
				byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
				// 只进缓存不显示：写磁盘（跨会话）+ 内存字节缓存（本会话），
				// 不解码 —— 行滚进视口时会走 LoadAsync 从字节缓存直接解码。
				WriteDisk(url, bytes);
				NoteBytes(url, bytes);
			}
			finally
			{
				NetGate.Release();
			}
		}
		catch (Exception ex)
		{
			// 预取是锦上添花，失败不影响显示，只留 Debug。
			LogHelper.LogDebug("封面预取失败: " + ex.Message);
		}
		finally
		{
			Preloading.TryRemove(url, out _);
		}
	}

	/// <summary>字节缓存写入 + LRU 续期 + 超限裁剪。</summary>
	private static void NoteBytes(string key, byte[] bytes)
	{
		BytesCache[key] = bytes;
		BytesLastUse[key] = Environment.TickCount64;
		TrimCaches();
	}

	/// <summary>两级内存缓存一起裁：按「最后使用时刻」从旧到新逐出，
	/// 超出上限的先走，可见行的解码位图一律跳过。</summary>
	private static void TrimCaches()
	{
		TrimOneCache(BytesCache, BytesLastUse, MaxMemoryBytes, decoded: false);
		TrimOneCache(DecodedCache, DecodedLastUse, MaxDecoded, decoded: true);
	}

	private static void TrimOneCache<TValue>(
		ConcurrentDictionary<string, TValue> cache,
		ConcurrentDictionary<string, long> lastUse,
		int cap,
		bool decoded)
	{
		int evicted = 0;
		while (cache.Count > cap)
		{
			string? oldest = null;
			long oldestTick = long.MaxValue;
			foreach (var kv in lastUse)
			{
				if (!cache.ContainsKey(kv.Key))
				{
					continue; // 时间戳表里的残留（缓存条目已从别的路径移走）
				}
				if (decoded && IsPinned(kv.Key))
				{
					continue; // 当前可见的封面绝不逐出
				}
				if (kv.Value < oldestTick)
				{
					oldestTick = kv.Value;
					oldest = kv.Key;
				}
			}
			if (oldest == null)
			{
				// 剩下的全是钉住的（或时间戳表坏掉）：就此打住，别死循环。
				break;
			}
			cache.TryRemove(oldest, out _);
			lastUse.TryRemove(oldest, out _);
			evicted++;
		}
		if (evicted > 0)
		{
			Interlocked.Add(ref _statEvictions, evicted);
		}
		// 时间戳表若比缓存膨胀太多（理论上不该发生，兜底），把孤儿键清掉。
		if (lastUse.Count > cache.Count * 2 + 64)
		{
			foreach (string key in lastUse.Keys)
			{
				if (!cache.ContainsKey(key))
				{
					lastUse.TryRemove(key, out _);
				}
			}
		}
	}

	private static bool IsPinned(string cacheKey)
	{
		// 解码缓存的键是「cacheKey#width」，钉住表记的是不带宽度的 cacheKey。
		int hash = cacheKey.LastIndexOf('#');
		string bare = hash > 0 ? cacheKey.Substring(0, hash) : cacheKey;
		lock (PinLock)
		{
			return PinnedKeys.Contains(bare);
		}
	}

	/// <summary>周期诊断：一条日志看全缓存的收敛状态。</summary>
	private static void LogDiagnostics(string tag)
	{
		try
		{
			int pinned;
			lock (PinLock)
			{
				pinned = PinnedKeys.Count;
			}
			double pixelsMb = Interlocked.Read(ref _statDecodedPixels) * 4.0 / (1024 * 1024);
			LogHelper.Log(string.Format(
				"[封面诊断/{0}] 解码缓存 {1}/{2} · 字节缓存 {3}/{4} · 钉住 {5} · 在飞装载 {6} · 在飞预取 {7} · 命中 {8} · 未命中 {9} · 网络 {10} · 解码 {11} · 逐出 {12} · 累计解码像素 ≈ {13:F1}MB",
				tag,
				DecodedCache.Count, MaxDecoded,
				BytesCache.Count, MaxMemoryBytes,
				pinned,
				InflightLoads.Count,
				Preloading.Count,
				Interlocked.Read(ref _statHits),
				Interlocked.Read(ref _statMisses),
				Interlocked.Read(ref _statFetches),
				Interlocked.Read(ref _statDecodes),
				Interlocked.Read(ref _statEvictions),
				pixelsMb));
		}
		catch
		{
			// 诊断日志永远不能反过来打扰主流程。
		}
	}

	private static string DiskPath(string url)
	{
		// URL 做哈希当文件名：长度固定、无非法字符、不可逆（封面不是机密，仅作去重键）。
		using var sha = SHA1.Create();
		byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
		string name = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
		return Path.Combine(DiskDir, name + ".img");
	}

	private static byte[]? ReadDisk(string url)
	{
		try
		{
			string path = DiskPath(url);
			if (File.Exists(path))
			{
				return File.ReadAllBytes(path);
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("读取封面磁盘缓存失败: " + ex.Message);
		}
		return null;
	}

	private static void WriteDisk(string url, byte[] bytes)
	{
		try
		{
			File.WriteAllBytes(DiskPath(url), bytes);
			TrimDiskIfNeeded();
		}
		catch (Exception ex)
		{
			// 写盘失败（只读 / 盘满）就放弃磁盘缓存，不影响内存与显示。
			LogHelper.LogDebug("写入封面磁盘缓存失败: " + ex.Message);
		}
	}

	/// <summary>磁盘文件超过软上限时，按修改时间删最旧的，避免无限占用。</summary>
	private static void TrimDiskIfNeeded()
	{
		try
		{
			var dir = new DirectoryInfo(DiskDir);
			if (!dir.Exists)
			{
				return;
			}
			var files = dir.GetFiles("*.img");
			if (files.Length <= MaxDiskFiles)
			{
				return;
			}
			// 按修改时间升序，删最旧的直到回到上限内。
			Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
			int toDelete = files.Length - MaxDiskFiles;
			for (int i = 0; i < toDelete; i++)
			{
				try
				{
					files[i].Delete();
				}
				catch (Exception ex)
				{
					LogHelper.LogDebug("删除过期封面缓存失败: " + ex.Message);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("裁剪封面磁盘缓存失败: " + ex.Message);
		}
	}

	/// <summary>归一化图片 URL：补全 // 协议、修复缺失/错误的 scheme；非法地址返回 null。</summary>
	private static string? NormalizeUrl(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}
		string u = url.Trim();
		if (u.StartsWith("//", StringComparison.Ordinal))
		{
			u = "https:" + u;
		}
		else if (u.StartsWith("/", StringComparison.Ordinal))
		{
			return null;
		}
		else if (!Uri.TryCreate(u, UriKind.Absolute, out _))
		{
			return null;
		}
		if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
		{
			u = "https://" + u.Substring("http://".Length);
		}
		return PinCoverSize(u);
	}

	/// <summary>钉定网易云封面的尺寸参数，让同一张封面永远映射到同一个缓存键。
	///
	/// 为什么：网易云不同接口返回的同一张专辑封面可能带不同的 ?param= 尺寸
	/// 后缀（有的接口给 300y300、有的给 130y130、有的干脆不带参数返回原图），
	/// 而字节/磁盘/解码三级缓存的键都是 URL 字符串——同曲两张尺寸不同的
	/// URL 就被当成两张封面：反复下载、反复解码、字节缓存里各存一份。
	/// 此前怀疑的「签名/时间戳参数导致缓存永远 miss」在这两个平台并不成立
	/// （QQ 的封面是按 albummid 拼的固定 CDN 地址，酷狗/B 站是稳定路径，
	/// 都没有会话级随机参数），真正的键不稳定来源是这类「同一张图、多个尺寸
	/// 变体」，这里统一剥掉查询参数、钉成固定尺寸（180y180 对 128px 解码宽
	/// 绰绰有余），任何入口拿到的同一张封面都收敛成同一个键。
	/// 注意：钉定会改缓存键，旧磁盘缓存条目会短暂变孤儿，由磁盘 LRU 按时间
	/// 淘汰，无需手动清理。</summary>
	private static string PinCoverSize(string url)
	{
		try
		{
			Uri uri = new(url);
			string host = uri.Host.ToLowerInvariant();
			// 只有网易云的封面 CDN（p1~p4.music.126.net 等）用 ?param= 尺寸语法。
			if (!host.EndsWith("music.126.net", StringComparison.Ordinal))
			{
				return url;
			}
			return uri.GetLeftPart(UriPartial.Path) + "?param=180y180";
		}
		catch
		{
			return url;
		}
	}

	/// <summary>封面缓存统计快照（调试面板「内存检测」用）。估算字节按
	/// 解码位图=宽×高×4、字节缓存=数组长度累加，是近似值但足够看量级。
	/// 注意不能是 readonly struct：GetStats 用对象初始化器逐字段填。</summary>
	public struct CoverStats
	{
		public int DecodedCount;
		public long DecodedEstBytes;
		public int BytesCount;
		public long BytesEstBytes;
		public int InflightLoads;
		public int Preloading;
		public int Pinned;
		public long Hits;
		public long Misses;
		public long Fetches;
		public long Decodes;
		public long Evictions;
	}

	/// <summary>取一份统计快照。解码缓存里的位图都是 Freeze 过的，跨线程读安全。</summary>
	public static CoverStats GetStats()
	{
		var s = new CoverStats
		{
			InflightLoads = InflightLoads.Count,
			Preloading = Preloading.Count,
			Hits = Interlocked.Read(ref _statHits),
			Misses = Interlocked.Read(ref _statMisses),
			Fetches = Interlocked.Read(ref _statFetches),
			Decodes = Interlocked.Read(ref _statDecodes),
			Evictions = Interlocked.Read(ref _statEvictions)
		};
		lock (PinLock)
		{
			s.Pinned = PinnedKeys.Count;
		}
		foreach (var kv in DecodedCache)
		{
			s.DecodedCount++;
			s.DecodedEstBytes += kv.Value is BitmapSource bs
				? (long)bs.PixelWidth * bs.PixelHeight * 4
				: 128 * 128 * 4; // 理论上都 Freeze 成了 BitmapSource，兜底按解码宽估算
		}
		foreach (var kv in BytesCache)
		{
			s.BytesCount++;
			s.BytesEstBytes += kv.Value.Length;
		}
		return s;
	}

	/// <summary>把封面字节拉进缓存（磁盘 + 内存字节缓存），不解码、不碰任何 Image。
	/// 走全局并发闸门（≤4），加载完成后再由调用方从字节缓存解码。
	/// cacheKey 与 url 分开传：常规封面两者相同；本地补封面的场景缓存键是
	/// 「local:文件路径」这类稳定键，网络才是真正的图片地址——同一首歌换一版
	/// 封面 URL 也仍命中同一份缓存。</summary>
	private static async Task FetchIntoCacheAsync(string cacheKey, string url)
	{
		try
		{
			await NetGate.WaitAsync();
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Referrer = new Uri(RefererFor(url));
				using var resp = await Http.SendAsync(req);
				resp.EnsureSuccessStatusCode();
				byte[] bytes = await resp.Content.ReadAsByteArrayAsync();

				// 落到磁盘（跨会话复用）与内存字节缓存（本次会话复用）。
				WriteDisk(cacheKey, bytes);
				NoteBytes(cacheKey, bytes);
			}
			finally
			{
				NetGate.Release();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("下载封面失败: " + ex.Message);
		}
	}

	private static string RefererFor(string url)
	{
		try
		{
			string host = new Uri(url).Host;
			if (host.Contains("bilibili") || host.Contains("hdslb")) return "https://www.bilibili.com/";
			if (host.Contains("126.net")) return "https://music.163.com/";
			if (host.Contains("kugou")) return "https://www.kugou.com/";
			if (host.Contains("gtimg")) return "https://y.qq.com/";
		}
		catch
		{
		}
		return "";
	}

	private static ImageSource? TryDecode(byte[] bytes, int decodeWidth)
	{
		try
		{
			var bi = new BitmapImage();
			bi.BeginInit();
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.DecodePixelWidth = decodeWidth;
			using var ms = new MemoryStream(bytes);
			bi.StreamSource = ms;
			bi.EndInit();
			bi.Freeze();
			return bi;
		}
		catch
		{
			return null;
		}
	}
}
