using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MusicBox.Controls;

/// <summary>持续平滑滚动：滚轮只累加目标偏移，由渲染帧驱动当前偏移指数式追向目标，
/// 配合距离越远跑得越快的系数，保证连续快速滚动时也能"跟得上"，停下后惯性缓落到目标。</summary>
public static class SmoothScroll
{
	private sealed class Smoother
	{
		public ScrollViewer Scroller = null!;
		public double Offset;
		public double Target;
	}

	private const double ReachRate = 10.0;
	private const double DistanceGain = 0.35;
	private const double SnapThreshold = 0.5;

	private static readonly Dictionary<ScrollViewer, Smoother> Active = new Dictionary<ScrollViewer, Smoother>();
	private static System.Diagnostics.Stopwatch? _watch;
	private static long _lastTick;

	public static void Animate(ScrollViewer sv, double deltaPixels)
	{
		if (sv == null || double.IsNaN(deltaPixels)) return;
		if (sv.ScrollableHeight <= 0.0) return;

		if (!Active.TryGetValue(sv, out Smoother? sm))
		{
			sm = new Smoother { Scroller = sv };
			Active[sv] = sm;
			sm.Offset = sv.VerticalOffset;
			sm.Target = sv.VerticalOffset;
		}
		else
		{
			// 动画仍在进行；若外部（拖滚动条/切换内容）改变了实际偏移，整体平移目标与当前，保留剩余行程
			double ext = sv.VerticalOffset - sm.Offset;
			if (Math.Abs(ext) > 1.0)
			{
				sm.Offset += ext;
				sm.Target += ext;
			}
		}

		sm.Target = Math.Clamp(sm.Target + deltaPixels, 0.0, sv.ScrollableHeight);
		double distance = Math.Abs(sm.Target - sv.VerticalOffset);
		if (distance < SnapThreshold)
		{
			Current(Active, sv, sm);
			return;
		}

		EnsureRenderHook();
	}

	private static void Current(Dictionary<ScrollViewer, Smoother> map, ScrollViewer sv, Smoother sm)
	{
		sv.ScrollToVerticalOffset(sm.Target);
		sm.Offset = sm.Target;
		map.Remove(sv);
	}

	private static void EnsureRenderHook()
	{
		if (_watch == null)
		{
			_watch = System.Diagnostics.Stopwatch.StartNew();
			_lastTick = 0;
			CompositionTarget.Rendering += OnRendering;
		}
	}

	private static void OnRendering(object? sender, EventArgs e)
	{
		long now = _watch!.ElapsedMilliseconds;
		double dt = (now - _lastTick) / 1000.0;
		_lastTick = now;
		if (dt <= 0.0) return;

		if (Active.Count == 0)
		{
			CompositionTarget.Rendering -= OnRendering;
			_watch.Stop();
			_watch = null;
			return;
		}

		List<ScrollViewer>? done = null;
		foreach (var kv in Active)
		{
			Smoother sm = kv.Value;
			if (sm.Scroller.ScrollableHeight <= 0.0)
			{
				(done ??= new List<ScrollViewer>()).Add(kv.Key);
				continue;
			}
			sm.Target = Math.Clamp(sm.Target, 0.0, sm.Scroller.ScrollableHeight);
			double distance = sm.Target - sm.Offset;
			if (Math.Abs(distance) < SnapThreshold)
			{
				sm.Scroller.ScrollToVerticalOffset(sm.Target);
				sm.Offset = sm.Target;
				(done ??= new List<ScrollViewer>()).Add(kv.Key);
				continue;
			}
			double rate = ReachRate + DistanceGain * Math.Abs(distance);
			double k = 1.0 - Math.Exp(-dt * rate);
			sm.Offset += distance * k;
			sm.Scroller.ScrollToVerticalOffset(sm.Offset);
		}

		if (done != null)
		{
			foreach (ScrollViewer key in done)
			{
				Active.Remove(key);
			}
		}
	}
}