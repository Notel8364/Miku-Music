using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MusicBox.Pages;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>账号收藏夹接入状态。和 <see cref="PlaylistFetchResult"/>（网易云/QQ 专用）
/// 分开定义：酷狗/汽水的「接口没打通」是永久性降级（占位），不是 Cookie 问题，
/// 归并到 CookieInvalid 会误导用户反复重登。</summary>
public enum AccountPlaylistStatus
{
	/// <summary>本地根本没存这个平台的 Cookie（未登录），占位提示去账号页登录。</summary>
	NotLoggedIn,

	/// <summary>歌单列表/曲目拉取成功。</summary>
	Ok,

	/// <summary>有 Cookie 但服务端拒绝（登录失效）。提示用户重新保存 Cookie。</summary>
	CookieInvalid,

	/// <summary>HTTP 请求本身失败（断网/超时/DNS），提示网络异常。</summary>
	NetworkError,

	/// <summary>接口试探过但没打通（签名/接口路径限制），分组显示「暂不支持」占位。</summary>
	Unsupported,
}

/// <summary>平台歌单的统一形状：四个平台的歌单 id 类型不同
/// （网易云/QQ 是 long，酷狗是 global_collection_id 字符串），统一转成字符串，
/// UI 与缓存键都不用再按平台分支。</summary>
public sealed class AccountPlaylist
{
	/// <summary>所属平台：netease / qq / kugou / soda。</summary>
	public string Platform { get; set; } = "";

	/// <summary>平台侧歌单 id 的字符串形式，取曲目时原样传回对应客户端。</summary>
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	/// <summary>歌单列表接口报的曲目数，只用于展示；真实条数以拉到的曲目为准。</summary>
	public int TrackCount { get; set; }

	public string CoverUrl { get; set; } = "";
}

/// <summary>一个平台分组的歌单列表 + 接入状态（状态决定分组头部显示什么）。</summary>
public sealed class AccountPlaylistGroup
{
	public string Platform { get; set; } = "";

	public AccountPlaylistStatus Status { get; set; } = AccountPlaylistStatus.Ok;

	public List<AccountPlaylist> Playlists { get; set; } = new();
}

/// <summary>
/// 音乐库「收藏夹」的聚合层：把四个平台账号下的歌单/曲目收进来，
/// 并维护一份「已拉取曲目」的内存搜索索引。
///
/// 为什么单独放一层而不写进 MusicLibraryPage：
/// 1) 页面只管排版和交互；网络编排、缓存、索引是纯数据逻辑，塞页面里会让
///    code-behind 膨胀到无法维护；
/// 2) 索引和缓存与页面生命周期无关（页面重建也该保留），放静态层天然满足。
///
/// 缓存与索引都在内存里、不落盘：曲目会随平台侧变动，落盘反而要处理过期问题；
/// 每次启动重新点一下就回来了，代价可接受（任务要求也只提内存缓存）。
/// </summary>
public static class AccountLibraryService
{
	/// <summary>已拉取过的歌单曲目缓存，键 = "平台:歌单id"。
	/// 二次点击同一歌单直接命中，不再发请求（任务硬性要求）。</summary>
	private static readonly Dictionary<string, List<object>> TrackCache = new(StringComparer.Ordinal);

	/// <summary>曲目缓存的锁。拉取链路 ConfigureAwait(false) 之后，
	/// 「批量加载任务」和「用户点歌单」可能同时在后台线程写这份字典，
	/// Dictionary 非线程安全，读写都要互斥。</summary>
	private static readonly object TrackCacheGate = new();

	/// <summary>搜索索引：所有被拉取过的曲目都进这里。增量追加，
	/// 后台批量加载每拉完一个歌单就补一批，搜索结果随之逐步变多。</summary>
	private static readonly List<object> Index = new();

	/// <summary>索引去重：同一平台的同一首歌（同 id）只进一次索引。
	/// 同一首歌出现在多个歌单里很常见，全存的话「全部歌曲」和搜索结果会重复。</summary>
	private static readonly HashSet<string> IndexKeys = new(StringComparer.Ordinal);

	/// <summary>读索引快照的锁。索引只在本方法/批量任务里增删，
	/// 但增删发生在后台任务线程，而 UI 线程随时在遍历，必须互斥。</summary>
	private static readonly object IndexGate = new();

	/// <summary>当前索引里的曲目数（给计数文案用）。</summary>
	public static int IndexCount { get { lock (IndexGate) return Index.Count; } }

	/// <summary>某歌单的曲目是否已在缓存里（决定点击时要不要发请求）。</summary>
	public static bool IsPlaylistCached(string platform, string id)
	{
		lock (TrackCacheGate) return TrackCache.ContainsKey(PlaylistKey(platform, id));
	}

	/// <summary>取缓存里的歌单曲目；没拉过返回 null（调用方自行决定是否发请求）。</summary>
	public static List<object>? GetCachedTracks(string platform, string id)
	{
		lock (TrackCacheGate)
		{
			return TrackCache.TryGetValue(PlaylistKey(platform, id), out var list) ? list : null;
		}
	}

	private static string PlaylistKey(string platform, string id) => platform + ":" + id;

	/// <summary>拉取全部四个平台的歌单列表。平台之间互不影响：
	/// 一个平台失败/不支持，其它平台的分组照常返回（降级要体面，不能一损俱损）。
	///
	/// 并发而不是串行：四个请求各打各的平台（网易云/QQ/酷狗/汽水分属不同域名），
	/// 不会互相抬高任一平台的请求频率，因此也不会额外触发风控；原来串行是出于
	/// 「同时打出去容易触发风控」的过度谨慎，实际四个请求落到四个不同主机，
	/// 并行把墙钟从「四个之和」降到「最慢那个」，收藏夹列表加载明显更快。
	/// 每个平台各自包 try/catch，一个失败只降级那一个分组，不影响其它（与串行一致）。</summary>
	public static async Task<List<AccountPlaylistGroup>> FetchAllPlaylistsAsync(CancellationToken ct = default)
	{
		var tasks = new List<Task<AccountPlaylistGroup>>
		{
			FetchNeteasePlaylistsAsync(ct),
			FetchQqPlaylistsAsync(ct),
			FetchKugouPlaylistsAsync(ct),
			FetchSodaPlaylistsAsync(ct),
			FetchBiliPlaylistsAsync(ct)
		};
		var groups = await Task.WhenAll(tasks);
		return groups.ToList();
	}

	private static async Task<AccountPlaylistGroup> FetchNeteasePlaylistsAsync(CancellationToken ct)
	{
		try
		{
			var (result, pls) = await NeteaseApiClient.FetchPlaylistsAsync(ct);
			return new AccountPlaylistGroup
			{
				Platform = "netease",
				Status = Map(result),
				Playlists = pls.Select(p => new AccountPlaylist
				{
					Platform = "netease",
					Id = p.Id.ToString(),
					Name = p.Name,
					TrackCount = p.TrackCount,
					CoverUrl = p.CoverUrl
				}).ToList()
			};
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库: 网易云歌单列表异常 " + ex.Message);
			return new AccountPlaylistGroup { Platform = "netease", Status = AccountPlaylistStatus.NetworkError };
		}
	}

	private static async Task<AccountPlaylistGroup> FetchQqPlaylistsAsync(CancellationToken ct)
	{
		try
		{
			var (result, pls) = await QqClient.FetchPlaylistsAsync(ct);
			return new AccountPlaylistGroup
			{
				Platform = "qq",
				Status = Map(result),
				Playlists = pls.Select(p => new AccountPlaylist
				{
					Platform = "qq",
					Id = p.DissId.ToString(),
					Name = p.Name,
					TrackCount = p.TrackCount,
					CoverUrl = p.CoverUrl
				}).ToList()
			};
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库: QQ歌单列表异常 " + ex.Message);
			return new AccountPlaylistGroup { Platform = "qq", Status = AccountPlaylistStatus.NetworkError };
		}
	}

	private static async Task<AccountPlaylistGroup> FetchKugouPlaylistsAsync(CancellationToken ct)
	{
		try
		{
			string cookie = SettingsManager.Instance.KugouCookie;
			var (status, pls) = await KugouClient.FetchAccountPlaylistsAsync(cookie, ct);
			return new AccountPlaylistGroup
			{
				Platform = "kugou",
				Status = status,
				Playlists = pls.Select(p => new AccountPlaylist
				{
					Platform = "kugou",
					Id = p.CollectionId,
					Name = p.Name,
					TrackCount = (int)p.Count
				}).ToList()
			};
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库: 酷狗歌单列表异常 " + ex.Message);
			return new AccountPlaylistGroup { Platform = "kugou", Status = AccountPlaylistStatus.NetworkError };
		}
	}

	private static async Task<AccountPlaylistGroup> FetchSodaPlaylistsAsync(CancellationToken ct)
	{
		try
		{
			string cookie = SettingsManager.Instance.SodaCookie;
			var (status, pls) = await SodaClient.FetchAccountPlaylistsAsync(cookie, ct);
			return new AccountPlaylistGroup { Platform = "soda", Status = status, Playlists = pls };
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库: 汽水歌单列表异常 " + ex.Message);
			return new AccountPlaylistGroup { Platform = "soda", Status = AccountPlaylistStatus.NetworkError };
		}
	}

	/// <summary>B站收藏夹：用户创建的每个收藏夹 = 一条歌单。没登录（没有
	/// BBDown cookie）报 CookieInvalid，页面按平台话术提示去账号页扫码。
	/// 接口失败降级 NetworkError，不影响其它平台分组。</summary>
	private static async Task<AccountPlaylistGroup> FetchBiliPlaylistsAsync(CancellationToken ct)
	{
		try
		{
			if (!BiliClient.HasUserCookie)
			{
				return new AccountPlaylistGroup { Platform = "bili", Status = AccountPlaylistStatus.CookieInvalid };
			}
			var folders = await BiliClient.FetchFavoriteFoldersAsync(ct);
			return new AccountPlaylistGroup
			{
				Platform = "bili",
				Status = folders.Count > 0 ? AccountPlaylistStatus.Ok : AccountPlaylistStatus.CookieInvalid,
				Playlists = folders.Select(f => new AccountPlaylist
				{
					Platform = "bili",
					Id = f.Id.ToString(),
					Name = f.Title,
					TrackCount = (int)f.Count
				}).ToList()
			};
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("音乐库: B站收藏夹列表异常 " + ex.Message);
			return new AccountPlaylistGroup { Platform = "bili", Status = AccountPlaylistStatus.NetworkError };
		}
	}

	/// <summary>网易云/QQ 的结果枚举 → 统一状态。两者语义一致，直接映射。</summary>
	private static AccountPlaylistStatus Map(PlaylistFetchResult result) => result switch
	{
		PlaylistFetchResult.Ok => AccountPlaylistStatus.Ok,
		PlaylistFetchResult.CookieInvalid => AccountPlaylistStatus.CookieInvalid,
		_ => AccountPlaylistStatus.NetworkError
	};

	/// <summary>拉一个歌单的曲目（带内存缓存）。返回的曲目是各平台自己的
	/// 曲目对象（NeteaseTrack/QqTrack/KugouTrack），播放/下载行同 OnlineMusicPage 的链路。</summary>
	public static async Task<(AccountPlaylistStatus status, List<object> tracks)> FetchPlaylistTracksAsync(
		string platform, string id, CancellationToken ct = default)
	{
		string key = PlaylistKey(platform, id);
		bool cachedHit;
		List<object>? cached;
		lock (TrackCacheGate)
		{
			cachedHit = TrackCache.TryGetValue(key, out var cachedList);
			cached = cachedList;
		}
		if (cachedHit && cached != null)
		{
			return (AccountPlaylistStatus.Ok, cached);
		}

		List<object> tracks;
		switch (platform)
		{
			case "netease":
			{
				if (!long.TryParse(id, out long pid))
				{
					return (AccountPlaylistStatus.CookieInvalid, new List<object>());
				}
				var (result, list) = await NeteaseApiClient.FetchPlaylistTracksAsync(pid, ct).ConfigureAwait(false);
				if (result != PlaylistFetchResult.Ok)
				{
					return (Map(result), new List<object>());
				}
				// ConfigureAwait(false)：1800 首歌单的 Cast/ToList 与上面客户端里的
				// 分批解析一并留在后台线程，UI 线程只收「已装填完毕」的整份列表。
				tracks = list.Cast<object>().ToList();
				break;
			}
			case "qq":
			{
				if (!long.TryParse(id, out long did))
				{
					return (AccountPlaylistStatus.CookieInvalid, new List<object>());
				}
				var (result, list) = await QqClient.FetchPlaylistSongsAsync(did, ct).ConfigureAwait(false);
				if (result != PlaylistFetchResult.Ok)
				{
					return (Map(result), new List<object>());
				}
				tracks = list.Cast<object>().ToList();
				break;
			}
			case "kugou":
			{
				var (result, list) = await KugouClient.FetchAccountPlaylistTracksAsync(
					SettingsManager.Instance.KugouCookie, id, ct).ConfigureAwait(false);
				if (result != AccountPlaylistStatus.Ok)
				{
					return (result, new List<object>());
				}
				// 列表接口给的是 SavedTrack（仅歌名/歌手/hash），而播放链路
				// （KugouClient.DownloadSongAsync）吃的是 KugouTrack，这里补上映射。
				tracks = list.Select(ToKugouTrack).Cast<object>().ToList();
				break;
			}
			case "soda":
			{
				// 汽水：2026-11-05 实测 /luna/pc 两个端点可达且无签名墙（详见
				// SodaClient 类头注释），曲目以 SavedTrack 形态直接入库——仅展示，
				// 播放链路未接（汽水曲目播放需要另起的直链/解密方案）。
				// 当前 settings.json 里的抖音 web 会话域不匹配时会拿到
				// CookieInvalid，状态透传给页面分流提示。
				var (result, list) = await SodaClient.FetchAccountPlaylistTracksAsync(
					SettingsManager.Instance.SodaCookie, id, ct).ConfigureAwait(false);
				if (result != AccountPlaylistStatus.Ok)
				{
					return (result, new List<object>());
				}
				tracks = list.Cast<object>().ToList();
				break;
			}
			case "bili":
			{
				// B站收藏夹：media_id 数字，内容自动翻页拉全。曲目是 BiliTrack，
				// 播放/下载走在线页同一条共享链路（PlayExternal 已有 BiliTrack 分支）。
				if (!long.TryParse(id, out long mediaId))
				{
					return (AccountPlaylistStatus.CookieInvalid, new List<object>());
				}
				var list = await BiliClient.FetchFavoriteFolderTracksAsync(mediaId, ct: ct).ConfigureAwait(false);
				if (list.Count == 0)
				{
					return (AccountPlaylistStatus.CookieInvalid, new List<object>());
				}
				tracks = list.Cast<object>().ToList();
				break;
			}
			default:
				// 未知平台防御分支，保证别抛。
				return (AccountPlaylistStatus.Unsupported, new List<object>());
		}

		lock (TrackCacheGate)
		{
			TrackCache[key] = tracks;
		}
		return (AccountPlaylistStatus.Ok, tracks);
	}

	/// <summary>酷狗列表接口返回 SavedTrack（hash/歌名/歌手），播放链路要 KugouTrack。
	/// 只回填播放真正用得到的字段：hash、文件名、歌手、时长、封面；
	/// 文件大小/专辑解析不出来就留默认值——只影响副标题展示，不影响播放。</summary>
	private static KugouTrack ToKugouTrack(SavedTrack s)
	{
		return new KugouTrack
		{
			Hash = s.Id,
			FileName = s.Title,
			SingerName = s.Artist,
			Duration = (int)(s.DurationMs / 1000),
			ImgUrl = s.Cover
		};
	}

	/// <summary>把一批曲目追加进搜索索引，返回「真正新增」的部分（键没重复的）。
	/// 调用方拿返回值做增量刷新：只把新增的过滤、渲染，已显示的不动。</summary>
	public static List<object> AddToIndex(string platform, IEnumerable<object> tracks)
	{
		var added = new List<object>();
		lock (IndexGate)
		{
			foreach (object t in tracks)
			{
				string key = TrackKey(platform, t);
				if (string.IsNullOrEmpty(key) || !IndexKeys.Add(key))
				{
					continue;
				}
				Index.Add(t);
				added.Add(t);
			}
		}
		return added;
	}

	/// <summary>曲目在缓存/索引里的去重键：平台 + 平台侧唯一 id。
	/// 跨平台不合并（网易云和 QQ 的同名歌是两条真实资产，硬并会并错版本）。</summary>
	private static string TrackKey(string platform, object t) => t switch
	{
		NeteaseTrack n => "netease:" + n.Id,
		QqTrack q => "qq:" + q.Mid,
		KugouTrack k => "kugou:" + k.Hash,
		// 汽水曲目以 SavedTrack 形态入库，Type 自带 "soda" 前缀，正好复用。
		SavedTrack s => s.Type + ":" + s.Id,
		BiliTrack b => "bili:" + b.Bvid,
		_ => ""
	};

	/// <summary>按关键词搜索引：歌名/歌手/专辑任一包含即命中（忽略大小写，
	/// 兜底再比一次压掉空格标点的键，跟本地库的搜索规则一致）。
	/// 返回快照列表，调用方可以放心枚举。</summary>
	public static List<object> SearchIndex(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return AllTracks();
		}
		string kw = keyword.Trim();
		string kwKey = TrackRows.NormalizeKey(kw);
		List<object> snapshot = AllTracks();
		var hits = new List<object>();
		foreach (object t in snapshot)
		{
			// 三路字段：任一命中即算。MatchesText 内部先原样包含、再比压缩键，
			// 与本地列表的「搜 cu 能找到 C U Again」行为保持一致。
			if (TrackRows.MatchesText(TitleOf(t), kw, kwKey)
				|| TrackRows.MatchesText(ArtistOf(t), kw, kwKey)
				|| TrackRows.MatchesText(AlbumOf(t), kw, kwKey))
			{
				hits.Add(t);
			}
		}
		return hits;
	}

	/// <summary>全部已索引曲目（合并去重由 AddToIndex 的键保证）。</summary>
	public static List<object> AllTracks()
	{
		lock (IndexGate)
		{
			return new List<object>(Index);
		}
	}

	/// <summary>索引条目的展示字段。放在这一层而不是页面：批量加载线程
	/// 也要取这些字段过滤，页面私有方法够不着后台。</summary>
	public static string TitleOf(object t) => t switch
	{
		NeteaseTrack n => n.Name,
		QqTrack q => q.Display,
		KugouTrack k => k.Display,
		SavedTrack s => s.Title,
		BiliTrack b => b.Title,
		_ => ""
	};

	public static string ArtistOf(object t) => t switch
	{
		NeteaseTrack n => n.ArtistNames,
		QqTrack q => q.Singer,
		KugouTrack k => k.SingerName,
		SavedTrack s => s.Artist,
		BiliTrack b => b.Author,
		_ => ""
	};

	public static string AlbumOf(object t) => t switch
	{
		NeteaseTrack n => n.AlbumInfo?.Name ?? "",
		QqTrack q => q.Album,
		KugouTrack k => k.AlbumName,
		_ => ""
	};

	/// <summary>行控件收藏/封面用的 SavedTrack 映射，直接复用 OnlineMusicPage
	/// 那份（internal static）：同一首歌两边存进收藏的 Key 必须一模一样，
	/// 各写一份迟早会漂移。</summary>
	public static SavedTrack? ToSavedTrack(object t) => OnlineMusicPage.ToSavedTrack(t);

	/// <summary>拉网易云/QQ 的账号名并写进设置持久化，任一平台的昵称「从无到有」
	/// 时返回 true（调用方据此重建收缩栏标题行，把账号名补上去）。
	/// 设计要点：
	/// · 账号名随 Cookie 存在设置里（NeteaseNick/QqNick），拿到一次就不再请求，
	///   收缩栏每次重建都读设置即可，不重复打账号接口；
	/// · 只在歌单列表加载成功后补拉一次 —— 拉列表本身已经证明登录态活着，
	///   昵称接口跟着这一次走，额外风控暴露几乎为零；
	/// · 失败静默（返回 false）：昵称是展示性信息，不值得为它弹提示。</summary>
	public static async Task<bool> FetchAccountNamesAsync(CancellationToken ct = default)
	{
		var s = SettingsManager.Instance;
		bool changed = false;

		if (!string.IsNullOrWhiteSpace(s.NeteaseCookie) && string.IsNullOrWhiteSpace(s.NeteaseNick))
		{
			try
			{
				string nick = await NeteaseApiClient.FetchAccountNameAsync(ct);
				if (nick.Length > 0)
				{
					s.NeteaseNick = nick;
					changed = true;
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("网易云账号名补拉失败: " + ex.Message);
			}
		}

		if (!string.IsNullOrWhiteSpace(s.QqCookie) && string.IsNullOrWhiteSpace(s.QqNick))
		{
			try
			{
				string nick = await QqClient.FetchAccountNameAsync(ct);
				if (nick.Length > 0)
				{
					s.QqNick = nick;
					changed = true;
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("QQ账号名补拉失败: " + ex.Message);
			}
		}

		if (BiliClient.HasUserCookie && string.IsNullOrWhiteSpace(s.BiliNick))
		{
			try
			{
				var (ok, _, uname) = await BiliClient.FetchAccountAsync(ct);
				if (ok && uname.Length > 0)
				{
					s.BiliNick = uname;
					changed = true;
				}
			}
			catch (Exception ex)
			{
				LogHelper.LogDebug("B站账号名补拉失败: " + ex.Message);
			}
		}

		if (changed)
		{
			SettingsManager.Save(s);
		}
		return changed;
	}

	/// <summary>平台账号名（网易云昵称 / QQ 昵称或 uin）。收缩栏标题行与
	/// 账号页顶部展示统一走这里，别处各自拼设置字段容易漂移。
	/// 没拉到过返回空串，调用方自行决定是否留空。</summary>
	public static string AccountNameOf(string platform) => platform switch
	{
		"netease" => SettingsManager.Instance.NeteaseNick ?? "",
		"qq" => SettingsManager.Instance.QqNick ?? "",
		"bili" => SettingsManager.Instance.BiliNick ?? "",
		_ => ""
	};
}
