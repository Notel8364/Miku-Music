using System;
using System.Diagnostics;
using System.Threading;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>
/// 内存守护：工作集连续两次超过设置里的阈值（默认 150MB）时，
/// 自动走一次 <see cref="MemoryRelease.ReleaseNow"/>，把占用压回基线。
///
/// 为什么是「连续两次」而不是一超就放：
/// 切歌、列表重建、视频背景起播那一瞬间工作集会冒一个尖峰，
/// 一超就清缓存等于把刚解码好的封面又扔掉，界面反而闪。
/// 每 5 秒采样一次，连超两次（约 10 秒）才算真的涨上去了。
///
/// 两次自动释放之间至少隔 90 秒：清封面缓存之后视口会重新按需解码，
/// 太频繁会变成「一直释放、一直重解码」的空转。
/// 阈值、开关都现读 SettingsManager，用户在设置页改完立即生效，不用重启。
/// </summary>
public static class MemoryGuard
{
	/// <summary>采样间隔（秒）。和设置页调试面板的 1s 采样分开：
	/// 这里只做决策，不需要那么密。</summary>
	private const int SampleSeconds = 5;

	/// <summary>两次自动释放的最小间隔（秒）。</summary>
	private const int CooldownSeconds = 90;

	/// <summary>连续超阈值多少次才动手（5s × 2 ≈ 10s）。</summary>
	private const int TriggerSamples = 2;

	private static Timer? _timer;

	/// <summary>0 = 空闲，1 = 正在检查/释放。防止定时器重入。</summary>
	private static int _busy;

	private static int _overCount;

	private static DateTime _lastReleaseUtc = DateTime.MinValue;

	/// <summary>上一次自动释放前/后的工作集（MB），调试面板可读。</summary>
	public static long LastBeforeMb { get; private set; }

	public static long LastAfterMb { get; private set; }

	public static DateTime LastReleaseUtc => _lastReleaseUtc;

	public static void Start()
	{
		if (_timer != null)
		{
			return;
		}
		// 首次采样延迟 30 秒：避开启动时的大量建页/解码，那时候的峰值没有代表性。
		// 连续两次采样 + 这个延迟，意味着最早也要 35 秒后才会自动释放，
		// 不会在用户刚打开程序、还在等第一屏渲染时把页面清出去。
		_timer = new Timer(_ => Tick(), null,
			TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(SampleSeconds));
		LogHelper.LogDebug($"[内存守护] 已启动：每 {SampleSeconds}s 采样，连续 {TriggerSamples} 次超阈值自动释放，冷却 {CooldownSeconds}s");
	}

	public static void Stop()
	{
		try
		{
			_timer?.Dispose();
		}
		catch
		{
		}
		_timer = null;
	}

	private static void Tick()
	{
		if (Interlocked.Exchange(ref _busy, 1) == 1)
		{
			return;
		}
		try
		{
			AppSettings s = SettingsManager.Instance;
			if (!s.AutoMemRelease)
			{
				_overCount = 0;
				return;
			}
			int threshold = Math.Clamp(s.AutoMemReleaseMb, 150, 4096);
			long nowMb = ReadWorkingSetMb();
			if (nowMb < 0)
			{
				return;
			}
			if (nowMb < threshold)
			{
				_overCount = 0;
				return;
			}
			_overCount++;
			if (_overCount < TriggerSamples)
			{
				return;
			}
			if ((DateTime.UtcNow - _lastReleaseUtc).TotalSeconds < CooldownSeconds)
			{
				return;
			}

			_lastReleaseUtc = DateTime.UtcNow;
			LastBeforeMb = nowMb;
			MemoryRelease.ReleaseNow();
			LastAfterMb = ReadWorkingSetMb();
			LogHelper.Log($"[内存守护] 工作集 {LastBeforeMb}MB ≥ 阈值 {threshold}MB，已自动释放 → {LastAfterMb}MB");
			// 释放后重新计数：下一次还得再连超两拍才会动手。
			_overCount = 0;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "内存守护自动释放失败");
		}
		finally
		{
			Interlocked.Exchange(ref _busy, 0);
		}
	}

	private static long ReadWorkingSetMb()
	{
		try
		{
			using Process p = Process.GetCurrentProcess();
			return p.WorkingSet64 / (1024L * 1024L);
		}
		catch
		{
			return -1;
		}
	}
}
