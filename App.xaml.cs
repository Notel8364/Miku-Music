using System.Windows;
using MusicBox.Services;

namespace MusicBox;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		// 顺序很重要：必须先抢单实例锁，再建主窗口。
		// App.xaml 已经去掉了 StartupUri，就是为了保证这里能拦在窗口创建之前；
		// 否则第二个实例会先把 SMTC 会话、播放、托盘、热键全建一遍再退出。
		if (!SingleInstance.TryClaim(out _))
		{
			// 已有实例在跑：已通知对方置顶，这里安静退出。
			Shutdown();
			return;
		}

		// 必须在建任何窗口之前挂 AUMID：SMTC 媒体浮层左下角的应用名和图标靠它反查，
		// 晚了浮层就固化成「未知应用」，之后再设也补不回来。
		AppIdentity.Apply();

		base.OnStartup(e);

		Loc.Default.SetCurrent(Settings.SettingsManager.Instance.Language);
		// 必须在建 MainWindow 之前落主题：窗口一创建，各处 DynamicResource 就开始解析了。
		ThemeManager.ApplyFromSettings();
		SingleInstance.ActivateRequested += OnActivateRequested;
		DispatcherUnhandledException += (_, args) =>
		{
			Services.LogHelper.LogError(args.Exception, "未处理异常");
			Services.LogHelper.Flush();
			Services.MusicService.Stop();
		};

		// 抢到锁之后才建主窗口。必须赋给 Application.MainWindow：
		// 各个页面是通过 Application.Current.MainWindow 拿主窗口的。
		MainWindow main = new MainWindow();
		Application.Current.MainWindow = main;
		main.Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		SingleInstance.ActivateRequested -= OnActivateRequested;
		SingleInstance.Release();
		Services.MusicService.OnAppExit();
		base.OnExit(e);
	}

	private void OnActivateRequested()
	{
		// 由后台线程触发，切回 UI 线程再唤醒窗口。
		Dispatcher.BeginInvoke(new System.Action(() => MusicBox.MainWindow.Instance?.WakeToFront()));
	}
}
