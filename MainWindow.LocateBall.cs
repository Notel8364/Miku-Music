using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox;

/// <summary>播放定位小球：拖动/惯性/缩放/落位保存，以及启动时的原位恢复。
///
/// 纯从 MainWindow.xaml.cs 搬出来的一段自成体系的交互逻辑（字段 + 事件处理器
/// + 动画辅助），行为、事件绑定、参数一个都没动；拆分目的只是让主窗口那个
/// 7000 多行的 God Object 少背一块全身而退。单击定位的 LocatePlayingTrack
/// 仍留在主窗口（它要跨页导航、找行、闪进度条，和其余流程耦合更深）。</summary>
public partial class MainWindow
{
	// ==================== 播放定位小球 ====================
	// 单击 = 定位正在播放的歌（跳来源页 + 行滚到视口中央 + 脉冲高亮 + 进度条闪点）；
	// 按住约 250ms = 进入拖动模式，松手落位写回设置，下次启动原位恢复。
	// 显隐开关已按产品决策恒开（设置页不再提供入口，配置读取处强制 true），
	// 小球永远显示，这里只保留启动时的原位恢复。

	/// <summary>启动路径恢复小球位置（原 SettingsPage 开关已撤，显隐恒为可见）。</summary>
	public void ApplyLocateBallVisibility()
	{
		PlayLocateBall.Visibility = Visibility.Visible;
		RestoreLocateBallPosition();
	}

	/// <summary>球底色跟随主题强调色（压到半透明）。ApplyAccentTheme 换色时一起刷。</summary>
	private void UpdateLocateBallTheme()
	{
		try
		{
			// 直接取 ApplyAccentTheme 算好的「画出来那份」强调色：
			// 它已按主题补过亮度，深色主题选纯黑当强调色也不会把球画没。
			if (Application.Current.Resources["AccentColor"] is Color c)
			{
				var fill = new SolidColorBrush(Color.FromArgb(0xB3, c.R, c.G, c.B));
				fill.Freeze();
				PlayLocateBallBg.Fill = fill;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "刷新定位小球主题色失败");
		}
	}

	private void RestoreLocateBallPosition()
	{
		double x = SettingsManager.Instance.LocateBallX;
		double y = SettingsManager.Instance.LocateBallY;
		// 用户还没挪过（NaN）就放右下：不压标题栏按钮，也离底栏控件远。
		if (double.IsNaN(x) || double.IsNaN(y))
		{
			x = ActualWidth - 78;
			y = ActualHeight - 190;
		}
		PlaceLocateBall(x, y);
	}

	private void PlaceLocateBall(double x, double y)
	{
		// 夹在窗口内：可以贴边，但不许整颗跑出窗口看不见。
		x = Math.Clamp(x, 4, Math.Max(4, ActualWidth - PlayLocateBall.Width - 4));
		y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - PlayLocateBall.Height - 4));
		PlayLocateBall.Margin = new Thickness(x, y, 0, 0);
	}

	private void SaveLocateBallPosition()
	{
		var s = SettingsManager.Instance;
		s.LocateBallX = PlayLocateBall.Margin.Left;
		s.LocateBallY = PlayLocateBall.Margin.Top;
		SettingsManager.Save(s);
	}

	// 拖动状态：按下 → 移动超过拖动阈值或按满 250ms = 拖动，跟手移动；
	// 阈值内松手 = 单击定位。原先只靠 250ms 计时器进拖动，快速一拖一放
	// 会被误判成单击、反而触发「跳转正在播放」，表现为球拖不动。
	private DispatcherTimer? _ballDragTimer;
	private bool _ballPressed;
	private bool _ballDragging;
	private Point _ballPressPos;

	// —— 手感物理：惯性滑行 + 弹性收尾 + 靠边小回弹 + 拖动微缩放 ——
	// 单击定位与长按拖动的判定阈值一概不动，这里只接管「松手之后」的收尾。
	private double _ballVelX;        // 松手惯性用的速度采样（px/事件 ≈ px/帧）
	private double _ballVelY;
	private long _ballLastMoveTick;  // 最近一次跟手移动的时间戳：停一拍再松手就不带惯性
	private int _ballAnimGen;        // 动画代次：新的按下会作废还没跑完的惯性/回弹动画

	// 惯性参数：松手速度按帧折算投影滑行距离。投影帧数固定 8 帧
	// （≈130ms 的滑行，跟以前一样「轻微」），滑多远由速度说了算、160px 封顶；
	// 主滑行 300ms QuadraticEase Out 收尾（200-300ms 规范内）。
	private const double BallInertiaFrames = 8.0;
	private const double BallInertiaMaxDist = 160.0;
	private const double BallInertiaMs = 300.0;

	// 拖动微缩放：拖住 1.05，松手 BounceEase 回弹到 1.0。
	private const double BallScaleDrag = 1.05;
	private const double BallScaleMs = 200.0;

	private void PlayLocateBall_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Grid ball)
		{
			return;
		}
		// 上一次的惯性/回弹/缩放动画还没收尾就又按下了：先停掉并把 Margin
		// 落到动画的当前值（直接停会弹回动画起点，球会瞬移），再进新一轮。
		CancelBallAnimations();
		_ballPressed = true;
		_ballDragging = false;
		_ballPressPos = e.GetPosition(this);
		// 捕获鼠标：拖出球体范围也继续跟手，不会中途丢掉。
		ball.CaptureMouse();
		_ballDragTimer?.Stop();
		_ballDragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		_ballDragTimer.Tick += (_, _) =>
		{
			_ballDragTimer!.Stop();
			// 计时期间已松手就不再进拖动模式（那次判定已交给单击分支）。
			if (_ballPressed && !_ballDragging)
			{
				EnterBallDragMode();
			}
		};
		_ballDragTimer.Start();
		e.Handled = true;
	}

	/// <summary>进入拖动模式（计时器满 250ms / 位移超阈值两条路都汇到这里）：
	/// 球轻微放大到 1.05，给一点「被捏住」的手感；速度采样从零起算。</summary>
	private void EnterBallDragMode()
	{
		_ballDragging = true;
		_ballVelX = 0;
		_ballVelY = 0;
		_ballLastMoveTick = 0;
		AnimateBallScale(BallScaleDrag, 120, bounce: false);
	}

	private void PlayLocateBall_MouseMove(object sender, MouseEventArgs e)
	{
		if (!_ballPressed || sender is not Grid ball)
		{
			return;
		}
		// MouseUp 被弹窗/切窗口吞掉的兜底：左键实际已松就按松手收尾，
		// 否则状态卡在拖动里，球会一直黏着鼠标走。
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			FinishBallDrag(ball);
			return;
		}
		Point p = e.GetPosition(this);
		if (!_ballDragging)
		{
			// 系统拖动阈值：按住后一抖就超，直接进拖动模式，不必等满 250ms。
			// 阈值按「按下点」算而不是上一次移动点，慢慢蹭也逃不过误判。
			if (Math.Abs(p.X - _ballPressPos.X) < SystemParameters.MinimumHorizontalDragDistance
				&& Math.Abs(p.Y - _ballPressPos.Y) < SystemParameters.MinimumVerticalDragDistance)
			{
				return;
			}
			_ballDragTimer?.Stop();
			EnterBallDragMode();
		}
		double dx = p.X - _ballPressPos.X;
		double dy = p.Y - _ballPressPos.Y;
		PlaceLocateBall(PlayLocateBall.Margin.Left + dx, PlayLocateBall.Margin.Top + dy);
		// 速度采样（px/事件）：指数平滑压掉手抖，松手惯性用最近几帧的平均速度。
		_ballVelX = _ballVelX * 0.35 + dx * 0.65;
		_ballVelY = _ballVelY * 0.35 + dy * 0.65;
		_ballLastMoveTick = Environment.TickCount64;
		_ballPressPos = p;
	}

	private void PlayLocateBall_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Grid ball)
		{
			return;
		}
		FinishBallDrag(ball);
		e.Handled = true;
	}

	/// <summary>收尾：拖过 = 松手回弹 + 惯性滑行（落位在动画结束写回设置）；
	/// 没拖过（阈值内）= 单击定位。先清状态再还捕获，LostMouseCapture 兜底
	/// 才不会重复处理。</summary>
	private void FinishBallDrag(Grid ball)
	{
		_ballDragTimer?.Stop();
		_ballDragTimer = null;
		bool wasDragging = _ballDragging;
		_ballPressed = false;
		_ballDragging = false;
		if (ball.IsMouseCaptured)
		{
			ball.ReleaseMouseCapture();
		}
		// 松手：球从 1.05 回弹到 1.0（BounceEase），拖过再带一段惯性滑行。
		AnimateBallScale(1.0, (int)BallScaleMs, bounce: true);
		if (wasDragging)
		{
			BeginBallInertia();
		}
		else
		{
			LocatePlayingTrack();
		}
	}

	/// <summary>松手惯性：按松手瞬间的速度投影一个滑行目标点（夹回窗口内），
	/// QuadraticEase EaseOut 弹性缓动收尾，结束后落位保存。
	/// 停了一拍（>90ms 没动）再松手当作没有速度，不滑。</summary>
	private void BeginBallInertia()
	{
		long idleMs = Environment.TickCount64 - _ballLastMoveTick;
		double vx = idleMs > 90 ? 0 : _ballVelX;
		double vy = idleMs > 90 ? 0 : _ballVelY;
		int gen = ++_ballAnimGen;

		double minX = 4, maxX = Math.Max(4, ActualWidth - PlayLocateBall.Width - 4);
		double minY = 4, maxY = Math.Max(4, ActualHeight - PlayLocateBall.Height - 4);
		double fx = PlayLocateBall.Margin.Left, fy = PlayLocateBall.Margin.Top;

		// 投影帧数固定 8 帧：滑行距离只由松手速度决定、160px 封顶，
		// 轻推一下就滑一小段（跟以前一样的「轻微」手感）。
		double dx = Math.Clamp(vx * BallInertiaFrames, -BallInertiaMaxDist, BallInertiaMaxDist);
		double dy = Math.Clamp(vy * BallInertiaFrames, -BallInertiaMaxDist, BallInertiaMaxDist);
		double tx = Math.Clamp(fx + dx, minX, maxX);
		double ty = Math.Clamp(fy + dy, minY, maxY);
		// 滑行会撞上窗口边被夹停：一声轻「嗒」。判定就放在投影这一拍 ——
		// 目标点被夹（±0.5px 容差内不算撞边）说明这段滑行注定贴边收场。
		// 界面音效开关在 UiSfx.Play 里当场读设置，这里不用管。
		if (Math.Abs((fx + dx) - tx) > 0.5 || Math.Abs((fy + dy) - ty) > 0.5)
		{
			UiSfx.PlayBallClamp();
		}
		// 2026-11-05 按用户要求去掉靠边回弹：目标直接夹回窗口内贴边停住。
		AnimateBallMarginTo(tx, ty, (int)BallInertiaMs,
			new QuadraticEase { EasingMode = EasingMode.EaseOut }, gen, SaveLocateBallPosition);
	}

	/// <summary>把球 Margin 动画到目标（只动 Left/Top）。Completed 里先把目标写成
	/// 基元值再停动画：HoldEnd 的动画一停属性会弹回基元值，顺序反了球会瞬移。</summary>
	private void AnimateBallMarginTo(double x, double y, int ms, EasingFunctionBase? ease, int gen, Action? done)
	{
		if (gen != _ballAnimGen)
		{
			return;
		}
		var anim = new ThicknessAnimation
		{
			To = new Thickness(x, y, 0, 0),
			Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
			EasingFunction = ease,
			FillBehavior = FillBehavior.HoldEnd
		};
		anim.Completed += (_, _) =>
		{
			if (gen != _ballAnimGen)
			{
				return;
			}
			PlayLocateBall.Margin = new Thickness(x, y, 0, 0);
			PlayLocateBall.BeginAnimation(FrameworkElement.MarginProperty, null);
			done?.Invoke();
		};
		PlayLocateBall.BeginAnimation(FrameworkElement.MarginProperty, anim);
	}

	/// <summary>球的缩放手感：拖住放大到 1.05，松手 BounceEase 回弹到 1.0。</summary>
	private void AnimateBallScale(double to, int ms, bool bounce)
	{
		EasingFunctionBase ease = bounce
			? new BounceEase { Bounces = 1, Bounciness = 4, EasingMode = EasingMode.EaseOut }
			: new QuadraticEase { EasingMode = EasingMode.EaseOut };
		var anim = new DoubleAnimation
		{
			To = to,
			Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
			EasingFunction = ease
		};
		BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
		BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
	}

	/// <summary>新的按下/拖动开始前停掉未完成的惯性/回弹/缩放动画。Margin 先读出
	/// 动画当前值写成基元值再停，否则一停就弹回动画起点、球瞬移。</summary>
	private void CancelBallAnimations()
	{
		_ballAnimGen++;
		double x = PlayLocateBall.Margin.Left, y = PlayLocateBall.Margin.Top;
		PlayLocateBall.BeginAnimation(FrameworkElement.MarginProperty, null);
		PlayLocateBall.Margin = new Thickness(x, y, 0, 0);
		BallScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		BallScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
	}

	/// <summary>捕获被别的弹层/窗口抢走的兜底：拖动强制中止，按「拖过」落位保存。
	/// 正常松手路径已经把状态清干净，这里只处理异常丢失，不会重复定位。</summary>
	private void PlayLocateBall_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (!_ballPressed && !_ballDragging)
		{
			return;
		}
		_ballDragTimer?.Stop();
		_ballDragTimer = null;
		_ballPressed = false;
		bool wasDragging = _ballDragging;
		_ballDragging = false;
		if (wasDragging)
		{
			SaveLocateBallPosition();
		}
	}
}
