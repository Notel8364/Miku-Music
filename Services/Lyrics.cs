using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBox.Services;

/// <summary>一行歌词。
/// <see cref="TimeMs"/> 是这一行开始的时间；<see cref="CharStarts"/> /
/// <see cref="CharDurs"/> 是逐字时间轴，没有时为 null，由
/// <see cref="InterpolateChars"/> 按行时长平均铺开兜底。</summary>
public sealed class LyricLine
{
	public long TimeMs;
	public string Text = "";
	public string? Translation;

	/// <summary>逐字起始时间（毫秒），和 <see cref="Text"/> 一一对应。</summary>
	public int[]? CharStarts;

	/// <summary>逐字时长（毫秒），和 <see cref="Text"/> 一一对应。</summary>
	public int[]? CharDurs;

	/// <summary>这一行到下一行之间的可用时长（毫秒），用来铺字和做「上一句已唱完」的判断。</summary>
	public long SpanMs = 4000;

	public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>一首歌的字幕数据：逐行时间轴 + 翻译 + 逐字时间轴。</summary>
public sealed class LyricBundle
{
	public List<LyricLine> Lines = new();

	public int Count => Lines.Count;

	/// <summary>逐字时间轴是否可用（拿不到 YRC 时靠插值也能出效果，所以一直可用）。</summary>
	public bool HasCharTiming
	{
		get
		{
			foreach (LyricLine l in Lines)
			{
				if (l.CharStarts != null)
				{
					return true;
				}
			}
			return false;
		}
	}

	/// <summary>取当前该显示的行号。二分查找，位置往前/往后拖进度条都很快。</summary>
	public int IndexAt(long posMs)
	{
		if (Lines.Count == 0)
		{
			return -1;
		}
		int lo = 0;
		int hi = Lines.Count - 1;
		int found = -1;
		while (lo <= hi)
		{
			int mid = (lo + hi) / 2;
			if (Lines[mid].TimeMs <= posMs)
			{
				found = mid;
				lo = mid + 1;
			}
			else
			{
				hi = mid - 1;
			}
		}
		return found;
	}

	/// <summary>这一行唱到百分之多少了（0~1）。没字级时间轴就按整行时长平均算。</summary>
	public double ProgressOf(int index, long posMs)
	{
		if (index < 0 || index >= Lines.Count)
		{
			return 0;
		}
		LyricLine line = Lines[index];
		long span = line.SpanMs;
		if (span <= 0)
		{
			return 0;
		}
		if (line.CharStarts == null || line.CharStarts.Length == 0)
		{
			return Math.Clamp((posMs - line.TimeMs) / (double)span, 0, 1);
		}
		// 有字级时间轴：按最后一个字的结束时间收尾，
		// 不然行会在最后一个字唱完后还一直往上涨，看着像卡住。
		long lastEnd = line.CharStarts[line.CharStarts.Length - 1] + line.CharDurs![line.CharDurs.Length - 1];
		if (posMs >= lastEnd)
		{
			return 1;
		}
		// 落在两个字的交界处就取后一个字已完成的程度，位置很稳定、不会跳。
		for (int i = 0; i < line.CharStarts.Length; i++)
		{
			long s = line.CharStarts[i];
			if (posMs < s)
			{
				long prevEnd = i == 0 ? line.TimeMs : line.CharStarts[i - 1] + line.CharDurs![i - 1];
				if (posMs <= prevEnd)
				{
					return 1.0;
				}
				long d = s - prevEnd;
				return d <= 0 ? 1.0 : Math.Clamp((posMs - prevEnd) / (double)d, 0, 1);
			}
		}
		return 1.0;
	}

	// ==================== 解析 ====================

	private static readonly Regex LrcTime = new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
	// YRC 一行形如：[start,dur](字起始,字时长,0)字(字起始,字时长,0)字
	// 注意时间在括号里、文字在括号后面，所以要「先吃括号再吃后面的字」。
	// 反过来写（先字后括号）会让每个字拿到下一个字的时间，整行错位一格。
	private static readonly Regex YrcLine = new Regex(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
	private static readonly Regex YrcWord = new Regex(@"\((\d+),(\d+)(?:,\d+)?\)([^\(\[]*)", RegexOptions.Compiled);

	public static LyricBundle Parse(string lrc, string? tlyric, string? yrc)
	{
		var b = new LyricBundle();
		var translate = ParseTimeList(tlyric);

		// LRC 单独解一份出来备用。翻译（tlyric）的时间轴是跟着 LRC 走的，
		// 而 YRC 的行首时间是「实际开唱那一刻」，两者经常差半秒以上。
		// 所以先在 LRC 这一侧把翻译配好，再搬到实际显示的行上。
		var lrcLines = ParseLrcList(lrc);

		bool usedYrc = false;
		if (!string.IsNullOrWhiteSpace(yrc))
		{
			ParseYrcInto(b, yrc);
			usedYrc = b.Lines.Count > 0;
		}
		if (b.Lines.Count == 0)
		{
			// 没 YRC 就退回 LRC。这也是 yrc=0 的歌翻译一直很准的原因：
			// 显示行和 LRC 行是同一批，索引天然对得上。
			foreach (var (ms, text) in lrcLines)
			{
				b.Lines.Add(new LyricLine { TimeMs = ms, Text = text });
			}
		}

		if (translate.Count > 0)
		{
			ApplyTranslations(b, lrcLines, translate, usedYrc);
		}

		// 补 SpanMs，并给没有字级时间轴的行铺一份插值
		for (int i = 0; i < b.Lines.Count; i++)
		{
			LyricLine cur = b.Lines[i];
			long next = i + 1 < b.Lines.Count ? b.Lines[i + 1].TimeMs : cur.TimeMs + 6000;
			if (next <= cur.TimeMs)
			{
				next = cur.TimeMs + 1500;
			}
			cur.SpanMs = next - cur.TimeMs;
			if (cur.CharStarts == null && !cur.IsBlank)
			{
				InterpolateChars(cur);
			}
		}
		return b;
	}

	/// <summary>把翻译贴到实际显示的行上。
	///
	/// 两步都是「一对一」对齐，不做逐行取最近：
	///
	/// 第一步 LRC ↔ tlyric。两者共用一条时间轴，行数相同就按下标，
	/// 不相同就按时间配（见 <see cref="AlignByTime"/>）。
	///
	/// 第二步把结果搬到显示行：
	/// - 不用 YRC 时，显示行就是 LRC 行，按下标直接取；
	/// - 用了 YRC 时，两边行数通常一样，按下标取最准；不一样就按时间配。
	///
	/// 之所以不用「每行各自取时间最近的一条」：那种写法不保证单调。
	/// 举实际的例子——某行 LRC 在 20.00s、tlyric 只有 19.60s 和 20.70s 两条，
	/// 取最近会判给 20.70s（差 0.70s）而不是 19.60s（差 0.40s）的情况在
	/// 时间戳密度不均时很常见。逐行独立取最近还允许「上一行配走下一行的翻译」，
	/// 于是同一行会同时被两条 LRC 行抢走，读出来就是「这句的翻译串到下一句」——
	/// 也就是「翻译词跟下一句的歌词混一起」。单调一对一从结构上堵掉这个。</summary>
	private static void ApplyTranslations(LyricBundle b,
		List<(long Ms, string Text)> lrcLines, List<(long Ms, string Text)> translate, bool usedYrc)
	{
		if (lrcLines.Count == 0) return;

		// 先算出「每条 LRC 行对应的翻译」
		var perLrc = new string?[lrcLines.Count];
		if (lrcLines.Count == translate.Count)
		{
			// 行数完全一致：按下标配。时间戳可能有几十毫秒偏差，
			// 但同一条时间轴上第 N 行就是第 N 行，不会串。
			for (int i = 0; i < lrcLines.Count; i++)
			{
				string t = translate[i].Text;
				perLrc[i] = string.IsNullOrWhiteSpace(t) ? null : t;
			}
		}
		else
		{
			AlignByTime(lrcLines, translate, perLrc, TranslationToleranceMs);
		}

		// 显示行 == LRC 行（没用 YRC，或本来就是拿 LRC 填的）
		if (!usedYrc || b.Lines.Count == lrcLines.Count)
		{
			for (int i = 0; i < b.Lines.Count && i < perLrc.Length; i++)
			{
				if (!b.Lines[i].IsBlank) b.Lines[i].Translation = perLrc[i];
			}
			return;
		}

		// 行数对不上：把每条显示行配到一条 LRC 行上。
		// 同样走单调一对一——YRC 的行首时间偏得厉害时，逐行取最近
		// 会把整段翻译串到隔壁行去。
		var display = new List<(long Ms, string Text)>(b.Lines.Count);
		foreach (LyricLine l in b.Lines) display.Add((l.TimeMs, l.Text));
		var sel = new string?[b.Lines.Count];
		AlignByTime(lrcLines, display, sel, YrcAlignToleranceMs);
		for (int i = 0; i < b.Lines.Count; i++)
		{
			if (!b.Lines[i].IsBlank) b.Lines[i].Translation = sel[i];
		}
	}

	/// <summary>LRC 与 tlyric 同一时间轴，配对容差（毫秒）。</summary>
	private const long TranslationToleranceMs = 400;

	/// <summary>YRC 与 LRC 是两条不同的轴（前者是实际开唱时刻），
	/// 偏移天然大得多，所以容差要放宽。但仍然是有限容差：
	/// 超出就留空，不拿隔壁行凑数——错译比没译更糟。</summary>
	private const long YrcAlignToleranceMs = 1500;

	/// <summary>两条时间轴之间做单调一对一配对，结果写进 <paramref name="result"/>。
	///
	/// 用双指针同步前进：两边都已按时间升序，任何时刻两个指针只增不减，
	/// 所以配出来的关系天然单调——不会出现「A 行配到的译文排在 B 行之后」，
	/// 也就不会出现同一行被两行抢走的情况。
	///
	/// 差值超出容差时，推进「时间落在后面」的那一侧，让两边重新对齐；
	/// 被跳过的那一行留空。宁可少显示一句翻译，也不显示错的那句。
	///
	/// 判断哪一侧落后，看 diff = src[j] - dst[i]：
	/// - diff 为负且绝对值超容差：src[j] 还停在更早的时间，是 src 缺行 → 推 src；
	/// - diff 为正且绝对值超容差：dst[i] 还停在更早的时间，是 dst 缺行 → 推 dst。</summary>
	private static void AlignByTime(List<(long Ms, string Text)> dst,
		List<(long Ms, string Text)> src, string?[] result, long toleranceMs)
	{
		int i = 0, j = 0;
		while (i < dst.Count && j < src.Count)
		{
			long diff = src[j].Ms - dst[i].Ms;
			if (diff < -toleranceMs)
			{
				// src 落后：src 少了这一行，跳过 src 继续等
				j++;
			}
			else if (diff > toleranceMs)
			{
				// dst 落后：dst 少了这一行，跳过 dst
				i++;
			}
			else
			{
				string t = src[j].Text;
				result[i] = string.IsNullOrWhiteSpace(t) ? null : t;
				i++;
				j++;
			}
		}
	}

	/// <summary>行级时间轴：[mm:ss.xx]歌词。同一行可能有多个时间标签（副歌重复段）。
	/// 按时间升序返回。等长时靠下标配对翻译，比按时间猜可靠得多。</summary>
	private static List<(long Ms, string Text)> ParseLrcList(string? lrc)
	{
		// 注意要写成 (long Ms, string Text)：写成 (long, string) 的话
		// var 推断出来的 List 元素是匿名元组，名字丢了，
		// 后面 list.Sort((x,y)=>x.Ms...) 直接编译不过。
		var list = new List<(long Ms, string Text)>();
		if (string.IsNullOrWhiteSpace(lrc))
		{
			return list;
		}
		foreach (string raw in lrc.Split('\n'))
		{
			string text = raw.Trim('\r', ' ', '\t');
			if (text.Length == 0)
			{
				continue;
			}
			MatchCollection times = LrcTime.Matches(text);
			if (times.Count == 0)
			{
				// [ti:xx] [ar:xx] [al:xx] [by:xx] 这些是文件头标签，直接丢
				continue;
			}
			// 时间标签后面才是正文；正文里可能还含 [xx]，按第一个标签结束处切
			int start = times[times.Count - 1].Index + times[times.Count - 1].Length;
			string body = text.Substring(start).Trim();
			if (body.Length == 0)
			{
				continue;
			}
			foreach (Match t in times)
			{
				list.Add((ToMs(t.Groups[1].Value, t.Groups[2].Value, t.Groups[3].Value), body));
			}
		}
		list.Sort((x, y) => x.Ms.CompareTo(y.Ms));
		return list;
	}

	/// <summary>逐字时间轴（YRC）。网易云有这首歌的「逐字歌词」时才有。
	/// 格式：[行起始毫秒,行时长毫秒]词(字起始毫秒,字时长毫秒)词(...)</summary>
	private static void ParseYrcInto(LyricBundle b, string yrc)
	{
		var order = new List<LyricLine>();
		foreach (string raw in yrc.Split('\n'))
		{
			string text = raw.Trim('\r', ' ', '\t');
			if (text.Length == 0)
			{
				continue;
			}
			Match head = YrcLine.Match(text);
			if (!head.Success)
			{
				continue;
			}
			long lineStart = SafeLong(head.Groups[1].Value);
			var chars = new List<char>();
			var starts = new List<int>();
			var durs = new List<int>();
			MatchCollection words = YrcWord.Matches(head.Groups[3].Value);
			foreach (Match w in words)
			{
				string chunk = w.Groups[3].Value;
				if (chunk.Length == 0)
				{
					continue;
				}
				int st = (int)SafeLong(w.Groups[1].Value);
				int du = (int)SafeLong(w.Groups[2].Value);
				// 一个词组（可能好几个字）共享一个时间段。
				// 整组都从 st 开始、整组都占 du 的话，长词组会整块一起亮，没有「一个字一个字」的感觉；
				// 所以把这一段时间平均切给组里每个字。
				int n = chunk.Length;
				int unit = n > 0 ? Math.Max(1, du / n) : du;
				for (int i = 0; i < n; i++)
				{
					chars.Add(chunk[i]);
					starts.Add(st + unit * i);
					int d = i == n - 1 ? Math.Max(1, du - unit * i) : unit;
					durs.Add(d);
				}
			}
			string body = new string(chars.ToArray()).Trim();
			// Trim 之后长度对不上了，char 数组要跟着从两头掐掉，
			// 否则行首尾的字级时间会整体错一格。
			TrimCharTimings(body, chars, starts, durs);
			order.Add(new LyricLine
			{
				TimeMs = lineStart,
				Text = body,
				CharStarts = starts.Count == body.Length && starts.Count > 0 ? starts.ToArray() : null,
				CharDurs = durs.Count == body.Length && durs.Count > 0 ? durs.ToArray() : null
			});
		}
		order.Sort((x, y) => x.TimeMs.CompareTo(y.TimeMs));
		b.Lines = order;
	}

	/// <summary>把字级时间数组掐到和正文一样长。正文被 Trim 过，
	/// 所以这里从两头按同样的空白数往里收。</summary>
	private static void TrimCharTimings(string text, List<char> chars, List<int> starts, List<int> durs)
	{
		// text 已经和这一行的文本 Trim 对齐过了，chars 还没。
		// 所以首尾的空白要从 chars 上摘掉，摘完才能和 text 一一对上。
		// 注意不能像原来那样「lead == 0 就直接返回」：只有尾部空格时
		// 三个数组会和 text 对不齐，字级数据就被整行丢掉、退回按行插值了。
		int lead = 0;
		while (lead < chars.Count && char.IsWhiteSpace(chars[lead]))
		{
			lead++;
		}
		int trail = chars.Count - (lead + text.Length);
		if (trail <= 0)
		{
			// 长度对不上，多半是这一行的字级数组本身缺项，
			// 硬凑反而会让时间轴指到错字上，交给上层退回按行插值。
			return;
		}
		chars.RemoveRange(lead, trail);
		int n = Math.Min(trail, Math.Max(0, starts.Count - lead));
		starts.RemoveRange(lead, n);
		int m = Math.Min(trail, Math.Max(0, durs.Count - lead));
		durs.RemoveRange(lead, m);
	}

	/// <summary>没有逐字歌词时，把一行的时间平均分给每个字。
	/// 这样照样有「一个字一个字亮起来」的效果，代价是节奏不如真逐字准。</summary>
	private static void InterpolateChars(LyricLine line)
	{
		int n = line.Text.Length;
		if (n == 0 || line.SpanMs <= 0)
		{
			return;
		}
		var starts = new int[n];
		var durs = new int[n];
		long span = line.SpanMs;
		for (int i = 0; i < n; i++)
		{
			double unit = span / (double)n;
			starts[i] = (int)Math.Round(line.TimeMs + unit * i);
			durs[i] = (int)Math.Round(unit);
		}
		line.CharStarts = starts;
		line.CharDurs = durs;
	}

	/// <summary>把翻译歌词解成按时间升序的 (时间, 文本) 列表。
	///
	/// 这里原来返回 Dictionary&lt;long,string&gt;，有两个毛病：
	/// 一是同一时间戳出现两次（重复副歌、多标签行）会后写覆盖先写，
	/// 直接丢一句；二是丢掉了原始顺序，而「LRC 和 tlyric 行数相同
	/// 就按第 N 行配第 N 行」才是最稳的配对方式，顺序没了就没法用。
	/// 所以改成 List，按文件原序 append，最后再按时间排一次。</summary>
	private static List<(long Ms, string Text)> ParseTimeList(string? tlyric)
	{
		var list = new List<(long Ms, string Text)>();
		if (string.IsNullOrWhiteSpace(tlyric))
		{
			return list;
		}
		foreach (string raw in tlyric.Split('\n'))
		{
			string text = raw.Trim('\r', ' ', '\t');
			if (text.Length == 0)
			{
				continue;
			}
			MatchCollection times = LrcTime.Matches(text);
			if (times.Count == 0)
			{
				continue;
			}
			int start = times[times.Count - 1].Index + times[times.Count - 1].Length;
			string body = text.Substring(start).Trim();
			if (body.Length == 0)
			{
				continue;
			}
			// 和 LRC 一样，一行可能带多个时间标签，全部都要收。
			// 原来只取 times[0]，多标签行会白丢一份翻译。
			foreach (Match t in times)
			{
				list.Add((ToMs(t.Groups[1].Value, t.Groups[2].Value, t.Groups[3].Value), body));
			}
		}
		list.Sort((x, y) => x.Ms.CompareTo(y.Ms));
		return list;
	}

	private static long ToMs(string mm, string ss, string? frac)
	{
		long m = SafeLong(mm);
		long s = SafeLong(ss);
		long f = 0;
		if (!string.IsNullOrEmpty(frac))
		{
			// 两位是厘秒、三位是毫秒
			f = frac.Length switch
			{
				1 => SafeLong(frac) * 100,
				2 => SafeLong(frac) * 10,
				_ => SafeLong(frac),
			};
		}
		return m * 60000 + s * 1000 + f;
	}

	private static long SafeLong(string s)
	{
		return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
	}
}
