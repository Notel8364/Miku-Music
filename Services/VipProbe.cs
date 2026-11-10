using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>
/// 「主动探测 VIP 音乐」的后台执行器（设置页开关，默认关）。
///
/// 为什么要有它：行上的 VIP 标签平时只在「点了播放、平台实测拒绝」那一刻
/// 才会打上 —— 用户不点的歌标签永远是空的，列表看起来好坏不分。
/// 开了这个开关后，列表加载完就在后台把没实测过的条目逐条探一遍：
/// 能播的把误标撤掉、不能播的提前打上，跟点播放用的是同一套实测判定
///（拿一次播放直链，见 <see cref="TrackAccess"/>），只是时机从「点播时」
/// 提前到「列表加载后」。
///
/// 节流设计：并发 3；同一首在飞不重排；实测过的（Playable/Restricted）直接跳过，
/// 绝不为已经知道答案的歌烧第二次请求；新的搜索/列表会把旧一批探测作废。
/// </summary>
public static class VipProbe
{
	// 并发 3：探测走的是各平台「拿直链」的轻接口，但一页 30 条、
	// 一次收藏夹几百条，并发放开了一是对接口不礼貌，二是容易触发风控，
	// 三是会和用户正在用的搜索/播放抢带宽。
	private static readonly SemaphoreSlim Gate = new(3, 3);

	// 同一首歌同时只放一个探测请求。KeyOf 出来的键带平台前缀，跨平台不会撞。
	private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal);
	private static readonly object Sync = new();

	// 代次号：每次 Schedule 自增。老一批探测跑到一半发现代次变了就退出 ——
	// 用户连着搜三五个词时，不该把已经不在屏幕上的旧结果也挨个探完。
	private static int _generation;

	/// <summary>开关是否打开（设置页「主动探测 VIP 音乐」）。</summary>
	public static bool Enabled => SettingsManager.Instance.OnlineVipProbe;

	/// <summary>借道探测闸门跑一段后台工作（与 VIP 实测共用并发 3 的上限）。
	/// 本地封面在线探测也走这里：两类都是「给列表行补信息」的后台请求，
	/// 合起来也要守同一道并发闸，免得接口请求翻倍、互相抢带宽。</summary>
	public static async Task<T> RunInGateAsync<T>(Func<Task<T>> work)
	{
		await Gate.WaitAsync();
		try
		{
			return await work();
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>按给定顺序排一批探测。调用方负责把「当前页可见行」排在前面，
	/// 这样用户正盯着的行最先拿到结论、标签最先就位；其余条目在后台慢慢补。</summary>
	public static void Schedule(IReadOnlyList<object> items)
	{
		if (!Enabled || items == null || items.Count == 0)
		{
			return;
		}
		int gen = Interlocked.Increment(ref _generation);
		_ = RunAsync(items, gen);
	}

	private static async Task RunAsync(IReadOnlyList<object> items, int gen)
	{
		foreach (object item in items)
		{
			// 开关被关掉，或者已经有新的一批排上来了：这批作废。
			if (!Enabled || Volatile.Read(ref _generation) != gen)
			{
				return;
			}
			await ProbeAsync(item);
		}
	}

	/// <summary>探测单条：拿一次播放直链，能拿到记「可播」，明确拿不到记「受限」。
	/// 判定与点播放路径完全一致（网易云的记录甚至就发生在同一个接口里）。</summary>
	private static async Task ProbeAsync(object item)
	{
		// 已经有实测结论的（点播放/上次探测留下的）不再烧请求。
		if (TrackAccess.StateOf(item) != AccessState.Unknown)
		{
			return;
		}
		string key = TrackAccess.KeyOf(item);
		lock (Sync)
		{
			if (!InFlight.Add(key))
			{
				return;
			}
		}
		try
		{
			await Gate.WaitAsync();
			try
			{
				// 排队期间可能已被点播/别的批次探完，再核一次。
				if (TrackAccess.StateOf(item) != AccessState.Unknown)
				{
					return;
				}
				switch (item)
				{
					case NeteaseTrack n:
					{
						// GetSongUrlAsync 内部会自己记 Playable/Restricted
						//（网络类失败不记，接口明确拒绝才记），这里只管调。
						await NeteaseApiClient.GetSongUrlAsync(n.Id);
						break;
					}
					case KugouTrack k:
					{
						// 拿不到播放地址 = 平台不给播（多为 VIP/付费），
						// 与 DownloadSongAsync 的判定同一套。
						string? url = await KugouClient.GetPlayUrlAsync(k);
						if (string.IsNullOrEmpty(url))
						{
							TrackAccess.NoteRestricted(item);
						}
						else
						{
							TrackAccess.NotePlayable(item);
						}
						break;
					}
					case QqTrack q when !string.IsNullOrEmpty(q.Mid):
					{
						string? url = await QqClient.GetSongUrlAsync(q.Mid!);
						if (string.IsNullOrEmpty(url))
						{
							TrackAccess.NoteRestricted(item);
						}
						else
						{
							TrackAccess.NotePlayable(item);
						}
						break;
					}
					// BiliTrack 不探测：B 站没有 VIP 拦路，实测一直是能播。
				}
			}
			finally
			{
				Gate.Release();
			}
		}
		catch (OperationCanceledException)
		{
			// 应用退出/请求取消：不算失败，也别记「受限」。
		}
		catch (Exception ex)
		{
			// 单条探测失败不影响其它条；网络抖动也不该误标成受限。
			LogHelper.LogError(ex, "VIP 探测失败: " + TrackAccess.KeyOf(item));
		}
		finally
		{
			lock (Sync)
			{
				InFlight.Remove(key);
			}
		}
	}
}
