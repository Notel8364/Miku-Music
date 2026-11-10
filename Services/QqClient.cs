using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>QQ 音乐账号下的一个歌单（收藏夹条目）。只留展示要用的字段。</summary>
public sealed class QqPlaylistSummary
{
	/// <summary>歌单 id（disstid），拉曲目时要用。</summary>
	public long DissId { get; set; }

	public string Name { get; set; } = "";

	public int TrackCount { get; set; }

	public string CoverUrl { get; set; } = "";
}

/// <summary>QQ 音乐搜索返回的单曲。</summary>
public sealed class QqTrack
{
	public string Mid { get; set; } = "";

	public string Name { get; set; } = "";

	public string Singer { get; set; } = "";

	public string Album { get; set; } = "";

	public string AlbumMid { get; set; } = "";

	public int Interval { get; set; }

	public long DurationMs => Interval * 1000L;

	/// <summary>QQ 的付费标记：0 = 免费，&gt;0 = VIP / 需购买。
	/// 注意 QQ 各接口键名不统一（payplay vs pay_play），统一由
	/// ReadPayPlay 解析，别在调用处自己 TryGetProperty。</summary>
	public int PayPlay { get; set; }

	public bool IsRestricted => PayPlay > 0;

	/// <summary>评论数。search_for_qq_cp 不返回 info 整块，这里恒为 0，
	/// 只影响「按评论数排序」这一项的权重，不影响能否搜到。</summary>
	public long CommentCount { get; set; }

	public string Display => Name;

	/// <summary>QQ 专辑封面（300x300，y.gtimg.cn 公开 CDN）。</summary>
	public string CoverUrl
	{
		get
		{
			string mid = string.IsNullOrEmpty(AlbumMid) ? Mid : AlbumMid;
			return string.IsNullOrEmpty(mid) ? "" : "https://y.gtimg.cn/music/photo_new/T002R300x300M000" + mid + ".jpg";
		}
	}
}

/// <summary>QQ音乐：内置浏览器登录后提取网页 Cookie 并用于云端同步收藏（最佳努力）。</summary>
public static class QqClient
{
	private const int INTERNET_COOKIE_HTTPONLY = 0x2000;

	private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

	[DllImport("wininet.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern bool InternetGetCookieEx(
		string url, string? cookieName, StringBuilder cookieData, ref int size, int flags, IntPtr reserved);

	static QqClient()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
		Http.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
	}

	public static string Cookie
	{
		get
		{
			string c = SettingsManager.Instance.QqCookie ?? "";
			return string.IsNullOrWhiteSpace(c) ? "" : c.Trim();
		}
	}

	public static bool HasCookie => !string.IsNullOrWhiteSpace(Cookie);

	/// <summary>读取指定站点下（含 httpOnly 的）全部 Cookie，返回 name=value 串。</summary>
	public static string ReadSiteCookies(string url)
	{
		try
		{
			int size = 4096;
			var sb = new StringBuilder(size);
			if (!InternetGetCookieEx(url, null, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero))
			{
				if (size > 4096)
				{
					sb = new StringBuilder(size);
					if (!InternetGetCookieEx(url, null, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero))
						return "";
				}
				else
				{
					return "";
				}
			}
			return sb.ToString();
		}
		catch
		{
			// 使用 WinInet 失败时退化为 JS document.cookie 为空（部分 Cookie 为 httpOnly）
			return "";
		}
	}

	/// <summary>「我喜欢的音乐」是每个 QQ 账号都有的默认收藏夹，dissid 全局固定
	/// （4040063455），cdinfo 接口凭 Cookie 鉴权、不依赖 hostuin。
	/// 旧接口 fcg_mylike_fd 已退役（404），不能再拿动态 dissid 了。</summary>
	private const long MyloveDissid = 4040063455L;

	/// <summary>从 Cookie 里解析账号 uin，只认纯数字。
	/// 优先级：uin=o… → pt2gguin=o… → wxuin=…。
	/// 三个都不是纯数字（比如 euin= 那种加密串，长得像 uin 却不是）就返回空——
	/// 拿加密串当 hostuin 打接口只会得到 500。</summary>
	public static string ExtractUin(string cookie)
	{
		try
		{
			if (string.IsNullOrEmpty(cookie)) return "";
			// 匹配「完整 cookie 名」，前面不能是字母/数字/下划线：
			// 不然 uin= 会命中 euin= / pt2gguin= 里嵌的那截。
			// 2026-11-05 实测坑：IndexOf 只取第一个命中——真实 uin= 排在 qqmusic_uin=
			// 这类复合字段后面时，第一个命中边界检查不过就整个放弃，导致「Cookie 里
			// 明明有 uin 却解析不到」。必须扫完全部出现位置，边界不过就 continue。
			string? pick(string key)
			{
				int start = 0;
				while (start < cookie.Length)
				{
					int idx = cookie.IndexOf(key, start, StringComparison.OrdinalIgnoreCase);
					if (idx < 0) return null;
					start = idx + 1;
					if (idx > 0 && (char.IsLetterOrDigit(cookie[idx - 1]) || cookie[idx - 1] == '_')) continue;
					// key 以 '=' 结尾，值起点就在 idx + key.Length——不能再检查"下一个字符是不是
					// '='（那是值的首字符，永远不会是'='），旧写法导致整个函数从未匹配成功过
					int val = idx + key.Length;
					if (val >= cookie.Length) continue;
					int end = cookie.IndexOf(';', val);
					string v = (end < 0 ? cookie.Substring(val) : cookie.Substring(val, end - val)).Trim();
					if (v.StartsWith("o", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
					if (v.Length > 0 && v.All(char.IsDigit)) return v;
				}
				return null;
			}
			return pick("uin=") ?? pick("pt2gguin=") ?? pick("wxuin=") ?? "";
		}
		catch
		{
			return "";
		}
	}

	/// <summary>最佳努力：用网页 Cookie 拉取账号「我喜欢的音乐」；为空时回退遍历
	/// 全部自建歌单（不再只取第一个）。接口为第三方逆向，失败返回 null。</summary>
	public static async Task<List<SavedTrack>?> FetchFavoritesAsync(int limit = 20, CancellationToken ct = default)
	{
		if (!HasCookie) return null;
		try
		{
			// 【我喜欢的音乐】优先：dissid 全局固定，cdinfo 凭 Cookie 鉴权、
			// 不碰 hostuin，所以这里根本用不上 ExtractUin（也绕开了 wx 账号
			// 没有 uin、mylike 旧接口又已 404 的坑）。limit 放大到 500：
			// 收藏了几百首只拉 30 首会让用户以为同步丢了。
			var liked = await FetchDissSongsAsync(MyloveDissid, 500, Cookie, ct);
			if (liked.Count > 0) return liked;

			// 兜底：遍历用户创建的全部歌单（这家的 diss 列表接口要吃 hostuin）。
			// 收藏夹接口拿不到/拿空的时候总不能两手空空回去，歌单里的歌也能放。
			string uin = ExtractUin(Cookie);
			if (string.IsNullOrWhiteSpace(uin))
			{
				LogHelper.LogDebug("QQ收藏同步：登录是微信网页会话，拿不到数字 uin，也没有可读歌单。");
				return null;
			}
			var dissids = await FetchAllCreatedDissidsAsync(uin, Cookie, ct);
			if (dissids.Count == 0)
			{
				// 没有异常也说明不了「同步成功」：空账号就是空。
				// 老版在这里静默 return null，日志里看不到任何线索，
				// 用户只会看到「失败」而不知道为什么。
				LogHelper.LogDebug("QQ收藏同步：我喜欢的音乐为空，账号下也没有任何可读歌单（网页微信会话通常就是这种空账号）。");
				return null;
			}
			var all = new List<SavedTrack>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (long dissid in dissids)
			{
				var fromDiss = await FetchDissSongsAsync(dissid, 500, Cookie, ct);
				foreach (var t in fromDiss)
				{
					if (seen.Add(t.Key))
					{
						all.Add(t);
					}
					if (all.Count >= 500) break;
				}
				if (all.Count >= 500) break;
			}
			if (all.Count == 0)
			{
				LogHelper.LogDebug("QQ收藏同步：找到歌单但没能解析出歌曲。");
			}
			return all;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ收藏加载异常: " + ex.Message);
			return null;
		}
	}

	/// <summary>用户创建的全部歌单 id（fcg_user_created_diss，按返回顺序，上限 30 个）。</summary>
	private static async Task<List<long>> FetchAllCreatedDissidsAsync(string uin, string cookie, CancellationToken ct)
	{
		var ids = new List<long>();
		try
		{
			string playlistsUrl = "https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss?format=json"
				+ "&hostuin=" + uin + "&ct=24&cv=0&dft=1&inCharset=utf-8&outCharset=utf-8";
			string body = StripJsonp(await PostQsong(playlistsUrl, cookie, ct));
			using var doc = JsonDocument.Parse(body);
			if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("diss_list", out var list))
			{
				return ids;
			}
			foreach (var item in list.EnumerateArray())
			{
				if (item.TryGetProperty("dissid", out var idEl) && idEl.ValueKind == JsonValueKind.String)
				{
					if (long.TryParse(idEl.GetString(), out long id) && id > 0)
					{
						ids.Add(id);
					}
				}
				else if (item.TryGetProperty("dissid", out var idNum) && idNum.ValueKind == JsonValueKind.Number)
				{
					long id = idNum.GetInt64();
					if (id > 0) ids.Add(id);
				}
				if (ids.Count >= 30) break;
			}
		}
		catch
		{
		}
		return ids;
	}

	/// <summary>取一个歌单（disstid）里的歌，统一的解析放这儿，mylove 和自建歌单共用。</summary>
	private static async Task<List<SavedTrack>> FetchDissSongsAsync(long dissid, int limit, string cookie, CancellationToken ct)
	{
		var tracks = new List<SavedTrack>(limit);
		string detailUrl = "https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?format=json"
			+ "&type=1&utf8=1&disstid=" + dissid + "&inCharset=utf-8&outCharset=utf-8";
		string body2 = StripJsonp(await PostQsong(detailUrl, cookie, ct));
		using var doc2 = JsonDocument.Parse(body2);
		if (!doc2.RootElement.TryGetProperty("cdlist", out var cd) && !(doc2.RootElement.TryGetProperty("data", out var d2) && d2.TryGetProperty("cdlist", out cd)))
		{
			return tracks;
		}
		foreach (var cditem in cd.EnumerateArray())
		{
			if (!cditem.TryGetProperty("songlist", out var songlist)) continue;
			foreach (var s in songlist.EnumerateArray())
			{
				if (tracks.Count >= limit) return tracks;
				string songmid = (s.TryGetProperty("mid", out var m) && m.ValueKind == JsonValueKind.String) ? m.GetString() ?? "" : "";
				string songName = s.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
				string singer = "";
				if (s.TryGetProperty("singer", out var sr))
				{
					var names = new List<string>();
					foreach (var a in sr.EnumerateArray()) if (a.TryGetProperty("name", out var an)) names.Add(an.GetString() ?? "");
					singer = string.Join(" / ", names);
				}
				long dur = s.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt64() * 1000 : 0;
				// 下载播放需 songmid → 由其它途径实现，收藏仅记录标题/歌手
				tracks.Add(new SavedTrack
				{
					Type = "qq",
					Id = songmid,
					Title = songName,
					Artist = singer,
					DurationMs = dur
				});
			}
		}
		return tracks;
	}

	private static async Task<string> PostQsong(string url, string cookie, CancellationToken ct)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", cookie);
		// 这些 fcgi 把 Referer 当硬校验：不带上 fcg_user_created_diss 直接回
		// {"code":5000,"message":"Referer error!"}，mylike 那家也是怎么带都 404。
		req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
		var resp = await Http.SendAsync(req, ct);
		return await resp.Content.ReadAsStringAsync(ct);
	}

	/// <summary>个别 qzone fcgi 不认 format=json，照样回 jsonCallback(...) 包裹；
	/// JsonDocument 吃不下「jsonCallback({...})」，解析前先剥壳。
	/// 有包剥包，没有就原样返回，绝不为此误伤正常 JSON。</summary>
	private static string StripJsonp(string body)
	{
		if (string.IsNullOrWhiteSpace(body)) return body;
		string s = body.TrimStart();
		if (s.StartsWith("jsonCallback(", StringComparison.Ordinal))
		{
			int close = s.LastIndexOf(')');
			if (close > 13)
			{
				return s.Substring(13, close - 13);
			}
		}
		// 兜底：别的 callback 名。从第一个 { 到最后一个 }。
		if (s[0] != '{' && s[0] != '[')
		{
			int idx = s.IndexOf('{');
			int close = s.LastIndexOf('}');
			if (idx >= 0 && close > idx)
			{
				return s.Substring(idx, close - idx + 1);
			}
		}
		return body;
	}

	// ---------- 搜索 / 播放 / 下载（最佳努力，无需登录） ----------

	/// <summary>上一次搜索是不是撞上了 QQ 接口限流/故障，而不是真的没搜到这首歌。
	/// 这两种情况界面上必须分开，不然用户只会以为 QQ 上根本没这首歌。</summary>
	public static bool LastSearchUnavailable { get; private set; }

	/// <summary>单次搜索的结果。Ok=true 表示请求成功且结构正常（列表本身可能是空的），
	/// Ok=false 表示接口本身出错 —— 只有这种情况才值得再打另一个接口。</summary>
	private readonly struct SearchAttempt
	{
		public readonly List<QqTrack> Tracks;
		public readonly bool Ok;
		public SearchAttempt(List<QqTrack> tracks, bool ok)
		{
			Tracks = tracks;
			Ok = ok;
		}
	}

	public static async Task<List<QqTrack>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(keyword)) return new List<QqTrack>();
		// 两个接口都试，谁先出货用谁；空结果必须继续试下一个。
	//
	// 这里是之前「QQ 一搜就 0 条」的真正原因：musicu.fcg 在未登录时不再报错，
	// 而是回 HTTP 200 + code 0 + 完全正常的结构，只有 song.list 是空的
	// （meta.is_filter = -2、searchid 为空、estimate_sum 是个溢出样的大数）。
	// 老代码写成「第一个接口 Ok 就直接 return」，于是把「接口被挡、无结果」
	// 当成了「QQ 上真没这首歌」，真正的可用接口压根没被调用。
	// 空列表不能作为结论 —— 只有所有接口都落空才敢说搜不到。
	bool anyHealthy = false;
	var attempts = new (string Name, Func<Task<SearchAttempt>> Run)[]
	{
		("search_for_qq_cp", () => SearchForQqCpAsync(keyword, limit, offset, ct)),
		("musicu", () => SearchMusicuAsync(keyword, limit, offset, ct)),
	};
	foreach (var (name, run) in attempts)
	{
		var a = await run();
		if (a.Tracks.Count > 0)
		{
			LogHelper.LogDebug("QQ搜索命中 接口=" + name + " keyword=" + keyword + " 条数=" + a.Tracks.Count);
			return a.Tracks;
		}
		if (a.Ok) anyHealthy = true;
	}
	LastSearchUnavailable = !anyHealthy;
	LogHelper.Log("QQ搜索无结果: " + keyword
		+ (LastSearchUnavailable ? "  原因=所有接口均异常，疑似被限流或需要登录" : "  原因=接口正常但无匹配"));
	return new List<QqTrack>();
}

	/// <summary>musicu.fcg 的 DoSearchForQQMusicDesktop。
	/// 保留是因为登录后（comm.uin 有真实值、带 Cookie）它仍然能给结果，
	/// 但注意：未登录时它不报错，而是回 HTTP 200 + code 0 + 完整结构 +
	/// song.list 空数组（meta.is_filter = -2、searchid 为空），
	/// 也就是「看起来完全正常」。这曾经让整个 QQ 搜索长期显示 0 条。
	/// 所以它现在只是 search_for_qq_cp 之后的第二手，任何情况下都不能
	/// 把它的空列表当成「没搜到」的证据。</summary>
	private static async Task<SearchAttempt> SearchMusicuAsync(string keyword, int limit, int offset, CancellationToken ct)
	{
		var result = new List<QqTrack>();
		try
		{
			int n = Math.Max(1, Math.Min(limit, 40));
			int page = Math.Max(1, offset / n + 1);
			string uin = HasCookie ? ExtractUin(Cookie) : "";
			if (string.IsNullOrEmpty(uin)) uin = "0";
			var reqData = new
			{
				comm = new { ct = 11, cv = 13080008, uin },
				req = new
				{
					module = "music.search.SearchCgiService",
					method = "DoSearchForQQMusicDesktop",
					param = new
					{
						query = keyword.Trim(),
						num_per_page = n,
						page_num = page,
						search_type = 0
					}
				}
			};
			// 2026-11-05：musicu.fcg 已强制 zzc 签名，直连会回 500003——统一走 PostMusicuAsync 的
			// musics.fcg 签名网关（自动注入 comm.qm_keyst）
			using var doc = await PostMusicuAsync(reqData, ct);
			if (doc == null)
			{
				LogHelper.Log("QQ搜索(musicu)网关无响应: " + keyword);
				return new SearchAttempt(result, false);
			}
			if (!doc.RootElement.TryGetProperty("req", out var r0)
				|| !r0.TryGetProperty("data", out var data)
				|| !data.TryGetProperty("body", out var bodyEl)
				|| !bodyEl.TryGetProperty("song", out var songEl)
				|| !songEl.TryGetProperty("list", out var list))
			{
				int code = doc.RootElement.TryGetProperty("code", out var cd) ? cd.GetInt32() : -1;
				LogHelper.Log("QQ搜索(musicu)无结果: " + keyword + "  原因=结构不符 code=" + code);
				return new SearchAttempt(result, false);
			}
			foreach (var item in list.EnumerateArray())
			{
				string mid = GetStr(item, "mid");
				if (string.IsNullOrEmpty(mid)) continue;
				string name = GetStr(item, "title");
				string singer = string.Join(" / ", ReadNames(item, "singer", "name"));
				string album = item.TryGetProperty("album", out var alb) ? GetStr(alb, "name") : "";
				string albumMid = item.TryGetProperty("album", out var alb2) ? GetStr(alb2, "mid") : "";
				int iv = item.TryGetProperty("interval", out var ivEl) && ivEl.ValueKind == JsonValueKind.Number ? ivEl.GetInt32() : 0;
				int payPlay = ReadPayPlay(item);
				// 评论数藏在 info 里（QQ 的 search list 有一半条目不带 info）。
			// 没有就留 0，排序时当同分处理，不影响其它条目。
			long commentCount = 0;
			if (item.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
			{
				if (infoEl.TryGetProperty("comment_count", out var cc) && cc.ValueKind == JsonValueKind.Number)
					commentCount = cc.GetInt64();
				else if (infoEl.TryGetProperty("comment_num", out var cn2) && cn2.ValueKind == JsonValueKind.Number)
					commentCount = cn2.GetInt64();
			}
			result.Add(new QqTrack { Mid = mid, Name = name, Singer = singer, Album = album, AlbumMid = albumMid, Interval = iv, PayPlay = payPlay, CommentCount = commentCount });
				if (result.Count >= limit) break;
			}
			return new SearchAttempt(result, true);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ搜索(musicu)异常: " + ex.Message);
			return new SearchAttempt(result, false);
		}
	}

/// <summary>搜索接口用的 UA。QQ 对空 UA 的请求很容易被降级成空结果。</summary>
	private const string SearchUA =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

	/// <summary>走 search_for_qq_cp —— 目前唯一不用登录就能拿到完整列表的入口，
	/// 歌名/歌手/专辑名/albummid/时长一次给全，albummid 在则封面可拼。
	/// 它取代的老 client_search_cp 已经稳定回 HTTP 500（实测 len=0）。</summary>
	private static async Task<SearchAttempt> SearchForQqCpAsync(string keyword, int limit, int offset, CancellationToken ct)
	{
		var result = new List<QqTrack>();
		try
		{
			// 服务端每页硬上限就是 30，n 给 40/60 也只回 30 条。
			int n = Math.Max(1, Math.Min(limit, 30));
			int page = Math.Max(1, offset / n + 1);
			string url = "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp?format=json&p=" + page
				+ "&n=" + n
				+ "&w=" + Uri.EscapeDataString(keyword.Trim())
				+ "&platform=yqq&hostUin=0&inCharset=utf-8&outCharset=utf-8";

			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			// Referer 是硬性要求：实测去掉后接口只回 109 字节的空壳（code 0、list 空），
			// 症状和被限流一模一样，很容易误判成「没搜到」。
			req.Headers.Referrer = new Uri("https://y.qq.com/");
			req.Headers.TryAddWithoutValidation("User-Agent", SearchUA);
			var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			if (!resp.IsSuccessStatusCode)
			{
				LogHelper.Log("QQ搜索(search_for_qq_cp)返回 " + (int)resp.StatusCode + " " + keyword);
				return new SearchAttempt(result, false);
			}
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			JsonElement song = default, list = default;
			bool shapeOk = root.TryGetProperty("data", out var data)
				&& data.TryGetProperty("song", out song)
				&& song.TryGetProperty("list", out list);
			if (!shapeOk)
			{
				// 以前这里一律 return 空列表，界面上就只显示 0 条，没人知道断在哪一步。
				string code = root.TryGetProperty("code", out var cd) ? cd.ToString() : "(无)";
				int total = song.ValueKind == JsonValueKind.Object
					&& song.TryGetProperty("totalnum", out var tn) ? tn.GetInt32() : -1;
				LogHelper.Log("QQ搜索(search_for_qq_cp)无结果: " + keyword + "  原因=结构不符 code=" + code
					+ " totalnum=" + total + " len=" + body.Length);
				return new SearchAttempt(result, false);
			}
			int totalNum = song.TryGetProperty("totalnum", out var tnum) ? tnum.GetInt32() : 0;
			foreach (var item in list.EnumerateArray())
			{
				string mid = GetStr(item, "songmid");
				if (string.IsNullOrEmpty(mid)) continue;
				string name = GetStr(item, "songname");
				string singer = string.Join(" / ", ReadNames(item, "singer", "name"));
				string album = GetStr(item, "albumname");
				string albumMid = GetStr(item, "albummid");
				int iv = item.TryGetProperty("interval", out var ivEl) && ivEl.ValueKind == JsonValueKind.Number ? ivEl.GetInt32() : 0;
				int payPlay = ReadPayPlay(item);
				// 这个接口不返回 info 整块，所以评论数拿不到，按 0 处理。
				// 只影响「按评论数排序」，不影响是否搜得到。
				long commentCount = 0;
				if (item.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
				{
					if (infoEl.TryGetProperty("comment_count", out var cc) && cc.ValueKind == JsonValueKind.Number)
						commentCount = cc.GetInt64();
					else if (infoEl.TryGetProperty("comment_num", out var cn2) && cn2.ValueKind == JsonValueKind.Number)
						commentCount = cn2.GetInt64();
				}
				result.Add(new QqTrack { Mid = mid, Name = name, Singer = singer, Album = album, AlbumMid = albumMid, Interval = iv, PayPlay = payPlay, CommentCount = commentCount });
				if (result.Count >= limit) break;
			}
			LogHelper.LogDebug("QQ搜索(search_for_qq_cp) " + keyword + " 命中 " + result.Count + " 条 / 共 " + totalNum);
			return new SearchAttempt(result, true);
		}
		catch (Exception ex)
		{
			// 原来只写 Debug 日志，线上排查时根本看不到
			LogHelper.LogDebug("QQ搜索(search_for_qq_cp)异常: " + keyword + " -> " + ex.Message);
			return new SearchAttempt(result, false);
		}
	}

	/// <summary>读 pay 块里的「是否 VIP / 需付费」标记。
	///
	/// 坑：QQ 不同接口对同一个概念用的键名完全不一样，写死一个必然全读成 0 ——
	///   search_for_qq_cp : payplay / paytrackprice / paydownload
	///   musicu.fcg       : pay_play / price
	/// 之前这里只认 pay_play 和 price，而 search_for_qq_cp 里这两个键压根不存在，
	/// 于是 30 条结果里 6 条真的 VIP（payplay=1）全被当成免费歌，
	/// VIP 徽标和「优先推 B 站候选」两个功能跟着一起失效。
	///
	/// 判定顺序：先取权威的 payplay / pay_play 标记；没有标记再退到价格
	/// （paytrackprice / price_track / price &gt; 0 也算要买）。
	/// 返回 0 = 没证据说它受限，所以免费歌不会被误判成 VIP。</summary>
	private static int ReadPayPlay(JsonElement item)
	{
		if (!item.TryGetProperty("pay", out var pay) || pay.ValueKind != JsonValueKind.Object)
			return 0;
		if (pay.TryGetProperty("payplay", out var a) && a.ValueKind == JsonValueKind.Number)
			return a.GetInt32();
		if (pay.TryGetProperty("pay_play", out var b) && b.ValueKind == JsonValueKind.Number)
			return b.GetInt32();
		foreach (string priceKey in new[] { "paytrackprice", "price_track", "price" })
		{
			if (pay.TryGetProperty(priceKey, out var p) && p.ValueKind == JsonValueKind.Number)
				return p.GetInt32();
		}
		return 0;
	}

	private static string GetStr(JsonElement el, string prop)
	{
		return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
	}

	private static List<string> ReadNames(JsonElement el, string arrProp, string nameProp)
	{
		var names = new List<string>();
		if (el.TryGetProperty(arrProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			foreach (var a in arr.EnumerateArray())
			{
				string n = GetStr(a, nameProp);
				if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
			}
		}
		return names;
	}

	/// <summary>和网易云那个 ResolveTrackAsync 对齐：按「歌名(+歌手)」搜 QQ，
	/// 再用歌名 / 歌手 / 时长在结果里挑最像的那首，挑不中返回 null。
	///
	/// 存在的意义是兜底：网易云搜不到（或搜到但没歌词）时，
	/// 歌词和封面还可以从 QQ 这边要一份。</summary>
	public static async Task<QqTrack?> ResolveTrackAsync(LyricMatchContext ctx, CancellationToken ct = default)
	{
		string title = CleanSearchKey(ctx.Title);
		string? artist = ctx.Artist == null ? null : CleanSearchKey(ctx.Artist);
		if (title.Length == 0)
		{
			return null;
		}
		try
		{
			List<QqTrack> hits = await SearchAsync(
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

	/// <summary>在搜索结果里挑最像的那首。规则和网易云版一致：
	/// 歌名必须完全对得上，歌手对得上才算数（对不上宁可不给，别配错），
	/// 时长只用来在「同歌名同歌手」的现场版/伴奏/翻唱里选最贴近的那个。</summary>
	private static QqTrack? PickBestMatch(string title, string? artist, List<QqTrack> hits, long wantMs)
	{
		QqTrack? titleOnly = null;
		QqTrack? bestByDuration = null;
		long bestDelta = long.MaxValue;
		foreach (QqTrack t in hits)
		{
			string tName = CleanSearchKey(t.Name ?? "");
			if (!string.Equals(tName, title, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!string.IsNullOrEmpty(artist) && !SingerMatches(t.Singer, artist))
			{
				continue;
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
					return t;
				}
			}
			titleOnly ??= t;
		}
		return bestByDuration ?? (string.IsNullOrEmpty(artist) ? titleOnly : null);
	}

	/// <summary>QQ 的歌手是一个「A/B/C」拼起来的字符串，逐个比对，
	/// 任意一个对上就算对上（跟网易云的 artists 数组一个意思）。</summary>
	private static bool SingerMatches(string? singer, string artist)
	{
		if (string.IsNullOrWhiteSpace(singer))
		{
			return false;
		}
		foreach (string part in singer.Split('/', '、', ',', ';'))
		{
			if (string.Equals(CleanSearchKey(part), artist, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>去掉括号备注和画质/格式后缀，跟网易云的 CleanSearchKey 同一套。</summary>
	private static string CleanSearchKey(string s)
	{
		s = (s ?? "").Trim();
		int cut = s.IndexOfAny(new[] { '(', '（', '[', '【' });
		if (cut > 0)
		{
			s = s.Substring(0, cut).Trim();
		}
		return s;
	}

	/// <summary>用 songmid 换取可播放地址（musicu.fcg 逆向接口，最佳努力，VIP 可能为空）。</summary>
	public static async Task<string?> GetSongUrlAsync(string songmid, CancellationToken ct = default)
	{
		try
		{
			string guid = RandomGuid();
			string uin = HasCookie ? ExtractUin(Cookie) : "";
			if (string.IsNullOrEmpty(uin))
			{
				uin = "0";
			}
			var reqData = new
			{
				req_0 = new
				{
					module = "vkey.GetVkeyServer",
					method = "CgiGetVkey",
					param = new
					{
						guid,
						songmid = new[] { songmid },
						songtype = new[] { 0 },
						uin,
						loginflag = 1,
						platform = "20"
					}
				}
			};
			// 2026-11-05：musicu.fcg 已强制 zzc 签名，直连会回 500003——统一走 PostMusicuAsync 的
			// musics.fcg 签名网关（自动注入 comm.qm_keyst）
			using var doc = await PostMusicuAsync(reqData, ct);
			if (doc == null) return null;
			if (!doc.RootElement.TryGetProperty("req_0", out var r0) || !r0.TryGetProperty("data", out var data))
				return null;
			string prefix = "";
			if (data.TryGetProperty("sip", out var sip) && sip.ValueKind == JsonValueKind.Array)
			{
				var hosts = sip.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
				if (hosts.Count > 0) prefix = hosts[0]!;
			}
			if (!data.TryGetProperty("midurlinfo", out var infos)) return null;
			foreach (var it in infos.EnumerateArray())
			{
				string purl = GetStr(it, "purl");
				if (!string.IsNullOrEmpty(purl))
				{
					return prefix.TrimEnd('/') + "/" + purl.TrimStart('/');
				}
			}
			return null;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ获取播放地址异常: " + ex.Message);
			return null;
		}
	}

	private static string RandomGuid() => Guid.NewGuid().ToString("N").Substring(0, 13);

	private static readonly string QqCacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBox", "cache", "qq");

	/// <summary>按 songmid 取 QQ 歌词（lrc + 翻译）。fcg_query_lyric_new 回的是
	/// musicJson({...}) 包裹，lyric/trans 是 base64 的 LRC 文本。
	/// 拿不到或歌词为空返回 null。QQ 歌词按「歌名+歌手」这一对齐要求，
	/// 由调用方保证 songmid 来自正在播的那首歌本身。</summary>
	public static async Task<LyricBundle?> GetLyricBundleAsync(string songMid, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(songMid))
		{
			return null;
		}
		try
		{
			string url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid="
				+ Uri.EscapeDataString(songMid)
				+ "&format=json&inCharset=utf-8&outCharset=utf-8";
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.Referrer = new Uri("https://y.qq.com/");
			var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			if (!resp.IsSuccessStatusCode)
			{
				return null;
			}
			body = StripJsonp(body);
			using var doc = JsonDocument.Parse(body);
			JsonElement root = doc.RootElement;
			if (root.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.GetInt32() != 0)
			{
				return null;
			}
			string lyric = DecodeB64(root, "lyric");
			string trans = DecodeB64(root, "trans");
			if (string.IsNullOrWhiteSpace(lyric))
			{
				return null;
			}
			return LyricBundle.Parse(lyric, string.IsNullOrEmpty(trans) ? null : trans, null);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ取歌词异常: " + ex.Message);
			return null;
		}
	}

	/// <summary>读 root 的 name 字段（base64 LRC）并解码。
	/// Convert.FromBase64String 自动忽略空白字符，QQ 偶尔在 base64 里塞换行也不怕。</summary>
	private static string DecodeB64(JsonElement root, string name)
	{
		try
		{
			if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String)
			{
				return "";
			}
			string? b64 = e.GetString();
			if (string.IsNullOrWhiteSpace(b64))
			{
				return "";
			}
			return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug($"QQ歌词 {name} base64 解码失败: {ex.Message}");
			return "";
		}
	}

	/// <summary>下载 QQ 歌曲到本地缓存目录，返回本地路径；失败返回 null。</summary>
	public static async Task<string?> DownloadSongAsync(QqTrack track, Action<float>? progress = null, CancellationToken ct = default, bool noteAccess = true)
	{
		string? url = await GetSongUrlAsync(track.Mid, ct);
		if (string.IsNullOrEmpty(url))
		{
			// 换不到直链 = QQ 那边不肯给，付费/payplay 那一类。
			// 后台预取不下结论，避免临时失败把可播曲目误标成受限。
			if (noteAccess)
			{
				TrackAccess.NoteRestricted(track);
			}
			return null;
		}
		try
		{
			string safe = string.Concat(track.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
			string dest = Path.Combine(QqCacheDir, safe + "_" + track.Mid + ".m4a");
			// 走协调器：预取与真正播放共用同一个下载任务、原子落盘。
			string? result = await DownloadCoordinator.GetOrDownloadAsync(dest, async part =>
			{
				using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
				resp.EnsureSuccessStatusCode();
				long total = resp.Content.Headers.ContentLength ?? 0L;
				using var source = await resp.Content.ReadAsStreamAsync(ct);
				using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
				byte[] buf = new byte[64 * 1024];
				long written = 0;
				int read;
				while ((read = await source.ReadAsync(buf, 0, buf.Length, ct)) > 0)
				{
					await target.WriteAsync(buf, 0, read, ct);
					written += read;
					if (total > 0) progress?.Invoke((float)written / total);
				}
				return target.Length >= 1024;
			});
			if (result == null)
			{
				return null;
			}
			if (noteAccess)
			{
				// 下到手了 = 这首歌在本程序里能听能下，行上那个 VIP 标签该撤
				TrackAccess.NotePlayable(track);
			}
			return result;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ下载异常: " + ex.Message);
			return null;
		}
	}

	// ---------- 账号收藏夹（musicu.fcg） ----------
	// 列表条目 QqPlaylistSummary 放在命名空间层（文件顶部），和 QqTrack 一样，界面也要用。

	/// <summary>向 musicu.fcg 发一个「顶层 module/method」格式的请求。
	/// 请求体结构：{"comm":{"ct":24,"cv":0,"uin":"<uin>","format":"json"},
	/// "module":"...","method":"...","param":{...}}。
	/// comm.ct=24/cv=0 是 PC 客户端的版本号组合，服务端按它识别调用方；
	/// uin 必须给（字符串形式），带不带 sign 这几个模块都能调。
	/// 请求头按网页端风格带 Referer/Origin: https://y.qq.com/ 和已存 Cookie
	/// （qm_keyst/uin 或 o 前缀的旧字段，服务端两种都认，代码不改名字）。
	/// 响应通常把业务数据放在顶层 data；个别情况会按模块名包一层，两种都兼容。
	/// HTTP 层异常原样抛出，由调用方归类成「网络失败」。</summary>
	/// <summary>QQ音乐网页网关的 zzc 签名（2026-11-05 实测接入）。
	/// 背景：musicu.fcg 已强制签名——无签名时连匿名搜索都回 code 500003（subcode 860100001），
	/// 老的 qzone fcg 接口也只回 2000，整个收藏夹链路因此断掉。
	/// 算法移植自 lx-music-desktop 的 tx/utils/crypto.js（社区验证多年，与 @jixun/qmweb-sign 一致，
	/// 算法分析见 https://jixun.uk/posts/2024/qqmusic-zzc-sign/ ）：
	/// ① 取 body 文本的 SHA1 十六进制（40 字符）；
	/// ② part1/part2 按两组固定索引从 hash 里挑 8 个字符——注意 JS 里索引 40 会越界产出
	///    undefined、join 时静默变空串，这里必须复刻这个行为（P1 里有 40 这个索引）；
	/// ③ 20 个混淆常量与 hash 相邻两字符的十六进制值逐个异或，得 20 字节做 base64 并剥掉 /+=；
	/// ④ sign = "zzc" + part1 + base64 + part2，整体转小写，拼在 musics.fcg 的查询参数上。
	/// 本机验证：搜索模块 code=0、GetPlaylistByUin code=0 拉到真实歌单。</summary>
	private static string ZzcSign(string text)
	{
		string hash;
		using (var sha1 = SHA1.Create())
		{
			hash = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
		}
		string Pick(int[] idx) => string.Concat(idx.Select(i => i < hash.Length ? hash[i].ToString() : ""));
		var scrambled = new byte[ScrambleValues.Length];
		for (int i = 0; i < ScrambleValues.Length; i++)
		{
			scrambled[i] = (byte)(ScrambleValues[i] ^ Convert.ToInt32(hash.Substring(i * 2, 2), 16));
		}
		string b64 = Convert.ToBase64String(scrambled).Replace("/", "").Replace("+", "").Replace("=", "");
		return ("zzc" + Pick(Part1Indexes) + b64 + Pick(Part2Indexes)).ToLowerInvariant();
	}

	// zzc 签名的三个常量表（SHA1 索引挑字 + 异或混淆），顺序不能动
	private static readonly int[] Part1Indexes = { 23, 14, 6, 36, 16, 40, 7, 19 };
	private static readonly int[] Part2Indexes = { 16, 1, 32, 12, 19, 27, 8, 5 };
	private static readonly int[] ScrambleValues = { 89, 39, 179, 150, 218, 82, 58, 252, 177, 52, 186, 123, 120, 64, 242, 133, 143, 161, 121, 179 };

	/// <summary>从 Cookie 串里取指定字段的值（完整字段名匹配，不会命中 euin/qm_keyst 里嵌的同名子串）。
	/// 供 comm.qm_keyst 注入用：歌单类模块的鉴权认 comm 里的 qm_keyst，缺了直接 500003。</summary>
	private static string? ExtractCookieField(string cookie, string field)
	{
		try
		{
			int idx = cookie.IndexOf(field, StringComparison.OrdinalIgnoreCase);
			if (idx < 0) return null;
			if (idx > 0 && (char.IsLetterOrDigit(cookie[idx - 1]) || cookie[idx - 1] == '_')) return null;
			int eq = idx + field.Length;
			if (eq >= cookie.Length || cookie[eq] != '=') return null;
			int end = cookie.IndexOf(';', eq);
			string v = (end < 0 ? cookie.Substring(eq + 1) : cookie.Substring(eq + 1, end - eq - 1)).Trim();
			return v.Length > 0 ? v : null;
		}
		catch
		{
			return null;
		}
	}

	private static async Task<JsonDocument?> PostMusicuAsync(object payload, CancellationToken ct)
	{
		Exception? lastError = null;
		string json;
		try
		{
			var node = JsonNode.Parse(JsonSerializer.Serialize(payload));
			if (node != null)
			{
				// ① musics.fcg 网关只认 req_N 包裹风格（{"comm":..,"req_1":{"module":..,"method":..,"param":..}}），
				//    顶层 module/method/param 风格一律回 500003/subcode 860100005（搜歌单实测复现）。
				//    这里统一自动包裹，调用方不用关心信封格式。
				if (node["module"] != null && node["req_1"] == null && node["req"] == null && node["req_0"] == null)
				{
					var wrapped = new JsonObject
					{
						["comm"] = (node["comm"] ?? new JsonObject()).DeepClone(),
						["req_1"] = new JsonObject
						{
							["module"] = node["module"]!.DeepClone(),
							["method"] = node["method"]!.DeepClone(),
							["param"] = node["param"]!.DeepClone()
						}
					};
					node = wrapped;
				}
				// ② 注入 comm.qm_keyst：歌单类模块鉴权认 comm 里的 qm_keyst，匿名对象构造时拿不到 Cookie 值
				if (HasCookie && node["comm"] is JsonObject comm)
				{
					string? keyst = ExtractCookieField(Cookie, "qm_keyst");
					if (!string.IsNullOrEmpty(keyst))
					{
						comm["qm_keyst"] = keyst;
					}
				}
			}
			json = node?.ToJsonString() ?? JsonSerializer.Serialize(payload);
		}
		catch
		{
			json = JsonSerializer.Serialize(payload); // 注入失败不挡路：按原样发
		}
		// ② 签名必须算在最终发送的 body 上（qm_keyst 注入会改变 body）
		string sign = ZzcSign(json);
		// ③ 网关从 musicu.fcg 换成 musics.fcg（sign 放查询参数）；u6 是官方同款灾备域
		foreach (string host in new[] { "https://u.y.qq.com/cgi-bin/musics.fcg", "https://u6.y.qq.com/cgi-bin/musics.fcg" })
		{
			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Post, host + "?sign=" + Uri.EscapeDataString(sign))
				{
					Content = new StringContent(json, Encoding.UTF8, "application/json")
				};
				req.Headers.Referrer = new Uri("https://y.qq.com/");
				req.Headers.Add("Origin", "https://y.qq.com");
				if (HasCookie) req.Headers.Add("Cookie", Cookie);
				var resp = await Http.SendAsync(req, ct);
				resp.EnsureSuccessStatusCode();
				string body = await resp.Content.ReadAsStringAsync(ct);
				LogHelper.LogDebug("musics.fcg 响应: " + body.Substring(0, Math.Min(200, body.Length)));
				return JsonDocument.Parse(body);
			}
			catch (HttpRequestException ex)
			{
				lastError = ex; // 换备用域名再试
			}
			catch (TaskCanceledException)
			{
				throw; // 超时/取消不值得再打另一个域名
			}
			catch (Exception ex) when (ex is not HttpRequestException and not TaskCanceledException)
			{
				LogHelper.LogDebug("musicu.fcg 请求失败: " + ex.Message);
				return null;
			}
		}
		LogHelper.LogDebug("musicu.fcg 两个域名都失败: " + (lastError?.Message ?? ""));
		throw lastError ?? new HttpRequestException("musicu.fcg 请求失败");
	}

	/// <summary>musicu 顶层 code 检查：code=0 才算成功，非 0 通常意味着登录态失效/权限不足，
	/// 归类成 Cookie 失效，让用户重新粘贴 Cookie 而不是去查网络。</summary>
	private static PlaylistFetchResult MusicuCodeToResult(JsonDocument? doc)
	{
		if (doc == null) return PlaylistFetchResult.NetworkError;
		var root = doc.RootElement;
		if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() != 0)
		{
			return PlaylistFetchResult.CookieInvalid;
		}
		return PlaylistFetchResult.Ok;
	}

	/// <summary>取 musicu 响应里的业务 data 块：优先顶层 data，没有再试
	/// req_1 包裹（musics.fcg 网关标准信封）和按模块名包一层两种形态。</summary>
	private static JsonElement? MusicuData(JsonDocument doc, string module)
	{
		var root = doc.RootElement;
		if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object) return d;
		if (root.TryGetProperty("req_1", out var r1) && r1.ValueKind == JsonValueKind.Object
			&& r1.TryGetProperty("data", out var d0) && d0.ValueKind == JsonValueKind.Object) return d0;
		if (root.TryGetProperty(module, out var m) && m.ValueKind == JsonValueKind.Object
			&& m.TryGetProperty("data", out var d2) && d2.ValueKind == JsonValueKind.Object) return d2;
		return null;
	}

	/// <summary>拉取登录账号在 QQ 音乐下的歌单（收藏夹）。需要已保存 Cookie。
	/// 流程：先 musicu.fcg 的 music.musicasset.PlaylistBaseRead / GetPlaylistByUin
	/// 拿到歌单 disstid 列表——注意它的响应 data.v_playlist 是「纯数字数组」，
	/// 不带名字/封面/曲目数，所以再对每个 disstid 并发（6 路）调一次
	/// uniform_get_Dissinfo（song_num=1 就够），用响应 dirinfo 里的
	/// dirname/song_cnt/picurl 补全展示信息。u.y.qq.com 主入口不通时自动落 u6 备用域名。</summary>
	public static async Task<(PlaylistFetchResult result, List<QqPlaylistSummary> playlists)> FetchPlaylistsAsync(CancellationToken ct = default)
	{
		var empty = new List<QqPlaylistSummary>();
		if (!HasCookie) return (PlaylistFetchResult.CookieInvalid, empty);
		// uin 从 Cookie 解析（uin=o12345 去掉 o 前缀就是数字）。musicu 的 comm.uin 和
		// param.uin 都用字符串形态（param.uin 传数字实测 10006）；拿不到数字 uin（微信网页会话登录）就没法调这个接口。
		string uinStr = ExtractUin(Cookie);
		if (!long.TryParse(uinStr, out long uin) || uin <= 0)
		{
			LogHelper.LogDebug("QQ收藏夹：Cookie 里解析不到数字 uin，无法拉歌单列表。");
			return (PlaylistFetchResult.CookieInvalid, empty);
		}
		try
		{
			// 2026-11-05 实测对齐：comm ct=19/cv=1873 + qm_keyst（PostMusicuAsync 自动注入），
			// param 用最小 { uin }——带 host_uin/size 之类字段实测也会过，但最小组合最稳。
			// 注意 param.uin 必须是字符串！传数字（long）实测必回 req_1.code=10006，
			// 字符串才回 code=0（双盲对照复现过，与 comm.uin 一样都用字符串形态）
			var payload = new
			{
				comm = new { ct = 19, cv = 1873, uin = uinStr, format = "json" },
				module = "music.musicasset.PlaylistBaseRead",
				method = "GetPlaylistByUin",
				param = new { uin = uinStr }
			};
			using var doc = await PostMusicuAsync(payload, ct);
			var r = MusicuCodeToResult(doc);
			if (r != PlaylistFetchResult.Ok) return (r, empty);

			var summaries = new List<QqPlaylistSummary>();
			var ids = new List<long>();
			JsonElement? data = MusicuData(doc!, "music.musicasset.PlaylistBaseRead");
			if (data.HasValue && data.Value.TryGetProperty("v_playlist", out var vp) && vp.ValueKind == JsonValueKind.Array)
			{
				foreach (var el in vp.EnumerateArray())
				{
					if (el.ValueKind == JsonValueKind.Object)
					{
						// 2026-11-05 实测：新网关直接返回歌单对象（tid/dirName/songNum/picUrl），
						// 在这里就能拼出完整摘要，省掉逐个并发拉详情的 6 路请求
						long tid = 0;
						if (el.TryGetProperty("tid", out var tidEl))
						{
							tid = tidEl.ValueKind == JsonValueKind.Number && tidEl.TryGetInt64(out long t) ? t
								: tidEl.ValueKind == JsonValueKind.String && long.TryParse(tidEl.GetString(), out long t2) ? t2 : 0;
						}
						if (tid <= 0) continue;
						string name = GetStr(el, "dirName");
						if (name.Length == 0) name = GetStr(el, "diss_name");
						if (name.Length == 0) name = "歌单 " + tid;
						int songNum = 0;
						if (el.TryGetProperty("songNum", out var snEl) && snEl.ValueKind == JsonValueKind.Number) songNum = snEl.GetInt32();
						else if (el.TryGetProperty("song_cnt", out var scEl) && scEl.ValueKind == JsonValueKind.Number) songNum = scEl.GetInt32();
						string cover = GetStr(el, "picUrl");
						if (cover.Length == 0) cover = GetStr(el, "bigpicUrl");
						if (cover.Length == 0) cover = GetStr(el, "logo");
						summaries.Add(new QqPlaylistSummary { DissId = tid, Name = name, TrackCount = songNum, CoverUrl = cover });
					}
					else if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long id) && id > 0) ids.Add(id);
					else if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out long id2) && id2 > 0) ids.Add(id2);
				}
			}
			else
			{
				// 【排查指引】GetPlaylistByUin 的预期结构是 {"code":0,"data":{"v_playlist":[...]}}
				//（对象数组或纯 disstid 数字数组两种形态都见过）。
				// 走到这里说明 code=0 但结构对不上：多半是登录态半失效，也可能是模块字段变更。
				// 把响应原文截断记进日志，方便对照实际返回修解析，别让用户对着空列表猜。
				string raw = doc!.RootElement.GetRawText();
				LogHelper.Log("QQ收藏夹：GetPlaylistByUin 响应里没有 data.v_playlist，实际结构："
					+ raw.Substring(0, Math.Min(600, raw.Length)));
				return (PlaylistFetchResult.CookieInvalid, empty);
			}
			// 新结构直接出结果；纯数字数组的老结构走下面的并发详情兜底
			if (summaries.Count > 0) return (PlaylistFetchResult.Ok, summaries);
			if (ids.Count == 0) return (PlaylistFetchResult.Ok, empty);

			// 按槽位并发拉详情：ConcurrentBag 无序，直接用数组槽位既安全又能保住 v_playlist 的原顺序
			var slots = new QqPlaylistSummary?[ids.Count];
			using var gate = new SemaphoreSlim(6);
			await Task.WhenAll(ids.Select(async (id, idx) =>
			{
				await gate.WaitAsync(ct);
				try
				{
					slots[idx] = await FetchDissMetaAsync(id, uin, ct);
				}
				catch (Exception ex)
				{
					LogHelper.LogDebug("QQ歌单详情失败 id=" + id + ": " + ex.Message); // 单个歌单失败不拖垮整张列表
				}
				finally
				{
					gate.Release();
				}
			}));
			var list = slots.Where(s => s != null).Cast<QqPlaylistSummary>().ToList();
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
			LogHelper.LogDebug("QQ收藏夹列表异常: " + ex.Message);
			return (PlaylistFetchResult.NetworkError, empty);
		}
	}

	/// <summary>拉一个歌单的展示信息（名字/曲目数/封面）。uniform_get_Dissinfo 带 dirinfo:1
	/// 时响应 data.dirinfo 里有 dirname/song_cnt/picurl。曲目数优先用 song_cnt，
	/// 拿不到再退 songlist 长度；解析失败就给个「歌单 + id」的占位名，行还是能点开。</summary>
	private static async Task<QqPlaylistSummary> FetchDissMetaAsync(long dissId, long uin, CancellationToken ct)
	{
		var summary = new QqPlaylistSummary { DissId = dissId, Name = "歌单 " + dissId };
		// 2026-11-05 实测：param 最小组合才过（login_uin/host_uin/dirinfo 会触发 10006），同 FetchPlaylistSongsAsync
		var payload = new
		{
			comm = new { ct = 19, cv = 1873, uin = uin.ToString(), format = "json" },
			module = "music.srfDissInfo.DissInfoForPc",
			method = "uniform_get_Dissinfo",
			param = new { disstid = dissId, onlysonglist = 0, song_begin = 0, song_num = 1 }
		};
		using var doc = await PostMusicuAsync(payload, ct);
		if (doc == null) return summary;
		var data = MusicuData(doc, "music.srfDissInfo.DissInfoForPc");
		if (data == null || !data.Value.TryGetProperty("dirinfo", out var dir) || dir.ValueKind != JsonValueKind.Object)
		{
			return summary;
		}
		// dirinfo 里的键名各端不统一，按常见候选逐个试，取到即停
		string name = GetStr(dir, "dirname");
		if (name.Length == 0) name = GetStr(dir, "title");
		if (name.Length == 0) name = GetStr(dir, "diss_name");
		if (name.Length == 0) name = GetStr(dir, "dissname");
		if (name.Length > 0) summary.Name = name;
		if (dir.TryGetProperty("song_cnt", out var sc) && sc.ValueKind == JsonValueKind.Number) summary.TrackCount = sc.GetInt32();
		else if (dir.TryGetProperty("songnum", out var sn) && sn.ValueKind == JsonValueKind.Number) summary.TrackCount = sn.GetInt32();
		string cover = GetStr(dir, "picurl");
		if (cover.Length == 0) cover = GetStr(dir, "logo");
		if (cover.Length == 0 && dir.TryGetProperty("dirpic", out var dp) && dp.ValueKind == JsonValueKind.Object)
		{
			cover = GetStr(dp, "url");
		}
		summary.CoverUrl = cover;
		// 曲目数兜底：新网关 songlist 在 data 顶层（dirinfo 常不带），先数 data.songlist
		// 再退 dirinfo.songlist，两个位置都没有才保持 0
		if (summary.TrackCount == 0 && data.Value.TryGetProperty("songlist", out var slTop) && slTop.ValueKind == JsonValueKind.Array)
		{
			summary.TrackCount = slTop.GetArrayLength();
		}
		if (summary.TrackCount == 0 && dir.TryGetProperty("songlist", out var sl) && sl.ValueKind == JsonValueKind.Array)
		{
			summary.TrackCount = sl.GetArrayLength();
		}
		return summary;
	}

	/// <summary>拉一个歌单里的全部曲目（musicu.fcg / uniform_get_Dissinfo）。需要已保存 Cookie。
	/// param 用最小组合 { disstid, onlysonglist, song_begin, song_num }，song_num=300
	/// 一次拿全（绝大多数歌单够用，超过部分暂不翻页）。
	/// 响应的曲目在新网关在 data.songlist（data 顶层），旧端形态在 data.dirinfo.songlist，
	/// 分 CD 形态在 data.cdlist[].songlist，三处按序兜底；元素是「短键」格式
	///（mid/name/singer[{name}]/album{name,mid}/interval/pay），与搜索接口键名不同，
	/// 统一映射成 QqTrack 就能走和搜索结果完全相同的播放/下载链路。
	/// 注意：PayPlay 只做数据记录，行上的 VIP 标签只认实测（TrackAccess），不按收费字段打标。</summary>
	public static async Task<(PlaylistFetchResult result, List<QqTrack> tracks)> FetchPlaylistSongsAsync(long dissId, CancellationToken ct = default)
	{
		var empty = new List<QqTrack>();
		if (!HasCookie) return (PlaylistFetchResult.CookieInvalid, empty);
		string uinStr = ExtractUin(Cookie);
		if (!long.TryParse(uinStr, out long uin) || uin <= 0)
		{
			return (PlaylistFetchResult.CookieInvalid, empty);
		}
		try
		{
			// 2026-11-05 实测：param 必须用最小组合 { disstid, onlysonglist, song_begin, song_num }——
			// 带 login_uin/host_uin/dirinfo 任何一项都会触发 code 10006；
			// comm ct=19/cv=1873 + qm_keyst（PostMusicuAsync 自动注入），ct=24 组合未验证过不冒险
			var payload = new
			{
				comm = new { ct = 19, cv = 1873, uin = uinStr, format = "json" },
				module = "music.srfDissInfo.DissInfoForPc",
				method = "uniform_get_Dissinfo",
				param = new { disstid = dissId, onlysonglist = 0, song_begin = 0, song_num = 300 }
			};
			// ConfigureAwait(false)：300 首响应的 JSON 解析 + 反序列化别压在 UI 线程上
			//（与网易云 FetchPlaylistTracksAsync 同理，本方法不碰任何 UI）。
			using var doc = await PostMusicuAsync(payload, ct).ConfigureAwait(false);
			var r = MusicuCodeToResult(doc);
			if (r != PlaylistFetchResult.Ok) return (r, empty);
			var data = MusicuData(doc!, "music.srfDissInfo.DissInfoForPc");
			if (data == null) return (PlaylistFetchResult.CookieInvalid, empty);

			var tracks = new List<QqTrack>();
			// 2026-11-05 实测根因：官方网关把曲目数组放在 data.songlist（data 顶层，
			// lx-music-desktop / qm-web 均按 req_1.data.songlist 取），dirinfo 只放元数据
			//（dirname/song_cnt），data.dirinfo.songlist 是旧端形态、很多歌单不再携带——
			// 只认 dirinfo.songlist 就会出现「请求 code=0 但界面曲目为空」。
			// 解析顺序：data.songlist → data.dirinfo.songlist → data.cdlist[].songlist（分 CD 形态）。
			JsonElement songList = default;
			bool hasSongs = false;
			if (data.Value.TryGetProperty("songlist", out var slTop) && slTop.ValueKind == JsonValueKind.Array)
			{
				songList = slTop;
				hasSongs = true;
			}
			else if (data.Value.TryGetProperty("dirinfo", out var dir)
				&& dir.TryGetProperty("songlist", out var slDir) && slDir.ValueKind == JsonValueKind.Array)
			{
				songList = slDir;
				hasSongs = true;
			}
			else if (data.Value.TryGetProperty("cdlist", out var cds) && cds.ValueKind == JsonValueKind.Array)
			{
				foreach (var cd in cds.EnumerateArray())
				{
					if (cd.ValueKind == JsonValueKind.Object
						&& cd.TryGetProperty("songlist", out var slCd) && slCd.ValueKind == JsonValueKind.Array)
					{
						songList = slCd;
						hasSongs = true;
						break;
					}
				}
			}
			if (!hasSongs)
			{
				// 三处都找不到：把响应原文截断写日志（同 FetchPlaylistsAsync 的排查指引写法），
				// 别让用户对着空列表猜，也方便下次接口改版时对照实际结构补解析
				string raw = doc!.RootElement.GetRawText();
				LogHelper.Log("QQ歌单曲目：uniform_get_Dissinfo 响应里找不到 songlist"
					+ "（已试 data.songlist / data.dirinfo.songlist / data.cdlist[].songlist），实际结构："
					+ raw.Substring(0, Math.Min(600, raw.Length)));
				return (PlaylistFetchResult.Ok, tracks);
			}
			foreach (var s in songList.EnumerateArray())
			{
				// 键名候选从多到少试：新结构是短键（mid/name/singer），老结构偶见长键（songmid/songname）
				string mid = GetStr(s, "mid");
				if (mid.Length == 0) mid = GetStr(s, "songmid");
				if (mid.Length == 0) continue; // 没有 mid 就换不到播放地址，这条没法播/下，跳过
				string name = GetStr(s, "name");
				if (name.Length == 0) name = GetStr(s, "songname");
				if (name.Length == 0) name = GetStr(s, "title");
				string singer = string.Join(" / ", ReadNames(s, "singer", "name"));
				string album = "", albumMid = "";
				if (s.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object)
				{
					album = GetStr(alb, "name");
					albumMid = GetStr(alb, "mid");
				}
				int iv = s.TryGetProperty("interval", out var ivEl) && ivEl.ValueKind == JsonValueKind.Number ? ivEl.GetInt32() : 0;
				tracks.Add(new QqTrack
				{
					Mid = mid,
					Name = name,
					Singer = singer,
					Album = album,
					AlbumMid = albumMid,
					Interval = iv,
					PayPlay = ReadPayPlay(s)
				});
			}
			return (PlaylistFetchResult.Ok, tracks);
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
			LogHelper.LogDebug("QQ歌单曲目加载异常: " + ex.Message);
			return (PlaylistFetchResult.NetworkError, empty);
		}
	}

	/// <summary>拉 QQ 音乐账号名，三级兜底：
	/// ① musicu.fcg 的 music.UserInfo.UserInfoServer.GetUserInfo（官方端取当前登录用户信息的模块）；
	/// ② qm_get_vipinfo.fcgi（GET 的老 VIP 信息接口，响应里也带昵称）；
	/// ③ 都拿不到就退回 Cookie 里的数字 uin —— 微信扫码会话的 Cookie 没有 uin
	/// 时连这个兜底都是空的，返回空串让调用方按「无账号名」降级（标题行不显示）。
	/// 收藏夹收缩栏标题行用；拿不到不算错误，静默降级。</summary>
	public static async Task<string> FetchAccountNameAsync(CancellationToken ct = default)
	{
		if (!HasCookie) return "";
		string uin = ExtractUin(Cookie);

		// ① GetUserInfo：昵称字段各端叫法不一（nickname/nickName/嵌在 userInfo 里），逐个候选试
		try
		{
			var payload = new
			{
				comm = new { ct = 19, cv = 1873, uin = uin, format = "json" },
				module = "music.UserInfo.UserInfoServer",
				method = "GetUserInfo",
				param = new { }
			};
			using var doc = await PostMusicuAsync(payload, ct);
			var data = doc == null ? null : MusicuData(doc, "music.UserInfo.UserInfoServer");
			if (data.HasValue)
			{
				string nick = GetStr(data.Value, "nickname");
				if (nick.Length == 0) nick = GetStr(data.Value, "nickName");
				if (nick.Length == 0 && data.Value.TryGetProperty("userInfo", out var ui)
					&& ui.ValueKind == JsonValueKind.Object)
				{
					nick = GetStr(ui, "nickname");
					if (nick.Length == 0) nick = GetStr(ui, "nickName");
				}
				if (nick.Length > 0) return nick;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ账号名获取失败(GetUserInfo): " + ex.Message);
		}

		// ② qm_get_vipinfo.fcgi：老网关 GET 接口，带 Cookie/Referer 直接问
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get,
				"https://u.y.qq.com/cgi-bin/qm_get_vipinfo.fcgi?format=json&uin=" + Uri.EscapeDataString(uin));
			req.Headers.Referrer = new Uri("https://y.qq.com/");
			req.Headers.Add("Cookie", Cookie);
			using var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			LogHelper.LogDebug("qm_get_vipinfo.fcgi 响应: " + body.Substring(0, Math.Min(200, body.Length)));
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			// 形态 { code:0, data:{ nickName | nickname } }；老接口偶见直接平铺在顶层
			foreach (var holder in new[] { root,
				root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default })
			{
				if (holder.ValueKind != JsonValueKind.Object) continue;
				string nick = GetStr(holder, "nickName");
				if (nick.Length == 0) nick = GetStr(holder, "nickname");
				if (nick.Length > 0) return nick;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ账号名获取失败(qm_get_vipinfo): " + ex.Message);
		}

		// ③ 兜底 uin：没有 uin（微信会话登录）就真的没有了
		return uin.Length > 0 ? uin : "";
	}
}