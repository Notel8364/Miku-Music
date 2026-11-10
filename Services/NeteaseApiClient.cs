using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>收藏夹拉取结果分类。网络层抛异常=断网/超时，业务码异常=登录失效，
/// 界面据此区分「Cookie 失效」和「网络失败」两种提示文案。网易云/QQ 两端共用。</summary>
public enum PlaylistFetchResult
{
	Ok,
	/// <summary>Cookie 失效 / 未登录（服务端业务码非 200，或解析不到账号信息）。</summary>
	CookieInvalid,
	/// <summary>网络异常（HTTP 请求本身抛了异常：断网、超时、DNS 等）。</summary>
	NetworkError,
}

/// <summary>网易云账号下的一个歌单（收藏夹条目）。只留展示要用的字段。</summary>
public sealed class NeteasePlaylistSummary
{
	public long Id { get; set; }

	public string Name { get; set; } = "";

	public int TrackCount { get; set; }

	public string CoverUrl { get; set; } = "";

	/// <summary>true = 收藏的别人的歌单，false = 自己创建的。
	/// 服务端把两种混在同一份列表里返回，这里只做标记，界面全部展示不打折。</summary>
	public bool Subscribed { get; set; }
}

public sealed class NeteaseTrack
{
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("artists")]
	public List<NeteaseArtist> Artists { get; set; } = new();

	/// <summary>cloudsearch 端点把歌手缩写成 <c>ar</c>，老端点才是 <c>artists</c>。</summary>
	[JsonPropertyName("ar")]
	public List<NeteaseArtist> ArtistsShort { get; set; } = new();

	/// <summary>两个端点里歌手总有一个是空的，所以对外只认拼写短的那个。</summary>
	public List<NeteaseArtist> ArtistList => Artists.Count > 0 ? Artists : ArtistsShort;

	[JsonPropertyName("album")]
	public NeteaseAlbum? Album { get; set; }

	/// <summary>cloudsearch 端点把专辑缩写成 <c>al</c>，老端点才是 <c>album</c>。
	/// 少了这个别名，走 cloudsearch 时搜索结果一条封面都取不到（全是灰图）。</summary>
	[JsonPropertyName("al")]
	public NeteaseAlbum? AlbumShort { get; set; }

	/// <summary>两个端点里专辑总有一个是空的，所以对外只读这个。</summary>
	public NeteaseAlbum? AlbumInfo => Album ?? AlbumShort;

	/// <summary>两个搜索端点（见 <see cref="NeteaseApiClient.SearchAsync"/>）把时长拼得不一样：
	/// cloudsearch 用缩写 <c>dt</c>，老端点用 <c>duration</c>。哪个来都行，
	/// 对外一律读 <see cref="DurationMs"/>。</summary>
	[JsonPropertyName("duration")]
	public long DurationMsRaw { get; set; }

	[JsonPropertyName("dt")]
	public long DurationMsShort { get; set; }

	public long DurationMs => DurationMsRaw > 0 ? DurationMsRaw : DurationMsShort;

	/// <summary>程序自己补时长时（<c>dt</c> 回填）走这里，写进正式那个字段。</summary>
	public void SetDurationMs(long value) => DurationMsRaw = value;

	[JsonPropertyName("fee")]
	public int Fee { get; set; }

	/// <summary>是否 VIP / 需购买。网易云的 fee：0 = 免费，1 = VIP，
	/// 4 = 购买过专辑，8 = 低音质免费。字段一直都在，只是之前没人读。</summary>
	public bool IsRestricted => Fee > 0;

	/// <summary>评论总数。搜索结果里没有这个字段（cloudsearch 只给 id/fee/pop 之类），
	/// 所以默认 -1 = 「还没问过」，0 = 问过了确实一条评论。
	/// 用 -1 而不是 0 是为了区分「没有数据」和「真的是 0」——
	/// 排序时 -1 一律排在最后，不会把没取到的条目顶到最前面。</summary>
	public long CommentCount { get; set; } = -1;

	public string ArtistNames => string.Join(" / ", ArtistList.ConvertAll(a => a.Name));

	public string DisplayName => $"{Name} - {ArtistNames}";

	public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

public sealed class NeteaseArtist
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";
}

public sealed class NeteaseAlbum
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("picUrl")]
	public string PicUrl { get; set; } = "";
}

public sealed class NeteaseSearchResult
{
	[JsonPropertyName("result")]
	public NeteaseSearchData? Data { get; set; }
}

public sealed class NeteaseSearchData
{
	[JsonPropertyName("songs")]
	public List<NeteaseTrack> Songs { get; set; } = new();

	[JsonPropertyName("songCount")]
	public int SongCount { get; set; }
}

public sealed class NeteaseSongUrlData
{
	[JsonPropertyName("data")]
	public List<NeteaseSongUrl> Data { get; set; } = new();
}

public sealed class NeteaseSongUrl
{
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[JsonPropertyName("url")]
	public string? Url { get; set; }

	[JsonPropertyName("br")]
	public int Bitrate { get; set; }

	[JsonPropertyName("type")]
	public string? Type { get; set; }

	[JsonPropertyName("code")]
	public int Code { get; set; }

	[JsonPropertyName("freeTrialInfo")]
	public NeteaseFreeTrial? FreeTrialInfo { get; set; }
}

public sealed class NeteaseFreeTrial
{
	[JsonPropertyName("start")]
	public int Start { get; set; }

	[JsonPropertyName("end")]
	public int End { get; set; }
}

   public sealed class NeteaseLyricResult
   {
       [JsonPropertyName("lrc")]
       public NeteaseLyricContent? Lrc { get; set; }

       // 翻译版。lv=-1&tv=-1 会一起带回来，之前只取 Lrc 就把它丢了。
       [JsonPropertyName("tlyric")]
       public NeteaseLyricContent? Tlyric { get; set; }

       // 逐字歌词（YRC）。有它才能做出 QQ 音乐那种「跟着唱」的一字一字描色。
       [JsonPropertyName("yrc")]
       public NeteaseLyricContent? Yrc { get; set; }
   }


public sealed class NeteaseLyricContent
{
	[JsonPropertyName("lyric")]
	public string Lyric { get; set; } = "";
}

public static class NeteaseApiClient
{
	/// <summary>当前网易云 Cookie。没登录就是空串。
	/// 发表评论前要用它判断「有没有登录」，缺了就不该让用户输入。</summary>
	public static string Cookie => SettingsManager.Instance.NeteaseCookie ?? "";

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(15)
	};

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "cache");

	static NeteaseApiClient()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
		Http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
	}

	/// <summary>网易云搜索。按顺序试几个端点，前面那个空了就换下一个。
	///
	/// 原来只用 <c>/api/search/get/web</c>，它现在稳定回 code=405
	/// （HTTP 200，但一条 song 都取不到），于是搜索框建议里的网易云永远是空的、
	/// 在线搜索页也搜不出东西 —— 看着像功能坏了，其实是被一个死端点卡住了。
	/// 活着的两个端点字段拼写不一样（<c>duration</c>/<c>artists</c> 对
	/// <c>dt</c>/<c>ar</c>），所以两种都收在 <see cref="NeteaseTrack"/> 里。</summary>
	public static async Task<List<NeteaseTrack>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(keyword)) return new List<NeteaseTrack>();
		string s = Uri.EscapeDataString(keyword.Trim());
		string[] endpoints =
		{
			$"https://music.163.com/api/cloudsearch/pc?s={s}&type=1&limit={limit}&offset={offset}",
			$"https://music.163.com/api/search/get?s={s}&type=1&limit={limit}&offset={offset}",
			$"https://music.163.com/api/search/get/web?s={s}&type=1&limit={limit}&offset={offset}"
		};
		foreach (string url in endpoints)
		{
			List<NeteaseTrack> songs = await TrySearchEndpointAsync(url, ct);
			if (songs.Count > 0)
			{
				// cloudsearch 端点偶发把同一首（同一 song id）在结果里返回两遍，
				// 展示层就出现「同首歌上下挨着两行」。这里按 id 掐掉重复，
				// 只留第一次出现的位置（服务端把它排前肯定有排序理由）。
				var seen = new HashSet<long>();
				var deduped = new List<NeteaseTrack>(songs.Count);
				foreach (var t in songs)
				{
					if (t.Id > 0 && seen.Add(t.Id))
					{
						deduped.Add(t);
					}
				}
				return deduped;
			}
		}
		return new List<NeteaseTrack>();
	}

	private static async Task<List<NeteaseTrack>> TrySearchEndpointAsync(string url, CancellationToken ct)
	{
		try
		{
			string json = await Http.GetStringAsync(url, ct);
			// 命中限流时这几个端点会回 {"code":405,...} 或 -460，
			// 而且偶尔会串到上一次请求的响应。两种都得挡掉，
			// 否则会把别的歌的结果当成匹配。
			if (json.Length < 200 || json.Contains("\"code\":405") || json.Contains("\"code\": -460")
				|| json.Contains("\"code\":-460"))
			{
				return new List<NeteaseTrack>();
			}
			var result = JsonSerializer.Deserialize<NeteaseSearchResult>(json);
			return result?.Data?.Songs ?? new List<NeteaseTrack>();
		}
		catch (OperationCanceledException)
		{
			// 用户还在改关键词，让上层去处理，别在这里当「这个端点挂了」换下一个。
			throw;
		}
		catch
		{
			// 这个端点挂了，换下一个试。
			return new List<NeteaseTrack>();
		}
	}

	public static async Task<string?> GetSongUrlAsync(long songId, CancellationToken ct = default)
	{
		try
		{
			var form = new FormUrlEncodedContent(new[]
			{
				new KeyValuePair<string, string>("ids", $"[{songId}]"),
				new KeyValuePair<string, string>("level", "exhigh"),
				new KeyValuePair<string, string>("encodeType", "aac"),
				new KeyValuePair<string, string>("csrf_token", "")
			});
			var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/song/enhance/player/url/v1")
			{
				Content = form
			};
		string? cookie = SettingsManager.Instance.NeteaseCookie;
			if (!string.IsNullOrWhiteSpace(cookie))
			{
				req.Headers.Add("Cookie", cookie);
			}
			var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			var data = JsonSerializer.Deserialize<NeteaseSongUrlData>(json);
			if (data?.Data != null && data.Data.Count > 0)
			{
				var songUrl = data.Data[0];
				if (songUrl.Code == 200 && !string.IsNullOrEmpty(songUrl.Url))
				{
					// 接口肯给直链 = 这首歌现在确实能听能下（有没有会员、是否已解禁
					// 全都算进去了），比 fee 字段准得多。
					TrackAccess.NotePlayable("netease", songId.ToString());
					return songUrl.Url;
				}
			}
		}
		catch
		{
			// 网络类的失败不知道是不是版权问题，别乱记「受限」
			return null;
		}
		// 走到这里说明接口明确拒绝了（VIP / 没版权 / 要买专辑）
		TrackAccess.NoteRestricted("netease", songId.ToString());
		return null;
	}

	/// <summary>一次把逐行歌词、翻译、逐字歌词都取回来。
	/// 字幕展示要三样：逐行时间轴用来滚动定位，翻译贴在下一行，
	/// 逐字时间轴用来做「唱到哪描到哪」。分开请求会多打几次接口，
	/// 所以合成一次拿。</summary>
	public static async Task<LyricBundle?> GetLyricBundleAsync(long songId, CancellationToken ct = default)
	{
		try
		{
			string url = $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&tv=-1&yv=-1&ytv=-1";
			string json = await Http.GetStringAsync(url, ct);
			NeteaseLyricResult? result = JsonSerializer.Deserialize<NeteaseLyricResult>(json);
			string lrc = result?.Lrc?.Lyric ?? "";
			string tlyric = result?.Tlyric?.Lyric ?? "";
			string yrc = result?.Yrc?.Lyric ?? "";
			if (string.IsNullOrWhiteSpace(lrc) && string.IsNullOrWhiteSpace(yrc))
			{
				return null;
			}
			LyricBundle bundle = LyricBundle.Parse(lrc, tlyric, yrc);
			// 把三份原始数据的分量记下来。「翻译不显示」有两种完全不同的原因：
			// 一是 tlyric 本来就是空的（这首歌网易云没收录译文），
			// 二是 tlyric 有内容但没对上时间轴。前者没救，后者是 bug，
			// 光看「带翻译=0」分不出来，所以在这里就把原始长度打出来。
			int withTr = bundle.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Translation));
			LogHelper.LogDebug($"[歌词] id={songId} lrc={lrc.Length} tlyric={tlyric.Length} "
				+ $"yrc={yrc.Length} -> 行数={bundle.Count} 带翻译={withTr}");
			return bundle;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "获取歌词失败: " + songId);
			return null;
		}
	}

	/// <summary>只要纯文本逐行歌词（老接口，保持兼容）。</summary>
	public static async Task<string?> GetLyricAsync(long songId, CancellationToken ct = default)
	{
		try
		{
			string url = $"https://music.163.com/api/song/lyric?id={songId}&lv=-1&tv=-1";
			string json = await Http.GetStringAsync(url, ct);
			var result = JsonSerializer.Deserialize<NeteaseLyricResult>(json);
			return result?.Lrc?.Lyric;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>用 <see cref="LyricMatchContext"/> 反查网易云的歌曲 ID。查不到返回 null。
	///
	/// 这是歌词匹配的主入口。分两种情况：
	/// <list type="number">
	/// <item>来源本身就是网易云：直接拿 ID 用，一次网络请求都不用发。
	/// 顺带保证拿到的是用户搜到的那一版，而不是「同名但另一个歌手」的版本。</item>
	/// <item>其他来源（酷狗/QQ/B 站/本地文件）：按歌名+歌手搜，
	/// 再用歌名必须完全一致、歌手至少一个对得上、时长接近三个条件筛。</item>
	/// </list></summary>
	public static async Task<long?> ResolveSongIdAsync(LyricMatchContext ctx, CancellationToken ct = default)
		=> (await ResolveTrackAsync(ctx, ct))?.Id;

	/// <summary>和 <see cref="ResolveSongIdAsync"/> 同一套匹配规则，但把整条搜索结果带回来。
	/// 歌词匹配成功后顺手用它的专辑图给本地文件补封面，省得再搜一次。</summary>
	public static async Task<NeteaseTrack?> ResolveTrackAsync(LyricMatchContext ctx, CancellationToken ct = default)
	{
		if (ctx.HasDirectId)
		{
			return long.TryParse(ctx.PlatformId, out long direct)
				? new NeteaseTrack { Id = direct }
				: null;
		}
		string title = CleanSearchKey(ctx.Title);
		string? artist = ctx.Artist == null ? null : CleanSearchKey(ctx.Artist);
		if (title.Length == 0)
		{
			return null;
		}
		try
		{
			List<NeteaseTrack> hits = await SearchAsync(
				string.IsNullOrEmpty(artist) ? title : title + " " + artist, 8, 0, ct);
			if (hits.Count == 0)
			{
				return null;
			}
			return PickBestMatch(title, artist, hits, ctx.DurationMs);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>用本地文件名反查网易云歌词。查不到返回 null。
	/// 只在歌名和歌手都对得上时才认，避免张冠李戴配出错歌词。</summary>
	public static async Task<string?> GuessLyricForLocalFileAsync(string filePath, CancellationToken ct = default)
	{
		long? id = await ResolveSongIdForLocalFileAsync(filePath, ct);
		if (id == null)
		{
			return null;
		}
		return await GetLyricAsync(id.Value, ct);
	}

	/// <summary>用本地文件名反查网易云的歌曲 ID。查不到返回 null。
	/// 字幕视图要拿到 ID 才能取逐行+翻译+逐字三份数据，所以把这步单独拆出来复用。</summary>
	public static async Task<long?> ResolveSongIdForLocalFileAsync(string filePath, CancellationToken ct = default)
	{
		try
		{
			string stem = Path.GetFileNameWithoutExtension(filePath);
			string title = stem;
			string? artist = null;
			// 常见命名 "歌手 - 歌名" / "歌名 - 歌手"
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
			title = CleanSearchKey(title);
			if (artist != null)
			{
				artist = CleanSearchKey(artist);
			}
			if (title.Length == 0)
			{
				return null;
			}
			List<NeteaseTrack> hits = await SearchAsync(
				string.IsNullOrEmpty(artist) ? title : title + " " + artist, 6, 0, ct);
			if (hits.Count == 0)
			{
				return null;
			}
			NeteaseTrack? best = PickBestMatch(title, artist, hits);
			return best?.Id;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>去掉文件名里的括号备注和画质/格式后缀。</summary>
	private static string CleanSearchKey(string s)
	{
		s = s.Trim();
		int cut = s.IndexOfAny(new[] { '(', '（', '[' });
		if (cut > 0)
		{
			s = s.Substring(0, cut).Trim();
		}
		// 常见的 "Live" / "Remix" / "Hi-Res" 之类尾巴留在原处，交给匹配阶段判断
		return s;
	}

	/// <summary>从候选里挑最像的一首。要求歌名完全一致；
	/// 歌手有值时也要求至少一个歌手名对得上，否则不认。</summary>
	private static NeteaseTrack? PickBestMatch(string title, string? artist, List<NeteaseTrack> hits)
		=> PickBestMatch(title, artist, hits, 0);

	/// <summary>带时长的版本。
	///
	/// 时长用来在「同歌名同歌手」的好几个候选（现场版/伴奏/翻唱版）里挑对的那一首。
	/// 只当排序用、不当硬条件：各平台报的时长有几百毫秒误差，
	/// 而且 B 站下载的剪辑版根本没有可比对的时长。
	/// 差得离谱（>15 秒）的直接排到最后，避免拿到一首长度完全不同的版本。</summary>
	private static NeteaseTrack? PickBestMatch(string title, string? artist, List<NeteaseTrack> hits, long wantMs)
	{
		NeteaseTrack? titleOnly = null;
		NeteaseTrack? bestByDuration = null;
		long bestDelta = long.MaxValue;
		foreach (NeteaseTrack t in hits)
		{
			string tName = CleanSearchKey(t.Name ?? "");
			if (!string.Equals(tName, title, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!string.IsNullOrEmpty(artist))
			{
				bool artistOk = false;
				foreach (NeteaseArtist a in t.Artists)
				{
					if (string.Equals(CleanSearchKey(a.Name ?? ""), artist, StringComparison.OrdinalIgnoreCase))
					{
						artistOk = true;
						break;
					}
				}
				if (!artistOk)
				{
					continue;
				}
			}
			if (wantMs > 0 && t.DurationMs > 0)
			{
				long delta = Math.Abs(t.DurationMs - wantMs);
				if (delta < bestDelta)
				{
					bestDelta = delta;
					bestByDuration = t;
				}
				if (delta <= 15000)
				{
					// 时长也基本对得上，这就是最可信的一个
					return t;
				}
			}
			titleOnly ??= t;
		}
		// 有时长信息且找到了接近的候选，就用时长挑出来的那个
		if (bestByDuration != null)
		{
			return bestByDuration;
		}
		// 歌手对不上就宁可不给，也不要配错
		return string.IsNullOrEmpty(artist) ? titleOnly : null;
	}

	public static async Task<string?> DownloadSongAsync(long songId, string? playUrl, Action<double>? progress = null, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(playUrl)) return null;
		try
		{
			string ext = DetectExtension(playUrl);
			string file = Path.Combine(CacheDir, $"{songId}.{ext}");
			string httpUrl = playUrl;
			if (httpUrl.StartsWith("http://"))
				httpUrl = "https" + httpUrl.Substring(4);
			// 走协调器：预取与真正播放共用同一个下载任务、先写 .part 再原子落盘，
			// 避免并发写同一文件抛异常，或半截文件被当成缓存命中。
			return await DownloadCoordinator.GetOrDownloadAsync(file, async part =>
			{
				using var response = await Http.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead, ct);
				response.EnsureSuccessStatusCode();
				long? total = response.Content.Headers.ContentLength;
				using var stream = await response.Content.ReadAsStreamAsync(ct);
				using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
				byte[] buf = new byte[65536];
				long readTotal = 0;
				int n;
				while ((n = await stream.ReadAsync(buf, 0, buf.Length, ct)) > 0)
				{
					await fs.WriteAsync(buf, 0, n, ct);
					readTotal += n;
					if (total > 0)
						progress?.Invoke((double)readTotal / total.Value);
				}
				return fs.Length >= 1024;
			});
		}
		catch
		{
			return null;
		}
	}

	public static void ClearCache()
	{
		try
		{
			if (Directory.Exists(CacheDir))
			{
				foreach (string f in Directory.GetFiles(CacheDir))
				{
					File.Delete(f);
				}
			}
		}
		catch
		{
		}
	}

	private static string DetectExtension(string url)
	{
		try
		{
			int q = url.IndexOf('?');
			string path = q >= 0 ? url.Substring(0, q) : url;
			string ext = Path.GetExtension(path).ToLowerInvariant();
			if (ext is ".m4a" or ".mp3" or ".flac" or ".aac" or ".ogg" or ".wav")
			{
				return ext.TrimStart('.');
			}
		}
		catch
		{
		}
		return "mp3";
	}

	// ---------- 二维码登录 ----------

	private static readonly Random QrRandom = new Random();

	private static string NextQrKey() => Guid.NewGuid().ToString("N");

	public static async Task<(bool ok, string unikey, string qrurl, string? error)> LoginQrStartAsync(CancellationToken ct = default)
	{
		try
		{
			string key = NextQrKey();
			var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("key", key) });
			var resp = await Http.PostAsync("https://music.163.com/api/login/qrcode/unikey", form, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			LogHelper.LogDebug("网易云扫码 key: " + json.Substring(0, Math.Min(220, json.Length)));
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 200
				&& doc.RootElement.TryGetProperty("unikey", out var uk))
			{
				string unikey = uk.GetString() ?? "";
				string qrurl = "https://music.163.com/login?codekey=" + unikey;
				return (true, unikey, qrurl, null);
			}
			return (false, "", "", "unikey 获取失败");
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云扫码启动异常: " + ex.Message);
			return (false, "", "", ex.Message);
		}
	}

	/// <summary>轮询扫码结果。code: 800 过期 / 801 未扫 / 802 已扫待确认 / 803 登录成功(返回 cookie)。</summary>
	public static async Task<(int code, string? cookie, string? message)> LoginQrPollAsync(string unikey, CancellationToken ct = default)
	{
		try
		{
			var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("key", unikey) });
			var req = new HttpRequestMessage(HttpMethod.Put, "https://music.163.com/api/login/qrcode/client/login")
			{
				Content = form
			};
			var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(json);
			int code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
			string? message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
			string? cookie = null;
			if (doc.RootElement.TryGetProperty("cookie", out var ck))
			{
				cookie = NormalizeCookie(ck.GetString());
			}
			return (code, cookie, message);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云扫码轮询异常: " + ex.Message);
			return (-1, null, ex.Message);
		}
	}

	/// <summary>从 API 返回的 cookie 文本中提取 name=value 对。</summary>
	public static string NormalizeCookie(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "";
		}
		var parts = new List<string>();
		foreach (string seg in raw.Split(';'))
		{
			string s = seg.Trim();
			int eq = s.IndexOf('=');
			if (eq <= 0) continue;
			string name = s.Substring(0, eq).Trim();
			string value = s.Substring(eq + 1).Trim();
			if (value.Length == 0) continue;
			bool valid = true;
			foreach (char ch in name)
			{
				if (!char.IsLetterOrDigit(ch) && ch != '_')
				{
					valid = false;
					break;
				}
			}
			if (!valid) continue;
			if (value.Length > 0 && value.All(c => c == ' ')) continue;
			parts.Add(name + "=" + value);
		}
		return string.Join("; ", parts);
	}

	public static async Task<(bool ok, long uid, string nickname)> FetchAccountAsync(string cookie, CancellationToken ct = default)
	{
		try
		{
			var req = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/api/nuser/account/get");
			if (!string.IsNullOrWhiteSpace(cookie)) req.Headers.Add("Cookie", cookie);
			var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("profile", out var profile))
			{
				long uid = profile.TryGetProperty("userId", out var uidEl) ? uidEl.GetInt64() : 0;
				string nick = profile.TryGetProperty("nickname", out var nickEl) ? nickEl.GetString() ?? "" : "";
				return (uid > 0, uid, nick);
			}
			return (false, 0, "");
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云账号信息异常: " + ex.Message);
			return (false, 0, "");
		}
	}

	/// <summary>加载当前账号「我喜欢的音乐」及全部歌单的歌曲。
	/// 歌单列表分页拉全（limit/offset 直到 more=false），
	/// 「我喜欢」（specialType==5）排最前；每个歌单先取 v6/playlist/detail，
	/// 该接口的 tracks 对大歌单只给一截，但 trackIds 是全量，
	/// 缺的部分按 200 个一批走 v3/song/detail 补齐。需要 cookie。</summary>
	public static async Task<List<NeteaseTrack>?> FetchMySongsAsync(string cookie, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return null;
		}
		try
		{
			var (ok, uid, _) = await FetchAccountAsync(cookie, ct);
			if (!ok || uid <= 0)
			{
				return null;
			}

			// 1) 全量歌单 id（分页）。「我喜欢」永远排第一。
			var playlistIds = new List<long>();
			int offset = 0;
			while (offset < 3000)
			{
				var req = new HttpRequestMessage(HttpMethod.Get,
					"https://music.163.com/api/user/playlist?uid=" + uid + "&limit=100&offset=" + offset);
				req.Headers.Add("Cookie", cookie);
				string json = await (await Http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct);
				int got = 0;
				bool more = false;
				using (var doc = JsonDocument.Parse(json))
				{
					if (!doc.RootElement.TryGetProperty("playlist", out var pl))
					{
						break;
					}
					foreach (var item in pl.EnumerateArray())
					{
						long id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
						int specialType = item.TryGetProperty("specialType", out var st) ? st.GetInt32() : 0;
						if (id <= 0) continue;
						if (specialType == 5)
						{
							playlistIds.Insert(0, id);
						}
						else
						{
							playlistIds.Add(id);
						}
						got++;
					}
					more = doc.RootElement.TryGetProperty("more", out var moreEl) && moreEl.ValueKind == JsonValueKind.True;
				}
				offset += got;
				if (!more || got == 0)
				{
					break;
				}
			}
			playlistIds = playlistIds.Distinct().ToList();

			// 2) 逐个歌单拉歌，跨歌单按 id 去重（同一首歌在多个歌单里只留一份）。
			var tracks = new List<NeteaseTrack>();
			var seen = new HashSet<long>();
			foreach (long pid in playlistIds)
			{
				await CollectPlaylistTracksAsync(pid, cookie, tracks, seen, ct);
			}
			return tracks;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云收藏加载异常: " + ex.Message);
			return null;
		}
	}

	/// <summary>拉一个歌单的全部歌曲：v6/playlist/detail 的 trackIds 是全量，
	/// tracks 可能只带一截；缺的按 200 个一批走 v3/song/detail 补齐。</summary>
	private static async Task CollectPlaylistTracksAsync(long pid, string cookie, List<NeteaseTrack> tracks, HashSet<long> seen, CancellationToken ct)
	{
		var byId = new Dictionary<long, NeteaseTrack>();
		var allIds = new List<long>();
		try
		{
			var req = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/api/v6/playlist/detail?id=" + pid);
			req.Headers.Add("Cookie", cookie);
			string json = await (await Http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("playlist", out var pl))
			{
				return;
			}
			if (pl.TryGetProperty("trackIds", out var ids))
			{
				foreach (var x in ids.EnumerateArray())
				{
					if (x.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
					{
						long id = idEl.GetInt64();
						if (id > 0 && !seen.Contains(id))
						{
							allIds.Add(id);
						}
					}
				}
			}
			if (pl.TryGetProperty("tracks", out var tr))
			{
				foreach (var item in tr.EnumerateArray())
				{
					var t = ParseNeteaseTrack(item);
					if (t != null && !byId.ContainsKey(t.Id))
					{
						byId[t.Id] = t;
					}
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云歌单详情异常 pid=" + pid + ": " + ex.Message);
			return;
		}

		// tracks 没盖全的部分批量补。
		var missing = allIds.Where(id => !byId.ContainsKey(id)).ToList();
		for (int i = 0; i < missing.Count; i += 200)
		{
			var chunk = missing.Skip(i).Take(200).ToList();
			try
			{
				string c = "[" + string.Join(",", chunk.Select(id => "{\"id\":" + id + "}")) + "]";
				var req = new HttpRequestMessage(HttpMethod.Get,
					"https://music.163.com/api/v3/song/detail?c=" + Uri.EscapeDataString(c));
				req.Headers.Add("Cookie", cookie);
				string json = await (await Http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("songs", out var songs))
				{
					foreach (var item in songs.EnumerateArray())
					{
						var t = ParseNeteaseTrack(item);
						if (t != null)
						{
							byId[t.Id] = t;
						}
					}
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("网易云歌曲批量补全异常: " + ex.Message);
			}
		}

		foreach (long id in allIds)
		{
			if (byId.TryGetValue(id, out var t) && seen.Add(id))
			{
				tracks.Add(t);
			}
		}
	}

	/// <summary>从歌单/详情接口的 track 对象解析出 NeteaseTrack（ar/al/dt/fee 同构）。</summary>
	private static NeteaseTrack? ParseNeteaseTrack(JsonElement item)
	{
		var t = new NeteaseTrack();
		if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
		{
			t.Id = idEl.GetInt64();
		}
		if (t.Id <= 0)
		{
			return null;
		}
		if (item.TryGetProperty("name", out var nm))
		{
			t.Name = nm.GetString() ?? "";
		}
		if (item.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.Number)
		{
			t.SetDurationMs(dt.GetInt64());
		}
		if (item.TryGetProperty("fee", out var fee) && fee.ValueKind == JsonValueKind.Number)
		{
			t.Fee = fee.GetInt32();
		}
		var artists = new List<NeteaseArtist>();
		if (item.TryGetProperty("ar", out var ars) || item.TryGetProperty("artists", out ars))
		{
			foreach (var a in ars.EnumerateArray())
			{
				if (a.TryGetProperty("name", out var an))
				{
					artists.Add(new NeteaseArtist { Name = an.GetString() ?? "" });
				}
			}
		}
		t.Artists = artists;
		if (item.TryGetProperty("al", out var al) || item.TryGetProperty("album", out al))
		{
			var album = new NeteaseAlbum();
			if (al.TryGetProperty("name", out var aln))
			{
				album.Name = aln.GetString() ?? "";
			}
			if (al.TryGetProperty("picUrl", out var pic))
			{
				album.PicUrl = pic.GetString() ?? "";
			}
			t.Album = album;
		}
		if (string.IsNullOrWhiteSpace(t.Name) || artists.Any(a => string.IsNullOrWhiteSpace(a.Name)))
		{
			return null;
		}
		return t;
	}

	public static void ClearCookieCache()
	{
		ClearCache();
		try
		{
			string kugou = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBox", "cache", "kugou");
			if (Directory.Exists(kugou))
			{
				foreach (string f in Directory.GetFiles(kugou)) File.Delete(f);
			}
		}
		catch
		{
		}
	}

	// ---------- 账号收藏夹（eapi 接口） ----------
	// 结果分类枚举 PlaylistFetchResult 和列表条目 NeteasePlaylistSummary
	// 放在命名空间层（文件顶部），和 NeteaseTrack 一样，QQ 端/界面也要用。

	/// <summary>eapi 请求要补齐的客户端环境字段。官方 PC 客户端调 eapi 时
	/// 会带上 os/appver/appid 这类环境信息，服务端对缺这些字段的请求可能按异常流量处理，
	/// 所以按官方客户端的风格补上（用户 Cookie 里已有的同名键不覆盖、不重复）。</summary>
	private const string EapiClientFields = "os=pc; appver=2.10.13; appid=160589688";

	/// <summary>把用户保存的 Cookie 和 <see cref="EapiClientFields"/> 拼成一条。
	/// MUSIC_U（登录凭证）必须还在里面——歌单接口完全靠它认人。</summary>
	private static string BuildEapiCookie(string cookie)
	{
		string merged = (cookie ?? "").Trim();
		var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string seg in merged.Split(';'))
		{
			int eq = seg.IndexOf('=');
			if (eq > 0) existing.Add(seg.Substring(0, eq).Trim());
		}
		foreach (string seg in EapiClientFields.Split(';'))
		{
			int eq = seg.IndexOf('=');
			if (eq <= 0) continue;
			string name = seg.Substring(0, eq).Trim();
			if (existing.Contains(name)) continue; // 用户自己带的字段优先，避免同键冲突
			merged = merged.Length > 0 ? merged + "; " + seg.Trim() : seg.Trim();
		}
		return merged;
	}

	/// <summary>eapi 加密（2026-11-04 实测修正版）。
	/// 本机用 PowerShell 对真实 Cookie 逐步验证过的组合：
	/// ① key 是 e82ckenh8dichen8（cloudmusic.dll 里找到的 ebcb06802bb66cd9 是旧版常量，
	///    服务端对它一律回 HTTP 200 空响应体，表现为「网络异常」假象）；
	/// ② 加密明文 = 「/api/形式的路径 + -36cd479b6b5- + 参数JSON + -36cd479b6b5- + md5摘要」，
	///    摘要 = md5("nobody{api路径}use{json}md5forencrypt")——JSON 本身必须在明文里，
	///    只加密「路径+摘要」或「路径+JSON」服务端都解不开；
	/// ③ 请求 URL 用 /eapi/ 路径，但加密明文里的路径必须是 /api/ 形式，否则路由返回 404 接口未找到。
	/// 这三条任何一条错了，服务端的反应都是空响应或 404，不会报具体错误。</summary>
	private static string EapiEncrypt(string apiPath, string jsonParams)
	{
		string digest;
		using (var md5 = MD5.Create())
		{
			string message = "nobody" + apiPath + "use" + jsonParams + "md5forencrypt";
			digest = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
		}
		string data = apiPath + "-36cd479b6b5-" + jsonParams + "-36cd479b6b5-" + digest;
		byte[] plain = Encoding.UTF8.GetBytes(data);
		using var aes = Aes.Create();
		aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.PKCS7;
		using var encryptor = aes.CreateEncryptor();
		byte[] cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
		return Convert.ToHexString(cipher); // .NET 默认输出大写 hex，正好符合要求
	}

	/// <summary>发一次 eapi 请求（POST https://music.163.com{path}，body 为 params=加密hex）。
	/// 任何失败（断网/超时/解析不了）都记日志后返回 null，由调用方归类成「网络失败」——
	/// 之前 HTTP 层异常是直接往外抛的，收藏夹整条链路就这一处会静默，
	/// 用户说「点了没反应」时日志里连一行痕迹都没有，根本没法排查。</summary>
	private static async Task<JsonDocument?> PostEapiAsync(string path, string jsonParams, string cookie, CancellationToken ct)
	{
		try
		{
			// URL 用 /eapi/ 路径，但加密明文里必须是 /api/ 形式，否则服务端路由 404；
			// 网关实测 interface.music.163.com 稳定，music.163.com 对部分 eapi 路径不路由
			string apiPath = path.Replace("/eapi/", "/api/");
			var req = new HttpRequestMessage(HttpMethod.Post, "https://interface.music.163.com" + path)
			{
				Content = new StringContent("params=" + EapiEncrypt(apiPath, jsonParams), Encoding.UTF8, "application/x-www-form-urlencoded")
			};
			string mergedCookie = BuildEapiCookie(cookie);
			if (mergedCookie.Length > 0) req.Headers.Add("Cookie", mergedCookie);
			var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			if (!resp.IsSuccessStatusCode)
			{
				// 非 200 也要留痕：状态码 + 响应开头，方便判断是风控还是域名问题
				LogHelper.LogDebug("eapi " + path + " HTTP " + (int)resp.StatusCode + " 响应: " + json.Substring(0, Math.Min(200, json.Length)));
				return null;
			}
			LogHelper.LogDebug("eapi " + path + " 响应: " + json.Substring(0, Math.Min(200, json.Length)));
			return JsonDocument.Parse(json);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("eapi " + path + " 失败: " + ex.GetType().Name + " " + ex.Message);
			return null;
		}
	}

	/// <summary>把网易云的业务码归类成收藏夹结果。code=200 才算成功，
	/// 其余（301 未登录等）一律按 Cookie 失效提示，别让用户对着「网络错误」排查登录问题。</summary>
	private static PlaylistFetchResult NeteaseCodeToResult(JsonDocument? doc)
	{
		if (doc == null) return PlaylistFetchResult.NetworkError;
		if (doc.RootElement.TryGetProperty("code", out var c)
			&& c.ValueKind == JsonValueKind.Number && c.GetInt32() != 200)
		{
			return PlaylistFetchResult.CookieInvalid;
		}
		return PlaylistFetchResult.Ok;
	}

	/// <summary>拉取登录账号在网易云下的全部歌单（收藏夹）。需要已保存 Cookie。
	/// 流程：先 eapi /eapi/w/nuser/account/get（参数为空对象，登录态全在 Cookie 里）拿 uid，
	/// 再 eapi /eapi/user/playlist 按 uid 列歌单。自建歌单和收藏的歌单在同一个列表里，
	/// 用 subscribed 字段区分但全部展示。</summary>
	public static async Task<(PlaylistFetchResult result, List<NeteasePlaylistSummary> playlists)> FetchPlaylistsAsync(CancellationToken ct = default)
	{
		string cookie = Cookie;
		var empty = new List<NeteasePlaylistSummary>();
		if (string.IsNullOrWhiteSpace(cookie))
		{
			LogHelper.LogDebug("收藏夹: 本地没有网易云 Cookie，直接按失效返回");
			return (PlaylistFetchResult.CookieInvalid, empty);
		}
		LogHelper.LogDebug("收藏夹: 开始拉取网易云歌单，Cookie 长度=" + cookie.Length);
		try
		{
			using var accDoc = await PostEapiAsync("/eapi/w/nuser/account/get", "{}", cookie, ct);
			var r1 = NeteaseCodeToResult(accDoc);
			if (r1 != PlaylistFetchResult.Ok) return (r1, empty);
			long uid = 0;
			if (accDoc!.RootElement.TryGetProperty("profile", out var profile)
				&& profile.TryGetProperty("userId", out var uidEl))
			{
				uid = uidEl.GetInt64();
			}
			if (uid <= 0)
			{
				LogHelper.LogDebug("收藏夹: account/get 响应里没拿到 userId（响应开头: " +
					accDoc.RootElement.ToString().Substring(0, Math.Min(200, accDoc.RootElement.ToString().Length)) + "）");
				return (PlaylistFetchResult.CookieInvalid, empty);
			}
			LogHelper.LogDebug("收藏夹: uid=" + uid);

			// uid 参数是纯数字，手工拼 JSON 不会有转义问题；limit=100 一页足够展示绝大多数账号
			using var plDoc = await PostEapiAsync("/eapi/user/playlist",
				"{\"uid\":" + uid + ",\"limit\":100,\"offset\":0}", cookie, ct);
			var r2 = NeteaseCodeToResult(plDoc);
			if (r2 != PlaylistFetchResult.Ok) return (r2, empty);

			var list = new List<NeteasePlaylistSummary>();
			if (plDoc!.RootElement.TryGetProperty("playlist", out var arr) && arr.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in arr.EnumerateArray())
				{
					var s = new NeteasePlaylistSummary
					{
						Id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0,
						Name = item.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "",
						TrackCount = item.TryGetProperty("trackCount", out var tcEl) && tcEl.ValueKind == JsonValueKind.Number ? tcEl.GetInt32() : 0,
						CoverUrl = item.TryGetProperty("coverImgUrl", out var cvEl) ? cvEl.GetString() ?? "" : "",
						Subscribed = item.TryGetProperty("subscribed", out var sbEl) && sbEl.ValueKind == JsonValueKind.True,
					};
					if (s.Id > 0) list.Add(s);
				}
			}
			return (PlaylistFetchResult.Ok, list);
		}
		catch (HttpRequestException)
		{
			return (PlaylistFetchResult.NetworkError, empty);
		}
		catch (TaskCanceledException)
		{
			return (PlaylistFetchResult.NetworkError, empty);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云收藏夹列表异常: " + ex.Message);
			return (PlaylistFetchResult.NetworkError, empty);
		}
	}

	/// <summary>拉网易云账号昵称：eapi /eapi/w/nuser/account/get（参数空对象，
	/// 登录态全在 Cookie 里）→ 响应 profile.nickname。收藏夹收缩栏标题行用。
	/// 没登录 / 服务端拒绝 / 字段解析不到一律返回空串，调用方按「无账号名」
	/// 降级（标题行不显示昵称），不弹错误 —— 昵称只是展示性信息，不值得打扰。</summary>
	public static async Task<string> FetchAccountNameAsync(CancellationToken ct = default)
	{
		string cookie = Cookie;
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return "";
		}
		try
		{
			using var doc = await PostEapiAsync("/eapi/w/nuser/account/get", "{}", cookie, ct);
			if (doc != null
				&& doc.RootElement.TryGetProperty("profile", out var profile)
				&& profile.ValueKind == JsonValueKind.Object
				&& profile.TryGetProperty("nickname", out var nick)
				&& nick.ValueKind == JsonValueKind.String)
			{
				return nick.GetString() ?? "";
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云账号昵称获取失败: " + ex.Message);
		}
		return "";
	}

	/// <summary>拉一个歌单里的全部曲目（按歌单原顺序）。需要已保存 Cookie。
	/// eapi /eapi/v6/playlist/detail 的响应里 trackIds 一定有（[{id,...}] 数组），
	/// 完整 tracks 则不一定（大歌单会被服务端裁掉），所以统一走「先收 trackIds，
	/// 再批量调 /eapi/v3/song/detail 把 id 换成完整曲目」的路子。
	/// v3/song/detail 的参数按官方客户端的格式：c 是「JSON 数组再序列化一遍」的字符串，
	/// ids 是纯数字数组字符串。</summary>
	public static async Task<(PlaylistFetchResult result, List<NeteaseTrack> tracks)> FetchPlaylistTracksAsync(long playlistId, CancellationToken ct = default)
	{
		string cookie = Cookie;
		var empty = new List<NeteaseTrack>();
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return (PlaylistFetchResult.CookieInvalid, empty);
		}
		try
		{
			// n=1000：让服务端把 trackIds 尽量给全。
			// ConfigureAwait(false)：1800 首的大歌单要走 9 批 v3/song/detail，
			// 每批几百 KB 的 JSON 解析 + 反序列化绝不能回到 UI 线程上做
			//（实测 1800 首歌单 UI 卡死 24 秒，日志 10:25:01→10:25:26）。
			// 本方法不碰任何 UI，解析/重排全程留在线程池，最后一并交付。
			using var doc = await PostEapiAsync("/eapi/v6/playlist/detail",
				"{\"id\":" + playlistId + ",\"n\":1000}", cookie, ct).ConfigureAwait(false);
			var r = NeteaseCodeToResult(doc);
			if (r != PlaylistFetchResult.Ok) return (r, empty);

			var idList = new List<long>();
			if (doc!.RootElement.TryGetProperty("playlist", out var pl)
				&& pl.TryGetProperty("trackIds", out var tids) && tids.ValueKind == JsonValueKind.Array)
			{
				foreach (var t in tids.EnumerateArray())
				{
					if (t.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long id) && id > 0)
					{
						idList.Add(id);
					}
				}
			}
			if (idList.Count == 0) return (PlaylistFetchResult.Ok, empty);

			// 去重：同一首被服务端返回两遍时，展示层会撞重复 key，先掐掉
			idList = idList.Distinct().ToList();

			// 批量换详情。参数串太长容易被网关拒，按 200 个一批分片请求
			var fetched = new List<NeteaseTrack>(idList.Count);
			foreach (long[] chunk in idList.Chunk(200))
			{
				string c = JsonSerializer.Serialize(chunk.Select(id => new Dictionary<string, long> { ["id"] = id }));
				string ids = "[" + string.Join(",", chunk) + "]";
				using var d2 = await PostEapiAsync("/eapi/v3/song/detail",
					JsonSerializer.Serialize(new Dictionary<string, string> { ["c"] = c, ["ids"] = ids }), cookie, ct).ConfigureAwait(false);
				var r2 = NeteaseCodeToResult(d2);
				if (r2 != PlaylistFetchResult.Ok) return (r2, empty);
				if (d2!.RootElement.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
				{
					foreach (var s in songs.EnumerateArray())
					{
						var nt = s.Deserialize<NeteaseTrack>();
						if (nt != null && nt.Id > 0 && !string.IsNullOrWhiteSpace(nt.Name))
						{
							fetched.Add(nt);
						}
					}
				}
			}

			// v3/song/detail 对已下架歌曲会悄悄漏掉几首，返回顺序也可能和 trackIds 不一致；
			// 按 trackIds 的原顺序重排，保证和手机/网页端看到的歌单顺序一致
			var byId = fetched.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
			var ordered = idList.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
			return (PlaylistFetchResult.Ok, ordered);
		}
		catch (HttpRequestException)
		{
			return (PlaylistFetchResult.NetworkError, empty);
		}
		catch (TaskCanceledException)
		{
			return (PlaylistFetchResult.NetworkError, empty);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云歌单曲目加载异常: " + ex.Message);
			return (PlaylistFetchResult.NetworkError, empty);
		}
	}
}
