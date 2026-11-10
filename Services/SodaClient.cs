using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>
/// 汽水音乐（抖音账号体系）的账号收藏接入。
///
/// 2026-11-05 实测结论（修正 11-04 的「签名挡死」判断）：
/// - qishui.douyin.com **没有网页播放器**：全部路径（含 /home、/discover，以及
///   www.douyin.com/qishui/ 的 302）都回落同一个 9.7KB 静态下载落地页
///   （版本 1.0.0.287，bundle 也是落地页的）；luna-web.douyin.com 只有 terms/privacy。
///   落地页 JS 里引用的 luna/seo_api 只是分享卡的 SEO 数据接口，与账号无关。
/// - api.qishui.com 的 /luna/pc 业务端点**不要求 X-Helios/X-Medusa 设备签名**：
///   裸 GET（Cookie + UA「LunaPC/3.5.2(412998333)」）返回的是结构化业务 JSON
///   （无效会话时 {"status_code":1000016,"status_msg":"登录状态已失效"}），
///   不是风控空包。开源实现 ly-music-source（GPL-3.0，npm）同样只用
///   纯 Cookie 裸 fetch 就跑通了 listUserPlaylists / getPlaylist。
/// - 真正的门槛是**会话域**：/luna/pc 只认汽水 PC 客户端（aid=386088）签发的
///   会话。本机 settings.json 里的 SodaCookie 是抖音 web 域会话（含 UIFID、
///   home_can_add_dy_2_desktop 等 web 专属字段，有效期至 2027-01，本身没过期），
///   luna_pc 一律回 1000016「登录状态已失效」。拿合法会话的两条路：
///   ① 官方 PC 客户端（SodaMusic）导出 Cookie（AppData\Roaming\SodaMusic\Network\Cookies）；
///   ② 走 PC 客户端扫码协议（bff-pc.qishui.com/light/invoke/scan_login）。
///   浏览器登录抖音导出的 Cookie 没有用——这一点要在给用户的提示里说清楚。
/// - 端点形态（与 ly-music-source 对齐，GET 参数模板见 PcQuery）：
///   歌单列表  GET /luna/pc/me/playlist            → playlists[]/playlist_list[]
///   歌单曲目  GET /luna/pc/playlist/detail?playlist_id=&cursor=&count=100
///             → playlist（元数据）+ media_resources[] + next_cursor 分页
///   喜欢的歌  GET /luna/pc/me/collection/media（后续可接「我喜欢」）
/// - 曲目只能进「收藏夹展示」（SavedTrack）；汽水音频侧还有取流+解密链路没接，
///   播放依旧走不通——收藏夹能列出来但点播放会提示暂不支持，这不是本次回归。
/// </summary>
public static class SodaClient
{
	private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

	private const string Aid = "386088";
	private const string ApiBase = "https://api.qishui.com";
	/// <summary>PC 客户端业务 UA：/luna/pc 端点用它走业务通道；
	/// 浏览器 UA 会被当成 web 请求，行为不一致（11-05 实测两者都能拿到业务 JSON，
	/// 但 PC UA 与开源实现对齐更稳）。</summary>
	private const string PcUserAgent = "LunaPC/3.5.2(412998333)";

	public static bool HasCookie => !string.IsNullOrWhiteSpace(SettingsManager.Instance.SodaCookie);

	/// <summary>/luna/pc 系列的公共查询参数。对齐官方 PC 客户端（3.5.2）与
	/// ly-music-source 的 pcQuery 模板；device_id/iid 用公开客户端的固定值即可，
	/// 服务端只校验会话 Cookie，不绑设备。</summary>
	private static string PcQuery(string extra)
	{
		return "aid=" + Aid
			+ "&app_name=luna_pc&region=cn&geo_region=cn&os_region=cn"
			+ "&device_id=3753066532709850&iid=3753066532713946"
			+ "&version_name=3.5.2&version_code=30050200"
			+ "&channel=official&build_mode=master"
			+ "&device_platform=windows&device_type=Windows&os_version=Windows%2010"
			+ (string.IsNullOrEmpty(extra) ? "" : "&" + extra);
	}

	private static async Task<string> SendAsync(string path, string extraQuery, string? cookie, CancellationToken ct)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + path + "?" + PcQuery(extraQuery));
		if (!string.IsNullOrWhiteSpace(cookie))
		{
			req.Headers.Add("Cookie", cookie);
		}
		// 客户端 UA 形如 LunaPC/3.5.2(412998333)，HttpClient 的严格解析器会把
		// 括号 comment 判为非法直接抛 FormatException（收藏夹因此一直
		// NetworkError）。用 WithoutValidation 原样发送绕过校验——服务端
		// 认的就是这个串，实测可拉到歌单。
		req.Headers.TryAddWithoutValidation("User-Agent", PcUserAgent);
		using var resp = await Http.SendAsync(req, ct);
		return await resp.Content.ReadAsStringAsync(ct);
	}

	/// <summary>
	/// 搜索音乐页「汽水」源：GET /luna/pc/search/all
	/// （端点/参数与 ly-music-source 的 qishui/search.js 对齐：q=关键词、
	/// cursor=偏移量、search_id=一次搜索会话的 uuid、search_method=input）。
	/// 返回结构是 result_groups[]（综合搜索的分组），取 id=="tracks" 那组，
	/// 条目在 data[].entity.track —— 与歌单曲目同构，字段提取复用同一套兜底。
	/// 实测（2026-11-06）：该端点**无需登录 Cookie** 即可匿名搜索，无签名墙；
	/// 未登录/低权重会话下服务端只给少量结果（几条），属正常形态，耗尽即止。
	/// VIP/试听标记不按平台字段预打（与其它源同一规则）：label_info 的
	/// need_vip 实测错标太多，只认 TrackAccess 的实测结论。
	/// 播放链路未接：取流端点 /luna/pc/track_v2 无有效 PC 会话时返回 200 空体
	/// （实测），无地址可解析，行播放仍提示「暂未接入」，不硬造。
	/// </summary>
	public static async Task<List<SavedTrack>> FetchSearchAsync(string keyword, int pageSize, int offset, CancellationToken ct = default)
	{
		var tracks = new List<SavedTrack>();
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return tracks;
		}
		try
		{
			string extra = "q=" + Uri.EscapeDataString(keyword)
				+ "&cursor=" + Math.Max(0, offset)
				+ "&search_id=" + Guid.NewGuid().ToString("N")
				+ "&search_method=input";
			// 搜索不需要会话：传 null，独立于账号登录状态。
			string json = await SendAsync("/luna/pc/search/all", extra, null, ct);
			if (string.IsNullOrWhiteSpace(json))
			{
				// 200 空 body：风控/网关形态（与 /me、/track_v2 同款），按无结果处理。
				LogHelper.LogDebug("汽水搜索: 空响应体");
				return tracks;
			}
			using var doc = JsonDocument.Parse(json);
			int code = doc.RootElement.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number
				? sc.GetInt32() : 0;
			if (code != 0)
			{
				LogHelper.LogDebug("汽水搜索: 服务端拒绝 status_code=" + code);
				return tracks;
			}
			if (!doc.RootElement.TryGetProperty("result_groups", out var groups)
				|| groups.ValueKind != JsonValueKind.Array)
			{
				return tracks;
			}
			foreach (var group in groups.EnumerateArray())
			{
				string? gid = group.TryGetProperty("id", out var g) && g.ValueKind == JsonValueKind.String
					? g.GetString() : null;
				if (!string.Equals(gid, "tracks", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (!group.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				{
					break;
				}
				foreach (var item in data.EnumerateArray())
				{
					// 综合搜索条目统一包在 entity.track 里（与歌单 media_resources 同构）。
					JsonElement t = default;
					if (item.TryGetProperty("entity", out var e))
					{
						if (e.TryGetProperty("track", out var e1)) { t = e1; }
						else if (e.TryGetProperty("track_wrapper", out var w) && w.TryGetProperty("track", out var w2)) { t = w2; }
					}
					else if (item.TryGetProperty("track", out var a)) { t = a; }
					else if (item.TryGetProperty("track_wrapper", out var b) && b.TryGetProperty("track", out var b2)) { t = b2; }
					if (t.ValueKind != JsonValueKind.Object)
					{
						continue;
					}
					string? id = TryGetString(t, "id", "track_id");
					if (string.IsNullOrEmpty(id))
					{
						continue;
					}
					// 时长两种单位都兜（与歌单解析一致）：>10000 视为毫秒，否则按秒换算。
					long dur = TryGetNumber(t, "duration", "duration_ms");
					if (dur > 0 && dur <= 10000)
					{
						dur *= 1000;
					}
					tracks.Add(new SavedTrack
					{
						Type = "soda",
						Id = id,
						Title = TryGetString(t, "name", "title") ?? id,
						Artist = JoinArtists(t),
						DurationMs = dur,
						Cover = ExtractUrlCover(t.TryGetProperty("album", out var album) ? album : t)
					});
					if (tracks.Count >= pageSize)
					{
						break;
					}
				}
				break; // 只要 tracks 这一组，其它组（top_results 等）不进结果
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("汽水搜索异常: " + ex.Message);
		}
		return tracks;
	}

	/// <summary>校验会话：GET /luna/pc/me（aid=386088），带粘贴的 Cookie。
	/// ok=true 时 nickname 为抖音昵称（接口结构变化时可能为空字符串）。</summary>
	public static async Task<(bool ok, string nickname)> FetchAccountAsync(string? cookie, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return (false, "");
		}
		try
		{
			var req = new HttpRequestMessage(HttpMethod.Get,
				"https://api.qishui.com/luna/pc/me?aid=" + Aid + "&device_platform=pc");
			req.Headers.Add("Cookie", cookie);
			req.Headers.UserAgent.ParseAdd(
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
			using var resp = await Http.SendAsync(req, ct);
			string json = await resp.Content.ReadAsStringAsync(ct);
			if (string.IsNullOrWhiteSpace(json))
			{
				return (false, ""); // 200 空 body：按会话无效处理（实测该端点无签名墙，空体基本是网络层或风控异常）
			}
			using var doc = JsonDocument.Parse(json);
			// 结构不保证稳定：昵称/uid 递归找，兼容 data.* / data.user.* 等嵌套。
			string nick = FindStringByKeys(doc.RootElement, "nickname", "nick_name");
			bool ok = FindNumberByKeys(doc.RootElement, "user_id", "uid", "user_id_str") > 0
				|| !string.IsNullOrEmpty(nick)
				|| FindBoolByKeys(doc.RootElement, "is_login");
			return (ok, nick);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("汽水会话校验异常: " + ex.Message);
			return (false, "");
		}
	}

	private static string FindStringByKeys(JsonElement el, params string[] keys)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in el.EnumerateObject())
				{
					if (keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
					{
						if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString() ?? "";
					}
					if (p.Value.ValueKind == JsonValueKind.Object)
					{
						string nested = FindStringByKeys(p.Value, keys);
						if (!string.IsNullOrEmpty(nested)) return nested;
					}
				}
			}
		}
		catch
		{
		}
		return "";
	}

	private static long FindNumberByKeys(JsonElement el, params string[] keys)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in el.EnumerateObject())
				{
					if (keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
					{
						if (p.Value.ValueKind == JsonValueKind.Number) return p.Value.GetInt64();
						if (p.Value.ValueKind == JsonValueKind.String && long.TryParse(p.Value.GetString(), out long v)) return v;
					}
					if (p.Value.ValueKind == JsonValueKind.Object)
					{
						long nested = FindNumberByKeys(p.Value, keys);
						if (nested > 0) return nested;
					}
				}
			}
		}
		catch
		{
		}
		return 0;
	}

	private static bool FindBoolByKeys(JsonElement el, params string[] keys)
	{
		try
		{
			if (el.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in el.EnumerateObject())
				{
					if (keys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)
					{
						return true;
					}
					if (p.Value.ValueKind == JsonValueKind.Object && FindBoolByKeys(p.Value, keys))
					{
						return true;
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	/// <summary>
	/// 音乐库「收藏夹」：汽水账号歌单拉取。
	/// GET /luna/pc/me/playlist（公共参数见 PcQuery，count=50），
	/// 返回 playlists[] / playlist_list[]（两个历史字段名都兜着）。
	/// 状态语义：1000016（登录状态已失效）= CookieInvalid——会话不是汽水 PC 域的
	/// 或已过期，用户重新从**官方 PC 客户端**导出 Cookie 即可（见类头注释）；
	/// 传输失败 = NetworkError；结构对不上 = Unsupported。
	/// </summary>
	public static async Task<(AccountPlaylistStatus status, List<AccountPlaylist> playlists)> FetchAccountPlaylistsAsync(
		string cookie, CancellationToken ct = default)
	{
		var empty = new List<AccountPlaylist>();
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return (AccountPlaylistStatus.NotLoggedIn, empty);
		}
		try
		{
			string json = await SendAsync("/luna/pc/me/playlist", "count=50", cookie, ct);
			if (string.IsNullOrWhiteSpace(json))
			{
				// 200 空 body：仍是风控形态（历史抓包行为），不当成登录失效误导用户。
				LogHelper.LogDebug("汽水收藏夹: 空响应体（风控形态）");
				return (AccountPlaylistStatus.Unsupported, empty);
			}
			using var doc = JsonDocument.Parse(json);
			int code = doc.RootElement.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number
				? sc.GetInt32() : 0;
			if (code != 0)
			{
				string msg = "";
				if (doc.RootElement.TryGetProperty("status_info", out var si)
					&& si.TryGetProperty("status_msg", out var sm))
				{
					msg = sm.GetString() ?? "";
				}
				LogHelper.LogDebug("汽水收藏夹: 服务端拒绝 status_code=" + code + " " + msg);
				// 1000016「登录状态已失效」：会话域不对（web Cookie）或已过期。
				return (AccountPlaylistStatus.CookieInvalid, empty);
			}
			if (!doc.RootElement.TryGetProperty("playlists", out var listEl)
				&& !doc.RootElement.TryGetProperty("playlist_list", out listEl))
			{
				LogHelper.LogDebug("汽水收藏夹: 响应里没有歌单数组字段（接口结构变化？）");
				return (AccountPlaylistStatus.Unsupported, empty);
			}
			var result = new List<AccountPlaylist>();
			foreach (var pl in listEl.EnumerateArray())
			{
				string? id = TryGetString(pl, "id");
				if (string.IsNullOrEmpty(id))
				{
					continue;
				}
				result.Add(new AccountPlaylist
				{
					Platform = "soda",
					Id = id,
					Name = TryGetString(pl, "title", "public_title", "name") ?? id,
					TrackCount = (int)TryGetNumber(pl, "count_tracks", "track_count"),
					CoverUrl = ExtractUrlCover(pl)
				});
			}
			LogHelper.LogDebug($"汽水收藏夹: 拉到 {result.Count} 个歌单");
			return (AccountPlaylistStatus.Ok, result);
		}
		catch (Exception ex)
		{
			// 只有请求本身发不出去（断网/超时/DNS）才算网络异常。
			LogHelper.LogDebug("汽水收藏夹异常: " + ex.Message);
			return (AccountPlaylistStatus.NetworkError, empty);
		}
	}

	/// <summary>
	/// 拉一个歌单的全部曲目：GET /luna/pc/playlist/detail?playlist_id=&cursor=0&count=100，
	/// 用响应里的 next_cursor 翻页（官方客户端 count 固定 100）。
	/// 曲目实体在 media_resources[] 里，形态有三种历史包装：item.track /
	/// item.track_wrapper.track / item.entity.track（开源实现全兜，这里照做）。
	/// 返回 SavedTrack（Type="soda"，Id=曲目 id）——与酷狗收藏夹同一套「展示型」
	/// 曲目对象；播放链路等汽水取流+解密接入后再升级。
	/// </summary>
	public static async Task<(AccountPlaylistStatus status, List<SavedTrack> tracks)> FetchAccountPlaylistTracksAsync(
		string cookie, string playlistId, CancellationToken ct = default)
	{
		var tracks = new List<SavedTrack>();
		if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(playlistId))
		{
			return (AccountPlaylistStatus.NotLoggedIn, tracks);
		}
		try
		{
			string cursor = "0";
			int guard = 0;
			while (guard++ < 50) // 100×50 上限远超任何真实歌单，防服务端分页死循环
			{
				// ConfigureAwait(false)：汽水歌单按 100/页循环拉取+解析，全程别回 UI 线程
				string json = await SendAsync("/luna/pc/playlist/detail",
					"playlist_id=" + Uri.EscapeDataString(playlistId) + "&cursor=" + cursor + "&count=100", cookie, ct).ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(json))
				{
					return (AccountPlaylistStatus.Unsupported, tracks);
				}
				using var doc = JsonDocument.Parse(json);
				int code = doc.RootElement.TryGetProperty("status_code", out var sc) && sc.ValueKind == JsonValueKind.Number
					? sc.GetInt32() : 0;
				if (code != 0)
				{
					LogHelper.LogDebug("汽水歌单曲目: 服务端拒绝 status_code=" + code);
					return (code == 1000016 ? AccountPlaylistStatus.CookieInvalid : AccountPlaylistStatus.Unsupported, tracks);
				}
				if (!doc.RootElement.TryGetProperty("media_resources", out var resEl)
					|| resEl.ValueKind != JsonValueKind.Array)
				{
					break;
				}
				foreach (var item in resEl.EnumerateArray())
				{
					JsonElement t = default;
					if (item.TryGetProperty("track", out var a)) { t = a; }
					else if (item.TryGetProperty("track_wrapper", out var b) && b.TryGetProperty("track", out var b2)) { t = b2; }
					else if (item.TryGetProperty("entity", out var c))
					{
						// entity 的两层包装：直接带 track，或 track_wrapper.track。
						if (c.TryGetProperty("track", out var c2))
						{
							t = c2;
						}
						else if (c.TryGetProperty("track_wrapper", out var c3) && c3.TryGetProperty("track", out var c4))
						{
							t = c4;
						}
					}
					if (t.ValueKind != JsonValueKind.Object)
					{
						continue;
					}
					string? id = TryGetString(t, "id", "track_id");
					if (string.IsNullOrEmpty(id))
					{
						continue;
					}
					// 时长两种单位都兜：>10000 视为毫秒，否则按秒换算。
					long dur = TryGetNumber(t, "duration", "duration_ms");
					if (dur > 0 && dur <= 10000)
					{
						dur *= 1000;
					}
					tracks.Add(new SavedTrack
					{
						Type = "soda",
						Id = id,
						Title = TryGetString(t, "name", "title") ?? id,
						Artist = JoinArtists(t),
						DurationMs = dur,
						Cover = ExtractUrlCover(t.TryGetProperty("album", out var album) ? album : t)
					});
				}
				long total = 0;
				if (doc.RootElement.TryGetProperty("playlist", out var plEl))
				{
					total = TryGetNumber(plEl, "count_tracks", "track_count");
				}
				if (total > 0 && tracks.Count >= total)
				{
					break;
				}
				string? next = null;
				if (doc.RootElement.TryGetProperty("next_cursor", out var nc) && nc.ValueKind == JsonValueKind.String)
				{
					next = nc.GetString();
				}
				else if (doc.RootElement.TryGetProperty("next_cursor", out var nc2) && nc2.ValueKind == JsonValueKind.Number)
				{
					next = nc2.GetRawText();
				}
				else if (doc.RootElement.TryGetProperty("cursor", out var cu) && cu.ValueKind == JsonValueKind.String)
				{
					next = cu.GetString();
				}
				if (string.IsNullOrEmpty(next) || next == cursor)
				{
					break;
				}
				cursor = next;
			}
			LogHelper.LogDebug($"汽水歌单曲目: playlist={playlistId} 拉到 {tracks.Count} 首");
			return (AccountPlaylistStatus.Ok, tracks);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("汽水歌单曲目异常: " + ex.Message);
			return (AccountPlaylistStatus.NetworkError, tracks);
		}
	}

	// ────── JSON 小工具（/luna/pc 响应字段命名不统一，按候选键顺序兜底） ──────

	private static string? TryGetString(JsonElement el, params string[] keys)
	{
		foreach (string k in keys)
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var v)
				&& v.ValueKind == JsonValueKind.String)
			{
				return v.GetString();
			}
		}
		return null;
	}

	private static long TryGetNumber(JsonElement el, params string[] keys)
	{
		foreach (string k in keys)
		{
			if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(k, out var v)
				&& v.ValueKind == JsonValueKind.Number)
			{
				return v.GetInt64();
			}
		}
		return 0;
	}

	/// <summary>汽水的封面/头像是 url_cover={uri,urls[]} 结构：优先 urls[0]（完整 CDN 地址），
	/// 只有 uri 时用官方模板拼裁剪尺寸（500x500 居中裁，与客户端展示一致）。</summary>
	private static string ExtractUrlCover(JsonElement el)
	{
		if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("url_cover", out var uc)
			|| uc.ValueKind != JsonValueKind.Object)
		{
			return "";
		}
		string first = "";
		string uri = "";
		if (uc.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array
			&& urls.GetArrayLength() > 0 && urls[0].ValueKind == JsonValueKind.String)
		{
			first = urls[0].GetString() ?? "";
		}
		if (uc.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String)
		{
			uri = u.GetString() ?? "";
		}
		if (first.StartsWith("http", StringComparison.OrdinalIgnoreCase)
			&& (string.IsNullOrEmpty(uri) || first.Contains(uri) || first.Contains('?')))
		{
			return first;
		}
		if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(uri))
		{
			return "";
		}
		string cover = first.Contains(uri) ? first : first + uri;
		if (!string.IsNullOrEmpty(cover) && !cover.Contains('~') && !cover.Contains('?'))
		{
			cover += "~tplv-b829550vbb-crop-center:500:500.jpg";
		}
		return cover;
	}

	/// <summary>artists[] 只取名字拼接（展示用）。</summary>
	private static string JoinArtists(JsonElement track)
	{
		if (track.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			var names = new List<string>();
			foreach (var a in arr.EnumerateArray())
			{
				string? n = TryGetString(a, "name");
				if (!string.IsNullOrEmpty(n))
				{
					names.Add(n);
				}
			}
			return string.Join(" / ", names);
		}
		return "";
	}

	// ════════ 播放链路（2026-11-08 接入） ════════
	//
	// /luna/pc/track_v2 取流需要汽水 PC 域的有效会话（空会话返回 200 空体），
	// 走不通；改用**分享页免登录取流**：music.douyin.com/qishui/share/track?track_id=
	// 的 SSR 页面内嵌 _ROUTER_DATA，loaderData.track_page.audioWithLyricsOption
	// 里有 audio_mp4 直链（douyinvod.com，带时效签名）——实测无需 Cookie、
	// 无需任何签名头，浏览器 UA 即可（2026-11-08 实测 200，Content-Type=audio/mp4）。
	// VIP 曲目未登录只会拿到 30~60 秒试听片段：声明 duration > 实际 offsetDuration
	// 时按试听处理，照样下载播放（能听到就行，不拦）。

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "cache", "soda");

	/// <summary>分享页按浏览器 UA 下发完整 SSR 数据；LunaPC UA 反而拿不到页面。</summary>
	private const string ShareUserAgent =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36";

	/// <summary>按曲目 id 取音频直链（免登录）。url=null 表示分享页里没有可用直链
	/// （风控空页/下架）；preview=true 表示拿到的是试听片段。</summary>
	public static async Task<(string? url, bool preview)> ResolveAudioUrlAsync(string trackId, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(trackId))
		{
			return (null, false);
		}
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get,
				"https://music.douyin.com/qishui/share/track?track_id=" + Uri.EscapeDataString(trackId));
			req.Headers.TryAddWithoutValidation("User-Agent", ShareUserAgent);
			using var resp = await Http.SendAsync(req, ct);
			string html = await resp.Content.ReadAsStringAsync(ct);
			if (string.IsNullOrWhiteSpace(html))
			{
				LogHelper.LogDebug("汽水取流: 分享页空响应");
				return (null, false);
			}
			return ExtractAudioFromSharePage(html);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("汽水取流异常: " + ex.Message);
			return (null, false);
		}
	}

	/// <summary>从分享页 HTML 里提取音频直链。audioWithLyricsOption 是页面上
	/// 唯一的音频数据块，url 是块内第一个 "url" 键。试听识别取同一块里的
	/// offsetDuration（实际下发流长）与 duration（声明时长）比较。</summary>
	internal static (string? url, bool preview) ExtractAudioFromSharePage(string html)
	{
		// 整页是一行超长 HTML：先定位块起点，再从起点往后小窗口截取，
		// 避免在 146KB 文本上跑贪婪正则（实测 .{0,4000} 这类写法回溯到分钟级）。
		int start = html.IndexOf("\"audioWithLyricsOption\":", StringComparison.Ordinal);
		if (start < 0)
		{
			return (null, false);
		}
		// 块的有效信息（url/duration/offsetDuration）都在 lyrics 嵌套之前；
		// lyrics 之后的句子数据对本用途是噪音，截到那里为止（找不到就取 8KB）。
		int end = html.IndexOf("\"lyrics\":", start, StringComparison.Ordinal);
		if (end < 0 || end - start > 8192)
		{
			end = start + 8192;
		}
		string block = html.Substring(start, end - start);
		var m = Regex.Match(block, "\"url\":\"(https:[^\"]+)\"");
		if (!m.Success)
		{
			return (null, false);
		}
		string url = UnescapeJsonString(m.Groups[1].Value);
		if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
		{
			return (null, false);
		}
		long declared = 0, actual = 0;
		var md = Regex.Match(block, "\"duration\":(\\d+)");
		var mo = Regex.Match(block, "\"offsetDuration\":(\\d+)");
		if (md.Success) long.TryParse(md.Groups[1].Value, out declared);
		if (mo.Success) long.TryParse(mo.Groups[1].Value, out actual);
		bool preview = declared > 0 && actual > 0 && actual < declared * 0.9;
		return (url, preview);
	}

	/// <summary>分享页 JSON 串里的最小反解：\uXXXX 与 \/。直链里只出现
	/// \u002F（/）与 \u0026（&）两种，但按通用规则兜一把更稳。</summary>
	private static string UnescapeJsonString(string s)
	{
		if (string.IsNullOrEmpty(s) || !s.Contains('\\'))
		{
			return s;
		}
		try
		{
			return Regex.Replace(s, @"\\u([0-9a-fA-F]{4})|\\/",
				m => m.Groups[1].Success
					? ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
					: "/");
		}
		catch
		{
			return s.Replace("\\u002F", "/").Replace("\\/", "/").Replace("\\u0026", "&");
		}
	}

	/// <summary>下载一首汽水曲目到缓存（播放与下载共用）。
	/// 直链是 audio_mp4（m4a 容器），Media Foundation 直接能放。
	/// 返回缓存文件路径；拿不到直链/下载失败返回 null。</summary>
	public static async Task<string?> DownloadSongAsync(SavedTrack track, Action<double>? progress = null, CancellationToken ct = default)
	{
		var (url, preview) = await ResolveAudioUrlAsync(track.Id ?? "", ct);
		if (string.IsNullOrEmpty(url))
		{
			return null;
		}
		if (preview)
		{
			LogHelper.LogDebug("汽水取流: 未登录只拿到试听片段 " + (track.Title ?? track.Id));
		}
		try
		{
			string file = Path.Combine(CacheDir, (track.Id ?? "soda") + ".m4a");
			// 走协调器：与酷狗/QQ 同一套原子落盘 + 并发去重。
			string? result = await DownloadCoordinator.GetOrDownloadAsync(file, async part =>
			{
				using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
					{
						progress?.Invoke((double)readTotal / total.Value);
					}
				}
				return fs.Length >= 1024;
			});
			if (result == null)
			{
				return null;
			}
			// 下了真字节流 = 这首在当前形态下能播，撤掉可能的受限标记。
			TrackAccess.NotePlayable(track);
			return result;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("汽水下载失败: " + ex.Message);
			return null;
		}
	}
}
