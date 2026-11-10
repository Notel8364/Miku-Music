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

	/// <summary>只查缓存、不碰网络也不清 _missing。进全屏时先同步推一把：
	/// 上一次拉成过的歌词立刻上屏，不给用户看空白窗口等网络。</summary>
	public LyricBundle? TryGetCached(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		return Cache.TryGetValue(path, out LyricBundle? hit) ? hit : null;
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
		LyricMatchContext primary = MusicService.CurrentLyricContext;
		LogHelper.LogDebug($"开始拉歌词: {Path.GetFileName(path)} "
			+ $"ctx='{primary.Title}' artist='{primary.Artist}' src={primary.Platform}:{primary.PlatformId} dur={primary.DurationMs}ms");
		try
		{
			// 按平台路由歌词源，全部按「歌名+歌手」对齐去搜：
			//   netease = 网易云直连 ID/反查，失败回 LRCLIB；
			//   qq      = QQ 歌词（songmid），失败回 LRCLIB；
			//   kugou   = 酷狗歌词（hash），失败回 LRCLIB；
			//   bili / 本地文件 = 继续网易云反查，失败回 LRCLIB。
			string platform = primary.Platform ?? "";
			long? id = null;
			LyricBundle? bundle = null;
			LyricMatchContext used = primary;
			bool fromPlatform = false;
			string platformName = "";
			// 歌词/封面各自来自哪个平台，底栏的双色框按它上色
			MatchSource lyricSource = MatchSource.None;

			// ① 平台直取：qq/kugou 带 songmid/hash，直接用 ID 拿歌词，
			//    省掉一次搜索，也避免搜到别的版本。
			if (string.Equals(platform, "qq", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(primary.PlatformId))
			{
				bundle = await QqClient.GetLyricBundleAsync(primary.PlatformId!, ct);
				platformName = "QQ";
				fromPlatform = bundle != null && bundle.Count > 0;
				if (fromPlatform)
				{
					lyricSource = MatchSource.Qq;
					// 拿到了就立刻记来源：后面的翻译补全还要再走一轮网络，
					// 那一轮再慢/出错也不该让左半圈一直空着。
					bundle!.LyricSource = lyricSource;
					MusicService.NoteMatchSource(path, lyric: lyricSource);
				}
			}
			else if (string.Equals(platform, "kugou", StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(primary.PlatformId))
			{
				bundle = await KugouClient.GetLyricBundleAsync(primary.PlatformId!, ct);
				platformName = "酷狗";
				fromPlatform = bundle != null && bundle.Count > 0;
				if (fromPlatform)
				{
					lyricSource = MatchSource.Other;
					bundle!.LyricSource = lyricSource;
					MusicService.NoteMatchSource(path, lyric: lyricSource);
				}
			}

			if (ct.IsCancellationRequested || gen != _generation)
			{
				return null;
			}

			// ② 平台直取没捞到（或来源不是 qq/kugou），按候选歌名依次去网易云搜。
			//    多个候选是为了对付「文件名解析出来的歌手是错的」——网易云歌手
			//    对不上就宁可不给，这时换成不带歌手的候选往往就能搜到。命中即止。
			//    这一步同时替代了老代码里「qq/kugou 失败再单独反查一次」的分支，
			//    netease/本地/B站 也走同一条，逻辑只有一份。
			if (bundle == null || bundle.Count == 0)
			{
				foreach (LyricMatchContext cand in MusicService.CurrentLyricCandidates)
				{
					if (ct.IsCancellationRequested || gen != _generation)
					{
						return null;
					}
					NeteaseTrack? hit = await NeteaseApiClient.ResolveTrackAsync(cand, ct);
					if (ct.IsCancellationRequested || gen != _generation)
					{
						return null;
					}
					if (hit == null)
					{
						continue;
					}
					id = hit.Id;
					var nb = await NeteaseApiClient.GetLyricBundleAsync(hit.Id, ct);
					if (ct.IsCancellationRequested || gen != _generation)
					{
						return null;
					}
					if (nb != null && nb.Count > 0)
					{
						bundle = nb;
						used = cand;
						platformName = "网易云";
						fromPlatform = true;
						lyricSource = MatchSource.Netease;
						nb.LyricSource = lyricSource;
						MusicService.NoteMatchSource(path, lyric: lyricSource);
						ApplyMatchedCover(path, hit);
						break;
					}
				}
			}

			// ③ 网易云没出货 → 换 QQ 音乐再搜一遍（顺序固定：先网易云后 QQ）。
			//    网易云的曲库和歌词在某些歌上就是缺的（尤其日语/同人曲），
			//    这时 QQ 往往有，而且它的封面是公开 CDN，不需要登录。
			if (bundle == null || bundle.Count == 0)
			{
				QqPick pick = await TryQqAsync(path, ct, gen);
				if (pick.Bundle != null && pick.Bundle.Count > 0)
				{
					bundle = pick.Bundle;
					used = pick.Used;
					platformName = "QQ音乐";
					fromPlatform = true;
					lyricSource = MatchSource.Qq;
					// 同上：拿到歌词的当下就落来源，不等后面的补齐流程。
					bundle.LyricSource = lyricSource;
					MusicService.NoteMatchSource(path, lyric: lyricSource);
				}
			}

			// ④ 歌词有了但封面还空着：只去 QQ 补一张封面，不动歌词。
			//    网易云那首歌的专辑图可能压根没有（PicUrl 为空），
			//    这时左右两半就会是「红（歌词=网易云）+ 绿（封面=QQ）」。
			//    在线歌自带封面，这里只在真的没有封面时才去问 QQ，别白跑一趟。
			if (bundle != null && bundle.Count > 0)
			{
				SavedTrack? cur = MusicService.BuildCurrentTrack();
				if (string.IsNullOrWhiteSpace(cur?.Cover)
					&& string.IsNullOrEmpty(MusicService.GetLocalMatchedCover(path)))
				{
					await TryQqAsync(path, ct, gen, coverOnly: true);
				}
			}

			// 平台源只有行级时间轴，而且翻译经常是空的。
			// 这时候再去 LRCLIB 补一次：它覆盖面更广，还带翻译。
			// 用「命中的那个候选」去查，别拿错的歌手把 LRCLIB 也带偏。
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return null;
			}
			bool hadLyrics = bundle != null && bundle.Count > 0;
			bundle = await FillGapsAsync(bundle, used, ct);
			// 走到 LRCLIB 才有的歌词：既不是网易云也不是 QQ，界面上那一半画灰。
			if (!hadLyrics && bundle != null && bundle.Count > 0 && lyricSource == MatchSource.None)
			{
				lyricSource = MatchSource.Other;
			}
			// 来源一定要落在歌词包上：FillGaps 可能换回一个新包（整首来自 LRCLIB），
			// 那时候只有这个新包身上有来源信息，丢了就得靠下面的界面兜底去猜。
			if (bundle != null && bundle.Count > 0 && bundle.LyricSource == MatchSource.None)
			{
				bundle.LyricSource = lyricSource;
			}
			string source = fromPlatform ? platformName + "+LRCLIB" : "LRCLIB";
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return null;
			}
			if (bundle == null || bundle.Count == 0)
			{
				MarkMissing(path);
				LogHelper.LogDebug("两个源都没匹配到歌词: " + used.Title);
				return null;
			}
			// 记下「匹配到歌词的音乐名」，沉浸页标题只显示这个。
			bundle.DisplayTitle = string.IsNullOrWhiteSpace(used.Title) ? null : used.Title;
			bundle.DisplayArtist = string.IsNullOrWhiteSpace(used.Artist) ? null : used.Artist;
			LastError = null;
			// 先把来源记上再入缓存：界面随时可能来读，两边看到的状态要一致。
			MusicService.NoteMatchSource(path, lyric: lyricSource);
			SyncTimeScale(bundle, used);
			Put(path, bundle);
			int withTr = bundle.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Translation));
			int withChar = bundle.Lines.Count(l => l.CharStarts != null);
			LogHelper.LogDebug($"歌词就绪: {used.Title} artist={used.Artist} id={id} "
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

	/// <summary>本地文件按文件名命中网易云后，把那一版更准的歌名/歌手和专辑封面
	/// 记到 MusicService；沉浸页的音乐图标就会换成专辑图。</summary>
	private static void ApplyMatchedCover(string path, NeteaseTrack hit)
	{
		string cover = hit.AlbumInfo?.PicUrl ?? "";
		if (string.IsNullOrWhiteSpace(cover))
		{
			return;
		}
		MusicService.ApplyLocalMatchedMeta(path, hit.Name, hit.ArtistNames, cover, MatchSource.Netease);
	}

	private readonly record struct QqPick(LyricBundle? Bundle, LyricMatchContext Used);

	/// <summary>按候选歌名去 QQ 音乐搜一遍：命中就把封面记下来，
	/// 不是「只要封面」的模式还会顺便取歌词。
	/// 命中即止，候选一个个试（和网易云那条路的写法一致）。</summary>
	private async Task<QqPick> TryQqAsync(string path, CancellationToken ct, int gen, bool coverOnly = false)
	{
		foreach (LyricMatchContext cand in MusicService.CurrentLyricCandidates)
		{
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return default;
			}
			QqTrack? hit = await QqClient.ResolveTrackAsync(cand, ct);
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return default;
			}
			if (hit == null)
			{
				continue;
			}
			// 命中就先把封面记上：QQ 的专辑图是公开 CDN，不登录也能取，
			// 就算歌词拿不到，这一趟也不算白跑（底栏右半边会变绿）。
			MusicService.ApplyLocalMatchedMeta(path, hit.Name, hit.Singer, hit.CoverUrl, MatchSource.Qq);
			if (coverOnly)
			{
				return new QqPick(null, cand);
			}
			LyricBundle? qb = await QqClient.GetLyricBundleAsync(hit.Mid, ct);
			if (ct.IsCancellationRequested || gen != _generation)
			{
				return default;
			}
			if (qb != null && qb.Count > 0)
			{
				LogHelper.LogDebug($"QQ音乐匹配到歌词: {hit.Name} mid={hit.Mid} 行数={qb.Count}");
				return new QqPick(qb, cand);
			}
		}
		return default;
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

	/// <summary>按真在播的音频时长，把歌词时间轴整体换算一遍。
	///
	/// 为什么要这一步：歌词时间戳是按「歌词来源那一版」的时长记的，而真在播的
	/// 文件时长可能完全是另一回事（B 站下的版本、别的版本、母带长度不同）。
	/// 两者一对不上，时间差会随进度越滚越大——开头还对，播到一半整屏错开，
	/// 一拖进度条更是「秒跳一句」。
	///
	/// 期望值优先取歌曲元数据时长（歌词就是按它去搜/去挑版本的），
	/// 没有就退回歌词自己的时间轴终点。改动的百分比太小（&lt;2%）不动它，
	/// 免得为了几十毫秒的误差把整条轴搅乱。
	/// 幂等：每次都从零重算，重复调用不会叠加。</summary>
	private static void SyncTimeScale(LyricBundle? bundle, LyricMatchContext ctx)
	{
		if (bundle == null || bundle.Count == 0)
		{
			return;
		}
		long actual = 0;
		try
		{
			// 解码器刚接上时可能还没有时长，这时算不了，等歌词送到界面时再补一次。
			actual = (long)MusicService.Duration.TotalMilliseconds;
		}
		catch
		{
			actual = 0;
		}
		long expected = ctx.DurationMs > 0 ? ctx.DurationMs : bundle.TimelineEndMs;
		if (!bundle.ApplyDurationScale(actual, expected))
		{
			return;
		}
		LogHelper.LogDebug($"歌词按实际时长换算: {ctx.Title} 期望={expected}ms "
			+ $"实际={actual}ms 比例={bundle.TimeScale:0.000}");
	}

	private async Task RefreshAsync(string path, int gen)
	{
		LyricBundle? bundle = await GetAsync(path, _stop.Token);
		if (_disposed || gen != _generation)
		{
			return;
		}
		// 拉歌词时播放器可能还没报出时长（在线歌要先下载/解码完），
		// 所以送出给界面前再补一次换算。
		SyncTimeScale(bundle, MusicService.CurrentLyricContext);
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
