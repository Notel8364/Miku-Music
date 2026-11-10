using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBox.Services;

/// <summary>本地音乐文件的「时长」补齐。
///
/// 为什么要单独一份：曲库 / 最近播放 / 我的收藏这三页的行手里只有一个文件路径，
/// 而 <see cref="SavedTrack.DurationMs"/> 只在「播过的那一刻」才填得上
/// （播放器解码器给出总长）。没播过的本地文件那条记录里就是 0，
/// 界面上自然没有时长——不是显示逻辑漏了，是数据本来就没。
///
/// 这里的做法：行要显示时长时问一句 <see cref="GetDuration"/>，
/// 有缓存就当场给出；没有就把文件丢进后台队列解码一次拿总长，
/// 拿到之后广播 <see cref="Updated"/>，让当前在屏幕上那一页重画一遍。
/// 解码全在后台线程、一次只开一个文件，既不拖住界面，
/// 也不会几十个文件一起把磁盘和解码器抢爆。</summary>
public static class LocalTrackMeta
{
	/// <summary>路径 → 时长（毫秒）。值 0 表示「查过但拿不到」，
	/// 之后不再重复探测，免得每次重画都去开一次文件。</summary>
	private static readonly ConcurrentDictionary<string, long> Durations =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>已经在队列里或正在探测的路径，挡住重复入队。</summary>
	private static readonly ConcurrentDictionary<string, byte> Probing =
		new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentQueue<string> Queue = new();

	/// <summary>后台循环是不是在跑。0 = 没跑，1 = 在跑。</summary>
	private static int _workerRunning;

	// 广播合并：一次刷新通常会排队几十首歌，探一首广播一次的话界面会连着抖几十下。
	// 这里攒到 400ms 一次统一通知，多首歌的重画就并成一次。
	private static readonly object _broadcastLock = new();
	private static DateTime _lastBroadcastUtc = DateTime.MinValue;
	private static bool _broadcastScheduled;
	private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(400);

	/// <summary>某首歌的时长补齐了。可能来自后台线程，订阅方自己切回 UI 线程。</summary>
	public static event Action? Updated;

	/// <summary>取这个文件的时长。已探到就立刻返回；没探过就后台探，这次先返回 0。</summary>
	public static long GetDuration(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return 0;
		}
		if (Durations.TryGetValue(path!, out long ms))
		{
			return ms;
		}
		Enqueue(path!);
		return 0;
	}

	/// <summary>已知时长直接记上（比如正在播的那首从播放器拿到的）。</summary>
	public static void NoteDuration(string? path, long ms)
	{
		if (string.IsNullOrWhiteSpace(path) || ms <= 0)
		{
			return;
		}
		Durations[path!] = ms;
	}

	private static void Enqueue(string path)
	{
		if (!Probing.TryAdd(path, 0))
		{
			return;
		}
		Queue.Enqueue(path);
		// 没有在跑的循环就起一个；已经在跑的会把新任务一起带走。
		if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
		{
			_ = Task.Run(WorkerLoopAsync);
		}
	}

	private static async Task WorkerLoopAsync()
	{
		try
		{
			while (Queue.TryDequeue(out string? path))
			{
				long ms = await Task.Run(() => ReadDuration(path)).ConfigureAwait(false);
				Durations[path] = ms;
				Probing.TryRemove(path, out _);
				if (ms > 0)
				{
					BroadcastUpdated();
				}
			}
		}
		catch
		{
			// 兜住任何漏出来的异常，别把后台循环打死
		}
		finally
		{
			Interlocked.Exchange(ref _workerRunning, 0);
		}
		// 收尾那一刻可能刚好又有人入队（它比过了 CAS 才发现循环已停），
		// 那样就没人接手了 —— 这里再看一眼队列补一次。
		if (!Queue.IsEmpty && Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
		{
			_ = Task.Run(WorkerLoopAsync);
		}
	}

	private static void BroadcastUpdated()
	{
		lock (_broadcastLock)
		{
			TimeSpan wait = _lastBroadcastUtc == DateTime.MinValue
				? TimeSpan.Zero
				: BroadcastInterval - (DateTime.UtcNow - _lastBroadcastUtc);
			if (wait > TimeSpan.Zero)
			{
				if (_broadcastScheduled)
				{
					return;
				}
				_broadcastScheduled = true;
				_ = Task.Run(async () =>
				{
					await Task.Delay(wait).ConfigureAwait(false);
					lock (_broadcastLock)
					{
						_lastBroadcastUtc = DateTime.UtcNow;
						_broadcastScheduled = false;
					}
					Updated?.Invoke();
				});
				return;
			}
			_lastBroadcastUtc = DateTime.UtcNow;
		}
		Updated?.Invoke();
	}

	/// <summary>解码一个音频文件拿总长；拿不到返回 0。
	/// 走 <see cref="MusicService.CreateReader"/>：和真正播放时用的是同一套
/// 「按后缀挑解码器」的逻辑，不会出现「列表里显示 3:20、播起来是 3:18」。</summary>
	private static long ReadDuration(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return 0;
		}
		try
		{
			if (!File.Exists(path))
			{
				return 0;
			}
			using (NAudio.Wave.WaveStream reader = MusicService.CreateReader(path))
			{
				long ms = (long)reader.TotalTime.TotalMilliseconds;
				return ms > 0 ? ms : 0;
			}
		}
		catch
		{
			return 0;
		}
	}
}
