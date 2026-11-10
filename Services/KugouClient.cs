using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using MusicBox.Settings;

namespace MusicBox.Services;

public sealed class KugouTrack
{
	public string Hash { get; set; } = "";

	public string FileName { get; set; } = "";

	public string SingerName { get; set; } = "";

	public string AlbumName { get; set; } = "";

	public int Duration { get; set; }

	public long AlbumId { get; set; }

	public string ImgUrl { get; set; } = "";

	public long FileSize { get; set; }

	public string Ext { get; set; } = "mp3";

	/// <summary>评论数。搜索接口不一定给，给了才用。</summary>
	public long CommentCount { get; set; }

	/// <summary>酷狗的收费标记。实测：免费曲 pay_type=0，
	/// 付费/VIP 曲 pay_type=1 或 3（3 还带 320k 付费位）。
	/// 0 = 免费，&gt;0 = 受限。</summary>
	public int PayType { get; set; }

	/// <summary>是否 VIP / 版权受限。搜索时就能判定，
	/// 不用等播放失败才发现下不了。</summary>
	public bool IsRestricted => PayType > 0;

	public long DurationMs => Duration * 1000L;

	public string Display
	{
		get
		{
			string name = FileName;
			string suffix = " - " + SingerName;
			if (!string.IsNullOrEmpty(SingerName) && name.EndsWith(suffix, StringComparison.Ordinal))
			{
				name = name.Substring(0, name.Length - suffix.Length);
			}
			return name.Trim();
		}
	}
}

public static class KugouClient
{
	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(15)
	};

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "cache", "kugou");

	static KugouClient()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
	}

	public static async Task<List<KugouTrack>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
	{
		var result = new List<KugouTrack>();
		if (string.IsNullOrWhiteSpace(keyword)) return result;
		try
		{
			int page = Math.Max(1, offset / Math.Max(1, limit) + 1);
			string url = "http://mobilecdn.kugou.com/api/v3/search/song?format=json&page=" + page
				+ "&pagesize=" + limit
				+ "&showtype=1&keyword=" + Uri.EscapeDataString(keyword.Trim());
			string json = await Http.GetStringAsync(url, ct);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("data", out var data)
				|| !data.TryGetProperty("info", out var info))
			{
				return result;
			}
			foreach (var item in info.EnumerateArray())
			{
				var t = new KugouTrack();
				if (item.TryGetProperty("hash", out var hash)) t.Hash = hash.GetString() ?? "";
				if (item.TryGetProperty("filename", out var fn)) t.FileName = fn.GetString() ?? "";
				if (item.TryGetProperty("singername", out var sn)) t.SingerName = sn.GetString() ?? "";
				if (item.TryGetProperty("album_name", out var an)) t.AlbumName = an.GetString() ?? "";
				if (item.TryGetProperty("duration", out var dur)) t.Duration = dur.GetInt32();
				if (item.TryGetProperty("album_id", out var aid) && aid.ValueKind == JsonValueKind.Number)
					t.AlbumId = aid.GetInt64();
				if (item.TryGetProperty("img", out var img)) t.ImgUrl = img.GetString() ?? "";
				if (item.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number)
					t.FileSize = fs.GetInt64();
				if (item.TryGetProperty("extname", out var ext)) t.Ext = ext.GetString() ?? "mp3";
				if (item.TryGetProperty("pay_type", out var pt) && pt.ValueKind == JsonValueKind.Number)
					t.PayType = pt.GetInt32();
				else if (item.TryGetProperty("feetype", out var ft) && ft.ValueKind == JsonValueKind.Number
					&& ft.GetInt32() > 0) t.PayType = ft.GetInt32();
				// 评论数。字段不存在就当 0：排序时它只是同组里的一项，
				// 缺了不会让整条排序失效。
				if (item.TryGetProperty("comment_num", out var cn) && cn.ValueKind == JsonValueKind.Number)
					t.CommentCount = cn.GetInt64();
				if (string.IsNullOrEmpty(t.Hash)) continue;
				result.Add(t);
			}
		}
		catch
		{
		}
		return result;
	}

	public static async Task<string?> GetPlayUrlAsync(KugouTrack track, CancellationToken ct = default)
	{
		if (track == null || string.IsNullOrEmpty(track.Hash)) return null;
		// 优先走 wwwapi 的 getdata 接口；该接口近年经常返回空 play_url（含免费曲），空则回落移动端接口
		try
		{
			string url = "http://wwwapi.kugou.com/yy/index.php?r=play/getdata&mid=1"
				+ "&hash=" + Uri.EscapeDataString(track.Hash)
				+ "&album_id=" + track.AlbumId
				+ "&_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			string json = await Http.GetStringAsync(url, ct);
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("data", out var data)
				&& data.TryGetProperty("play_url", out var pu))
			{
				string? playUrl = pu.GetString();
				if (!string.IsNullOrEmpty(playUrl))
				{
					return ToHttps(playUrl);
				}
			}
		}
		catch
		{
		}
		return await GetPlayUrlMobileAsync(track, ct);
	}

	private static async Task<string?> GetPlayUrlMobileAsync(KugouTrack track, CancellationToken ct)
	{
		try
		{
			string url = "http://m.kugou.com/app/i/getSongInfo.php?cmd=playInfo&hash=" + Uri.EscapeDataString(track.Hash);
			string json = await Http.GetStringAsync(url, ct);
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() == 1
				&& doc.RootElement.TryGetProperty("url", out var u))
			{
				string? playUrl = u.GetString();
				if (!string.IsNullOrEmpty(playUrl))
				{
					return ToHttps(playUrl);
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static string ToHttps(string url)
	{
		if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
		{
			return "https" + url.Substring(4);
		}
		return url;
	}

	public static async Task<string?> DownloadSongAsync(KugouTrack track, Action<double>? progress = null, CancellationToken ct = default, bool noteAccess = true)
	{
		string? playUrl = await GetPlayUrlAsync(track, ct);
		if (string.IsNullOrEmpty(playUrl))
		{
			// 拿不到播放地址 = 酷狗那边不给，付费那一类。
			// 后台预取（noteAccess:false）不下结论：可达性以用户真正点播为准。
			if (noteAccess)
			{
				TrackAccess.NoteRestricted(track);
			}
			return null;
		}
		try
		{
			string ext = DetectExtension(playUrl);
			string file = Path.Combine(CacheDir, $"{track.Hash}.{ext}");
			// 走协调器：预取与真正播放共用同一个下载任务、原子落盘。
			// 「下了货真价实的字节流」这个结论只在真正写完了才记。
			string? result = await DownloadCoordinator.GetOrDownloadAsync(file, async part =>
			{
				using var response = await Http.GetAsync(playUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
			if (result == null)
			{
				return null;
			}
			if (noteAccess)
			{
				// 下了货真价实的字节流 = 能听能下，VIP 标签该撤
				TrackAccess.NotePlayable(track);
			}
			return result;
		}
		catch
		{
			return null;
		}
	}

	public static string FormatFileSize(long bytes)
	{
		if (bytes <= 0) return "";
		if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + "MB";
		if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + "KB";
		return bytes + "B";
	}

	/// <summary>按 hash 取酷狗歌词。play/getdata 的 data.lyrics 是整段 LRC 明文，
	/// data.trans 偶尔带翻译。拿不到/歌词为空返回 null。</summary>
	public static async Task<LyricBundle?> GetLyricBundleAsync(string hash, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(hash))
		{
			return null;
		}
		try
		{
			string url = "http://wwwapi.kugou.com/yy/index.php?r=play/getdata&mid=1"
				+ "&hash=" + Uri.EscapeDataString(hash)
				+ "&_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			string json = await Http.GetStringAsync(url, ct);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("data", out var data)
				|| !data.TryGetProperty("lyrics", out var lyr) || lyr.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			string lyrics = lyr.GetString() ?? "";
			string trans = data.TryGetProperty("trans", out var tr) && tr.ValueKind == JsonValueKind.String ? tr.GetString() ?? "" : "";
			if (string.IsNullOrWhiteSpace(lyrics))
			{
				return null;
			}
			return LyricBundle.Parse(lyrics, string.IsNullOrEmpty(trans) ? null : trans, null);
		}
		catch
		{
			return null;
		}
	}

	private static string DetectExtension(string url)
	{
		try
		{
			var path = new Uri(url).AbsolutePath;
			string ext = System.IO.Path.GetExtension(path);
			return string.IsNullOrEmpty(ext) ? "mp3" : ext.TrimStart('.');
		}
		catch
		{
			return "mp3";
		}
	}

	// ==================== 账号 / 收藏夹（mobile 网关协议） ====================
	// 原理：酷狗云接口认「userid + token」这对参数化会话（不是浏览器 Cookie），
	// 网关 https://gateway.kugou.com 用 x-router 头路由到真实服务，
	// 每个请求带公共参数（dfid/mid/uuid/appid/clientver/clienttime）并追加
	// signature = MD5(盐 + 按key排序的"k=v"串 + 请求体JSON + 盐)。
	// 算法与开源实现 MakcRe/KuGouMusicApi 的 signatureAndroidParams 一致。

	private const string SignSalt = "OIlwieks28dk2k092lksi2UIkp";
	private const int KgAppid = 1070;
	private const int KgClientVer = 11083;
	private const string KgUa = "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi";

	// 登录态接口（云歌单列表 / 歌单曲目）必须用的业务 appid + clientver。
	// 与扫码登录签发的会话、以及 KuGouMusicApi 的 defaultParams 一致。
	// 2026-11-08 实测：拿扫码 token 发 /v7/get_all_list，用 1070/11083 一律
	// {"status":0,"error_code":20006}；换 1005/20489 立刻返回 data.info 歌单数组。
	// 1070/11083 仍留给搜索、取流这类不需要账号的公开接口，别全局替换。
	private const int CloudAppid = 1005;
	private const int CloudClientVer = 20489;

	/// <summary>设备 mid：进程内固定。服务端主要校验签名与参数的一致性，
	/// 用机器名哈希保证同一台机器每次启动都是同一个值。</summary>
	private static readonly string KgMid = MakeKgMid();

	private static string MakeKgMid()
	{
		try
		{
			byte[] h = MD5.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|musicbox|kg"));
			return Convert.ToHexString(h).ToLowerInvariant();
		}
		catch
		{
			return "00000000000000000000000000000000";
		}
	}

	/// <summary>从粘贴的 Cookie 文本里解析 userid / token / dfid。
	/// 三样都来自酷狗账号体系：移动端抓包或「token=xxx; userid=yyy」格式均可。</summary>
	public static (string userid, string token, string dfid)? ParseSession(string? cookie)
	{
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return null;
		}
		string? Pick(string key)
		{
			foreach (string seg in cookie.Split(';'))
			{
				string s = seg.Trim();
				int eq = s.IndexOf('=');
				if (eq <= 0) continue;
				if (!string.Equals(s.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
				string v = s.Substring(eq + 1).Trim();
				if (v.Length > 0) return v;
			}
			return null;
		}
		string userid = Pick("userid") ?? "";
		string token = Pick("token") ?? "";
		string dfid = Pick("dfid") ?? "-";
		if (string.IsNullOrWhiteSpace(userid) || string.IsNullOrWhiteSpace(token))
		{
			return null;
		}
		return (userid, token, dfid);
	}

	/// <summary>Android 协议签名：MD5(盐 + 按key排序的"k=v"串 + 请求体JSON + 盐)。</summary>
	private static string SignAndroid(IEnumerable<KeyValuePair<string, string>> query, string? body)
	{
		var sb = new StringBuilder();
		sb.Append(SignSalt);
		foreach (var kv in query.OrderBy(k => k.Key, StringComparer.Ordinal))
		{
			sb.Append(kv.Key).Append('=').Append(kv.Value);
		}
		sb.Append(body ?? "");
		sb.Append(SignSalt);
		return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
	}

	private static string QueryString(IEnumerable<KeyValuePair<string, string>> query)
		=> string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

	/// <summary>带公共参数与签名的网关 POST（x-router 路由到真实服务）。
	/// 返回响应 JSON 文本；网络失败抛异常，由调用方兜底。</summary>
	private static async Task<string> GatewayPostAsync(string cookie, string path, string router,
		Dictionary<string, object?> body, CancellationToken ct)
	{
		var session = ParseSession(cookie) ?? throw new InvalidOperationException("kugou cookie 缺少 userid/token");
		string clienttime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
		var query = new List<KeyValuePair<string, string>>
		{
			new("dfid", session.dfid), new("mid", KgMid), new("uuid", "-"),
			new("appid", CloudAppid.ToString()), new("clientver", CloudClientVer.ToString()),
			new("clienttime", clienttime), new("plat", "1"),
			new("userid", session.userid), new("token", session.token),
		};
		string json = JsonSerializer.Serialize(body);
		query.Add(new("signature", SignAndroid(query, json)));

		var req = new HttpRequestMessage(HttpMethod.Post,
			"https://gateway.kugou.com" + path + "?" + QueryString(query))
		{
			Content = new StringContent(json, Encoding.UTF8, "application/json")
		};
		req.Headers.UserAgent.ParseAdd(KgUa);
		req.Headers.Add("x-router", router);
		req.Headers.Add("kg-rc", "1");
		req.Headers.Add("kg-thash", "5d816a0");
		req.Headers.Add("kg-rec", "1");
		req.Headers.Add("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F");
		req.Headers.Add("dfid", session.dfid);
		req.Headers.Add("clienttime", clienttime);
		req.Headers.Add("mid", KgMid);
		using var resp = await Http.SendAsync(req, ct);
		resp.EnsureSuccessStatusCode();
		return await resp.Content.ReadAsStringAsync(ct);
	}

	/// <summary>带公共参数与 Android 签名的网关 GET（登录态接口用）。
	/// 歌单曲目接口现在也搬到 gateway.kugou.com 了：直连 pubsongs.kugou.com
	/// 会 404，且必须带 signature 与 appid/clientver=1005/20489 才认扫码会话。
	/// GET 无请求体，签名里的 body 位置填空串。</summary>
	private static async Task<string> GatewayGetAsync(string cookie, string path,
		Dictionary<string, string> business, CancellationToken ct)
	{
		var session = ParseSession(cookie) ?? throw new InvalidOperationException("kugou cookie 缺少 userid/token");
		string clienttime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
		var query = new List<KeyValuePair<string, string>>
		{
			new("dfid", session.dfid), new("mid", KgMid), new("uuid", "-"),
			new("appid", CloudAppid.ToString()), new("clientver", CloudClientVer.ToString()),
			new("clienttime", clienttime),
			new("userid", session.userid), new("token", session.token),
		};
		foreach (var kv in business)
		{
			query.Add(kv);
		}
		query.Add(new("signature", SignAndroid(query, "")));

		using var req = new HttpRequestMessage(HttpMethod.Get,
			"https://gateway.kugou.com" + path + "?" + QueryString(query));
		req.Headers.UserAgent.ParseAdd(KgUa);
		req.Headers.Add("dfid", session.dfid);
		req.Headers.Add("clienttime", clienttime);
		req.Headers.Add("mid", KgMid);
		req.Headers.Add("kg-rc", "1");
		req.Headers.Add("kg-thash", "5d816a0");
		req.Headers.Add("kg-rec", "1");
		req.Headers.Add("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F");
		using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
	}

	public sealed class KugouPlaylist
	{
		public string CollectionId { get; set; } = "";
		public string Name { get; set; } = "";
		public long Count { get; set; }
	}

	/// <summary>云歌单列表（cloudlist.service.kugou.com/v7/get_all_list，分页）。
	/// 服务端返回结构没有稳定文档，这里按 data.info / data.lists / data.list /
	/// data 本身是数组 四种形态防御性解析；一种都没命中就当空列表。</summary>
	public static async Task<List<KugouPlaylist>?> FetchPlaylistsAsync(string cookie, int pages = 2, CancellationToken ct = default)
	{
		var session = ParseSession(cookie);
		if (session == null)
		{
			return null;
		}
		// 解析一次、复用结果：原来在循环里两次 ParseSession(cookie)!，
		// 既重复解析又用 ! 掩盖了「其实已经判过 null」的事实。
		(string kugouUserId, string kugouToken, _) = session.Value;
		var list = new List<KugouPlaylist>();
		for (int page = 1; page <= pages; page++)
		{
			var body = new Dictionary<string, object?>
			{
				["userid"] = long.TryParse(kugouUserId, out long uid) ? uid : 0,
				["token"] = kugouToken,
				["total_ver"] = 979,
				["type"] = 2,
				["page"] = page,
				["pagesize"] = 100,
			};
			string json;
			try
			{
				json = await GatewayPostAsync(cookie, "/v7/get_all_list", "cloudlist.service.kugou.com", body, ct);
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("酷狗歌单列表请求失败(page=" + page + "): " + ex.Message);
				return list.Count > 0 ? list : null;
			}
			int got = ParsePlaylists(json, list);
			if (got < 100)
			{
				break;
			}
		}
		return list;
	}

	private static int ParsePlaylists(string json, List<KugouPlaylist> into)
	{
		int before = into.Count;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("data", out var data))
			{
				return 0;
			}
			JsonElement arr = default;
			bool found = false;
			foreach (string key in new[] { "info", "lists", "list", "data" })
			{
				if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array)
				{
					arr = a;
					found = true;
					break;
				}
			}
			if (!found && data.ValueKind == JsonValueKind.Array)
			{
				arr = data;
				found = true;
			}
			if (!found)
			{
				return 0;
			}
			foreach (var item in arr.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object) continue;
				string cid = "";
				if (item.TryGetProperty("global_collection_id", out var g) && g.ValueKind == JsonValueKind.String)
				{
					cid = g.GetString() ?? "";
				}
				else if (item.TryGetProperty("id", out var idEl))
				{
					cid = idEl.ToString();
				}
				if (string.IsNullOrEmpty(cid) || cid == "0") continue;
				string name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
				long count = 0;
				if (item.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number)
				{
					count = c.GetInt64();
				}
				else if (item.TryGetProperty("song_count", out var sc) && sc.ValueKind == JsonValueKind.Number)
				{
					count = sc.GetInt64();
				}
				into.Add(new KugouPlaylist { CollectionId = cid, Name = name, Count = count });
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("酷狗歌单列表解析失败: " + ex.Message);
		}
		return into.Count - before;
	}

	/// <summary>取一个歌单（global_collection_id）的全部歌曲，begin_idx 翻页。
	/// 2026-11-08 实测：旧的 pubsongs.kugou.com 直连已返回 404；正确入口是
	/// gateway.kugou.com 的同名路径，且要带 Android 签名与 appid=1005/clientver=20489。
	/// 业务参数照 KuGouMusicApi 的 playlist_track_all 对齐（少一个都可能空列表）。</summary>
	private const int PlaylistTrackPageSize = 30;

	public static async Task<List<SavedTrack>?> FetchPlaylistTracksAsync(string cookie, string collectionId, int maxTracks = 1000, CancellationToken ct = default)
	{
		var session = ParseSession(cookie);
		if (session == null)
		{
			return null;
		}
		var tracks = new List<SavedTrack>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		int begin = 0;
		while (begin < maxTracks)
		{
			var business = new Dictionary<string, string>
			{
				["global_collection_id"] = collectionId,
				["begin_idx"] = begin.ToString(),
				["pagesize"] = PlaylistTrackPageSize.ToString(),
				["area_code"] = "1",
				["plat"] = "1",
				["type"] = "1",
				["mode"] = "1",
				["personal_switch"] = "1",
				["extend_fields"] = "abtags,hot_cmt,popularization",
			};
			string json;
			try
			{
				json = await GatewayGetAsync(cookie, "/pubsongs/v2/get_other_list_file_nofilt", business, ct)
					.ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("酷狗歌单歌曲请求失败(" + collectionId + "): " + ex.Message);
				return tracks.Count > 0 ? tracks : null;
			}
			var (items, count) = ParseKugouTrackPage(json);
			bool anyNew = false;
			foreach (var t in items)
			{
				if (seen.Add(t.Key))
				{
					tracks.Add(t);
					anyNew = true;
				}
			}
			if (count < PlaylistTrackPageSize || !anyNew)
			{
				break;
			}
			begin += PlaylistTrackPageSize;
		}
		return tracks;
	}

	private static (List<SavedTrack> tracks, int count) ParseKugouTrackPage(string json)
	{
		var tracks = new List<SavedTrack>();
		int count = 0;
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("data", out var data))
			{
				return (tracks, 0);
			}
			JsonElement arr = default;
			bool found = false;
			foreach (string key in new[] { "info", "songs", "list", "lists" })
			{
				if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array)
				{
					arr = a;
					found = true;
					break;
				}
			}
			if (!found)
			{
				return (tracks, 0);
			}
			foreach (var item in arr.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object) continue;
				string hash = "";
				if (item.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String)
				{
					hash = h.GetString() ?? "";
				}
				if (string.IsNullOrEmpty(hash)) continue;
				string name = "";
				if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
				{
					name = n.GetString() ?? "";
				}
				else if (item.TryGetProperty("oriname", out var o) && o.ValueKind == JsonValueKind.String)
				{
					name = o.GetString() ?? "";
				}
				else if (item.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String)
				{
					name = f.GetString() ?? "";
				}
			string singer = item.TryGetProperty("singername", out var sn) ? sn.GetString() ?? "" : "";
			// 新版 gateway 响应没有 singername，歌手在 singerinfo[0].name。
			if (string.IsNullOrEmpty(singer)
				&& item.TryGetProperty("singerinfo", out var si) && si.ValueKind == JsonValueKind.Array
				&& si.GetArrayLength() > 0
				&& si[0].TryGetProperty("name", out var sn0) && sn0.ValueKind == JsonValueKind.String)
			{
				singer = sn0.GetString() ?? "";
			}
			if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(singer))
			{
				// 只有 filename 的场合，剥掉结尾的「 - 歌手」当歌名。
				name = f2(name, singer, item);
			}
			// duration 是秒（旧接口），timelen 是毫秒（新版 gateway 响应）。
			long dur = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
				? d.GetInt64() * 1000L
				: item.TryGetProperty("timelen", out var tl) && tl.ValueKind == JsonValueKind.Number
					? tl.GetInt64()
					: 0;
			string cover = item.TryGetProperty("imgurl", out var im) ? im.GetString() ?? "" :
				item.TryGetProperty("img", out var im2) ? im2.GetString() ?? "" :
				item.TryGetProperty("cover", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() ?? "" : "";
			// 新版封面 URL 里带 {size} 占位，不替换就是 404。
			cover = cover.Replace("{size}", "150", StringComparison.OrdinalIgnoreCase);
				tracks.Add(new SavedTrack
				{
					Type = "kugou",
					Id = hash,
					Title = name.Trim(),
					Artist = singer,
					DurationMs = dur,
					Cover = cover,
				});
				count++;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("酷狗歌单歌曲解析失败: " + ex.Message);
		}
		return (tracks, count);

		static string f2(string name, string singer, JsonElement item)
		{
			if (item.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String)
			{
				string fn = f.GetString() ?? "";
				string suffix = " - " + singer;
				if (!string.IsNullOrEmpty(singer) && fn.EndsWith(suffix, StringComparison.Ordinal))
				{
					return fn.Substring(0, fn.Length - suffix.Length).Trim();
				}
				return fn.Trim();
			}
			return name;
		}
	}

	/// <summary>拉取账号收藏：歌单列表（第一页）+ 每个歌单的歌曲，合并去重。
	/// Cookie 缺 userid/token 返回 null；网络失败返回 null；空账号返回空列表。</summary>
	public static async Task<List<SavedTrack>?> FetchFavoritesAsync(string cookie, CancellationToken ct = default)
	{
		if (ParseSession(cookie) == null)
		{
			return null;
		}
		var pls = await FetchPlaylistsAsync(cookie, 1, ct);
		if (pls == null)
		{
			return null;
		}
		var all = new List<SavedTrack>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var p in pls.Take(5))
		{
			var ts = await FetchPlaylistTracksAsync(cookie, p.CollectionId, 600, ct);
			if (ts == null)
			{
				continue;
			}
			foreach (var t in ts)
			{
				if (seen.Add(t.Key))
				{
					all.Add(t);
				}
			}
			if (all.Count >= 1500)
			{
				break;
			}
		}
		return all;
	}

	/// <summary>从 Cookie 串里取指定键的值（ParseSession 里的 Pick 只在它自己
	/// 作用域内可见，这里要读的是 userid/token 以外的键，另起一个等价实现）。</summary>
	private static string? CookieValue(string cookie, string key)
	{
		foreach (string seg in cookie.Split(';'))
		{
			string s = seg.Trim();
			int eq = s.IndexOf('=');
			if (eq <= 0) continue;
			if (!string.Equals(s.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
			string v = s.Substring(eq + 1).Trim();
			return v.Length > 0 ? v : null;
		}
		return null;
	}

	/// <summary>
	/// 音乐库「收藏夹」：带状态的酷狗账号歌单拉取。
	///
	/// 接入策略与 2026-11-04 的实测结论（响应原文都记在这里，方便以后翻案）：
	/// 1. 首选 Android 网关协议（cloudlist.service.kugou.com/v7/get_all_list）：
	///    需要 Cookie 同时带 userid + token（手机端抓包才有）。网页登录
	///    kugou.com 的 Cookie 只有 KugooID/kg_h_uid/UserName/t/dfid/kg_mid，
	///    没有 token，ParseSession 直接返回 null，这条路对纯网页 Cookie 走不通。
	/// 2. 网页 Cookie 降级试探：
	///    a) 用 KugooID 推 uid、拿 t 当 token 拼一个「合成 Cookie」走同一个网关，
	///       网关认可签名但拒绝鉴权：{"status":0,"error_code":20006}（空 token 同样 20006）；
	///    b) m3ws.kugou.com 网页路由 ?r=uc/user/info / uc/user/songlist / uc/getlist
	///       全部回 "No Action Found!"（路由活着，但这些 action 名不存在或不对外）；
	///    c) ucmp3.kugou.com 连 DNS 都不解析。
	///    所以只要不是 a) 里网关真的吐出了歌单，一律判 Unsupported，
	///    UI 显示「暂不支持」占位 —— 这不是 Cookie 失效，提示重登没有意义。
	/// </summary>
	public static async Task<(AccountPlaylistStatus status, List<KugouPlaylist> playlists)> FetchAccountPlaylistsAsync(
		string cookie, CancellationToken ct = default)
	{
		var empty = new List<KugouPlaylist>();
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return (AccountPlaylistStatus.NotLoggedIn, empty);
		}
		if (ParseSession(cookie) != null)
		{
			var pls = await FetchPlaylistsAsync(cookie, ct: ct);
			return pls == null ? (AccountPlaylistStatus.NetworkError, empty) : (AccountPlaylistStatus.Ok, pls);
		}

		// 网页 Cookie：把网页字段映射成网关协议字段再试一次（KugooID → userid，
		// t → token，kg_dfid → dfid）。拼出来传给 GatewayPostAsync，签名、公共头全套复用。
		string uid = CookieValue(cookie, "KugooID") ?? CookieValue(cookie, "kg_h_uid") ?? "";
		string webToken = CookieValue(cookie, "t") ?? "";
		if (uid.Length > 0 && webToken.Length > 0)
		{
			string synthetic = "userid=" + uid + "; token=" + webToken + "; dfid="
				+ (CookieValue(cookie, "kg_dfid") ?? CookieValue(cookie, "dfid") ?? "-");
			try
			{
				var body = new Dictionary<string, object?>
				{
					["userid"] = long.TryParse(uid, out long u) ? u : 0,
					["token"] = webToken,
					["total_ver"] = 979,
					["type"] = 2,
					["page"] = 1,
					["pagesize"] = 100,
				};
				string json = await GatewayPostAsync(synthetic, "/v7/get_all_list", "cloudlist.service.kugou.com", body, ct);
				var list = new List<KugouPlaylist>();
				ParsePlaylists(json, list);
				if (list.Count > 0)
				{
					return (AccountPlaylistStatus.Ok, list);
				}
				// 解析不出歌单说明网关拒绝了这个会话（实测 error_code=20006），
				// 原文丢进调试日志，别让用户反复重登一个救不回来的登录态。
				LogHelper.LogDebug("酷狗网页Cookie走网关被拒: " + json);
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("酷狗网页Cookie试探异常: " + ex.Message);
			}
		}
		return (AccountPlaylistStatus.Unsupported, empty);
	}

	/// <summary>拉一个酷狗歌单的曲目（带状态版，给音乐库收藏夹用）。
	/// 网页 Cookie 没有 userid/token，连 pubsongs 曲目接口的鉴权都过不了，
	/// 直接判 Unsupported；有手机会话才真正发请求。</summary>
	public static async Task<(AccountPlaylistStatus status, List<SavedTrack> tracks)> FetchAccountPlaylistTracksAsync(
		string cookie, string collectionId, CancellationToken ct = default)
	{
		var empty = new List<SavedTrack>();
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return (AccountPlaylistStatus.NotLoggedIn, empty);
		}
		if (ParseSession(cookie) == null)
		{
			return (AccountPlaylistStatus.Unsupported, empty);
		}
		var list = await FetchPlaylistTracksAsync(cookie, collectionId, ct: ct);
		return list == null ? (AccountPlaylistStatus.NetworkError, empty) : (AccountPlaylistStatus.Ok, list);
	}

	// ==================== 扫码登录（官方 H5 web 协议） ====================
	//
	// 网页 Cookie 没有 userid/token，/v7/get_all_list 一律 20006（见上），手机抓包
	// 门槛又高。这里走酷狗官方 H5 扫码登录协议，三步与 MakcRe/KuGouMusicApi 的
	// login_qr_key / login_qr_check 完全一致：
	//   ① GET login-user.kugou.com/v2/qrcode               → 拿二维码 key
	//   ② 用户用酷狗 App 扫码（二维码内容 = H5 登录页 URL?qrcode=key）
	//   ③ 轮询 login-user.kugou.com/v2/get_userinfo_qrcode
	//      data.status：1=等待扫码 2=已扫码待确认 4=成功（data.token + data.userid）
	//      0=二维码过期（重新走①）
	// web 签名：signature = MD5(web盐 + 按key排序的"k=v"连续串 + 请求体 + web盐)，
	// 与 helper.js 的 signatureWebParams 一致；GET 请求体为空串。

	private const string WebSignSalt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";
	private const int WebAppid = 1005;
	private const int WebClientVer = 20489;
	private const int WebSrcAppid = 2919;
	/// <summary>二维码签发接口专用 appid（H5 登录页同款，不等于业务 appid）。</summary>
	private const int QrIssueAppid = 1001;

	/// <summary>一次扫码轮询的快照。Status：1=等待扫码 2=已扫码待确认
	/// 4=登录成功 0=二维码过期 -1=网络/服务异常。</summary>
	public sealed record KugouQrPoll(int Status, string? Token, string? Userid, string? Nickname);

	/// <summary>web 协议公共参数（与 createRequest 的 defaultParams 对齐）。
	/// dfid 固定 '-'：登录接口不校验设备指纹，与开源实现行为一致。</summary>
	private static Dictionary<string, string> WebCommonParams()
	{
		return new Dictionary<string, string>
		{
			["dfid"] = "-",
			["mid"] = KgMid,
			["uuid"] = "-",
			["appid"] = WebAppid.ToString(),
			["clientver"] = WebClientVer.ToString(),
			["clienttime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
		};
	}

	/// <summary>web 签名：MD5(盐 + 按 "k=v" 拼接后按 key 字典序排序的连续串 + body + 盐)。</summary>
	private static string SignWeb(Dictionary<string, string> p, string body = "")
	{
		string concat = string.Concat(p.Select(kv => kv.Key + "=" + kv.Value)
			.OrderBy(s => s, StringComparer.Ordinal));
		return Convert.ToHexString(MD5.HashData(
			Encoding.UTF8.GetBytes(WebSignSalt + concat + body + WebSignSalt))).ToLowerInvariant();
	}

	/// <summary>发一个 web 协议 GET：公共参数 + 业务参数合并 → 签名 → 编码发出。
	/// 失败/空响应返回 null。</summary>
	private static async Task<JsonDocument?> WebGetAsync(string path, Dictionary<string, string> extra, CancellationToken ct)
	{
		try
		{
			Dictionary<string, string> p = WebCommonParams();
			foreach (var kv in extra)
			{
				p[kv.Key] = kv.Value;
			}
			p["signature"] = SignWeb(p);
			string query = string.Join("&",
				p.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
			using var req = new HttpRequestMessage(HttpMethod.Get, "https://login-user.kugou.com" + path + "?" + query);
			req.Headers.TryAddWithoutValidation("User-Agent", KgUa);
			req.Headers.TryAddWithoutValidation("dfid", "-");
			req.Headers.TryAddWithoutValidation("clienttime", p["clienttime"]);
			req.Headers.TryAddWithoutValidation("mid", KgMid);
			req.Headers.TryAddWithoutValidation("kg-rc", "1");
			req.Headers.TryAddWithoutValidation("kg-thash", "5d816a0");
			req.Headers.TryAddWithoutValidation("kg-rec", "1");
			req.Headers.TryAddWithoutValidation("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F");
			using var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			if (string.IsNullOrWhiteSpace(json))
			{
				return null;
			}
			return JsonDocument.Parse(json);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("酷狗扫码请求异常: " + ex.Message);
			return null;
		}
	}

	/// <summary>签发二维码：返回 key（二维码内容见 QrLoginContent）。</summary>
	public static async Task<string?> LoginQrStartAsync(CancellationToken ct = default)
	{
		using var doc = await WebGetAsync("/v2/qrcode", new Dictionary<string, string>
		{
			["appid"] = QrIssueAppid.ToString(),
			["type"] = "1",
			["plat"] = "4",
			["qrcode_txt"] = $"https://h5.kugou.com/apps/loginQRCode/html/index.html?appid={WebAppid}&",
			["srcappid"] = WebSrcAppid.ToString(),
		}, ct);
		if (doc == null)
		{
			return null;
		}
		JsonElement root = doc.RootElement;
		if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
		{
			foreach (string key in new[] { "qrcode", "key", "qrcode_string" })
			{
				if (data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
					&& !string.IsNullOrEmpty(v.GetString()))
				{
					return v.GetString();
				}
			}
		}
		LogHelper.LogDebug("酷狗扫码: 签发响应里没有 key: " + root.ToString()[..Math.Min(200, root.ToString().Length)]);
		return null;
	}

	/// <summary>二维码内容（用户拿酷狗 App 扫的那个东西）。</summary>
	public static string QrLoginContent(string key)
	{
		return "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=" + Uri.EscapeDataString(key);
	}

	/// <summary>轮询一次扫码状态。见 <see cref="KugouQrPoll"/> 的状态码。</summary>
	public static async Task<KugouQrPoll> LoginQrPollAsync(string key, CancellationToken ct = default)
	{
		using var doc = await WebGetAsync("/v2/get_userinfo_qrcode", new Dictionary<string, string>
		{
			["plat"] = "4",
			["appid"] = WebAppid.ToString(),
			["srcappid"] = WebSrcAppid.ToString(),
			["qrcode"] = key,
		}, ct);
		if (doc == null)
		{
			return new KugouQrPoll(-1, null, null, null);
		}
		JsonElement root = doc.RootElement;
		int status = -1;
		if (root.TryGetProperty("data", out var data) && data.TryGetProperty("status", out var st))
		{
			if (st.ValueKind == JsonValueKind.Number)
			{
				status = st.GetInt32();
			}
			else if (st.ValueKind == JsonValueKind.String && int.TryParse(st.GetString(), out int s))
			{
				status = s;
			}
		}
		if (status != 4)
		{
			return new KugouQrPoll(status, null, null, null);
		}
		string? token = null, userid = null, nick = null;
		if (data.ValueKind == JsonValueKind.Object)
		{
			if (data.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
			{
				token = t.GetString();
			}
			if (data.TryGetProperty("userid", out var u))
			{
				userid = u.ValueKind == JsonValueKind.Number ? u.GetInt64().ToString() : u.GetString();
			}
			if (data.TryGetProperty("nickname", out var n) && n.ValueKind == JsonValueKind.String)
			{
				nick = n.GetString();
			}
		}
		if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userid))
		{
			LogHelper.LogDebug("酷狗扫码: status=4 但缺 token/userid: " + root.ToString()[..Math.Min(200, root.ToString().Length)]);
			return new KugouQrPoll(-1, null, null, null);
		}
		return new KugouQrPoll(4, token, userid, nick);
	}

	/// <summary>扫码成功后把手机域会话写进设置：收藏夹/歌单曲目两条链立刻可用。</summary>
	public static void SaveQrSession(string userid, string token, string? nickname)
	{
		var s = SettingsManager.Instance;
		s.KugouCookie = "userid=" + userid + "; token=" + token + "; dfid=-";
		if (!string.IsNullOrWhiteSpace(nickname))
		{
			s.KugouNick = nickname;
		}
		SettingsManager.Save(s);
	}
}