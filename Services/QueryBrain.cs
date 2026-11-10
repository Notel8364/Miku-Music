using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MusicBox.Services;

/// <summary>一条「爱好 / 圈子」词条。敲进去的关键词往往只是个简称，
/// 用户真正想要的是它背后的那一整片音乐（敲「东方」要的是东方 Project
/// 的同人曲，不是恰好叫「东方」的那一首歌）。</summary>
public sealed class InterestTopic
{
	public string Id { get; init; } = "";

	/// <summary>界面上显示的名字。</summary>
	public string Name { get; init; } = "";

	/// <summary>能命中这个词条的输入。可以是简称、英文名、日文名。</summary>
	public string[] Keys { get; init; } = Array.Empty<string>();

	/// <summary>补全时要补上的那一截。敲「东方」补「Project」，敲「pho」补「nk」。
	/// 空串表示这条不参与补全（只参与排序加权）。</summary>
	public string Complete { get; init; } = "";

	/// <summary>true = 这一条是「直接推荐一首」而不是「把词补完」：
	/// 采纳时整句换成 <see cref="Complete"/>（english → So Far Away），
	/// 而不是拼在后面（那会变成「english So Far Away」，什么都搜不到）。</summary>
	public bool Replace { get; init; }

	/// <summary>拿去真搜的词。敲「东方」实际搜「东方Project」。
	/// 空串表示不改写搜索词，照用户敲的原样搜。</summary>
	public string SearchAs { get; init; } = "";

	/// <summary>相关性加权词：社团名 / 艺人 / 曲名关键词。
	/// 命中的结果会被排到前面（见 <see cref="QueryBrain.RelatedScore"/>）。</summary>
	public string[] Related { get; init; } = Array.Empty<string>();

	/// <summary>这个圈子里的代表曲目，同时用作「歌名补全」的候选。</summary>
	public string[] Samples { get; init; } = Array.Empty<string>();
}

/// <summary>一次补全的结果。</summary>
public sealed class Guess
{
	/// <summary>要补上的那一截（「project」）。</summary>
	public string Suffix { get; init; } = "";

	/// <summary>补全后的整词。</summary>
	public string Full { get; init; } = "";

	/// <summary>归一化出来的概率（0-1）：这份补全有多可信。</summary>
	public double Probability { get; init; }

	/// <summary>补全来源，界面上写着是「兴趣」还是「搜过」。</summary>
	public string Source { get; init; } = "";

	/// <summary>命中的爱好词条；按搜索历史补全时为 null。</summary>
	public InterestTopic? Topic { get; init; }
}

/// <summary>一句话算出「用户敲的这个大概属于哪个圈子」，顺便给出补全。
///
/// 三件事：
/// 1) <see cref="Complete"/> —— 输入框里的淡色补全（东方→project、pho→nk）；
/// 2) <see cref="Analyze"/> —— 概率最高的爱好词条，用来改写搜索词 + 结果排序；
/// 3) 记次数 —— 搜过/采纳过的词记下来，下次「So」能猜到「So Far Away」。
///
/// 概率不是拍脑袋的阈值，是把每个候选打分后 softmax 归一化：
/// 候选分 = 基础分（兴趣 &gt; 搜过 &gt; 代表曲）+ 打字进度（敲得越接近全词越可信）
///         + 历史次数（搜得越多越可信）。</summary>
public static class QueryBrain
{
	/// <summary>低于这个概率就不信：既不改写搜索词，也不重排结果。
	/// 免得敲个「e」就把人家想搜的英文歌全换成电音。</summary>
	public const double TrustThreshold = 0.30;

	private static readonly List<InterestTopic> Topics = BuildTopics();

	private static readonly object Sync = new();

	/// <summary>搜过/采纳过的词 → 次数。补全「So → So Far Away」靠它。</summary>
	private static Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>搜索结果里出现过的歌名 → 次数。补全「Bad Ap → Bad Apple!!」靠它：
	/// 用户敲的往往只是歌名的头几个字，真正想要的是结果里那一整条。
	/// 点开播过的记 3 分（见 NoteTitle 的 by），只是出现在列表里记 1 分。</summary>
	private static Dictionary<string, int> _titles = new(StringComparer.OrdinalIgnoreCase);

	private static bool _loaded;

	/// <summary>落盘的结构。counts 是搜过的词，titles 是结果里的歌名。</summary>
	private sealed class BrainStore
	{
		public Dictionary<string, int> Counts { get; set; } = new();
		public Dictionary<string, int> Titles { get; set; } = new();
	}

	private static readonly string StorePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "query_brain.json");

	private static InterestTopic? _lastTopic;
	private static double _lastProbability;

	private static List<InterestTopic> BuildTopics()
	{
		return new List<InterestTopic>
		{
			// ── 东方 Project ──
			// 敲「东方」要的是上海アリス幻樂団那一整个同人圈，不是叫「东方」的那一首。
			new InterestTopic
			{
				Id = "touhou",
				Name = "东方Project",
				Keys = new[] { "东方", "東方", "touhou", "东方project", "東方Project", "东方同人", "東方同人" },
				Complete = "Project",
				SearchAs = "东方Project",
				Related = new[]
				{
					"上海アリス幻樂団", "上海アリス", "ZUN", "IOSYS", "幽閉サテライト", "幽闭サテライト",
					"Alstroemeria Records", "Alstroemeria", "魂音泉", "豚乙女", "暁Records",
					"Foxtail-Grass Studio", "Foxtail-Grass", "EastNewSound", "森羅万象", "東京アクティブNEETs",
					"神乃木製作所", "まらしぃ", "Demetori", "彩音", "流派未階堂", "C-CLAYS", "ShibayanRecords",
					"東方紅魔郷", "東方妖々夢", "東方永夜抄", "東方花映塚", "東方風神録", "東方地霊殿",
					"東方星蓮船", "東方神霊廟", "東方輝針城", "東方紺珠伝", "東方天空璋", "東方鬼形獣",
					"東方虹龍洞", "東方獣王園", "紅魔郷", "妖々夢", "永夜抄", "幻想郷", "萃夢想",
					"亡き王女の為のセプテット", "恋色マスタースパーク", "感情の摩天楼", "ネクロファンタジア",
					"ナイト・オブ・ナイツ", "Bad Apple", "少女さとり", "月まで届け", "幽雅に咲かせ",
					"U.N.オーエンは彼女なのか", "大神神話伝", "虎柄の毘沙門天", "遠野幻想物語"
				},
				Samples = new[]
				{
					"Bad Apple!!", "ナイト・オブ・ナイツ", "恋色マスタースパーク",
					"亡き王女の為のセプテット", "感情の摩天楼 ~ Cosmic Mind"
				}
			},

			// ── Phonk ──
			new InterestTopic
			{
				Id = "phonk",
				Name = "Phonk",
				Keys = new[] { "phonk", "pho", "фонк", "drift phonk", "driftphonk", "巴西 phonk" },
				Complete = "nk",
				SearchAs = "phonk",
				Related = new[]
				{
					"Kordhell", "DVRST", "Ghostface Playa", "KUTE", "INTERWORLD", "MoonDeity", "Hensonn",
					"Dxrk", "Eternxlkz", "KSLV", "ONIMXRU", "SMITHMANE", "PLAYAMANE", "RAIZHELL",
					"MC ORSEN", "GRAVECHILL", "SCARLXRD", "Freddie Dredd", "Sxmpra", "DJ Yung Vamp",
					"Kaito Shoma", "MUPP", "Sadfriendd", " isolate.exe", "g3ox_em", "KXNVRA", "Phonk Killer",
					"Murder In My Mind", "Close Eyes", "METAMORPHOSIS", "NEON BLADE", "Sahara",
					"LIVE ANOTHER DAY", "LAND OF FIRE", "Why Not", "AVOID ME", "CADILLAC", "Twilight",
					"phonk", "drift", "slowed", "reverb"
				},
				Samples = new[]
				{
					"Murder In My Mind", "Close Eyes", "METAMORPHOSIS", "NEON BLADE", "Why Not"
				}
			},

			// ── 英文 / 欧美流行 ──
			new InterestTopic
			{
				Id = "english",
				Name = "英文歌",
				Keys = new[] { "english", "英文", "欧美", "欧美流行", "english song" },
				// 爱好补全：敲 english 直接补出那首最常听的（整句换掉，不是拼在后面）
				Complete = "So Far Away",
				Replace = true,
				SearchAs = "",
				Related = new[]
				{
					"Martin Garrix", "David Guetta", "Alan Walker", "Avicii", "Ed Sheeran", "Taylor Swift",
					"Adele", "Dua Lipa", "The Weeknd", "Billie Eilish", "Imagine Dragons", "Coldplay",
					"Maroon 5", "Sia", "Rihanna", "Bruno Mars", "Justin Bieber", "Katy Perry", "Shakira"
				},
				Samples = new[]
				{
					"So Far Away", "Faded", "Shape of You", "Blinding Lights", "Bad Guy"
				}
			},

			// ── VOCALOID / 初音 ──
			new InterestTopic
			{
				Id = "vocaloid",
				Name = "VOCALOID",
				Keys = new[] { "vocaloid", "初音", "初音ミク", "初音未来", "miku", "voca", "ボカロ" },
				Complete = "ミク",
				SearchAs = "初音ミク",
				Related = new[]
				{
					"初音ミク", "鏡音リン", "鏡音レン", "巡音ルカ", "MEIKO", "KAITO", "VOCALOID",
					"千本桜", "メルト", "ワールドイズマイン", "ロミオとシンデレラ", "ハジメテノオト",
					"deco*27", "ryo", "supercell", "livetune", "kz", "畑亜貴", "cosMo", "ポリスピカデリー",
					"Miku", "Hatsune", "ボカロ"
				},
				Samples = new[] { "千本桜", "メルト", "ワールドイズマイン", "ロミオとシンデレラ" }
			},

			// ── 中文 VOCALOID ──
			new InterestTopic
			{
				Id = "vsinger",
				Name = "中文VOCALOID",
				Keys = new[] { "洛天依", "天依", "vsinger", "中文vocaloid", "中文虚拟歌手" },
				Complete = "天依",
				SearchAs = "洛天依",
				Related = new[]
				{
					"洛天依", "言和", "乐正绫", "乐正龙牙", "心华", "徵羽摩柯", "墨清弦", "Vsinger",
					"Vsinger", "中文VOCALOID"
				},
				Samples = new[] { "普通DISCO", "权御天下", "千年食谱颂" }
			},

			// ── 动漫歌曲 ──
			new InterestTopic
			{
				Id = "anisong",
				Name = "动漫歌曲",
				Keys = new[] { "anisong", "动漫", "動漫", "二次元", "动画歌曲", "番剧" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"LiSA", "YOASOBI", "Eve", "Official髭男dism", "King Gnu", "ClariS", "Aimer", "Uru",
					"紅蓮華", "残酷な天使のテーゼ", "鬼滅の刃", "呪術廻戦", "VIVID VICE", "一途",
					"夜に駆ける", "群青", "Sparkle", "打上花火", "前前前世", "オープニング", "アニソン"
				},
				Samples = new[] { "紅蓮華", "夜に駆ける", "残酷な天使のテーゼ", "前前前世" }
			},

			// ── 蒸汽波 ──
			new InterestTopic
			{
				Id = "vaporwave",
				Name = "蒸汽波",
				Keys = new[] { "vaporwave", "蒸汽波", "蒸気波", "vapo" },
				Complete = "rwave",
				SearchAs = "vaporwave",
				Related = new[]
				{
					"Macintosh Plus", "リサフランク420", "Blank Banshee", "FLORAL SHOPPE", "vaporwave",
					"蒸汽波", "future funk", "Yung Bae", " Saint Pepsi"
				},
				Samples = new[] { "リサフランク420 / 現代のコンピュー", "Enjoy Yourself" }
			},

			// ── City Pop ──
			new InterestTopic
			{
				Id = "citypop",
				Name = "City Pop",
				Keys = new[] { "citypop", "city pop", "シティーポップ", "城市流行", "シティポップ" },
				Complete = "pop",
				SearchAs = "city pop",
				Related = new[]
				{
					"竹内まりや", "松任谷由実", "山下達郎", "大貫妙子", "プラスティック・ラブ", "Plastic Love",
					"真夜中のドア", "週末の恋人達", "杏里", "稲垣潤一", "シティーポップ", "city pop"
				},
				Samples = new[] { "プラスティック・ラブ", "真夜中のドア ~ Stay With Me" }
			},

			// ── 古风 / 国风 ──
			new InterestTopic
			{
				Id = "gufeng",
				Name = "古风",
				Keys = new[] { "古风", "古風", "国风", "國風", "gufeng" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"河图", "银临", "音频怪物", "小曲儿", "双笙", "不才", "玄觞", "许嵩", "周深",
					"贰婶", "排骨教主", "奇然", "萧忆情", "古风"
				},
				Samples = new[] { "牵丝戏", "锦鲤抄", "不老梦", "棠梨煎雪" }
			},

			// ── 说唱 ──
			new InterestTopic
			{
				Id = "hiphop",
				Name = "说唱",
				Keys = new[] { "rap", "说唱", "說唱", "hiphop", "hip hop", "嘻哈" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"Eminem", "Kendrick Lamar", "Travis Scott", "Drake", "J. Cole", "Kanye West",
					"GAI", "马思唯", "Higher Brothers", "艾热", "王以太", "幼稚园杀手", "hiphop", "rap"
				},
				Samples = new[] { "Godzilla", "HUMBLE.", "SICKO MODE" }
			},

			// ── 电子 / EDM ──
			new InterestTopic
			{
				Id = "edm",
				Name = "电子",
				Keys = new[] { "edm", "电音", "电子", "電子", "electronic", "dubstep" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"Alan Walker", "Martin Garrix", "Avicii", "David Guetta", "Skrillex", "Marshmello",
					"The Chainsmokers", "Zedd", "Calvin Harris", "Tiësto", "Hardwell", "Faded", "So Far Away",
					"edm", "electronic"
				},
				Samples = new[] { "Faded", "So Far Away", "Wake Me Up", "Titanium" }
			},

			// ── 古典 / 钢琴 ──
			new InterestTopic
			{
				Id = "classical",
				Name = "古典",
				Keys = new[] { "classical", "古典", "钢琴", "piano", "クラシック" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"Mozart", "Beethoven", "Chopin", "Bach", "Debussy", "Liszt", "久石让", "久石譲",
					"宫崎骏", "Ryuichi Sakamoto", "坂本龍一", "肖邦", "莫扎特", "贝多芬"
				},
				Samples = new[] { "月光", "Canon in D", "Gymnopédies" }
			},

			// ── 爵士 ──
			new InterestTopic
			{
				Id = "jazz",
				Name = "爵士",
				Keys = new[] { "jazz", "爵士", "ジャズ" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"Bill Evans", "Miles Davis", "John Coltrane", "Thelonious Monk", "Chet Baker",
					"Norah Jones", "ジャズ", "jazz"
				},
				Samples = new[] { "So What", "Take Five", "Blue in Green" }
			},

			// ── 游戏原声 ──
			new InterestTopic
			{
				Id = "ost",
				Name = "游戏原声",
				Keys = new[] { "ost", "原声", "游戏音乐", "ゲーム音楽", "原声带" },
				Complete = "",
				SearchAs = "",
				Related = new[]
				{
					"原声带", "OST", "Original Soundtrack", "ゲーム音楽", "塞尔达", "原神", "崩坏",
					"Minecraft", "Undertale", "NieR", "尼尔"
				},
				Samples = new[] { "Weight of the World", "Dragonborn" }
			}
		};
	}

	// ── 对外 ──────────────────────────────────────────────────────────

	/// <summary>算出「这个输入最可能想补成什么」。
	/// 候选来自：爱好词条（东方→Project）、搜过的词、圈子代表曲。
	/// 打分后 softmax 归一化成概率；没候选就返回空的 Guess。</summary>
	public static Guess Complete(string? input)
	{
		string q = (input ?? "").Trim();
		// 一个字就开始猜太吵：敲「e」弹一堆补全反而碍事。
		// 中文一个字往往已经够指路（「东」除外，两个字的词才稳），
		// 这里统一要求至少两个字符。
		if (q.Length < 2)
		{
			return new Guess();
		}
		EnsureLoaded();
		var cands = new List<(string Suffix, string Full, double Score, string Source, InterestTopic? Topic)>();

		// ① 爱好词条：简称命中（东方 → 东方Project、pho → phonk）
		foreach (InterestTopic t in Topics)
		{
			foreach (string key in t.Keys)
			{
				if (!StartsWith(key, q))
				{
					continue;
				}
				// 敲得越接近全词越可信：敲「pho」比敲「p」更像 phonk
				double progress = (double)Math.Min(q.Length, key.Length) / Math.Max(1, key.Length);
				double score = 2.0 + 1.1 * progress + HabitBonus(t.Id);
				if (t.Replace)
				{
					// 推荐整首：淡色挂着的是歌名，采纳后整句换成它（english → So Far Away）
					cands.Add((" " + t.Complete, t.Complete, score, "兴趣", t));
					break;
				}
				// 补的是**规范词剩下的那一截**，不是拼两个词：
				// 「pho」+「nk」= phonk，中间不能插空格；
				// 「东方」+「Project」同理。已经敲全了（phonk）就没得补。
				string target = "";
				if (t.SearchAs.Length > q.Length && StartsWith(t.SearchAs, q))
				{
					target = t.SearchAs;
				}
				else
				{
					// 规范词对不上（敲的是「東方」而规范词是「东方Project」）
					// 就在这个词条的所有别名里找一个比输入长的来补。
					foreach (string alt in t.Keys)
					{
						if (alt.Length > q.Length && StartsWith(alt, q))
						{
							target = alt;
							break;
						}
					}
				}
				if (target.Length == 0)
				{
					break;
				}
				cands.Add((target.Substring(q.Length), target, score, "兴趣", t));
				break;
			}
		}

		// ② 搜过的词：按次数排（So → So Far Away）
		lock (Sync)
		{
			foreach (var kv in _counts)
			{
				string word = kv.Key;
				if (word.Length <= q.Length || !StartsWith(word, q))
				{
					continue;
				}
				double progress = (double)q.Length / word.Length;
				double score = 1.0 + 0.55 * Math.Log10(1 + kv.Value) + 0.9 * progress;
				cands.Add((word.Substring(q.Length), word, score, "搜过", null));
			}
		}

		// ③ 圈子的代表曲：也算补全候选，权重最低
		foreach (InterestTopic t in Topics)
		{
			foreach (string s in t.Samples)
			{
				if (s.Length <= q.Length || !StartsWith(s, q))
				{
					continue;
				}
				double progress = (double)q.Length / s.Length;
				cands.Add((s.Substring(q.Length), s, 0.9 + 0.6 * progress, "曲目", t));
			}
		}

		// ④ 结果里出现过的歌名：敲「Bad Ap」补「ple!!」，敲「六兆」补全整条歌名。
		//    这一路是「适配」匹配：先折成比对用的样子（小写、去掉空格标点），
		//    所以「badapple」也能对上「Bad Apple!!」—— 用户打字时对空格和
		//    感叹号没那么讲究。切回去补的时候用下标映射，补的是原串的那一截，
		//    不会把人家歌名里的大小写和感叹号弄丢。
		string fq = Fold(q, out _);
		lock (Sync)
		{
			foreach (var kv in _titles)
			{
				string title = kv.Key;
				if (title.Length <= q.Length || title.Length > 80)
				{
					continue;
				}
				string ft = Fold(title, out int[] map);
				if (ft.Length <= fq.Length || !ft.StartsWith(fq, StringComparison.Ordinal))
				{
					continue;
				}
				// 原串直接以输入开头就按原串切，否则按折叠后的位置切回原串
				int start = StartsWith(title, q) ? q.Length : map[fq.Length];
				if (start >= title.Length)
				{
					continue;
				}
				string suffix = title.Substring(start);
				if (suffix.Length > 40)
				{
					continue;
				}
				double progress = (double)q.Length / title.Length;
				// 出现次数越多越可信（播过的那首权重最高），敲得越接近全名也越可信
				double score = 1.1 + 0.55 * Math.Log10(1 + kv.Value) + 0.9 * progress;
				cands.Add((suffix, title, score, "结果", null));
			}
		}

		if (cands.Count == 0)
		{
			return new Guess();
		}
		// 同分同串去重：同一个词可能既在历史里又在代表曲里
		var best = new Dictionary<string, (string Suffix, string Full, double Score, string Source, InterestTopic? Topic)>(
			StringComparer.OrdinalIgnoreCase);
		foreach (var c in cands)
		{
			// 同一个补全被好几路同时指出来（比如「So Far Away」既在英文歌的
			// 代表曲里、又在电子的代表曲里）说明更可信 —— 分数累加而不是取最大。
			if (best.TryGetValue(c.Full, out var old))
			{
				// 来源标签跟着分高的那一走：「Bad Apple!!」既是东方的代表曲、
				// 又真的在结果里出现过，这时该显示「结果」而不是「曲目」。
				best[c.Full] = (old.Suffix, old.Full, old.Score + c.Score * 0.5,
					c.Score > old.Score ? c.Source : old.Source, old.Topic ?? c.Topic);
			}
			else
			{
				best[c.Full] = c;
			}
		}
		var list = best.Values.OrderByDescending(c => c.Score).Take(8).ToList();
		double total = list.Sum(c => Math.Exp(c.Score));
		var top = list[0];
		double p = total > 0 ? Math.Exp(top.Score) / total : 0.0;
		return new Guess
		{
			Suffix = top.Suffix,
			Full = top.Full,
			Probability = Math.Clamp(p, 0.0, 1.0),
			Source = top.Source,
			Topic = top.Topic
		};
	}

	/// <summary>这句搜索词最可能属于哪个圈子，连同概率一起给出。
	/// 概率低于 <see cref="TrustThreshold"/> 就当没命中。</summary>
	public static (InterestTopic? Topic, double Probability) Analyze(string? query)
	{
		string q = (query ?? "").Trim();
		// 只敲一个字不算数：「东」也可能是「东风破」。
		// 补全那边同样要求至少两个字，两边口径一致。
		if (q.Length < 2)
		{
			_lastTopic = null;
			_lastProbability = 0;
			return (null, 0);
		}
		EnsureLoaded();
		var scored = new List<(InterestTopic Topic, double Score)>();
		foreach (InterestTopic t in Topics)
		{
			double s = TopicScore(t, q);
			if (s > 0)
			{
				scored.Add((t, s));
			}
		}
		if (scored.Count == 0)
		{
			_lastTopic = null;
			_lastProbability = 0;
			return (null, 0);
		}
		double total = scored.Sum(x => Math.Exp(x.Score));
		var top = scored.OrderByDescending(x => x.Score).First();
		double p = total > 0 ? Math.Exp(top.Score) / total : 0.0;
		_lastTopic = top.Topic;
		_lastProbability = p;
		return (top.Topic, p);
	}

	/// <summary>该拿去真搜的词。命中圈子且它指定了 <see cref="InterestTopic.SearchAs"/>
	/// 时换成规范词（东方 → 东方Project）；否则原样返回。</summary>
	public static string Expand(string? query)
	{
		string q = (query ?? "").Trim();
		if (q.Length == 0)
		{
			return "";
		}
		var (topic, prob) = Analyze(q);
		if (topic == null || prob < TrustThreshold || topic.SearchAs.Length == 0)
		{
			return q;
		}
		// 敲了好几个词（「东方 红魔乡」）说明用户心里已经有具体目标了，
		// 这时候再自作主张换成规范词只会帮倒忙。
		if (q.Contains(' ') || q.Contains('　'))
		{
			return q;
		}
		return topic.SearchAs;
	}

	/// <summary>结果排序用：这首歌跟当前圈子有多贴。
	/// 数的是 Related 词在歌名/歌手里命中了几个，命中越多分越高。</summary>
	public static int RelatedScore(string? title, string? artist)
	{
		InterestTopic? t = _lastTopic;
		if (t == null || _lastProbability < TrustThreshold)
		{
			return 0;
		}
		string hay = ((title ?? "") + " " + (artist ?? "")).Trim();
		if (hay.Length == 0)
		{
			return 0;
		}
		int hits = 0;
		foreach (string term in t.Related)
		{
			if (term.Length == 0)
			{
				continue;
			}
			if (hay.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				hits++;
			}
		}
		return hits;
	}

	/// <summary>上一次 <see cref="Analyze"/> 的命中，界面上显示「猜你想找：XX（78%）」。</summary>
	public static (InterestTopic? Topic, double Probability) Last => (_lastTopic, _lastProbability);

	/// <summary>记一次搜索。次数影响补全排序，也影响概率。</summary>
	public static void NoteQuery(string? query)
	{
		Bump(query, 1);
	}

	/// <summary>记一次「采纳了补全」。比普通搜索更值钱，说明猜对了。</summary>
	public static void NoteAccepted(string? query)
	{
		Bump(query, 3);
	}

	/// <summary>把一批搜索结果里的歌名记下来，供以后补全。
	/// 只是列出来的记 1 分；真的点开播了的用 <see cref="NoteTitle"/> 记 3 分。</summary>
	public static void NoteTitles(IEnumerable<string?> titles)
	{
		if (titles == null)
		{
			return;
		}
		bool dirty = false;
		EnsureLoaded();
		lock (Sync)
		{
			foreach (string? raw in titles)
			{
				if (AddTitle(raw, 1))
				{
					dirty = true;
				}
			}
			if (dirty)
			{
				TrimTitles();
			}
		}
		if (dirty)
		{
			Save();
		}
	}

	/// <summary>记一首歌名。<paramref name="by"/> 是这次记几分：
	/// 出现在结果里记 1，用户真的播了记 3（播过说明是想要的那首）。</summary>
	public static void NoteTitle(string? title, int by = 1)
	{
		EnsureLoaded();
		lock (Sync)
		{
			if (!AddTitle(title, by))
			{
				return;
			}
			TrimTitles();
		}
		Save();
	}

	/// <returns>true = 真记进去了（有新词或次数变了）。</returns>
	private static bool AddTitle(string? raw, int by)
	{
		string t = (raw ?? "").Trim();
		// 太短的是噪音（「」「 live」），太长的多半不是歌名而是整行说明
		if (t.Length < 2 || t.Length > 80)
		{
			return false;
		}
		if (t.IndexOfAny(new[] { '\\', '/' }) >= 0)
		{
			// 带路径分隔符的是文件名，不是歌名
			return false;
		}
		_titles[t] = _titles.TryGetValue(t, out int n) ? n + by : by;
		return true;
	}

	/// <summary>别让歌名表无限长：留用得最多的那批。</summary>
	private static void TrimTitles()
	{
		if (_titles.Count <= 500)
		{
			return;
		}
		foreach (string k in _titles.OrderBy(x => x.Value).Take(_titles.Count - 350).Select(x => x.Key).ToList())
		{
			_titles.Remove(k);
		}
	}

	private static void Bump(string? query, int by)
	{
		string q = (query ?? "").Trim();
		if (q.Length == 0 || q.Length > 80)
		{
			return;
		}
		EnsureLoaded();
		lock (Sync)
		{
			_counts[q] = _counts.TryGetValue(q, out int n) ? n + by : by;
			// 别让它无限长：留最近用得多的那批
			if (_counts.Count > 400)
			{
				foreach (string k in _counts.OrderBy(x => x.Value).Take(_counts.Count - 300).Select(x => x.Key).ToList())
				{
					_counts.Remove(k);
				}
			}
		}
		Save();
	}

	private static int HabitBonus(string topicId)
	{
		// 用户搜得越多的圈子，补全时越优先（「pho」天天敲就该先给 phonk）
		lock (Sync)
		{
			int n = 0;
			foreach (var kv in _counts)
			{
				if (QueryBrainTopicsHit(topicId, kv.Key))
				{
					n += kv.Value;
				}
			}
			return n > 0 ? (int)Math.Min(1.2, 0.35 * Math.Log10(1 + n) * 2) : 0;
		}
	}

	private static bool QueryBrainTopicsHit(string topicId, string word)
	{
		InterestTopic? t = Topics.FirstOrDefault(x => x.Id == topicId);
		if (t == null)
		{
			return false;
		}
		return t.Related.Any(r => r.Length > 0 && word.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0)
			|| t.Keys.Any(k => word.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	private static double TopicScore(InterestTopic t, string q)
	{
		double best = 0;
		foreach (string key in t.Keys)
		{
			if (string.Equals(key, q, StringComparison.OrdinalIgnoreCase))
			{
				// 完全等于简称：最可信的一档
				best = Math.Max(best, 2.6 + HabitBonus(t.Id));
				continue;
			}
			if (StartsWith(key, q))
			{
				double progress = (double)q.Length / key.Length;
				best = Math.Max(best, 1.6 + 1.4 * progress + HabitBonus(t.Id));
				continue;
			}
			// 反过来：输入里带着这个圈子的关键词（「东方 红魔乡」）
			if (q.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				best = Math.Max(best, 1.9 + HabitBonus(t.Id));
			}
		}
		if (best > 0)
		{
			return best;
		}
		// 相关艺人/社团名也算线索（敲「Kordhell」→ 归到 phonk）
		foreach (string term in t.Related)
		{
			if (term.Length < 3)
			{
				continue;
			}
			if (q.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return 1.5;
			}
		}
		return 0;
	}

	private static bool StartsWith(string text, string prefix)
		=> text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

	/// <summary>把字符串折成「比对用的样子」：小写、去掉空格和标点符号。
	/// 「Bad Apple!!」折成「badapple」，这样敲「badapple」也能对上。
	/// <paramref name="map"/> 记下折叠后每个字符在原串里的下标，
	/// 补完那截要靠它切回原串（保住原来的大小写和感叹号）。</summary>
	private static string Fold(string s, out int[] map)
	{
		var sb = new System.Text.StringBuilder(s.Length);
		var idx = new List<int>(s.Length);
		for (int i = 0; i < s.Length; i++)
		{
			char c = s[i];
			if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
			{
				continue;
			}
			sb.Append(char.ToLowerInvariant(c));
			idx.Add(i);
		}
		map = idx.ToArray();
		return sb.ToString();
	}

	/// <summary>拼补全串：两边都是 ASCII 字母/数字时中间补一个空格，
	/// 中文之间直接贴（「东方」+「Project」不要空格）。</summary>
	private static string TrimJoin(string typed, string suffix)
	{
		if (suffix.Length == 0)
		{
			return "";
		}
		if (typed.Length == 0)
		{
			return suffix;
		}
		char a = typed[^1];
		char b = suffix[0];
		bool needSpace = IsAsciiWord(a) && IsAsciiWord(b);
		return needSpace ? " " + suffix : suffix;
	}

	private static bool IsAsciiWord(char c)
		=> (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

	// ── 落盘 ──────────────────────────────────────────────────────────

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
					string json = File.ReadAllText(StorePath);
					BrainStore? data = null;
					try
					{
						data = JsonSerializer.Deserialize<BrainStore>(json);
					}
					catch
					{
						// 老版本存的是一整个「词 → 次数」字典，底下那个分支接住
					}
					if (data != null)
					{
						if (data.Counts != null)
						{
							foreach (var kv in data.Counts)
							{
								_counts[kv.Key] = kv.Value;
							}
						}
						if (data.Titles != null)
						{
							foreach (var kv in data.Titles)
							{
								_titles[kv.Key] = kv.Value;
							}
						}
					}
					else
					{
						var legacy = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
						if (legacy != null)
						{
							foreach (var kv in legacy)
							{
								_counts[kv.Key] = kv.Value;
							}
						}
					}
				}
			}
			catch
			{
				// 记不住次数不影响补全，只是少了「按搜过的次数排序」这一路
			}
		}
	}

	private static void Save()
	{
		try
		{
			Dictionary<string, int> counts;
			Dictionary<string, int> titles;
			lock (Sync)
			{
				counts = new Dictionary<string, int>(_counts);
				titles = new Dictionary<string, int>(_titles);
			}
			string json = JsonSerializer.Serialize(new BrainStore
			{
				Counts = counts,
				Titles = titles
			});
			File.WriteAllText(StorePath, json);
		}
		catch
		{
		}
	}
}
