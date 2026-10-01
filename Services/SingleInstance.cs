using System;
using System.Threading;

namespace MusicBox.Services;

/// <summary>
/// 单实例守卫。必须在 <c>Application.OnStartup</c> 里、MainWindow 被构造之前完成抢占。
/// 靠的是 App.xaml 不写 <c>StartupUri</c>：StartupUri 会在 OnStartup 之前就把主窗口建出来，
/// 那样第二个实例会完整建好窗口、初始化播放、接上 SMTC、建托盘图标并注册全局热键，
/// 然后才发现自己不是主实例——表现就是「互顶打开」。
/// 后开的实例不再弹框退出，而是唤醒并置顶先开的那个。
/// </summary>
public static class SingleInstance
{
	private const string MutexName = @"Local\MikuMusic_SingleInstance";

	private const string ActivateEventName = @"Local\MikuMusic_SingleInstance_Activate";

	private static Mutex? _mutex;

	private static EventWaitHandle? _activateEvent;

	private static Thread? _listener;

	/// <summary>后开的实例请求把先开的实例唤醒置顶。在后台线程上触发，实现方需自己切回 UI 线程。</summary>
	public static event Action? ActivateRequested;

	/// <summary>抢锁结果。<paramref name="isPrimary"/> 为 false 时应立即退出进程，且不要再弹任何框。</summary>
	public static bool TryClaim(out bool isPrimary)
	{
		isPrimary = false;
		try
		{
			_mutex = new Mutex(true, MutexName, out bool createdNew);
			if (!createdNew)
			{
				// 已有人持有：通知对方置顶，然后安静退出。
				SignalExistingInstance();
				return false;
			}

			_activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
			_listener = new Thread(Listen)
			{
				IsBackground = true,
				Name = "MikuMusicSingleInstance"
			};
			_listener.Start();
			isPrimary = true;
			return true;
		}
		catch
		{
			// 抢锁本身失败（权限、命名空间异常等）时按唯一实例继续启动，
			// 否则程序会因为一个辅助机制彻底打不开。
			Release();
			isPrimary = true;
			return true;
		}
	}

	/// <summary>进程退出时释放句柄。幂等，可重复调用。</summary>
	public static void Release()
	{
		try
		{
			_listener = null;
			EventWaitHandle? waitHandle = _activateEvent;
			_activateEvent = null;
			waitHandle?.Dispose();
			Mutex? mutex = _mutex;
			_mutex = null;
			if (mutex != null)
			{
				try
				{
					mutex.ReleaseMutex();
				}
				catch (ApplicationException)
				{
					// 本线程并非互斥量拥有者（正常流程下不会发生），忽略。
				}
				mutex.Dispose();
			}
		}
		catch
		{
		}
	}

	private static void SignalExistingInstance()
	{
		try
		{
			// 主实例可能刚拿到互斥量、还没建好事件句柄，这里重试一小段时间。
			for (int i = 0; i < 40; i++)
			{
				try
				{
					using EventWaitHandle existing = EventWaitHandle.OpenExisting(ActivateEventName);
					existing.Set();
					return;
				}
				catch (WaitHandleCannotBeOpenedException)
				{
					Thread.Sleep(50);
				}
			}
		}
		catch
		{
		}
	}

	private static void Listen()
	{
		try
		{
			EventWaitHandle? waitHandle = _activateEvent;
			while (waitHandle != null && waitHandle.WaitOne())
			{
				ActivateRequested?.Invoke();
			}
		}
		catch
		{
		}
	}
}
