using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBox.Services;

/// <summary>LRCLIB 的返回结构。只取用得到的字段。</summary>
public sealed class LrcLibResult
{
	[JsonPropertyName("trackName")]
	public string? TrackName { get; set; }

	[JsonPropertyName("artistName")]
	public string? ArtistName { get; set; }

	/// <summary>带时间戳的 LRC，line 级别。</summary>
	[JsonPropertyName("syncedLyrics")]
	public string? SyncedLyrics { get; set; }

	[JsonPropertyName("plainLyrics")]
	public string? PlainLyrics { get; set; }

	/// <summary>翻译，也是带时间戳的 LRC。库里有几成歌带这个。</summary>
	[JsonPropertyName("translation")]
	public string? Translation { get; set; }

	[JsonPropertyName("instrumental")]
	public bool Instrumental { get; set; }

	/// <summary>歌曲总时长（秒）。模糊搜索时拿它判断是不是同一首。</summary>
	[JsonPropertyName("duration")]
	public int Duration { get; set; }
}

/// <summary>第二歌词源：LRCLIB。
///
/// 为什么需要它：网易云的公开接口只给 lrc 和 tlyric，实测连 yrc（逐字）
/// 都不返回，tlyric 也经常是空的——很多歌既没有逐字也没有翻译。
/// LRCLIB 是社区维护的开源库，覆盖面比单一大平台广，而且带 translation 字段，
/// 正好补上「翻译经常是空的」这个洞。
///
/// 代价是它按「歌手 + 歌名 + 时长」精确匹配，所以必须拿到歌曲时长；
/// 时长对不上就直接放弃，绝不硬凑——歌词配错比没有歌词糟糕得多。</summary>
public static class LrcLibClient
{
	private const string Base = "https://lrclib.net/api";

	private static readonly HttpClient Http = new()
	{
		Timeout = TimeSpan.FromSeconds(15)
	};

	/// <summary>LRCLIB 要求带一个能识别出调用方的 UA，纯空 UA 会被拒。</summary>
	private static void EnsureHeaders()
	{
		if (!Http.DefaultRequestHeaders.Contains("User-Agent"))
		{
			Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "MusicBox/1.0 (desktop lyrics)");
		}
	}

	/// <summary>按歌名+歌手+时长精确查。查不到返回 null。</summary>
	public static async Task<LrcLibResult?> GetAsync(string title, string? artist, int durationSec,
		CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(title) || durationSec <= 0)
		{
			return null;
		}
		try
		{
			EnsureHeaders();
			string url = $"{Base}/get?track_name={Uri.EscapeDataString(title)}"
				+ $"&artist_name={Uri.EscapeDataString(artist ?? "")}"
				+ $"&duration={durationSec}";
			string json = await Http.GetStringAsync(url, ct);
			return JsonSerializer.Deserialize<LrcLibResult>(json);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch
		{
			// 查不到很正常（库里没这首歌、时长对不上、网络不通），不当错误处理。
			// 上层会继续用网易云的结果。
			return null;
		}
	}

	/// <summary>精确查不到时按歌名模糊搜，取时长最接近的一条。</summary>
	public static async Task<LrcLibResult?> SearchAsync(string title, string? artist, int durationSec,
		CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			return null;
		}
		try
		{
			EnsureHeaders();
			string url = $"{Base}/search?q={Uri.EscapeDataString(title)}"
				+ (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
			string json = await Http.GetStringAsync(url, ct);
			LrcLibResult[]? hits = JsonSerializer.Deserialize<LrcLibResult[]>(json);
			if (hits == null || hits.Length == 0)
			{
				return null;
			}
			LrcLibResult? best = null;
			int bestDiff = int.MaxValue;
			foreach (LrcLibResult h in hits)
			{
				if (string.IsNullOrWhiteSpace(h.SyncedLyrics))
				{
					continue;
				}
				// 没给时长就拿第一条，LRCLIB 的搜索结果本身已经按相关度排过序了。
				if (durationSec <= 0)
				{
					return h;
				}
				// 时长差 2 秒以内才认。同名翻唱、现场版差的就是几秒，
				// 差太远说明根本不是同一首，配错了比空着更糟。
				int diff = Math.Abs(h.Duration - durationSec);
				if (diff < bestDiff)
				{
					bestDiff = diff;
					best = h;
				}
			}
			return bestDiff <= 2 ? best : null;
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

	/// <summary>逐级降级地查一次：先精确，再模糊。都没有就返回 null。</summary>
	public static async Task<LyricBundle?> FetchBundleAsync(string title, string? artist, int durationSec,
		CancellationToken ct = default)
	{
		LrcLibResult? hit = await GetAsync(title, artist, durationSec, ct);
		hit ??= await SearchAsync(title, artist, durationSec, ct);
		if (hit == null)
		{
			return null;
		}
		// LRCLIB 没有逐字时间，第二个参数给 null，走插值兜底。
		LyricBundle b = LyricBundle.Parse(hit.SyncedLyrics ?? hit.PlainLyrics ?? "", hit.Translation, null);
		return b.Count > 0 ? b : null;
	}
}
