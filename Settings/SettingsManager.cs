using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MusicBox.Services;

namespace MusicBox.Settings;

public sealed class FxSettings
{
	public string PresetName { get; set; } = "关闭";

	public bool EqEnabled { get; set; } = false;

	public float[] EqGains { get; set; } = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

	public bool BassBoostEnabled { get; set; } = false;

	public float BassBoostGain { get; set; } = 0f;

	public bool ReverbEnabled { get; set; } = false;

	public float ReverbRoomSize { get; set; } = 0.3f;

	public float ReverbWetLevel { get; set; } = 0.2f;

	public bool ExciterEnabled { get; set; } = false;

	public float ExciterAmount { get; set; } = 0f;

	public bool CompressorEnabled { get; set; } = false;

	public float CompressorThreshold { get; set; } = -20f;

	public float CompressorRatio { get; set; } = 2f;

	public bool WidthEnabled { get; set; } = false;

	public float WidthAmount { get; set; } = 1f;

	public bool SurroundEnabled { get; set; } = false;

	public float SurroundStrength { get; set; } = 0.85f;

	/// <summary>环绕算法变体。默认同相加宽（<see cref="SurroundMode.SamePhase"/>）——
	/// 另外几套是当初因为「有回声/有耳鸣」被换掉的旧算法，
	/// 现在作为候选一起留着给用户试听。</summary>
	public SurroundMode SurroundMode { get; set; } = SurroundMode.SamePhase;
}

public sealed class SavedTrack
{
	public string Type { get; set; } = "local";

	public string Id { get; set; } = "";

	public string Title { get; set; } = "";

	public string Artist { get; set; } = "";

	public long DurationMs { get; set; }

	public string Cover { get; set; } = "";

	public string Path { get; set; } = "";

	public string Key => Type == "local" ? "local:" + Path : Type + ":" + Id;

	public override string ToString() => Title;
}

public sealed class AppSettings
{
	public string MusicFolder { get; set; } = "";

	public string MusicPath { get; set; } = "";

	public string BackgroundPath { get; set; } = "";

	public string BackgroundType { get; set; } = "image";

	public double BackgroundDarkness { get; set; } = 0.38;

    /// <summary>背景是否缓慢摆动。默认关闭：用户反馈背景会「乱动漂移」，
    /// 固定不动更稳，需要的话可以在设置里手动打开。</summary>
    public bool BackgroundSwing { get; set; }

    /// <summary>用户是否在设置里亲手点过「背景摆动」这个开关。
    /// 旧版本的默认值是 true，所以老用户 settings.json 里存的就是 true，
    /// 光把属性默认值改成 false 压不住他们 —— 每次启动还是会动。
    /// 这个标记用来区分「沿用旧默认」和「用户自己开的」：
    /// 前者强制关掉，后者照用户的选择保留。</summary>
    public bool BackgroundSwingChosen { get; set; }

	public double BgVideoVolume { get; set; } = 0.7;

	/// <summary>背景图/背景视频自身的不透明度（0~1），对应 MhyunBox 的 BackgroundOpacity。
	/// 注意它和 BackgroundDarkness 是两件事：前者压底图本身，后者才是压暗蒙版。</summary>
	public double BackgroundOpacity { get; set; } = 0.5;

	/// <summary>背景缓慢摆动的幅度（0~100），对应 MhyunBox 的 SwingAmplitude。</summary>
	public double SwingAmplitude { get; set; } = 50.0;

	/// <summary>背景视频轮播目录：留空则用程序目录下 Assets\Backgrounds。</summary>
	public string VideoFolder { get; set; } = "";

	/// <summary>曲源：local / online，对应 MhyunBox 的 MusicSource。</summary>
	public string MusicSource { get; set; } = "local";

	/// <summary>主题：dark / light / system。当前外壳按 WinUI 深色排版，先落 dark。</summary>
	public string ThemeMode { get; set; } = "dark";

	/// <summary>全局字号，对应 MhyunBox 的 FontSize / FontBold。</summary>
	public double FontSize { get; set; } = 14.0;

	public bool FontBold { get; set; } = false;

	/// <summary>托盘图标是否常驻显示（与「最小化到托盘」分开控制）。</summary>
	public bool ShowTrayIcon { get; set; } = true;

	public string Language { get; set; } = "zh-CN";

	public string PlaybackMode { get; set; } = "random";

	public List<string> MusicOrder { get; set; } = new List<string>();

	public List<string> SkippedMusic { get; set; } = new List<string>();

	public double MusicVolume { get; set; } = 0.5;

	public bool SurroundEnable { get; set; } = true;

	public double SurroundStrength { get; set; } = 1.0;

	/// <summary>环绕算法变体。这才是 BuildFxChain 实际读的那个
	/// （和 SurroundEnable / SurroundStrength 同级）。
	/// FxSettings 里那份是给预设用的，ApplyPreset 两边都会写。</summary>
	public SurroundMode SurroundMode { get; set; } = SurroundMode.SamePhase;

	/// <summary>下载缓存：规范化歌名 → 已下载到音乐库的本地文件路径，命中同名曲目时复用不重复下载。</summary>
	public Dictionary<string, string> DownloadCache { get; set; } = new Dictionary<string, string>();

	public bool MusicPaused { get; set; } = false;

	public bool AutoPlayOnStartup { get; set; } = true;

	/// <summary>点标题栏 X 之后干什么：tray = 收进托盘（默认），ask = 收进托盘并在托盘上提示，
	/// exit = 直接退出。用户要的是「默认关掉还留着程序」，所以默认值给 tray。</summary>
	public string CloseBehavior { get; set; } = "tray";

	public bool StartMinimizedToTray { get; set; } = false;

	public bool ShowStartupAnimation { get; set; } = true;

	public bool AutoStartOnBoot { get; set; } = false;

	public bool DebugMode { get; set; } = false;

	public string NeteaseCookie { get; set; } = "";

	public string NeteaseUid { get; set; } = "";

	public string NeteaseNick { get; set; } = "";

	public string BiliCookie { get; set; } = "";

	public string QqCookie { get; set; } = "";

	public string QqUin { get; set; } = "";

	public string KugouCookie { get; set; } = "";

	public string KugouNick { get; set; } = "";

	/// <summary>程序主色调（十六进制 #RRGGBB），影响播放按钮、进度条等强调色。
	/// 默认墨黑——设置页下拉里墨黑也排在第一位，两边必须一致，
	/// 不然新装用户看到的默认色和下拉第一项对不上。</summary>
	public string AccentColor { get; set; } = "#1C1C1C";

	/// <summary>底部「最近播放」抽屉最后打开的标签页。</summary>
	public string DrawerTab { get; set; } = "recent";

	/// <summary>全局快捷键。默认值刻意避开 Ctrl+Shift+字母 / Ctrl+Alt+字母：
	/// 截图工具和中文输入法基本都占这一片，实测三个组合同时注册失败。
	/// 改用方向键和空格这类非字母键，被占概率小得多；万一还是被占，
	/// <see cref="Services.HotkeyManager"/> 会自动顺备选往下试。</summary>
	public Dictionary<string, string> Hotkeys { get; set; } = new Dictionary<string, string>
	{
		["next"] = "Ctrl+Alt+Right",
		["prev"] = "Ctrl+Alt+Left",
		["playpause"] = "Ctrl+Alt+Space",
		["fav"] = "Ctrl+Alt+F",
		["toggle"] = "Ctrl+Alt+W",
		["lyric_passthrough"] = "Ctrl+Alt+L"
	};

	public FxSettings Fx { get; set; } = new FxSettings();

	public bool OnlineEnabled { get; set; } = true;

	public int OnlineSearchLimit { get; set; } = 30;

	public List<string> OnlineEngines { get; set; } = new List<string> { "netease", "kugou", "bili", "qq" };

	/// <summary>B 站结果是否按播放量从高到低排。只有 B 站有这个字段，
	/// 其它平台不受影响。默认开。</summary>
	public bool OnlineSortByPlay { get; set; } = true;

	/// <summary>音乐库是否记住搜索关键词。false = 每次切走页面就清空筛选。</summary>
	public bool LibraryKeepFilter { get; set; } = false;

	/// <summary>桌面歌词悬浮窗是否显示。这是总开关，关掉就不跟随当前歌曲去拉歌词。</summary>
	public bool LyricWindowEnabled { get; set; } = false;

	/// <summary>歌词窗口上次的位置和大小。存下来下次开还是在这个地方。</summary>
	public double LyricWindowLeft { get; set; } = double.NaN;

	public double LyricWindowTop { get; set; } = double.NaN;

	/// <summary>歌词窗宽度。460 太窄，长句和带 feat. 的作者名会被切掉，所以放宽到 720。</summary>
	public double LyricWindowWidth { get; set; } = 720;

	/// <summary>歌词窗高度。</summary>
	public double LyricWindowHeight { get; set; } = 300;

	/// <summary>歌词布局：true = 竖排（上一句/当前句/下一句滚动），false = 横排（单行字幕带）。</summary>
	public bool LyricWindowVertical { get; set; } = true;

	/// <summary>是否同时显示翻译行。网易云的 tlyric 就是官方译文。</summary>
	public bool LyricShowTranslation { get; set; } = true;

	/// <summary>歌词窗口是否总在最前。</summary>
	public bool LyricWindowTopmost { get; set; } = true;

	/// <summary>歌词窗口是否鼠标穿透。
	/// 开了之后背景点得穿到下面的窗口上，窗口自己只剩歌词文字；
	/// 标题栏那排按钮仍然点得到（穿透是按 WM_NCHITTEST 逐点判的，
	/// 落在按钮上就当普通客户区），鼠标移到窗口上沿会把标题栏唤出来。</summary>
	public bool LyricWindowClickThrough { get; set; } = false;

	/// <summary>歌词窗字族。存的是字体家族名（如 "SimSun"），
	/// 不是显示用的中文名（"宋体"）。设置页的下拉按本机已装字体过滤，
	/// 换机器时这里存的值在新机器上不存在时会退回默认，见 <see cref="Load"/>。</summary>
	public string LyricFontFamily { get; set; } = "Microsoft YaHei UI";

	/// <summary>歌词窗当前行的自定义颜色（#RRGGBB）。
	/// 空串 = 跟随主题（用 TextPrimaryBrush / TextTertiaryBrush）。
	/// 存的是色值不是索引，跟换主题一样，换主题不该把用户挑的歌词色冲掉。</summary>
	public string LyricFontColor { get; set; } = "";

	/// <summary>选中的歌词动画模板名。存名字不存索引——内置模板改名/加项时
	/// 老设置不会因为下标错位而指到别的动画上。取不到就回
	/// <see cref="LyricAnimTemplates.Default"/>。</summary>
	public string LyricAnimTemplate { get; set; } = "";

	/// <summary>自定义歌词动画模板文件夹。空 = 只用内置那两套。</summary>
	public string LyricAnimTemplateFolder { get; set; } = "";

	/// <summary>搜索音乐页的爱好提示词（逗号分隔），打开时随机取一个自动搜索。</summary>
	public string HobbyKeywords { get; set; } =
		"二次元,东方Project,原神,崩坏,明日方舟,赛马娘,孤独摇滚,葬送的芙莉莲,轻音少女,吹响吧上低音号,魔法少女小圆,初音未来,VOCALOID,极乐净土,宅舞曲,动漫原声,日漫主题曲,二创,RED,车万歌,音乐节Live,催泪神曲,翻唱,鬼灭之刃,间谍过家家,路人女主,爱乐之城";

	public string BbDownPath { get; set; } = "";

	public List<SavedTrack> RecentPlayed { get; set; } = new List<SavedTrack>();

	public List<SavedTrack> Favorites { get; set; } = new List<SavedTrack>();
}

public static class SettingsManager
{
	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "settings.json");

	private static AppSettings? _instance;

	public static AppSettings Instance => _instance ??= Load();

	public static AppSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
				if (settings != null)
				{
					if (settings.Fx == null) settings.Fx = new FxSettings();
					if (settings.Fx.EqGains == null || settings.Fx.EqGains.Length != 10)
						settings.Fx.EqGains = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
					if (settings.RecentPlayed == null) settings.RecentPlayed = new List<SavedTrack>();
					if (settings.Favorites == null) settings.Favorites = new List<SavedTrack>();
					if (settings.DownloadCache == null) settings.DownloadCache = new Dictionary<string, string>();
					if (settings.OnlineEngines == null || settings.OnlineEngines.Count == 0)
						settings.OnlineEngines = new List<string> { "netease", "kugou", "bili", "qq" };
					if (settings.Hotkeys == null || settings.Hotkeys.Count == 0)
						settings.Hotkeys = new Dictionary<string, string>
						{
							["next"] = "Ctrl+Alt+Right",
							["prev"] = "Ctrl+Alt+Left",
							["playpause"] = "Ctrl+Alt+Space",
							["fav"] = "Ctrl+Alt+F",
							["toggle"] = "Ctrl+Alt+W",
							["lyric_passthrough"] = "Ctrl+Alt+L"
						};
					// 已有用户的 Hotkeys 字典里没有新加的键（上面那个整表替换只在
					// 字典为空时才走）。缺键 = 没绑，不给默认值的话这个功能就没有
					// 任何入口，而它是关掉鼠标穿透后唯一的救命通道。
					if (!settings.Hotkeys.ContainsKey("lyric_passthrough"))
						settings.Hotkeys["lyric_passthrough"] = "Ctrl+Alt+L";
					if (!settings.Hotkeys.ContainsKey("toggle"))
						settings.Hotkeys["toggle"] = "Ctrl+Alt+W";
				// 旧版本默认是 ask，点 X 会直接退进程。老配置迁到 tray，
				// 用户自己在设置里选过 exit 的不动。
				if (string.IsNullOrWhiteSpace(settings.CloseBehavior) || settings.CloseBehavior == "ask")
					settings.CloseBehavior = "tray";
				// 背景摆动同理：旧默认是 true，用户没亲手点过就当没选过，直接关掉。
				// 点过开关的（BackgroundSwingChosen=true）保留他的选择，不强制覆盖。
				if (!settings.BackgroundSwingChosen)
					settings.BackgroundSwing = false;
				// 字族是「按机器」的值：换台电脑、字体没装，存下来的家族名就无效。
				// 无效时 WPF 会静默退回默认字体，看起来像「设置没生效」，所以这里
				// 先探一下本机装没装，没装就换回默认并落盘。
				if (string.IsNullOrWhiteSpace(settings.LyricFontFamily)
					|| !LyricFonts.IsAvailable(settings.LyricFontFamily))
				{
					settings.LyricFontFamily = LyricFonts.Default;
				}
				// 歌词色同理是「按机器」的值：存的不是索引而是色值，
				// 但手改过 json 或抄了别的机器的值就可能不是合法颜色。
				// 解析不出来就当没设，退回跟随主题——不能因为一个坏色值
				// 让整首歌词窗口的字变成透明的。
				if (!string.IsNullOrWhiteSpace(settings.LyricFontColor)
					&& !IsHexColor(settings.LyricFontColor))
				{
					settings.LyricFontColor = "";
				}
				// 动画模板名同理：文件夹可能已经被删了/挪了。
				// Resolve 找不到就回默认，这里不强制改写用户的名字，
				// 免得他把文件夹拿回来时设置被抹掉。
				return settings;
				}
			}
		}
		catch
		{
		}
		return new AppSettings();
	}

	/// <summary>#RGB / #RRGGBB / #AARRGGBB 都能算合法。</summary>
	internal static bool IsHexColor(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string s = text.Trim();
		if (!s.StartsWith("#", StringComparison.Ordinal))
		{
			return false;
		}
		s = s[1..];
		if (s.Length is not (3 or 6 or 8))
		{
			return false;
		}
		foreach (char c in s)
		{
			if (!Uri.IsHexDigit(c))
			{
				return false;
			}
		}
		return true;
	}

	public static void Save(AppSettings settings)
	{
		_instance = settings;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch
		{
		}
	}
}
