using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicBox.Services;

/// <summary>歌词切句动画的形态。</summary>
public enum LyricAnimKind
{
	/// <summary>当前句原地浮动 + 选中缩放过渡，翻译横向推挤。
	/// 不移动上下行，所以已唱/待唱那两行的位置是稳的。</summary>
	Float,

	/// <summary>三行整体上移：当前句被顶到「上一句」的位置，
	/// 原来的下一句升进当前句，而再下一句从下面冒出来。</summary>
	PushUp,
}

/// <summary>一套歌词动画参数。
///
/// 这一层存在的意义是：切句动画的所有可调量（时长、幅度、缩放起点、摆动周期）
/// 以前全是 <c>LyricWindow</c> 里的 const，改一个数要重新编译。
/// 抽成模板后既能在设置里切风格，也能让用户从文件夹里加载自己的一套。</summary>
public sealed class LyricAnimTemplate
{
	public string Name { get; init; } = "";
	public string Description { get; init; } = "";

	public LyricAnimKind Kind { get; init; }

	/// <summary>选中过渡时长（毫秒）。0 = 不播缩放过渡。</summary>
	public double SelectMs { get; init; } = 240;

	/// <summary>选中过渡的起始缩放。1 = 不缩。</summary>
	public double SelectFromScale { get; init; } = 0.94;

	/// <summary>翻译推挤时长（毫秒）。0 = 直接换字不推。</summary>
	public double TransPushMs { get; init; } = 300;

	/// <summary>当前句上下摆动的振幅上限（像素）。0 = 不摆。</summary>
	public double BobAmplitude { get; init; } = 4.0;

	/// <summary>摆动一轮来回的秒数。</summary>
	public double BobPeriodSeconds { get; init; } = 2.4;

	/// <summary>三行上移（PushUp）的时长（毫秒）。</summary>
	public double PushUpMs { get; init; } = 320;

	/// <summary>来源文件夹；内置模板为 null。</summary>
	public string? Folder { get; init; }

	public bool IsBuiltIn => Folder == null;

	/// <summary>是否需要给三行挂竖向位移（PushUp 才需要）。</summary>
	public bool NeedsRowShift => Kind == LyricAnimKind.PushUp;
}

/// <summary>歌词动画模板表：内置两套 + 用户文件夹里加载的若干套。</summary>
public static class LyricAnimTemplates
{
	/// <summary>内置默认。当前这套就是用户一直在看的那套，
	/// 所以它必须是最稳的那套：只动当前句，上下两行不挪位置。</summary>
	public static readonly LyricAnimTemplate Float = new()
	{
		Name = "默认 · 浮动顶出",
		Description = "当前句原地轻微上下浮动，切进来时放大一下淡入；翻译从左进来把上一句往右顶出去。上下两行不移动。",
		Kind = LyricAnimKind.Float,
		SelectMs = 240,
		SelectFromScale = 0.94,
		TransPushMs = 300,
		BobAmplitude = 4.0,
		BobPeriodSeconds = 2.4,
		PushUpMs = 0,
	};

	/// <summary>第二套：三行整体上移，下一句升上来、再下一句从下面冒出来。</summary>
	public static readonly LyricAnimTemplate PushUp = new()
	{
		Name = "顶上去 · 下一句冒头",
		Description = "唱到下一句时整块上移一格：当前句被顶到上一行的位置，原来的下一句升进中间，再下一句从下面冒出来。",
		Kind = LyricAnimKind.PushUp,
		SelectMs = 0,
		SelectFromScale = 1.0,
		TransPushMs = 260,
		BobAmplitude = 0.0,
		BobPeriodSeconds = 2.4,
		PushUpMs = 320,
	};

	/// <summary>内置模板的固定顺序：默认永远第一项。</summary>
	public static readonly LyricAnimTemplate[] BuiltIn = { Float, PushUp };

	public static LyricAnimTemplate Default => Float;

	/// <summary>文件夹里最多读多少个模板文件。
	/// 用户可能指到盘根或某个几千文件的目录，不设上限的话
	/// 点一次「加载」要扫到地老天荒，界面直接卡住。</summary>
	private const int MaxFolderTemplates = 64;

	/// <summary>单个模板文件的大小上限。防的是「指着选了个 2GB 的 json」。</summary>
	private const long MaxFileBytes = 256 * 1024;

	/// <summary>按名字取模板：先内置，再文件夹。
	/// 取不到就回默认，而不是抛异常或返回 null——
	/// 「文件夹被删了/名字被手改了」是常态，不是错误。</summary>
	public static LyricAnimTemplate Resolve(string? name, string? folder)
	{
		foreach (LyricAnimTemplate t in BuiltIn)
		{
			if (string.Equals(t.Name, name, StringComparison.Ordinal))
			{
				return t;
			}
		}
		if (!string.IsNullOrWhiteSpace(folder))
		{
			foreach (LyricAnimTemplate t in LoadFolder(folder))
			{
				if (string.Equals(t.Name, name, StringComparison.Ordinal))
				{
					return t;
				}
			}
		}
		return Default;
	}

	/// <summary>内置 + 文件夹，一份可以直接铺到设置页的完整列表。</summary>
	public static List<LyricAnimTemplate> All(string? folder)
	{
		var list = new List<LyricAnimTemplate>(BuiltIn);
		if (!string.IsNullOrWhiteSpace(folder))
		{
			list.AddRange(LoadFolder(folder));
		}
		return list;
	}

	/// <summary>扫文件夹里顶层的 *.json，每个文件一套模板。
	/// 顺序按文件名排，保证每次进设置页的列表顺序一致。</summary>
	public static List<LyricAnimTemplate> LoadFolder(string? folder)
	{
		var list = new List<LyricAnimTemplate>();
		if (string.IsNullOrWhiteSpace(folder))
		{
			return list;
		}
		string[] files;
		try
		{
			if (!Directory.Exists(folder))
			{
				return list;
			}
			// 只看顶层，不递归：模板是「一个文件夹一套」，递归会把
			// 用户音乐目录里成千上万个文件也当成候选。
			files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
		}
		catch
		{
			return list;
		}
		Array.Sort(files, StringComparer.OrdinalIgnoreCase);

		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach (LyricAnimTemplate t in BuiltIn)
		{
			used.Add(t.Name);
		}
		int taken = 0;
		foreach (string file in files)
		{
			if (taken >= MaxFolderTemplates)
			{
				break;
			}
			try
			{
				var info = new FileInfo(file);
				if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
				{
					continue;
				}
				TemplateFile? tf = JsonSerializer.Deserialize<TemplateFile>(
					File.ReadAllText(file),
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				if (tf == null)
				{
					continue;
				}
				LyricAnimTemplate t = FromFile(tf, folder, Path.GetFileNameWithoutExtension(file));
				// 同名（跟内置重名、或文件夹里自己重名）就跳过，
				// 不覆盖内置项——内置名是设置里存的标识，被顶掉等于设置失效。
				if (!used.Add(t.Name))
				{
					continue;
				}
				list.Add(t);
				taken++;
			}
			catch
			{
				// 一个坏文件不该让整个文件夹都读不出来
			}
		}
		return list;
	}

	/// <summary>文件 → 模板。缺的项用内置默认补上，越界的值夹回合理区间。
	///
	/// 夹值这步是必须的：这里读的是用户（或用户手滑复制来的）文件，
	/// 一个 <c>PushUpMs: 99999999</c> 或 <c>BobAmplitude: -5</c> 不该
	/// 让窗口抖到卡死。所有时长/幅度都先夹再返回。</summary>
	private static LyricAnimTemplate FromFile(TemplateFile tf, string folder, string fallbackName)
	{
		string name = (tf.Name ?? "").Trim();
		if (name.Length == 0)
		{
			name = fallbackName;
		}
		LyricAnimKind kind = ParseKind(tf.Kind, Float.Kind);
		var def = kind == LyricAnimKind.PushUp ? PushUp : Float;
		return new LyricAnimTemplate
		{
			Name = name,
			Description = (tf.Description ?? "").Trim(),
			Kind = kind,
			SelectMs = Clamp(tf.SelectMs ?? def.SelectMs, 0, 2000),
			SelectFromScale = Clamp(tf.SelectFromScale ?? def.SelectFromScale, 0.5, 1.5),
			TransPushMs = Clamp(tf.TransPushMs ?? def.TransPushMs, 0, 3000),
			BobAmplitude = Clamp(tf.BobAmplitude ?? def.BobAmplitude, 0, 40),
			BobPeriodSeconds = Clamp(tf.BobPeriodSeconds ?? def.BobPeriodSeconds, 0.2, 20),
			PushUpMs = Clamp(tf.PushUpMs ?? def.PushUpMs, 0, 3000),
			Folder = folder,
		};
	}

	private static LyricAnimKind ParseKind(string? raw, LyricAnimKind fallback)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return fallback;
		}
		string s = raw.Trim();
		if (s.Equals("pushup", StringComparison.OrdinalIgnoreCase)
			|| s.Equals("push_up", StringComparison.OrdinalIgnoreCase)
			|| s == "顶上去")
		{
			return LyricAnimKind.PushUp;
		}
		return LyricAnimKind.Float;
	}

	private static double Clamp(double v, double lo, double hi)
		=> double.IsNaN(v) || double.IsInfinity(v) ? lo : Math.Clamp(v, lo, hi);

	/// <summary>模板文件的形状。全部可空 + 全部有默认值：
	/// 只写 <c>{"Name":"x","Kind":"pushup"}</c> 也是一份合法模板。</summary>
	private sealed class TemplateFile
	{
		public string? Name { get; set; }
		public string? Description { get; set; }
		public string? Kind { get; set; }
		public double? SelectMs { get; set; }
		public double? SelectFromScale { get; set; }
		public double? TransPushMs { get; set; }
		public double? BobAmplitude { get; set; }
		public double? BobPeriodSeconds { get; set; }
		public double? PushUpMs { get; set; }
	}
}
