using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicBox;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public class HotkeyRow
{
	public string Key { get; set; } = "";
	public string Label { get; set; } = "";
	public string Shortcut { get; set; } = "";
}

public partial class SettingsPage : UserControl, IMusicPage
{
	private bool _ready;

	private bool _loggingIn;

	public SettingsPage()
	{
		InitializeComponent();
	}

	public void OnNavigatedTo()
	{
		var s = SettingsManager.Instance;
		_ready = false;

		UpdateThemeUI();
		BgDarknessSlider.Value = Math.Clamp(s.BackgroundDarkness, 0.1, 0.8);
		BgDarknessText.Text = (int)Math.Round(s.BackgroundDarkness * 100.0) + "%";
		BgVideoVolLabel.Text = Loc.Default["set_bg_video_vol"];
		BgVideoVolumeSlider.Value = Math.Clamp(s.BgVideoVolume, 0.0, 1.0);
		BgVideoVolText.Text = (int)Math.Round(Math.Clamp(s.BgVideoVolume, 0.0, 1.0) * 100.0) + "%";
		BgSwingCheckBox.IsChecked = s.BackgroundSwing;
		BgOpacityLabel.Text = Loc.Default["set_bg_opacity"];
		BgOpacitySlider.Value = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
		BgOpacityText.Text = (int)Math.Round(Math.Clamp(s.BackgroundOpacity, 0.0, 1.0) * 100.0) + "%";
		BgSwingAmpLabel.Text = Loc.Default["set_bg_swing_amp"];
		BgSwingAmpSlider.Value = Math.Clamp(s.SwingAmplitude, 0.0, 100.0);
		BgSwingAmpText.Text = ((int)Math.Round(Math.Clamp(s.SwingAmplitude, 0.0, 100.0))).ToString();
		UpdateBgVideoFolderText();
		BgPathText.Text = string.IsNullOrWhiteSpace(s.BackgroundPath)
			? Loc.Default["set_bg_default"]
			: s.BackgroundPath;

		BgTypeCombo.SelectedIndex = string.Equals(s.BackgroundType, "video", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
		UpdateChooseButtonLabel();

		LanguageCombo.SelectedIndex = (s.Language ?? "zh-CN") switch
		{
			"zh-TW" => 1,
			"en" => 2,
			"ru" => 3,
			"ja" => 4,
			"ko" => 5,
			_ => 0
		};

		_syncThemeMode = true;
		ThemeModeCombo.SelectedIndex = (s.ThemeMode ?? "dark") switch
		{
			"light" => 1,
			"system" => 2,
			_ => 0
		};
		_syncThemeMode = false;

		UpdateBiliStatus();
		UpdateCookieUI();
		UpdateHobbyUI();
		UpdateBehaviorUI();
		UpdateHotkeyUI();
		UpdateDebugUI();
		UpdateLyricFontUI();
		UpdateLyricAnimUI(reloadFromDisk: true);
		UpdateLyricColorUI();
		_ready = true;
	}

	private bool _syncLyricFont;

	/// <summary>歌词字体下拉。列表是按本机实际装了什么动态填的，
	/// 所以不同系统上看到的项数不一样——这是故意的：把没装的字体列出来
	/// 让人选，选了也是静默退回默认，不如一开始就不给。</summary>
	private void UpdateLyricFontUI()
	{
		LyricFontTitleText.Text = Loc.Default["set_lyric_font_title"];
		LyricFontLabelText.Text = Loc.Default["set_lyric_font"];
		LyricFontHintText.Text = Loc.Default["set_lyric_font_sub"];

		_syncLyricFont = true;
		LyricFontCombo.Items.Clear();
		string cur = SettingsManager.Instance.LyricFontFamily;
		int idx = 0;
		var list = LyricFonts.Available();
		for (int i = 0; i < list.Count; i++)
		{
			var item = new ComboBoxItem
			{
				Content = list[i].Display,
				Tag = list[i].Family
			};
			LyricFontCombo.Items.Add(item);
			if (string.Equals(list[i].Family, cur, StringComparison.OrdinalIgnoreCase)) idx = i;
		}
		LyricFontCombo.SelectedIndex = idx;
		_syncLyricFont = false;
	}

	// ── 歌词动画模板 ──────────────────────────────────────────────

	/// <summary>ComboBox 里的包装项。不能用 ComboBoxItem 直接当数据源：
	/// 那样 Description 挂不上去，下拉右边也没地方放说明。</summary>
	private sealed class AnimItem
	{
		public required LyricAnimTemplate Template { get; init; }
		public string Display => Template.IsBuiltIn
			? Template.Name
			: $"{Template.Name}（{Path.GetFileName(Template.Folder)}）";
		public string Desc => Template.Description;
	}

	private bool _syncAnim;

	/// <summary>把内置 + 文件夹里加载到的模板铺到下拉里。
	/// 会读磁盘（LoadFolder 扫目录），所以只在真正要刷新时调，
	/// 不是每次切页面都调。</summary>
	private void UpdateLyricAnimUI(bool reloadFromDisk)
	{
		LyricAnimTitle.Text = Loc.Default["set_lyric_anim_title"];
		LyricAnimHintText.Text = Loc.Default["set_lyric_anim_sub"];
		LyricAnimFolderTitle.Text = Loc.Default["set_lyric_anim_folder"];
		LyricAnimPickFolderButton.Content = Loc.Default["set_lyric_anim_pick"];
		LyricAnimReloadButton.Content = Loc.Default["set_lyric_anim_reload"];
		LyricAnimFolderHintText.Text = Loc.Default["set_lyric_anim_folder_hint"];
		LyricColorTitle.Text = Loc.Default["set_lyric_color_title"];
		LyricColorHintText.Text = Loc.Default["set_lyric_color_sub"];
		LyricColorLabel.Text = Loc.Default["set_lyric_color_label"];
		LyricColorApplyButton.Content = Loc.Default["set_lyric_color_apply"];
		LyricColorResetButton.Content = Loc.Default["set_lyric_color_reset"];

		var s = SettingsManager.Instance;
		if (reloadFromDisk)
		{
			LyricAnimTemplates.LoadFolder(s.LyricAnimTemplateFolder);
		}
		List<LyricAnimTemplate> all = LyricAnimTemplates.All(s.LyricAnimTemplateFolder);

		_syncAnim = true;
		LyricAnimCombo.Items.Clear();
		int sel = 0;
		for (int i = 0; i < all.Count; i++)
		{
			LyricAnimCombo.Items.Add(new AnimItem { Template = all[i] });
			if (string.Equals(all[i].Name, s.LyricAnimTemplate, StringComparison.Ordinal)) sel = i;
		}
		LyricAnimCombo.SelectedIndex = sel;
		_syncAnim = false;
		LyricAnimFolderBox.Text = s.LyricAnimTemplateFolder;

		if (LyricAnimCombo.SelectedItem is AnimItem cur)
		{
			LyricAnimDescText.Text = cur.Desc;
		}
	}

	private void LyricAnimCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// 程序回填 SelectedIndex 时也会走这里，靠这个标志挡住
		if (_syncAnim || !_ready) return;
		if (LyricAnimCombo.SelectedItem is not AnimItem item) return;
		LyricAnimDescText.Text = item.Desc;
		var s = SettingsManager.Instance;
		if (string.Equals(s.LyricAnimTemplate, item.Template.Name, StringComparison.Ordinal)) return;
		s.LyricAnimTemplate = item.Template.Name;
		SettingsManager.Save(s);
		// 歌词窗切句时才读模板，所以不用重启；但正在跑的那句缩放/摆动
		// 状态得立刻收拾干净，不然两套动画的变换会互相打架。
		LyricRuntime.RefreshAnimation();
	}

	private void LyricAnimPickFolderButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		string? picked = null;
		try
		{
			using var dlg = new System.Windows.Forms.FolderBrowserDialog
			{
				Description = Loc.Default["set_lyric_anim_folder"],
				UseDescriptionForTitle = true,
				ShowNewFolderButton = true
			};
			if (!string.IsNullOrWhiteSpace(s.LyricAnimTemplateFolder)
				&& Directory.Exists(s.LyricAnimTemplateFolder))
			{
				dlg.SelectedPath = s.LyricAnimTemplateFolder;
			}
			if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
			{
				picked = dlg.SelectedPath;
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "打开模板文件夹选择器失败");
			picked = null;
		}
		if (string.IsNullOrWhiteSpace(picked)) return;

		// 换文件夹就把当前选的自定义模板名清掉——旧名字在新文件夹里
		// 根本不存在，留在设置里是个永远解析不到的死引用。
		bool hadCustom = !LyricAnimTemplates.Resolve(s.LyricAnimTemplate, null).IsBuiltIn
			|| (LyricAnimTemplates.Resolve(s.LyricAnimTemplate, s.LyricAnimTemplateFolder).Folder != null);
		s.LyricAnimTemplateFolder = picked;
		if (hadCustom) s.LyricAnimTemplate = "";
		SettingsManager.Save(s);

		int n = LyricAnimTemplates.LoadFolder(picked).Count;
		UpdateLyricAnimUI(reloadFromDisk: false);
		LyricRuntime.RefreshAnimation();
		// 结果直接写回那行说明文字：这里本来就在讲文件夹的事，
		// 另弹一个提示反而要用户再去对哪个窗。
		LyricAnimFolderHintText.Text = n > 0
			? string.Format(Loc.Default["lyric_anim_folder_set"], picked, n)
			: Loc.Default["lyric_anim_folder_none"];
	}

	private void LyricAnimReloadButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		// 重新加载 = 从磁盘再读一遍。Resolve 不缓存文件夹内容，
		// 所以这里只要重新铺一遍下拉就会拿到新文件。
		UpdateLyricAnimUI(reloadFromDisk: true);
		LyricRuntime.RefreshAnimation();
		int n = LyricAnimTemplates.LoadFolder(s.LyricAnimTemplateFolder).Count;
		LyricAnimFolderHintText.Text = n > 0
			? string.Format(Loc.Default["lyric_anim_folder_set"], s.LyricAnimTemplateFolder, n)
			: Loc.Default["lyric_anim_folder_none"];
	}

	// ── 歌词颜色 ─────────────────────────────────────────────────

	private void UpdateLyricColorUI()
	{
		LyricColorPreview.Text = Loc.Default["set_lyric_color_preview"];
		LyricColorBox.Text = SettingsManager.Instance.LyricFontColor;
		ApplyLyricColorPreview();
	}

	/// <summary>预览条把输入框里的字按解析出来的颜色显示，
	/// 让用户在点「应用」之前就能看见效果——
	/// 颜色这种东西光看色值框是判断不出好坏的。</summary>
	private void ApplyLyricColorPreview()
	{
		string raw = LyricColorBox.Text.Trim();
		if (raw.Length == 0)
		{
			LyricColorPreview.Foreground =
				(System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
			return;
		}
		try
		{
			LyricColorPreview.Foreground =
				new System.Windows.Media.SolidColorBrush(
					(System.Windows.Media.Color)System.Windows.Media.ColorConverter
						.ConvertFromString(raw));
		}
		catch
		{
			LyricColorPreview.Foreground =
				(System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
		}
	}

	/// <summary>提交颜色。返回 false 表示输入没法用，调用方负责提示。</summary>
	private bool CommitLyricColor()
	{
		string raw = LyricColorBox.Text.Trim();
		var s = SettingsManager.Instance;
		if (raw.Length == 0)
		{
			if (s.LyricFontColor.Length == 0) return true;
			s.LyricFontColor = "";
			SettingsManager.Save(s);
			LyricRuntime.RefreshAppearance();
			return true;
		}
		if (!SettingsManager.IsHexColor(raw))
		{
			LyricColorHintText.Text = Loc.Default["set_lyric_color_bad"];
			return false;
		}
		LyricColorHintText.Text = Loc.Default["set_lyric_color_sub"];
		string norm = NormalizeHex(raw);
		if (string.Equals(s.LyricFontColor, norm, StringComparison.OrdinalIgnoreCase)) return true;
		s.LyricFontColor = norm;
		SettingsManager.Save(s);
		LyricRuntime.RefreshAppearance();
		return true;
	}

	/// <summary>#abc → #aabbcc。短色值 WPF 自己认得，但存进设置统一存长格式，
	/// 免得同一个颜色在文件里有两种写法、比对时判成两个不同的值。</summary>
	private static string NormalizeHex(string raw)
	{
		string h = raw.Trim().TrimStart('#');
		if (h.Length == 3)
		{
			h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
		}
		return "#" + h.ToUpperInvariant();
	}

	private void LyricColorApplyButton_Click(object sender, RoutedEventArgs e)
	{
		if (CommitLyricColor())
		{
			ApplyLyricColorPreview();
		}
	}

	private void LyricColorResetButton_Click(object sender, RoutedEventArgs e)
	{
		LyricColorBox.Text = "";
		ApplyLyricColorPreview();
		CommitLyricColor();
	}

	private void LyricColorBox_LostFocus(object sender, RoutedEventArgs e)
	{
		ApplyLyricColorPreview();
		CommitLyricColor();
	}

	private void LyricColorBox_KeyDown(object sender, KeyEventArgs e)
	{
		// 输入过程中不能每次按键都存盘 + 推一次歌词窗刷新，
		// 一边打字一边重排歌词太卡。只在回车时提交。
		if (e.Key != Key.Enter) return;
		if (CommitLyricColor())
		{
			ApplyLyricColorPreview();
		}
		e.Handled = true;
	}

	private void LyricFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// 程序回填 SelectedIndex 时也会走这里，靠这个标志挡住
		if (_syncLyricFont || !_ready) return;
		if (LyricFontCombo.SelectedItem is not ComboBoxItem { Tag: string family }) return;
		var s = SettingsManager.Instance;
		if (string.Equals(s.LyricFontFamily, family, StringComparison.OrdinalIgnoreCase)) return;
		s.LyricFontFamily = family;
		SettingsManager.Save(s);
		// 歌词窗是 Hide 不是 Close，不推一下得手动关一次重开才看得到
		LyricRuntime.RefreshAppearance();
	}

	private void UpdateDebugUI()
	{
		DbgTitleText.Text = Loc.Default["set_dbg_title"];
		DbgLogButton.Content = Loc.Default["set_dbg_log"];
		DbgMusicFolderButton.Content = Loc.Default["set_dbg_lib"];
		DbgDataFolderButton.Content = Loc.Default["set_dbg_data"];
		DbgClearCacheButton.Content = Loc.Default["set_dbg_cache"];
	}

	private bool _syncDarkness;

	private void UpdateThemeUI()
	{
		var s = SettingsManager.Instance;
		string accent = (s.AccentColor ?? "").Trim().ToUpperInvariant();
		int idx = 0;
		for (int i = 0; i < ThemeAccentCombo.Items.Count; i++)
		{
			if (ThemeAccentCombo.Items[i] is ComboBoxItem item && (item.Tag as string)?.ToUpperInvariant() == accent)
			{
				idx = i;
				break;
			}
		}
		ThemeAccentCombo.SelectedIndex = idx;
		_syncDarkness = true;
		ThemeDarknessSlider.Value = Math.Clamp(s.BackgroundDarkness, 0.1, 0.8);
		ThemeDarknessText.Text = (int)Math.Round(ThemeDarknessSlider.Value * 100.0) + "%";
		_syncDarkness = false;
	}

	private bool _syncThemeMode;

	private void ThemeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncThemeMode || !_ready || ThemeModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode })
		{
			return;
		}
		var s = SettingsManager.Instance;
		if (string.Equals(s.ThemeMode, mode, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		s.ThemeMode = mode;
		SettingsManager.Save(s);
		ThemeManager.ApplyFromSettings();
		// 页面是用代码建出来一行的（TrackRows），换完主题得让当前页重建一次才跟上新配色。
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyAccentTheme();
		}
	}

	private void ThemeAccentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)	{
		if (!_ready || ThemeAccentCombo.SelectedItem is not ComboBoxItem { Tag: string hex })
		{
			return;
		}
		var s = SettingsManager.Instance;
		if (string.Equals(s.AccentColor, hex, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		s.AccentColor = hex;
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyAccentTheme();
		}
	}

	private void ThemeDarknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready || _syncDarkness)
		{
			return;
		}
		var s = SettingsManager.Instance;
		s.BackgroundDarkness = Math.Round(e.NewValue, 2);
		ThemeDarknessText.Text = (int)Math.Round(s.BackgroundDarkness * 100.0) + "%";
		SettingsManager.Save(s);
		_syncDarkness = true;
		if (BgDarknessSlider != null)
		{
			BgDarknessSlider.Value = s.BackgroundDarkness;
		}
		_syncDarkness = false;
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
		}
	}

	private static string AppDataDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBox");

	private void DbgLogButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(AppDataDir);
			Process.Start("explorer.exe", "/select,\"" + Path.Combine(AppDataDir, "log.txt") + "\"");
		}
		catch
		{
		}
	}

	private void DbgMusicFolderButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(MusicService.MusicFolder);
			Process.Start("explorer.exe", "\"" + MusicService.MusicFolder + "\"");
		}
		catch
		{
		}
	}

	private void DbgDataFolderButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(AppDataDir);
			Process.Start("explorer.exe", "\"" + AppDataDir + "\"");
		}
		catch
		{
		}
	}

	private void DbgClearCacheButton_Click(object sender, RoutedEventArgs e)
	{
		ClearDownloadsCache();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_dbg_cache_done"]);
		}
	}

	/// <summary>清除在线平台下载缓存（网易云/QQ/酷狗），B站音乐库文件与设置保留。</summary>
	private static void ClearDownloadsCache()
	{
		try
		{
			string cacheRoot = Path.Combine(AppDataDir, "cache");
			if (Directory.Exists(cacheRoot))
			{
				Directory.Delete(cacheRoot, true);
			}
		}
		catch
		{
		}
	}

	private void UpdateHobbyUI()
	{
		HobbyTitle.Text = Loc.Default["set_hobby_title"];
		HobbySub.Text = Loc.Default["set_hobby_sub"];
		HobbySaveButton.Content = Loc.Default["set_hobby_save"];
		HobbyBox.Text = SettingsManager.Instance.HobbyKeywords ?? "";
	}

	private void HobbySaveButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.HobbyKeywords = (HobbyBox.Text ?? "").Trim();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_hobby_saved"]);
		}
	}

	private void UpdateCookieUI()
	{
		var s = SettingsManager.Instance;
		NeteaseCookieTitle.Text = Loc.Default["set_netease_title"];
		NeteaseCookieBox.Text = s.NeteaseCookie ?? "";
		NeteaseCookieBox.ToolTip = Loc.Default["set_netease_cookie_ph"];
		NeteaseCookieSaveButton.Content = Loc.Default["set_netease_save"];
		NeteaseCookieClearButton.Content = Loc.Default["set_netease_clear"];
		NeteaseBrowserLoginButton.Content = Loc.Default["set_login_open"] + " 网易云";
		NeteaseFetchCookieButton.Content = Loc.Default["set_login_fetch"];

		BiliCookieTitle.Text = Loc.Default["set_bili_cookie_title"];
		BiliCookieSub.Text = Loc.Default["set_bili_cookie_sub"];
		BiliCookieBox.Text = s.BiliCookie ?? "";
		BiliCookieBox.ToolTip = Loc.Default["set_bili_cookie_ph"];
		BiliCookieSaveButton.Content = Loc.Default["set_bili_cookie_save"];
		BiliCookieClearButton.Content = Loc.Default["set_bili_cookie_clear"];
		BiliBrowserLoginButton.Content = Loc.Default["set_login_open"] + " B站";
		BiliFetchCookieButton.Content = Loc.Default["set_login_fetch"];

		QqCookieTitle.Text = Loc.Default["set_qq_title"];
		QqCookieBox.Text = s.QqCookie ?? "";
		QqCookieBox.ToolTip = Loc.Default["set_qq_ph"];
		QqCookieSaveButton.Content = Loc.Default["set_qq_save"];
		QqCookieClearButton.Content = Loc.Default["set_qq_clear"];
		QqBrowserLoginButton.Content = Loc.Default["set_login_open"] + " QQ音乐";
		QqFetchCookieButton.Content = Loc.Default["set_login_fetch"];

		KugouCookieTitle.Text = Loc.Default["set_kugou_title"];
		KugouNickBox.Text = s.KugouNick ?? "";
		KugouCookieBox.Text = s.KugouCookie ?? "";
		KugouCookieBox.ToolTip = Loc.Default["set_kugou_cookie_ph"];
		KugouCookieSaveButton.Content = Loc.Default["set_kugou_save"];
		KugouCookieClearButton.Content = Loc.Default["set_kugou_clear"];
	}

	/// <summary>用默认浏览器打开登录页（程序不再内置浏览器），并提示回来读取。</summary>
	private void OpenBrowserLogin(string url)
	{
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

	private void QqBrowserLoginButton_Click(object sender, RoutedEventArgs e)
	{
		OpenBrowserLogin("https://y.qq.com/");
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
		OpenBrowserLogin("https://passport.bilibili.com/login");
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
		OpenBrowserLogin("https://music.163.com/#/login");
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

	private void UpdateBehaviorUI()
	{
		var s = SettingsManager.Instance;
		BehaviorTitleText.Text = Loc.Default["set_behavior_title"];
		AutoStartCheckBox.Content = Loc.Default["set_autostart"];
		StartTrayCheckBox.Content = Loc.Default["set_start_tray"];
		ShowTrayIconCheckBox.Content = Loc.Default["set_tray_icon"];
		StartAnimCheckBox.Content = Loc.Default["set_start_anim"];
		DebugModeCheckBox.Content = Loc.Default["set_debug"];

		AutoStartCheckBox.IsChecked = Services.AutoStart.IsEnabled();
		StartTrayCheckBox.IsChecked = s.StartMinimizedToTray;
		ShowTrayIconCheckBox.IsChecked = s.ShowTrayIcon;
		StartAnimCheckBox.IsChecked = s.ShowStartupAnimation;
		DebugModeCheckBox.IsChecked = s.DebugMode;
	}

	private void AutoStartCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		Services.AutoStart.SetEnabled(true);
	}

	private void AutoStartCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		Services.AutoStart.SetEnabled(false);
	}

	private void StartTrayCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		SetStartTray(true);
	}

	private void StartTrayCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		SetStartTray(false);
	}

	private void SetStartTray(bool value)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.StartMinimizedToTray = value;
		SettingsManager.Save(s);
	}

	private void StartAnimCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		SetStartAnim(true);
	}

	private void StartAnimCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		SetStartAnim(false);
	}

	private void SetStartAnim(bool value)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.ShowStartupAnimation = value;
		SettingsManager.Save(s);
	}

	private void DebugModeCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		SetDebugMode(true);
	}

	private void DebugModeCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		SetDebugMode(false);
	}

	private void SetDebugMode(bool value)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.DebugMode = value;
		SettingsManager.Save(s);
		Services.HotkeyManager.RegisterAll();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["toast_debug"] + (value ? "ON" : "OFF"));
		}
	}

	private void UpdateChooseButtonLabel()
	{
		bool video = BgTypeCombo.SelectedIndex == 1;
		ChooseBgButton.Content = Loc.Default[video ? "set_bg_choose_video" : "set_bg_choose"];
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

	private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code })
		{
			return;
		}
		var s = SettingsManager.Instance;
		if (string.Equals(s.Language, code, StringComparison.Ordinal))
		{
			return;
		}
		s.Language = code;
		SettingsManager.Save(s);
		Loc.Default.SetCurrent(code);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["toast_language"]);
		}
	}

	private void BgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready)
		{
			return;
		}
		var s = SettingsManager.Instance;
		s.BackgroundType = BgTypeCombo.SelectedIndex == 1 ? "video" : "image";
		SettingsManager.Save(s);
		UpdateChooseButtonLabel();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
		}
	}

	private void ChooseBgButton_Click(object sender, RoutedEventArgs e)
	{
		bool video = BgTypeCombo.SelectedIndex == 1;
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = Loc.Default[video ? "set_bg_choose_video" : "set_bg_choose"],
			Filter = video
				? "视频文件 (*.mp4;*.mov;*.mkv;*.wmv;*.webm)|*.mp4;*.mov;*.mkv;*.wmv;*.webm"
				: "图片文件 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
			CheckFileExists = true
		};
		if (dialog.ShowDialog() == true)
		{
			var s = SettingsManager.Instance;
			s.BackgroundPath = dialog.FileName;
			SettingsManager.Save(s);
			BgPathText.Text = dialog.FileName;
			if (Application.Current.MainWindow is MainWindow mw)
			{
				mw.ApplyBackground();
			}
		}
	}

	private void BgDarknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready || _syncDarkness)
		{
			return;
		}
		var s = SettingsManager.Instance;
		s.BackgroundDarkness = Math.Round(e.NewValue, 2);
		BgDarknessText.Text = (int)Math.Round(s.BackgroundDarkness * 100.0) + "%";
		SettingsManager.Save(s);
		_syncDarkness = true;
		if (ThemeDarknessSlider != null)
		{
			ThemeDarknessSlider.Value = s.BackgroundDarkness;
		}
		_syncDarkness = false;
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
		}
	}

	private void BgSwingCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		if (_ready)
		{
			SetBackgroundSwing(true);
		}
	}

	private void BgSwingCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		if (_ready)
		{
			SetBackgroundSwing(false);
		}
	}

	private void SetBackgroundSwing(bool value)
	{
		var s = SettingsManager.Instance;
		s.BackgroundSwing = value;
		// 标记「用户亲手选过」，这样启动时的旧默认迁移就不会再把这个开关强制关掉
		s.BackgroundSwingChosen = true;
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
		}
	}

	private void BgOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.BackgroundOpacity = Math.Round(e.NewValue, 2);
		BgOpacityText.Text = (int)Math.Round(s.BackgroundOpacity * 100.0) + "%";
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
		}
	}

	private void BgSwingAmpSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.SwingAmplitude = Math.Round(e.NewValue, 0);
		BgSwingAmpText.Text = ((int)s.SwingAmplitude).ToString();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
		}
	}

	private void UpdateBgVideoFolderText()
	{
		var s = SettingsManager.Instance;
		if (BgVideoFolderText == null) return;
		BgVideoFolderText.Text = string.IsNullOrWhiteSpace(s.VideoFolder)
			? Loc.Default["set_bg_default"]
			: s.VideoFolder;
	}

	private void BgVideoFolderButton_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.OpenFolderDialog
		{
			Title = Loc.Default["set_bg_video_folder"]
		};
		var s = SettingsManager.Instance;
		if (!string.IsNullOrWhiteSpace(s.VideoFolder)
			&& System.IO.Directory.Exists(s.VideoFolder))
		{
			dialog.InitialDirectory = s.VideoFolder;
		}
		if (dialog.ShowDialog() == true)
		{
			s.VideoFolder = dialog.FolderName;
			SettingsManager.Save(s);
			UpdateBgVideoFolderText();
		}
	}

	private void ShowTrayIconCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.ShowTrayIcon = true;
		SettingsManager.Save(s);
		ApplyTrayIconVisibility();
	}

	private void ShowTrayIconCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.ShowTrayIcon = false;
		SettingsManager.Save(s);
		ApplyTrayIconVisibility();
	}

	private void ApplyTrayIconVisibility()
	{
		try
		{
			TrayIconService.SetVisible(SettingsManager.Instance.ShowTrayIcon);
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "切换托盘图标失败");
		}
	}

	private void BgVideoVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.BgVideoVolume = Math.Clamp(e.NewValue, 0.0, 1.0);
		BgVideoVolText.Text = (int)Math.Round(s.BgVideoVolume * 100.0) + "%";
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyBackground();
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

	private void UpdateHotkeyUI()
	{
		HotkeyTitleText.Text = Loc.Default["set_hotkey_title"];
		HotkeyHintText.Text = Loc.Default["set_hotkey_hint"];
		HotkeyList.Children.Clear();
		var defs = new[]
		{
			new HotkeyRow { Key = "next", Label = Loc.Default["mt_next"] },
			new HotkeyRow { Key = "prev", Label = Loc.Default["mt_prev"] },
			new HotkeyRow { Key = "playpause", Label = Loc.Default["mt_playpause"] },
			new HotkeyRow { Key = "fav", Label = Loc.Default["act_fav"] },
			new HotkeyRow { Key = "toggle", Label = Loc.Default["set_hotkey_toggle"] }
		};
		foreach (HotkeyRow row in defs)
		{
			string cur = CurrentShortcut(row.Key);
			var grid = new Grid { Margin = new Thickness(0) };
			grid.Margin = new Thickness(0, 3, 0, 3);
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			var label = new TextBlock
			{
				Text = row.Label,
				FontSize = 13,
				Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetColumn(label, 0);
			grid.Children.Add(label);

			var glyph = new TextBlock
			{
				Text = cur,
				FontSize = 13,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			var btn = new Button
			{
				MinWidth = 180,
				Padding = new Thickness(12, 5, 12, 5),
				HorizontalAlignment = HorizontalAlignment.Left,
				Cursor = System.Windows.Input.Cursors.Hand,
				Tag = row.Key,
				Style = (Style)FindResource("GhostButtonStyle"),
				Content = glyph
			};
			btn.Click += RebindButton_Click;
			Grid.SetColumn(btn, 1);
			grid.Children.Add(btn);
			HotkeyList.Children.Add(grid);
		}
	}

	private void RebindButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button btn || btn.Tag is not string key)
		{
			return;
		}
		_ready = false;
		btn.Focus();
		string keyCaption = key switch
		{
			"next" => Loc.Default["mt_next"],
			"prev" => Loc.Default["mt_prev"],
			"playpause" => Loc.Default["mt_playpause"],
			"fav" => Loc.Default["act_fav"],
			_ => Loc.Default["set_hotkey_toggle"]
		};
		btn.Content = new TextBlock { Text = Loc.Default.Format("set_hotkey_press", keyCaption), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
		btn.KeyDown += RebindKeyDown;
		btn.LostFocus += RebindLostFocus;
		btn.Tag = "capture:" + key;
	}

	private void RebindKeyDown(object sender, KeyEventArgs e)
	{
		if (sender is not Button btn)
		{
			return;
		}
		e.Handled = true;
		ModifierKeys mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
		if (mods == 0 || (e.Key == Key.System ? KeyInterop.VirtualKeyFromKey(Key.System) == 0 : e.Key == System.Windows.Input.Key.None) && e.KeyboardDevice.Modifiers == ModifierKeys.None)
		{
			return;
		}
		Key realKey = e.Key == Key.System ? e.SystemKey : e.Key;
		if (realKey == Key.LeftCtrl || realKey == Key.RightCtrl || realKey == Key.LeftAlt || realKey == Key.RightAlt ||
			realKey == Key.LeftShift || realKey == Key.RightShift || realKey == Key.LWin || realKey == Key.RWin)
		{
			return;
		}
		EndRebind(btn, HotkeyManager.BuildComboText(mods, realKey));
	}

	private void RebindLostFocus(object sender, RoutedEventArgs e)
	{
		if (sender is Button btn)
		{
			EndRebind(btn, null);
		}
	}

	private void EndRebind(Button btn, string? newShortcut)
	{
		btn.KeyDown -= RebindKeyDown;
		btn.LostFocus -= RebindLostFocus;
		string key;
		if (btn.Tag is string tag && tag.StartsWith("capture:", StringComparison.Ordinal))
		{
			key = tag.Substring("capture:".Length);
		}
		else
		{
			return;
		}
		btn.Tag = key;
		if (string.IsNullOrWhiteSpace(newShortcut))
		{
			btn.Content = new TextBlock { Text = CurrentShortcut(key), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
			_ready = true;
			return;
		}
		// 校验组合键
		var (ok, mods, vk) = HotkeyManager.ParseCombo(newShortcut);
		if (!ok || mods == 0 || vk == 0)
		{
			btn.Content = new TextBlock { Text = Loc.Default["set_hotkey_invalid"], FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
			_ready = true;
			return;
		}
		var s = SettingsManager.Instance;
		s.Hotkeys ??= new System.Collections.Generic.Dictionary<string, string>();
		s.Hotkeys[key] = newShortcut;
		SettingsManager.Save(s);
		HotkeyManager.RegisterAll();
		btn.Content = new TextBlock { Text = newShortcut, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default.Format("set_hotkey_saved", newShortcut));
		}
		_ready = true;
	}

	private string CurrentShortcut(string key)
	{
		var s = SettingsManager.Instance;
		string? eff = Services.HotkeyManager.EffectiveCombo(key);
		if (!string.IsNullOrWhiteSpace(eff)) return eff;
		if (s.Hotkeys != null && s.Hotkeys.TryGetValue(key, out string? cur) && !string.IsNullOrWhiteSpace(cur))
		{
			return cur + " (未生效)";
		}
		return Loc.Default["set_hotkey_none"];
	}
}
