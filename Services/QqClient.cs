using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MusicBox.Settings;

namespace MusicBox.Services;

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

	/// <summary>QQ 的 pay.pay_play：0 = 免费，&gt;0 = VIP / 需购买。
	/// 匿名搜索常常整页取不到（接口要登录），这时保持 0，
	/// 也就是「没证据说它受限」，不会把免费歌误判成 VIP。
	/// 登录后能拿到真实值，搜索页就能提前把 B 站候选顶上来。</summary>
	public int PayPlay { get; set; }

	public bool IsRestricted => PayPlay > 0;

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

	/// <summary>从 Cookie 里解析 QQ 号 uin（通常形如 uin=o0123456789）。</summary>
	public static string ExtractUin(string cookie)
	{
		try
		{
			if (string.IsNullOrEmpty(cookie)) return "";
			int idx = cookie.IndexOf("uin=", StringComparison.OrdinalIgnoreCase);
			if (idx < 0) return "";
			int eq = cookie.IndexOf('=', idx);
			if (eq < 0) return "";
			int end = cookie.IndexOf(';', eq);
			string v = end < 0 ? cookie.Substring(eq + 1) : cookie.Substring(eq + 1, end - eq - 1);
			v = v.Trim();
			if (v.StartsWith("o", StringComparison.OrdinalIgnoreCase) && v.Length > 1) v = v.Substring(1);
			return v;
		}
		catch
		{
			return "";
		}
	}

	/// <summary>最佳努力：用网页 Cookie 拉取账号第一个歌单的歌曲。接口为第三方逆向，失败返回 null。</summary>
	public static async Task<List<SavedTrack>?> FetchFavoritesAsync(int limit = 20, CancellationToken ct = default)
	{
		if (!HasCookie) return null;
		try
		{
			string uin = ExtractUin(Cookie);
			if (string.IsNullOrWhiteSpace(uin)) return null;

			// 获取用户创建的歌单（第一个）
			string playlistsUrl = "https://c.y.qq.com/rsc/fcgi-bin/fcg_user_created_diss?format=json" +
				"&hostuin=" + uin + "&ct=24&cv=0&dft=1&inCharset=utf-8&outCharset=utf-8";
			string body = await PostQsong(playlistsUrl, Cookie, ct);
			using var doc = JsonDocument.Parse(body);
			if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("diss_list", out var list))
				return null;
			long dissid = 0;
			foreach (var item in list.EnumerateArray())
			{
				if (item.TryGetProperty("dissid", out var idEl) && idEl.ValueKind == JsonValueKind.String)
				{
					if (long.TryParse(idEl.GetString(), out long id)) { dissid = id; break; }
				}
				else if (item.TryGetProperty("dissid", out var idNum) && idNum.ValueKind == JsonValueKind.Number)
				{
					dissid = idNum.GetInt64();
					break;
				}
			}
			if (dissid <= 0) return null;

			// 取歌单内歌曲
			string detailUrl = "https://c.y.qq.com/qzone/fcg-bin/fcg_ucc_getcdinfo_byids_cp.fcg?format=json" +
				"&type=1&utf8=1&disstid=" + dissid + "&inCharset=utf-8&outCharset=utf-8";
			string body2 = await PostQsong(detailUrl, Cookie, ct);
			using var doc2 = JsonDocument.Parse(body2);
			if (!doc2.RootElement.TryGetProperty("cdlist", out var cd) && !(doc2.RootElement.TryGetProperty("data", out var d2) && d2.TryGetProperty("cdlist", out cd)))
				return null;
			var tracks = new List<SavedTrack>();
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
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ收藏加载异常: " + ex.Message);
			return null;
		}
	}

	private static async Task<string> PostQsong(string url, string cookie, CancellationToken ct)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", cookie);
		var resp = await Http.SendAsync(req, ct);
		return await resp.Content.ReadAsStringAsync(ct);
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
		// 优先走 musicu.fcg。原来的 client_search_cp 已经在随机回 HTTP 500，
		// 界面上就表现为「QQ 一搜就是 0 条」，同一个关键词有时 30 条有时 0 条。
		// 老接口留作兜底，不直接删。
		LastSearchUnavailable = false;
		var first = await SearchMusicuAsync(keyword, limit, offset, ct);
		if (first.Tracks.Count > 0) return first.Tracks;
		if (first.Ok)
		{
			// 接口是通的，只是没匹配到歌，属于「真的没搜到」。
			// 这时别再去打老接口 —— 等于把请求量翻倍，只会更快把限流窗口撑起来。
			LogHelper.LogDebug("QQ搜索无匹配: " + keyword);
			return first.Tracks;
		}
		var second = await SearchLegacyAsync(keyword, limit, offset, ct);
		if (second.Tracks.Count > 0) return second.Tracks;
		LastSearchUnavailable = !second.Ok;
		LogHelper.Log("QQ搜索无结果: " + keyword
			+ (LastSearchUnavailable ? "  原因=两个接口均异常，疑似被限流" : "  原因=接口正常但无匹配"));
		return second.Tracks;
	}

	/// <summary>走 QQ 现行接口 musicu.fcg 的 DoSearchForQQMusicDesktop。</summary>
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
			using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
			{
				Content = new StringContent(JsonSerializer.Serialize(reqData), Encoding.UTF8, "application/json")
			};
			req.Headers.Referrer = new Uri("https://y.qq.com/");
			req.Headers.Add("Origin", "https://y.qq.com");
			if (HasCookie)
			{
				req.Headers.Add("Cookie", Cookie);
			}
			var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			if (!resp.IsSuccessStatusCode)
			{
				LogHelper.Log("QQ搜索(musicu)返回 " + (int)resp.StatusCode + " " + keyword);
				return new SearchAttempt(result, false);
			}
			using var doc = JsonDocument.Parse(body);
			if (!doc.RootElement.TryGetProperty("req", out var r0)
				|| !r0.TryGetProperty("data", out var data)
				|| !data.TryGetProperty("body", out var bodyEl)
				|| !bodyEl.TryGetProperty("song", out var songEl)
				|| !songEl.TryGetProperty("list", out var list))
			{
				int code = doc.RootElement.TryGetProperty("code", out var cd) ? cd.GetInt32() : -1;
				LogHelper.Log("QQ搜索(musicu)无结果: " + keyword + "  原因=结构不符 code=" + code + " len=" + body.Length);
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
				int payPlay = 0;
				if (item.TryGetProperty("pay", out var pay))
				{
					if (pay.TryGetProperty("pay_play", out var pp) && pp.ValueKind == JsonValueKind.Number)
						payPlay = pp.GetInt32();
					else if (pay.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number)
						payPlay = pr.GetInt32();
				}
				result.Add(new QqTrack { Mid = mid, Name = name, Singer = singer, Album = album, AlbumMid = albumMid, Interval = iv, PayPlay = payPlay });
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

	/// <summary>老接口 client_search_cp，只在 musicu 本身出错时兜底。</summary>
	private static async Task<SearchAttempt> SearchLegacyAsync(string keyword, int limit, int offset, CancellationToken ct)
	{
		var result = new List<QqTrack>();
		try
		{
			int n = Math.Max(1, Math.Min(limit, 40));
			int page = Math.Max(1, offset / n + 1);
			string url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=" + page
				+ "&n=" + n
				+ "&w=" + Uri.EscapeDataString(keyword.Trim())
				+ "&platform=yqq&hostUin=0&inCharset=utf-8&outCharset=utf-8";
			string body = await Http.GetStringAsync(url, ct);
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			JsonElement song = default;
			JsonElement list = default;
			bool shapeOk = root.TryGetProperty("data", out var data)
				&& data.TryGetProperty("song", out song)
				&& song.TryGetProperty("list", out list);
			if (!shapeOk)
			{
				// 这个老接口有时候会把 list 置空、有时候直接换结构，原来一律
				// return 空列表，界面上就只显示 0 条，没人知道到底是哪一步断的。
				string code = root.TryGetProperty("code", out var cd) ? cd.ToString() : "(无)";
				int curnum = song.ValueKind == JsonValueKind.Object
					&& song.TryGetProperty("curnum", out var cn) ? cn.GetInt32() : -1;
				LogHelper.Log("QQ搜索(老接口)无结果: " + keyword + "  原因=结构不符 code=" + code
					+ " curnum=" + curnum + " len=" + body.Length);
				return new SearchAttempt(result, false);
			}
			foreach (var item in list.EnumerateArray())
			{
				string mid = GetStr(item, "songmid");
				if (string.IsNullOrEmpty(mid)) continue;
				string name = GetStr(item, "songname");
				string singer = string.Join(" / ", ReadNames(item, "singer", "name"));
				string album = GetStr(item, "albumname");
				string albumMid = GetStr(item, "albummid");
				int iv = item.TryGetProperty("interval", out var ivEl) && ivEl.ValueKind == JsonValueKind.Number ? ivEl.GetInt32() : 0;
				int payPlay = 0;
				if (item.TryGetProperty("pay", out var pay))
				{
					if (pay.TryGetProperty("pay_play", out var pp) && pp.ValueKind == JsonValueKind.Number)
						payPlay = pp.GetInt32();
					else if (pay.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number)
						payPlay = pr.GetInt32();
				}
				result.Add(new QqTrack { Mid = mid, Name = name, Singer = singer, Album = album, AlbumMid = albumMid, Interval = iv, PayPlay = payPlay });
				if (result.Count >= limit) break;
			}
			return new SearchAttempt(result, true);
		}
		catch (Exception ex)
		{
			// 原来只写 Debug 日志，线上排查时根本看不到
			LogHelper.LogDebug("QQ搜索(老接口)异常: " + keyword + " -> " + ex.Message);
			return new SearchAttempt(result, false);
		}
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
			using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
			{
				Content = new StringContent(JsonSerializer.Serialize(reqData), Encoding.UTF8, "application/json")
			};
			req.Headers.Referrer = new Uri("https://y.qq.com/");
			req.Headers.Add("Origin", "https://y.qq.com");
			if (HasCookie)
			{
				req.Headers.Add("Cookie", Cookie);
			}
			var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(body);
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

	/// <summary>下载 QQ 歌曲到本地缓存目录，返回本地路径；失败返回 null。</summary>
	public static async Task<string?> DownloadSongAsync(QqTrack track, Action<float>? progress = null, CancellationToken ct = default)
	{
		string? url = await GetSongUrlAsync(track.Mid, ct);
		if (string.IsNullOrEmpty(url)) return null;
		try
		{
			Directory.CreateDirectory(QqCacheDir);
			string safe = string.Concat(track.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
			string dest = Path.Combine(QqCacheDir, safe + "_" + track.Mid + ".m4a");
			if (File.Exists(dest)) return dest;
			using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
			resp.EnsureSuccessStatusCode();
			long total = resp.Content.Headers.ContentLength ?? 0L;
			using var source = await resp.Content.ReadAsStreamAsync(ct);
			using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
			byte[] buf = new byte[64 * 1024];
			long written = 0;
			int read;
			while ((read = await source.ReadAsync(buf, 0, buf.Length, ct)) > 0)
			{
				await target.WriteAsync(buf, 0, read, ct);
				written += read;
				if (total > 0) progress?.Invoke((float)written / total);
			}
			return dest;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("QQ下载异常: " + ex.Message);
			return null;
		}
	}
}