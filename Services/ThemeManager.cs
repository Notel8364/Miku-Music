using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>
/// 浅色 / 深色两套配色。切换时直接改写 Application.Current.Resources 里那批画刷，
/// 所以 XAML 里引用这些画刷的地方必须写成 DynamicResource —— StaticResource
/// 在加载时就定死了，换主题不会跟着变。
///
/// 强调色（Accent*）不在这里管，由 MainWindow.ApplyAccentTheme 按用户选的色调算；
/// 只有两个底色随强调色走（AccentSubtleBrush / AccentSubtleHoverBrush），跟着一起算。
/// </summary>
public static class ThemeManager
{
	/// <summary>主题切换后触发，页面可以在这里重建那些用硬编码颜色建出来的行。</summary>
	public static event Action? ThemeChanged;

	private sealed record Palette(
		string PageBg, string LayerBg, string PaneBg,
		string CardBg, string CardBgSecondary, string CardBorder,
		string TextPrimary, string TextSecondary, string TextTertiary,
		string ControlFill, string ControlFillHover, string ControlFillPressed,
		string ControlStroke, string ControlStrokeHover,
		string SubtleFill, string SubtleFillHover, string SubtleFillPressed,
		string FlyoutBg, string FlyoutBorder, string TrackBg, string DisabledFg,
		string InputBg, string OverlayBg, string VideoCardMask, string Shadow);

	private static readonly Palette Dark = new Palette(
		PageBg: "#0C0C0C", LayerBg: "#202020", PaneBg: "#1C1C1F",
		CardBg: "#0DFFFFFF", CardBgSecondary: "#0AFFFFFF", CardBorder: "#1FFFFFFF",
		TextPrimary: "#FFFFFF", TextSecondary: "#C8FFFFFF", TextTertiary: "#8AFFFFFF",
		ControlFill: "#0DFFFFFF", ControlFillHover: "#15FFFFFF", ControlFillPressed: "#21FFFFFF",
		ControlStroke: "#15FFFFFF", ControlStrokeHover: "#3DFFFFFF",
		SubtleFill: "#0AFFFFFF", SubtleFillHover: "#0FFFFFFF", SubtleFillPressed: "#15FFFFFF",
		FlyoutBg: "#F21C1C1C", FlyoutBorder: "#28FFFFFF", TrackBg: "#1FFFFFFF", DisabledFg: "#5FFFFFFF",
		InputBg: "#FF1E1E1E", OverlayBg: "#B01A1A1A",
		// 视频背景时卡片压成 75% 近黑：视频只当纹理，浅色字才看得清。
		VideoCardMask: "#C00E0E0E", Shadow: "#000000");

	private static readonly Palette Light = new Palette(
		PageBg: "#F2F2F4", LayerBg: "#FFFFFF", PaneBg: "#FAFAFB",
		// 浅色下不能再用「半透明白 5%」当卡片底（白底上等于没有卡片），
		// 改成接近不透明的白色，卡片边界靠描边和阴影撑起来。
		CardBg: "#F5FFFFFF", CardBgSecondary: "#E8FFFFFF", CardBorder: "#14101828",
		TextPrimary: "#FF16181C", TextSecondary: "#FF4B4F57", TextTertiary: "#FF7C818B",
		ControlFill: "#0F16181C", ControlFillHover: "#1A16181C", ControlFillPressed: "#2616181C",
		ControlStroke: "#1F16181C", ControlStrokeHover: "#4D16181C",
		SubtleFill: "#0F16181C", SubtleFillHover: "#1A16181C", SubtleFillPressed: "#2616181C",
		FlyoutBg: "#FAFFFFFF", FlyoutBorder: "#1F16181C", TrackBg: "#1F16181C", DisabledFg: "#8A16181C",
		InputBg: "#FFFFFFFF", OverlayBg: "#B3FFFFFF",
		// 浅色下文字是深色，视频卡片就得反过来压成半透明白，否则深色字看不清。
		VideoCardMask: "#B8FFFFFF", Shadow: "#000000");

	public static bool IsDark { get; private set; } = true;

	/// <summary>把设置里的 ThemeMode 落到实际配色上。system 跟随 Windows 的应用主题。</summary>
	public static void ApplyFromSettings()
	{
		string mode = SettingsManager.Instance.ThemeMode ?? "dark";
		if (string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase))
		{
			mode = SystemThemePrefersDark() ? "dark" : "light";
		}
		Apply(string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark");
	}

	public static void Apply(string mode)
	{
		try
		{
			Palette p = string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase) ? Light : Dark;
			IsDark = !string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase);
			var res = Application.Current?.Resources;
			if (res == null)
			{
				return;
			}

			Put(res, "PageBgBrush", p.PageBg);
			Put(res, "LayerBgBrush", p.LayerBg);
			Put(res, "PaneBgBrush", p.PaneBg);
			Put(res, "CardBgBrush", p.CardBg);
			Put(res, "CardBgSecondaryBrush", p.CardBgSecondary);
			Put(res, "CardBorderBrush", p.CardBorder);
			Put(res, "TextPrimaryBrush", p.TextPrimary);
			Put(res, "TextSecondaryBrush", p.TextSecondary);
			Put(res, "TextTertiaryBrush", p.TextTertiary);
			Put(res, "ControlFillBrush", p.ControlFill);
			Put(res, "ControlFillHoverBrush", p.ControlFillHover);
			Put(res, "ControlFillPressedBrush", p.ControlFillPressed);
			Put(res, "ControlStrokeBrush", p.ControlStroke);
			Put(res, "ControlStrokeHoverBrush", p.ControlStrokeHover);
			Put(res, "SubtleFillBrush", p.SubtleFill);
			Put(res, "SubtleFillHoverBrush", p.SubtleFillHover);
			Put(res, "SubtleFillPressedBrush", p.SubtleFillPressed);
			Put(res, "FlyoutBgBrush", p.FlyoutBg);
			Put(res, "FlyoutBorderBrush", p.FlyoutBorder);
			Put(res, "TrackBgBrush", p.TrackBg);
			Put(res, "DisabledFgBrush", p.DisabledFg);
			Put(res, "InputBgBrush", p.InputBg);
			Put(res, "OverlayBgBrush", p.OverlayBg);
			Put(res, "VideoCardMaskBrush", p.VideoCardMask);
			Put(res, "ShadowColor", p.Shadow);

			ApplyAccentDerived(res);
			LogHelper.Log("主题已切换: " + (IsDark ? "深色" : "浅色"));
			ThemeChanged?.Invoke();
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "切换主题失败");
		}
	}

	/// <summary>两个跟着强调色走的底色，单独算，方便换色时一起更新。</summary>
	public static void ApplyAccentDerived(ResourceDictionary res)
	{
		object? raw = res["AccentColor"];
		if (raw is not Color accent)
		{
			return;
		}
		Put(res, "AccentSubtleBrush", Color.FromArgb(0x33, accent.R, accent.G, accent.B).ToString());
		Put(res, "AccentSubtleHoverBrush", Color.FromArgb(0x4D, accent.R, accent.G, accent.B).ToString());
	}

	/// <summary>视频背景时卡片该盖什么色（浅色/深色是反的，见 Palette 注释）。</summary>
	public static Brush VideoCardMaskBrush()
	{
		var res = Application.Current?.Resources;
		return res != null && res["VideoCardMaskBrush"] is Brush b ? b : Brushes.Black;
	}

	/// <summary>取当前主题下的某个画刷，给代码里建控件的地方用（TrackRows 等）。</summary>
	public static Brush Brush(string key, Brush fallback)
	{
		var res = Application.Current?.Resources;
		return res != null && res[key] is Brush b ? b : fallback;
	}

	public static SolidColorBrush Primary() =>
		(SolidColorBrush)Brush("TextPrimaryBrush", Brushes.White);

	public static SolidColorBrush Secondary() =>
		(SolidColorBrush)Brush("TextSecondaryBrush", Brushes.Gray);

	public static SolidColorBrush Tertiary() =>
		(SolidColorBrush)Brush("TextTertiaryBrush", Brushes.DimGray);

	/// <summary>当前强调色，代码里给链接/进度之类上色时用（会跟着 ApplyAccentTheme 走）。</summary>
	public static SolidColorBrush Accent() =>
		(SolidColorBrush)Brush("AccentBrush", new SolidColorBrush(Color.FromRgb(0x3C, 0x8C, 0xFF)));

	/// <summary>强调色的低透明底，代码里做「选中」高亮时用（跟 NavItem 选中底一致）。</summary>
	public static SolidColorBrush AccentSubtle() =>
		(SolidColorBrush)Brush("AccentSubtleBrush", new SolidColorBrush(Color.FromArgb(0x33, 0x3C, 0x8C, 0xFF)));

	/// <summary>标签行的悬停底，和标题栏按钮的 hover 保持同一套观感。</summary>
	public static SolidColorBrush RowHover() =>
		(SolidColorBrush)Brush("SubtleFillHoverBrush", new SolidColorBrush(Color.FromArgb(0x0F, 255, 255, 255)));

	/// <summary>选中的那一行：强调色描边 + 一点强调色底，不做整块实色填充。</summary>
	public static SolidColorBrush RowSelectedFill() =>
		(SolidColorBrush)Brush("AccentSubtleHoverBrush", new SolidColorBrush(Color.FromArgb(0x4D, 0x3C, 0x8C, 0xFF)));

	public static SolidColorBrush CardBorder() =>
		(SolidColorBrush)Brush("CardBorderBrush", new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)));

	public static SolidColorBrush CardFill() =>
		(SolidColorBrush)Brush("CardBgBrush", new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)));

	private static bool SystemThemePrefersDark()
	{
		try
		{
			var key = Microsoft.Win32.Registry.CurrentUser
				.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
			object? value = key?.GetValue("AppsUseLightTheme");
			key?.Dispose();
			// 缺省按深色处理：这套界面本来就是深色排版起家的
			return value == null || value is int i && i == 0;
		}
		catch
		{
			return true;
		}
	}

	private static void Put(ResourceDictionary res, string key, string hex)
	{
		try
		{
			if (hex.StartsWith("#", StringComparison.Ordinal) && hex.Length == 9)
			{
				var c = (Color)ColorConverter.ConvertFromString(hex);
				var b = new SolidColorBrush(c);
				b.Freeze();
				res[key] = b;
				return;
			}
			res[key] = (Brush)new BrushConverter().ConvertFromString(hex)!;
		}
		catch
		{
		}
	}
}
