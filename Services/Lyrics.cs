using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MusicBox.Settings;

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

	/// <summary>匹配到这份歌词时实际用的歌名。供界面显示（沉浸页标题只显示
	/// 匹配到歌词的音乐名），由 <c>LyricFollowService</c> 在成功匹配后写入；
	/// 没有匹配信息（比如直接从平台 ID 拿到的）时为 null。</summary>
	public string? DisplayTitle;

	/// <summary>匹配到这份歌词时实际用的歌手；本地文件常为 null。</summary>
	public string? DisplayArtist;

	/// <summary>这份歌词是从哪个平台取到的。底栏封面方块的左半圈按它上色：
	/// 网易云红、QQ 绿、其它来源（LRCLIB / 酷狗 / B 站）灰。
	/// 由 <c>LyricFollowService</c> 在**拿到歌词的那一刻**就写进来，
	/// 界面只读它，不用去猜「到底哪个源出的结果」。</summary>
	public MatchSource LyricSource;

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

	/// <summary>这份歌词自己的时间轴终点（毫秒）：最后一行唱完的位置。
	///
	/// 没有歌曲元数据时长时，用它反推「这份歌词是按多长的音频写的」。
	/// 最后一行带字级时间轴时取最后一个字的结束时间，否则取行尾。</summary>
	public long TimelineEndMs { get; private set; }

	/// <summary>歌词时间轴相对音频时间轴的缩放比例，1.0 = 两者一致。</summary>
	public double TimeScale { get; private set; } = 1.0;

	/// <summary>歌词零点相对音频零点的偏移（毫秒），默认 0。
	/// 现在只用来兜住「歌词整体晚一点/早一点开唱」的情况。</summary>
	public long TimeOffsetMs { get; private set; }

	/// <summary>时间轴有没有被换算过。没换算时映射是恒等映射，可以直接跳过。</summary>
	public bool HasTimeScale => Math.Abs(TimeScale - 1.0) > 0.0005 || TimeOffsetMs != 0;

	/// <summary>把音频播放位置换算成这份歌词的时间轴位置。
	///
	/// 音频和歌词不是同一个长度时必须过这一层：歌词时间戳是按「歌词那份音频」
	/// 的时间轴记的（B 站下的版本、别的版本、现场版，母带长度都可能不一样），
	/// 直接拿播放位置去比，时间差会随进度越滚越大——开头还对，走到一半就整屏错开。</summary>
	public long MapPosition(long posMs)
	{
		if (!HasTimeScale || posMs < 0)
		{
			return posMs;
		}
		double mapped = (posMs - TimeOffsetMs) * TimeScale;
		return mapped <= 0 ? 0 : (long)Math.Round(mapped);
	}

	/// <summary>允许的缩放上下限。
	///
	/// 差一两秒属于元数据误差，不该动时间轴；差得离谱（比如拿到的是另一首）
	/// 也不该把整条轴拉成两倍，那只会把「对不上」换成「更对不上」。</summary>
	private const double MinScale = 0.75;

	private const double MaxScale = 1.25;

	/// <summary>两者差不到这么多毫秒就当没差别。</summary>
	private const long MinDiffMs = 3000;

	/// <summary>按实际音频时长把歌词时间轴整体拉伸/压缩，返回是否真的改了。
	///
	/// <paramref name="expectedDurationMs"/> 是「这份歌词对应的那版音频有多长」
	/// （优先用歌曲元数据时长，没有就退回 <see cref="TimelineEndMs"/>），
	/// <paramref name="actualDurationMs"/> 是现在真在播的解码时长。</summary>
	public bool ApplyDurationScale(long actualDurationMs, long expectedDurationMs)
	{
		ResetTimeScale();
		if (actualDurationMs <= 0 || expectedDurationMs <= 0)
		{
			return false;
		}
		if (Math.Abs(expectedDurationMs - actualDurationMs) < MinDiffMs)
		{
			return false;
		}
		double scale = Math.Clamp(expectedDurationMs / (double)actualDurationMs, MinScale, MaxScale);
		if (Math.Abs(scale - 1.0) < 0.02)
		{
			return false;
		}
		TimeScale = scale;
		return true;
	}

	/// <summary>撤掉换算，回到「歌词时间戳就是音频时间戳」。</summary>
	public void ResetTimeScale()
	{
		TimeScale = 1.0;
		TimeOffsetMs = 0;
	}

	/// <summary>第一句真正开唱的时间；没有可显示的行时为 -1。
	///
	/// 这就是「唱词前摇」的判定依据。绝大多数抒情歌开头有一段鼓/贝斯/前奏，
	/// 歌词表在这段时间里还没有任何一行可显示；而旧的 <see cref="IndexAt"/>
	/// 是纯二分找「最后一条 TimeMs ≤ posMs 的行」，在第一条时间戳之前
	/// 找不到任何行（found 保持 -1）恰好是对的，但在「第一条时间戳就是 0
	/// 或很小」的文件上，第一句连同它的译文会在歌还没唱到的时候
	/// 就整条亮出来（实测就是C U Again 报「还没开始唱就出了第一句翻译」）。
	/// 所以这里不取「第一条行的 TimeMs」，而是取「第一条有文字的行」，
	/// 并且跳过开头的空行/纯标记行 —— 前摇里那些 `[00:00.00]♪` 之类的行
	/// 不该把「开始唱」提前到 0。</summary>
	public long FirstSingingMs { get; private set; } = -1;

	/// <summary>播放位置是不是还落在前摇里（早于第一句开唱）。
	/// 落在这段区间里就一行都不显示——这和「这一句唱完了」是两回事，
	/// 都用 IndexAt 返回 -1 表示，但调用方可以用这个方法区分
	/// 「安静的前奏」和「歌词走完了」，好显示不同的提示。
	/// 参数是<see cref="MapPosition"/>换算过的歌词时间轴位置。</summary>
	public bool IsBeforeFirstSinging(long posMs)
	{
		return FirstSingingMs >= 0 && posMs < FirstSingingMs;
	}

	/// <summary>取当前该显示的行号。二分查找，位置往前/往后拖进度条都很快。
	/// <paramref name="posMs"/> 是播放器的原始位置，内部会换算到歌词时间轴。</summary>
	public int IndexAt(long posMs)
	{
		if (Lines.Count == 0)
		{
			return -1;
		}
		// 有过缩放/偏移时先把播放位置换算成歌词时间轴再比，
		// 否则时间差会随进度越滚越大。
		long pos = MapPosition(posMs);
		// 前摇：一行都不显示。
		//
		// 不这么做的话，第一条时间戳靠前的歌会在鼓点里就把第一句
		// 连同译文打出来，看着像「字幕提前了半首歌」。
		// 顺带也修了 C U Again 那个具体现象。
		if (IsBeforeFirstSinging(pos))
		{
			return -1;
		}
		int lo = 0;
		int hi = Lines.Count - 1;
		int found = -1;
		while (lo <= hi)
		{
			int mid = (lo + hi) / 2;
			if (Lines[mid].TimeMs <= pos)
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

	/// <summary>这一行唱到百分之多少了（0~1）。没字级时间轴就按整行时长平均算。
	///
	/// 有字级时间轴时也按「整行的时间」线性算，而不是逐字跳：
	/// 逐字算出来的是「当前这个字空档里走了多少」，一到字的边界就归零重来，
	/// 擦除矩形于是「涨到头 → 缩回去 → 再涨」，一格一跳。
	/// 那正是「动画不够平滑、看着有阶段被跳过」的来源：
	/// 唱到一半突然整句都亮了，下一个字又整句都暗了。
	/// 这里保留字级时间轴只有一处用途：拿最后一个字的结束时间当行尾，
	/// 不然最后一个字唱完了进度还在往上涨，看着像卡住。</summary>
	public double ProgressOf(int index, long posMs)
	{
		if (index < 0 || index >= Lines.Count)
		{
			return 0;
		}
		// 和 IndexAt 一样先换算到歌词时间轴，否则擦除进度会比唱词快/慢。
		long pos = MapPosition(posMs);
		LyricLine line = Lines[index];
		long span = line.SpanMs;
		if (span <= 0)
		{
			return 0;
		}
		// 颜色渲染（卡拉OK擦除）的速度窗口，由设置页滑块决定：颜色最多用
		// 这么多毫秒扫完整句。正常句（整句时长没超过它）仍跟演唱匀速；
		// 只有长句/跨句空档太长时才提前刷满，把空档留到句尾让整句全亮。
		// 早先的「2 秒全量兜底」太激进，中速句刷得比唱还快，所以默认给
		// 3.6 秒——正常句退回复古匀速，只有明显超长的句子才加成。
		long cap = Math.Max(200, SettingsManager.Instance.LyricRevealMs);
		long window = Math.Min(span, cap);
		if (line.CharStarts == null || line.CharStarts.Length == 0)
		{
			return Math.Clamp((pos - line.TimeMs) / (double)window, 0, 1);
		}
		// 有字级时间轴时，匀速的那段按「实际唱完的时间」收尾。
		//
		// 以前这里写的是 Math.Max(span, lastEnd - lineStart)：只有当最后一个字
		// 唱得比行尾还晚时才算数。大多数歌词不是这样——最后一个字的结束时间
		// 远早于这一行的 span（后面还有拖长的尾音/气口）。于是整行用 1/span
		// 这个偏慢的匀速去推，进度条到最后一个字的时候还差一截，然后 posMs
		// 一过 lastEnd 直接 return 1，「唰」一下整句全亮——这就是「还没渲染完
		// 突然填满」。
		//
		// 所以这里再算一次：匀速不够就额外补一段速度，把窗口收到「实际唱完」
		// 那一刻，让进度刚好在那里走到 100%，而不是靠跳变走到 100%。
		// 窗口同时被上面的模式上限兜底：字级时间轴给得太长也一样先刷满。
		long lineStart = line.TimeMs;
		long lastEnd = line.CharStarts[line.CharStarts.Length - 1] + line.CharDurs![line.CharDurs.Length - 1];
		// 收尾时间至少要比行首晚一点，也不比行尾更晚——
		// 万一字级时间轴本身是坏的（最后一个字时间 0），
		// 不能让它把整句压成 0 长度导致除零或者一瞬间满格。
		long spanChars = Math.Clamp(lastEnd - lineStart, 1, window);
		if (pos <= lineStart)
		{
			return 0;
		}
		if (pos >= lineStart + spanChars)
		{
			return 1;
		}
		return Math.Clamp((pos - lineStart) / (double)spanChars, 0, 1);
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

		b.RebuildFirstSinging();
		b.RebuildTimelineEnd();
		return b;
	}

	/// <summary>算出这份歌词的时间轴终点。见 <see cref="TimelineEndMs"/> 的说明。
	///
	/// 倒着找最后一条「有字级时间轴或至少不是空行」的行：文件末尾常有
	/// `[duration]` 这种纯标记行和空白行，拿它们当终点会算出一个虚高的时长，
	/// 后面按它换算时间轴就会把整首歌的歌词往前提。</summary>
	public void RebuildTimelineEnd()
	{
		TimelineEndMs = 0;
		for (int i = Lines.Count - 1; i >= 0; i--)
		{
			LyricLine line = Lines[i];
			if (line.IsBlank)
			{
				continue;
			}
			long end = line.TimeMs + Math.Clamp(line.SpanMs, 500, 8000);
			if (line.CharStarts is { Length: > 0 } starts && line.CharDurs is { Length: > 0 } durs)
			{
				long charEnd = (long)starts[starts.Length - 1] + durs[durs.Length - 1];
				if (charEnd > line.TimeMs)
				{
					end = charEnd;
				}
			}
			TimelineEndMs = end;
			break;
		}
	}

	/// <summary>算出第一句真正开唱的时间。见 <see cref="FirstSingingMs"/> 的说明。
	///
	/// 只在「有文字的行」里找最早的那一条，并且跳过纯器乐标记行：
	/// 前奏里 LRC 经常会写 `[00:00.00]♪` 或者 `[00:00.00]---`，
	/// 那种行不算「开始唱」，否则前摇判定会被它拉到 0，等于没做。
	///
	/// 只算一次、之后只读，所以这里顺手在 Count 上做缓存：
	/// IndexAt 每秒被调用二十多次，每次重扫一遍全部行是白烧 CPU。</summary>
	public void RebuildFirstSinging()
	{
		long first = -1;
		for (int i = 0; i < Lines.Count; i++)
		{
			if (IsSingingLine(Lines[i].Text))
			{
				first = Lines[i].TimeMs;
				break;
			}
		}
		FirstSingingMs = first;
	}

	/// <summary>这一行算不算「有人在唱」。空行和纯器乐标记行都不算。
	///
	/// 判据是「去掉空白和标点之后还剩不剩下东西」：
	/// ♪、♫、（副歌）、—— 这类行去掉就空了，说明这段是器乐/结构标记，
	/// 不该被当成「开口唱了」。</summary>
	private static bool IsSingingLine(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c) || IsCjk(c))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsCjk(char c)
	{
		return (c >= 0x4E00 && c <= 0x9FFF)   // 基本汉字
			|| (c >= 0x3400 && c <= 0x4DBF)   // 扩展 A
			|| (c >= 0x3040 && c <= 0x30FF)   // 日文假名
			|| (c >= 0xAC00 && c <= 0xD7AF)   // 韩文音节
			|| (c >= 0xF900 && c <= 0xFAFF);  // 兼容汉字
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
