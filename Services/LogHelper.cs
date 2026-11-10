using System;
using System.IO;
using System.Text;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>日志级别。数值越大越严重。</summary>
public enum LogLevel
{
	/// <summary>逐帧/逐次调用才记的细节，平时没人要看。</summary>
	Debug = 0,

	/// <summary>正常流程节点：启动、切歌、窗口就绪。</summary>
	Info = 1,

	/// <summary>出错了。带异常对象。</summary>
	Error = 2,

	/// <summary>定向诊断：和 Error 一样**永远落盘**、不看调试开关，
	/// 但本身不是错误，前缀 [Diag] 区分。
	/// 给「必须拿到现场才能查」的排查用（歌词滚动方向、按钮命中这类），
	/// 只记事件节点、不记帧，量可控；查完把调用删掉即可。</summary>
	Diag = 3,
}

/// <summary>
/// 写运行日志。
///
/// 所有落盘都收在 <see cref="Write"/> 一个出口里，「调试模式」只在这里判断一次：
///
///   Debug / Info  —— 要设置里勾了调试模式才写。这两级就是「详细日志」，
///                    平时不开就不落盘，免得一个播放器无休无止地写日志。
///   Error        —— 永远写。
///
/// Error 不跟着开关走是有意的：这九十来处 LogError 是出问题时**唯一**的现场。
/// 要是也跟着开关走，用户在没开调试模式的时候遇到故障，日志里会什么都没有，
/// 下次就只能靠猜——之前排查歌词上移动画就是这么定位不出来的。
/// 真要「连错误也不写」，把下面 Write 里的级别判断改成只看 Error 即可。
///
/// DebugMode 是每次落盘前现读的，SettingsManager 里改完立刻生效，不用重启。
/// </summary>
public static class LogHelper
{
	private static readonly object LockObj = new object();

	private static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBox", "log.txt");

	public static string LogFilePath => LogFile;

	/// <summary>当前是否在记详细日志（Debug / Info 两级的总开关）。</summary>
	public static bool VerboseEnabled
	{
		get
		{
			try
			{
				return SettingsManager.Instance.DebugMode;
			}
			catch
			{
				// 设置还没初始化（极早期启动、单元测试）时不该因为读开关就崩。
				return false;
			}
		}
	}

	public static void Log(string message) => Write(LogLevel.Info, message, null);

	public static void LogDebug(string message) => Write(LogLevel.Debug, message, null);

	public static void LogError(Exception? ex, string message) => Write(LogLevel.Error, message, ex);

	/// <summary>定向诊断日志：无视调试模式、永远写。见 <see cref="LogLevel.Diag"/>。</summary>
	public static void LogDiag(string message) => Write(LogLevel.Diag, message, null);

	public static void Flush()
	{
	}

	private static void Write(LogLevel level, string message, Exception? ex)
	{
		// 级别判断放在加锁之前：SettingsManager 取实例可能自己加锁，
		// 在我们的锁里再去拿有死锁的隐患。
		if (level < LogLevel.Error && !VerboseEnabled)
		{
			return;
		}
		lock (LockObj)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
				string tag = level switch
				{
					LogLevel.Debug => " [Debug]",
					LogLevel.Error => " [Error]",
					LogLevel.Diag => " [Diag]",
					_ => "",
				};
				string line = ex == null
					? $"[{DateTime.Now:HH:mm:ss.fff}]{tag} {message}"
					: $"[{DateTime.Now:HH:mm:ss.fff}]{tag} {message} | {ex}";
				File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
			}
			catch
			{
			}
		}
	}
}