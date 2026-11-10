using System;
using System.IO;
using System.Windows;
using MusicBox.Controls;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>删本地音乐。音乐库和「我的收藏」两个页面共用一套，
/// 免得两个地方对「要不要连带删文件」「删完要不要清收藏」的处理长出两套来。</summary>
public static class TrackDelete
{
	/// <summary>确认框选「不再提醒」之后落盘的地方。放在设置里而不是静态字段：
	/// 静态字段重启就没了，等于这个勾白勾。</summary>
	public static bool AlwaysDeleteWithoutAsking
	{
		get => SafeGet();
		set
		{
			try
			{
				var s = SettingsManager.Instance;
				s.ConfirmTrackDelete = !value;
				SettingsManager.Save(s);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "保存删除确认设置失败");
			}
		}
	}

	private static bool SafeGet()
	{
		try
		{
			return !SettingsManager.Instance.ConfirmTrackDelete;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>删一首本地音乐：先问，然后删文件，再把收藏/最近播放里的残条目清掉。
	/// 返回 true 表示确实动了手。</summary>
	public static bool RequestDelete(Window? owner, string path, string title)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			if (!AlwaysDeleteWithoutAsking)
			{
				var r = ConfirmDialog.Show(
					owner,
					Loc.Default.Format("del_track_msg", title),
					Loc.Default["del_track_ok"],
					Loc.Default["del_track_cancel"],
					Loc.Default["del_track_dont_ask"],
					showDontAsk: true);
				if (r == ConfirmResult.Cancel)
				{
					return false;
				}
				if (r == ConfirmResult.ConfirmAndDontAsk)
				{
					AlwaysDeleteWithoutAsking = true;
				}
			}

			// 先清记录再删文件：反过来的话 File.Delete 一旦抛异常，
			// 收藏里已经没了、文件还在，用户会以为文件被误删了。
			RemoveFromLibraryRecords(path);

			if (File.Exists(path))
			{
				File.Delete(path);
			}
			MusicService.ReloadLibrary();
			Toast(owner, Loc.Default.Format("del_track_done", title));
			return true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "删除本地音乐失败: " + path);
			Toast(owner, Loc.Default.Format("del_track_fail", Path.GetFileName(path)));
			return false;
		}
	}

	/// <summary>把这一首从收藏、最近播放里摘掉。
	/// 收藏里留着一条点不开的条目，比删干净更让人困惑。</summary>
	private static void RemoveFromLibraryRecords(string path)
	{
		try
		{
			UserLibrary.RemoveFavorite("local:" + path);
			UserLibrary.RemoveRecent("local:" + path);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "清理收藏/最近记录失败: " + path);
		}
	}

	private static void Toast(Window? owner, string text)
	{
		if (owner is MainWindow mw)
		{
			mw.ShowToast(text);
		}
		else
		{
			LogHelper.Log(text);
		}
	}
}