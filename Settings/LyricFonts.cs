using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace MusicBox.Settings;

/// <summary>歌词窗可选字族清单。
///
/// 为什么不直接列出系统所有字体：<see cref="Fonts.SystemFontFamilies"/> 在这台
/// 机器上有 91 个族，里面大半是纯西文或符号字体（Consolas、Marlett、Webdings…），
/// 拿来做中英文歌词只会掉字形或者比例失真。所以给一份「适合当中英文歌词」
/// 的候选表，再用本机实际安装情况过滤——用户看到的就是自己能选的。
///
/// 清单是按 Windows 内置中文字体整理的（微软雅黑/等线/宋体/黑体/楷体/仿宋/
/// 正黑，以及华文系列和隶书幼圆）。繁体系统上正黑和细明体也在表里，
/// 简体系上华文系列不在——过滤之后各机器看到的组合不一样，这是对的。
public static class LyricFonts
{
	/// <summary>默认字族。跟 <see cref="AppSettings.LyricFontFamily"/> 的初值保持一致。</summary>
	public const string Default = "Microsoft YaHei UI";

	/// <summary>候选清单：(显示用中文名, 字体家族名)。</summary>
	private static readonly (string Display, string Family)[] Candidates =
	{
		("微软雅黑 UI", "Microsoft YaHei UI"),
		("微软雅黑", "Microsoft YaHei"),
		("等线", "DengXian"),
		("宋体", "SimSun"),
		("新宋体", "NSimSun"),
		("黑体", "SimHei"),
		("楷体", "KaiTi"),
		("仿宋", "FangSong"),
		("微软正黑体", "Microsoft JhengHei"),
		("华文细黑", "STXihei"),
		("华文黑体", "STHeiti"),
		("华文中宋", "STZhongsong"),
		("华文宋体", "STSong"),
		("华文仿宋", "STFangsong"),
		("华文楷体", "STKaiti"),
		("华文行楷", "STXingkai"),
		("华文彩云", "STCaiyun"),
		("华文琥珀", "STHupo"),
		("隶书", "LiSu"),
		("幼圆", "YouYuan"),
		("方正舒体", "FZShuTi"),
		("方正姚体", "FZYaoti"),
		("细明体", "MingLiU"),
		("新细明体", "PMingLiU"),
		("等线 Light", "DengXian Light"),
		// 日韩系只作为「字形覆盖广」的备选，排在中文族后面。
		("游ゴシック（Yu Gothic）", "Yu Gothic"),
		("맑은 고딕（Malgun Gothic）", "Malgun Gothic"),
		("ＭＳ ゴシック（MS Gothic）", "MS Gothic"),
	};

	private static HashSet<string>? _installed;

	/// <summary>本机实际装了的候选族。Fonts 初始化比较慢，整段只探一次。</summary>
	private static HashSet<string> Installed
	{
		get
		{
			if (_installed != null) return _installed;
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try
			{
				foreach (var f in Fonts.SystemFontFamilies) set.Add(f.Source);
			}
			catch
			{
				// 极端精简的 Windows 可能枚举失败。留空集合，
				// Available() 会退化成只给默认族，不会让下拉整个变空。
			}
			_installed = set;
			return _installed;
		}
	}

	/// <summary>候选清单里本机装了的：显示名 + 家族名。默认族永远排第一。</summary>
	public static List<(string Display, string Family)> Available()
	{
		var list = new List<(string, string)>();
		foreach (var c in Candidates)
		{
			if (Installed.Contains(c.Family)) list.Add(c);
		}
		// 一个候选都没命中（很怪的精简系统）时至少把默认族放进去，
		// 否则下拉是空的，用户没法表达「我要默认」
		if (list.Count == 0) list.Add(("微软雅黑 UI", Default));
		// 把默认族提到最前，省得每次都要在二十项里找
		int i = list.FindIndex(x => string.Equals(x.Item2, Default, StringComparison.OrdinalIgnoreCase));
		if (i > 0)
		{
			var d = list[i];
			list.RemoveAt(i);
			list.Insert(0, d);
		}
		return list;
	}

	/// <summary>家族名在本机能不能用。设置里存的值就是靠它验一遍。</summary>
	public static bool IsAvailable(string? family)
	{
		if (string.IsNullOrWhiteSpace(family)) return false;
		if (string.Equals(family, Default, StringComparison.OrdinalIgnoreCase)) return true;
		return Installed.Contains(family);
	}

	/// <summary>家族名 -> 显示名。找不到就原样返回（多半是用户手改过设置）。</summary>
	public static string DisplayName(string? family)
	{
		foreach (var c in Candidates)
		{
			if (string.Equals(c.Family, family, StringComparison.OrdinalIgnoreCase)) return c.Display;
		}
		return family ?? Default;
	}
}
