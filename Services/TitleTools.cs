using System;
using System.Collections.Generic;
using System.Text;

namespace MusicBox.Services;

/// <summary>把视频标题/平台文件名的噪音挑掉，剩真正的歌名。
///
/// B 站下载的音频文件名 = 视频标题，通常是「【某某】歌名《Love Is Gone》- 搬运/翻唱」
/// 这种夹带货。直接拿去搜歌词会匹配到一百个不相关结果。
///
/// 挑法（用户指定）：
///   1) 书名号《…》里的名字优先级最高，取最长一段；
///   2) 括号（（）【】[]）里带分隔符（；;、/|·）时，取括号内部分隔后的最长段；
///   3) 都没有的话，短标题（≤ 指定长度）不动 —— 用户要求小于指定字符数不过滤；
///      长标题把可摘除的括号块全部清掉再返回。
/// 挑无可挑就原样返回，交给歌词源自己猜。</summary>
public static class TitleTools
{
	/// <summary>长于这个字符数的标题才做第 ③ 步的括号块清洗；之下的原样返回。</summary>
	private const int MinStripLength = 16;

	private static readonly (char Open, char Close)[] BracketPairs =
	{
		('（', '）'), ('(', ')'), ('【', '】'), ('[', ']'), ('「', '」'), ('『', '』')
	};

	/// <summary>括号内部「该算分隔符」的字符：中文分号、英文分号、顿号、斜杠、竖线、间隔号。</summary>
	private static readonly char[] InnerSeparators = { '；', ';', '、', '/', '|', '·' };

	/// <summary>挑出能拿去搜歌词的干净歌名。挑不出变化时返回原文。全空返回空串。</summary>
	public static string CleanBiliTitle(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return raw ?? "";
		}
		string s = CollapseSpaces(raw.Trim());

		// ① 书名号里是歌名的概率最高（搬运/翻唱标题最爱这么标）。取最长一段，
		//    免得「《A》《B》」这种一次标了好几首的取错对象。
		string book = LongestBookSegment(s);
		if (book.Length > 0)
		{
			return book;
		}

		// ② 括号里带分隔符：标题把歌名和说明挤在同一个括号里（比如（翻唱；xx）），
		//    挑分隔后的最长段。
		string inner = LongestSeparatedBracket(s);
		if (inner.Length > 0)
		{
			return inner;
		}

		// ③ 短标题不动 —— 用户要求「小于指定字符数不用过滤」，短标题摘括号容易摘过头。
		if (s.Length < MinStripLength)
		{
			return s;
		}
		string stripped = StripBracketContents(s);
		stripped = CollapseSpaces(stripped).Trim().TrimEnd('-', '–', '—', '_', '·', '：', ':', ' ');
		if (stripped.Length > 0 && stripped.Length < s.Length)
		{
			return stripped;
		}
		return s;
	}

	/// <summary>取《…》里最长的一段。没有书名号返回空串。</summary>
	private static string LongestBookSegment(string s)
	{
		int bestStart = -1, bestLen = 0;
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] == '《')
			{
				int j = s.IndexOf('》', i + 1);
				if (j > i + 1 && (j - i - 1) > bestLen)
				{
					bestStart = i + 1;
					bestLen = j - i - 1;
				}
				i = j;
			}
		}
		if (bestStart < 0)
		{
			return "";
		}
		return CollapseSpaces(s.Substring(bestStart, bestLen)).Trim();
	}

	/// <summary>括号里带「分隔符」时，返回内部分隔后的最长段；没有这样的括号返回空串。</summary>
	private static string LongestSeparatedBracket(string s)
	{
		string best = "";
		for (int i = 0; i < s.Length; i++)
		{
			foreach (var (opn, cls) in BracketPairs)
			{
				if (s[i] != opn)
				{
					continue;
				}
				int j = s.IndexOf(cls, i + 1);
				if (j > i + 1)
				{
					string inner = s.Substring(i + 1, j - i - 1).Trim();
					if (inner.IndexOfAny(InnerSeparators) >= 0)
					{
						string seg = LongestSegment(inner);
						if (seg.Length > best.Length)
						{
							best = seg;
						}
					}
					i = j;
					break;
				}
			}
		}
		return best;
	}

	private static string LongestSegment(string inner)
	{
		string best = "";
		foreach (string seg in inner.Split(InnerSeparators, StringSplitOptions.RemoveEmptyEntries))
		{
			string t = seg.Trim().Trim('-', '–', '—', '·', ' ');
			if (t.Length > best.Length)
			{
				best = t;
			}
		}
		return best;
	}

	/// <summary>已知的「说明性」噪音词：出现在标题里基本是搬运/翻唱/画质说明，
	/// 不是歌名的一部分。用来判断「这像视频标题」并帮清洗候选。</summary>
	private static readonly string[] NoiseKeywords =
	{
		"翻唱", "搬运", "转载", "完整版", "高音质", "无损", "动态歌词", "字幕",
		"官方", "现场", "伴奏", "纯音乐", "母带", "抖音", "中文字幕", "中日字幕",
		"Official", "Music Video", "Lyric Video", "Lyrics Video", "Audio"
	};

	/// <summary>看一段名字像不像「平台/视频标题」而不是规整的「歌手 - 歌名」。
	///
	/// 这是一条不依赖下载白名单的启发式：用户抱怨「各种音乐文件有些机制匹配不到
	/// 歌词，但其实能匹配」，根源就是只有 DownloadCache 命中的文件才会走
	/// <see cref="CleanBiliTitle"/>，其余按「歌手 - 歌名」硬拆，经常把标题里
	/// 的说明块当歌手，而网易云「歌手对不上就宁可不给」，于是一首有歌词的歌
	/// 被判成没歌词。有了这条，带书名号/方头括号/噪音词的名字不用进白名单
	/// 也会被当成平台标题去挑歌名。</summary>
	public static bool LooksLikePlatformTitle(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}
		foreach (char c in raw)
		{
			if (c is '《' or '》' or '【' or '】' or '「' or '」' or '『' or '』')
			{
				return true;
			}
		}
		if (raw.IndexOf('|') >= 0 || raw.IndexOf('｜') >= 0)
		{
			return true;
		}
		foreach (string k in NoiseKeywords)
		{
			if (raw.Contains(k, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>从一段原始名字里枚举「可拿去搜歌词」的候选歌名，按可信度从高到低，
	/// 已去重。调用方依次去搜，命中即用——这是替代下载白名单的「类似机制」：
	/// 不预先登记哪些文件该怎么拆，而是把所有可能的拆法都试一遍。</summary>
	public static IEnumerable<string> CandidateTitles(string? raw)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		string s = CollapseSpaces(raw?.Trim() ?? "");
		if (s.Length == 0)
		{
			yield break;
		}
		if (seen.Add(s))
		{
			yield return s;
		}
		// ① 书名号/分隔括号挑歌名（搬运/翻唱标题最常用）。
		string cleaned = CleanBiliTitle(s);
		if (cleaned.Length > 0 && seen.Add(cleaned))
		{
			yield return cleaned;
		}
		// ② 把所有括号块（含【】「」）整体摘掉，剩下「歌手 - 歌名」或纯歌名。
		string stripped = CollapseSpaces(StripBracketContents(s))
			.Trim().TrimEnd('-', '–', '—', '_', '·', '：', ':', '|', '｜', ' ');
		if (stripped.Length > 0 && seen.Add(stripped))
		{
			yield return stripped;
		}
		// ③ 去掉噪音词（「官方」「动态歌词」这类）。
		string denoised = RemoveNoiseTags(s);
		if (denoised.Length > 0 && seen.Add(denoised))
		{
			yield return denoised;
		}
		// ④ 「某某 - 歌名」取右段。
		int sep = s.IndexOf(" - ", StringComparison.Ordinal);
		if (sep > 0)
		{
			string right = CollapseSpaces(s.Substring(sep + 3)).Trim();
			if (right.Length > 0 && seen.Add(right))
			{
				yield return right;
			}
		}
	}

	/// <summary>把噪音词整段删掉并收拾残留首尾符号。</summary>
	public static string RemoveNoiseTags(string? raw)
	{
		if (string.IsNullOrEmpty(raw))
		{
			return raw ?? "";
		}
		string r = raw;
		foreach (string k in NoiseKeywords)
		{
			r = r.Replace(k, "", StringComparison.OrdinalIgnoreCase);
		}
		return CollapseSpaces(r).Trim().TrimEnd('-', '–', '—', '_', '·', '：', ':', '|', '｜', ' ');
	}

	/// <summary>把全部成对括号及其内容摘掉（(feat. xx)【PV】（翻唱）这类说明一起清），
	/// 没匹配到闭合符的开括号作半角字符丢弃。</summary>
	private static string StripBracketContents(string s)
	{
		var sb = new StringBuilder(s.Length);
		int i = 0;
		while (i < s.Length)
		{
			char c = s[i];
			int skipTo = -1;
			foreach (var (opn, cls) in BracketPairs)
			{
				if (c == opn)
				{
					int j = s.IndexOf(cls, i + 1);
					skipTo = j > i ? j + 1 : i + 1;
					break;
				}
			}
			if (skipTo < 0)
			{
				sb.Append(c);
				i++;
			}
			else
			{
				i = skipTo;
			}
		}
		return sb.ToString();
	}

	/// <summary>连续空白压成一个空格并去掉首尾空白。</summary>
	private static string CollapseSpaces(string s)
	{
		bool wasSpace = false;
		var sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (char.IsWhiteSpace(c))
			{
				if (!wasSpace)
				{
					sb.Append(' ');
				}
				wasSpace = true;
			}
			else
			{
				sb.Append(c);
				wasSpace = false;
			}
		}
		return sb.ToString().Trim();
	}
}