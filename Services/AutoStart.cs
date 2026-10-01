using Microsoft.Win32;

namespace MusicBox.Services;

/// <summary>开机自启动：写入/读取/删除当前用户 Run 注册表项。</summary>
public static class AutoStart
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "MikuMusic";

	public static bool IsEnabled()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
			object? value = key?.GetValue(ValueName);
			return value is string s && !string.IsNullOrWhiteSpace(s);
		}
		catch
		{
			return false;
		}
	}

	public static void SetEnabled(bool enable)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
			if (enable)
			{
				string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
				if (!string.IsNullOrWhiteSpace(exe))
				{
					key.SetValue(ValueName, "\"" + exe + "\"");
				}
			}
			else
			{
				key.DeleteValue(ValueName, false);
			}
		}
		catch
		{
		}
	}
}