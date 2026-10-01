using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MusicBox.Settings;

namespace MusicBox.Services;

public sealed class NeteaseTrack
{
	[JsonPropertyName("id")]
	public long Id { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("artists")]
	public List<NeteaseArtist> Artists { get; set; } = new();

	[JsonPropertyName("album")]
	public NeteaseAlbum? Album { get; set; }

	[JsonPropertyName("duration")]
	public long DurationMs { get; set; }

	[JsonPropertyName("fee")]
	public int Fee { get; set; }

	/// <summary>是否 VIP / 需购买。网易云的 fee：0 = 免费，1 = VIP，
	/// 4 = 购买过专辑，8 = 低音质免费。字段一直都在，只是之前没人读。</summary>
	public bool IsRestricted => Fee > 0;

	public string ArtistNames => string.Join(" / ", Artists.ConvertAll(a => a.Name));

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

	public static async Task<List<NeteaseTrack>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(keyword)) return new List<NeteaseTrack>();
		try
		{
			string s = Uri.EscapeDataString(keyword.Trim());
			string url = $"https://music.163.com/api/search/get/web?s={s}&type=1&limit={limit}&offset={offset}";
			string json = await Http.GetStringAsync(url, ct);
			// 命中限流时这个端点会回 {"code":405,...}，
			// 而且偶尔会串到上一次请求的响应。两种都得挡掉，
			// 否则会把别的歌的结果当成匹配。
			if (json.Length < 200 || json.Contains("\"code\":405") || json.Contains("\"code\": -460")
				|| json.Contains("\"code\":-460"))
			{
				return new List<NeteaseTrack>();
			}
			var result = JsonSerializer.Deserialize<NeteaseSearchResult>(json);
			var songs = result?.Data?.Songs;
			return songs ?? new List<NeteaseTrack>();
		}
		catch
		{
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
					return songUrl.Url;
				}
			}
		}
		catch
		{
		}
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
	{
		if (ctx.HasDirectId)
		{
			return long.Parse(ctx.PlatformId!);
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
			NeteaseTrack? best = PickBestMatch(title, artist, hits, ctx.DurationMs);
			return best?.Id;
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
			Directory.CreateDirectory(CacheDir);
			string ext = DetectExtension(playUrl);
			string file = Path.Combine(CacheDir, $"{songId}.{ext}");
			if (File.Exists(file) && new FileInfo(file).Length > 1024)
			{
				return file;
			}
			string httpUrl = playUrl;
			if (httpUrl.StartsWith("http://"))
				httpUrl = "https" + httpUrl.Substring(4);
			using var response = await Http.GetAsync(httpUrl, HttpCompletionOption.ResponseHeadersRead, ct);
			response.EnsureSuccessStatusCode();
			long? total = response.Content.Headers.ContentLength;
			using var stream = await response.Content.ReadAsStreamAsync(ct);
			using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
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
			if (fs.Length < 1024)
			{
				File.Delete(file);
				return null;
			}
			return file;
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

	/// <summary>加载当前账号「我喜欢的音乐」及第一个歌单的歌曲（优先我喜欢）。需要 cookie。</summary>
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
			List<long> playlistIds = new List<long>();
			var req = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/api/user/playlist?uid=" + uid + "&limit=100");
			req.Headers.Add("Cookie", cookie);
			var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			using (var doc = JsonDocument.Parse(json))
			{
				if (doc.RootElement.TryGetProperty("playlist", out var pl))
				{
					long? favId = null;
					long? firstId = null;
					foreach (var item in pl.EnumerateArray())
					{
						long id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
						int specialType = item.TryGetProperty("specialType", out var st) ? st.GetInt32() : 0;
						string pname = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
						firstId ??= id;
						if (specialType == 5 || (pname.IndexOf("我喜欢", StringComparison.Ordinal) >= 0 && id > 0))
						{
							favId = id;
							break;
						}
					}
					if (favId.HasValue) playlistIds.Add(favId.Value);
					if (firstId.HasValue && !playlistIds.Contains(firstId.Value)) playlistIds.Add(firstId.Value);
				}
			}
			if (playlistIds.Count == 0)
			{
				return new List<NeteaseTrack>();
			}
			var tracks = new List<NeteaseTrack>();
			foreach (long pid in playlistIds)
			{
				var req2 = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/api/v6/playlist/detail?id=" + pid);
				req2.Headers.Add("Cookie", cookie);
				var resp2 = await Http.SendAsync(req2, ct);
				string json2 = await resp2.Content.ReadAsStringAsync(ct);
				using var doc2 = JsonDocument.Parse(json2);
				if (!doc2.RootElement.TryGetProperty("playlist", out var pl2)
					|| !pl2.TryGetProperty("tracks", out var tr))
				{
					continue;
				}
				foreach (var item in tr.EnumerateArray())
				{
					NeteaseTrack t = new NeteaseTrack();
					if (item.TryGetProperty("id", out var idEl)) t.Id = idEl.GetInt64();
					if (item.TryGetProperty("name", out var nm)) t.Name = nm.GetString() ?? "";
					if (item.TryGetProperty("dt", out var dt)) t.DurationMs = dt.GetInt64();
					if (item.TryGetProperty("fee", out var fee)) t.Fee = fee.GetInt32();
					var artists = new List<NeteaseArtist>();
					if (item.TryGetProperty("ar", out var ars))
					{
						foreach (var a in ars.EnumerateArray())
						{
							if (a.TryGetProperty("name", out var an))
								artists.Add(new NeteaseArtist { Name = an.GetString() ?? "" });
						}
					}
					t.Artists = artists;
					if (item.TryGetProperty("al", out var al))
					{
						var album = new NeteaseAlbum();
						if (al.TryGetProperty("name", out var aln)) album.Name = aln.GetString() ?? "";
						if (al.TryGetProperty("picUrl", out var pic)) album.PicUrl = pic.GetString() ?? "";
						t.Album = album;
					}
					if (t.Id > 0 && !string.IsNullOrWhiteSpace(t.Name) && !artists.Any(track => string.IsNullOrWhiteSpace(track.Name)))
					{
						tracks.Add(t);
					}
				}
				if (tracks.Count > 0) break;
			}
			return tracks;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("网易云收藏加载异常: " + ex.Message);
			return null;
		}
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
}
