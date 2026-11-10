namespace MusicBox.Views;

using System.Windows.Media;

/// <summary>歌词窗的视觉常量（横排 + 竖排共用一份）。
///
/// 原来散在 LyricWindow.xaml.cs 中段和 LyricView.xaml.cs 顶部，
/// 调一个字号要翻两个文件、还会和动画常量混在一起。
/// 集中到这里只为「好找」——数值一个没改，语义和注释照旧。
///
/// 刻意没搬的：颜色/画刷逻辑（CurSungBrush、AnimateBrush 一族）。
/// 它们绑着具体 TextBlock 和设置读取，硬抽成静态类得把元素传来传去，
/// 收益不大、回归风险倒是真的，留在各自文件里。</summary>
internal static class LyricVisualTheme
{
	// ── 横排：三行明暗层级（LyricWindow）──

	/// <summary>上一句（已唱过）的透明度。</summary>
	public const double PrevPlayed = 0.22;

	/// <summary>下一句（还没唱到）的透明度。刻意比上一句亮一点，
	/// 形成「已唱—正在唱—待唱」三档，扫一眼就知道进度。
	///
	/// 原来是 0.42，太暗了：它下面还压着 TextSecondaryBrush(#C8FFFFFF)，
	/// 实际不透明度只剩 0.42*0.78≈0.33。PushUp「下一句冒头」展示的就是
	/// 这一行，暗到读不出来等于那个效果不存在。提到 0.62：仍然明显
	/// 弱于当前句（1.0），但读得出来。</summary>
	public const double NextPending = 0.62;

	/// <summary>上移动画里「上一句」那一格起始的透明度。
	/// 退上去的那句原本是当前句（全不透明），一路淡到 <see cref="PrevPlayed"/>，
	/// 于是「唱过了」这件事是动画本身交代的，不只是落位后忽然变灰。</summary>
	public const double PrevArriveOpacity = 0.9;

	/// <summary>上移动画里「当前句」那一格起始的透明度。
	/// 从下面升上来的那句先半透明，落到中间才完全亮起来。</summary>
	public const double CurArriveOpacity = 0.55;

	/// <summary>新冒出来的那句的起始缩放。略小于 1，冒到位的同时长开。</summary>
	public const double NextRiseScale = 0.92;

	/// <summary>量不到格子间距时的兜底步长（像素）。</summary>
	public const double FallbackRowStep = 40.0;

	// ── 横排：字号自适应（LyricWindow）──

	/// <summary>当前行字号上限。横排当前行的基准字号，长的会往下缩、短的不超过它。</summary>
	public const double CurFontMax = 21;

	/// <summary>当前行字号下限。再长的句子也保留这个大小，
	/// 再缩下去在大屏上就读不清了，宁可让它换行。</summary>
	public const double CurFontMin = 12;

	/// <summary>字号缩放的基准窗口高度。弹窗默认就是这个高度，
	/// 此时缩放系数正好是 1，行为跟「不缩放」一样。</summary>
	public const double BaseWindowHeight = 150;

	/// <summary>字号随弹窗高度缩放的范围。0.72~1.55 大致对应
	/// 最小高度到两倍多：太小了字会缩到读不清，再大则撑破三行布局。</summary>
	public const double FontScaleMin = 0.72;
	public const double FontScaleMax = 1.55;

	// ── 竖排：行高与字号（LyricView）──

	/// <summary>单行文字的高度（逻辑像素）。有翻译时行高翻倍。</summary>
	public const double LineHeightPx = 34;

	/// <summary>两行（正文+翻译）时的行高。</summary>
	public const double TwoLineHeightPx = LineHeightPx * 2;

	/// <summary>当前行相对视口正中往下偏多少。留出上方空间放歌名，
	/// 视觉重心才在下方，跟网易云桌面歌词的排布一致。</summary>
	public const double ActiveLineDropPx = 110;

	/// <summary>竖排字号上限/下限。最远的行缩到下限，避免「景深」变成看不清。</summary>
	public const int MaxFontSize = 19;
	public const double MinFontSize = 13.5;

	// ── 桌面歌词固定配色（横排 + 竖排共用，不随应用主题）──
	//
	// 桌面歌词浮在桌面上，不是应用窗口的一部分：应用换深浅主题，
	// 歌词都该长一个样。以前空配置回退到 {DynamicResource 主题刷}，
	// 主题一切歌词跟着变色——「在深色主题下调好的观感，
	// 切到浅色主题后桌面歌词突然发黑」就是这么来的。
	// 定值取自深色主题（桌面歌词的默认语境）：
	// 未唱=纯白（层级靠行透明度交代）、已唱=主题强调蓝、译文=65% 白。
	// DlSecondary/DlTertiary 是上一句/下一句/状态行这些「没被配置覆盖」
	// 的行的恢复色，对应以前的主题 TextSecondary/TextTertiary。

	/// <summary>未唱文字（当前句底色层/下一句）。纯白。</summary>
	public static readonly Color DlUnsung = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

	/// <summary>已唱文字（当前句亮色层/上一句）。强调蓝（深色主题 Accent 的定值）。</summary>
	public static readonly Color DlSung = Color.FromArgb(0xFF, 0x55, 0xA2, 0xFF);

	/// <summary>译文行。65% 白。</summary>
	public static readonly Color DlTranslation = Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF);

	/// <summary>次级文字（上一句/下一句的 XAML 默认）。原 TextSecondaryBrush 定值。</summary>
	public static readonly Color DlSecondary = Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);

	/// <summary>弱化文字（状态行/空态提示）。原 TextTertiaryBrush 定值。</summary>
	public static readonly Color DlTertiary = Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF);
}
