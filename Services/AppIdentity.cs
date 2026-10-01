using System;
using System.Runtime.InteropServices;


namespace MusicBox.Services;

/// <summary>
/// 显式声明 AppUserModelID（AUMID）。
///
/// 为什么需要：Windows 的 SMTC 媒体浮层（从右下角媒体图标滑出来那块）左下角显示的
/// 「应用名 + 图标」，是从进程的 AUMID 反查系统里登记的那个应用身份得到的。
/// 非打包（ unpackaged ）的普通 exe 不会自动有 AUMID，浮层就退化成
/// 「未知应用 / Unknown application」。
///
/// 这里不用 MSIX、不用注册表登记快捷方式，只调
/// SetCurrentProcessExplicitAppUserModelID 把 AUMID 挂到当前进程上；
/// 同时用 SHAppBarMessage 之类的花招对浮层没用（那是任务栏用的）。
/// 关键点是必须在创建任何窗口/SMTC 之前调用，所以由 App.OnStartup 最前面调。
/// </summary>
public static class AppIdentity
{
	/// <summary>进程级 AUMID。跟 MhyunBox 用同一套命名习惯，便于在系统里区分两个播放器。</summary>
	public const string AppUserModelId = "MikuMusic.Desktop.NAudioPlayer";

	/// <summary>托盘气泡、系统浮层、任务栏里显示的程序名。</summary>
	public const string DisplayName = "Miku Music";

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

	/// <summary>已设置成功就不再重复设置。重复调用其实也无害，但保持幂等便于排查。</summary>
	public static bool IsApplied { get; private set; }

	/// <summary>
	/// 必须在建窗口之前调用。返回 true 表示 AUMID 已挂上。
	/// 失败不致命：只是浮层退回「未知应用」，播放等功能不受影响，所以只记日志不抛。
	/// </summary>
	public static bool Apply()
	{
		if (IsApplied)
		{
			return true;
		}
		try
		{
			int hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
			IsApplied = hr == 0;
			LogHelper.LogDebug($"[AUMID] SetCurrentProcessExplicitAppUserModelID({AppUserModelId}) hr=0x{hr:X8} applied={IsApplied}");
			return IsApplied;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "设置 AppUserModelID 失败（媒体浮层会显示「未知应用」）");
			return false;
		}
	}
}
