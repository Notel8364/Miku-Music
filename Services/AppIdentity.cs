using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MusicBox.Services;

/// <summary>
/// 显式声明 AppUserModelID（AUMID），并把这个身份登记成系统能反查出名字/图标的形态。
///
/// 解析链（为什么改了注册表媒体卡还是「未知应用」）：
/// - 进程身份：非打包 exe 默认没有 AUMID，系统给的是「exe 路径形态」的隐式 AUMID，
///   谁都反查不出来。必须用 SetCurrentProcessExplicitAppUserModelID 在最早期挂上
///   显式 AUMID；SMTC 媒体会话建起来时（SmtcService.Attach）拍下的是**当时的进程
///   AUMID**，所以这一步必须发生在任何窗口/SMTC/托盘创建之前——现在由
///   AppIdentityBootstrap 的 ModuleInitializer 在 Main 之前执行，比 App.OnStartup
///   还早（OnStartup 里那次调用保留作幂等兜底）。
/// - 通知（Toast）显示名：按 HKCU\Software\Classes\AppUserModelId\{AUMID} 里的
///   DisplayName/IconUri 反查，查不到就把 AUMID 字符串原样顶上去（旧版通知里那串
///   「MikuMusic.Desktop.NAudioPlayer」就是这么来的）。这条链只要写注册表就够。
/// - 媒体浮层（音量图标滑出来的媒体卡）显示名：**不走上面的注册表键**，它按
///   「开始菜单里带 System.AppUserModel.ID 属性、且与进程 AUMID 匹配的快捷方式」
///   解析应用名/图标（Electron 应用的同款问题见 Obsidian 论坛 #119966：快捷方式
///   的 AUMID 对不上，媒体卡一律「Unknown app」）。所以免安装程序还需要往
///   开始菜单写一个带该属性的 .lnk——这就是 EnsureStartMenuShortcut 干的事。
///   ⚠ 实机排查结论：当前这台机器的 Windows 属性系统整体损坏（连 notepad.exe
///   都打不开属性库，一律 E_NOINTERFACE），.lnk 的 AUMID 属性在本机写不进去，
///   快捷方式只能落成基础形态，媒体卡应用名在这台机器上退回注册表链解析；
///   正常机器上属性写入会成功、走完整的快捷方式解析链。
/// </summary>
public static class AppIdentity
{
	/// <summary>短 AUMID。健康机器上（.lnk 的 System.AppUserModel.ID 属性能写进去）
	/// 用它当进程 AUMID，和快捷方式属性一一对应。</summary>
	public const string ShortAumid = "MikuMusic";

	/// <summary>进程实际用的 AUMID。不再固定短名：
	/// 本机属性系统损坏，.lnk 的 AUMID 属性写不进去，开始菜单条目的 AppID
	/// 因此是 **exe 全路径**（Get-StartApps 实测），短名在 shell 的应用表里
	/// 查不到任何条目 → 媒体卡「未知应用」。所以属性写不进去时改用 exe 路径
	/// 当 AUMID，正好和开始菜单条目对上，应用名/图标都能解析出来。
	/// Apply() 在 ModuleInitializer 里赋值，之后只读。</summary>
	public static string AppUserModelId { get; private set; } = ShortAumid;

	/// <summary>托盘气泡、系统浮层、任务栏里显示的程序名。</summary>
	public const string DisplayName = "Miku Music";

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

	/// <summary>让 shell 丢掉关联/图标缓存重读。SHCNE_ASSOCCHANGED = 0x08000000，
	/// SHCNF_FLUSH = 0x1000（同步刷完再返回）。</summary>
	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

	private const int ShcneAssocChanged = 0x08000000;
	private const uint ShcnfFlush = 0x1000;

	/// <summary>已设置成功就不再重复设置。重复调用其实也无害，但保持幂等便于排查。</summary>
	public static bool IsApplied { get; private set; }

	/// <summary>
	/// 必须在建窗口/SMTC 之前调用。返回 true 表示 AUMID 已挂上。
	/// 失败不致命：只是浮层退回「未知应用」，播放等功能不受影响，所以只记日志不抛。
	/// </summary>
	public static bool Apply()
	{
		if (IsApplied)
		{
			return true;
		}
		// 防御：外部工具（探针、测试宿主）引用 MikuMusic.dll 时，某些静态初始化
		// 路径也会走到这里。若放任登记，它会把 AUMID 注册表键、开始菜单 .lnk
		// 的目标和图标全改成「那个工具自己的 exe」，媒体卡立刻显示错的应用身份。
		// 只认真正的主程序宿主；其它进程一律跳过 shell 登记。
		string? host = Environment.ProcessPath;
		string hostName = host == null ? "" : Path.GetFileNameWithoutExtension(host);
		if (!string.Equals(hostName, "MikuMusic", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(hostName, "MusicBox", StringComparison.OrdinalIgnoreCase))
		{
			LogHelper.LogDebug($"[AUMID] 非主程序宿主，跳过 shell 身份登记：{host}");
			return false;
		}
		try
		{
			// 先把开始菜单快捷方式这条链走完，才知道本进程该用哪个 AUMID：
			// .lnk 带上了 System.AppUserModel.ID 属性 → 用短名，和属性对应；
			// 写不进去（本机属性系统损坏）→ 用 exe 路径，和开始菜单条目的
			// AppID 对上。SMTC 会话还没建，现在决定正合适。
			bool lnkHasAumid = EnsureStartMenuShortcut();
			string? exe = ExePath();
			AppUserModelId = lnkHasAumid || exe == null ? ShortAumid : exe;
			int hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
			IsApplied = hr == 0;
			LogHelper.Log(
				$"[AUMID] SetCurrentProcessExplicitAppUserModelID({AppUserModelId}) hr=0x{hr:X8} applied={IsApplied} " +
				$"lnkAumid={lnkHasAumid}");
			if (IsApplied)
			{
				// AUMID 挂上后马上登记显示身份。必须在任何通知/托盘化之前完成：
				// 托盘气球转成 Toast 时系统就按这个键反查应用名和图标。
				RegisterToastIdentity(AppUserModelId);
			}
			return IsApplied;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "设置 AppUserModelID 失败（媒体浮层会显示「未知应用」）");
			return false;
		}
	}

	/// <summary>当前进程 exe 的真实路径（apphost）。拿不到退回托管 DLL 路径，
	/// 再拿不到返回 null。为什么优先 exe：程序图标 (<ApplicationIcon>) 编译时
	/// 内嵌在 apphost exe 里，托管 DLL（Assembly.Location）不带 Win32 图标资源，
	/// 系统从 DLL 里抽图标会失败——旧版 IconUri 登记的就是 DLL，浮层图标因此
	/// 挂过空。单文件发布时 ProcessPath 同样有效。</summary>
	private static string? ExePath()
	{
		string? exe = Environment.ProcessPath;
		if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
		{
			exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
		}
		return string.IsNullOrEmpty(exe) || !File.Exists(exe) ? null : exe;
	}

	/// <summary>
	/// 往 HKCU\Software\Classes\AppUserModelId\{AUMID} 写 DisplayName / IconUri。
	/// 这是免安装（非 MSIX）程序让**通知**显示正确应用名/图标的标准做法
	/// （媒体卡不认这个键，见类型注释的解析链）。
	/// 先查再写：现有值和目标完全一致就不动注册表，避免每次启动都白写一遍。
	/// 失败不致命：只是通知应用名退回 AUMID 字符串，功能不受影响。
	/// </summary>
	private static void RegisterToastIdentity(string aumid)
	{
		try
		{
			string? exe = ExePath();
			using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
				@"Software\Classes\AppUserModelId\" + aumid, writable: true);
			if (key == null)
			{
				return;
			}
			bool changed = false;
			if (!string.Equals(key.GetValue("DisplayName") as string, DisplayName, StringComparison.Ordinal))
			{
				key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
				changed = true;
			}
			if (exe != null && !string.Equals(key.GetValue("IconUri") as string, exe, StringComparison.Ordinal))
			{
				key.SetValue("IconUri", exe, RegistryValueKind.String);
				changed = true;
			}
			LogHelper.LogDebug($"[AUMID] 注册表身份登记完成 changed={changed} icon={exe ?? "(未登记)"}");
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "登记 AUMID 显示身份失败（通知应用名会显示 AUMID 字符串）");
		}
	}

	// ────── 开始菜单快捷方式（媒体卡应用名的解析入口） ──────

	/// <summary>建/维护开始菜单快捷方式，并尽量把 System.AppUserModel.ID 属性写进去。
	/// 返回 true 表示 .lnk 现在确实带着正确的 AUMID 属性（调用方据此决定进程
	/// 用短 AUMID 还是 exe 路径）。</summary>
	private static bool EnsureStartMenuShortcut()
	{
		try
		{
			string? exe = ExePath();
			if (exe == null)
			{
				LogHelper.LogDebug("[AUMID] 取不到 exe 路径，跳过开始菜单快捷方式登记");
				return false;
			}
			string dir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
			string lnk = Path.Combine(dir, DisplayName + ".lnk");

			// 幂等：文件在且目标指向当前 exe 就不重建（免得每次启动都重写、刷开始菜单索引）。
			// 注意幂等条件不要求 AUMID 属性齐备——属性缺失只走下面一次廉价的补写尝试，
			// 不触发整个 .lnk 重建。本机属性系统损坏时补写永远失败，若把它算进「需要重建」
			// 就会每次启动都重写快捷方式、反复刷开始菜单索引。
			// exe 换了发布目录（target 对不上）才在这里重建，保证快捷方式永远指向当前 exe。
			bool targetOk = File.Exists(lnk)
				&& GetShortcutTarget(lnk) is string target
				&& string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
			if (!targetOk)
			{
				// ① 建基础 .lnk（路径/图标/描述）并落盘。
				var link = (IShellLinkW)(object)new ShellLinkClass();
				try
				{
					link.SetPath(exe);
					link.SetIconLocation(exe, 0);
					link.SetDescription(DisplayName);
					((IPersistFile)link).Save(lnk, true);
				}
				finally
				{
					Marshal.FinalReleaseComObject(link);
				}
				LogHelper.Log($"[AUMID] 开始菜单快捷方式已登记（媒体浮层应用名解析入口）：{lnk}");
				// 通知 shell 刷新关联缓存。媒体浮层的应用名是 shell 缓存的，
				// 快捷方式刚换成新目标（发新版换了目录）时缓存里还是旧路径，
				// 那段时间浮层就会把我们的会话显示成「未知应用」。
				// 只在真的重建了快捷方式时发一次，别每次启动都刷一遍图标缓存。
				try
				{
					SHChangeNotify(ShcneAssocChanged, ShcnfFlush, IntPtr.Zero, IntPtr.Zero);
				}
				catch
				{
				}
			}

			// ② 关键一步：把 System.AppUserModel.ID 属性写进 lnk（best-effort）。
			// 媒体浮层解析 Win32 程序的应用名走「开始菜单里 AUMID 匹配的快捷方式」，
			// 只写注册表 DisplayName 不一定够（上一轮的教训，见类型注释）。
			// 为什么不直接 (IPropertyStore)shellLink 对象：ShellLink 类对象对
			// IID_IPropertyStore 的 QueryInterface 返回 E_NOINTERFACE——实机日志
			// 每次启动都炸 InvalidCastException，基础 .lnk 因此从未落盘，这就是
			// 媒体卡一直「未知应用」的根因之一。实测 Load 之后 QI 依旧失败，
			// 说明该对象根本不暴露属性库接口，必须改走属性系统：
			// SHGetPropertyStoreFromParsingName 按文件路径打开读写属性库
			// （资源管理器自己写 lnk 属性的同一条路）。
			// 实机进一步排查的结论：这台 Windows 的属性系统整体损坏——notepad.exe、
			// hosts 等所有文件类型打开属性库一律 E_NOINTERFACE（正常系统 .exe 必可读），
			// 所以在这台机器上属性写不进去是环境问题、属预期：快捷方式降级为基础形态
			// （依然有效，目标始终指向当前 exe，exe 原地替换后 .lnk 依旧可用），
			// 媒体卡应用名退回注册表登记链（RegisterToastIdentity）。
			if (string.Equals(GetShortcutAumid(lnk), ShortAumid, StringComparison.Ordinal))
			{
				return true; // 属性已带且正确，什么都不用做
			}
			try
			{
				WriteShortcutAumid(lnk, ShortAumid);
				// 写完立刻读回来验证：属性库对 .lnk 的写入有时会在 Commit 之后
				// 才落盘，读回成功才算真的生效（这条日志就是排查「未知应用」的
				// 唯一凭据，别只信 SetValue 的 HRESULT）。
				string? back = GetShortcutAumid(lnk);
				bool ok = string.Equals(back, ShortAumid, StringComparison.Ordinal);
				LogHelper.Log(ok
					? $"[AUMID] 快捷方式 AUMID 属性写入并读回成功：{lnk}"
					: $"[AUMID] 快捷方式 AUMID 属性写入后读回不一致（back={back ?? "null"}）：{lnk}");
				if (ok)
				{
					// 快捷方式的身份变了，通知 shell 丢掉旧的解析缓存，
					// 否则媒体浮层还会拿缓存里的「未知应用」顶一阵。
					try
					{
						SHChangeNotify(ShcneAssocChanged, ShcnfFlush, IntPtr.Zero, IntPtr.Zero);
					}
					catch
					{
					}
				}
				return ok;
			}
			catch (Exception ex)
			{
				// 写不进去只影响媒体卡应用名，用 Debug 级别避免每次启动刷一条 Error。
				LogHelper.LogDebug($"[AUMID] 快捷方式 AUMID 属性未写入：{ex.Message}");
				return false;
			}
		}
		catch (Exception ex)
		{
			// 写不进去（企业策略锁开始菜单等）只影响媒体卡应用名，日志留痕不致命。
			LogHelper.LogError(ex, "登记开始菜单快捷方式失败（媒体卡可能仍显示未知应用）");
			return false;
		}
	}

	/// <summary>按 .lnk 文件路径打开它的属性库并写入 System.AppUserModel.ID。
	/// 用 SHGetPropertyStoreFromParsingName（GPS_READWRITE）而不是 ShellLink
	/// 对象本身：后者 QI IPropertyStore 会 E_NOINTERFACE（见 EnsureStartMenuShortcut）。
	///
	/// ⚠ 这里原来声明成 <c>PreserveSig=false</c> + <c>out IPropertyStore</c>，
	/// 每次启动都炸 InvalidCastException「Specified cast is not valid」，
	/// 于是 .lnk 永远拿不到 AUMID 属性，媒体卡也就永远解析不出应用名
	/// （浮层退回「未知应用」）。改成拿原始 IntPtr 再自己 QI：
	/// 走的是同一条 shell 接口，但不依赖封送器对「out 接口指针」的推断，
	/// 失败时还能拿到真实 HRESULT 写进日志。返回 true 表示确实写进去了。</summary>
	private static bool WriteShortcutAumid(string lnkPath, string aumid)
	{
		Guid iid = IidPropertyStore;
		int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, GpsReadWrite, ref iid, out IntPtr pv);
		if (hr != 0 || pv == IntPtr.Zero)
		{
			throw new COMException($"SHGetPropertyStoreFromParsingName(READWRITE) hr=0x{hr:X8}", hr);
		}
		try
		{
			var store = (IPropertyStore)Marshal.GetObjectForIUnknown(pv);
			try
			{
				PROPERTYKEY key = PkeyAppUserModelId;
				var pvValue = new PropVariant
				{
					Vt = 31, // VT_LPWSTR
					PointerValue = Marshal.StringToCoTaskMemUni(aumid)
				};
				try
				{
					hr = store.SetValue(ref key, ref pvValue);
					if (hr != 0)
					{
						throw new COMException($"IPropertyStore.SetValue hr=0x{hr:X8}", hr);
					}
					hr = store.Commit();
					if (hr != 0)
					{
						throw new COMException($"IPropertyStore.Commit hr=0x{hr:X8}", hr);
					}
				}
				finally
				{
					_ = PropVariantClear(ref pvValue);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(store);
			}
		}
		finally
		{
			// 不管成功失败都要放掉这一份引用（GetObjectForIUnknown 各自 +1）。
			Marshal.Release(pv);
		}
		return true;
	}

	/// <summary>读回 .lnk 里的 System.AppUserModel.ID 属性；没有/读不到返回 null。
	/// 幂等检查用它判断「现有快捷方式是否已带正确 AUMID」，缺了就重写。</summary>
	private static string? GetShortcutAumid(string lnkPath)
	{
		try
		{
			Guid iid = IidPropertyStore;
			int hr = SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, GpsDefault, ref iid, out IntPtr pv);
			if (hr != 0 || pv == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				var store = (IPropertyStore)Marshal.GetObjectForIUnknown(pv);
				try
				{
					PROPERTYKEY key = PkeyAppUserModelId;
					if (store.GetValue(ref key, out PropVariant value) != 0)
					{
						return null;
					}
					try
					{
						return value.Vt == 31 && value.PointerValue != IntPtr.Zero
							? Marshal.PtrToStringUni(value.PointerValue)
							: null;
					}
					finally
					{
						_ = PropVariantClear(ref value);
					}
				}
				finally
				{
					Marshal.ReleaseComObject(store);
				}
			}
			finally
			{
				Marshal.Release(pv);
			}
		}
		catch
		{
			return null;
		}
	}

	/// <summary>读一个 .lnk 的目标路径；读不出来（损坏/异构）返回 null。</summary>
	private static string? GetShortcutTarget(string lnkPath)
	{
		try
		{
			var link = (IShellLinkW)(object)new ShellLinkClass();
			try
			{
				((IPersistFile)link).Load(lnkPath, 0);
				const int maxPath = 520; // 双倍 MAX_PATH，容忍长路径
				IntPtr buf = Marshal.AllocCoTaskMem(maxPath * 2);
				try
				{
					link.GetPath(buf, maxPath, IntPtr.Zero, 0);
					return Marshal.PtrToStringUni(buf);
				}
				finally
				{
					Marshal.FreeCoTaskMem(buf);
				}
			}
			finally
			{
				Marshal.FinalReleaseComObject(link);
			}
		}
		catch
		{
			return null;
		}
	}

	// 以下是一小段创建 .lnk 的 COM interop 样板（CLSID_ShellLink / IShellLinkW /
	// IPersistFile / IPropertyStore）。不引第三方包就为了这一个快捷方式，
	// 槽位顺序必须与 SDK 头文件完全一致，别动。

	[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
	private sealed class ShellLinkClass
	{
	}

	[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellLinkW
	{
		void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
		void GetIDList(out IntPtr ppidl);
		void SetIDList(IntPtr pidl);
		void GetDescription(IntPtr pszName, int cch);
		void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
		void GetWorkingDirectory(IntPtr pszDir, int cch);
		void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
		void GetArguments(IntPtr pszArgs, int cch);
		void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
		void GetHotkey(out ushort wHotkey);
		void SetHotkey(ushort wHotkey);
		void GetShowCmd(out int iShowCmd);
		void SetShowCmd(int iShowCmd);
		void GetIconLocation(IntPtr pszIconPath, int cch, out int iIcon);
		void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
		void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
		void Resolve(IntPtr hwnd, uint fFlags);
		void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
	}

	[ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPersistFile
	{
		void GetClassID(out Guid classId);
		[PreserveSig]
		int IsDirty();
		void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
		void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
		void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
		void GetCurFile(out string fileName);
	}

	/// <summary>IPropertyStore 的 IID。SHGetPropertyStoreFromParsingName 的 riid
	/// 和接口声明的 Guid 必须是同一个值，否则拿回来的 RCW 转不过去。</summary>
	private static readonly Guid IidPropertyStore = new("886D8EEB-8CF2-444E-8DDE-CDE6D0F7C56E");

	[ComImport, Guid("886D8EEB-8CF2-444E-8DDE-CDE6D0F7C56E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore
	{
		// 全部显式 PreserveSig 并把 HRESULT 当返回值：这样失败时能拿到真实错误码
		// 写日志，而不是被封送器转成一个看不出所以然的 COMException。
		[PreserveSig] int GetCount(out uint propertyCount);
		[PreserveSig] int GetAt(uint propertyIndex, out PROPERTYKEY key);
		[PreserveSig] int GetValue(ref PROPERTYKEY key, out PropVariant value);
		[PreserveSig] int SetValue(ref PROPERTYKEY key, ref PropVariant value);
		[PreserveSig] int Commit();
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROPERTYKEY
	{
		public Guid FmtId;
		public uint Pid;
	}

	/// <summary>System.AppUserModel.ID 属性键（PKEY_AppUserModel_ID）。
	///
	/// GUID 必须是 9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3，pid 5。
	/// 这里原来写的是 9F4C2855-9F98-11D0-A54F-0014A34736F9 —— 一个根本不存在的
	/// 属性键，SetValue 自然什么都写不进去，媒体卡也就一直「未知应用」。</summary>
	private static readonly PROPERTYKEY PkeyAppUserModelId = new()
	{
		FmtId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
		Pid = 5
	};

	// GETPROPERTYSTOREFLAGS：
	//   GPS_DEFAULT   = 0x00000000（只读）
	//   GPS_READWRITE = 0x00000002（要写属性必须给这个）
	//   GPS_FASTPROPERTIESONLY = 0x00000008（**只读**快速属性）
	// 原来把 READWRITE 写成 0x8，实际给的是 FASTPROPERTIESONLY，
	// 于是 SHGetPropertyStoreFromParsingName 直接回 E_NOINTERFACE（0x80004002），
	// 写快捷方式属性这步从第一天起就没成功过。
	private const uint GpsDefault = 0;
	private const uint GpsReadWrite = 0x2;

	/// <summary>按解析路径打开 shell 项的属性库（资源管理器写 .lnk 属性同款 API）。
	/// 最后一个参数拿原始接口指针自己 QI（见 WriteShortcutAumid 里的说明），
	/// 所以用 out IntPtr + PreserveSig，失败时 hr 可读、可记日志。</summary>
	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
	private static extern int SHGetPropertyStoreFromParsingName(
		[MarshalAs(UnmanagedType.LPWStr)] string pszPath,
		IntPtr pbc,
		uint grfFlags,
		ref Guid riid,
		out IntPtr ppv);

	/// <summary>最小 PROPVARIANT：只支持本文件用到的 VT_LPWSTR（字符串）形态。
	///
	/// 布局必须和 native PROPVARIANT 完全一致：2 字节 vt + 3 个保留字（共 8 字节）
	/// + union。x64 上 union 占 16 字节（里面有 DECIMAL），x86 上是 8 字节。
	/// 用两个 IntPtr 表示 union 刚好两边都对：x86 = 8+8 = 16，x64 = 8+16 = 24。
	/// 原来只写了一个 IntPtr，x64 上结构短 8 字节，native 写越界 —— 这类错误
	/// 不会立刻炸，而是后面某次封送才莫名其妙地失败。
	/// 用完必须 PropVariantClear 释放里面的 CoTaskMem 指针。</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct PropVariant
	{
		public ushort Vt;
		public ushort Reserved1;
		public ushort Reserved2;
		public ushort Reserved3;
		public IntPtr PointerValue;
		public IntPtr PointerValue2;
	}

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PropVariant pv);
}

/// <summary>
/// 进程入口最早期挂 AUMID：ModuleInitializer 在 Main 执行之前、App 静态构造
/// 之前运行。SMTC 媒体会话创建时拍下的是当时的进程 AUMID，而 WPF 的
/// App.OnStartup 前面还有 InitializeComponent/资源初始化等一串跑在它前面的
/// 代码（还有第三方静态初始化链），统一挪到模块初始化器里挂，保证「先有
/// 身份、后有会话」。App.OnStartup 里的调用保留，幂等。
/// </summary>
internal static class AppIdentityBootstrap
{
	// 注意这里写全限定名：项目里有个同名类型把 RuntimeHelpers 短名遮住了。
	[System.Runtime.CompilerServices.ModuleInitializer]
	internal static void Run()
	{
		AppIdentity.Apply();
	}
}
