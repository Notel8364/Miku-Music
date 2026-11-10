using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBox.Services;

/// <summary>
/// 在线音频下载的并发协调器。
///
/// 两个目的：
/// 1) 去重：预加载和真正播放可能同时要同一首歌（同一 hash / songId）。
///    若各写各的，FileMode.Create + FileShare.None 会让后来者直接抛异常，
///    表现为「明明预取过了，切歌却播放失败」。同一个目标文件只跑一次下载，
///    后来的调用复用同一个 Task。
/// 2) 原子落盘：先写 .part，写完再 Move 成正式文件。预取被取消/失败时
///    顶多留个 .part，不会被 File.Exists 当成完整缓存命中而播到半截。
///
/// 各平台只管「把流写进 part 路径」，缓存键仍由各自按 hash/songId 计算，
/// 因此预取与播放命中同一个文件。
/// </summary>
public static class DownloadCoordinator
{
	private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> InFlight =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>缓存文件是否可用（存在且不像半截文件）。</summary>
	public static bool IsUsable(string path)
	{
		try
		{
			return File.Exists(path) && new FileInfo(path).Length > 1024;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>拿本地缓存路径：已缓存直接返回；否则并发安全地下载一次。
	/// writePart 负责把内容写进给定的 .part 路径，返回是否写完整。
	/// 注意这里刻意不把调用方的 CancellationToken 传进共享 Task：
	/// 预取方取消时，不能把复用同一 Task 的播放方也一起取消掉。</summary>
	public static Task<string?> GetOrDownloadAsync(
		string dest,
		Func<string, Task<bool>> writePart)
	{
		if (IsUsable(dest))
		{
			return Task.FromResult<string?>(dest);
		}
		Lazy<Task<string?>> lazy = InFlight.GetOrAdd(dest,
			key => new Lazy<Task<string?>>(
				() => RunAsync(key, writePart),
				LazyThreadSafetyMode.ExecutionAndPublication));
		return lazy.Value;
	}

	private static async Task<string?> RunAsync(string dest, Func<string, Task<bool>> writePart)
	{
		try
		{
			if (IsUsable(dest))
			{
				return dest;
			}
			string? dir = Path.GetDirectoryName(dest);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
			string part = dest + ".part";
			try
			{
				bool ok = await writePart(part).ConfigureAwait(false);
				if (!ok)
				{
					return null;
				}
				// 目标可能已被另一条路径写好；overwrite 保证最终一定是完整文件。
				File.Move(part, dest, true);
			}
			finally
			{
				try
				{
					if (File.Exists(part))
					{
						File.Delete(part);
					}
				}
				catch
				{
				}
			}
			return IsUsable(dest) ? dest : null;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("下载协调器异常: " + ex.Message);
			return null;
		}
		finally
		{
			InFlight.TryRemove(dest, out _);
		}
	}
}
