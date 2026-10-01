using System;
using System.Windows;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>歌词窗口的全局持有者。
///
/// 为什么要单独一个类：歌词窗要在三个地方被碰到——底部开关按钮、
/// 主窗��关掉时、Esc 收起时。零散地 new 一个 Window 会开出好几个，
/// 而且每个都自己订阅一遍事件，Dispose 时漏掉就泄漏。
/// 这里保证全局只有一个窗口实例，并把开关状态同步给主窗的按钮。</summary>
public static class LyricRuntime
{
	private static LyricFollowService? _follow;
	private static Views.LyricWindow? _window;

	/// <summary>跟随服务。切歌时它去拉歌词，窗口只管显示。</summary>
	public static LyricFollowService Follow => _follow ??= new LyricFollowService();

	/// <summary>开关状态变了。用于通知主窗按钮换图标/高亮。</summary>
	public static event Action? VisibilityChanged;

	/// <summary>歌词窗口当前是否开着（不是「功能是否开启」）。</summary>
	public static bool IsWindowOpen => _window != null && _window.IsVisible;

	/// <summary>总开关：设置里记的 LyricWindowEnabled。</summary>
	public static bool Enabled
	{
		get => SettingsManager.Instance.LyricWindowEnabled;
		set
		{
			AppSettings s = SettingsManager.Instance;
			if (s.LyricWindowEnabled == value)
			{
				return;
			}
			s.LyricWindowEnabled = value;
			SettingsManager.Save(s);
			if (value)
			{
				Show();
			}
			else
			{
				Close();
			}
			VisibilityChanged?.Invoke();
		}
	}

	/// <summary>点底部按钮时用：开着就收起来，收起来就打开。</summary>
	public static void Toggle() => Enabled = !Enabled;

	/// <summary>打开歌词窗。</summary>
	public static void Show()
	{
		AppSettings s = SettingsManager.Instance;
		if (!s.LyricWindowEnabled)
		{
			return;
		}
		// 故意不设 Owner。Owner 有一堆附带效果：主窗最小化/收进托盘时
		// 拥有者窗口会跟着隐藏——而桌面歌词恰恰要能在最小化时继续显示，
		// 那是它存在的意义之一。Alt+Tab 的隐藏由 ShowInTaskbar=False 已经解决了。
		// 不用 Owner 换来的是「最小化也能看歌词」，代价是主窗关掉时要自己收尾，
		// 那条路在 MainWindow.Window_Closing 里已经接了 Shutdown。
		if (_window == null)
		{
			_window = new Views.LyricWindow();
		}
		_window.Topmost = s.LyricWindowTopmost;
		if (!_window.IsVisible)
		{
			_window.Show();
		}
		// 第一次打开时没有焦点，窗口是灰的，要再点一下标题栏才有反应。
		// 但如果用户正在主窗打字就别抢焦点了，只在刚显示的那一下激活。
		if (s.LyricWindowTopmost)
		{
			_window.Activate();
		}
		// 总开关开着但窗口从没建过时，构造里没拉到当前歌的歌词，补一次。
		_window.ApplyCurrentTrack();
	}

	/// <summary>真正关掉歌词窗。功能开关不动，窗口下次开还是原样。</summary>
	public static void Close()
	{
		if (_window == null)
		{
			return;
		}
		// 只 Hide 不 Close：Close 会走 Closed 事件、把订阅退掉，
		// 下次开就得到一个失联的空壳。这里只藏起来。
		_window.Hide();
		VisibilityChanged?.Invoke();
	}

	/// <summary>窗口自己关/收起时回调，让主窗按钮取消高亮。</summary>
	internal static void OnWindowClosed()
	{
		VisibilityChanged?.Invoke();
	}

	/// <summary>切「背景鼠标穿透」。
	///
	/// 单独抽出来是因为这条路径和「点按钮」是对称的：开了穿透之后
	/// 鼠标点不中歌词窗，只能从托盘或全局快捷键把它关回来，
	/// 两条路最后都汇到这个方法，所以状态和外观只有一处实现。</summary>
	public static void ToggleClickThrough()
	{
		bool next = !SettingsManager.Instance.LyricWindowClickThrough;
		if (_window == null)
		{
			// 窗口还没建出来，就只把开关记下，等下次打开时按新状态呈现
			AppSettings s = SettingsManager.Instance;
			s.LyricWindowClickThrough = next;
			SettingsManager.Save(s);
			return;
		}
		_window.SetPassthrough(next);
	}

	/// <summary>设置页改了字族之后推给已经开着的歌词窗。
	/// 歌词窗是 Hide() 不是 Close()，单例会长期活着，所以不这么做的话
	/// 用户得手动关一次歌词窗再开才能看到新字体。</summary>
	public static void RefreshAppearance()
	{
		_window?.ApplyFontFamily(SettingsManager.Instance.LyricFontFamily);
		// 自定义歌词色也要跟着推。ApplyFontColor 内部已经会在
		// 「没设自定义色」时把画刷还原成主题引用，所以清空颜色也走这一条。
		_window?.ApplyFontColor();
	}

	/// <summary>换动画模板后推一下歌词窗。
	///
	/// 模板是每切一句才读的，所以选完新模板、当前这句还没换时，
	/// 窗上还留着旧模板留下的缩放/位移/摆动。这里让窗子把当前这句
	/// 用新模板重新落位一次，不必等下一句、也不必重开窗。</summary>
	public static void RefreshAnimation()
	{
		_window?.ApplyAnimTemplate();
	}



	/// <summary>主窗关闭时调：窗口和跟随服务一起收掉，别留后台任务。</summary>
	public static void Shutdown()
	{
		if (_window != null)
		{
			_window.Hide();
			_window.Close();
			_window = null;
		}
		_follow?.Dispose();
		_follow = null;
	}
}

