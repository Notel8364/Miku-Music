using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MusicBox.Controls;

/// <summary>歌词取色器弹窗：SV 色域面板 + 色相条 + 两行预设色 + #RRGGBB 输入框。
///
/// 为什么不用 WinForms 的 ColorDialog：那是 Win2000 的观感，和整应用的
/// 深浅主题完全脱节；而且「跟随主题」这种语义（空值）在系统对话框里
/// 没地方放。自绘一次换来视觉统一，还能直接把当前色回显成 SV/H 坐标。
///
/// 交互模型：打开时把初始色折算成 (H,S,V) 铺好两个光标；拖 SV 面板改
/// 饱和/明度，拖色相条改色相，改动实时回调（选完即存，不需要「确定」键）。
/// 面板本身是三层叠加：底层纯色相、中层白→透明横渐变、顶层透明→黑竖渐变，
/// 这就是教科书式的 HSV 平面投影——没必要动用图像，三个渐变画刷就够。</summary>
public sealed class ColorPickerPopup
{
	// 两行预设色：第一行是歌词最常用的白→深灰（和设置页色板一致），
	// 第二行是常拿来强调的主题色。设置页色板里已有的色就不重复铺了，
	// 这里补的是色板没有、但拖 SV 面板又不容易精确拖出来的中间值。
	private static readonly string[] PresetRow1 =
	{
		"#FFFFFF", "#E8E8EA", "#C8C8CC", "#9A9AA0", "#6E6E76", "#4A4A52", "#2E2E34", "#1A1A20"
	};

	private static readonly string[] PresetRow2 =
	{
		"#FF6B8A", "#FFC44D", "#4FD1C5", "#A78BFA", "#7CDCFE", "#8AFF80", "#FF9F68", "#B8CCFF"
	};

	private const double SvWidth = 232;
	private const double SvHeight = 132;
	private const double HueHeight = 12;

	private readonly Popup _popup = new();
	private readonly Rectangle _svHue = new();
	private readonly Rectangle _svWhite = new();
	private readonly Rectangle _svBlack = new();
	private readonly Ellipse _svCursor = new();
	private readonly Rectangle _hueBar = new();
	private readonly Rectangle _hueCursor = new();
	private readonly Rectangle _preview = new();
	private readonly TextBox _hexBox = new();
	private readonly TextBlock _hexLabel = new();

	private Action<string>? _onPick;
	private bool _syncing; // hex 输入框 ↔ 面板坐标互相喂值时防回环
	private bool _svDrag;
	private bool _hueDrag;
	private double _h;
	private double _s;
	private double _v;

	private FrameworkElement? _themeSource;

	public ColorPickerPopup()
	{
		Build();
	}

	/// <summary>打开取色器。initial 是当前色（跟随主题时由调用方给主题实际色）；
	/// onPick 在每次改动时收到 "#RRGGBB"。StaysOpen=false：点外面自动收起收尾。</summary>
	public void Open(Color initial, FrameworkElement target, Action<string> onPick)
	{
		_onPick = onPick;
		_themeSource = target;
		ApplyTheme(target);
		(_h, _s, _v) = RgbToHsv(initial);
		if (_v <= 0)
		{
			// 纯黑没有色相可言：SV 光标落在左下角，色相保持上次的值即可。
			_h = 0;
		}
		_popup.PlacementTarget = target;
		_popup.Placement = PlacementMode.Bottom;
		_popup.HorizontalOffset = 0;
		_popup.IsOpen = true;
		RefreshAll();
	}

	private void Build()
	{
		_svHue.Width = SvWidth;
		_svHue.Height = SvHeight;
		_svHue.RadiusX = 6;
		_svHue.RadiusY = 6;
		_svWhite.Width = SvWidth;
		_svWhite.Height = SvHeight;
		_svWhite.RadiusX = 6;
		_svWhite.RadiusY = 6;
		_svWhite.IsHitTestVisible = false;
		_svWhite.Fill = new LinearGradientBrush(
			Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
			Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0);
		_svBlack.Width = SvWidth;
		_svBlack.Height = SvHeight;
		_svBlack.RadiusX = 6;
		_svBlack.RadiusY = 6;
		_svBlack.IsHitTestVisible = false;
		_svBlack.Fill = new LinearGradientBrush(
			Color.FromArgb(0x00, 0x00, 0x00, 0x00),
			Color.FromArgb(0xFF, 0x00, 0x00, 0x00), 90.0);
		_svHue.MouseLeftButtonDown += SvOnDown;
		_svHue.MouseMove += SvOnMove;
		_svHue.MouseLeftButtonUp += SvOnUp;
		_svHue.Cursor = Cursors.Cross;

		_svCursor.Width = 14;
		_svCursor.Height = 14;
		_svCursor.StrokeThickness = 2;
		_svCursor.Stroke = Brushes.White;
		_svCursor.Fill = Brushes.Transparent;
		_svCursor.IsHitTestVisible = false;
		_svCursor.Effect = new System.Windows.Media.Effects.DropShadowEffect
		{
			ShadowDepth = 0,
			BlurRadius = 4,
			Opacity = 0.6
		};

		var svCanvas = new Canvas();
		Canvas.SetLeft(_svCursor, 0);
		Canvas.SetTop(_svCursor, 0);
		svCanvas.Children.Add(_svCursor);
		svCanvas.IsHitTestVisible = false;

		var svGrid = new Grid { Width = SvWidth, Height = SvHeight, Cursor = Cursors.Cross };
		svGrid.Children.Add(_svHue);
		svGrid.Children.Add(_svWhite);
		svGrid.Children.Add(_svBlack);
		svGrid.Children.Add(svCanvas);
		svGrid.MouseLeftButtonDown += SvOnDown;
		svGrid.MouseMove += SvOnMove;
		svGrid.MouseLeftButtonUp += SvOnUp;

		_hueBar.Width = SvWidth;
		_hueBar.Height = HueHeight;
		_hueBar.RadiusX = 6;
		_hueBar.RadiusY = 6;
		_hueBar.Cursor = Cursors.Hand;
		_hueBar.Fill = BuildHueBrush();
		_hueBar.MouseLeftButtonDown += HueOnDown;
		_hueBar.MouseMove += HueOnMove;
		_hueBar.MouseLeftButtonUp += HueOnUp;

		_hueCursor.Width = 6;
		_hueCursor.Height = HueHeight + 8;
		_hueCursor.RadiusX = 2;
		_hueCursor.RadiusY = 2;
		_hueCursor.Fill = Brushes.White;
		_hueCursor.Stroke = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));
		_hueCursor.IsHitTestVisible = false;

		var hueCanvas = new Canvas { Width = SvWidth, Height = HueHeight, IsHitTestVisible = false };
		hueCanvas.Children.Add(_hueCursor);

		var hueGrid = new Grid { Width = SvWidth, Height = HueHeight };
		hueGrid.Children.Add(_hueBar);
		hueGrid.Children.Add(hueCanvas);

		// —— 预设色：两行，8 个一行 ——
		var presets = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
		presets.Children.Add(BuildPresetRow(PresetRow1));
		presets.Children.Add(BuildPresetRow(PresetRow2));

		// —— 底部：预览块 + #RRGGBB 输入 ——
		_preview.Width = 26;
		_preview.Height = 26;
		_preview.RadiusX = 5;
		_preview.RadiusY = 5;
		_preview.StrokeThickness = 1;

		_hexLabel.Text = "#RRGGBB";
		_hexLabel.VerticalAlignment = VerticalAlignment.Center;
		_hexLabel.Margin = new Thickness(10, 0, 0, 0);

		_hexBox.Width = 84;
		_hexBox.Height = 26;
		_hexBox.VerticalContentAlignment = VerticalAlignment.Center;
		_hexBox.MaxLength = 7;
		_hexBox.TextChanged += HexBox_TextChanged;
		_hexBox.KeyDown += HexBox_KeyDown;

		var bottom = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0, 10, 0, 0)
		};
		bottom.Children.Add(_preview);
		bottom.Children.Add(_hexLabel);
		bottom.Children.Add(_hexBox);

		var root = new StackPanel();
		root.Children.Add(svGrid);
		root.Children.Add(hueGrid);
		root.Children.Add(presets);
		root.Children.Add(bottom);

		var border = new Border
		{
			Child = root,
			Padding = new Thickness(12),
			CornerRadius = new CornerRadius(10),
			BorderThickness = new Thickness(1)
		};
		_popup.Child = border;
		_popup.AllowsTransparency = true;
		_popup.StaysOpen = false;
		_popup.Closed += (_, _) =>
		{
			// 收起即销毁回调：别让一个已经看不见的弹窗还往设置里写颜色。
			_onPick = null;
		};
	}

	private UIElement BuildPresetRow(string[] colors)
	{
		var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
		foreach (string hex in colors)
		{
			var swatch = new Rectangle
			{
				Width = 24,
				Height = 20,
				RadiusX = 4,
				RadiusY = 4,
				Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
				StrokeThickness = 1,
				Cursor = Cursors.Hand,
				ToolTip = hex,
				Margin = new Thickness(0, 0, 3, 0)
			};
			swatch.MouseLeftButtonDown += (_, e) =>
			{
				// 点预设 = 点了输入框里的一个完整色值，走同一条 SetHex 路径。
				SetHex(hex);
				e.Handled = true;
			};
			panel.Children.Add(swatch);
		}
		return panel;
	}

	private void ApplyTheme(FrameworkElement source)
	{
		// Popup 不在可视树里，DynamicResource 够不着主窗口的主题资源；
		// 打开那一刻从来源控件 FindResource 拍一份快照用——弹窗开着的时候
		// 主题不可能切（弹窗是模态体验），收起下次再取新的。
		var border = (Border)_popup.Child;
		border.Background = ResolveBrush(source, "FlyoutBgBrush", Color.FromRgb(0x1C, 0x1C, 0x1C));
		border.BorderBrush = ResolveBrush(source, "FlyoutBorderBrush", Color.FromRgb(0x40, 0x40, 0x48));
		Brush fg = ResolveBrush(source, "TextPrimaryBrush", Colors.White);
		_hexLabel.Foreground = fg;
		_hexBox.Foreground = fg;
		_hexBox.Background = ResolveBrush(source, "InputBgBrush", Color.FromRgb(0x1E, 0x1E, 0x1E));
		_hexBox.BorderBrush = ResolveBrush(source, "FlyoutBorderBrush", Color.FromRgb(0x40, 0x40, 0x48));
		_preview.Stroke = ResolveBrush(source, "FlyoutBorderBrush", Color.FromRgb(0x40, 0x40, 0x48));
	}

	private static Brush ResolveBrush(FrameworkElement source, string key, Color fallback)
	{
		if (source.TryFindResource(key) is Brush b)
		{
			return b;
		}
		return new SolidColorBrush(fallback);
	}

	// —— 状态刷新 ——

	private void RefreshAll()
	{
		// 顺序：先铺 SV 底层色相 → 再摆光标 → 最后喂输入框。
		_svHue.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
		UpdateSvCursor();
		UpdateHueCursor();
		_syncing = true;
		_hexBox.Text = ToHex(CurrentColor());
		_hexBox.CaretIndex = _hexBox.Text.Length;
		_syncing = false;
		_preview.Fill = new SolidColorBrush(CurrentColor());
	}

	private void UpdateSvCursor()
	{
		Canvas.SetLeft(_svCursor, _s * (SvWidth - _svCursor.Width));
		Canvas.SetTop(_svCursor, (1 - _v) * (SvHeight - _svCursor.Height));
	}

	private void UpdateHueCursor()
	{
		Canvas.SetLeft(_hueCursor, _h / 360.0 * (SvWidth - _hueCursor.Width));
		Canvas.SetTop(_hueCursor, -4);
	}

	private Color CurrentColor()
	{
		return HsvToRgb(_h, _s, _v);
	}

	private void Emit()
	{
		_preview.Fill = new SolidColorBrush(CurrentColor());
		_syncing = true;
		string hex = ToHex(CurrentColor());
		if (_hexBox.Text != hex)
		{
			_hexBox.Text = hex;
			_hexBox.CaretIndex = hex.Length;
		}
		_syncing = false;
		_onPick?.Invoke(hex);
	}

	// —— SV 面板拖动 ——

	private void SvOnDown(object sender, MouseButtonEventArgs e)
	{
		_svDrag = true;
		var pos = e.GetPosition(_svHue);
		SvFromPoint(pos.X, pos.Y);
		_svHue.CaptureMouse();
		e.Handled = true;
	}

	private void SvOnMove(object sender, MouseEventArgs e)
	{
		if (!_svDrag || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		var pos = e.GetPosition(_svHue);
		SvFromPoint(pos.X, pos.Y);
	}

	private void SvOnUp(object sender, MouseButtonEventArgs e)
	{
		_svDrag = false;
		_svHue.ReleaseMouseCapture();
		e.Handled = true;
	}

	private void SvFromPoint(double x, double y)
	{
		_s = Math.Clamp(x / SvWidth, 0, 1);
		_v = 1 - Math.Clamp(y / SvHeight, 0, 1);
		UpdateSvCursor();
		Emit();
	}

	// —— 色相条拖动 ——

	private void HueOnDown(object sender, MouseButtonEventArgs e)
	{
		_hueDrag = true;
		double x = e.GetPosition(_hueBar).X;
		HueFromPoint(x);
		_hueBar.CaptureMouse();
		e.Handled = true;
	}

	private void HueOnMove(object sender, MouseEventArgs e)
	{
		if (!_hueDrag || e.LeftButton != MouseButtonState.Pressed)
		{
			return;
		}
		HueFromPoint(e.GetPosition(_hueBar).X);
	}

	private void HueOnUp(object sender, MouseButtonEventArgs e)
	{
		_hueDrag = false;
		_hueBar.ReleaseMouseCapture();
		e.Handled = true;
	}

	private void HueFromPoint(double x)
	{
		_h = Math.Clamp(x / SvWidth, 0, 1) * 360.0;
		UpdateHueCursor();
		// 色相变了，SV 底层那张「满饱和纯色」也得跟着换，
		// 不然面板上的颜色跟实际输出对不上。
		_svHue.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
		Emit();
	}

	// —— Hex 输入 ——

	private void SetHex(string hex)
	{
		try
		{
			var c = (Color)ColorConverter.ConvertFromString(hex);
			(_h, _s, _v) = RgbToHsv(c);
			RefreshAll();
			_onPick?.Invoke(ToHex(c));
		}
		catch
		{
			// 不是合法色值就不管：用户还在打字，等输完整再说。
		}
	}

	private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_syncing)
		{
			return;
		}
		string t = _hexBox.Text.Trim();
		// 接受 #RRGGBB 和裸 RRGGBB：粘贴 / 手打都顺手。
		if (t.StartsWith('#'))
		{
			t = t[1..];
		}
		if (t.Length == 6 && IsHexDigits(t))
		{
			SetHex("#" + t);
		}
	}

	private void HexBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter)
		{
			return;
		}
		// 回车 = 就算没输满 6 位也按当前内容试一次（3 位短写法也认）。
		string t = _hexBox.Text.Trim();
		if (t.Length > 0)
		{
			SetHex(t);
		}
		e.Handled = true;
	}

	private static bool IsHexDigits(string s)
	{
		foreach (char ch in s)
		{
			if (!Uri.IsHexDigit(ch))
			{
				return false;
			}
		}
		return true;
	}

	private static string ToHex(Color c)
	{
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
	}

	// —— HSV ↔ RGB ——
	// H: 0~360；S/V: 0~1。公式取自 HSV 标准六边形模型，
	// 与设计软件（PS/Sketch 的色板）保持一致的坐标语义。

	private static Color HsvToRgb(double h, double s, double v)
	{
		double c = v * s;
		double hp = h / 60.0;
		double x = c * (1 - Math.Abs(hp % 2 - 1));
		double r = 0, g = 0, b = 0;
		switch (Math.Floor(hp) % 6)
		{
			case 0: r = c; g = x; break;
			case 1: r = x; g = c; break;
			case 2: g = c; b = x; break;
			case 3: g = x; b = c; break;
			case 4: r = x; b = c; break;
			default: r = c; b = x; break;
		}
		double m = v - c;
		return Color.FromRgb(
			(byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
			(byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
			(byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
	}

	private static (double h, double s, double v) RgbToHsv(Color c)
	{
		double r = c.R / 255.0;
		double g = c.G / 255.0;
		double b = c.B / 255.0;
		double max = Math.Max(r, Math.Max(g, b));
		double min = Math.Min(r, Math.Min(g, b));
		double d = max - min;
		double h = 0;
		if (d > 0)
		{
			if (max == r)
			{
				h = 60 * (((g - b) / d) % 6);
			}
			else if (max == g)
			{
				h = 60 * ((b - r) / d + 2);
			}
			else
			{
				h = 60 * ((r - g) / d + 4);
			}
		}
		if (h < 0)
		{
			h += 360;
		}
		double s = max <= 0 ? 0 : d / max;
		return (h, s, max);
	}

	/// <summary>色相条背景：红→黄→绿→青→蓝→洋红→红，
	/// 六个停靠点各 60°，和 SV 面板的坐标轴严格对齐。</summary>
	private static Brush BuildHueBrush()
	{
		var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
		double[] hues = { 0, 60, 120, 180, 240, 300, 360 };
		foreach (double h in hues)
		{
			brush.GradientStops.Add(new GradientStop(HsvToRgb(h, 1, 1), h / 360.0));
		}
		return brush;
	}
}
