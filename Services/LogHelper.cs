using System;
using System.IO;
using System.Text;
using MusicBox.Settings;

namespace MusicBox.Services;

public static class LogHelper
{
	private static readonly object LockObj = new object();

	private static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBox", "log.txt");

	public static string LogFilePath => LogFile;

	public static void Log(string message)
	{
		Write($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
	}

	public static void LogDebug(string message)
	{
		try
		{
			if (SettingsManager.Instance.DebugMode)
			{
				Write($"[{DateTime.Now:HH:mm:ss.fff}] [Debug] {message}");
			}
		}
		catch
		{
		}
	}

	public static void LogError(Exception? ex, string message)
	{
		Write($"[{DateTime.Now:HH:mm:ss.fff}] {message} | {ex}");
	}

	public static void Flush()
	{
	}

	private static void Write(string line)
	{
		lock (LockObj)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
				File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
			}
			catch
			{
			}
		}
	}
}