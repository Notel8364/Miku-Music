using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBox.Services;

/// <summary>搜索用的模糊匹配：扩展查询词、给结果打分、找出标题里要高亮的片段。
///
/// 三个问题是一起冒出来的：
/// 1. 用户搜「云鹤」，歌名是《雲鶴サクリファイス》——各平台自己的搜索接口
///    简繁通吃，但**不认「繁转简」**，所以一个字的差别就搜不到。
/// 2. 搜到了也不一定排在前面：接口是按热度/相关度混着给的，
///    名字里带关键词的那几首经常落在第二页。
/// 3. 命中了看不出来：列表里是一排歌名，用户得自己扫。
///
/// 所以这里只做三件事：把关键词扩成几个等价的查询词、给每条结果算一个
/// 0~1 的相似度、把命中片段的下标算出来给 UI 高亮。都不碰网络，也不碰播放队列。
/// </summary>
internal static class SearchMatcher
{
	/// <summary>归一化时要丢掉的字符：空白 + 常见标点 + 括号。
	/// 和 OnlineMusicPage.Normalize 用的是同一套，别改一处漏一处
	/// （那条是拿去当去重 key 的，两边口径不一样会出现「看着一样却当成两首」）。</summary>
	private const string NoiseChars = " \t\r\n\u3000-_.,()（）[]【】,，、/·!！?？'\"“”‘’:：;；";

	private static readonly Regex NoiseRun = new(@"[\s\-_\.\u3000()（）\[\]【】,，、/·!！?？'""“”‘’:：;；]",
		RegexOptions.Compiled);

	private static readonly char[] NoiseCharArray = NoiseChars.ToCharArray();

	/// <summary>括号里的内容多半是 feat. / Live / remix / cover 这类装饰，
	/// 拿它当关键词去搜基本搜不到东西——用户搜「云鹤」不会想搜「云鹤 (Live版)」。</summary>
	private static readonly Regex DecoratorRun = new(@"\s*[\(\（\[【][^\(\）\[\]【】]*[\)\）\]】]\s*", RegexOptions.Compiled);

	/// <summary>搜索词的等价写法，按「最可能被用户接受」排序。
	///
	/// 第 0 个一定就是原词本身（调用方拿它当主查询用），后面才是补充查询：
	/// 简转繁、繁转简、再各自去掉括号装饰。返回结果去重且保持顺序。</summary>
	public static IReadOnlyList<string> QueryVariants(string keyword)
	{
		var result = new List<string>();
		void Add(string? q)
		{
			string s = (q ?? "").Trim();
			// List<string>.Contains 没有带比较器的重载，别用 LINQ 的 Contains
			// （那个会对整个列表做一次字符串比较，这里列表只有个位数，但写法上
			//   一旦被复制到别处就会变成 O(n²)，而且读起来像 LINQ）。
			bool dup = false;
			foreach (string existing in result)
			{
				if (string.Equals(existing, s, StringComparison.OrdinalIgnoreCase))
				{
					dup = true;
					break;
				}
			}
			if (s.Length > 0 && !dup)
			{
				result.Add(s);
			}
		}

		Add(keyword);
		string trad = ChineseConvert.ToTraditional(keyword);
		string simp = ChineseConvert.ToSimplified(keyword);
		Add(simp);
		Add(trad);
		// 日文新字体折回简体：「砕月」要额外发一次「碎月」，
		// 否则中文平台的搜索接口认不出这个词——各平台的接口简繁通吃，
		// 但没有一份知道日文新字体这一层。
		Add(ChineseConvert.Normalize(keyword));
		// 装饰去掉后常常正好等于另一个变体，Add 内部会去重。
		foreach (string baseText in new[] { keyword, trad, simp, ChineseConvert.Normalize(keyword) })
		{
			Add(StripDecorators(baseText));
		}
		return result;
	}

	/// <summary>去掉括号里的 feat./Live/remix 等装饰；去掉后什么都不剩就原样返回。
	///
	/// 只删括号里的内容和两端残留的分隔符，不会把整串里的标点都抹掉——
	/// 那是 <see cref="Normalize"/> 干的事。「Love Live!」应该还是「Love Live!」，
	/// 抹成「LoveLive」等于凭空多发一次必然没用的请求。</summary>
	public static string StripDecorators(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return text ?? "";
		}
		if (text.IndexOfAny(new[] { '(', '（', '[', '【' }) < 0)
		{
			return text.Trim();
		}
		string stripped = DecoratorRun.Replace(text, " ").Trim(NoiseCharArray);
		return stripped.Length > 0 ? stripped : text.Trim();
	}

	/// <summary>这条结果跟关键词有多像，0~1，越大越像。
	///
	/// 归一化 + 简繁归一之后比，所以「雲鶴」和「云鹤」在比较时是同一个词。
	/// 完全不命中也给一个很低的分（bigram 相似度），这样排序稳定，
	/// 不会因为排序键全相等而让 List.Sort 随机打乱。</summary>
	public static double Score(string? title, string? artist, string keyword)
	{
		string k = Normalize(keyword);
		if (k.Length == 0)
		{
			return 0;
		}
		double best = ContainmentScore(k, Normalize(title));
		if (artist != null)
		{
			// 歌名没命中、歌手名命中：也算相关，但排不到歌名命中的前面。
			best = Math.Max(best, ContainmentScore(k, Normalize(artist)) * 0.6);
		}
		if (best <= 0)
		{
			// 一个字都没对上时给一点相似度，别让「差一个字」和「完全无关」并排。
			string t = Normalize(title);
			best = Math.Max(0.3 * Dice(k, t, bigrams: true), 0.25 * Dice(k, t, bigrams: false));
		}
		return best;
	}

	/// <summary>关键词在标题里的命中片段（原文下标），用来高亮。
	///
	/// 先按字面找，找不到再走「归一化 + 繁转简」那条路，并把命中的位置
	/// 反查回原文下标——<see cref="ChineseConvert"/> 是逐字映射，长度不变，
	/// 所以下标是对得上的，「雲鶴」也能把「云鹤」这两个字标出来。</summary>
	public static IReadOnlyList<(int Start, int Length)> FindRanges(string? title, string? keyword)
	{
		if (string.IsNullOrEmpty(title) || string.IsNullOrWhiteSpace(keyword))
		{
			return Array.Empty<(int, int)>();
		}

		var ranges = new List<(int, int)>();
		int from = 0;
		while (from < title.Length)
		{
			int hit = title.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
			if (hit < 0)
			{
				break;
			}
			ranges.Add((hit, keyword.Length));
			from = hit + keyword.Length;
		}
		if (ranges.Count > 0)
		{
			return ranges;
		}

		string k = Normalize(keyword);
		if (k.Length == 0)
		{
			return ranges;
		}
		string t = Normalize(title, out int[] map);
		int normFrom = 0;
		while (normFrom + k.Length <= t.Length)
		{
			int hit = t.IndexOf(k, normFrom, StringComparison.Ordinal);
			if (hit < 0)
			{
				break;
			}
			int start = map[hit];
			int end = map[hit + k.Length - 1] + 1;
			ranges.Add((start, end - start));
			normFrom = hit + k.Length;
		}
		return ranges;
	}

	/// <summary>归一化：小写 + 去标点空白 + 繁转简 + 日文新字体折叠。用于比较，不用于显示。</summary>
	public static string Normalize(string? text) => Normalize(text, out _);

	private static string Normalize(string? text, out int[] map)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			map = Array.Empty<int>();
			return "";
		}
		var sb = new StringBuilder(text.Length);
		var idx = new List<int>(text.Length);
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
			{
				// 表情之类：两个字符合并算一个位置，不然高亮可能把代理对劈开。
				sb.Append(c).Append(text[i + 1]);
				idx.Add(i);
				idx.Add(i);
				i++;
				continue;
			}
			if (NoiseChars.IndexOf(c) >= 0)
			{
				continue;
			}
			c = LowerAscii(c);
			c = ChineseConvert.NormalizeChar(c);
			sb.Append(c);
			idx.Add(i);
		}
		map = idx.ToArray();
		return sb.ToString();
	}

	/// <summary>只对 ASCII 字母小写。
	/// 不用 ToLowerInvariant(char)：少数字符会小写成两个字符（比如 U+0130），
	/// 那会让归一化后的长度和原文对不上，反查下标就全乱了。</summary>
	private static char LowerAscii(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

	/// <summary>包含关系打分：完全相等 > 前缀命中 > 中间命中，
	/// 同样命中时位置越靠前、标题越短分越高。</summary>
	private static double ContainmentScore(string keyword, string text)
	{
		if (keyword.Length == 0 || text.Length == 0)
		{
			return 0;
		}
		if (text == keyword)
		{
			return 1.0;
		}
		if (text.StartsWith(keyword, StringComparison.Ordinal))
		{
			return 0.92 - LengthPenalty(keyword.Length, text.Length);
		}
		int at = text.IndexOf(keyword, StringComparison.Ordinal);
		if (at < 0)
		{
			return 0;
		}
		double positional = 0.80 * (1.0 - (double)at / text.Length);
		return positional - LengthPenalty(keyword.Length, text.Length);
	}

	/// <summary>多出来的字越多，扣得越多（最多扣 0.15）。
	/// 「云鹤」应该排在「雲鶴サクリファイス (feat. nakotanmaru)」前面，
	/// 但两者的包含命中已经很接近了，这里只做轻微的偏好。</summary>
	private static double LengthPenalty(int keywordLength, int textLength)
	{
		double extra = textLength - keywordLength;
		return extra <= 0 ? 0 : Math.Min(0.15, extra / (double)keywordLength * 0.15);
	}

	/// <summary>Dice 系数。<paramref name="bigrams"/> 为 true 取二元组，否则取单字。
	///
	/// 两个都要算：二元组对「差得比较远」判得准，但两个字的关键词只有一个二元组
	/// （「云鹤」→ 一个「云鹤」），差一个字就变成 0 分，看着像完全无关。
	/// 单字那套正好补上这个洞（「云鹤」vs「云海」能拿到一半）。</summary>
	private static double Dice(string a, string b, bool bigrams)
	{
		int aLen = bigrams ? a.Length - 1 : a.Length;
		int bLen = bigrams ? b.Length - 1 : b.Length;
		if (aLen <= 0 || bLen <= 0)
		{
			return a == b ? 1 : 0;
		}
		int span = bigrams ? 2 : 1;
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		for (int i = 0; i < aLen; i++)
		{
			string g = a.Substring(i, span);
			counts[g] = counts.TryGetValue(g, out int n) ? n + 1 : 1;
		}
		int hit = 0;
		for (int i = 0; i < bLen; i++)
		{
			string g = b.Substring(i, span);
			if (counts.TryGetValue(g, out int n) && n > 0)
			{
				counts[g] = n - 1;
				hit++;
			}
		}
		return 2.0 * hit / (aLen + bLen);
	}
}