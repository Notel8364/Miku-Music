using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

// 和 SettingsPage 一样的别名技巧：别 using System.Windows.Shapes，
// 万一以后这页也要用 System.IO.Path 就会撞名。SetLoginStatus 的签名
// 原样从设置页搬过来，别名也跟着过来，方法体一行不用改。
using Ellipse2 = System.Windows.Shapes.Ellipse;

/// <summary>账号页：各平台登录卡（网易云 / QQ / 酷狗 / B 站 Cookie）
/// 加 BBDown 扫码登录，整块从设置页拆出来，由标题栏「账号」按钮直达。</summary>
public partial class AccountPage : UserControl, IMusicPage
{
	private bool _loggingIn;

	/// <summary>汽水 Cookie 校验的进行中标记。按钮禁用会被布局/焦点恢复提前解开，
	/// 而 async void 事件本身没有重入保护，连点两下会发两次校验请求。</summary>
	private bool _sodaVerifying;

	public AccountPage()
	{
		InitializeComponent();
	}

	public void OnNavigatedTo()
	{
		// 切界面语言后要重跑一遍这些刷新。
		// XAML 里的 {Binding ...Item[...]} 会跟着 Loc.PropertyChanged 自己刷，
		// 代码里 text.Text = Loc.Default[...] 的不会 —— 那是一次性赋值。
		// 所以只切语言不重刷的话，这一页有一半文案还是上一个语言的。
		Loc.Default.LanguageChanged -= OnLanguageChanged;
		Loc.Default.LanguageChanged += OnLanguageChanged;
		UpdateCookieUI();
		RefreshLoginStatus();
		UpdateBiliStatus();
	}

	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		UpdateCookieUI();
		RefreshLoginStatus();
		UpdateBiliStatus();
	}

	private void UpdateCookieUI()
	{
		var s = SettingsManager.Instance;

		NeteaseCookieTitle.Text = Loc.Default["set_netease_title"];
		NeteaseSub.Text = Loc.Default["set_netease_sub"];
		NeteaseCookieBox.Text = s.NeteaseCookie ?? "";
		NeteaseCookieBox.ToolTip = Loc.Default["set_netease_cookie_ph"];
		// 三段式统一文案：保存Cookie/清除 五张卡共用一对键（acct_cookie_*）。
		NeteaseCookieSaveButton.Content = Loc.Default["acct_cookie_save"];
		NeteaseCookieClearButton.Content = Loc.Default["acct_cookie_clear"];
		NeteaseBrowserLoginButton.Content = Loc.Default["set_login_web"];
		NeteaseQrButton.Content = Loc.Default["set_login_qr"];
		NeteaseFetchCookieButton.Content = Loc.Default["set_login_fetch"];
		NeteasePlaylistLoadButton.Content = Loc.Default["acct_pl_load"];

		BiliCookieTitle.Text = Loc.Default["set_bili_cookie_title"];
		BiliCookieSub.Text = Loc.Default["set_bili_cookie_sub"];
		BiliCookieBox.Text = s.BiliCookie ?? "";
		BiliCookieBox.ToolTip = Loc.Default["set_bili_cookie_ph"];
		BiliCookieSaveButton.Content = Loc.Default["set_bili_cookie_save"];
		BiliCookieClearButton.Content = Loc.Default["set_bili_cookie_clear"];
		BiliBrowserLoginButton.Content = Loc.Default["set_login_open"] + " " + Loc.Default["plat_bili"];
		BiliFetchCookieButton.Content = Loc.Default["set_login_fetch"];

		QqCookieTitle.Text = Loc.Default["set_qq_title"];
		QqSub.Text = Loc.Default["set_qq_sub"];
		QqCookieBox.Text = s.QqCookie ?? "";
		QqCookieBox.ToolTip = Loc.Default["set_qq_ph"];
		QqCookieSaveButton.Content = Loc.Default["acct_cookie_save"];
		QqCookieClearButton.Content = Loc.Default["acct_cookie_clear"];
		QqWebLoginButton.Content = Loc.Default["set_login_web"];
		QqFetchCookieButton.Content = Loc.Default["set_login_fetch"];
		QqPlaylistLoadButton.Content = Loc.Default["acct_pl_load"];

		KugouCookieTitle.Text = Loc.Default["set_kugou_title"];
		KugouSub.Text = Loc.Default["set_kugou_sub"];
		KugouNickBox.Text = s.KugouNick ?? "";
		KugouCookieBox.Text = s.KugouCookie ?? "";
		KugouCookieBox.ToolTip = Loc.Default["set_kugou_cookie_ph"];
		KugouCookieSaveButton.Content = Loc.Default["acct_cookie_save"];
		KugouCookieClearButton.Content = Loc.Default["acct_cookie_clear"];
		KugouWebLoginButton.Content = Loc.Default["set_login_web"];
		KugouQrLoginButton.Content = Loc.Default["set_kugou_qr"];
		// 酷狗：扫码登录（userid+token）打通后按钮可用；纯网页 Cookie 仍置灰。
		KugouPlaylistLoadButton.Content = Loc.Default["acct_pl_load"];

		RefreshLoginStatus();
	}

	// ==================== 登录卡片（折叠） ====================

	/// <summary>刷新四张卡的摘要行：圆点 + 「已登录 / 未登录」。
	/// 收起状态下这一行就是全部信息，所以必须准：
	/// Cookie 存没存、QQ 那份是拿浏览器里读的（HasCookie 而不是设置字段，
	/// 因为 QQ 的 Cookie 也可能来自扫码那条路），都按实际可用的那份算。</summary>
	private void RefreshLoginStatus()
	{
		var s = SettingsManager.Instance;
		bool neteaseOn = !string.IsNullOrWhiteSpace(s.NeteaseCookie);
		bool qqOn = QqClient.HasCookie || !string.IsNullOrWhiteSpace(s.QqCookie);
		SetLoginStatus(
			NeteaseStatusDot, NeteaseStatusText,
			neteaseOn);
		SetLoginStatus(
			QqStatusDot, QqStatusText,
			qqOn);
		SetLoginStatus(
			KugouStatusDot, KugouStatusText,
			!string.IsNullOrWhiteSpace(s.KugouCookie));
		SetLoginStatus(
			BiliCookieStatusDot, BiliCookieStatusText,
			!string.IsNullOrWhiteSpace(s.BiliCookie));
		SodaCookieTitle.Text = Loc.Default["set_soda_title"];
		SodaSub.Text = Loc.Default["set_soda_sub"];
		SodaCookieBox.Text = s.SodaCookie ?? "";
		SodaCookieBox.ToolTip = Loc.Default["set_soda_cookie_ph"];
		SodaCookieSaveButton.Content = Loc.Default["acct_cookie_save"];
		SodaCookieClearButton.Content = Loc.Default["acct_cookie_clear"];
		SodaVerifyButton.Content = Loc.Default["set_soda_verify"];
		SodaWebLoginButton.Content = Loc.Default["set_login_web"];
		// 汽水没有收藏夹同步能力：按钮常驻置灰，文案照常填上（XAML 里 IsEnabled=False）。
		SodaPlaylistLoadButton.Content = Loc.Default["acct_pl_load"];
		SetLoginStatus(
			SodaStatusDot, SodaStatusText,
			!string.IsNullOrWhiteSpace(s.SodaCookie));
		// 收藏夹行常驻（网易云 / QQ 两家支持）：未登录只把按钮置灰，不藏整行，
		// 让「有这个能力」在收起前也看得见。登录态没了要把已加载的歌单行
		// 一并清掉：行的点击逻辑依赖 Cookie，留着只会点开就报错。
		UpdatePlaylistSection("netease", neteaseOn);
		UpdatePlaylistSection("qq", qqOn);
		UpdateKugouPlaylistSection();
	}

	/// <summary>酷狗收藏夹行：只有拿到手机域会话（userid+token）才让按钮可点。
	/// 网页 Cookie 拉歌单必被网关拒，登录后把已铺的歌单行清掉，免得点开报错。</summary>
	private void UpdateKugouPlaylistSection()
	{
		bool on = KugouClient.ParseSession(SettingsManager.Instance.KugouCookie) != null;
		KugouPlaylistLoadButton.IsEnabled = on;
		if (!on)
		{
			KugouPlaylistList.Children.Clear();
			KugouPlaylistStatus.Text = "";
		}
	}

	private static void SetLoginStatus(Ellipse2 dot, TextBlock text, bool loggedIn)
	{
		dot.Fill = loggedIn
			? ThemeManager.Accent()
			: ThemeManager.Brush("TextTertiaryBrush", Brushes.Gray);
		text.Text = Loc.Default[loggedIn ? "login_state_on" : "login_state_off"];
		text.Foreground = loggedIn
			? ThemeManager.Primary()
			: ThemeManager.Tertiary();
	}

	// ==================== 收藏夹（网易云 / QQ） ====================

	/// <summary>收藏夹加载中。两个平台共用一个闸：反正请求都发往同两个账号，
	/// 同时拉两份列表除了让风控多看几眼没有任何好处。</summary>
	private bool _loadingPlaylists;

	/// <summary>「加载收藏夹」按钮行的收缩状态（键 = 平台名）。
	/// 点按钮行（按钮以外任意处）收起/展开下方歌单列表。</summary>
	private readonly HashSet<string> _collapsedPlaylistSections = new(StringComparer.Ordinal);

	/// <summary>按钮行收缩头：整条可点。Button 自己会吃掉 MouseLeftButtonDown
	///（ButtonBase 为点击手势标记已处理），所以点「加载收藏夹」按钮不会误触折叠。</summary>
	private void PlaylistSectionHeader_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.Tag is string platform)
		{
			TogglePlaylistSection(platform);
		}
	}

	/// <summary>收起/展开一个平台的歌单列表（MaxHeight 动画 + 箭头转向，
	/// 与音乐库左栏收缩栏共用 TrackRows.SetPanelExpanded）。</summary>
	private void TogglePlaylistSection(string platform)
	{
		bool isNetease = platform == "netease";
		System.Windows.Controls.Panel list = isNetease ? NeteasePlaylistList : QqPlaylistList;
		RotateTransform chevron = isNetease ? NeteasePlaylistChevronRotate : QqPlaylistChevronRotate;
		bool collapse = !_collapsedPlaylistSections.Contains(platform);
		if (collapse)
		{
			_collapsedPlaylistSections.Add(platform);
		}
		else
		{
			_collapsedPlaylistSections.Remove(platform);
		}
		TrackRows.SetPanelExpanded(list, chevron, !collapse);
	}

	/// <summary>状态行文案统一带账号名前缀（「昵称 · 已加载 N 个歌单」）。
	/// 昵称还没拉到过（或平台没登录）就原样返回基础文案。</summary>
	private static string WithAccountName(string platform, string baseText)
	{
		string nick = AccountLibraryService.AccountNameOf(platform);
		return nick.Length > 0 ? nick + " · " + baseText : baseText;
	}

	private void NeteasePlaylistLoadButton_Click(object sender, RoutedEventArgs e) => _ = LoadPlaylistsAsync("netease");

	private void QqPlaylistLoadButton_Click(object sender, RoutedEventArgs e) => _ = LoadPlaylistsAsync("qq");

	private void KugouPlaylistLoadButton_Click(object sender, RoutedEventArgs e) => _ = LoadPlaylistsAsync("kugou");

	/// <summary>收藏夹行常驻显示（网易云 / QQ 两张卡），未登录时按钮置灰——
	/// 三段式统一后五张卡的第三段长一个样，能力差异用「可点/不可点」表达。
	/// 退出登录时把已加载的歌单行一并清掉——行的点击逻辑依赖 Cookie，
	/// 留着只会点开就报错。</summary>
	private void UpdatePlaylistSection(string platform, bool loggedIn)
	{
		if (platform == "netease")
		{
			NeteasePlaylistLoadButton.IsEnabled = loggedIn;
			if (!loggedIn)
			{
				NeteasePlaylistList.Children.Clear();
				NeteasePlaylistStatus.Text = "";
			}
		}
		else
		{
			QqPlaylistLoadButton.IsEnabled = loggedIn;
			if (!loggedIn)
			{
				QqPlaylistList.Children.Clear();
				QqPlaylistStatus.Text = "";
			}
		}
	}

	/// <summary>拉取账号下的歌单（收藏夹）并铺成列表行。只在用户点「加载收藏夹」时触发，
	/// 绝不自动轮询/自动加载——多打一次账号接口就多一分风控暴露。
	/// 失败分两种说：Cookie 失效（服务端业务码异常）和网络异常（请求本身抛了异常），
	/// 混成一句「加载失败」会让用户往网络上去查，白折腾。</summary>
	private async System.Threading.Tasks.Task LoadPlaylistsAsync(string platform)
	{
		if (_loadingPlaylists)
		{
			return;
		}
		_loadingPlaylists = true;
		LogHelper.LogDebug("收藏夹: 用户点击加载按钮 platform=" + platform);
		Button loadBtn = platform switch
		{
			"netease" => NeteasePlaylistLoadButton,
			"kugou" => KugouPlaylistLoadButton,
			_ => QqPlaylistLoadButton,
		};
		TextBlock status = platform switch
		{
			"netease" => NeteasePlaylistStatus,
			"kugou" => KugouPlaylistStatus,
			_ => QqPlaylistStatus,
		};
		bool wasEnabled = loadBtn.IsEnabled;
		loadBtn.IsEnabled = false;
		status.Text = WithAccountName(platform, Loc.Default["acct_pl_loading"]);
		try
		{
			if (platform == "netease")
			{
				var (result, playlists) = await NeteaseApiClient.FetchPlaylistsAsync();
				if (result != PlaylistFetchResult.Ok)
				{
					ShowPlaylistProblem(platform, status, result);
					return;
				}
				NeteasePlaylistList.Children.Clear();
				foreach (var pl in playlists)
				{
					// 闭包捕获要用局部变量：foreach 变量在旧 C# 里是共享的，
					// 就算新编译器已修掉这个坑，显式捕获也让人一眼看懂每行绑的是自己的歌单
					var captured = pl;
					NeteasePlaylistList.Children.Add(BuildPlaylistRow(
						pl.Name,
						Loc.Default.Format("acct_pl_count", pl.TrackCount),
						pl.CoverUrl,
						() => _ = OpenNeteasePlaylistAsync(captured)));
				}
				ShowPlaylistCount(status, playlists.Count);
				status.Text = WithAccountName(platform, status.Text);
			}
			else if (platform == "kugou")
			{
				string cookie = SettingsManager.Instance.KugouCookie ?? "";
				var (st, pls) = await KugouClient.FetchAccountPlaylistsAsync(cookie);
				if (st != AccountPlaylistStatus.Ok)
				{
					ShowPlaylistProblem(platform, status, st == AccountPlaylistStatus.NetworkError
						? PlaylistFetchResult.NetworkError
						: PlaylistFetchResult.CookieInvalid);
					return;
				}
				KugouPlaylistList.Children.Clear();
				foreach (var pl in pls)
				{
					var captured = pl;
					KugouPlaylistList.Children.Add(BuildPlaylistRow(
						pl.Name,
						Loc.Default.Format("acct_pl_count", pl.Count),
						"",
						() => _ = OpenKugouPlaylistAsync(captured),
						"kugou"));
				}
				ShowPlaylistCount(status, pls.Count);
				status.Text = WithAccountName(platform, status.Text);
			}
			else
			{
				var (result, playlists) = await QqClient.FetchPlaylistsAsync();
				if (result != PlaylistFetchResult.Ok)
				{
					ShowPlaylistProblem(platform, status, result);
					return;
				}
				QqPlaylistList.Children.Clear();
				foreach (var pl in playlists)
				{
					var captured = pl;
					QqPlaylistList.Children.Add(BuildPlaylistRow(
						pl.Name,
						Loc.Default.Format("acct_pl_count", pl.TrackCount),
						pl.CoverUrl,
						() => _ = OpenQqPlaylistAsync(captured)));
				}
				ShowPlaylistCount(status, playlists.Count);
				status.Text = WithAccountName(platform, status.Text);
			}
			// 账号名跟着这次成功的列表加载补拉一次（设置里已有就不发请求），
			// 拉到了把昵称补到状态行开头。
			// 旧版在这里按「首次成功加载收藏夹」的先后写 LibraryPlatformOrder，
			// 已按要求撤掉：初始顺序改为按歌单数升序（见 MusicLibraryPage
			// 的 SortGroupsBySavedOrder），只有用户手动拖动才落盘顺序。
			_ = RefreshAccountNameAsync(platform);
		}
		catch (Exception ex)
		{
			// 兜底必须留：这个方法被 `_ =` fire-and-forget 调用，异常没人观察，
			// 铺歌单行的 UI 代码一旦抛异常，状态行会永远卡在「正在加载」且日志无痕
			LogHelper.LogDebug("收藏夹: 加载流程异常 platform=" + platform + " -> " + ex);
			status.Text = WithAccountName(platform, Loc.Default["acct_pl_fail_net"]);
			if (Application.Current.MainWindow is MainWindow mw)
			{
				mw.ShowToast(Loc.Default["acct_pl_fail_net"], false);
			}
		}
		finally
		{
			_loadingPlaylists = false;
			loadBtn.IsEnabled = wasEnabled;
		}
	}

	/// <summary>收藏夹拉取失败：状态行 + Toast 各说一次，文案按失效类型区分。
	/// 状态行同样带账号名前缀（顶部 = 账号名 + 状态）。</summary>
	private void ShowPlaylistProblem(string platform, TextBlock status, PlaylistFetchResult result)
	{
		string msg = Loc.Default[result == PlaylistFetchResult.CookieInvalid ? "acct_pl_fail_cookie" : "acct_pl_fail_net"];
		status.Text = WithAccountName(platform, msg);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(msg, false);
		}
	}

	private static void ShowPlaylistCount(TextBlock status, int count)
	{
		// 空账号和加载成功要分开说：0 个歌单不是「加载完成」，是「没有东西可看」。
		status.Text = count > 0 ? Loc.Default.Format("acct_pl_loaded_list", count) : Loc.Default["acct_pl_empty"];
	}

	/// <summary>账号名补拉（fire-and-forget）：只在设置里还没有昵称时发请求，
	/// 拉到了把当前状态行文字重新套上「昵称 · 状态」前缀。UI 上下文的
	/// async 链调用，await 之后天然回 UI 线程。</summary>
	private async System.Threading.Tasks.Task RefreshAccountNameAsync(string platform)
	{
		try
		{
			bool changed = await AccountLibraryService.FetchAccountNamesAsync();
			if (changed)
			{
				TextBlock status = platform switch
				{
					"netease" => NeteasePlaylistStatus,
					"kugou" => KugouPlaylistStatus,
					_ => QqPlaylistStatus,
				};
				if (status.Text.Length > 0)
				{
					status.Text = WithAccountName(platform, status.Text);
				}
			}
		}
		catch (Exception ex)
		{
			// fire-and-forget 兜底：昵称补拉失败只留痕，不影响收藏夹展示。
			LogHelper.LogDebug("收藏夹: 账号名补拉异常 " + ex.Message);
		}
	}

	/// <summary>点开网易云歌单：拉整份曲目（trackIds → 批量 song detail），
	/// 成功就跳到在线搜索页灌进结果列表。曲目为空不跳转——
	/// 跳过去看一个空列表反而是惊吓，留在原地说明更清楚。</summary>
	private async System.Threading.Tasks.Task OpenNeteasePlaylistAsync(NeteasePlaylistSummary pl)
	{
		NeteasePlaylistStatus.Text = WithAccountName("netease", Loc.Default.Format("acct_pl_opening", pl.Name));
		var (result, tracks) = await NeteaseApiClient.FetchPlaylistTracksAsync(pl.Id);
		if (result != PlaylistFetchResult.Ok)
		{
			ShowPlaylistProblem("netease", NeteasePlaylistStatus, result);
			return;
		}
		if (tracks.Count == 0)
		{
			NeteasePlaylistStatus.Text = Loc.Default["acct_pl_tracks_empty"];
			return;
		}
		MainWindow.Instance?.OpenPlaylistInOnlinePage("netease", pl.Name, tracks.Cast<object>().ToList(), pl.Id.ToString());
	}

	/// <summary>点开 QQ 歌单：musicu.fcg 拉整份曲目映射成 QqTrack，
	/// 走和搜索结果完全相同的播放/下载链路。</summary>
	/// <summary>点开酷狗歌单：走已打通的手机域会话拉曲目，再灌进在线页，
	/// 和网易云/QQ 歌单行完全同一条动线。</summary>
	private async System.Threading.Tasks.Task OpenKugouPlaylistAsync(KugouClient.KugouPlaylist pl)
	{
		KugouPlaylistStatus.Text = WithAccountName("kugou", Loc.Default.Format("acct_pl_opening", pl.Name));
		string cookie = SettingsManager.Instance.KugouCookie ?? "";
		var (st, tracks) = await KugouClient.FetchAccountPlaylistTracksAsync(cookie, pl.CollectionId);
		if (st != AccountPlaylistStatus.Ok)
		{
			ShowPlaylistProblem("kugou", KugouPlaylistStatus, st == AccountPlaylistStatus.NetworkError
				? PlaylistFetchResult.NetworkError
				: PlaylistFetchResult.CookieInvalid);
			return;
		}
		if (tracks.Count == 0)
		{
			KugouPlaylistStatus.Text = Loc.Default["acct_pl_tracks_empty"];
			return;
		}
		MainWindow.Instance?.OpenPlaylistInOnlinePage(
			"kugou", pl.Name, tracks.Cast<object>().ToList(), pl.CollectionId);
	}

	private async System.Threading.Tasks.Task OpenQqPlaylistAsync(QqPlaylistSummary pl)
	{
		QqPlaylistStatus.Text = WithAccountName("qq", Loc.Default.Format("acct_pl_opening", pl.Name));
		var (result, tracks) = await QqClient.FetchPlaylistSongsAsync(pl.DissId);
		if (result != PlaylistFetchResult.Ok)
		{
			ShowPlaylistProblem("qq", QqPlaylistStatus, result);
			return;
		}
		if (tracks.Count == 0)
		{
			QqPlaylistStatus.Text = Loc.Default["acct_pl_tracks_empty"];
			return;
		}
		MainWindow.Instance?.OpenPlaylistInOnlinePage("qq", pl.Name, tracks.Cast<object>().ToList(), pl.DissId.ToString());
	}

	/// <summary>收藏夹里的一行歌单：封面（有就异步加载，没有给音符占位）+ 名字 + 「N 首」+ 右箭头。
	/// 底色用不透明的 InputBgBrush（和输入框同层），悬停切 RowHover——
	/// 毛玻璃/透明主题下也不会露出卡片底下的内容，主题一换自动跟着变。
	/// 原来是 private，音乐库「收藏夹」左栏的歌单行复用同一份构建
	/// （同一种底色、悬停、封面占位，两个页面看起来是一套控件）。
	/// platform：歌单所属平台（soda/qq/…），无封面时用它挑占位图标 ——
	/// 汽水给青绿底的音符小徽章（矢量风格），其余平台维持通用音符占位。
	/// transparent：底栏卡片透明（音乐库左栏的收藏夹用 true）。
	/// 账号页保持不透明：那边的歌单行嵌在登录卡片里，透明会露出卡片底色。</summary>
	internal static FrameworkElement BuildPlaylistRow(string name, string sub, string coverUrl, Action onClick, string? platform = null, bool transparent = false)
	{
		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		UIElement cover;
		if (!string.IsNullOrWhiteSpace(coverUrl))
		{
			var img = new Image
			{
				Width = 36,
				Height = 36,
				Stretch = Stretch.UniformToFill
			};
			// 「显示封面图片」关掉时不再请求歌单封面（与列表行同一开关）：
			// 歌单也是一行一请求，收藏夹几百个歌单时同样可观。留占位不发请求。
			if (SettingsManager.Instance.ShowListCovers)
			{
				CoverLoader.LoadAsync(img, coverUrl, 72);
			}
			// 圆角裁切靠 Border.ClipToBounds：Image 自己没有 CornerRadius，
			// 套一层带圆角的 Border 是最省事的做法
			cover = new Border
			{
				Width = 36,
				Height = 36,
				CornerRadius = new CornerRadius(6),
				ClipToBounds = true,
				Child = img
			};
		}
		else if (string.Equals(platform, "soda", StringComparison.Ordinal))
		{
			// 汽水歌单没有封面：用品牌徽章占位（青绿底 + 白音符，随主题深浅换色），
			// 不再是千篇一律的灰底音符 —— 36px 角标一眼认出这是汽水的歌单。
			cover = TrackRows.SodaIconBadge(36);
		}
		else
		{
			cover = new Border
			{
				Width = 36,
				Height = 36,
				CornerRadius = new CornerRadius(6),
				Background = ThemeManager.Brush("SubtleFillHoverBrush", Brushes.LightGray),
				Child = new TextBlock
				{
					Text = "\uE8D6", // Segoe MDL2 Assets: MusicNote
					FontFamily = new FontFamily("Segoe MDL2 Assets"),
					FontSize = 14,
					Foreground = ThemeManager.Tertiary(),
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				}
			};
		}
		grid.Children.Add(cover);

		var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
		text.Children.Add(new TextBlock
		{
			Text = name,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold,
			Foreground = ThemeManager.Primary(),
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		text.Children.Add(new TextBlock
		{
			Text = sub,
			FontSize = 11.5,
			Foreground = ThemeManager.Tertiary(),
			Margin = new Thickness(0, 1, 0, 0)
		});
		Grid.SetColumn(text, 1);
		grid.Children.Add(text);

		var chevron = new TextBlock
		{
			Text = "\uE76C", // Segoe MDL2 Assets: ChevronRight
			FontFamily = new FontFamily("Segoe MDL2 Assets"),
			FontSize = 11,
			Foreground = ThemeManager.Tertiary(),
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(8, 0, 0, 0)
		};
		Grid.SetColumn(chevron, 2);
		grid.Children.Add(chevron);

		var row = new Border
		{
			// 透明模式（音乐库左栏）：卡片不画底色，整栏看起来是通透的一列行，
			// 只有悬停时才浮出高亮。吸顶的平台组头压上来时也不会出现
			// 「一块实心深色卡片被切掉一半」这种硬边。
			Background = transparent ? Brushes.Transparent : ThemeManager.Brush("InputBgBrush", Brushes.Gray),
			CornerRadius = new CornerRadius(8),
			Padding = new Thickness(8, 6, 10, 6),
			Margin = new Thickness(0, 0, 0, 6),
			Cursor = System.Windows.Input.Cursors.Hand,
			Child = grid,
			SnapsToDevicePixels = true
		};
		Brush rest = row.Background;
		// 透明卡片没有底色可对比，悬停要走半透明高亮（SubtleFillHover），
		// 不透明卡片沿用按主题算好的 RowHover 实色。
		row.MouseEnter += (_, _) =>
		{
			if (transparent)
			{
				row.SetResourceReference(Border.BackgroundProperty, "SubtleFillHoverBrush");
			}
			else
			{
				row.Background = ThemeManager.RowHover();
			}
		};
		row.MouseLeave += (_, _) => row.Background = rest;
		// 用 MouseLeftButtonDown 而不是套 Button：按钮模板要重写才有这种圆角行效果，
		// Border + 悬停变色已经够用，还省一层模板
		row.MouseLeftButtonDown += (_, _) => onClick();
		return row;
	}

	private void LoginHeader_Click(object sender, RoutedEventArgs e)
	{
		// 代码展开时同步改的 IsChecked 会再触发一次 Click，必须挡掉，
		// 否则「展开 -> 这次 Click -> 又收回去」。
		if (_syncLoginToggle)
		{
			return;
		}
		if (sender is ToggleButton tb)
		{
			SetLoginCardExpanded((string)tb.Tag, tb.IsChecked == true);
		}
	}

	// 下面三个查表函数是实例方法：XAML 里 x:Name 生成的字段都是实例成员，
	// 写成 static 就得先 new 一个 AccountPage 才能拿到，纯属多余。
	private FrameworkElement? LoginBody(string key) => key switch
	{
		"Netease" => NeteaseBody,
		"Qq" => QqBody,
		"Kugou" => KugouBody,
		"Bili" => BiliCookieBody,
		"Soda" => SodaBody,
		_ => null,
	};

	private RotateTransform? LoginChevron(string key) => key switch
	{
		"Netease" => NeteaseChevronRotate,
		"Qq" => QqChevronRotate,
		"Kugou" => KugouChevronRotate,
		"Bili" => BiliCookieChevronRotate,
		"Soda" => SodaChevronRotate,
		_ => null,
	};

	private ToggleButton? LoginToggle(string key) => key switch
	{
		"Netease" => NeteaseHeader,
		"Qq" => QqHeader,
		"Kugou" => KugouHeader,
		"Bili" => BiliCookieHeader,
		"Soda" => SodaHeader,
		_ => null,
	};

	private ScaleTransform? LoginBodyScale(string key) => key switch
	{
		"Netease" => NeteaseBodyScale,
		"Qq" => QqBodyScale,
		"Kugou" => KugouBodyScale,
		"Bili" => BiliCookieBodyScale,
		"Soda" => SodaBodyScale,
		_ => null,
	};

	/// <summary>展开/收起某张卡，带动画：内容按 ScaleY 0↔1 长开/收回，
	/// 同时淡入淡出，箭头 90°↔0° 转过去。收起时等动画走完才真 Collapsed。</summary>
	private void SetLoginCardExpanded(string key, bool expanded)
	{
		FrameworkElement? body = LoginBody(key);
		RotateTransform? chevron = LoginChevron(key);
		ScaleTransform? scale = LoginBodyScale(key);

		if (expanded)
		{
			_loginExpanded.Add(key);
		}
		else
		{
			_loginExpanded.Remove(key);
		}

		if (body != null && scale != null)
		{
			// 展开：先让它参与布局再从 0 起步，否则起点没地方算。
			body.Visibility = Visibility.Visible;
			// 先把基准值钉死再挂动画。之前那轮 HoldEnd 的动画可能还把
			// ScaleY 按在 0 或 1 上，不重设基准就会出现「动画不动」。
			scale.ScaleY = expanded ? 0.0 : 1.0;
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			body.Opacity = expanded ? 0.0 : 1.0;
			body.BeginAnimation(UIElement.OpacityProperty, null);

			// EaseOut：起步快、收尾慢。收起用 EaseIn 反过来，看着才对——
			// 两边都用 EaseOut 的话，收起会先慢后快，像被吸回去。
			var easing = new CubicEase { EasingMode = expanded ? EasingMode.EaseOut : EasingMode.EaseIn };
			var grow = new DoubleAnimation(scale.ScaleY, expanded ? 1.0 : 0.0, TimeSpan.FromMilliseconds(200))
			{
				EasingFunction = easing,
				FillBehavior = FillBehavior.HoldEnd,
			};
			var fade = new DoubleAnimation(body.Opacity, expanded ? 1.0 : 0.0, TimeSpan.FromMilliseconds(expanded ? 200 : 150))
			{
				EasingFunction = easing,
				FillBehavior = FillBehavior.HoldEnd,
			};
			if (!expanded)
			{
				// 收起完才真隐藏。动画中途被打断（又点开）时这条不会触发，
				// 另外还要对一下当前意图：Base 值上两种状态都是 0，
				// 判断不了，只能自己记一份。
				fade.Completed += (_, _) =>
				{
					if (!_loginExpanded.Contains(key) && body.Visibility == Visibility.Visible)
					{
						body.Visibility = Visibility.Collapsed;
					}
				};
			}
			// 直接用 BeginAnimation 而不是 Storyboard：ScaleTransform 是
			// Freezable，Storyboard.Begin 没有接 Freezable 的重载，
			// Animatable.BeginAnimation 才是给这类变换对象用的。
			// 顺带好处：连点两次时新动画自动顶掉旧的，不用自己管上一条。
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
			body.BeginAnimation(UIElement.OpacityProperty, fade);
		}

		if (chevron != null)
		{
			var turn = new DoubleAnimation(chevron.Angle, expanded ? 0.0 : 90.0, TimeSpan.FromMilliseconds(200))
			{
				EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
				FillBehavior = FillBehavior.HoldEnd,
			};
			chevron.BeginAnimation(RotateTransform.AngleProperty, turn);
		}
		ToggleButton? toggle = LoginToggle(key);
		if (toggle != null && toggle.IsChecked != expanded)
		{
			// 由代码展开时（比如点「打开浏览器登录」后要让人看见输入框），
			// 直接改 IsChecked 会再触发一次 Click，两次翻转又变回去。
			_syncLoginToggle = true;
			toggle.IsChecked = expanded;
			_syncLoginToggle = false;
		}
	}

	private bool _syncLoginToggle;

	/// <summary>当前处于展开态的卡。动画回调要靠它判断「这次收起还没被撤销」，
	/// 因为动画播完后 ScaleY 的基准值两种状态都是 0，从数值上看不出来。</summary>
	private readonly HashSet<string> _loginExpanded = new();

	/// <summary>把某张卡展开（不改变箭头以外的东西）。用于「点了按钮要让用户看见结果」的场景。
	///
	/// 必须先判「已经展开」：SetLoginCardExpanded(true) 无条件把 ScaleY 归 0、
	/// Opacity 归 0，再动画长回 1。卡片本来就开着时（点卡里的「扫码登录 /
	/// 网页登录」按钮必然是这个状态），视觉上就是整块内容瞬间收缩又弹回来一下。
	/// 这里做幂等，已展开且确实可见时直接返回，不重播动画。</summary>
	private void ExpandLoginCard(string key)
	{
		FrameworkElement? body = LoginBody(key);
		if (_loginExpanded.Contains(key) && body?.Visibility == Visibility.Visible)
		{
			return;
		}
		SetLoginCardExpanded(key, true);
	}

	/// <summary>用默认浏览器打开登录页（程序不再内置浏览器），并提示回来读取。</summary>
	private void OpenBrowserLogin(string url, string cardKey = "")
	{
		// 顺手把卡片展开：登录完要回到这儿点「读取本机Cookie」，
		// 卡要是还收着，读到没读到都看不见，只剩一个飘在上面的提示。
		if (cardKey.Length > 0)
		{
			ExpandLoginCard(cardKey);
		}
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
		catch
		{
		}
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_login_opened"]);
		}
	}

	/// <summary>从剪贴板读取一行形如 k=v; k2=v2 的 Cookie 文本，取不到返回 null。</summary>
	private static string? TryClipboardCookie()
	{
		try
		{
			string text = Clipboard.GetText() ?? "";
			text = text.Trim();
			if (text.Length < 8 || !text.Contains('='))
			{
				return null;
			}
			if (text.Contains('\n'))
			{
				text = text.Replace("\r\n", "\n").Replace('\r', '\n')
					.Split('\n')
					.Select(l => l.Trim())
					.FirstOrDefault(l => l.Length >= 8 && l.Contains('=')) ?? "";
			}
			if (text.Length < 8 || text.Length > 2048 || !text.Contains('='))
			{
				return null;
			}
			return text;
		}
		catch
		{
			return null;
		}
	}

	private void QqWebLoginButton_Click(object sender, RoutedEventArgs e)
	{
		ExpandLoginCard("Qq");
		var win = new WebLoginWindow(
			"https://y.qq.com/",
			"https://y.qq.com/",
			Loc.Default["plat_qq"],
			cookie =>
			{
				var st2 = SettingsManager.Instance;
				st2.QqCookie = cookie.Trim();
				st2.QqUin = Services.QqClient.ExtractUin(st2.QqCookie);
				SettingsManager.Save(st2);
				UpdateCookieUI();
			});
		if (Window.GetWindow(this) is Window owner)
		{
			win.Owner = owner;
		}
		win.ShowDialog();
	}

	/// <summary>酷狗网页登录：内置浏览器打开酷狗首页，登录完成后把 kugou.com 域下的
	/// 会话 Cookie（含 httpOnly）整体抓回存入设置。入口和 QQ 那张卡完全同构。</summary>
	private void KugouQrLoginButton_Click(object sender, RoutedEventArgs e)
	{
		ExpandLoginCard("Kugou");
		var win = new KugouQrLoginWindow();
		if (Window.GetWindow(this) is Window owner)
		{
			win.Owner = owner;
		}
		win.ShowDialog();
		if (win.DialogResult == true)
		{
			// 扫码成功：会话（userid+token+昵称）已在弹窗里落盘，这里刷新卡片状态
			// 并把输入框同步成新 Cookie，用户能直接看到变化。
			var st = SettingsManager.Instance;
			KugouCookieBox.Text = st.KugouCookie ?? "";
			KugouNickBox.Text = st.KugouNick ?? "";
			UpdateCookieUI();
			MainWindow.Instance?.ShowToast(Loc.Default["set_kugou_qr_ok"], true);
		}
	}

	private void KugouWebLoginButton_Click(object sender, RoutedEventArgs e)
	{
		ExpandLoginCard("Kugou");
		var win = new WebLoginWindow(
			"https://www.kugou.com/",
			"https://www.kugou.com/",
			Loc.Default["plat_kugou"],
			cookie =>
			{
				var st2 = SettingsManager.Instance;
				// 扫码会话（userid+token）是收藏夹唯一能用的凭据；网页 Cookie 不含
				// 这两个字段，覆盖掉会让刚扫的码立刻失效、收藏夹又变「暂不支持」。
				// 用户点网页登录多半只是想看看，这里保留会话并给一句明确提示。
				if (KugouClient.ParseSession(st2.KugouCookie) != null)
				{
					MainWindow.Instance?.ShowToast(Loc.Default["set_kugou_web_kept_qr"], true);
					return;
				}
				st2.KugouCookie = cookie.Trim();
				SettingsManager.Save(st2);
				UpdateCookieUI();
			});
		if (Window.GetWindow(this) is Window owner)
		{
			win.Owner = owner;
		}
		win.ShowDialog();
	}

	/// <summary>汽水「网页登录」：汽水有网页版（qishui.douyin.com，就是抖音音乐版 SPA），
	/// 2026-11-05 实测直接开抖音主站拿到的会话，汽水 API（api.qishui.com/luna/pc/me）不认、
	/// 返回 1000016 登录失效——汽水侧会话必须在汽水网页版里登录建立。
	/// 所以打开汽水网页版让用户登录，并按 qishui.douyin.com 域抓 Cookie
	/// （顺带覆盖 .douyin.com 域 Cookie，避免漏掉 host-only 的汽水会话字段）。</summary>
	private void SodaWebLoginButton_Click(object sender, RoutedEventArgs e)
	{
		ExpandLoginCard("Soda");
		var win = new WebLoginWindow(
			"https://qishui.douyin.com/",
			"https://qishui.douyin.com/",
			Loc.Default["plat_soda"],
			cookie =>
			{
				var st2 = SettingsManager.Instance;
				st2.SodaCookie = cookie.Trim();
				SettingsManager.Save(st2);
				UpdateCookieUI();
			});
		if (Window.GetWindow(this) is Window owner)
		{
			win.Owner = owner;
		}
		win.ShowDialog();
	}

	private void QqFetchCookieButton_Click(object sender, RoutedEventArgs e)
	{
		string? cookie = QqClient.ReadSiteCookies("https://y.qq.com");
		bool fromClip = false;
		if (string.IsNullOrWhiteSpace(cookie))
		{
			cookie = TryClipboardCookie();
			fromClip = cookie != null;
		}
		if (string.IsNullOrWhiteSpace(cookie))
		{
			// 自动读取失败时把卡展开：下面那句提示就是让人手动粘贴的，
			// 卡收着的话连粘贴框都看不到。
			ExpandLoginCard("Qq");
			if (Application.Current.MainWindow is MainWindow mw0)
			{
				mw0.ShowToast(Loc.Default["set_login_fetch_none"]);
			}
			return;
		}
		var s = SettingsManager.Instance;
		s.QqCookie = cookie.Trim();
		s.QqUin = Services.QqClient.ExtractUin(s.QqCookie);
		SettingsManager.Save(s);
		UpdateCookieUI();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default[fromClip ? "set_login_clip_ok" : "set_login_fetch_ok"]);
		}
	}

	private void BiliBrowserLoginButton_Click(object sender, RoutedEventArgs e)
	{
		OpenBrowserLogin("https://passport.bilibili.com/login", "Bili");
	}

	private void BiliFetchCookieButton_Click(object sender, RoutedEventArgs e)
	{
		// 优先取 BBDown 扫码登录产物，其次系统浏览器 Cookie，最后剪贴板
		string? cookie = BiliClient.ReadBbDownCookieString();
		bool fromClip = false;
		if (string.IsNullOrWhiteSpace(cookie))
		{
			cookie = QqClient.ReadSiteCookies("https://www.bilibili.com");
		}
		if (string.IsNullOrWhiteSpace(cookie))
		{
			cookie = TryClipboardCookie();
			fromClip = cookie != null;
		}
		if (string.IsNullOrWhiteSpace(cookie))
		{
			// 自动读取失败时把卡展开：下面那句提示就是让人手动粘贴的，
			// 卡收着的话连粘贴框都看不到。
			ExpandLoginCard("Bili");
			if (Application.Current.MainWindow is MainWindow mw0)
			{
				mw0.ShowToast(Loc.Default["set_login_fetch_none"]);
			}
			return;
		}
		var s = SettingsManager.Instance;
		s.BiliCookie = cookie.Trim();
		SettingsManager.Save(s);
		UpdateCookieUI();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default[fromClip ? "set_login_clip_ok" : "set_login_fetch_ok"]);
		}
	}

	private void NeteaseBrowserLoginButton_Click(object sender, RoutedEventArgs e)
	{
		OpenBrowserLogin("https://music.163.com/#/login", "Netease");
	}

	/// <summary>网易云原生扫码登录：弹扫码窗，成功后把 Cookie 存进设置并刷新卡片状态。</summary>
	private void NeteaseQrButton_Click(object sender, RoutedEventArgs e)
	{
		var win = new QrLoginWindow(cookie =>
		{
			var s = SettingsManager.Instance;
			s.NeteaseCookie = NeteaseApiClient.NormalizeCookie(cookie);
			SettingsManager.Save(s);
			Dispatcher.BeginInvoke(new Action(() =>
			{
				UpdateCookieUI();
				if (Application.Current.MainWindow is MainWindow mw)
				{
					mw.ShowToast(Loc.Default["set_netease_saved"]);
				}
			}));
		});
		win.Owner = Application.Current.MainWindow;
		win.ShowDialog();
	}

	private void NeteaseFetchCookieButton_Click(object sender, RoutedEventArgs e)
	{
		string? cookie = QqClient.ReadSiteCookies("https://music.163.com");
		bool fromClip = false;
		if (string.IsNullOrWhiteSpace(cookie))
		{
			cookie = TryClipboardCookie();
			fromClip = cookie != null;
		}
		if (string.IsNullOrWhiteSpace(cookie))
		{
			// 自动读取失败时把卡展开：下面那句提示就是让人手动粘贴的，
			// 卡收着的话连粘贴框都看不到。
			ExpandLoginCard("Netease");
			if (Application.Current.MainWindow is MainWindow mw0)
			{
				mw0.ShowToast(Loc.Default["set_login_fetch_none"]);
			}
			return;
		}
		var s = SettingsManager.Instance;
		s.NeteaseCookie = NeteaseApiClient.NormalizeCookie(cookie);
		SettingsManager.Save(s);
		UpdateCookieUI();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default[fromClip ? "set_login_clip_ok" : "set_login_fetch_ok"]);
		}
	}

	private void QqCookieSaveButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.QqCookie = (QqCookieBox.Text ?? "").Trim();
		s.QqUin = Services.QqClient.ExtractUin(s.QqCookie);
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_qq_saved"]);
		}
	}

	private void QqCookieClearButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.QqCookie = "";
		s.QqUin = "";
		SettingsManager.Save(s);
		QqCookieBox.Text = "";
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_qq_cleared"]);
		}
	}

	private void NeteaseCookieSaveButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.NeteaseCookie = (NeteaseCookieBox.Text ?? "").Trim();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_netease_saved"]);
		}
	}

	private void NeteaseCookieClearButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.NeteaseCookie = "";
		SettingsManager.Save(s);
		NeteaseCookieBox.Text = "";
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_netease_cleared"]);
		}
	}

	private void KugouCookieSaveButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.KugouNick = (KugouNickBox.Text ?? "").Trim();
		s.KugouCookie = (KugouCookieBox.Text ?? "").Trim();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_kugou_saved"]);
		}
	}

	private void KugouCookieClearButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.KugouNick = "";
		s.KugouCookie = "";
		SettingsManager.Save(s);
		KugouNickBox.Text = "";
		KugouCookieBox.Text = "";
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_kugou_cleared"]);
		}
	}

	private void SodaCookieSaveButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.SodaCookie = (SodaCookieBox.Text ?? "").Trim();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_soda_saved"]);
		}
	}

	private void SodaCookieClearButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.SodaCookie = "";
		s.SodaNick = "";
		SettingsManager.Save(s);
		SodaCookieBox.Text = "";
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_soda_cleared"]);
		}
	}

	/// <summary>汽水会话校验：拿粘贴的 Cookie 请求 /luna/pc/me，反馈是否有效。
	/// 账号/收藏接口需要官方签名头，这里只验证会话，不承诺能同步收藏。</summary>
	private async void SodaVerifyButton_Click(object sender, RoutedEventArgs e)
	{
		if (_sodaVerifying)
		{
			return;
		}
		var s = SettingsManager.Instance;
		string cookie = (SodaCookieBox.Text ?? "").Trim();
		if (string.IsNullOrWhiteSpace(cookie))
		{
			if (Application.Current.MainWindow is MainWindow mw0)
			{
				mw0.ShowToast(Loc.Default["set_soda_cookie_ph"]);
			}
			return;
		}
		_sodaVerifying = true;
		s.SodaCookie = cookie;
		SettingsManager.Save(s);
		SodaVerifyButton.IsEnabled = false;
		try
		{
			var (ok, nick) = await SodaClient.FetchAccountAsync(cookie);
			if (Application.Current.MainWindow is MainWindow mw)
			{
				mw.ShowToast(ok
					? Loc.Default.Format("set_soda_verify_ok", string.IsNullOrEmpty(nick) ? "-" : nick)
					: Loc.Default["set_soda_verify_fail"]);
			}
			UpdateCookieUI();
		}
		finally
		{
			_sodaVerifying = false;
			SodaVerifyButton.IsEnabled = true;
		}
	}

	private void BiliCookieSaveButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.BiliCookie = (BiliCookieBox.Text ?? "").Trim();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_bili_cookie_saved"]);
		}
	}

	private void BiliCookieClearButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.BiliCookie = "";
		SettingsManager.Save(s);
		BiliCookieBox.Text = "";
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_bili_cookie_cleared"]);
		}
	}

	private void UpdateBiliStatus()
	{
		string? tool = BiliClient.BbDownToolPath;
		bool hasTool = !string.IsNullOrEmpty(tool) && File.Exists(tool);
		if (!hasTool)
		{
			BiliStatusText.Text = Loc.Default["set_bili_fail"];
			BiliDeleteButton.IsEnabled = false;
			return;
		}
		bool ok = BiliClient.HasUserCookie;
		BiliStatusText.Text = Loc.Default[ok ? "set_bili_status_ok" : "set_bili_status_no"];
		BiliDeleteButton.IsEnabled = ok;
		if (ok && string.IsNullOrEmpty(BiliQrText.Text))
		{
			BiliQrPlaceholder.Visibility = Visibility.Visible;
		}
	}

	private async void BiliLoginButton_Click(object sender, RoutedEventArgs e)
	{
		if (_loggingIn)
		{
			return;
		}
		_loggingIn = true;
		BiliLoginButton.IsEnabled = false;
		BiliQrText.Text = "";
		BiliQrImage.Source = null;
		BiliQrImage.Visibility = Visibility.Collapsed;
		BiliQrText.Visibility = Visibility.Visible;
		BiliQrPlaceholder.Visibility = Visibility.Visible;
		BiliStatusText.Text = Loc.Default["set_bili_logging"];
		try
		{
			await BiliClient.LoginBbDownAsync(line => Dispatcher.BeginInvoke(new Action(() =>
			{
				AppendQr(line);
			}), DispatcherPriority.Background));
		}
		finally
		{
			_loggingIn = false;
			BiliLoginButton.IsEnabled = true;
		}
		UpdateBiliStatus();
		string? bbCookie = BiliClient.ReadBbDownCookieString();
		bool ok = !string.IsNullOrWhiteSpace(bbCookie);
		if (ok)
		{
			// 扫码成功直接写入程序内 Cookie，供「云端同步收藏」使用（保存在本机设置里）
			var s = SettingsManager.Instance;
			s.BiliCookie = bbCookie!.Trim();
			SettingsManager.Save(s);
			UpdateCookieUI();
			// Cookie 已入库，清掉本机 BBDown 残留（二维码、BBDown.data 等）
			BiliClient.CleanupLoginArtifacts();
		}
		UpdateBiliStatus();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default[ok ? "set_bili_ok" : "set_bili_fail"], ok);
		}
	}

	private void AppendQr(string? line)
	{
		if (string.IsNullOrEmpty(line))
		{
			return;
		}
		BiliQrText.Text += line + "\n";
		BiliQrPlaceholder.Visibility = Visibility.Collapsed;
		BiliQrText.ScrollToEnd();
		TryShowQrImage();
	}

	private void TryShowQrImage()
	{
		try
		{
			string dir = BiliClient.LoginDir;
			if (!Directory.Exists(dir)) return;
			string? png = Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)
				.OrderByDescending(f => new FileInfo(f).LastWriteTime)
				.FirstOrDefault();
			if (string.IsNullOrEmpty(png)) return;

			var bi = new BitmapImage();
			bi.BeginInit();
			bi.UriSource = new Uri(png);
			bi.CacheOption = BitmapCacheOption.OnLoad;
			bi.EndInit();
			bi.Freeze();
			BiliQrImage.Source = bi;
			BiliQrImage.Visibility = Visibility.Visible;
			BiliQrText.Visibility = Visibility.Collapsed;
			BiliQrPlaceholder.Visibility = Visibility.Collapsed;
		}
		catch
		{
		}
	}

	private void BiliDeleteButton_Click(object sender, RoutedEventArgs e)
	{
		BiliClient.DeleteLocalCookie();
		BiliQrText.Text = "";
		BiliQrImage.Source = null;
		BiliQrImage.Visibility = Visibility.Collapsed;
		BiliQrText.Visibility = Visibility.Visible;
		BiliQrPlaceholder.Visibility = Visibility.Visible;
		UpdateBiliStatus();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_bili_delete"]);
		}
	}

	private void BiliRefreshButton_Click(object sender, RoutedEventArgs e)
	{
		UpdateBiliStatus();
	}
}
