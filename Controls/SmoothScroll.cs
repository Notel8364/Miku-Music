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

		// 定时补间（翻页回顶部用）。滚轮那种「越远跑越快」的指数逼近
		// 适合连续滚动：一段 900px 的回顶只要一两帧就到了，看着像瞬移。
		// 翻页要的是「看得出滚了一段路」，所以另给一条固定时长的路���。
		public bool Timed;
		public double From;
		public double To;
		public double DurationMs;
		public double StartedMs;
	}

	private const double ReachRate = 10.0;
	private const double DistanceGain = 0.35;
	private const double SnapThreshold = 0.5;

	private static readonly Dictionary<ScrollViewer, Smoother> Active = new Dictionary<ScrollViewer, Smoother>();
	private static System.Diagnostics.Stopwatch? _watch;
	private static long _lastTick;

	/// <summary>在固定时长内把偏移补间到 <paramref name="targetOffset"/>。
	///
	/// 和 <see cref="Animate"/> 的区别是时间可预期：这里走 smoothstep，
	/// 起止慢、中间快，整段正好走完 <paramref name="durationMs"/>。
	/// 翻页回顶部用它，列表的滑入和回顶同时开始、同时结束。
	public static void AnimateTo(ScrollViewer sv, double targetOffset, double durationMs)
	{
		if (sv == null)
		{
			return;
		}
		double max = Math.Max(0.0, sv.ScrollableHeight);
		double to = double.IsNaN(targetOffset) ? 0.0 : Math.Clamp(targetOffset, 0.0, max);
		if (durationMs <= 0 || sv.ScrollableHeight <= 0.0 || Math.Abs(to - sv.VerticalOffset) < SnapThreshold)
		{
			sv.ScrollToVerticalOffset(to);
			return;
		}

		if (!Active.TryGetValue(sv, out Smoother? sm))
		{
			sm = new Smoother { Scroller = sv, Offset = sv.VerticalOffset, Target = sv.VerticalOffset };
			Active[sv] = sm;
		}
		EnsureRenderHook();

		sm.Timed = true;
		sm.From = sm.Offset;
		sm.To = to;
		sm.Target = to;
		sm.DurationMs = durationMs;
		sm.StartedMs = _watch!.ElapsedMilliseconds;
	}

	/// <summary>立即停掉某个 ScrollViewer 上还没跑完的补间。
	///
	/// 为什么需要它：翻页重绘（RenderEngine）和「定位到正在播放的行」
	/// 这类代码会**直接**写 VerticalOffset，而此刻可能还有一条上一页
	/// 留下的补间在按旧目标逐帧写偏移——两条路同时写同一个属性，
	/// 表现就是列表抖一下、然后被那条旧补间硬拽回顶部/原位。
	/// 所以凡是绕过 SmoothScroll 直接赋偏移的地方，赋值前先 Cancel。</summary>
	public static void Cancel(ScrollViewer sv)
	{
		if (sv == null)
		{
			return;
		}
		Active.Remove(sv);
	}

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
			// 滚轮接管：正在跑的定时补间让位给滚轮的指数逼近，
			// 不然两条路会同时写 VerticalOffset，互相拽。
			sm.Timed = false;
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
			// 从 0 起算会让第一帧的 dt 等于表启动至今的时长，
			// 而表是在 EnsureRenderHook 里刚建的，这个值本来也接近 0，
			// 只是第二次 AnimateTo 时表已经跑了一阵，dt 会突然很大。
			_lastTick = _watch.ElapsedMilliseconds;
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
			if (sm.Timed)
			{
				// 定时补间：按真实经过的时间算比例，不看还差多少像素。
				// smoothstep（t²(3-2t)）两端慢中间快，起步和收尾都不生硬。
				double t = sm.DurationMs <= 0 ? 1.0
					: Math.Clamp((now - sm.StartedMs) / sm.DurationMs, 0.0, 1.0);
				double eased = t * t * (3.0 - 2.0 * t);
				double value = Math.Clamp(sm.From + (sm.To - sm.From) * eased,
					0.0, sm.Scroller.ScrollableHeight);
				sm.Offset = value;
				sm.Scroller.ScrollToVerticalOffset(value);
				if (t >= 1.0)
				{
					(done ??= new List<ScrollViewer>()).Add(kv.Key);
				}
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