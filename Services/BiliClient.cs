using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBox.Services;

public sealed class BiliTrack
{
	public string Bvid { get; set; } = "";

	public long Aid { get; set; }

	public string Title { get; set; } = "";

	public string Author { get; set; } = "";

	public long Mid { get; set; }

	public string DurationRaw { get; set; } = "";

	public long Play { get; set; }

	public string Pic { get; set; } = "";

	public string TypeName { get; set; } = "";

	public TimeSpan Duration
	{
		get
		{
			try
			{
				string[] parts = DurationRaw.Split(':');
				if (parts.Length == 2)
					return TimeSpan.FromMinutes(int.Parse(parts[0])).Add(TimeSpan.FromSeconds(int.Parse(parts[1])));
				if (parts.Length == 3)
					return TimeSpan.FromHours(int.Parse(parts[0])).Add(TimeSpan.FromMinutes(int.Parse(parts[1]))).Add(TimeSpan.FromSeconds(int.Parse(parts[2])));
			}
			catch
			{
			}
			return TimeSpan.Zero;
		}
	}

	public string Display => BiliClient.StripHtml(Title);
}

public static class BiliClient
{
	private static readonly int[] MixinKeyTab =
	{
		46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52
	};

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(15)
	};

	private static string _buvidCookie = "";
	// wbi 签名用的 mixin key 每天才换一次，没必要每次搜索都去拉一遍 nav。
	// 原来每次搜索都重新取 buvid + nav，等于一次搜索多打两个请求，
	// 既慢又更容易撞上 B 站的 IP 限流。
	private static string _mixinKey = "";
	private static DateTime _mixinKeyAt = DateTime.MinValue;

	// 解析B站视频通常需要音频与视频分离，这里只下载音频
	private static readonly string BbDownCacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "bbdown");

	static BiliClient()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
		Http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
		Http.DefaultRequestHeaders.Add("Origin", "https://www.bilibili.com");
		// 实测：老的非 WBI 搜索接口会挑请求头，缺 Accept / Accept-Language 直接回 412。
		// 补齐浏览器那套基础头，WBI 和老接口都能过。
		Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
		Http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		Http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		Http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
	}

	public static string LoginDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "BBDownLogin");

	public static string CookieFile => Path.Combine(LoginDir, "BBDown.data");

	public static bool IsBbDownLoggedIn => File.Exists(CookieFile);

	public static string? BbDownToolPath => ResolveBbDownExe();

	/// <summary>用户在设置中粘贴的浏览器 Cookie（供搜索/收藏等网页接口使用）。</summary>
	public static string UserCookie
	{
		get
		{
			string c = MusicBox.Settings.SettingsManager.Instance.BiliCookie ?? "";
			return string.IsNullOrWhiteSpace(c) ? "" : c.Trim();
		}
	}

	public static bool HasUserCookie => !string.IsNullOrWhiteSpace(UserCookie);

	private static string CookieHeader => string.IsNullOrWhiteSpace(UserCookie) ? _buvidCookie : UserCookie;

	/// <summary>运行 BBDown 登录。登录 Cookie 只保存在本机 LoginDir，绝不复用 E:\BBdown 下的用户数据。</summary>
	public static async Task<byte> LoginBbDownAsync(Action<string?>? onLine, CancellationToken ct = default)
	{
		string? exe = ResolveBbDownExe();
		if (string.IsNullOrEmpty(exe))
		{
			return 2;
		}
		try
		{
			CleanupLoginArtifacts();
			Directory.CreateDirectory(LoginDir);
			Directory.CreateDirectory(BbDownCacheDir);
			var psi = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = "login",
				WorkingDirectory = LoginDir,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
			using var proc = new Process { StartInfo = psi };
			proc.OutputDataReceived += (_, e) => onLine?.Invoke(e.Data);
			proc.ErrorDataReceived += (_, e) =>
			{
				if (string.IsNullOrWhiteSpace(e.Data)) return;
				onLine?.Invoke(e.Data);
			};
			proc.Start();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
			await proc.WaitForExitAsync(linked.Token);
			return (byte)(proc.ExitCode == 0 ? 0 : 1);
		}
		catch (OperationCanceledException)
		{
			return 3;
		}
		catch
		{
			return 4;
		}
	}

	public static void DeleteLocalCookie()
	{
		try
		{
			if (File.Exists(CookieFile))
			{
				File.Delete(CookieFile);
			}
		}
		catch
		{
		}
	}

	/// <summary>清空 BBDown 登录目录（二维码、BBDown.data、日志等残留）。</summary>
	public static void CleanupLoginArtifacts()
	{
		try
		{
			if (Directory.Exists(LoginDir))
			{
				foreach (string f in Directory.GetFiles(LoginDir))
				{
					try
					{
						File.Delete(f);
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
	}

	/// <summary>从 BBDown 登录产物 BBDown.data（JSON）里解析出 k=v; ... 的 Cookie 串，未登录/损坏返回 null。</summary>
	public static string? ReadBbDownCookieString()
	{
		try
		{
			if (!File.Exists(CookieFile))
			{
				return null;
			}
			string json = File.ReadAllText(CookieFile);
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Object)
			{
				return null;
			}
			var parts = new List<string>();
			foreach (var prop in cookies.EnumerateObject())
			{
				if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(prop.Value.GetString()))
				{
					parts.Add(prop.Name + "=" + prop.Value.GetString());
				}
			}
			return parts.Count == 0 ? null : string.Join("; ", parts);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>上一次搜索是不是被 B 站风控挡了，而不是真的没搜到。
	/// 这两种情况界面上必须区分开：一个是「没找到歌」，一个是「被限流了、过会儿再试」，
	/// 原来一律渲染成空列表，用户完全看不出到底发生了什么。</summary>
	public static bool LastSearchBlocked { get; private set; }

	/// <summary>多关键词连续搜索时，调用方需要把多轮结果汇总成一个风控状态，
	/// 所以提供一个回写入口。</summary>
	public static void SetLastSearchBlocked(bool blocked) => LastSearchBlocked = blocked;

	public static async Task<List<BiliTrack>> SearchAsync(string keyword, int maxAttempts = 3)
	{
		var result = new List<BiliTrack>();
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return result;
		}
		// 之前这里整段包在 catch{} 里，返回空列表也不留任何痕迹，
		// 结果就是「B站搜不出来」但没人知道卡在哪一步。现在每种失败都记进日志。
		LastSearchBlocked = false;
		string lastReason = "";
		for (int attempt = 0; attempt < maxAttempts; attempt++)
		{
			try
			{
				var tryResult = await SearchWbiOnceAsync(keyword);
				if (tryResult.Tracks.Count > 0)
				{
					result = tryResult.Tracks;
					break;
				}
				lastReason = tryResult.Reason;
				if (!tryResult.Challenge)
				{
					// 不是风控，就是真没结果，再打多少次都一样
					break;
				}
				LastSearchBlocked = true;
				// 风控时别再猛打。原来是 500/1000ms 连打三次，
				// 而 B 站是按 IP 限流的，这么打只会把限流窗口越撑越长，结果永远返回空。
				// 现在退避拉长，并且换非 WBI 的老接口再试一次（它走另一套风控）。
				await Task.Delay(1500 * (attempt + 1));
				if (attempt == 0)
				{
					var legacy = await SearchLegacyAsync(keyword);
					if (legacy.Count > 0)
					{
						result = legacy;
						LastSearchBlocked = false;
						break;
					}
				}
			}
			catch (Exception ex)
			{
				lastReason = ex.GetType().Name + ": " + ex.Message;
				LogHelper.LogError(ex, "B站搜索异常: " + keyword);
			}
		}
		if (result.Count == 0)
		{
			// 空结果必须留痕，否则界面上只会看到一片空白，无从排查
			LogHelper.Log("B站搜索无结果: " + keyword + "  原因=" + (lastReason == "" ? "未知" : lastReason));
		}
		return result;
	}

	/// <summary>走 WBI 签名的搜索。challenge=true 表示被风控挡了，而不是「没搜到」。</summary>
	/// <summary>单次 WBI 搜索的结果。Challenge=true 表示被风控挡了，而不是「没搜到」；
	/// 两种情况要分开处理，async 方法不能用 out 参数，所以用结构体带回来。</summary>
	private readonly struct SearchAttempt
	{
		public readonly List<BiliTrack> Tracks;
		public readonly string Reason;
		public readonly bool Challenge;
		public SearchAttempt(List<BiliTrack> tracks, string reason, bool challenge)
		{
			Tracks = tracks;
			Reason = reason;
			Challenge = challenge;
		}
	}

	/// <summary>从 Cookie 里取 csrf（bili_jct）。没有就是未登录/登录过期。</summary>
	public static bool TryGetCsrf(out string csrf)
	{
		csrf = "";
		string raw = UserCookie;
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		foreach (string part in raw.Split(';'))
		{
			string kv = part.Trim();
			if (kv.StartsWith("bili_jct=", StringComparison.OrdinalIgnoreCase))
			{
				csrf = kv.Substring(9).Trim();
				return csrf.Length > 0;
			}
		}
		return false;
	}

	/// <summary>发一个 WBI 签名的 POST，返回原始 JSON。
	/// 发评论要用：接口要求 wts + w_rid + csrf 三样齐全。</summary>
	public static async Task<string> PostWbiAsync(string path, Dictionary<string, string> form, string csrf)
	{
		string cookie = CookieHeader;
		string mixinKey = await GetMixinKeyAsync(cookie);
		var prms = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var kv in form)
		{
			prms[kv.Key] = kv.Value;
		}
		// csrf 按 B 站要求也参与签名，所以要在算 w_rid 之前放进去
		prms["csrf"] = csrf;
		prms["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
		var qb = new StringBuilder();
		bool first = true;
		foreach (var kv in prms)
		{
			if (!first) qb.Append('&');
			first = false;
			qb.Append(kv.Key).Append('=').Append(FilterValue(kv.Value));
		}
		string wRid = Md5Hex(qb.ToString() + FilterValue(mixinKey));
		string url = "https://api.bilibili.com" + path + "?" + qb + "&w_rid=" + wRid;

		using var req = new HttpRequestMessage(HttpMethod.Post, url)
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf"] = csrf })
		};
		req.Headers.TryAddWithoutValidation("Cookie", cookie);
		req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
		req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
		var resp = await Http.SendAsync(req);
		return await resp.Content.ReadAsStringAsync();
	}

	private static async Task<SearchAttempt> SearchWbiOnceAsync(string keyword)
	{
		var result = new List<BiliTrack>();
		string cookie = await GetFreshCookieAsync();
		string mixinKey = await GetMixinKeyAsync(cookie);
		var prms = new SortedDictionary<string, string>(StringComparer.Ordinal)
		{
			["search_type"] = "video",
			["keyword"] = keyword.Trim(),
			["page"] = "1",
			["platform"] = "web",
			["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
		};
		var qb = new StringBuilder();
		bool first = true;
		foreach (var kv in prms)
		{
			if (!first) qb.Append('&');
			first = false;
			qb.Append(kv.Key).Append('=').Append(FilterValue(kv.Value));
		}
		string query = qb.ToString();
		string wRid = Md5Hex(query + FilterValue(mixinKey));
		string url = "https://api.bilibili.com/x/web-interface/wbi/search/type?" + query + "&w_rid=" + wRid;

		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", CookieHeader);
		var resp = await Http.SendAsync(req);
		string body = await resp.Content.ReadAsStringAsync();
		if (resp.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
		{
			return new SearchAttempt(result, "HTTP 412 PreconditionFailed", true);
		}
		if (body.Contains("v_voucher") || body.Contains("captcha") || body.Length < 200)
		{
			string why = body.Contains("v_voucher") ? "反爬 v_voucher"
				: body.Contains("captcha") ? "反爬 captcha"
				: $"响应过短 len={body.Length}";
			return new SearchAttempt(result, why, true);
		}
		using var doc = JsonDocument.Parse(body);
		var root = doc.RootElement;
		int code = root.TryGetProperty("code", out var cd) && cd.ValueKind == JsonValueKind.Number ? cd.GetInt32() : 0;
		if (code != 0)
		{
			string msg = root.TryGetProperty("message", out var mg) ? mg.GetString() ?? "" : "";
			return new SearchAttempt(result, $"业务错误 code={code} msg={msg}", false);
		}
		string reason = "";
		if (root.TryGetProperty("data", out var data) && data.TryGetProperty("result", out var res))
		{
			foreach (var item in res.EnumerateArray())
			{
				result.Add(ParseTrack(item));
			}
		}
		else
		{
			reason = "响应里没有 data.result";
		}
		if (result.Count == 0)
		{
			reason = "data.result 为空数组";
		}
		return new SearchAttempt(result, reason, false);
	}

	/// <summary>非 WBI 的老搜索接口。它走的是另一套风控，
	/// 实测 WBI 被挑战时它还能出结果，用作兜底。</summary>
	private static async Task<List<BiliTrack>> SearchLegacyAsync(string keyword)
	{
		var result = new List<BiliTrack>();
		try
		{
			string url = "https://api.bilibili.com/x/web-interface/search/type?search_type=video"
				+ "&keyword=" + Uri.EscapeDataString(keyword.Trim()) + "&page=1";
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.Add("Cookie", CookieHeader);
			var resp = await Http.SendAsync(req);
			if (!resp.IsSuccessStatusCode)
			{
				return result;
			}
			string body = await resp.Content.ReadAsStringAsync();
			if (body.Contains("v_voucher") || body.Contains("captcha") || body.Length < 200)
			{
				return result;
			}
			using var doc = JsonDocument.Parse(body);
			if (doc.RootElement.TryGetProperty("data", out var data)
				&& data.TryGetProperty("result", out var res))
			{
				foreach (var item in res.EnumerateArray())
				{
					result.Add(ParseTrack(item));
				}
			}
		}
		catch
		{
		}
		return result;
	}

	/// <summary>B站搜索返回的标题里带 &lt;em class="keyword"&gt; 高亮标签和 &amp;amp; 之类实体，
	/// 不洗一遍的话界面上会直接显示这些标签文本。</summary>
	private static string CleanHtml(string s)
	{
		if (string.IsNullOrEmpty(s)) return s;
		string t = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
		return System.Net.WebUtility.HtmlDecode(t).Trim();
	}

	private static BiliTrack ParseTrack(JsonElement item)
	{
		var t = new BiliTrack();
		if (item.TryGetProperty("bvid", out var bv)) t.Bvid = bv.GetString() ?? "";
		if (item.TryGetProperty("aid", out var aid)) t.Aid = aid.GetInt64();
		if (item.TryGetProperty("title", out var title)) t.Title = CleanHtml(title.GetString() ?? "");
		if (item.TryGetProperty("author", out var author)) t.Author = author.GetString() ?? "";
		if (item.TryGetProperty("mid", out var mid)) t.Mid = mid.GetInt64();
		if (item.TryGetProperty("duration", out var dur)) t.DurationRaw = dur.ToString();
		if (item.TryGetProperty("play", out var play)) t.Play = play.ValueKind == JsonValueKind.Number ? play.GetInt64() : 0;
		if (item.TryGetProperty("pic", out var pic)) t.Pic = (pic.GetString() ?? "").StartsWith("//") ? "https:" + pic.GetString() : pic.GetString() ?? "";
		if (item.TryGetProperty("typename", out var typeName)) t.TypeName = typeName.GetString() ?? "";
		return t;
	}

	private static async Task<string> GetFreshCookieAsync()
	{
		// buvid 是访客标识，稳定复用才正常。原来每次搜索都重新 spi 一次，
		// 等于每次都换一个新访客，还白搭一个请求 —— 既像刷接口，又更容易被风控。
		if (!string.IsNullOrEmpty(_buvidCookie))
		{
			return _buvidCookie;
		}
		try
		{
			string spi = await Http.GetStringAsync("https://api.bilibili.com/x/frontend/finger/spi");
			using var doc = JsonDocument.Parse(spi);
			var data = doc.RootElement.GetProperty("data");
			string b3 = data.GetProperty("b_3").GetString() ?? "";
			string b4 = data.GetProperty("b_4").GetString() ?? "";
			_buvidCookie = $"buvid3={b3}; buvid4={b4}; b_nut={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
		}
		catch
		{
		}
		return _buvidCookie;
	}

	private static async Task<string> GetMixinKeyAsync(string cookie)
	{
		// mixin key 一天才换一次，缓存两小时完全够用。
		if (_mixinKey.Length == 32 && (DateTime.Now - _mixinKeyAt) < TimeSpan.FromHours(2))
		{
			return _mixinKey;
		}
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
			req.Headers.Add("Cookie", cookie);
			var resp = await Http.SendAsync(req);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			var data = doc.RootElement.GetProperty("data");
			var wbi = data.GetProperty("wbi_img");
			string? imgUrl = wbi.GetProperty("img_url").GetString();
			string? subUrl = wbi.GetProperty("sub_url").GetString();
			string imgKey = imgUrl is null ? "" : Path.GetFileNameWithoutExtension(new Uri(imgUrl).AbsolutePath);
			string subKey = subUrl is null ? "" : Path.GetFileNameWithoutExtension(new Uri(subUrl).AbsolutePath);
			string mixed = MixinKey(FilterValue(imgKey + subKey));
			if (mixed.Length == 32)
			{
				_mixinKey = mixed;
				_mixinKeyAt = DateTime.Now;
			}
			return mixed;
		}
		catch
		{
			// nav 挂了但之前缓存过 key 的话，继续用旧的总比整次搜索失败强
			return _mixinKey;
		}
	}

	private static string MixinKey(string orig)
	{
		var sb = new StringBuilder(32);
		for (int i = 0; i < 32; i++) sb.Append(orig[MixinKeyTab[i]]);
		return sb.ToString();
	}

	private static string FilterValue(string s)
	{
		var sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			switch (c)
			{
				case '!':
				case '\'':
				case '(':
				case ')':
				case '*':
					continue;
				default:
					sb.Append(c);
					break;
			}
		}
		return sb.ToString();
	}

	private static string Md5Hex(string s)
	{
		using var md5 = MD5.Create();
		byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
		var sb = new StringBuilder(hash.Length * 2);
		foreach (byte b in hash) sb.Append(b.ToString("x2"));
		return sb.ToString();
	}

	// ---------- 下载 ----------

	public static string? ResolveBbDownExe()
	{
		string? configured = MusicBox.Settings.SettingsManager.Instance.BbDownPath;
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
		{
			return configured;
		}
		string tools = Path.Combine(AppContext.BaseDirectory, "Tools", "BBDown.exe");
		if (File.Exists(tools))
		{
			return tools;
		}
		if (File.Exists(@"E:\BBdown\BBDown.exe"))
		{
			return @"E:\BBdown\BBDown.exe";
		}
		return null;
	}

	public static string CacheDir => BbDownCacheDir;

	public static async Task<string?> DownloadAudioAsync(BiliTrack track, Action<string>? progress, CancellationToken ct = default)
	{
		string? exe = ResolveBbDownExe();
		if (string.IsNullOrEmpty(exe))
		{
			return null;
		}
		try
		{
			progress?.Invoke("正在解析B站视频信息…");
			Directory.CreateDirectory(BbDownCacheDir);
			Directory.CreateDirectory(LoginDir);
			var psi = new ProcessStartInfo
			{
				FileName = exe,
				Arguments = $"\"{track.Bvid}\" --audio-only -p 1 --skip-subtitle --work-dir \"{BbDownCacheDir}\"",
				WorkingDirectory = LoginDir,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8
			};
			using var proc = new Process { StartInfo = psi };
			proc.OutputDataReceived += (_, e) =>
			{
				string line = e.Data ?? "";
				var m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*%");
				if (m.Success)
				{
					progress?.Invoke("下载中 " + m.Groups[1].Value + "%");
				}
				else if (line.Contains("作品已下载", StringComparison.OrdinalIgnoreCase) || line.Contains("下载完成", StringComparison.OrdinalIgnoreCase))
				{
					progress?.Invoke("下载完成");
				}
			};
			proc.Start();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
			await proc.WaitForExitAsync(linked.Token);
			if (proc.ExitCode != 0)
			{
				return null;
			}
			FileInfo? best = FindNewestAudio(BbDownCacheDir);
			return best?.FullName;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch
		{
			return null;
		}
	}

	private static FileInfo? FindNewestAudio(string dir)
	{
		if (!Directory.Exists(dir)) return null;
		var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
			.Where(f => new[] { ".m4a", ".mp3", ".flac", ".aac", ".wav", ".ogg" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
			.Select(f => new FileInfo(f))
			.Where(f => f.Length > 1024)
			.OrderByDescending(f => f.LastWriteTime);
		return files.FirstOrDefault();
	}

	public static string FormatPlay(long play)
	{
		if (play >= 10000) return (play / 10000.0).ToString("0.0") + "万";
		if (play >= 1000) return (play / 1000.0).ToString("0.0") + "k";
		return play.ToString();
	}

	// ---------- 用户收藏（网页 Cookie）----------

	/// <summary>使用设置中粘贴的 B 站 Cookie 加载账号信息。</summary>
	public static async Task<(bool ok, long mid, string uname)> FetchAccountAsync(CancellationToken ct = default)
	{
		try
		{
			var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
			req.Headers.Add("Cookie", CookieHeader);
			var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;
			bool isLogin = root.TryGetProperty("data", out var data) && data.TryGetProperty("isLogin", out var il) && il.GetBoolean();
			if (!isLogin) return (false, 0, "");
			long mid = data.TryGetProperty("mid", out var m) ? m.GetInt64() : 0;
			string uname = data.TryGetProperty("uname", out var u) ? u.GetString() ?? "" : "";
			return (mid > 0, mid, uname);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("B站账号信息异常: " + ex.Message);
			return (false, 0, "");
		}
	}

	/// <summary>加载用户收藏夹里的视频（含收藏夹名称）。未登录/失败返回 null。</summary>
	public static async Task<(string folder, List<BiliTrack> tracks)?> FetchUserFavoritesAsync(int limit = 20, CancellationToken ct = default)
	{
		if (!HasUserCookie)
		{
			return null;
		}
		try
		{
			var (ok, mid, _) = await FetchAccountAsync(ct);
			if (!ok || mid <= 0) return null;

			long mediaId = 0;
			string folderName = "";
			var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid=" + mid);
			req.Headers.Add("Cookie", CookieHeader);
			var resp = await Http.SendAsync(req, ct);
			string body = await resp.Content.ReadAsStringAsync(ct);
			using (var doc = JsonDocument.Parse(body))
			{
				if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("list", out var list))
				{
					foreach (var item in list.EnumerateArray())
					{
						long id = item.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
						if (id <= 0) continue;
						mediaId = id;
						folderName = item.TryGetProperty("title", out var tn) ? tn.GetString() ?? "" : "";
						break;
					}
				}
			}
			if (mediaId <= 0) return null;

			var tracks = new List<BiliTrack>();
			var req2 = new HttpRequestMessage(HttpMethod.Get,
				"https://api.bilibili.com/x/v3/fav/resource/list?media_id=" + mediaId + "&pn=1&ps=" + limit + "&platform=web");
			req2.Headers.Add("Cookie", CookieHeader);
			var resp2 = await Http.SendAsync(req2, ct);
			string body2 = await resp2.Content.ReadAsStringAsync(ct);
			using (var doc2 = JsonDocument.Parse(body2))
			{
				if (doc2.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("medias", out var medias))
				{
					foreach (var item in medias.EnumerateArray())
					{
						var t = new BiliTrack();
						if (item.TryGetProperty("bvid", out var bv)) t.Bvid = bv.GetString() ?? "";
						if (item.TryGetProperty("title", out var title)) t.Title = StripHtml(title.GetString() ?? "");
						if (item.TryGetProperty("cover", out var cover)) t.Pic = (cover.GetString() ?? "").StartsWith("//") ? "https:" + cover.GetString() : cover.GetString() ?? "";
						if (item.TryGetProperty("duration", out var dur)) t.DurationRaw = FormatSecondsFromInt(dur.GetInt32());
						if (item.TryGetProperty("cnt_info", out var cnt) && cnt.TryGetProperty("play", out var play))
							t.Play = play.GetInt64();
						if (item.TryGetProperty("upper", out var up) && up.TryGetProperty("name", out var upn))
							t.Author = upn.GetString() ?? "";
						if (t.Bvid.Length > 0 && t.Title.Length > 0) tracks.Add(t);
					}
				}
			}
			return (folderName, tracks);
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("B站收藏夹加载异常: " + ex.Message);
			return null;
		}
	}

	private static string FormatSecondsFromInt(int total)
	{
		try
		{
			return TimeSpan.FromSeconds(total).ToString(total >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
		}
		catch
		{
			return "";
		}
	}

	public static string StripHtml(string input)
	{
		if (string.IsNullOrEmpty(input)) return "";
		return Regex.Replace(input, "<.*?>", "").Replace("&amp;", "&").Trim();
	}
}