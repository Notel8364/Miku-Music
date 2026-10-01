using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

	public static async Task<string?> DownloadSongAsync(KugouTrack track, Action<double>? progress = null, CancellationToken ct = default)
	{
		string? playUrl = await GetPlayUrlAsync(track, ct);
		if (string.IsNullOrEmpty(playUrl)) return null;
		try
		{
			Directory.CreateDirectory(CacheDir);
			string ext = DetectExtension(playUrl);
			string file = Path.Combine(CacheDir, $"{track.Hash}.{ext}");
			if (File.Exists(file) && new FileInfo(file).Length > 1024)
			{
				return file;
			}
			using var response = await Http.GetAsync(playUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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

	public static string FormatFileSize(long bytes)
	{
		if (bytes <= 0) return "";
		if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + "MB";
		if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + "KB";
		return bytes + "B";
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
}