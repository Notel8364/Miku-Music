using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>歌词窗口的开关状态之外的一切：拉歌词、跟进度、缓存都放这儿。
///
/// 设计要点：
/// 1) 跟着 <see cref="MusicService.StateChanged"/> 走，所以切歌就自动换歌词，
///    不需要在每一行上挂按钮。切歌很快时会连着来好几次，这里用
///    「取回来后发现歌已经变了就丢掉」的方式处理，不会把 A 的歌词画到 B 上。
/// 2) 按文件路径缓存。同一首歌来回切时直接命中，不重复请求网易云，
///    也不会因为网络慢导致歌词半天不出来。
/// 3) 只用 <see cref="LyricCache"/> 这一个静态表做缓存，故意不落盘：
///    歌词几十 KB 就够，进程退出就没了，没必要管失效。</summary>
public sealed class LyricFollowService : IDisposable
{
	/// <summary>缓存上限。500 首足够一个歌单来回切了，超了就丢最久没用的。</summary>
	private const int CacheLimit = 500;

	private static readonly ConcurrentDictionary<string, LyricBundle> Cache =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
	private readonly CancellationTokenSource _stop = new();

	private string? _path;
	private int _generation;
	private bool _disposed;

	/// <summary>拖动进度条时用来抑制「取完歌词又跳回去」的抖动（毫秒）。</summary>
	public const int StaleToleranceMs = 250;

	/// <summary>歌词内容变化时触发。参数是这份歌词对应的歌名，仅供界面显示。</summary>
	public event Action<LyricBundle?>? LyricsChanged;

	/// <summary>正在拉歌词时为 true，界面可以转圈。</summary>
	public bool IsLoading { get; private set; }

	/// <summary>上一次拉失败的原因，用于给用户一句人话提示。</summary>
	public string? LastError { get; private set; }

	public LyricFollowService()
	{
		MusicService.StateChanged += OnStateChanged;
		_path = MusicService.CurrentPath;
	}

	/// <summary>取当前歌的歌词；已缓存就立刻返回，没有就先去拉。</summary>
	public async Task<LyricBundle?> GetAsync(string path, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		if (Cache.TryGetValue(path, out LyricBundle? hit))
		{
			return hit;
		}
		// 明确查过没有的歌就别反复去问了，否则每切一次就白等一次网络超时。
		if (_missing.Contains(path))
		{
			return null;
		}
		return await FetchAsync(path, ct);
	}

	/// <summary>手动重试：把「查不到」的记录清掉再拉一次。用户点了歌名还是没歌词时会用。</summary>
	public async Task<LyricBundle?> RetryAsync(string path, CancellationToken ct = default)
	{
		_missing.Remove(path);
		Cache.TryRemove(path, out _);
		return await FetchAsync(path, ct);
	}

	private async Task<LyricBundle?> FetchAsync(string path, CancellationToken ct)
	{
		int gen = _generation;
		IsLoading = true;
		// 定位信息在拉网络之前就取好：网络等待期间可能已经切歌，
		// 那时 MusicService 里的元数据属于新歌，拿它去搜就串台了。
		LyricMatchContext ctx = MusicService.CurrentLyricContext;
		LogHelper.LogDebug($"开始拉歌词: {Path.GetFileName(path)} "
			+ $"ctx='{ctx.Title}' artist='{ctx.Artist}' src={ctx.Platform}:{ctx.PlatformId} dur={ctx.DurationMs}ms");
		try
		{
			long? id = await NeteaseApiClient.ResolveSongIdAsync(ctx, ct);
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return null;
			}
			LyricBundle? bundle = null;
			bool fromNetease = false;
			if (id == null)
			{
				// 匹配不到网易云 ID 不等于没歌词。少了这一段分支，
				// LRCLIB 这个兜底源在「网易云没有这首歌」时永远没机会上场，
				// 而这恰恰是它最有用的场景之一（冷门/B 站下载的曲子）。
				LogHelper.LogDebug("没有匹配到网易云歌曲ID，改用 LRCLIB: " + ctx.Title);
			}
			else
			{
				bundle = await NeteaseApiClient.GetLyricBundleAsync(id.Value, ct);
				if (ct.IsCancellationRequested || gen != _generation)
				{
					return null;
				}
				fromNetease = bundle != null && bundle.Count > 0;
			}
			// 网易云只有行级时间轴，而且 tlyric 经常是空的。
			// 这时候再去 LRCLIB 补一次：它覆盖面更广，还带翻译。
			bundle = await FillGapsAsync(bundle, ctx, ct);
			string source = fromNetease ? "网易云+LRCLIB" : "LRCLIB";
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return null;
			}
			if (bundle == null || bundle.Count == 0)
			{
				MarkMissing(path);
				LogHelper.LogDebug("两个源都没匹配到歌词: " + ctx.Title);
				return null;
			}
			LastError = null;
			Put(path, bundle);
			int withTr = bundle.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Translation));
			int withChar = bundle.Lines.Count(l => l.CharStarts != null);
			LogHelper.LogDebug($"歌词就绪: {ctx.Title} id={id} "
				+ $"行数={bundle.Count} 带翻译={withTr} 带字级={withChar} 源={source}");
			return bundle;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			LogHelper.LogError(ex, "拉歌词失败: " + Path.GetFileName(path));
			return null;
		}
		finally
		{
			if (gen == _generation)
			{
				IsLoading = false;
			}
		}
	}

	/// <summary>用 LRCLIB 补齐网易云缺的部分：
	/// 一首歌的歌词整首都拿不到时换 LRCLIB 的，已拿到但一行翻译都没有时只补翻译。</summary>
	private async Task<LyricBundle?> FillGapsAsync(LyricBundle? netease, LyricMatchContext ctx, CancellationToken ct)
	{
		string title = ctx.Title;
		string? artist = ctx.Artist;
		bool needAll = netease == null || netease.Count == 0;
		// needAll 为 false 时编译器还看不出 netease 一定非空，这里只是让它别再报警。
		bool needTranslation = !needAll && SettingsManager.Instance.LyricShowTranslation
			&& !netease!.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));
		if (!needAll && !needTranslation)
		{
			return netease;
		}
		int dur = ctx.DurationMs > 0 ? (int)Math.Round(ctx.DurationMs / 1000.0) : 0;
		LyricBundle? lrclib = await LrcLibClient.FetchBundleAsync(title, artist, dur, ct);
		if (lrclib == null || ct.IsCancellationRequested)
		{
			return netease;
		}
		if (needAll)
		{
			return lrclib;
		}
		// 只补翻译：按时间对齐贴回去，正文还是用网易云那份。
		MergeTranslations(netease!, lrclib);
		return netease;
	}

	/// <summary>把 LRCLIB 的翻译按时间贴到已有歌词上。只填空的，不覆盖已有的。</summary>
	private static void MergeTranslations(LyricBundle target, LyricBundle from)
	{
		foreach (LyricLine line in target.Lines)
		{
			if (!string.IsNullOrWhiteSpace(line.Translation))
			{
				continue;
			}
			foreach (LyricLine src in from.Lines)
			{
				if (Math.Abs(src.TimeMs - line.TimeMs) <= 400 && !string.IsNullOrWhiteSpace(src.Translation))
				{
					line.Translation = src.Translation;
					break;
				}
			}
		}
	}

	private void MarkMissing(string path)	{
		_missing.Add(path);
		TrimMissing();
	}

	/// <summary>_missing 只用来挡重复请求，不需要留太多，否则会把「以后补了歌词」也一起挡掉。</summary>
	private void TrimMissing()
	{
		if (_missing.Count <= CacheLimit)
		{
			return;
		}
		// 直接留最近的一半。缓存命中与否决定有没有歌词，
		// 所以这个集合宁可丢也不能无限长。
		var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int n = 0;
		foreach (string k in _missing)
		{
			if (n++ >= CacheLimit / 2)
			{
				break;
			}
			keep.Add(k);
		}
		_missing.Clear();
		_missing.UnionWith(keep);
	}

	private void Put(string path, LyricBundle bundle)
	{
		Cache[path] = bundle;
		_missing.Remove(path);
		if (Cache.Count > CacheLimit)
		{
			// Cache 是无序的，随手丢一个到上限为止即可，
			// 这里不做严格 LRU：歌词重拉一次也就一次网络请求。
			int drop = Cache.Count - CacheLimit;
			foreach (string k in Cache.Keys)
			{
				if (drop-- <= 0)
				{
					break;
				}
				Cache.TryRemove(k, out _);
			}
		}
	}

	private void OnStateChanged()
	{
		string? now = MusicService.CurrentPath;
		if (string.Equals(now, _path, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_path = now;
		_generation++;
		LogHelper.LogDebug($"切歌 -> {Path.GetFileName(now ?? "(无)")} gen={_generation}");

		string path = now ?? "";
		if (path.Length == 0)
		{
			LyricsChanged?.Invoke(null);
			return;
		}
		// 先把上一首的清掉，界面不会短暂留着上一首的词。
		LyricsChanged?.Invoke(null);
		_ = RefreshAsync(path, _generation);
	}

	private async Task RefreshAsync(string path, int gen)
	{
		LyricBundle? bundle = await GetAsync(path, _stop.Token);
		if (_disposed || gen != _generation)
		{
			return;
		}
		LyricsChanged?.Invoke(bundle);
	}

	/// <summary>设置里改了「显示翻译」之后，让界面按新设置重画一次。</summary>
	public void RefreshPresentation()
	{
		if (!string.IsNullOrEmpty(_path))
		{
			LyricsChanged?.Invoke(Cache.TryGetValue(_path, out LyricBundle? b) ? b : null);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		MusicService.StateChanged -= OnStateChanged;
		try
		{
			_stop.Cancel();
		}
		catch
		{
			// 关停时的取消异常无所谓
		}
		_stop.Dispose();
	}
}

/// <summary>找歌词时用来定位一首歌的信息。
///
/// 以前整套逻辑只吃一个「文件路径」，在线歌曲就全废了：播放的是
/// <c>cache\399342886.m4a</c> 这种缓存名，拿它去搜网易云必然搜不到，
/// 于是界面标题明明显示「Love Potion - 鎖那 / なゆごろう」，
/// 歌词框却说「没匹配到这首歌的歌词」。
///
/// 有了这个结构，来源不再重要：在线歌直接带平台 ID，本地歌从文件名解析。</summary>
/// <param name="Title">歌名，已去掉括号备注。</param>
/// <param name="Artist">歌手；本地文件解析不出时为 null。</param>
/// <param name="DurationMs">时长，用来在同名候选里挑最像的那首。0 表示不知道。</param>
/// <param name="Platform">来源平台（netease/kugou/qq/bili）；本地文件为 null。</param>
/// <param name="PlatformId">来源平台上的歌曲 ID；本地文件为 null。</param>
public readonly record struct LyricMatchContext(
	string Title,
	string? Artist,
	long DurationMs,
	string? Platform = null,
	string? PlatformId = null)
{
	/// <summary>是不是能直接按 ID 取歌词（省掉一次搜索，也避免搜到别的版本）。</summary>
	public bool HasDirectId =>
		PlatformId != null
		&& long.TryParse(PlatformId, out long id)
		&& id > 0
		&& string.Equals(Platform, "netease", StringComparison.OrdinalIgnoreCase);

	/// <summary>拼给搜索接口的关键词。有歌手就一起带上，命中率明显更高。</summary>
	public string SearchKey =>
		string.IsNullOrEmpty(Artist) ? Title : Title + " " + Artist;
}

/// <summary>歌词窗口用的进度读取。抽出来是为了界面层不直接摸播放器，
/// 单元测试和「无播放时也能显示」的情况都好处理。</summary>
public static class LyricClock
{
	/// <summary>当前播放位置（毫秒）。没有在播时返回 -1。</summary>
	public static long PositionMs
	{
		get
		{
			// TimeSpan.Milliseconds 是「毫秒那一段」，范围只有 0-999；
			// TimeSpan.TotalMilliseconds 才是从零起算的总毫秒数。
			// 用错的那个，歌词进度每满一分钟就会从 3:59 跳回 0:00。
			return MusicService.CurrentPath == null
				? -1
				: (long)MusicService.Position.TotalMilliseconds;
		}
	}
}
