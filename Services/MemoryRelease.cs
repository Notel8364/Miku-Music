using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicBox.Services;

/// <summary>
/// 一键内存释放：封面内存缓存 + GC 全代压缩 + 工作集换出，三个入口共用这一份：
/// - 托盘驻留（MainWindow.ReleaseMemoryForTrayResidency）：窗口没人看，释放后
///   回到前台时视口会自动重载封面；
/// - 设置页调试面板「立即释放」（DbgMemReleaseButton）：排查内存问题时手动触发；
/// - 底栏「内存释放」键（MiniMemButton）：用户随手清一把，Toast 反馈。
/// 以前只有托盘化一处，这次抽成公共方法——三处各自维护一份迟早走样。
/// </summary>
public static class MemoryRelease
{
	[DllImport("psapi.dll")]
	private static extern bool EmptyWorkingSet(IntPtr hProcess);

	/// <summary>最小化路径用：只把页面换出工作集，不清缓存不清堆
	/// （窗口随时会被拖回来，清了封面缓存重建会闪）。</summary>
	public static void TrimWorkingSet()
	{
		try
		{
			using var p = Process.GetCurrentProcess();
			EmptyWorkingSet(p.Handle);
		}
		catch
		{
			// Psapi 不可用（理论上只存在于 XP 以下）就当没有这个功能。
		}
	}

	/// <summary>
	/// 执行一轮完整释放。返回本次释放的封面缓存规模（解码项/字节项），
	/// 给调试面板与底栏 Toast 拼提示文案用。
	/// 步骤顺序有讲究：先掐掉封面缓存的大块强引用，GC 时位图/字节已经
	/// 没有托管引用才能真正回收；最后 EmptyWorkingSet 把剩余页换出工作集。
	/// </summary>
	public static (int DecodedItems, int ByteItems) ReleaseNow()
	{
		try
		{
			// 释放前取一份统计：缓存在下一步就被清空了，数字要从这里拿。
			var before = CoverLoader.GetStats();
			// 1) 掐掉封面缓存的大块强引用（解码位图 + 压缩字节），只留磁盘缓存。
			CoverLoader.ReleaseDecodedCache();
			// 2) 全代压缩回收：compacting 把 LOH 碎片压实，托管堆才能真正还页给系统。
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			// 3) 把剩余页面换出工作集，任务管理器里的占用立即落底。
			TrimWorkingSet();
			LogHelper.LogDebug($"内存释放完成：封面解码 {before.DecodedCount} 项 / 字节 {before.BytesCount} 项已清（磁盘缓存保留）");
			return (before.DecodedCount, before.BytesCount);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "内存释放失败");
			return (0, 0);
		}
	}
}
