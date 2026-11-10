using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

	/// <summary>最近一次播放时间（仅「最近播放」列表用）。默认值 default
	/// 表示老版本写入的记录没有时间戳 —— 按保留天数清理时把它当超期处理，
	/// 否则这些化石永远清不掉。</summary>
	public DateTime PlayedAt { get; set; }

	public string Key => Type == "local" ? "local:" + Path : Type + ":" + Id;

	public override string ToString() => Title;
}

/// <summary>播放上下文：启动自动播放用它找回「上次播放时所在的列表」。
/// <see cref="Page"/> = 来源页（library=音乐库-本地 / fav=我的收藏 /
/// recent=最近播放 / online=搜索音乐与在线列表）；<see cref="ListKey"/> =
/// 该页列表标识（本地固定 "local"、收藏/最近为 "fav"/"recent"、在线为
/// "playlist:&lt;平台&gt;:&lt;歌单id&gt;" / "search:&lt;引擎&gt;:&lt;关键词&gt;" /
/// "libfav"（音乐库收藏夹视图），空串 = 无法定位、恢复时静默跳过）。</summary>
public sealed class PlayContext
{
	public string Page { get; set; } = "";

	public string ListKey { get; set; } = "";
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

	/// <summary>背景模糊半径（0~60）。主界面与沉浸页共用这一个数，
	/// 沉浸页再有 FsBgBlur 总开关。0 = 不模糊（主界面默认保持原样）。</summary>
	public double BackgroundBlurAmount { get; set; } = 30.0;

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

	/// <summary>启动时自动播放（随机）：默认关。开着时进程冷启动会按
	/// <see cref="LastPlayContext"/> 找回上次播放所在的列表，从里面随机挑一首开播；
	/// 与 <see cref="AutoPlayOnStartup"/>（续播上一首）互斥，开着时后者让位，
	/// 避免两路同时开播。</summary>
	public bool StartupAutoPlay { get; set; } = false;

	/// <summary>最近一次成功开播的播放上下文。播放链路在切歌成功时写入，
	/// 「启动时自动播放」用它恢复列表数据源。没有任何记录或无法定位列表时为空。</summary>
	public PlayContext? LastPlayContext { get; set; }

	/// <summary>点标题栏 X 之后干什么：tray = 收进托盘（默认），ask = 收进托盘并在托盘上提示，
	/// exit = 直接退出。用户要的是「默认关掉还留着程序」，所以默认值给 tray。</summary>
	public string CloseBehavior { get; set; } = "tray";

	public bool ShowStartupAnimation { get; set; } = true;

	public bool AutoStartOnBoot { get; set; } = false;

	public bool DebugMode { get; set; } = false;

	[JsonConverter(typeof(ProtectedCookieConverter))]
	public string NeteaseCookie { get; set; } = "";

	public string NeteaseUid { get; set; } = "";

	/// <summary>网易云账号昵称。随 NeteaseCookie 一起持久化：收藏夹收缩栏
	/// 标题行要展示账号名，不能每次重建面板都去打账号接口。</summary>
	public string NeteaseNick { get; set; } = "";

	[JsonConverter(typeof(ProtectedCookieConverter))]
	public string BiliCookie { get; set; } = "";

	[JsonConverter(typeof(ProtectedCookieConverter))]
	public string QqCookie { get; set; } = "";

	public string QqUin { get; set; } = "";

	/// <summary>QQ 音乐账号名（昵称，拿不到时退 uin）。随 QqCookie 一起持久化：
	/// 收藏夹收缩栏标题行要展示账号名，不能每次重建面板都去打账号接口。</summary>
	public string QqNick { get; set; } = "";

	/// <summary>B站账号名。收藏夹收缩栏标题行展示用：账号页拉到一次就持久化，
	/// 之后每次重建面板读设置，不再打账号接口。</summary>
	public string BiliNick { get; set; } = "";

	[JsonConverter(typeof(ProtectedCookieConverter))]
	public string KugouCookie { get; set; } = "";

	public string KugouNick { get; set; } = "";

	/// <summary>汽水音乐（抖音账号体系）的会话 Cookie。含 sessionid 等字段；
	/// 仅用于会话校验与后续接口，签名头暂未实现。</summary>
	[JsonConverter(typeof(ProtectedCookieConverter))]
	public string SodaCookie { get; set; } = "";

	public string SodaNick { get; set; } = "";

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
		["toggle"] = "Ctrl+Alt+M",
		["lyric_passthrough"] = "Ctrl+Alt+L",
		["lyrics_toggle"] = "Ctrl+Alt+W"
	};

	/// <summary>程序内快捷键（Q/E/1/2/3）。跟 <see cref="Hotkeys"/> 分开存：
	/// 那份是全局键（带 Ctrl/Alt，走 RegisterHotKey），这份是裸键，
	/// 只有本窗口有焦点才生效，机制不一样，不能混在一张表里——
	/// 混了以后 SettingsPage 那边的改键逻辑会分不清该不该调 RegisterAll。
	/// 键名用 Key 枚举名（Q/E/D1/D2/D3），见 <see cref="Services.LocalHotkeys"/>。</summary>
	public Dictionary<string, string> LocalHotkeys { get; set; } = new Dictionary<string, string>
	{
		["list_prev"] = "Q",
		["list_next"] = "E",
		["prev"] = "D1",
		["next"] = "D2",
		["playpause"] = "D3"
	};

	public FxSettings Fx { get; set; } = new FxSettings();

	public bool OnlineEnabled { get; set; } = true;

	/// <summary>删本地音乐之前要不要弹确认框。用户勾过「不再提醒」就置 false。
	/// 单独一个字段而不是塞在某个弹窗的配置文件里：删除是唯一会动磁盘的动作，
	/// 这个开关必须显眼、能被翻回来。</summary>
	public bool ConfirmTrackDelete { get; set; } = true;

	public int OnlineSearchLimit { get; set; } = 30;

	public List<string> OnlineEngines { get; set; } = new List<string> { "netease", "kugou", "bili", "qq" };

	/// <summary>音乐库「收藏夹」平台分组的显示顺序。
	/// 初始顺序 = 账号页里各平台第一次「加载收藏夹」成功的先后
	/// （AccountPage 成功加载时把该平台追加到末尾，已存在就不动）；
	/// 之后用户在音乐库拖动排序的落盘值也存这份列表。
	/// 没进列表的平台（比如从没加载成功过的）排在最后，按默认顺序。</summary>
	public List<string> LibraryPlatformOrder { get; set; } = new List<string>();
	/// <summary>LibraryPlatformOrder 是否出自用户手动拖动。只有拖动落盘才置 true；
	/// 未经拖动时收藏夹分组按「歌单数从少到多」排初始顺序，这份列表不生效
	///（它可能残留着旧版「按账号加载先后」自动写入的值，不能当作用户意图）。</summary>
	public bool LibraryPlatformOrderIsManual { get; set; } = false;

	/// <summary>B 站结果是否按播放量从高到低排。只有 B 站有这个字段，
	/// 其它平台不受影响。默认开。</summary>
	public bool OnlineSortByPlay { get; set; } = true;

	// 「音乐库是否记住搜索关键词」的开关已删：音乐库筛选行为固定为
	// 不保留——跳去别的页面再回来一律清空筛选（对应 UI 上的
	// 「保留关键词」勾选框已移除）。旧配置文件里遗留的
	// LibraryKeepFilter 字段会被反序列化器忽略，无需迁移。

	/// <summary>主动探测 VIP 音乐（默认关）。
	/// 关：只在真的点播放、实测被平台拒绝时才记「受限」并打 VIP 标签 ——
	/// 不为列表里可能一辈子不会点的歌提前烧网络请求。
	/// 开：搜索结果 / 收藏夹列表加载完后，后台逐条实测（拿一次直链，
	/// 并发 3、当前页优先），能播的把误标的标签撤掉、不能播的提前打上。
	/// 探测用的判定和点播放是同一套（TrackAccess 实测结论），只是时机提前。</summary>
	public bool OnlineVipProbe { get; set; } = false;

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

	/// <summary>歌词窗「已经唱过的那一段」的颜色（#RRGGBB）。空串 = 跟随主题。
	///
	/// 横排当前句是两层叠出来的：底层整句铺满，是「还没唱到」；
	/// 上层只露出擦除进度之前的一段，那一段就是这个「已唱色」。
	/// 单独给一个设置，是因为绝大多数歌的已唱色就该比未唱色亮，
	/// 而用户偶尔会想把已唱的那段压暗（暗色壁纸上的浅色歌词已经不刺眼了，
	/// 这时反过来把未唱那段提亮更舒服）。
	///
	/// 和 <see cref="LyricFontColor"/> 一样存色值而不是索引：换主题不清空它。</summary>
	public string LyricSungColor { get; set; } = "";

	/// <summary>译文行的颜色（#RRGGBB）。空串 = 跟随主题（译文用次级灰）。
	///
	/// 译文夹在当前句和下一句之间，默认次级灰是刻意压低的，但用户经常想
	/// 把译文单独调亮或换个色系，跟主歌词分层阅读。和另两个色一样存色值
	/// 不存索引：换主题不清空它。</summary>
	public string LyricTranslateColor { get; set; } = "";

	/// <summary>选中的歌词动画模板名。存名字不存索引——内置模板改名/加项时
	/// 老设置不会因为下标错位而指到别的动画上。取不到就回
	/// <see cref="LyricAnimTemplates.Default"/>。</summary>
	public string LyricAnimTemplate { get; set; } = "";

	/// <summary>自定义歌词动画模板文件夹。空 = 只用内置那两套。</summary>
	public string LyricAnimTemplateFolder { get; set; } = "";

	// ── 界面效果开关（设置页「界面效果」卡）。全部默认开：
	// 动效是这个播放器的卖点，关掉是用户的主动选择，不是默认行为。 ──

	/// <summary>全屏播放页的进场动画（0.965→1 缩放 + 淡入）。</summary>
	public bool FsEntranceAnim { get; set; } = true;

	/// <summary>播放时全屏页的黑胶唱片旋转。</summary>
	public bool FsDiscSpin { get; set; } = true;

	/// <summary>沉浸页显示黑胶唱片（右下角面板里可关：关掉后只留居中歌词）。</summary>
	public bool FsShowDisc { get; set; } = true;

	/// <summary>沉浸页歌词列表形态时当前句钉在视口正中（关 = 让出底部渐隐带）。</summary>
	public bool FsLyricCenter { get; set; } = true;

	/// <summary>沉浸页隐藏字幕切句动画：换句直接落位不播过渡。</summary>
	public bool FsHideAnim { get; set; } = false;

	/// <summary>沉浸页背景加载主程序的背景图片（高清/模糊开关见下面两个）。</summary>
	public bool FsBgUseMain { get; set; } = true;

	/// <summary>沉浸页背景模糊（模糊时用 720 解码 + BlurEffect，肉眼就是磨砂玻璃）。</summary>
	public bool FsBgBlur { get; set; } = true;

	/// <summary>沉浸页背景高清（1920 解码、不模糊）。和模糊互斥，优先于模糊。</summary>
	public bool FsBgHd { get; set; } = false;

	/// <summary>沉浸页底部波浪的模式：volume（音量电平）/ fft（频谱）/ off（关）。</summary>
	public string WaveMode { get; set; } = "volume";

	/// <summary>歌词跟随的滚动动效：平滑滚动插值 + 新当前句的冒头浮现。
	/// 关掉后换行直接落位（瞬移），适合想要零动画的场景。</summary>
	public bool LyricScrollAnim { get; set; } = true;

	/// <summary>滚动动画时长（毫秒）。滚动本身不再是可调项：设置页那个滑块
	/// 改的是颜色渲染速度（见 <see cref="LyricRevealMs"/>），这里固定一档就够用。</summary>
	public const int LyricScrollMs = 420;

	/// <summary>程序内（沉浸式全屏页）的歌词改用字幕形态展示：
	/// 当前句居中大字、上一句/下一句淡显、译文单独一行，
	/// 切句动画跟随 <see cref="LyricAnimTemplate"/> 选的模板。
	/// 默认开：字幕形态是用户拍板的沉浸页形态，列表式滚动彻底退场。</summary>
	public bool LyricInAppSubtitleAnim { get; set; } = true;

	/// <summary>卡拉OK歌词「颜色渲染」速度：颜色扫过整句的最长用时（毫秒）。
	/// 正常句仍跟演唱匀速；只有整句时长超过这个值的长句/句间空档才提前刷满。
	/// 值越小刷得越快。默认 3600 = 原来的「过长才加成」档。
	/// 见 <see cref="Services.Lyrics.ProgressOf"/>。</summary>
	public int LyricRevealMs { get; set; } = 3600;

	/// <summary>搜索音乐页的爱好提示词（逗号分隔），打开时随机取一个自动搜索。</summary>
	public string HobbyKeywords { get; set; } =
		"二次元,东方Project,原神,崩坏,明日方舟,赛马娘,孤独摇滚,葬送的芙莉莲,轻音少女,吹响吧上低音号,魔法少女小圆,初音未来,VOCALOID,极乐净土,宅舞曲,动漫原声,日漫主题曲,二创,RED,车万歌,音乐节Live,催泪神曲,翻唱,鬼灭之刃,间谍过家家,路人女主,爱乐之城";

	/// <summary>搜索建议优先尝试的平台："netease" / "qq" / "kugou"。
	/// 空 = 保持默认顺序（网易云 → QQ → 酷狗）。只决定尝试顺序，
	/// 「第一个源没结果就落到下一个」的兜底语义不变。</summary>
	public string FirstSearchPlatform { get; set; } = "";

	public string BbDownPath { get; set; } = "";

	/// <summary>播放定位小球是否显示（单击跳到正在播放的歌，长按拖动挪位置）。
	/// 按产品决策恒开：设置页入口已撤，Load 里强制覆盖存储值，
	/// 保留字段仅为兼容旧配置，行为代码不再判断。</summary>
	public bool LocateBallEnabled { get; set; } = true;

	/// <summary>界面音效（合成音，无音频文件）：定位球惯性滑行被窗口边夹停时
	/// 一声轻「嗒」、单击定位球定位成功时一声更轻的确认音。
	/// 按产品决策恒开：设置页入口已撤，Load 里强制覆盖存储值，
	/// 保留字段仅为兼容旧配置，行为代码不再判断。</summary>
	public bool UiSfxEnabled { get; set; } = true;

	/// <summary>最小化时调用 EmptyWorkingSet 把工作集换出物理内存。
	/// 按产品决策恒开：设置页入口已撤，Load 里强制覆盖存储值，
	/// 保留字段仅为兼容旧配置，行为代码不再判断。</summary>
	public bool MinimizeToWorkingSet { get; set; } = true;

	/// <summary>内存占用超过阈值时自动释放一次（封面内存缓存 → GC 压缩 →
	/// 工作集换出，与底栏释放键同一套实现）。默认开：常挂后台听歌时
	/// 托管堆只涨不还，靠用户记着手动点释放不现实。</summary>
	public bool AutoMemRelease { get; set; } = true;

	/// <summary>自动释放阈值（MB）。默认 150，下限也是 150：再低会贴着
	/// 程序空载基线反复触发，每次清封面缓存都要重解码，反而更费。</summary>
	public int AutoMemReleaseMb { get; set; } = 150;

	/// <summary>定位小球上次停放的位置（窗口内左上角坐标）。
	/// NaN = 用户还没挪过，第一次启动给默认位置；每次松手落位后都写回。</summary>
	public double LocateBallX { get; set; } = double.NaN;

	public double LocateBallY { get; set; } = double.NaN;

	public List<SavedTrack> RecentPlayed { get; set; } = new List<SavedTrack>();

	/// <summary>最近播放保留天数：0 = 永久，1~365 天，默认 30。
	/// 只在设置页点「清理」时按天清（写入路径只按条数裁剪，见 RecentMaxCount）。</summary>
	public int RecentKeepDays { get; set; } = 30;

	/// <summary>最近播放最多同时保留多少条，写入时超出即裁掉最旧的。
	/// 0 = 不限制。默认 1000：几千条 SavedTrack 序列化进 settings.json
	/// 会明显拖慢每次播放后的落盘，也拖慢最近播放页的筛选。</summary>
	public int RecentMaxCount { get; set; } = 1000;

	/// <summary>封面显示总开关（默认开）。关掉后**一切封面位**都只显示
	/// 纯色占位：列表行、播放条封面、SMTC 缩略图、沉浸页唱片、歌单卡片等
	/// 全部短路，网络封面请求全停（磁盘缓存保留，开回来不用重新下载）。
	/// 这是要看「关掉后的实际内存底线」用的闸，不再只是列表行的开关。</summary>
	public bool ShowListCovers { get; set; } = true;

	/// <summary>最近播放页被关掉的平台勾选（逗号分隔的引擎名，如 "qq,kugou"）。
	/// 空 = 全部平台都显示。存「被关掉的那些」而不是「被选中的那些」：
	/// 以后新增平台时老配置不至于把新平台一并藏掉。</summary>
	public string RecentPlatformsOff { get; set; } = "";

	public List<SavedTrack> Favorites { get; set; } = new List<SavedTrack>();
}

public static class SettingsManager
{
	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"MusicBox", "settings.json");

	private static AppSettings? _instance;

	/// <summary>设置落盘的互斥锁。Save 会在播放起播、歌词拖动、设置页保存等
	/// 多个入口被调用（且可能跨线程），没有锁时两次写会互相截断文件。</summary>
	private static readonly object SaveLock = new object();

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
							["toggle"] = "Ctrl+Alt+M",
							["lyric_passthrough"] = "Ctrl+Alt+L",
							["lyrics_toggle"] = "Ctrl+Alt+W"
						};
					// 已有用户的 Hotkeys 字典里没有新加的键（上面那个整表替换只在
					// 字典为空时才走）。缺键 = 没绑，不给默认值的话这个功能就没有
					// 任何入口，而它是关掉鼠标穿透后唯一的救命通道。
					if (!settings.Hotkeys.ContainsKey("lyric_passthrough"))
						settings.Hotkeys["lyric_passthrough"] = "Ctrl+Alt+L";
if (!settings.Hotkeys.ContainsKey("toggle"))
					settings.Hotkeys["toggle"] = "Ctrl+Alt+M";
				// 「切换歌词」是新动作，默认拿走了 Ctrl+Alt+W；老配置里 toggle
				// 还存着旧默认 Ctrl+Alt+W 的话会跟它撞车（后注册的那个会失败）。
				// 只搬「没改过默认值」的用户：自己改过键的不动。
				if (string.Equals(settings.Hotkeys["toggle"], "Ctrl+Alt+W", StringComparison.OrdinalIgnoreCase))
					settings.Hotkeys["toggle"] = "Ctrl+Alt+M";
				// 歌词开关热键：老配置里没有这一项，补上默认值。
				if (!settings.Hotkeys.ContainsKey("lyrics_toggle"))
					settings.Hotkeys["lyrics_toggle"] = "Ctrl+Alt+W";
				// 局内键是后加的，老配置里整个字典都没有。空着不补的话
				// 界面上「Q/E/1/2/3」这几行会显示成默认值但一个都不生效。
				// 返回值必须接回来：老配置反序列化成 null 时 WithDefaults 是新建了一个
				// 字典，接不住的话这个新字典当场就丢了，等于没补。
				settings.LocalHotkeys = Services.LocalHotkeys.WithDefaults(settings.LocalHotkeys);
				// 旧版本默认是 ask，点 X 会直接退进程。老配置迁到 tray，
				// 用户自己在设置里选过 exit 的不动。
				if (string.IsNullOrWhiteSpace(settings.CloseBehavior) || settings.CloseBehavior == "ask")
					settings.CloseBehavior = "tray";
				// 背景摆动的两个开关（轻微摆动 / 摆动幅度）从设置里撤了，界面上
				// 再也没有入口，留着旧值只会让人以为「我明明关了它怎么还在动」。
				// 这里无条件关掉——摆动本来就是默认行为之外的锦上添花，
				// 代码路径还在，以后想加回界面把这里的强制关删掉即可。
				settings.BackgroundSwing = false;
				settings.BackgroundSwingChosen = true;
				// 启动动画和程序内字幕动画的两个选项已从设置页撤掉（界面效果归零
				// 为「默认都开」，用户要求的动作也只剩这一种）。老配置里存过 false
				// 的必须在这兜住，否则删了开关之后那台机器上动画就永远回不来了。
				settings.ShowStartupAnimation = true;
				settings.LyricInAppSubtitleAnim = true;
				// 「界面音效 / 播放定位小球 / 最小化释放物理内存」三个开关已按产品决策
				// 恒开，设置页不再提供入口。保留字段只为兼容旧配置（反序列化不报错），
				// 存储值一律忽略：老配置里存过 false 的也按 true 走，相关功能代码
				// 不再判断开关。以后想加回界面，把这里的强制覆盖删掉即可。
				settings.UiSfxEnabled = true;
				settings.LocateBallEnabled = true;
				settings.MinimizeToWorkingSet = true;
				// 自动释放阈值来自用户输入/旧配置，手改 json 可能写成 0 或负数：
				// 那会变成「每一拍都释放」，程序等于一直在 GC。夹回可用的下限。
				if (settings.AutoMemReleaseMb < 150) settings.AutoMemReleaseMb = 150;
				if (settings.AutoMemReleaseMb > 4096) settings.AutoMemReleaseMb = 4096;
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
				// 已唱色同样清洗，坏值退回跟随主题。
				if (!string.IsNullOrWhiteSpace(settings.LyricSungColor)
					&& !IsHexColor(settings.LyricSungColor))
				{
					settings.LyricSungColor = "";
				}
				// 翻译色同理。
				if (!string.IsNullOrWhiteSpace(settings.LyricTranslateColor)
					&& !IsHexColor(settings.LyricTranslateColor))
				{
					settings.LyricTranslateColor = "";
				}
				// 动画模板名同理：文件夹可能已经被删了/挪了。
				// Resolve 找不到就回默认，这里不强制改写用户的名字，
				// 免得他把文件夹拿回来时设置被抹掉。
				return settings;
				}
			}
		}
		catch (Exception ex)
		{
			// Load 在 Instance 首次求值时执行；这里只能用 LogError——
			// LogHelper 的 Debug/Info 级会回读 DebugMode 而再次进 Instance，形成递归。
			LogHelper.LogError(ex, "读取设置失败，已回退默认配置");
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
		lock (SaveLock)
		{
			_instance = settings;
			string tmp = FilePath + ".tmp";
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
				// 同目录同卷 rename：要么旧文件完整，要么新文件完整，不会出现
				// 「写到一半进程被杀 → settings.json 半截 JSON → 所有设置回默认」。
				File.Move(tmp, FilePath, overwrite: true);
			}
			catch (Exception ex)
			{
				LogHelper.LogError(ex, "保存设置失败");
				try
				{
					if (File.Exists(tmp))
					{
						File.Delete(tmp);
					}
				}
				catch (Exception cleanupEx)
				{
					LogHelper.LogDebug("清理设置临时文件失败: " + cleanupEx.Message);
				}
			}
		}
	}
}
