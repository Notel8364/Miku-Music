using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicBox.Services;

public sealed class OnlineComment
{
	/// <summary>平台侧的评论 id（B 站 rpid、网易云 commentId）。
	/// 「展开回复」要用它当根评论 id 去找子回复，没有就展不开。</summary>
	public long Id { get; set; }

	public string User { get; set; } = "";

	public string Content { get; set; } = "";

	public string Time { get; set; } = "";

	/// <summary>点赞数。B 站 reply.like、网易云 likedCount。</summary>
	public long Likes { get; set; }

	/// <summary>这条评论下面被回复了多少条（B 站 reply.rcount）。
	/// 用来提示「展开 N 条回复」；为 0 表示没有回复。</summary>
	public int ReplyCount { get; set; }

	/// <summary>本条评论自身是不是在回复别人（B 站 reply.reply_type / parent 非 0）。
	/// 接口会直接把这些二级评论混在同一个数组里返回，
	/// 不标出来的话用户会觉得「怎么一半评论都在回复某条评论」。</summary>
	public bool IsReply { get; set; }

	/// <summary>它在回复谁（被回复者的昵称，B 站 reply.reply_control.reply_to_reply_uname）。</summary>
	public string ReplyTo { get; set; } = "";

	/// <summary>接口随这条一起带回来的子回复（B 站 reply.replies 前几条 / 网易云 beReplied）。</summary>
	public List<OnlineComment> Replies { get; set; } = new List<OnlineComment>();
}

public static class OnlineComments
{
	/// <summary>评论翻页游标。B 站用 next(=回复 id)，网易云用 offset。</summary>
	public sealed class Page
	{
		public List<OnlineComment> Comments { get; set; } = new List<OnlineComment>();
		/// <summary>下一页游标；空串表示没有更多了。</summary>
		public string Next { get; set; } = "";
	}

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(12)
	};

	static OnlineComments()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");
	}

	/// <summary>拉取评论。返回 null 表示接口失败/不支持，空列表表示无评论。</summary>
	public static async Task<List<OnlineComment>?> FetchAsync(string source, object track)
	{
		return source switch
		{
			"bili" when track is BiliTrack b => await FetchBiliAsync(b.Aid),
			"netease" when track is NeteaseTrack n => await FetchNeteaseAsync(n.Id),
			_ => null
		};
	}

	/// <summary>拉取一页评论。cursor 为空表示第一页；
	/// 之后传上一次的 Next，返回的 Next 为空串表示已经到底。</summary>
	public static async Task<Page?> FetchPageAsync(string source, object track, string cursor)
	{
		return source switch
		{
			"bili" when track is BiliTrack b => await FetchBiliPageAsync(b.Aid, cursor),
			"netease" when track is NeteaseTrack n => await FetchNeteasePageAsync(n.Id, cursor),
			_ => null
		};
	}

	// ---------- 发表评论 ----------

	/// <summary>当前是否具备发表评论的条件：Cookie 有效 + 拿得到 csrf。
	/// 两个条件缺一不可，缺了输入框就是禁用的。</summary>
	public static bool CanPost(string source)
	{
		return source switch
		{
			"bili" => BiliClient.TryGetCsrf(out _) && !string.IsNullOrWhiteSpace(BiliClient.UserCookie),
			"netease" => !string.IsNullOrWhiteSpace(NeteaseApiClient.Cookie),
			_ => false
		};
	}

	/// <summary>发表一条评论。返回空串表示成功，否则返回错误描述。</summary>
	public static async Task<string> PostAsync(string source, object track, string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return "empty";
		}
		try
		{
			return source switch
			{
				"bili" when track is BiliTrack b => await PostBiliAsync(b.Aid, message),
				"netease" when track is NeteaseTrack n => await PostNeteaseAsync(n.Id, message),
				_ => "unsupported"
			};
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "发表评论失败: " + source);
			return ex.Message;
		}
	}

	private static async Task<string> PostBiliAsync(long aid, string message)
	{
		if (!BiliClient.TryGetCsrf(out string csrf))
		{
			return "no-csrf";
		}
		string json = await BiliClient.PostWbiAsync("/x/v2/reply/wbi/main", new Dictionary<string, string>
		{
			["oid"] = aid.ToString(),
			["type"] = "1",
			["mode"] = "3",
			["message"] = message
		}, csrf);
		using var doc = JsonDocument.Parse(json);
		int code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
			? c.GetInt32() : -1;
		string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
		// 0 = 成功。B 站风控时常见 -352 / 12002 之类。
		return code == 0 ? "" : (msg.Length > 0 ? msg : "code " + code);
	}

	private static async Task<string> PostNeteaseAsync(long id, string message)
	{
		string cookie = NeteaseApiClient.Cookie;
		if (string.IsNullOrWhiteSpace(cookie))
		{
			return "no-cookie";
		}
		var form = new Dictionary<string, string>
		{
			["content"] = message,
			["type"] = "0",
			["oid"] = id.ToString()
		};
		using var req = new HttpRequestMessage(HttpMethod.Post,
			$"https://music.163.com/api/v1/resource/comments/R_SO_4_{id}")
		{
			Content = new FormUrlEncodedContent(form)
		};
		req.Headers.TryAddWithoutValidation("Cookie", cookie);
		req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
		req.Headers.TryAddWithoutValidation("Origin", "https://music.163.com");
		using HttpResponseMessage resp = await Http.SendAsync(req);
		string body = await resp.Content.ReadAsStringAsync();
		if (!resp.IsSuccessStatusCode)
		{
			return "HTTP " + (int)resp.StatusCode;
		}
		using var doc = JsonDocument.Parse(body);
		if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
			&& c.GetInt32() == 200)
		{
			return "";
		}
		return "code " + body.Length;
	}

	private static string FormatTime(long ts)
	{
		try
		{
			DateTime dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().DateTime;
			return dt.ToString("yyyy-MM-dd");
		}
		catch
		{
			return "";
		}
	}

	private static async Task<Page> FetchBiliPageAsync(long aid, string cursor)
	{
		var page = new Page();
		try
		{
			string url = $"https://api.bilibili.com/x/v2/reply/main?type=1&oid={aid}&mode=3&next={Uri.EscapeDataString(string.IsNullOrEmpty(cursor) ? "1" : cursor)}";
			string json = await Http.GetStringAsync(url);
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("data", out var data))
			{
				if (data.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in replies.EnumerateArray())
					{
						OnlineComment? c = ParseBiliReply(item);
						if (c != null)
						{
							page.Comments.Add(c);
						}
					}
				}
				// 置顶评论只在该视频第一页出现，插到最前面
				if (string.IsNullOrEmpty(cursor)
					&& data.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Object
					&& top.TryGetProperty("upper", out var upper) && upper.ValueKind == JsonValueKind.Object)
				{
					OnlineComment? pinned = ParseBiliReply(upper);
					if (pinned != null)
					{
						page.Comments.Insert(0, pinned);
					}
				}
				// 下一页游标：cursor 是上一页最后一条的 rpid。
				// 为 0 说明已经翻到底。
				if (data.TryGetProperty("cursor", out var cur) && cur.TryGetProperty("is_end", out var isEnd)
					&& isEnd.ValueKind == JsonValueKind.False)
				{
					if (data.TryGetProperty("cursor", out var c2) && c2.TryGetProperty("next", out var next))
					{
						page.Next = next.ValueKind == JsonValueKind.Number ? next.GetInt64().ToString() : next.GetString() ?? "";
					}
					if (page.Comments.Count == 0)
					{
						page.Next = "";
					}
				}
			}
		}
		catch
		{
			page.Next = "";
		}
		return page;
	}

	// ---------- 评论总数（给「按评论数排序」用）----------

	/// <summary>评论总数，按「平台+id」缓存。
	/// 同一个 id 反复排序 / 反复渲染时只问一次接口。</summary>
	private static readonly Dictionary<string, long> TotalCache = new Dictionary<string, long>();

	/// <summary>取评论总数，取不到返回 null（不是 0）。
	///
	/// 网易云的搜索结果里根本没有评论数（cloudsearch 只给 id/fee/pop），
	/// 但评论接口的 limit=1 只要总数、正文一个字都不返回，
	/// 一首歌一个请求，比拉整页评论便宜得多。
	/// 这个请求只在用户真的勾了「按评论数排序」时才发，
	/// 不然每页 30 个请求只为排个序，太不划算。
	/// </summary>
	public static async Task<long?> FetchTotalAsync(string source, object track)
	{
		string key = source + ":" + TrackId(track);
		if (key.Length > 1 && key[1] == ':' && key.Length > 2 && TotalCache.TryGetValue(key, out long hit))
		{
			return hit;
		}
		try
		{
			if (source == "netease" && track is NeteaseTrack n)
			{
				string url = $"https://music.163.com/api/v1/resource/comments/R_SO_4_{n.Id}?limit=1&offset=0";
				string json = await Http.GetStringAsync(url);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number)
				{
					long total = tot.GetInt64();
					TotalCache[key] = total;
					return total;
				}
			}
			else if (source == "bili" && track is BiliTrack b)
			{
				string url = $"https://api.bilibili.com/x/v2/reply/main?type=1&oid={b.Aid}&mode=3&next=1";
				string json = await Http.GetStringAsync(url);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("data", out var data)
					&& data.TryGetProperty("page", out var page)
					&& page.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Number)
				{
					long total = cnt.GetInt64();
					TotalCache[key] = total;
					return total;
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("取评论总数失败: " + source + " " + ex.Message);
		}
		return null;
	}

	private static string TrackId(object track) => track switch
	{
		NeteaseTrack n => n.Id.ToString(),
		BiliTrack b => b.Aid.ToString(),
		QqTrack q => q.Mid,
		KugouTrack k => k.Hash,
		_ => ""
	};

	/// <summary>拉某条评论下面的回复（点「展开 N 条回复」时用）。
	///
	/// 主列表接口只在每条评论里内嵌前几条回复，剩下的要单独问。
	/// 返回空列表表示「没有回复」或「接口没给」，调用方按前者处理，
	/// 不能当成失败弹错误 —— 用户点开发现 0 条是正常结果。
	/// </summary>
	public static async Task<List<OnlineComment>> FetchSubRepliesAsync(string source, object track, long rootId)
	{
		var list = new List<OnlineComment>();
		if (rootId <= 0)
		{
			return list;
		}
		try
		{
			if (source == "bili" && track is BiliTrack b)
			{
				string url = $"https://api.bilibili.com/x/v2/reply/reply?type=1&oid={b.Aid}&root={rootId}&pn=1&ps=20";
				string json = await Http.GetStringAsync(url);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("data", out var data)
					&& data.TryGetProperty("replies", out var replies)
					&& replies.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in replies.EnumerateArray())
					{
						OnlineComment? sub = ParseBiliReply(item);
						if (sub != null)
						{
							list.Add(sub);
						}
					}
				}
			}
			else if (source == "netease" && track is NeteaseTrack n)
			{
				string url = "https://music.163.com/api/v2/resource/comment/reply/get"
					+ $"?resourceId=R_SO_4_{n.Id}&resourceType=0&parentCommentId={rootId}&limit=20&offset=0";
				string json = await Http.GetStringAsync(url);
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("hotComments", out var hot) && hot.ValueKind == JsonValueKind.Array)
				{
					ReadNetease(hot, list);
				}
				if (doc.RootElement.TryGetProperty("comments", out var comments)
					&& comments.ValueKind == JsonValueKind.Array)
				{
					ReadNetease(comments, list);
				}
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogDebug("取回复失败: " + source + " " + ex.Message);
		}
		return list;
	}

	private static async Task<Page> FetchNeteasePageAsync(long id, string cursor)
	{
		var page = new Page();
		try
		{
			int offset = int.TryParse(cursor, out int o) ? o : 0;
			string url = $"https://music.163.com/api/v1/resource/comments/R_SO_4_{id}?limit=20&offset={offset}";
			string json = await Http.GetStringAsync(url);
			using var doc = JsonDocument.Parse(json);
			bool hot = string.IsNullOrEmpty(cursor);
			if (doc.RootElement.TryGetProperty("hotComments", out var hotArr) && hotArr.ValueKind == JsonValueKind.Array)
			{
				ReadNetease(hotArr, page.Comments);
			}
			if (page.Comments.Count == 0 && doc.RootElement.TryGetProperty("comments", out var comments)
				&& comments.ValueKind == JsonValueKind.Array)
			{
				ReadNetease(comments, page.Comments);
			}
			// total 是该资源的评论总数，翻够了就把游标清空
			if (doc.RootElement.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number)
			{
				page.Next = offset + page.Comments.Count < tot.GetInt32() ? (offset + page.Comments.Count).ToString() : "";
			}
			else
			{
				page.Next = page.Comments.Count > 0 ? (offset + page.Comments.Count).ToString() : "";
			}
		}
		catch
		{
			page.Next = "";
		}
		return page;
	}

	/// <summary>解析 B 站单条 reply。置顶评论和普通评论共用这一份，
	/// 免得两边字段解析不一致（点赞数在一边有、一边没有）。</summary>
	private static OnlineComment? ParseBiliReply(JsonElement item)
	{
		string uname = item.TryGetProperty("member", out var member)
			&& member.TryGetProperty("uname", out var un) ? un.GetString() ?? "" : "";
		string msg = item.TryGetProperty("content", out var content)
			&& content.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
		long ts = item.TryGetProperty("ctime", out var c) && c.ValueKind == JsonValueKind.Number
			? c.GetInt64() : 0;
		if (string.IsNullOrWhiteSpace(msg))
		{
			return null;
		}

		var r = new OnlineComment
		{
			User = string.IsNullOrEmpty(uname) ? "匿名" : uname,
			Content = BiliClient.StripHtml(msg),
			Time = FormatTime(ts)
		};

		if (item.TryGetProperty("rpid", out var rpid) && rpid.ValueKind == JsonValueKind.Number)
		{
			r.Id = rpid.GetInt64();
		}

		if (item.TryGetProperty("like", out var like) && like.ValueKind == JsonValueKind.Number)
		{
			r.Likes = like.GetInt64();
		}
		// rcount 是「被回复数」，不是本页渲染出来的条数
		if (item.TryGetProperty("rcount", out var rc) && rc.ValueKind == JsonValueKind.Number)
		{
			r.ReplyCount = rc.GetInt32();
		}
		// parent != 0 说明这条是挂在别人下面的二级评论
		if (item.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Number
			&& parent.GetInt64() != 0)
		{
			r.IsReply = true;
		}
		// 被回复者昵称在 reply_control 里
		if (item.TryGetProperty("reply_control", out var ctrl) && ctrl.ValueKind == JsonValueKind.Object
			&& ctrl.TryGetProperty("reply_to_reply_uname", out var toName))
		{
			string to = toName.GetString() ?? "";
			if (!string.IsNullOrEmpty(to))
			{
				r.ReplyTo = to;
				r.IsReply = true;
			}
		}

		// 接口会带几条内嵌回复，一起渲染出来
		if (item.TryGetProperty("replies", out var subs) && subs.ValueKind == JsonValueKind.Array)
		{
			foreach (var sub in subs.EnumerateArray())
			{
				OnlineComment? sc = ParseBiliReply(sub);
				if (sc == null)
				{
					continue;
				}
				sc.IsReply = true;
				if (string.IsNullOrEmpty(sc.ReplyTo))
				{
					sc.ReplyTo = r.User;
				}
				r.Replies.Add(sc);
			}
		}
		return r;
	}

	private static void ReadNetease(JsonElement arr, List<OnlineComment> into)
	{
		foreach (var item in arr.EnumerateArray())
		{
			string uname = item.TryGetProperty("user", out var user)
				&& user.TryGetProperty("nickname", out var un) ? un.GetString() ?? "" : "";
			string msg = item.TryGetProperty("content", out var ct) ? ct.GetString() ?? "" : "";
			long ts = item.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number
				? t.GetInt64() : 0;
			if (string.IsNullOrWhiteSpace(msg)) continue;
			var c = new OnlineComment { User = uname, Content = msg, Time = FormatTime(ts) };
			if (item.TryGetProperty("commentId", out var cid) && cid.ValueKind == JsonValueKind.Number)
			{
				c.Id = cid.GetInt64();
			}
			if (item.TryGetProperty("likedCount", out var liked) && liked.ValueKind == JsonValueKind.Number)
			{
				c.Likes = liked.GetInt64();
			}
			// 网易云：beReplied 是被回复的那条，replyCount 是它下面的回复总数
			if (item.TryGetProperty("replyCount", out var rc) && rc.ValueKind == JsonValueKind.Number)
			{
				c.ReplyCount = rc.GetInt32();
			}
			if (item.TryGetProperty("beReplied", out var be) && be.ValueKind == JsonValueKind.Array)
			{
				foreach (var b in be.EnumerateArray())
				{
					string target = b.TryGetProperty("user", out var bu)
						&& bu.TryGetProperty("nickname", out var bn) ? bn.GetString() ?? "" : "";
					if (string.IsNullOrEmpty(target)) continue;
					c.IsReply = true;
					c.ReplyTo = target;
					if (c.Replies.Count == 0)
					{
						string replyMsg = b.TryGetProperty("content", out var bc) ? bc.GetString() ?? "" : "";
						if (!string.IsNullOrWhiteSpace(replyMsg))
						{
							c.Replies.Add(new OnlineComment { User = uname, Content = replyMsg, IsReply = true, ReplyTo = target });
						}
					}
				}
			}
			into.Add(c);
		}
	}

	private static async Task<List<OnlineComment>?> FetchBiliAsync(long aid)
	{
		Page p = await FetchBiliPageAsync(aid, "");
		return p.Comments;
	}

	private static async Task<List<OnlineComment>?> FetchNeteaseAsync(long id)
	{
		Page p = await FetchNeteasePageAsync(id, "");
		return p.Comments;
	}
}