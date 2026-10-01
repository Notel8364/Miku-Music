using System;
using System.Collections.Generic;
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
		RecentlyPlayed.Insert(0, track);
		if (RecentlyPlayed.Count > 80)
		{
			RecentlyPlayed.RemoveRange(80, RecentlyPlayed.Count - 80);
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

	public static bool IsFavorite(SavedTrack track)
	{
		if (track == null)
		{
			return false;
		}
		return Favorites.Any(f => string.Equals(f.Key, track.Key, StringComparison.Ordinal));
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

	private static readonly object _mergeLock = new object();

	/// <summary>把云端拉取的收藏合并进本地（按 Key 去重，新条目插入到最前）。返回新增条数。</summary>
	public static int MergeFavorites(IReadOnlyList<SavedTrack> incoming)
	{
		if (incoming == null || incoming.Count == 0) return 0;
		int added = 0;
		lock (_mergeLock)
		{
			var existing = new HashSet<string>(Favorites.Select(f => f.Key), StringComparer.Ordinal);
			var fresh = new List<SavedTrack>();
			foreach (var t in incoming)
			{
				if (t == null || string.IsNullOrWhiteSpace(t.Title)) continue;
				if (existing.Add(t.Key))
				{
					fresh.Add(t);
				}
			}
			if (fresh.Count > 0)
			{
				Favorites.InsertRange(0, fresh);
				added = fresh.Count;
				Persist();
			}
		}
		if (added > 0)
		{
			Changed?.Invoke();
		}
		return added;
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