using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using MusicBox.Services;

namespace MusicBox.Pages;

public partial class AboutPage : UserControl, IMusicPage
{
	/// <summary>
	/// 致谢清单按四组归类。原则：只列本项目真正用到/真正参考过的项目与公开资料，
	/// 名字、协议、链接都从代码注释与公开仓库核实，不把「听说过」的项目凑进来。
	/// Group 对应 Loc 键 about_group_{deps|api|soda}，切换界面语言时标题跟着变。
	/// </summary>
	private const string GroupDeps = "deps";
	private const string GroupApi = "api";
	private const string GroupSoda = "soda";

	private static readonly (string Group, string Name, string License, string Desc, string Url)[] Credits =
	{
		// ── 直接依赖：NuGet 包、随程序附带的工具、运行时组件 ──
		(GroupDeps, "NAudio 2.2.1", "MIT",
			"音频解码、WasapiOut 输出、音量与音效链（模拟 Hifi 环绕）",
			"https://github.com/naudio/NAudio"),
		(GroupDeps, "QRCoder 1.8.1", "MIT",
			"酷狗扫码登录窗口里的二维码渲染",
			"https://github.com/codebude/QRCoder"),
		(GroupDeps, "Microsoft.Web.WebView2", "微软组件",
			"内置浏览器网页登录与各平台 Cookie 抓取（非开源运行时）",
			"https://developer.microsoft.com/microsoft-edge/webview2/"),
		(GroupDeps, "BBDown (nilaoda)", "MIT",
			"B 站视频/音频下载，随程序附带并以独立进程运行",
			"https://github.com/nilaoda/BBDown"),
		(GroupDeps, ".NET 8 / WPF", "MIT",
			"应用程序框架、界面与 WinRT/SMTC 互操作",
			"https://github.com/dotnet/wpf"),
		(GroupDeps, "LRCLIB", "社区服务",
			"公开歌词库，提供歌词与翻译（与网易云词源互为兜底）",
			"https://lrclib.org"),

		// ── 接口与实现参考：各平台协议的开源实现 ──
		(GroupApi, "Binaryify/NeteaseCloudMusicApi", "MIT",
			"网易云 eapi / weapi 加密与账号歌单接口思路",
			"https://github.com/Binaryify/NeteaseCloudMusicApi"),
		(GroupApi, "MakcRe/KuGouMusicApi", "MIT",
			"酷狗 Android / Web 双签名、扫码登录、云歌单与曲目接口",
			"https://github.com/MakcRe/KuGouMusicApi"),
		(GroupApi, "lx-music-desktop (lyswhut)", "Apache-2.0",
			"QQ 音乐 zzc 签名（tx/utils/crypto.js）、多源聚合与去重思路",
			"https://github.com/lyswhut/lx-music-desktop"),
		(GroupApi, "@jixun/qmweb-sign · jixun.uk 分析", "社区实现",
			"QQ musicu.fcg 签名算法的交叉验证与推导",
			"https://jixun.uk/posts/2024/qqmusic-zzc-sign/"),
		(GroupApi, "MhyunBox", "本地项目",
			"媒体卡显示、界面与背景风格参考（非公开仓库）",
			""),

		// ── 汽水音乐专项：代码里直接引用 + 社区公开实现 ──
		(GroupSoda, "ly-music-source", "GPL-3.0",
			"汽水音乐 /luna/pc 端点与 pcQuery 公共参数模板（代码注释中直接引用）",
			"https://www.npmjs.com/search?q=ly-music-source"),
		(GroupSoda, "guohuiyuan/music-lib", "AGPL-3.0",
			"汽水音乐客户端实现上游，社区音源与取流逻辑参考",
			"https://github.com/guohuiyuan/music-lib"),
		(GroupSoda, "520Qiuyu/qishuiMusicAnalysis", "社区开源",
			"分享页 _ROUTER_DATA 的提取流程（HTML → 内嵌 JSON 载荷）",
			"https://github.com/520Qiuyu/qishuiMusicAnalysis"),
		(GroupSoda, "YaKun9/media-parser", "公开文档",
			"汽水音乐逆向解析指南：audioWithLyricsOption 字段、VIP 试听片段判定",
			"https://github.com/YaKun9/media-parser/blob/main/docs/parsers/qsmusic.md"),
		(GroupSoda, "Laine127/libresoda", "公开文档",
			"整曲取流 x-helios / x-medusa 签名来源分析（说明为何改走分享页直链）",
			"https://github.com/Laine127/libresoda/blob/main/docs/FULL-QUALITY-STREAM.md"),
	};

	/// <summary>社区公开原理：不是代码依赖，而是各平台协议与系统行为的公开整理，
	/// 本程序据此实现。每条尽量写清「做了什么」，避免只堆名词。</summary>
	private static readonly (string Name, string Desc, string Url)[] Principles =
	{
		("汽水音乐 · 分享页免登录取流",
			"分享落地页 music.douyin.com/qishui/share/track 由服务端渲染，把 _ROUTER_DATA 注入页面；其中 loaderData.track_page.audioWithLyricsOption.url 是 audio_mp4 直链，无需 Cookie 与设备签名。VIP 曲目只下发 30~60 秒片段，用 offsetDuration < duration × 0.9 判定试听。",
			"https://github.com/YaKun9/media-parser/blob/main/docs/parsers/qsmusic.md"),
		("汽水音乐 · 业务端点与会话域",
			"api.qishui.com 的 /luna/pc/search/all 等端点使用 aid=386088 与 LunaPC UA；账号类接口只认汽水 PC 客户端签发的会话，浏览器导出的抖音 web 域 Cookie 会返回 1000016「登录状态已失效」。",
			"https://www.npmjs.com/search?q=ly-music-source"),
		("网易云音乐 · eapi 加密",
			"请求体经 AES-ECB 加密并附 MD5 摘要后走 /eapi/*；账号歌单先由 /eapi/w/nuser/account/get 取得 uid，再按 uid 拉歌单与曲目。",
			"https://github.com/Binaryify/NeteaseCloudMusicApi"),
		("QQ 音乐 · musicu.fcg 与 zzc 签名",
			"网关请求需签名：对 body 取 SHA1 → 按两组固定索引挑 8 个字符 → 与 20 个混淆常量异或 → base64 后剥掉 /+=，拼成 zzc... 附在查询参数上；JS 版索引 40 越界产生 undefined 的行为也需原样复刻。",
			"https://jixun.uk/posts/2024/qqmusic-zzc-sign/"),
		("酷狗音乐 · 网关签名与扫码登录",
			"Android 签名 = MD5(盐 + 按 key 排序的 k=v 串 + 请求体 + 盐)；登录态接口使用 appid=1005 / clientver=20489。扫码走 login-user.kugou.com/v2/qrcode 签发，再轮询 /v2/get_userinfo_qrcode 拿 userid + token。",
			"https://github.com/MakcRe/KuGouMusicApi"),
		("酷狗音乐 · 云歌单接口",
			"gateway.kugou.com 的 /v7/get_all_list 返回 data.info 歌单，/pubsongs/v2/get_other_list_file_nofilt 返回 data.songs 曲目；旧的 pubsongs.kugou.com 直连已失效。",
			""),
		("B 站 · 下载链路",
			"BBDown 作为独立进程运行，登录 Cookie 只写本机 BBDown.data；音视频分离下载后再入库。",
			"https://github.com/nilaoda/BBDown"),
		("Windows · SMTC 媒体卡与 AUMID",
			"非打包 exe 没有显式 AppUserModelID 时媒体卡显示「未知应用」；需 SetCurrentProcessExplicitAppUserModelID + 开始菜单快捷方式的 System.AppUserModel.ID 属性 + 注册表 DisplayName/IconUri 三者对齐。无封面时系统会拿应用图标补位，因此改用全透明缩略图占位（Electron 社区同款问题，见 Obsidian 论坛 #119966）。",
			"https://learn.microsoft.com/windows/win32/shell/appids"),
		("WPF · 浮层与滑条交互",
			"Popup 采用 StaysOpen=True 并自行判定外部点击，避免它自带的鼠标捕获与 Slider 拖拽抢同一份 CaptureMouse；滑条落点由模板 PART_Track 与 Thumb 半宽反算；窗口位置变化时收起浮层，防止独立 HWND 与主窗口脱离。",
			"https://stackoverflow.com/q/9724766"),
		("播放 · 音画同步与曲末推进",
			"音频（NAudio）与背景视频（MediaElement）各跑各的时钟，用非对称容差 + 追赶提前量抵消解码渲染延迟；WasapiOut 读完最后一个缓冲会自行 Stop，因此按 reader.Position 到达 Length 判定曲末并推进下一首，而不是依赖播放停止事件。",
			""),
	};

	public AboutPage()
	{
		InitializeComponent();
		TryLoadIcon();
		BuildCredits();
		BuildPrinciples();
	}

	public void OnNavigatedTo()
	{
		// 版本号从程序集读，XAML 里那个写死的值只是给设计器看的兜底。
		// 真正的版本号只有 csproj 的 <Version> 一个来源，
		// 关于页不自己存一份，免得两处对不上。
		try
		{
			var asm = System.Reflection.Assembly.GetExecutingAssembly();
			var attrs = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
			string? v = (attrs.Length > 0 ? attrs[0] as System.Reflection.AssemblyInformationalVersionAttribute : null)?.InformationalVersion
				?? asm.GetName().Version?.ToString(3);
			if (!string.IsNullOrWhiteSpace(v))
			{
				// InformationalVersion 常带 "+<commit>"，只留前三段
				int plus = v.IndexOf('+');
				VersionText.Text = "v" + (plus > 0 ? v[..plus] : v);
			}
		}
		catch
		{
		}
	}

	private void BuildCredits()
	{
		CreditsPanel.Children.Clear();
		string? group = null;
		foreach (var c in Credits)
		{
			if (c.Group != group)
			{
				group = c.Group;
				CreditsPanel.Children.Add(new TextBlock
				{
					Text = Loc.Default["about_group_" + group],
					FontSize = 13,
					FontWeight = FontWeights.SemiBold,
					Foreground = ThemeManager.Accent(),
					Margin = new Thickness(0, 6, 0, 10)
				});
			}
			CreditsPanel.Children.Add(BuildEntry(c.Name, c.License, c.Desc, c.Url, showTag: true));
		}
	}

	private void BuildPrinciples()
	{
		PrinciplesPanel.Children.Clear();
		foreach (var p in Principles)
		{
			PrinciplesPanel.Children.Add(BuildEntry(p.Name, "", p.Desc, p.Url, showTag: false));
		}
	}

	/// <summary>致谢条目与原理条目共用一套排版：名称 + 可选标签 + 说明 + 可选链接。</summary>
	private FrameworkElement BuildEntry(string name, string tag, string desc, string url, bool showTag)
	{
		var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
		var header = new StackPanel { Orientation = Orientation.Horizontal };
		header.Children.Add(new TextBlock
		{
			Text = name,
			FontSize = 13.5,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Primary()
		});
		if (showTag && tag.Length > 0)
		{
			header.Children.Add(new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x3C, 0x8C, 0xFF)),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(6, 1, 6, 1),
				Margin = new Thickness(8, 0, 0, 0),
				VerticalAlignment = VerticalAlignment.Center,
				Child = new TextBlock
				{
					Text = tag,
					FontSize = 11,
					Foreground = ThemeManager.Accent()
				}
			});
		}
		panel.Children.Add(header);
		panel.Children.Add(new TextBlock
		{
			Text = desc,
			FontSize = 12.5,
			Foreground = ThemeManager.Secondary(),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 3, 0, 0)
		});
		if (url.Length > 0)
		{
			var link = new Hyperlink(new Run(url))
			{
				FontSize = 12,
				Foreground = ThemeManager.Accent(),
				ToolTip = url
			};
			link.RequestNavigate += OnLinkNavigate;
			panel.Children.Add(new TextBlock { Inlines = { link }, Margin = new Thickness(0, 2, 0, 0) });
		}
		return panel;
	}

	private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
	{
		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = e.Uri.AbsoluteUri,
				UseShellExecute = true
			});
			e.Handled = true;
		}
		catch
		{
		}
	}

	private void TryLoadIcon()
	{
		try
		{
			string path = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.png");
			if (File.Exists(path))
			{
				var bi = new BitmapImage();
				bi.BeginInit();
				bi.UriSource = new Uri(path);
				// 高 DPI 缩放大图时用高质量缩放，只限宽不限高，保持原始宽高比不被压缩
				bi.DecodePixelWidth = 152;
				bi.CacheOption = BitmapCacheOption.OnLoad;
				bi.EndInit();
				bi.Freeze();
				AppIcon.Source = bi;
			}
		}
		catch
		{
		}
	}
}
