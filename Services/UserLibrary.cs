using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicBox.Settings;

namespace MusicBox.Services;

public static class UserLibrary
{
	public static event Action? Changed;

	public static List<SavedTrack> RecentlyPlayed { get; private set; } = new List<SavedTrack>();

	public static List<SavedTrack> Favorites { get; private set; } = new List<SavedTrack>();

	static UserLibrary()
	{
		try
		{
			var s = SettingsManager.Instance;
			RecentlyPlayed = new List<SavedTrack>(s.RecentPlayed ?? new List<SavedTrack>());
			Favorites = new List<SavedTrack>(s.Favorites ?? new List<SavedTrack>());
		}
		catch
		{
		}
	}

	public static void NotePlay(SavedTrack track)
	{
		if (track == null || string.IsNullOrWhiteSpace(track.Title) || string.Equals(track.Title, "<no-music>", StringComparison.Ordinal))
		{
			return;
		}
		RecentlyPlayed.RemoveAll(t => string.Equals(t.Key, track.Key, StringComparison.Ordinal));
		track.PlayedAt = DateTime.Now;
		RecentlyPlayed.Insert(0, track);
		// 条数上限走设置（RecentMaxCount，默认 1000，0 = 不限）：原来是写死的 80，
		// 用户想留全量就留不住；上限太大又会让 settings.json 膨胀。
		// 写入时裁剪而不是加载时：记录一边攒一边瘦身，别等文件大了再补救。
		int max = SettingsManager.Instance.RecentMaxCount;
		if (max > 0 && RecentlyPlayed.Count > max)
		{
			RecentlyPlayed.RemoveRange(max, RecentlyPlayed.Count - max);
		}
		Persist();
		Changed?.Invoke();
	}

	public static void ClearRecent()
	{
		RecentlyPlayed.Clear();
		Persist();
		Changed?.Invoke();
	}

	/// <summary>清空全部收藏并落盘广播。「我的收藏」页右上角的清空键走这里。</summary>
	public static void ClearFavorites()
	{
		Favorites.Clear();
		Persist();
		Changed?.Invoke();
	}

	/// <summary>按「最近播放保留天数」设置清理一次，返回清掉的条数（给 Toast 用）。
	/// 0 = 永久保留，调用方此时不该调进来；真调了也按 0 条处理。
	/// 无时间戳的旧记录（老版本最多存 80 条且不记播放时间）视为超期一并清掉 ——
	/// 它们永远等不到「再听一次」来刷新时间，不清就成了清不掉的化石。</summary>
	public static int CleanupRecentByKeepDays()
	{
		int keepDays = Math.Clamp(SettingsManager.Instance.RecentKeepDays, 0, 365);
		if (keepDays <= 0)
		{
			return 0;
		}
		DateTime cutoff = DateTime.Now.AddDays(-keepDays);
		int before = RecentlyPlayed.Count;
		RecentlyPlayed.RemoveAll(t => t == null || t.PlayedAt == default || t.PlayedAt < cutoff);
		int removed = before - RecentlyPlayed.Count;
		if (removed > 0)
		{
			Persist();
			Changed?.Invoke();
		}
		return removed;
	}

	public static bool IsFavorite(SavedTrack track)
	{
		if (track == null)
		{
			return false;
		}
		return Favorites.Any(f => string.Equals(f.Key, track.Key, StringComparison.Ordinal));
	}

	/// <summary>只拿 key 问收藏状态。
	/// 底栏那颗心手上只有一个 key（曲名/歌手随时可能变），走这个省得再造一个
	/// SavedTrack —— 而 key 一旦对不上就是「明明收藏了却显示空心」。</summary>
	public static bool IsFavoriteByKey(string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return false;
		}
		return Favorites.Any(f => string.Equals(f.Key, key, StringComparison.Ordinal));
	}

	public static void ToggleFavorite(SavedTrack track)
	{
		if (track == null || string.IsNullOrWhiteSpace(track.Title))
		{
			return;
		}
		if (IsFavorite(track))
		{
			Favorites.RemoveAll(f => string.Equals(f.Key, track.Key, StringComparison.Ordinal));
		}
		else
		{
			Favorites.Insert(0, track);
		}
		Persist();
		Changed?.Invoke();
	}

	public static void RemoveFavorite(string key)
	{
		Favorites.RemoveAll(f => string.Equals(f.Key, key, StringComparison.Ordinal));
		Persist();
		Changed?.Invoke();
	}

	/// <summary>从最近播放里摘掉一条。之前没有这条是因为「最近播放」的删除键
	/// 只清列表不动文件，而删本地文件（音乐库 / 我的收藏的新删除键）会连带
	/// 把这里也清掉——留着一条点不开的记录比删干净更让人困惑。</summary>
	public static void RemoveRecent(string key)
	{
		RecentlyPlayed.RemoveAll(t => string.Equals(t.Key, key, StringComparison.Ordinal));
		Persist();
		Changed?.Invoke();
	}

	/// <summary>本地文件改名后同步收藏/最近播放里的 Path 与 Title。
	/// 自己重命名时文件监视器那套映射还没跑到，必须直接改，别等 ReconcileLibrary。</summary>
	public static void ApplyLocalRename(string oldPath, string newPath, string newTitle)
	{
		int changed = RewriteRename(Favorites, oldPath, newPath, newTitle);
		changed += RewriteRename(RecentlyPlayed, oldPath, newPath, newTitle);
		if (changed > 0)
		{
			Persist();
			Changed?.Invoke();
		}
	}

	private static int RewriteRename(List<SavedTrack> list, string oldPath, string newPath, string newTitle)
	{
		int changed = 0;
		foreach (SavedTrack t in list)
		{
			if (t != null
				&& string.Equals(t.Type, "local", StringComparison.Ordinal)
				&& !string.IsNullOrWhiteSpace(t.Path)
				&& string.Equals(t.Path, oldPath, StringComparison.OrdinalIgnoreCase))
			{
				t.Path = newPath;
				t.Title = newTitle;
				changed++;
			}
		}
		return changed;
	}

	/// <summary>仅改标题（B 站下载源改名；平台条目的 Key 不含路径，路径没变）。
	/// 收藏和最近播放共用 Key（bili:bvid），两处一起更。</summary>
	public static void Retitle(string key, string newTitle)
	{
		if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(newTitle))
		{
			return;
		}
		int changed = 0;
		foreach (SavedTrack t in Favorites)
		{
			if (string.Equals(t.Key, key, StringComparison.Ordinal) && !string.Equals(t.Title, newTitle, StringComparison.Ordinal))
			{
				t.Title = newTitle;
				changed++;
			}
		}
		foreach (SavedTrack t in RecentlyPlayed)
		{
			if (string.Equals(t.Key, key, StringComparison.Ordinal) && !string.Equals(t.Title, newTitle, StringComparison.Ordinal))
			{
				t.Title = newTitle;
				changed++;
			}
		}
		if (changed > 0)
		{
			Persist();
			Changed?.Invoke();
		}
	}

	/// <summary>把外部改名同步进收藏 / 最近播放：路径换成新路径，歌名换成新文件名。
	/// 返回被改过的条目数（0 表示没变化，不落盘也不广播）。
	///
	/// 为什么必须做：收藏里存的是「收藏那一刻」的标题和路径。
	/// 用户在文件资源管理器里重命名之后，程序不做这一步的话
	/// 「我的收藏」永远显示老名字，点播放还会因为路径失效而报错。
	///
	/// 只处理本地文件（Type=="local"）：在线条目的标题来自平台，改名无从谈起。
	/// 已经被真删掉的文件也不动 —— 删文件不该顺手把用户的收藏也清掉。</summary>
	public static int ReconcileLibrary()
	{
		int changed = 0;
		changed += ReconcileList(Favorites);
		changed += ReconcileList(RecentlyPlayed);
		if (changed > 0)
		{
			Persist();
			Changed?.Invoke();
		}
		return changed;
	}

	private static int ReconcileList(List<SavedTrack> list)
	{
		int changed = 0;
		foreach (SavedTrack t in list)
		{
			if (t == null || !string.Equals(t.Type, "local", StringComparison.Ordinal)
				|| string.IsNullOrWhiteSpace(t.Path))
			{
				continue;
			}
			string path = t.Path;
			if (!SafeExists(path))
			{
				// 原路径没了：如果是「改名」而不是「删除」，映射到新位置。
				string? mapped = MusicService.MapRenamedPath(path);
				if (string.IsNullOrEmpty(mapped) || !SafeExists(mapped))
				{
					// 监视器只管得到「程序开着的时候」发生的改名。
					// 关掉程序改的名（或者换机器、网络盘重新挂载）没有记录可查，
					// 只能拿曲库里还在的文件名做一次保守匹配。
					mapped = GuessRenamedInLibrary(t.Title, t.Path);
				}
				if (string.IsNullOrEmpty(mapped) || !SafeExists(mapped))
				{
					continue;
				}
				path = mapped;
			}
			string title = Path.GetFileNameWithoutExtension(path);
			bool pathChanged = !string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase);
			bool titleChanged = !string.Equals(t.Title, title, StringComparison.Ordinal);
			if (pathChanged || titleChanged)
			{
				t.Path = path;
				t.Title = title;
				changed++;
			}
		}
		return changed;
	}

	private static bool SafeExists(string path)
	{
		try
		{
			return File.Exists(path);
		}
		catch
		{
			return false;
		}
	}

	/// <summary>没有改名记录时的兜底匹配：拿曲库里现有的文件名找「最像那一首」的文件。
	///
	/// 刻意收得很紧，宁可匹配不上（收藏里留着老名字，用户自己改一次也就好了），
	/// 也不能把收藏指到别的歌上去 —— 那是更糟的错误。
	/// 条件：去掉所有装饰后两边有 ≥4 个字符的公共前缀，或短的一侧被长的完全包含；
	/// 而且全曲库只能有一个候选 —— 两个都像就说明是巧合，不认。
	/// </summary>
	private static string? GuessRenamedInLibrary(string oldTitle, string oldPath)
	{
		try
		{
			string oldKey = NameKey(oldTitle.Length > 0 ? oldTitle : Path.GetFileNameWithoutExtension(oldPath));
			if (oldKey.Length < 4 || MusicService.KnownMusic.Count == 0)
			{
				return null;
			}
			string? hit = null;
			foreach (string f in MusicService.KnownMusic)
			{
				string key = NameKey(Path.GetFileNameWithoutExtension(f));
				if (key.Length == 0 || !Similar(oldKey, key))
				{
					continue;
				}
				// 第二个候选出现了：分不清，就当没改名。
				if (hit != null)
				{
					return null;
				}
				hit = f;
			}
			return hit;
		}
		catch
		{
			return null;
		}
	}

	private static bool Similar(string a, string b)
	{
		if (string.Equals(a, b, StringComparison.Ordinal))
		{
			return true;
		}
		if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
		{
			return Math.Min(a.Length, b.Length) >= 4;
		}
		int i = 0;
		while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
		return i >= 4;
	}

	/// <summary>文件名/标题压成「只有字母数字和汉字」的比较键，装饰一律丢掉。</summary>
	private static string NameKey(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		var sb = new System.Text.StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				sb.Append(char.ToLowerInvariant(c));
			}
		}
		return sb.ToString();
	}

	private static void Persist()
	{
		try
		{
			var s = SettingsManager.Instance;
			s.RecentPlayed = new List<SavedTrack>(RecentlyPlayed);
			s.Favorites = new List<SavedTrack>(Favorites);
			SettingsManager.Save(s);
		}
		catch
		{
		}
	}
}