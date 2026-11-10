using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MusicBox.Services;

namespace MusicBox.Controls;

/// <summary>杀掉 WPF 弹层（ComboBox 下拉等）打开瞬间在屏幕左上角闪一帧的老毛病。
///
/// 现象：点开下拉的瞬间，列表先在桌面左上角闪一下，然后才落到正确位置；
/// 更糟的一种是首开直接「飞到左上角并停留」——恢复显形得太早，锚点还没算完。
/// 原因：Popup 的 HWND 是第一次打开时才创建，创建后先摆到 (0,0)、之后才
/// 移到锚点位置——中间露出的一两帧就是那一闪。
///
/// 做法：弹出那一拍先把 popup 内容改成透明（画不出任何像素），然后**逐拍
/// 校验实际落位**：拿锚点（PlacementTarget/宿主控件）换算出的期望屏幕位置，
/// 和 popup.Child.PointToScreen(0,0) 的实际位置比对，偏差超过 4px 就保持
/// 透明、nudge 逼 WPF 重算锚点、下一拍再验；对齐了才恢复不透明。
///
/// 为什么不能像旧版那样「盲等一拍就显形」：页面刚切、ScrollViewer 布局收尾、
/// 设置节首次展开时，锚点/布局常常还没算完，Background 那一拍远远不够，
/// 显形时弹层还停在 (0,0)，就成了肉眼可见的「停在左上角」。
/// 校验循环挂 Background 优先级，上限 1.5 秒兜底（宁可晚现身也不永久隐形）。
///
/// 修复尝试一（透明 HWND 路径）：非透明弹层走普通 HWND 定位路径，首开更容易
/// 先落 (0,0)；透明分层窗口（AllowsTransparency=True）的创建/定位路径不同，
/// 已知能避开这种落位。App.xaml 的 ComboBox 模板已显式打开，这里对其它挂了
/// Silence 的弹层在接管时兜底补上。
///
/// 修复尝试二（贴原点关开重试）：若校验超时时位置仍贴屏幕原点——说明 nudge
/// 救不回来（WPF 每次重算都把弹层摆回 (0,0)）——趁内容还全透明（用户全程
/// 看不见），先关掉弹层、延一帧重开。重开走全新的 HWND 创建 + 锚点定位流程，
/// 大概率落回正确位置；重开后照常校验一轮，不行最多再试一次。重试轮的校验
/// 时限缩短到 400ms，避免最坏情况下拉迟到好几秒。
///
/// 临时诊断：每个弹层的首个打开周期会逐拍把「期望位置/实际位置/每拍差值/
/// 最终结果」写进诊断日志（LogHelper.LogDiag，[Diag][弹层] 前缀），之后的
/// 开关只记一行最终结果，供复现「首开飞左上角」后对照排查；问题定位后可
/// 整体摘除这批日志。
///
/// DPI 说明：两端 PointToScreen 返回的都是物理像素，坐标系天然一致，
/// SystemParameters 的缩放系数不需要再参与换算。
///
/// 用法：<c>ctl:PopupFlashGuard.Silence="True"</c>，挂在任何一个带 Popup 的
/// 控件上（挂在 ComboBox 上管它模板里那个内部 Popup）。App.xaml 的隐式
/// ComboBox 样式里已经全局打开，其它下拉想用再单独挂。</summary>
public static class PopupFlashGuard
{
	/// <summary>按需开启。True 时找到宿主模板里的 Popup，接管开合信号。</summary>
	public static readonly DependencyProperty SilenceProperty =
		DependencyProperty.RegisterAttached(
			"Silence", typeof(bool), typeof(PopupFlashGuard),
			new PropertyMetadata(BooleanBoxes.False, OnSilenceChanged));

	public static bool GetSilence(DependencyObject d)
	{
		return d != null && (bool)d.GetValue(SilenceProperty);
	}

	public static void SetSilence(DependencyObject d, bool value)
	{
		if (d != null)
		{
			d.SetValue(SilenceProperty, value ? BooleanBoxes.True : BooleanBoxes.False);
		}
	}

	// 落位判定容差（物理像素）：PointToScreen 有取整/亚像素抖动，4px 内都算到位。
	private const double LandingTolerancePx = 4.0;

	// 相邻两拍实际位置几乎不动才算「稳定」。
	private const double StableTolerancePx = 0.5;

	// 「贴着屏幕原点」判定线：卡死 bug 的弹层就停在 (0,0)（内容自带 3px margin）。
	// 只用它给「位置稳定」兜底把关——锚点比对是相对位置，不受它影响。
	private const double OriginGuardPx = 8.0;

	// 校验循环上限：超时强制显形，防「永远透明」死等。
	private const double LandingTimeoutMs = 1500.0;

	// 贴原点重试轮的校验上限：重开后的 HWND 全新创建，正常一两拍就落位；
	// 给 400ms 是留足余量又不至于让用户久等。
	private const double LandingRetryTimeoutMs = 400.0;

	// 贴原点重试的最大次数：两次重开都救不回来就只能强制显形（并已记进日志）。
	private const int MaxLandingRetries = 2;

	// 诊断日志每个弹层首开周期最多记多少拍，防布局异常时刷爆日志。
	private const int MaxDiagTicks = 12;

	// 找到的 popup 记在宿主的附加属性里：Unload 卸载要还回去，
	// 重挂（Loaded 再来一次）也不至于重复挂两遍信号。
	private static readonly DependencyPropertyKey HookedPopupPropertyKey =
		DependencyProperty.RegisterAttachedReadOnly(
			"HookedPopup", typeof(Popup), typeof(PopupFlashGuard), new PropertyMetadata(null));

	// popup → 宿主反查表。OnPopupIsOpenChanged 是挂在 popup 上的静态回调，
	// 自己拿不到宿主；靠这张表反查，同时用来判断「这个 popup 是否已被接管」，
	// 接管关系丢了就当场补挂（见 OnPopupIsOpenChanged 的自愈入口）。
	private static readonly ConditionalWeakTable<Popup, FrameworkElement> HookedHosts = new();

	// 每个弹层一轮「落位校验」的代次：新一轮开合自增，旧循环看到代次对不上
	// 就退场，快速开合时两轮循环不会互相踩。同一份 state 还带着重试计数与
	// 诊断标记（跟着弹层走，跨开合轮次保留）。
	private sealed class WatchState
	{
		public int Generation;

		// 贴原点重试的「关闭→重开」进行中：关闭那一拍别把透明恢复成不透明
		//（弹层此刻还贴在 (0,0)，一显形用户就看见了）。
		public bool RetryingClose;

		// 本轮「用户打开」已做的重试次数：正常关闭时清零，重开时保留。
		public int Retries;

		// 逐拍诊断日志只记每个弹层的首个打开周期，之后只记最终结果一行。
		public bool DiagDone;

		// —— 首开内容预热 ——
		// 悬空一帧的「首点闪」不是位置问题（逐拍校验已把位置捂准了），而是
		// 首开时才第一次跑的**内容**开销：ItemsPresenter 首次 Measure 时的
		// 选项容器生成 + 弹层子树首轮布局。首开那几帧弹层已经显形、内容还是
		// 空壳，等容器建好才补上 —— 用户看到的就是闪一下。预热在用户点开
		// **之前**（页面 Loaded 后空闲拍）对弹层内容干跑一次 Measure，
		// 把容器生成和布局提前吃掉；首开时弹出的就是排好版的完整内容。
		// 注意绝不真的打开弹层（见 WarmupPopup），Warmed 保证只预热一次。
		public bool Warmed;
	}

	private static readonly ConditionalWeakTable<Popup, WatchState> WatchStates = new();

	private static readonly DependencyPropertyDescriptor PopupIsOpenDescriptor =
		DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup));

	private static void OnSilenceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not FrameworkElement host)
		{
			return;
		}
		bool on = (bool)e.NewValue;
		if (on)
		{
			host.Loaded += Host_Loaded;
			host.Unloaded += Host_Unloaded;
			// 折叠面板里首次实例化的 ComboBox 要等面板展开才走 Loaded；
			// IsVisibleChanged 更早触发，多给一次补挂机会。
			host.IsVisibleChanged += Host_IsVisibleChanged;
			// 模板可能已经展开（页面装好之后才挂到这个属性上），先试挂一次。
			TryAttach(host);
		}
		else
		{
			host.Loaded -= Host_Loaded;
			host.Unloaded -= Host_Unloaded;
			host.IsVisibleChanged -= Host_IsVisibleChanged;
			Detach(host);
		}
	}

	private static void Host_Loaded(object sender, RoutedEventArgs e)
	{
		TryAttach((FrameworkElement)sender);
	}

	private static void Host_Unloaded(object sender, RoutedEventArgs e)
	{
		Detach((FrameworkElement)sender);
	}

	private static void Host_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		// 只有「变可见」这一下需要补挂；变隐藏由 Unloaded/Detach 收尾。
		if ((bool)e.NewValue)
		{
			TryAttach((FrameworkElement)sender);
		}
	}

	private static void TryAttach(FrameworkElement host)
	{
		if (GetHookedPopup(host) != null)
		{
			// 已经接管：宿主可见性每次翻转都会进来，顺带补排一次预热
			//（首次接管时宿主可能还不可见，预热当时被跳过，这里就是补跑的入口）。
			ScheduleWarmup(host);
			return; // 已经接管
		}
		if (!host.IsLoaded)
		{
			return; // Loaded / IsVisibleChanged 会再来
		}
		host.ApplyTemplate();
		Popup? popup = FindTemplatePopup(host);
		if (popup == null)
		{
			// 模板还挂在懒加载状态：给一拍布局机会，仍找不到就等下回 Loaded。
			host.Dispatcher.BeginInvoke(
				new Action(() =>
				{
					if (GetHookedPopup(host) == null)
					{
						Popup? late = FindTemplatePopup(host);
						if (late != null)
						{
							DoAttach(host, late);
						}
					}
				}),
				DispatcherPriority.Loaded);
			return;
		}
		DoAttach(host, popup);
	}

	private static void DoAttach(FrameworkElement host, Popup popup)
	{
		// 修复尝试一：接管时兜底把弹层切到透明分层窗口路径（见类注释）。
		// 已开过 HWND 的弹层改这个属性会抛异常——那种本来就管不到首开，吞掉。
		if (!popup.AllowsTransparency && !popup.IsOpen)
		{
			try
			{
				popup.AllowsTransparency = true;
			}
			catch
			{
				// 个别宿主/环境不支持分层窗口：维持原样，靠逐拍校验兜底。
			}
		}
		if (HookedHosts.TryGetValue(popup, out FrameworkElement? existing))
		{
			if (ReferenceEquals(existing, host))
			{
				return; // 已是这层绑定，别把信号挂成两份
			}
			// 换宿主换绑：先拆旧监听再重挂，模板 popup 实际不会发生，防一手。
			PopupIsOpenDescriptor.RemoveValueChanged(popup, OnPopupIsOpenChanged);
			HookedHosts.Remove(popup);
		}
		HookedHosts.Add(popup, host);
		PopupIsOpenDescriptor.AddValueChanged(popup, OnPopupIsOpenChanged);
		host.SetValue(HookedPopupPropertyKey, popup);
		// 接管完成即排首开预热（见 WatchState.Warmed 注释）：等布局收尾后的
		// 空闲拍执行，不跟页面首帧抢时间。
		ScheduleWarmup(host);
	}

	private static void Detach(FrameworkElement host)
	{
		Popup? popup = GetHookedPopup(host);
		if (popup != null)
		{
			PopupIsOpenDescriptor.RemoveValueChanged(popup, OnPopupIsOpenChanged);
			HookedHosts.Remove(popup);
			CancelLandingWatch(popup);
			if (popup.Child is UIElement child)
			{
				child.Opacity = 1;
			}
		}
		host.SetValue(HookedPopupPropertyKey, null);
	}

	private static Popup? GetHookedPopup(DependencyObject host)
	{
		return host.GetValue(HookedPopupPropertyKey.DependencyProperty) as Popup;
	}

	// 防止同一个宿主重复排预热（Loaded/IsVisibleChanged/DoAttach 三处入口）。
	private static readonly ConditionalWeakTable<FrameworkElement, object> WarmupScheduled =
		new();

	/// <summary>排一次首开预热：Idle 拍把弹层悄悄开一拍再关掉。
	/// 只在宿主可见时跑（折叠面板收起时预热看不见也没意义，展开时的
	/// IsVisibleChanged→TryAttach 会再排一次）；预热过/正在开就跳过。</summary>
	private static void ScheduleWarmup(FrameworkElement host)
	{
		Popup? popup = GetHookedPopup(host);
		if (popup == null)
		{
			return;
		}
		if (WatchStates.TryGetValue(popup, out WatchState? st) && st.Warmed)
		{
			return; // 预热过，一个弹层一次就够
		}
		if (WarmupScheduled.TryGetValue(host, out _))
		{
			return; // 已排过，别叠
		}
		WarmupScheduled.Add(host, true);
		host.Dispatcher.BeginInvoke(new Action(() =>
		{
			WarmupScheduled.Remove(host);
			WarmupPopup(host, popup);
		}), DispatcherPriority.ApplicationIdle);
	}

	/// <summary>执行内容预热：对弹层内容干跑一次 Measure（不打开弹层）。
	/// 为什么不真的开一下再关：ComboBox 模板里 Popup.IsOpen 是
	/// TemplateBinding，直接拨 popup.IsOpen 会用本地值顶掉绑定，
	/// 之后用户真点开时弹层失灵；改拨 IsDropDownOpen 又会让输入框描边
	/// 亮一拍（成新的闪烁源）。首点闪的主因也不是 HWND 创建（那个便宜），
	/// 而是首开才做的容器生成+首轮布局 —— Measure 就能把这两样提前吃掉。</summary>
	private static void WarmupPopup(FrameworkElement host, Popup popup)
	{
		try
		{
			if (GetHookedPopup(host) != popup || popup.IsOpen || !host.IsVisible)
			{
				return; // 接管关系没了 / 用户已经先点开了 / 宿主还看不见：放弃这次
			}
			WatchState state = WatchStates.GetOrCreateValue(popup);
			if (state.Warmed)
			{
				return;
			}
			if (popup.Child is not UIElement child)
			{
				return;
			}
			state.Warmed = true;
			// 无限尺寸 Measure：ItemsPresenter 首测会生成全部选项容器，
			// ScrollViewer 模板也一并套上 —— 正是首开那几帧在做的事。
			// 结果只写进 DesiredSize，不影响宿主布局；弹层没开，屏幕上看不到任何变化。
			child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		}
		catch
		{
			// 预热失败就当没做过，逐拍校验仍兜底。
		}
	}

	private static void OnPopupIsOpenChanged(object? sender, EventArgs e)
	{
		if (sender is not Popup popup)
		{
			return;
		}
		try
		{
			// 自愈接管入口：回调还在响、反查表里却没有这个 popup，说明接管关系
			// 丢了（Unloaded 卸过一次、或换绑残留的监听）。就地补挂再处理，
			// 覆盖「折叠面板里首次实例化模板、guard 还没挂上 popup 就开了」的漏网场景。
			if (!HookedHosts.TryGetValue(popup, out FrameworkElement? hookedHost))
			{
				hookedHost = ResolveHost(popup);
				if (hookedHost != null)
				{
					if (!GetSilence(hookedHost))
					{
						return; // 宿主明确关掉了 Silence：这个弹层不该被接管
					}
					DoAttach(hookedHost, popup);
				}
				// hookedHost == null：找不到宿主也照常捂这一拍，锚点还有 PlacementTarget 兜底
			}

			if (popup.Child is not UIElement child)
			{
				return;
			}
			if (popup.IsOpen)
			{
				// 刚开：这一拍交给 WPF 建 HWND、定位。内容全透明，
				// 哪怕 HWND 还停在 (0,0) 也画不出东西。
				child.Opacity = 0;
				StartLandingWatch(popup, child, hookedHost);
			}
			else
			{
				bool retrying = WatchStates.TryGetValue(popup, out WatchState? st) && st.RetryingClose;
				CancelLandingWatch(popup);
				if (retrying)
				{
					// 贴原点重试的关闭拍：保持透明等重开（重试计数留在 state 里，
					// 只有用户正常关闭才清零，这样重开后的校验轮还知道自己第几试）。
				}
				else
				{
					if (WatchStates.TryGetValue(popup, out WatchState? st2))
					{
						st2.Retries = 0;
					}
					child.Opacity = 1;
				}
			}
		}
		catch
		{
		}
	}

	/// <summary>模板里的 Popup，PlacementTarget/TemplatedParent 都指向套模板的那个控件；
	/// 两个都拿不到就真没有宿主了。</summary>
	private static FrameworkElement? ResolveHost(Popup popup)
	{
		if (popup.PlacementTarget is FrameworkElement target)
		{
			return target;
		}
		return popup.TemplatedParent as FrameworkElement;
	}

	/// <summary>落位校验循环：Background 优先级逐拍比对「期望锚点位置 vs 实际位置」，
	/// 对齐才显形；没对齐就保持透明、nudge 逼 WPF 重算锚点、下一拍再验。
	/// 上限 1.5 秒（重试轮 400ms），超时走「贴原点重开」或强制显形兜底。</summary>
	private static void StartLandingWatch(Popup popup, UIElement child, FrameworkElement? host)
	{
		WatchState state = WatchStates.GetOrCreateValue(popup);
		state.Generation++;
		int gen = state.Generation;
		double timeoutMs = state.Retries > 0 ? LandingRetryTimeoutMs : LandingTimeoutMs;
		long deadline = Environment.TickCount64 + (long)timeoutMs;
		long startTick = Environment.TickCount64;

		// 锚点优先用 PlacementTarget（模板 popup 自动指向 ComboBox），兜底宿主/模板父。
		Visual? anchor = (popup.PlacementTarget as Visual) ?? host ?? (popup.TemplatedParent as Visual);
		Point? lastActual = null;
		int diagTicks = 0;
		bool diagFull = !state.DiagDone;

		if (diagFull)
		{
			LogHelper.LogDiag(
				$"[Diag][弹层] 开启 {PopupTag(popup)} placement={popup.Placement} 透明HWND={popup.AllowsTransparency} " +
				$"锚点={(anchor is FrameworkElement fe ? fe.GetType().Name : "无")} 时限={timeoutMs:F0}ms 重试={state.Retries}");
		}

		void Step()
		{
			// 新一轮开合已接管、或弹层已卸载：这轮循环退场。
			if (!WatchStates.TryGetValue(popup, out WatchState? st) || st.Generation != gen)
			{
				return;
			}
			if (!popup.IsOpen)
			{
				// 重试的关闭拍不该显形（弹层还贴在原点，显形就穿帮）；普通关闭恢复正常。
				if (!st.RetryingClose)
				{
					child.Opacity = 1;
				}
				return;
			}
			if (child.Opacity != 0)
			{
				return; // 已被别的路径显形，不再插手
			}

			Point? actual = GetActualScreenOrigin(child);
			if (actual == null)
			{
				// popup 的 HWND 还没建好 / 内容还没进展示源：此刻显形必闪，继续捂着。
				if (Environment.TickCount64 < deadline)
				{
					child.Dispatcher.BeginInvoke(Step, DispatcherPriority.Background);
					return;
				}
				child.Opacity = 1;
				LogWatchResult(popup, st, "超时(HWND未就绪)", null, startTick);
				return;
			}

			bool anchorOk = MatchesAnchor(popup, anchor, actual.Value);
			bool stableOk = !anchorOk && IsStableAndOffOrigin(actual.Value, lastActual);
			bool landed = anchorOk || stableOk;
			Point? prevActual = lastActual;
			lastActual = actual;

			if (diagFull && diagTicks < MaxDiagTicks)
			{
				diagTicks++;
				(Point? below, Point? _) = ComputeAnchorCandidates(popup, anchor as FrameworkElement);
				string delta = prevActual.HasValue
					? $"Δ=({actual.Value.X - prevActual.Value.X:F1},{actual.Value.Y - prevActual.Value.Y:F1})"
					: "Δ=首拍";
				LogHelper.LogDiag(
					$"[Diag][弹层] 拍{diagTicks} {PopupTag(popup)} 实际={Fmt(actual)} 期望下={Fmt(below)} {delta}");
			}

			if (landed || Environment.TickCount64 >= deadline)
			{
				// 修复尝试二：校验超时、实际位置一直贴屏幕原点——nudge 对它无效
				//（WPF 每次重算都摆回 (0,0)）。趁内容还全透明，关闭→延一帧重开，
				// 走全新的 HWND 创建 + 锚点定位流程；重开后照常校验。
				if (!landed && actual.HasValue && IsNearOrigin(actual.Value)
					&& st.Retries < MaxLandingRetries
					&& TryReopenStuck(popup, st, child))
				{
					LogHelper.LogDiag(
						$"[Diag][弹层] 贴原点重试 {PopupTag(popup)} 实际={Fmt(actual)} 第{st.Retries}/{MaxLandingRetries}次 关闭→重开");
					return;
				}
				// 显形前顺手再 nudge 重算一次锚点：落位路径扫掉残余半像素错位，
				// 超时路径做最后一次挽救。
				SilenceNudge(popup);
				child.Opacity = 1;
				LogWatchResult(popup, st, anchorOk ? "锚点对齐" : stableOk ? "位置稳定" : "超时强制显形", actual, startTick);
				return;
			}

			// 还没落位：保持透明，nudge 逼 WPF 重算锚点，下一拍再验。
			SilenceNudge(popup);
			child.Dispatcher.BeginInvoke(Step, DispatcherPriority.Background);
		}

		child.Dispatcher.BeginInvoke(Step, DispatcherPriority.Background);
	}

	/// <summary>让还在排队的校验拍退场：代次 +1，旧循环一看代次对不上就自己结束。</summary>
	private static void CancelLandingWatch(Popup popup)
	{
		WatchStates.GetOrCreateValue(popup).Generation++;
	}

	/// <summary>贴原点卡死时的重开：经宿主 ComboBox 关掉再延一帧打开（保证 ComboBox
	/// 内部状态同步——直接拨 popup.IsOpen 会让模板绑定失步）；非模板弹层才直接拨。
	/// 返回 false 表示关闭动作本身失败，调用方走强制显形。</summary>
	private static bool TryReopenStuck(Popup popup, WatchState st, UIElement child)
	{
		try
		{
			ComboBox? combo = popup.TemplatedParent as ComboBox;
			st.Retries++;
			st.RetryingClose = true;
			child.Dispatcher.BeginInvoke(new Action(() =>
			{
				st.RetryingClose = false;
				try
				{
					if (combo != null)
					{
						combo.IsDropDownOpen = true;
					}
					else
					{
						popup.IsOpen = true;
					}
				}
				catch
				{
					// 宿主已经不在（页面切走等）：放弃重开，就地显形。
					if (popup.Child is UIElement c)
					{
						c.Opacity = 1;
					}
				}
			}), DispatcherPriority.Background);
			if (combo != null)
			{
				combo.IsDropDownOpen = false;
			}
			else
			{
				popup.IsOpen = false;
			}
			return true;
		}
		catch
		{
			st.RetryingClose = false;
			return false;
		}
	}

	/// <summary>锚点比对：把锚点控件换算成 popup 内容左上角**应该**在的屏幕位置，
	/// 和实际位置比对。Placement=Bottom：目标底左 + 水平/垂直偏移，再缩进内容自身
	/// Margin；下方放不下时 WPF 会翻到上方，按「popup 底边贴目标顶边」再给一个候选。
	/// 两端都是 PointToScreen 的物理像素，坐标系一致，DPI 缩放不用换算。</summary>
	private static bool MatchesAnchor(Popup popup, Visual? anchor, Point actual)
	{
		if (anchor is not FrameworkElement target)
		{
			return false;
		}
		(Point? below, Point? above) = ComputeAnchorCandidates(popup, target);
		if (below.HasValue && CloseEnough(below.Value, actual))
		{
			return true;
		}
		if (above.HasValue && CloseEnough(above.Value, actual))
		{
			return true;
		}
		return false;
	}

	/// <summary>算锚点的两个候选期望位置（正常摆法 / 翻到上方）。宿主还没进展示源
	/// 或尺寸没量出来时返回空——锚点不可信。</summary>
	private static (Point? Below, Point? Above) ComputeAnchorCandidates(Popup popup, FrameworkElement? target)
	{
		if (target == null)
		{
			return (null, null);
		}
		try
		{
			if (PresentationSource.FromVisual(target) == null)
			{
				return (null, null);
			}
			Size size = target.RenderSize;
			if (size.Width <= 0 || size.Height <= 0)
			{
				return (null, null); // 还没量出尺寸，锚点不可信
			}

			Point topLeft = target.PointToScreen(new Point(0, 0));
			double hOff = popup.HorizontalOffset;
			double vOff = popup.VerticalOffset;
			FrameworkElement? child = popup.Child as FrameworkElement;
			Thickness m = child?.Margin ?? default;

			// 期望位置（正常摆法）：目标底左 + 偏移 + 内容自身 Margin。
			Point below = new Point(
				topLeft.X + hOff + m.Left,
				topLeft.Y + size.Height + vOff + m.Top);

			// 翻转到上方的候选：popup 底边贴目标顶边。WPF 翻转时偏移方向、工作区
			// 钳制的细节猜不准，算式是近似的；对不上没关系，「位置稳定」那条兜底
			// 会放行，1.5 秒上限兜住最坏情况。
			Point? above = null;
			double childHeight = child?.RenderSize.Height ?? 0;
			if (childHeight > 0)
			{
				above = new Point(
					topLeft.X + hOff + m.Left,
					topLeft.Y - (m.Top + childHeight + m.Bottom) + vOff + m.Top);
			}
			return (below, above);
		}
		catch
		{
			return (null, null);
		}
	}

	/// <summary>兜底放行：位置连续两拍不动（中间还 nudge 过，说明 WPF 重算已收敛）
	/// 且不贴屏幕原点。覆盖「翻到上方、贴边挪移」等锚点换算猜不准的摆法——
	/// 再捂着只会让下拉迟到。贴原点的「卡死在 (0,0)」被 OriginGuardPx 拦在外面。</summary>
	private static bool IsStableAndOffOrigin(Point actual, Point? lastActual)
	{
		if (lastActual == null)
		{
			return false;
		}
		return NearlySame(actual, lastActual.Value)
			&& !IsNearOrigin(actual);
	}

	/// <summary>贴屏幕原点判定：XY 都落在原点保护半径内才算（合法锚在屏幕左上角的
	/// 弹层 X 或 Y 总有一个超出，不会误伤）。</summary>
	private static bool IsNearOrigin(Point p)
	{
		return p.X <= OriginGuardPx && p.Y <= OriginGuardPx;
	}

	/// <summary>popup 内容左上角的实际屏幕位置（物理像素）。HWND 还没建好 / 内容
	/// 还没进展示源时返回 null。</summary>
	private static Point? GetActualScreenOrigin(UIElement child)
	{
		try
		{
			if (PresentationSource.FromVisual(child) == null)
			{
				return null;
			}
			return child.PointToScreen(new Point(0, 0));
		}
		catch
		{
			return null;
		}
	}

	private static bool CloseEnough(Point expected, Point actual)
	{
		return Math.Abs(expected.X - actual.X) <= LandingTolerancePx
			&& Math.Abs(expected.Y - actual.Y) <= LandingTolerancePx;
	}

	private static bool NearlySame(Point a, Point b)
	{
		return Math.Abs(a.X - b.X) <= StableTolerancePx
			&& Math.Abs(a.Y - b.Y) <= StableTolerancePx;
	}

	private static void SilenceNudge(Popup popup)
	{
		try
		{
			popup.HorizontalOffset += 0.001;
			popup.HorizontalOffset -= 0.001;
		}
		catch
		{
		}
	}

	// —— 临时诊断日志（问题定位后可整体摘除）——

	/// <summary>弹层标识：宿主类型 + 哈希前几位，用来在日志里把同一弹层的几行串起来。</summary>
	private static string PopupTag(Popup popup)
	{
		string host = popup.TemplatedParent?.GetType().Name ?? "无宿主";
		return $"{host}#{popup.GetHashCode():X4}";
	}

	private static string Fmt(Point? p)
	{
		return p.HasValue ? $"({p.Value.X:F1},{p.Value.Y:F1})" : "null";
	}

	private static void LogWatchResult(Popup popup, WatchState st, string reason, Point? actual, long startTick)
	{
		st.DiagDone = true;
		LogHelper.LogDiag(
			$"[Diag][弹层] 显形 {PopupTag(popup)} 原因={reason} 实际={Fmt(actual)} " +
			$"用时={Environment.TickCount64 - startTick}ms 重试={st.Retries}");
	}

	/// <summary>在模板展开后的视觉树里找第一个 Popup。ComboBox 的内部 popup
	/// 挂在模板里，走视觉树一层层翻就能拿到。</summary>
	private static Popup? FindTemplatePopup(DependencyObject root)
	{
		// Popup 本体不算其子树的 Visual（child window 内部是另一棵树），
		// 但 Popup 这个元素自身仍在宿主的视觉树里，逐层翻即可。
		if (root is Popup p)
		{
			return p;
		}
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			Popup? found = FindTemplatePopup(VisualTreeHelper.GetChild(root, i));
			if (found != null)
			{
				return found;
			}
		}
		return null;
	}

	private static class BooleanBoxes
	{
		public static readonly object True = true;
		public static readonly object False = false;
	}
}
