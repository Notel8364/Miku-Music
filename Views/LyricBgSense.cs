using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MusicBox.Views;

/// <summary>
/// 歌词文字对桌面壁纸的对比度自适应。
///
/// 桌面歌词直接浮在壁纸上：用户挑的歌词色在浅色壁纸上看着正常，
/// 换到纯黑壁纸上就可能整个糊进去（未唱色给了深色时尤其明显，
/// 已唱色/译文色同理）。这里取一次壁纸的平均亮度缓存起来，
/// 渲染层在给文字上色前先过一遍 <see cref="EnsureReadable"/>：
/// 对比不够就把颜色往白（暗背景）或往黑（亮背景）混，
/// 色相不变，只是亮度被推到能分清的程度——
/// 「挑的还是那个颜色，只是在黑底上也看得清了」。
/// 壁纸亮度拿不到（纯色背景/注册表被改/解码失败）时返回 -1，
/// 调用方原样用色，行为跟以前完全一致。
/// </summary>
public static class LyricBgSense
{
	private static readonly object Gate = new();
	private static double _lum = -1;
	private static DateTime _at = DateTime.MinValue;

	/// <summary>桌面壁纸平均亮度（0=全黑 1=全白）。
	/// 结果缓存 60 秒：换壁纸不是高频操作，但用户换了壁纸想立刻看到效果，
	/// 缓存太长（比如按天）会让人以为没生效；60 秒内颜色刷新直接复用，
	/// 过期才重新解码一次 24px 的缩略图，开销可以忽略。</summary>
	public static double DesktopLuminance()
	{
		lock (Gate)
		{
			if (_lum >= 0 && DateTime.Now - _at < TimeSpan.FromSeconds(60))
			{
				return _lum;
			}
			_at = DateTime.Now;
			try
			{
				string? path = Registry.GetValue(
					@"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", null) as string;
				if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				{
					// 幻灯片/纯色背景下注册表可能指不到具体文件，
					// 退到系统转码好的当前壁纸（TranscodedWallpaper，JPEG）。
					path = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
						"Microsoft", "Windows", "Themes", "TranscodedWallpaper");
				}
				if (!File.Exists(path))
				{
					_lum = -1;
					return -1;
				}
				var bmp = new BitmapImage();
				bmp.BeginInit();
				bmp.CacheOption = BitmapCacheOption.OnLoad;
				bmp.UriSource = new Uri(path);
				bmp.DecodePixelWidth = 24; // 只要平均亮度，24px 足够，再大的原图也不卡
				bmp.EndInit();
				int w = bmp.PixelWidth, h = bmp.PixelHeight;
				if (w <= 0 || h <= 0)
				{
					_lum = -1;
					return -1;
				}
				var px = new byte[w * h * 4];
				bmp.CopyPixels(px, w * 4, 0);
				double sum = 0;
				int n = w * h;
				for (int i = 0; i < n; i++)
				{
					// BGRA 排列，感知亮度权重（ITU-R BT.709）。
					double r = px[i * 4 + 2] / 255.0;
					double g = px[i * 4 + 1] / 255.0;
					double b = px[i * 4] / 255.0;
					sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
				}
				_lum = sum / n;
				return _lum;
			}
			catch
			{
				_lum = -1;
				return -1;
			}
		}
	}

	/// <summary>按背景亮度把文字色推到可读范围。bgLum &lt; 0 = 背景未知，原样返回。
	///
	/// 规则（感知亮度 Y = 0.2126R + 0.7152G + 0.0722B）：
	/// · 背景偏暗（Y≤0.5）：文字感知亮度至少 max(0.42, 背景+0.25)，
	///   不够就往白混——黑壁纸上的深红/深青就是这样被提亮的；
	/// · 背景偏亮（Y&gt;0.5）：文字感知亮度至多 min(0.55, 背景−0.25)，
	///   超了就往黑混——白壁纸上挑白色/浅色同理。
	/// 往白/黑线性混合时感知亮度也线性变化，所以一次算比例就能精确到位，
	/// 色相不变，挑的颜色还是那个色系。</summary>
	public static Color EnsureReadable(Color c, double bgLum)
	{
		if (bgLum < 0)
		{
			return c;
		}
		double y = Luma(c);
		if (bgLum <= 0.5)
		{
			double need = Math.Max(0.42, bgLum + 0.25);
			if (y < need && y < 1)
			{
				double t = (need - y) / (1 - y);
				return Lerp(c, Colors.White, t);
			}
			return c;
		}
		double cap = Math.Min(0.55, bgLum - 0.25);
		if (y > cap && y > 0)
		{
			double t = (y - cap) / y;
			return Lerp(c, Colors.Black, t);
		}
		return c;
	}

	private static double Luma(Color c) =>
		(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

	private static Color Lerp(Color a, Color b, double t)
	{
		if (t < 0) t = 0;
		if (t > 1) t = 1;
		return Color.FromRgb(
			(byte)Math.Round(a.R + (b.R - a.R) * t),
			(byte)Math.Round(a.G + (b.G - a.G) * t),
			(byte)Math.Round(a.B + (b.B - a.B) * t));
	}
}
