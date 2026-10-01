using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace MusicBox.Services;

/// <summary>系统托盘图标（右下角）。程序关闭时保留到托盘继续播放。</summary>
public static class TrayIconService
{
	private static WinForms.NotifyIcon? _icon;

	public static Action? ShowWindowRequested { get; set; }

	public static Action? ToggleWindowRequested { get; set; }

	public static Action? PlayPauseRequested { get; set; }

	public static Action? PrevRequested { get; set; }

	public static Action? NextRequested { get; set; }

	public static Action? ExitRequested { get; set; }

	public static bool IsCreated => _icon != null;

	/// <summary>显示/隐藏托盘图标。图标对象仍然保留，所以再打开不用重建。</summary>
	public static void SetVisible(bool visible)
	{
		if (_icon == null)
		{
			return;
		}
		try
		{
			_icon.Visible = visible;
		}
		catch
		{
		}
	}

	public static bool IsVisible => _icon?.Visible ?? false;

	public static void Create()
	{
		if (_icon != null)
		{
			return;
		}
		try
		{
			System.Drawing.Icon? icon = null;
			try
			{
				string exe = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
				if (System.IO.File.Exists(exe))
				{
					icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
				}
			}
			catch
			{
			}

			var menu = new WinForms.ContextMenuStrip();
			menu.Items.Add(Loc.Default["tray_show"], null, (_, _) => ShowWindowRequested?.Invoke());
			menu.Items.Add(Loc.Default["tray_playpause"], null, (_, _) => PlayPauseRequested?.Invoke());
			menu.Items.Add(Loc.Default["tray_prev"], null, (_, _) => PrevRequested?.Invoke());
			menu.Items.Add(Loc.Default["tray_next"], null, (_, _) => NextRequested?.Invoke());
			menu.Items.Add(new WinForms.ToolStripSeparator());
			menu.Items.Add(Loc.Default["tray_exit"], null, (_, _) => ExitRequested?.Invoke());

			_icon = new WinForms.NotifyIcon
			{
				Icon = icon,
				Text = AppIdentity.DisplayName,
				ContextMenuStrip = menu,
				// 托盘图标是否常驻由设置里的「显示托盘图标」决定（默认显示）
				Visible = Settings.SettingsManager.Instance.ShowTrayIcon
			};
			_icon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();
			_icon.MouseUp += (_, e) =>
			{
				if (e.Button == WinForms.MouseButtons.Left)
				{
					ShowWindowRequested?.Invoke();
				}
			};

			if (icon == null)
			{
				CreateDefaultIcon();
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("托盘创建失败: " + ex.Message);
		}
	}

	/// <summary>
	/// 刷新托盘气泡（鼠标停在托盘图标上弹出来那块）。
	/// 上行是当前曲名（没有曲名就用文件名），下行固定程序名。
	/// NotifyIcon.Text 上限 63 字符，超了会抛 ArgumentException，所以必须自己截断；
	/// 托盘气泡不支持分号以外的富文本，字号统一，视觉上放大字体的效果靠 SMTC 浮层。
	/// </summary>
	public static void SetNowPlaying(string? title)
	{
		if (_icon == null)
		{
			return;
		}
		try
		{
			string head = string.IsNullOrWhiteSpace(title)
				? AppIdentity.DisplayName
				: title.Trim();
			// 留 1 个字符给结尾的 \r\n
			const int Limit = 63;
			if (head.Length > Limit - 1)
			{
				head = head.Substring(0, Limit - 1);
			}
			string text = head + "\r\n" + AppIdentity.DisplayName;
			if (text.Length > Limit)
			{
				text = text.Substring(0, Limit);
			}
			_icon.Text = text;
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("更新托盘气泡失败: " + ex.Message);
		}
	}

	private static void CreateDefaultIcon()
	{
		try
		{
			if (_icon == null) return;
			using var bmp = new System.Drawing.Bitmap(32, 32);
			using (var g = System.Drawing.Graphics.FromImage(bmp))
			{
				g.Clear(System.Drawing.Color.FromArgb(60, 140, 255));
				using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
				g.FillEllipse(brush, 7, 8, 6, 10);
				g.FillRectangle(brush, 12, 4, 4, 18);
				g.FillEllipse(brush, 18, 5, 6, 12);
			}
			IntPtr hIcon = bmp.GetHicon();
			_icon.Icon = System.Drawing.Icon.FromHandle(hIcon);
		}
		catch
		{
		}
	}

	public static void ShowBalloon(string text)
	{
		try
		{
			if (_icon == null) return;
			_icon.BalloonTipTitle = AppIdentity.DisplayName;
			_icon.BalloonTipText = text;
			_icon.ShowBalloonTip(2500);
		}
		catch
		{
		}
	}

	public static void Dispose()
	{
		try
		{
			if (_icon != null)
			{
				_icon.Visible = false;
				_icon.Dispose();
				_icon = null;
			}
		}
		catch
		{
		}
	}
}