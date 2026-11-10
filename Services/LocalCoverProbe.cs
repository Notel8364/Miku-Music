using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MusicBox.Services;

/// <summary>
/// 本地音乐封面在线探测：设置页「主动探测 VIP 音乐」开着时，曲库/收藏夹里
/// 没有内嵌或旁路封面的本地曲目，后台按「歌名 + 歌手」在网易云 / QQ 在线源
/// 搜索匹配，把首个命中的专辑封面补给那一行。
///
/// 为什么挂在 VipProbe 的闸门上：这和 VIP 实测一样是「给列表行补信息」的
/// 后台请求，两类合起来也只允许并发 3，避免请求翻倍、互相抢带宽。
///
/// 触发时机由 TrackRows 的封面懒加载视口机制把关：行滚进可视区才探测，
/// 视口外的行绝不发请求 —— 用户不看的歌不值得烧流量。
///
/// 缓存与去重：
/// - 命中结果按「local:文件路径」这样的稳定键落进 CoverLoader 共用缓存
///   （内存字节 + 磁盘跨会话），同一首歌下次直接命中，不再发请求；
/// - 探测过没命中的记负缓存（null），同样不再重复请求；
/// - 同一首在飞只放一个请求，后到的行等它出结果。
///
/// 失败一律静默：探测是锦上添花，绝不打扰播放、不弹任何提示。
/// </summary>
public static class LocalCoverProbe
{
	// 探测结论：key → 封面 URL（null = 探过没命中，负缓存防重复打接口）。
	// 进程内一份就够 —— 字节本体由 CoverLoader 落盘跨会话复用，这里只记「去哪拿」。
	private static readonly ConcurrentDictionary<string, string?> Results = new(StringComparer.Ordinal);

	// 同一首在飞去重：key 相同的并发调用等第一个的结果，不重发请求。
	private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal);
	private static readonly object Sync = new();

	// 等待在飞请求出结果的上限：正常一次搜索一两秒，8 秒还不出就当失败，
	// 不让后到的行无限等。
	private static readonly TimeSpan AwaitInFlightTimeout = TimeSpan.FromSeconds(8);

	/// <summary>缓存里是否已有结论（url 非 null = 命中过）。</summary>
	public static bool TryGetCached(string key, out string? url)
	{
		return Results.TryGetValue(key, out url);
	}

	/// <summary>行进入视口后调用：查结论缓存，命中直接按 key 走 CoverLoader 显示；
	/// 没有结论就排一次后台探测，命中后把字节落进共用缓存再回 UI 线程淡入显示。
	/// title / artist 用于拼搜索词（歌手缺省就只用歌名），调用方保证 title 非空。
	/// onLoaded 在封面真正画上那一拍触发（TrackRows 的淡入动画挂在这里）。</summary>
	public static void BeginProbing(Image img, string cacheKey, string title, string artist, Action? onLoaded)
	{
		if (img == null || string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(title))
		{
			return;
		}
		_ = Task.Run(async () =>
		{
			string? url = await ResolveAsync(cacheKey, title, artist);
			if (url == null)
			{
				return; // 没命中（含负缓存与失败）：静默，行保持占位。
			}
			// 回 UI 线程再碰 Image；字节从 CoverLoader 共用缓存装载（网络仅首次）。
			var d = System.Windows.Application.Current?.Dispatcher;
			if (d == null || d.HasShutdownStarted)
			{
				return;
			}
			_ = d.BeginInvoke(new Action(() =>
			{
				CoverLoader.LoadByKeyAsync(img, cacheKey, url, 128, onLoaded);
			}));
		});
	}

	/// <summary>取一首本地歌的封面 URL：先看结论缓存，再等在飞的结果，
	/// 都轮不到才真正发起一次「网易云 → QQ」的顺序搜索（并发受 VipProbe 闸门管）。</summary>
	private static async Task<string?> ResolveAsync(string cacheKey, string title, string artist)
	{
		if (Results.TryGetValue(cacheKey, out string? cached))
		{
			return cached;
		}
		bool owner;
		lock (Sync)
		{
			// 进锁的间隙里可能刚好出结论，再核一次；没结论才抢在飞位。
			if (Results.TryGetValue(cacheKey, out cached))
			{
				return cached;
			}
			owner = InFlight.Add(cacheKey);
		}
		if (!owner)
		{
			// 别的线程正在探这一首：等它出结果，不重发请求（8 秒等不到就放弃）。
			DateTime deadline = DateTime.UtcNow + AwaitInFlightTimeout;
			while (DateTime.UtcNow < deadline)
			{
				if (Results.TryGetValue(cacheKey, out string? waiting))
				{
					return waiting;
				}
				bool stillFlying;
				lock (Sync)
				{
					stillFlying = InFlight.Contains(cacheKey);
				}
				if (!stillFlying)
				{
					// 在飞的已经退出但没写结论（异常路径）：本次放弃，不接棒重试。
					return Results.TryGetValue(cacheKey, out string? last) ? last : null;
				}
				await Task.Delay(200);
			}
			return null;
		}

		try
		{
			// 搜索词：歌名 + 歌手（有歌手才拼）。文件名常常带「01 - xxx」这类
			// 前缀，但平台的模糊搜索对这种词容忍度不错，先不过度清洗。
			string query = string.IsNullOrWhiteSpace(artist) ? title : title + " " + artist;
			string? url = await VipProbe.RunInGateAsync(async () =>
			{
				try
				{
					List<NeteaseTrack> hits = await NeteaseApiClient.SearchAsync(query, 5, 0);
					foreach (NeteaseTrack t in hits)
					{
						string? pic = t.AlbumInfo?.PicUrl;
						if (!string.IsNullOrWhiteSpace(pic))
						{
							return pic;
						}
					}
				}
				catch
				{
					// 网易云挂了/被风控：换 QQ 试，不记任何「受限」结论。
				}
				try
				{
					List<QqTrack> qqHits = await QqClient.SearchAsync(query, 5, 0);
					foreach (QqTrack q in qqHits)
					{
						string? cover = q.CoverUrl;
						if (!string.IsNullOrWhiteSpace(cover))
						{
							return cover;
						}
					}
				}
				catch
				{
					// QQ 也失败：本次探测放弃。
				}
				return null;
			});
			Results[cacheKey] = url; // 命中记 URL，没命中记 null（负缓存）。
			return url;
		}
		catch
		{
			// 闸门等待被打断等异常路径：记负缓存，静默放弃。
			Results.TryAdd(cacheKey, null);
			return null;
		}
		finally
		{
			lock (Sync)
			{
				InFlight.Remove(cacheKey);
			}
		}
	}
}
