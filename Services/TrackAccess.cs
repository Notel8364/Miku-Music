using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>一首歌「在这个程序里到底能不能播」的三态结论。</summary>
public enum AccessState
{
	/// <summary>还没试过，只能按平台给的字段猜。</summary>
	Unknown,

	/// <summary>实测过：拿得到直链，能播能下。</summary>
	Playable,

	/// <summary>实测过：接口明确拒绝，或者报错里说是会员/付费。</summary>
	Restricted
}

/// <summary>VIP 标签到底该不该打。
///
/// 平台的收费字段（网易云的 fee、QQ 的 payplay、酷狗的 pay_type）只能说明
/// 「这首歌对口需要付费服务」，跟「本程序现在能不能播它不是一回事」——
/// 手机会员赠送、专辑解禁、账号 cookie、某些 quality 免费听统统会让字段失真，
/// 照着打标签就会出现「标了 VIP 结果点开能正常听」或者「没标点开要 VIP」。
/// 真正准的结论只有一个来源：实测。点播放真去拿过直链才知道行不行。
///
/// 所以这里的规则：
///   只有实测结论说了算 —— 拿得到直链能播能下的不打标签，
///   接口明确拒绝（或报错里说是会员/付费）的才打。
///   结论落盘，下次打开还在；没实测过的一律不打标签。</summary>
public static class TrackAccess
{
	/// <summary>某个条目的结论变了：行控件是按旧结论建出来的，订阅方负责更新。
	/// 参数是受影响条目的登记号（KeyOf），订阅方据此做定向行更新。
	/// 为什么带 key：VIP 探测开一轮会把整个歌单逐首实测（1550 首就是 1550 条结论），
	/// 以前事件不带参数、订阅方只能整页重画 —— 每条结论触发一次整页重画，
	/// 重画尾部又把探测重排一遍，形成 200ms 节拍的自激重画风暴（UI 未响应的主因）。
	/// 带 key 后订阅方可以只重建受影响的那一行，风暴从根上断掉。</summary>
	public static event Action<string?>? Changed;

	private static readonly object Sync = new();

	/// <summary>key → true = 确认受限，false = 确认能播。</summary>
	private static Dictionary<string, bool> _known = new(StringComparer.OrdinalIgnoreCase);

	private static bool _loaded;

	private static readonly string StorePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "track_access.json");

	// ── 条目识别 ──────────────────────────────────────────────────

	public static string PlatformOf(object? item) => item switch
	{
		NeteaseTrack => "netease",
		QqTrack => "qq",
		KugouTrack => "kugou",
		BiliTrack => "bili",
		// 收藏/最近播放/会话队列里存的是 SavedTrack，Type 字段记了来源平台。
		// 快捷键切歌要在播放前就知道这条受不受限，靠的就是这批对象。
		SavedTrack s => (s.Type ?? "").Trim().ToLowerInvariant(),
		_ => ""
	};

	private static string IdOf(object? item) => item switch
	{
		NeteaseTrack n => n.Id.ToString(),
		QqTrack q => q.Mid ?? "",
		KugouTrack k => k.Hash ?? "",
		BiliTrack b => b.Bvid ?? "",
		SavedTrack s => s.Id ?? "",
		_ => ""
	};

	private static string TitleOf(object? item) => item switch
	{
		NeteaseTrack n => n.Name ?? "",
		QqTrack q => q.Name ?? "",
		KugouTrack k => k.FileName ?? "",
		BiliTrack b => b.Display ?? "",
		SavedTrack s => s.Title ?? "",
		_ => ""
	};

	private static string ArtistOf(object? item) => item switch
	{
		NeteaseTrack n => n.ArtistNames ?? "",
		QqTrack q => q.Singer ?? "",
		KugouTrack k => k.SingerName ?? "",
		SavedTrack s => s.Artist ?? "",
		_ => ""
	};

	/// <summary>同一首歌的唯一登记号。有平台 id 用 id，
	/// 拿不到 id 才退化成「平台 + 曲名 + 歌手」（同一首歌换个源来也要认得出来）。</summary>
	public static string KeyOf(object? item)
	{
		string platform = PlatformOf(item);
		string id = IdOf(item).Trim();
		if (id.Length > 0)
		{
			return platform + "|" + id;
		}
		return platform + "|" + Fold(TitleOf(item)) + "|" + Fold(ArtistOf(item));
	}

	// ── 查询 ──────────────────────────────────────────────────────

	public static AccessState StateOf(object? item)
	{
		if (item == null)
		{
			return AccessState.Unknown;
		}
		EnsureLoaded();
		lock (Sync)
		{
			return _known.TryGetValue(KeyOf(item), out bool bad)
				? (bad ? AccessState.Restricted : AccessState.Playable)
				: AccessState.Unknown;
		}
	}

	/// <summary>该不该给这一条打 VIP 标签。只听实测结论：
	/// 接口明确拒绝过才打，没实测过（Unknown）和实测能播的一律不打 ——
	/// 平台的收费字段错得太多，拿它猜出来的标签只会误导。</summary>
	public static bool IsRestricted(object? item) => StateOf(item) == AccessState.Restricted;

	// ── 记录 ──────────────────────────────────────────────────────

	/// <summary>播成了 / 下载成了：这首歌在这个程序里是能拿的。</summary>
	public static void NotePlayable(object? item)
	{
		Note(item, false);
	}

	/// <summary>播成了 / 下载成了（手上只有 SavedTrack 时走这条）。</summary>
	public static void NotePlayable(string platform, string id, string? title = null, string? artist = null)
	{
		Note(platform, id, title, artist, false);
	}

	/// <summary>接口明确拒绝，或者报错里带着会员/付费：确实受限。</summary>
	public static void NoteRestricted(object? item)
	{
		Note(item, true);
	}

	/// <summary>SavedTrack 版的受限记录。</summary>
	public static void NoteRestricted(string platform, string id, string? title = null, string? artist = null)
	{
		Note(platform, id, title, artist, true);
	}

	private static void Note(object? item, bool restricted)
	{
		if (item == null)
		{
			return;
		}
		Note(PlatformOf(item), IdOf(item), TitleOf(item), ArtistOf(item), restricted);
	}

	private static void Note(string platform, string id, string? title, string? artist, bool restricted)
	{
		if (platform.Length == 0)
		{
			return;
		}
		id = (id ?? "").Trim();
		string key = id.Length > 0
			? platform + "|" + id
			: platform + "|" + Fold(title) + "|" + Fold(artist);
		if (Fold(title).Length == 0 && id.Length == 0)
		{
			return;
		}
		EnsureLoaded();
		bool changed = false;
		lock (Sync)
		{
			if (!_known.TryGetValue(key, out bool old) || old != restricted)
			{
				_known[key] = restricted;
				changed = true;
			}
			if (_known.Count > 4000)
			{
				foreach (string k in _known.Where(x => x.Value == false)
					.OrderBy(x => x.Key).Take(500).Select(x => x.Key).ToList())
				{
					_known.Remove(k);
				}
			}
		}
		if (!changed)
		{
			return;
		}
		Save();
		// 把受影响的登记号广播出去：订阅方只更新这个 key 对应的行，
		// 不再被迫整页重画（音乐库 1550 首的歌单逐条实测时曾以此打出重画风暴）。
		Changed?.Invoke(key);
	}

	/// <summary>报错文案里有没有「VIP / 会员 / 付费」的意思。
	/// 有就是明确的受限信号，可以放心记下来。</summary>
	public static bool SoundsRestricted(string? error)
	{
		if (string.IsNullOrWhiteSpace(error))
		{
			return false;
		}
		string e = error.ToLowerInvariant();
		return e.Contains("vip") || e.Contains("会员") || e.Contains("付费")
			|| e.Contains("版权") || e.Contains("copyright") || e.Contains("需要购买")
			|| e.Contains("试听") || e.Contains("付费歌曲");
	}

	/// <summary>折成比对用的样子：小写、去空格。</summary>
	private static string Fold(string? s)
		=> (s ?? "").Trim().ToLowerInvariant();

	// ── 落盘 ──────────────────────────────────────────────────────

	private static void EnsureLoaded()
	{
		lock (Sync)
		{
			if (_loaded)
			{
				return;
			}
			_loaded = true;
			try
			{
				string dir = Path.GetDirectoryName(StorePath) ?? "";
				if (dir.Length > 0)
				{
					Directory.CreateDirectory(dir);
				}
				if (File.Exists(StorePath))
				{
					var data = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(StorePath));
					if (data != null)
					{
						_known = new Dictionary<string, bool>(data, StringComparer.OrdinalIgnoreCase);
					}
				}
			}
			catch
			{
				// 记不住就每次重新猜，不影响使用
			}
		}
	}

	private static void Save()
	{
		try
		{
			Dictionary<string, bool> snapshot;
			lock (Sync)
			{
				snapshot = new Dictionary<string, bool>(_known);
			}
			File.WriteAllText(StorePath, JsonSerializer.Serialize(snapshot));
		}
		catch
		{
		}
	}
}
