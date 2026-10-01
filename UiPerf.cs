using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicBox.Services;

namespace MusicBox;

/// <summary>UI 线程耗时探针。只把超过阈值的操作记进日志，平时零开销。
/// 目的是把「程序未响应」变成可读的数字：到底是换页慢、切歌慢，
/// 还是某一次网络回调在 UI 线程上同步等了几秒。</summary>
public static class UiPerf
{
	/// <summary>超过这个毫秒数才值得记一笔。低于它的属于正常抖动。</summary>
	public const long SlowMs = 120;

	[ThreadStatic]
	private static Stack<(string Name, long Start)>? _stack;

	/// <summary>量一段同步代码的耗时。超出 <see cref="SlowMs"/> 就写日志。</summary>
	public static T Measure<T>(string name, Func<T> body)
	{
		_stack ??= new Stack<(string, long)>();
		_stack.Push((name, Stopwatch.GetTimestamp()));
		try
		{
			return body();
		}
		finally
		{
			var (n, start) = _stack.Pop();
			Log(name, n, Stopwatch.GetTimestamp() - start);
		}
	}

	/// <summary>量一段同步代码的耗时（无返回值）。</summary>
	public static void Measure(string name, Action body)
	{
		Measure<bool>(name, () => { body(); return true; });
	}

	/// <summary>把累计耗时也报出来：同一个操作一帧内被调用了很多次时，
	/// 单次都不慢但加起来很卡，Total 能看出来。</summary>
	[ThreadStatic]
	private static Dictionary<string, (int Count, long Ticks)>? _totals;

	public static void Log(string label, string name, long ticks)
	{
		double ms = ticks * 1000.0 / Stopwatch.Frequency;
		try
		{
			_totals ??= new Dictionary<string, (int, long)>(StringComparer.Ordinal);
			_totals.TryGetValue(label, out var acc);
			acc = (acc.Count + 1, acc.Ticks + ticks);
			_totals[label] = acc;
			if (ms >= SlowMs)
			{
				double totalMs = acc.Ticks * 1000.0 / Stopwatch.Frequency;
				LogHelper.Log($"[perf] {label} 本次 {ms:F0}ms，本次以来累计 {acc.Count} 次 / {totalMs:F0}ms");
			}
		}
		catch
		{
		}
	}
}
