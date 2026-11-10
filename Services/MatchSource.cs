namespace MusicBox.Services;

/// <summary>一份数据（歌词 / 封面）是从哪个平台取到的。
///
/// 只在界面上用来给底栏那颗封面方块画双色框：网易云红、QQ 绿，
/// 其它来源（LRCLIB / 酷狗 / B 站）灰，没匹配到就不画。
/// 不参与任何播放逻辑，纯粹是个展示标记。</summary>
public enum MatchSource
{
	/// <summary>没匹配到（或还没去查）。界面上这一半不画颜色。</summary>
	None = 0,

	/// <summary>网易云音乐。</summary>
	Netease = 1,

	/// <summary>QQ 音乐。</summary>
	Qq = 2,

	/// <summary>其它来源：LRCLIB、酷狗、B 站……</summary>
	Other = 3
}

/// <summary>一首歌的两半来源：歌词 + 封面。两者可以来自不同平台，
/// 所以分开记（网易云出歌词、QQ 出封面是常态）。</summary>
public readonly record struct MatchSources(MatchSource Lyric, MatchSource Cover)
{
	public static MatchSources Empty => new(MatchSource.None, MatchSource.None);
}
