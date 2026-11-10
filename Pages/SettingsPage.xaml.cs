using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MusicBox;
using MusicBox.Controls;
using MusicBox.Services;
using MusicBox.Settings;

namespace MusicBox.Pages;

public class HotkeyRow
{
	public string Key { get; set; } = "";
	public string Label { get; set; } = "";
	public string Shortcut { get; set; } = "";

	/// <summary>true = 局内键（裸键，存 Key 枚举名）。
	/// 键名里 prev/next/playpause 和全局键重名，所以行与行不能只靠 Key 区分，
	/// 按钮 Tag 后面挂个 LocalTagSuffix 标记。</summary>
	public bool Local { get; set; }
}

public partial class SettingsPage : UserControl, IMusicPage
{
	/// <summary>标记「这个按钮改的是局内键」的 Tag 后缀。
	/// 全局键和局内键的键名会重名（都是 prev/next/playpause），
	/// 光看 Tag 分不出是哪一种，改键逻辑就会走错分支。</summary>
	private const string LocalTagSuffix = "|local";

	private bool _ready;

	/// <summary>歌词颜色渲染速度预览用的节拍器。全局只建一次，切页回来接着用。</summary>
	private DispatcherTimer? _lyricPreviewTimer;

	/// <summary>预览里示范用的「整句时长」：颜色在这个窗口内按 ProgressOf 同一套
	/// 规则扫过去。取滑块上限，保证每一档速度在预览里都看得出区别。</summary>
	private const double RevealPreviewLineMs = 5000.0;

	/// <summary>刷满后停顿一下再循环，不然整块一直闪。</summary>
	private const double RevealPreviewHoldMs = 900.0;

	/// <summary>主题下拉第一次展开要塞一堆带画刷的 ComboBoxItem。
	/// 曾经在这里做「进页面时空闲时各开合一次」的预热，
	/// 但 ComboBox 浮层是真实顶层窗口：预热那次开合本身就会出现
	/// 一个游离 HWND，首次还拿到错的锚点，看得见地闪到屏幕左上角，
	/// 用户以为坏了。预热已删，点击时的那一点点构建现状可接受。</summary>

	public SettingsPage()
	{
		InitializeComponent();
		Unloaded += (_, _) => StopMemMonitor();
	}

	/// <summary>把这页所有还开着的下拉浮层收掉。
	///
	/// 两个毛病都归它：
	/// 一是「漂移」—— ComboBox 的浮层是独立窗口，只认开它那一刻的锚点位置，
	/// 页面一滚它不会跟着重算，于是选项列表就悬在原地、和它本该属于的那个
	/// 下拉框对不上了。所以页面滚动时直接收掉。
	/// 二是「卡在别的程序上面」—— 同一个独立 HWND，程序切走了它不会跟着沉下去。
	/// 切走时由 MainWindow.Window_Deactivated 调这里收尾。
	/// 只收下拉框和弹出层，不动 CheckBox：勾选框不是浮层，收了等于把用户的勾给改了。</summary>
	public void CloseFloatingDropdowns()
	{
		foreach (ComboBox combo in FindDropdowns(this))
		{
			if (combo.IsDropDownOpen)
			{
				combo.IsDropDownOpen = false;
			}
		}
	}

	private static IEnumerable<ComboBox> FindDropdowns(DependencyObject root)
	{
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is ComboBox combo)
			{
				yield return combo;
			}
			foreach (ComboBox nested in FindDropdowns(child))
			{
				yield return nested;
			}
		}
	}

	/// <summary>页面一滚就把开着的下拉收掉：它不会跟着锚点走，留着只会漂在原地。</summary>
	private void SettingsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.ExtentHeightChange == 0.0 && e.VerticalChange == 0.0)
		{
			return;
		}
		CloseFloatingDropdowns();
	}

	public void OnNavigatedTo()
	{
		// 切界面语言后要重跑一遍这些 Update*UI。
		// XAML 里的 {Binding ...Item[...]} 会跟着 Loc.PropertyChanged 自己刷，
		// 代码里 text.Text = Loc.Default[...] 的不会 —— 那是一次性赋值。
		// 所以只切语言不重跑的话，这一页有一半标题还是上一个语言的。
		Loc.Default.LanguageChanged -= OnLanguageChanged;
		Loc.Default.LanguageChanged += OnLanguageChanged;
		RefreshAllText();
		StartLyricRevealPreview();
		// 「调试与路径」区显隐 + 内存检测计时器启停都在这里统一处理：
		// 调试模式没开时整块隐藏、1s 采样计时器保持停止（省 CPU）。
		ApplyDebugSection();
	}

	private DateTime _lyricPreviewStart;

	/// <summary>颜色渲染预览：固定一句 3.2 秒的示例歌词，按 ProgressOf 同一套
	/// 规则（窗口 = min(整句时长, 设置值)）把亮色层从左往右擦出来，
	/// 刷满停一下再循环。滑块改完立刻变。</summary>
	private void StartLyricRevealPreview()
	{
		if (LyricRevealPreviewClipHost == null)
		{
			return;
		}
		// 首帧先裁掉，别让整句亮着闪一下再开始擦。
		LyricRevealPreviewClipHost.Clip = new RectangleGeometry(new Rect(0, 0, 0, 0));
		_lyricPreviewStart = DateTime.Now;
		if (_lyricPreviewTimer == null)
		{
			_lyricPreviewTimer = new DispatcherTimer(DispatcherPriority.Render);
			_lyricPreviewTimer.Interval = TimeSpan.FromMilliseconds(33);
			_lyricPreviewTimer.Tick += LyricRevealPreviewTick;
		}
		_lyricPreviewTimer.Start();
	}

	private void LyricRevealPreviewTick(object? sender, EventArgs e)
	{
		if (!IsVisible || LyricRevealPreviewClipHost == null || LyricRevealPreviewBase == null)
		{
			return;
		}
		double width = LyricRevealPreviewBase.ActualWidth;
		double height = LyricRevealPreviewBase.ActualHeight;
		if (width <= 0 || height <= 0)
		{
			return;
		}
		double cap = Math.Max(200, SettingsManager.Instance.LyricRevealMs);
		double window = Math.Min(RevealPreviewLineMs, cap);
		double cycle = RevealPreviewLineMs + RevealPreviewHoldMs;
		double t = (DateTime.Now - _lyricPreviewStart).TotalMilliseconds % cycle;
		double progress = Math.Clamp(t / window, 0, 1);
		LyricRevealPreviewClipHost.Clip =
			new RectangleGeometry(new Rect(0, 0, width * progress, height));
	}

	/// <summary>进页面时空闲优先级把两个主题下拉各开合一次，
	/// 让它们带画刷的选项容器先烘出来，用户真去点时不卡那一下。</summary>
	/// <summary>把这一页所有「代码里赋值的文案」按当前语言重新取一遍。</summary>
	private void OnLanguageChanged(object? sender, EventArgs e)
	{
		RefreshAllText(reloadTemplates: false);
	}

	/// <param name="reloadTemplates">要不要重新扫磁盘上的动画模板目录。
	/// 进页面要扫（用户可能刚放了新的 .json 进去），
	/// 切语言不用扫（模板没变，扫了只是白花 IO）。</param>
	private void RefreshAllText(bool reloadTemplates = true)
	{
		try
		{
		var s = SettingsManager.Instance;
		_ready = false;

		UpdateThemeUI();
		BgOpacityLabel.Text = Loc.Default["set_bg_opacity"];
		BgOpacitySlider.Value = Math.Clamp(s.BackgroundOpacity, 0.0, 1.0);
		BgOpacityText.Text = (int)Math.Round(Math.Clamp(s.BackgroundOpacity, 0.0, 1.0) * 100.0) + "%";
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

		UpdateHobbyUI();
		UpdateBehaviorUI();
		UpdateHotkeyUI();
		UpdateDebugUI();
		UpdateLyricFontUI();
		// 切语言不重读磁盘：模板本身没变，只是下拉里的字要换。
		UpdateLyricAnimUI(reloadFromDisk: reloadTemplates);
		UpdateLyricColorUI();
		_ready = true;
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "刷新设置页文案失败");
			_ready = true;
		}
	}

	private bool _syncLyricFont;

	/// <summary>歌词字体下拉。列表是按本机实际装了什么动态填的，
	/// 所以不同系统上看到的项数不一样——这是故意的：把没装的字体列出来
	/// 让人选，选了也是静默退回默认，不如一开始就不给。</summary>
	private void UpdateLyricFontUI()
	{
		// 原来这里还有一个 LyricFontTitleText（「歌词外观」的卡标题），
		// 现在字体那一行已经并进「歌词设置」区块，不再单独有标题了。
		LyricFontLabelText.Text = Loc.Default["set_lyric_font"];

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
		// 选中框兜底：万一某主题/样式没走 ItemTemplate，至少 ToString 也回显中文名，
		// 不会在框里裸奔成「MusicBox.Pages.SettingsPage+AnimItem」。
		public override string ToString() => Display;
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
		LyricRevealTitleText.Text = Loc.Default["set_lyric_reveal"];
		LyricRevealHintText.Text = Loc.Default["set_lyric_reveal_hint"];
		// 颜色区已精简：只留三个四字选项名（标签），标题/说明/应用/恢复主题
		// 按钮都从 XAML 撤掉了，这里也就不再给它们赋文案。
		LyricColorLabel.Text = Loc.Default["set_lyric_color_label"];
		LyricSungLabel.Text = Loc.Default["set_lyric_sung_label"];
		LyricTranslateLabel.Text = Loc.Default["set_lyric_translate_label"];

		var s = SettingsManager.Instance;
		LyricRevealSlider.Value = Math.Clamp(s.LyricRevealMs, 400, 5000);
		UpdateLyricRevealValueText();
		if (reloadFromDisk)
		{
			// 丢缓存，让下面 All() 真的重新读盘。
			// （原来这里是一次结果被丢掉的 LoadFolder 调用，纯浪费一次扫盘。）
			LyricAnimTemplates.Invalidate();
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

		int n = LyricAnimTemplates.All(picked).Count - LyricAnimTemplates.BuiltIn.Length;
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
		// 「重新加载」= 从磁盘再读一遍。Resolve 现在会缓存文件夹内容，
		// 所以必须先 Invalidate，否则这里只是把同一份缓存重新铺一遍，
		// 用户在外部改完 json 再点这个按钮不会有任何反应。
		UpdateLyricAnimUI(reloadFromDisk: true);
		LyricRuntime.RefreshAnimation();
		int n = LyricAnimTemplates.All(s.LyricAnimTemplateFolder).Count
			- LyricAnimTemplates.BuiltIn.Length;
		LyricAnimFolderHintText.Text = n > 0
			? string.Format(Loc.Default["lyric_anim_folder_set"], s.LyricAnimTemplateFolder, n)
			: Loc.Default["lyric_anim_folder_none"];
	}

	// ── 歌词颜色（底衬色 / 已唱色 / 翻译色）──────────────────────────────
	//
	// 三个色共用一套逻辑，所以抽成 ColorSlot：一份设置字段 + 一个色板容器
	// + 输入框 + 两个按钮。翻译色就是照这个套路直接多 new 一个 ColorSlot
	// 加上的，没有再复制一遍「校验 / 归一 / 存盘 / 刷界面」。

	/// <summary>预设色板。用户要的是「调色板面板」而不是一个色值输入框：
	/// 让用户自己想 #RRGGBB 是把开发的工作推给了用户。
	///
	private sealed class ColorSlot
	{
		public required Func<string> Get;

		public required Action<string> Set;

		public required string HintSub;

		public required string HintBad;

		public required WrapPanel Swatches;

		/// <summary>隐藏的色值载体：色板不再显示输入框，但提交/预览仍从它读当前值。</summary>
		public required TextBox Box;

		/// <summary>「自定义…」按钮：背景要染成当前选中的颜色。</summary>
		public Button? CustomButton;
	}

	// 三个 slot 都是 UpdateLyricColorUI 里 new 出来的，
	// 在那之前事件理论上不会触发；留个 null 是为了不给构造函数加一堆初始化，
	// 真触发时那些 null 判断也只是安静地什么都不做。
	private ColorSlot? _slotLit;

	private ColorSlot? _slotBase;

	private ColorSlot? _slotTr;

	// 取色器一个就够：三个色轮着用（同一时刻只有一个弹窗在改一个 slot）。
	private ColorPickerPopup? _colorPicker;

	private void UpdateLyricColorUI()
	{
		var s = SettingsManager.Instance;
		LyricColorBox.Text = s.LyricFontColor;
		LyricSungBox.Text = s.LyricSungColor;
		LyricTranslateBox.Text = s.LyricTranslateColor;

		_slotBase = new ColorSlot
		{
			Get = () => s.LyricFontColor,
			Set = v => { s.LyricFontColor = v; },
			HintSub = "set_lyric_color_sub",
			HintBad = "set_lyric_color_bad",
			Swatches = LyricColorSwatches,
			Box = LyricColorBox
		};
		_slotLit = new ColorSlot
		{
			Get = () => s.LyricSungColor,
			Set = v => { s.LyricSungColor = v; },
			HintSub = "set_lyric_sung_sub",
			HintBad = "set_lyric_sung_bad",
			Swatches = LyricSungSwatches,
			Box = LyricSungBox
		};
		_slotTr = new ColorSlot
		{
			Get = () => s.LyricTranslateColor,
			Set = v => { s.LyricTranslateColor = v; },
			HintSub = "set_lyric_translate_sub",
			HintBad = "set_lyric_translate_bad",
			Swatches = LyricTranslateSwatches,
			Box = LyricTranslateBox
		};

		BuildSwatches(_slotBase);
		BuildSwatches(_slotLit);
		BuildSwatches(_slotTr);
		RefreshSlot(_slotBase);
		RefreshSlot(_slotLit);
		RefreshSlot(_slotTr);
	}

	/// <summary>每组只铺两个按钮：A（跟随主题）+「自定义…」（调色盘入口）。
	/// 预设色板已按用户要求撤掉：自定义按钮本身就是取色器，选完立即生效，
	/// 不需要预设色、HEX 输入框或「应用」按钮。自定义按钮的背景由
	/// RefreshSlot 染成当前选中的颜色，顺带充当色值回显。</summary>
	private void BuildSwatches(ColorSlot slot)
	{
		try
		{
			slot.Swatches.Children.Clear();

			// A = 跟随主题（空值交还给主题）。用户指定要保留的就是这个按钮。
			// Tag 里放 slot，避免用父控件层级反查（层级一变就会改错色）。
			var follow = new Button
			{
				Style = TryFindResource("ColorSwatchButtonStyle") as Style,
				Margin = new Thickness(0, 0, 6, 4),
				Tag = new SwatchTag("", slot),
				ToolTip = Loc.Default["set_lyric_color_theme"],
				Content = new TextBlock { Text = "A", FontSize = 12, FontWeight = FontWeights.Bold }
			};
			follow.SetResourceReference(Control.BackgroundProperty, "CardBgBrush");
			if (follow.Content is TextBlock marker)
			{
				marker.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
			}
			follow.Click += Swatch_Click;
			slot.Swatches.Children.Add(follow);

			var custom = new Button
			{
				Style = TryFindResource("GhostButtonStyle") as Style,
				Margin = new Thickness(0, 0, 4, 4),
				Tag = slot,
				Content = new TextBlock { Text = Loc.Default["set_color_custom"], FontSize = 11.5 },
				ToolTip = Loc.Default["set_color_custom"]
			};
			custom.Click += CustomPick_Click;
			slot.Swatches.Children.Add(custom);
			slot.CustomButton = custom;
		}
		catch { }
	}

	/// <summary>「自定义…」：打开取色器弹窗，初始色回显成 SV/H 坐标。
	/// 当前是「跟随主题」（空值）时，拿主题里这个角色的实际颜色当初始坐标——
	/// 否则面板光标只能给个默认位置，用户从完全不相干的颜色开始拖。</summary>
	private void CustomPick_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: ColorSlot slot })
		{
			return;
		}
		string cur = NormalizeHex(slot.Box.Text);
		Color initial;
		if (cur.Length == 0)
		{
			string themeKey = ReferenceEquals(slot, _slotLit)
				? "AccentBrush"
				: ReferenceEquals(slot, _slotTr) ? "TextSecondaryBrush" : "TextPrimaryBrush";
			initial = (TryFindResource(themeKey) as SolidColorBrush)?.Color ?? Colors.White;
		}
		else
		{
			initial = (Color)ColorConverter.ConvertFromString(cur);
		}
		_colorPicker ??= new ColorPickerPopup();
		_colorPicker.Open(initial, (Button)sender, hex =>
		{
			slot.Box.Text = hex;
			if (CommitSlot(slot))
			{
				RefreshSlot(slot);
			}
		});
	}

	private void Swatch_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: SwatchTag tag })
		{
			return;
		}
		tag.Slot.Box.Text = tag.Hex;
		if (CommitSlot(tag.Slot))
		{
			RefreshSlot(tag.Slot);
		}
	}

	/// <summary>色块自带的数据：颜色值 + 它属于哪个色板。
	/// 见 <see cref="BuildSwatches"/> 里为什么不能用父控件反查。</summary>
	private sealed class SwatchTag
	{
		public string Hex { get; }

		public ColorSlot Slot { get; }

		public SwatchTag(string hex, ColorSlot slot)
		{
			Hex = hex;
			Slot = slot;
		}
	}

	/// <summary>把某个色的「当前值」刷到输入框、预览字和色块描边上。</summary>
	private void RefreshSlot(ColorSlot slot)
	{
		try
		{
			string cur = NormalizeHex(slot.Get());
			foreach (object child in slot.Swatches.Children)
			{
				if (child is not Button btn || btn.Tag is not SwatchTag tag)
				{
					continue;
				}
				bool on = NormalizeHex(tag.Hex) == cur;
				// 选中环画在色块外面一圈（ColorSwatchButtonStyle 的 Ring），
				// 不是给色块本身加描边 —— 给色块加描边会把底色往里挤一圈，
				// 浅色块看着就比相邻的色块小一圈。
				if (on)
				{
					btn.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
					btn.BorderThickness = new Thickness(2);
				}
				else
				{
					btn.ClearValue(Control.BorderBrushProperty);
					btn.BorderThickness = new Thickness(0);
				}
			}
			// 「自定义…」按钮兼作色值回显：选了具体颜色就把整个按钮染成那个色，
			// 文字按亮度自动取黑/白；空值（跟随主题）则退回 GhostButtonStyle 默认底。
			if (slot.CustomButton is Button custom)
			{
				string hex = NormalizeHex(slot.Get());
				if (hex.Length > 0)
				{
					try
					{
						Color c = (Color)ColorConverter.ConvertFromString(hex);
						custom.Background = new SolidColorBrush(c);
						double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
						if (custom.Content is TextBlock caption)
						{
							caption.Foreground = lum > 150 ? Brushes.Black : Brushes.White;
						}
					}
					catch { }
				}
				else
				{
					custom.ClearValue(Control.BackgroundProperty);
					if (custom.Content is TextBlock caption)
					{
						caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
					}
				}
			}
			RenderLyricPreview();
		}
		catch { }
	}

	/// <summary>一个预览同时显示两个颜色：左边一段用已唱色、右边一段用未唱色，
	/// 中间那道界就是弹窗里真正的擦除线，下面再带一行译文。
	///
	/// 以前是两个预览各跟一个颜色框，那样看不出两个色叠在一句话上是什么样，
	/// 而设置里最容易翻车的就是「这两个色配在一起刺不刺眼」——
	/// 分开看两个都正常，叠上去才发现已唱色比未唱色还暗。
	/// 文案走 Loc，所以切界面语言时主歌词和译文会一起换，
	/// 不会留着上一个语言的那句话（那才是「字体随界面语言变化」，
	/// 字是换语言了、句子还是旧的，比不换更糟）。</summary>
	private void RenderLyricPreview()
	{
		if (_slotBase == null || _slotLit == null)
		{
			return;
		}
		try
		{
			// ④：底部「歌词渲染」预览也用真实的未唱/已唱色（而非主题灰阶），
			// 让这段卡拉OK擦除演示和歌词窗里真正看到的颜色一致。
			UpdateRevealPreviewColors();
			string line = Loc.Default["set_lyric_preview_line"];
			// 切在半途：左边唱过、右边没唱。两段长度不同也无所谓，
			// 这里要的是「两种色在同一个元素里并排」这个效果。
			int cut = Math.Max(1, line.Length / 2);
			var sung = new Run(line[..cut]);
			var unsung = new Run(line[cut..]);
			sung.Foreground = PreviewBrush(_slotLit.Box.Text, "TextSecondaryBrush");
			unsung.Foreground = PreviewBrush(_slotBase.Box.Text, "TextPrimaryBrush");
			LyricPreviewMain.Inlines.Clear();
			LyricPreviewMain.Inlines.Add(sung);
			LyricPreviewMain.Inlines.Add(unsung);
			LyricPreviewSub.Text = Loc.Default["set_lyric_preview_sub"];
			// 译文行跟着翻译色走：预览是「三色同屏」的最后一环，
			// 只演示两个主色的话，翻译色挑完还得开一次歌词窗才知道什么样。
			LyricPreviewSub.Foreground = _slotTr == null
				? ThemeBrush("TextSecondaryBrush")
				: PreviewBrush(_slotTr.Box.Text, "TextSecondaryBrush");
		}
		catch { }
	}

	/// <summary>底部「歌词渲染」预览的着色：Base 层用未唱色（_slotBase），
	/// Lit 层（擦除扫过的那半句）用已唱色（_slotLit）。跟随主题时空串退回主题灰阶，
	/// 选了自定义色就显示该色 —— 和「三色同屏预览」「歌词窗」同一套配色口径。</summary>
	private void UpdateRevealPreviewColors()
	{
		if (LyricRevealPreviewBase == null || LyricRevealPreviewLit == null) return;
		if (_slotBase == null || _slotLit == null) return;
		try
		{
			LyricRevealPreviewBase.Foreground = PreviewBrush(_slotBase.Box.Text, "TextTertiaryBrush");
			LyricRevealPreviewLit.Foreground = PreviewBrush(_slotLit.Box.Text, "TextPrimaryBrush");
		}
		catch { }
	}

	/// <summary>把输入框里的字符串变成画刷；空串（跟随主题）或填错就退回主题色。
	/// 两个色板各自跟随主题时，用次级色画「已唱」那半段，
	/// 否则整句一个颜色、预览等于什么都没演示。</summary>
	private Brush PreviewBrush(string raw, string themeFallback)
	{
		raw = raw.Trim();
		if (raw.Length == 0)
		{
			return ThemeBrush(themeFallback);
		}
		try
		{
			return BrushFromHex(raw);
		}
		catch
		{
			return ThemeBrush(themeFallback);
		}
	}

	private Brush ThemeBrush(string key)
	{
		return TryFindResource(key) as Brush ?? Brushes.Gray;
	}

	private static SolidColorBrush BrushFromHex(string hex)
	{
		return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
	}

	/// <summary>提交某个色。返回 false 表示输入没法用，调用方负责提示。</summary>
	private bool CommitSlot(ColorSlot slot)
	{
		string raw = slot.Box.Text.Trim();
		var s = SettingsManager.Instance;
		string norm = "";
		if (raw.Length > 0)
		{
			if (!SettingsManager.IsHexColor(raw))
			{
				ShowHint(slot, slot.HintBad);
				return false;
			}
			norm = NormalizeHex(raw);
		}
		ShowHint(slot, slot.HintSub);
		if (string.Equals(slot.Get(), norm, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		slot.Set(norm);
		SettingsManager.Save(s);
		LyricRuntime.RefreshAppearance();
		return true;
	}

	private void ShowHint(ColorSlot slot, string key)
	{
		// 三行说明文字已从界面移除：正常提交（HintSub）静默通过；
		// 只有非法色值才用 Toast 说一次（取色器输出一定是合法 #RRGGBB，
		// 这条主要兜底其它路径写进来的坏值）。
		if (key == slot.HintBad)
		{
			MainWindow.Instance?.ShowToast(Loc.Default[key], false);
		}
	}

	/// <summary>#abc → #aabbcc。短色值 WPF 自己认得，但存进设置统一存长格式，
	/// 免得同一个颜色在文件里有两种写法、比对时判成两个不同的值。
	/// 空串（跟随主题）原样返回空。</summary>
	private static string NormalizeHex(string raw)
	{
		string h = raw.Trim().TrimStart('#');
		if (h.Length == 0)
		{
			return "";
		}
		if (h.Length == 3)
		{
			h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
		}
		return "#" + h.ToUpperInvariant();
	}

	private void LyricColorApplyButton_Click(object sender, RoutedEventArgs e)
	{
		if (_slotBase != null && CommitSlot(_slotBase))
		{
			RefreshSlot(_slotBase);
		}
	}

	private void LyricColorResetButton_Click(object sender, RoutedEventArgs e)
	{
		if (_slotBase == null) return;
		LyricColorBox.Text = "";
		if (CommitSlot(_slotBase))
		{
			RefreshSlot(_slotBase);
		}
	}

	private void LyricColorBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (_slotBase == null) return;
		if (CommitSlot(_slotBase))
		{
			RefreshSlot(_slotBase);
		}
	}

	private void LyricColorBox_KeyDown(object sender, KeyEventArgs e)
	{
		// 输入过程中不能每次按键都存盘 + 推一次歌词窗刷新，
		// 一边打字一边重排歌词太卡。只在回车时提交。
		if (e.Key != Key.Enter) return;
		if (_slotBase != null && CommitSlot(_slotBase))
		{
			RefreshSlot(_slotBase);
		}
		e.Handled = true;
	}

	private void LyricSungApplyButton_Click(object sender, RoutedEventArgs e)
	{
		if (_slotLit != null && CommitSlot(_slotLit))
		{
			RefreshSlot(_slotLit);
		}
	}

	private void LyricSungResetButton_Click(object sender, RoutedEventArgs e)
	{
		if (_slotLit == null) return;
		LyricSungBox.Text = "";
		if (CommitSlot(_slotLit))
		{
			RefreshSlot(_slotLit);
		}
	}

	private void LyricSungBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (_slotLit == null) return;
		if (CommitSlot(_slotLit))
		{
			RefreshSlot(_slotLit);
		}
	}

	private void LyricSungBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		if (_slotLit != null && CommitSlot(_slotLit))
		{
			RefreshSlot(_slotLit);
		}
		e.Handled = true;
	}

	private void LyricTranslateApplyButton_Click(object sender, RoutedEventArgs e)
	{
		if (_slotTr != null && CommitSlot(_slotTr))
		{
			RefreshSlot(_slotTr);
		}
	}

	private void LyricTranslateResetButton_Click(object sender, RoutedEventArgs e)
	{
		if (_slotTr == null) return;
		LyricTranslateBox.Text = "";
		if (CommitSlot(_slotTr))
		{
			RefreshSlot(_slotTr);
		}
	}

	private void LyricTranslateBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (_slotTr == null) return;
		if (CommitSlot(_slotTr))
		{
			RefreshSlot(_slotTr);
		}
	}

	private void LyricTranslateBox_KeyDown(object sender, KeyEventArgs e)
	{
		// 和另两个色框一致：输入过程中不存盘不推送，只在回车时提交
		if (e.Key != Key.Enter) return;
		if (_slotTr != null && CommitSlot(_slotTr))
		{
			RefreshSlot(_slotTr);
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
		DbgMemTitle.Text = Loc.Default["dbg_mem_title"];
		DbgMemReleaseButton.Content = Loc.Default["dbg_mem_release"];
	}

	// ─────── 内存检测（调试卡内，1s 一刷） ───────

	/// <summary>内存监视的秒表。只在本页可见时才真正开销：OnNavigatedTo 启动，
	/// Unloaded/离开可视树时停掉，Tick 里再挡一层「不可见就不刷」。</summary>
	private DispatcherTimer? _memTimer;

	/// <summary>进程对象跨 tick 复用，每拍 Refresh 一次拿最新工作集。
	/// 每秒新建 Process 对象会累积句柄，复用更稳。</summary>
	private Process? _memProcess;

	private void StartMemMonitor()
	{
		DbgMemTitle.Text = Loc.Default["dbg_mem_title"];
		DbgMemReleaseButton.Content = Loc.Default["dbg_mem_release"];
		RefreshMemText();
		_memTimer ??= CreateMemTimer();
		_memTimer.Start();
	}

	private void StopMemMonitor()
	{
		_memTimer?.Stop();
	}

	private DispatcherTimer CreateMemTimer()
	{
		var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		t.Tick += (_, _) =>
		{
			// 页面被切走/窗口最小化时不白刷：正文刷新含遍历缓存字典，省着点。
			if (!IsLoaded || !IsVisible)
			{
				return;
			}
			RefreshMemText();
		};
		return t;
	}

	private void RefreshMemText()
	{
		try
		{
			var loc = Loc.Default;
			_memProcess ??= Process.GetCurrentProcess();
			_memProcess.Refresh();
			double MiB(long b) => b / 1024.0 / 1024.0;
			// 托管堆水位：注意这里刻意用 GC.GetTotalMemory(false)。
			// true 会强制一次全代阻塞回收——每秒在 UI 线程上来一次会把播放卡出
			// 顿挫，监控本身变成问题源头。带压缩的全代回收只留给「立即释放」。
			long heap = GC.GetTotalMemory(false);
			var cs = CoverLoader.GetStats();
			(int liveRows, long builtRows) = TrackRows.GetRowStats();

			DbgMemText.Text =
				$"{loc["dbg_mem_ws"]} {MiB(_memProcess.WorkingSet64):F1} · " +
				$"{loc["dbg_mem_priv"]} {MiB(_memProcess.PrivateMemorySize64):F1} · " +
				$"{loc["dbg_mem_heap"]} {MiB(heap):F1} MiB\n" +
				$"{loc["dbg_mem_cover"]} {cs.DecodedCount} ({MiB(cs.DecodedEstBytes):F1} MiB) / " +
				$"{cs.BytesCount} ({MiB(cs.BytesEstBytes):F1} MiB)\n" +
				$"{loc["dbg_mem_inflight"]} {cs.InflightLoads} / {cs.Preloading} · {cs.Pinned} · {cs.Evictions}\n" +
				$"{loc["dbg_mem_rows"]} {liveRows} · {builtRows}";
		}
		catch
		{
			// 排查工具自身别把页面搞挂：进程句柄失效等瞬态异常下一拍自然恢复。
		}
	}

	/// <summary>「立即释放」：与托盘驻留、底栏释放键共用同一份实现
	/// （封面缓存 → GC 压缩 → EmptyWorkingSet），完成后刷新面板并 Toast。</summary>
	private void DbgMemReleaseButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			(int decoded, int bytes) = MemoryRelease.ReleaseNow();
			RefreshMemText();
			if (Application.Current.MainWindow is MainWindow mw)
			{
				mw.ShowToast(string.Format(Loc.Default["toast_mem_released"], decoded, bytes));
			}
		}
		catch (Exception ex)
		{
			LogHelper.LogError(ex, "调试面板内存释放失败");
		}
	}

	private bool _syncDarkness;
	private bool _syncAccent;
	private bool _syncThemeMode;

	/// <summary>八个预设强调色的显示文字，切语言时刷一遍。
	/// 这几个 ComboBoxItem 的 Content 原来直接写死中文，
	/// 英文/日文界面下拉里还是「深色/浅色/跟随系统」「墨黑/天蓝…」。</summary>
	private void RefreshThemeOptionText()
	{
		ThemeModeDarkItem.Content = Loc.Default["theme_dark"];
		ThemeModeLightItem.Content = Loc.Default["theme_light"];
		ThemeModeSystemItem.Content = Loc.Default["theme_system"];
		AccentInkItem.Content = Loc.Default["accent_ink"];
		AccentSkyItem.Content = Loc.Default["accent_sky"];
		AccentTealItem.Content = Loc.Default["accent_teal"];
		AccentPineItem.Content = Loc.Default["accent_pine"];
		AccentAmberItem.Content = Loc.Default["accent_amber"];
		AccentRoseItem.Content = Loc.Default["accent_rose"];
		AccentVioletItem.Content = Loc.Default["accent_violet"];
		AccentWhiteItem.Content = Loc.Default["accent_white"];
	}

	private void UpdateThemeUI()
	{
		var s = SettingsManager.Instance;
		RefreshThemeOptionText();

		_syncThemeMode = true;
		ThemeModeCombo.SelectedIndex = string.Equals(s.ThemeMode, "light", StringComparison.OrdinalIgnoreCase)
			? 1
			: string.Equals(s.ThemeMode, "system", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
		_syncThemeMode = false;

		// 保存的强调色不在八个预设里（老版本设置、手改过 settings.json、
		// 或者自定义过），以前这里 idx 默认 0，下拉就悄悄显示成「墨黑」——
		// 用户看到的和他实际用的完全不是一回事。
		// 现在把真实值补成一项显示出来，选中它。
		string accent = (s.AccentColor ?? "").Trim().ToUpperInvariant();
		EnsureAccentItem(accent);
		_syncAccent = true;
		for (int i = 0; i < ThemeAccentCombo.Items.Count; i++)
		{
			if (ThemeAccentCombo.Items[i] is ComboBoxItem item
				&& string.Equals((item.Tag as string)?.ToUpperInvariant(), accent, StringComparison.Ordinal))
			{
				ThemeAccentCombo.SelectedIndex = i;
				break;
			}
		}
		_syncAccent = false;

		_syncDarkness = true;
		ThemeDarknessSlider.Value = Math.Clamp(s.BackgroundDarkness, 0.1, 0.8);
		ThemeDarknessText.Text = (int)Math.Round(ThemeDarknessSlider.Value * 100.0) + "%";
		_syncDarkness = false;
	}

	/// <summary>八个预设里没有这个颜色时，往下拉尾部补一项并显示它自己。
	/// 重复调用不会补第二次。</summary>
	private void EnsureAccentItem(string accent)
	{
		if (accent.Length == 0)
		{
			return;
		}
		for (int i = 0; i < ThemeAccentCombo.Items.Count; i++)
		{
			if (ThemeAccentCombo.Items[i] is ComboBoxItem item
				&& string.Equals((item.Tag as string)?.ToUpperInvariant(), accent, StringComparison.Ordinal))
			{
				return;
			}
		}
		ThemeAccentCombo.Items.Add(new ComboBoxItem
		{
			Content = accent,
			Tag = accent,
		});
	}

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
		// 顺带把主题色的字也换掉：accent 有没有被 ReadableAccent 提亮/压暗
		// 取决于当前是深色还是浅色，而这句 ApplyAccentTheme 之前没人调。
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ApplyAccentTheme();
		}
	}

	private void ThemeAccentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// _syncAccent 必须在这里挡：UpdateThemeUI 设 SelectedIndex 时
		// 会触发这个事件，不挡就会把「只是回显」当成「用户换了色」再存一遍。
		if (_syncAccent || !_ready || ThemeAccentCombo.SelectedItem is not ComboBoxItem { Tag: string hex })
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

	// 爱好是一组一组的词，用标签卡装比一长串逗号分隔的文本好读也好改：
	// 敲完一个词按空格/回车就包成一张卡，点卡把词撤回来改，
	// 框里空着按退格把最后一张取回来。保存时卡片按逗号拼回设置里那个字符串。
	private readonly List<string> _hobbyChips = new();

	private void UpdateHobbyUI()
	{
		HobbyTitle.Text = Loc.Default["set_hobby_title"];
		HobbySub.Text = Loc.Default["set_hobby_sub"];
		HobbySaveButton.Content = Loc.Default["set_hobby_save"];
		if (HobbyChipHint != null)
		{
			HobbyChipHint.Text = Loc.Default["set_hobby_chip_hint"];
		}
		// 首选搜索建议平台：文案随语言刷新，选中项跟着存档走。
		// 组装期间 _ready 为 false，SelectionChanged 里会自动空转，不会误存盘。
		FirstPlatformLabel.Text = Loc.Default["set_first_platform"];
		FirstPlatformDefaultItem.Content = Loc.Default["set_first_platform_default"];
		FirstPlatformNeteaseItem.Content = Loc.Default["set_first_platform_netease"];
		FirstPlatformQqItem.Content = Loc.Default["set_first_platform_qq"];
		FirstPlatformKugouItem.Content = Loc.Default["set_first_platform_kugou"];
		FirstPlatformCombo.SelectedIndex =
			(SettingsManager.Instance.FirstSearchPlatform ?? "").Trim().ToLowerInvariant() switch
			{
				"netease" => 1,
				"qq" => 2,
				"kugou" => 3,
				_ => 0
			};
		// 已存的那串按逗号拆开，一个词一张卡摆出来
		_hobbyChips.Clear();
		HobbyChipHost.Children.Clear();
		HobbyBox.Text = "";
		foreach (string part in (SettingsManager.Instance.HobbyKeywords ?? "")
			.Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
		{
			AddHobbyChip(part.Trim(), false);
		}
	}

	/// <summary>敲完一个词按空格/回车 → 包成标签；框空着按退格 → 取回最后一张改。</summary>
	private void HobbyBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.Tab)
		{
			string word = (HobbyBox.Text ?? "").Trim();
			if (word.Length > 0)
			{
				AddHobbyChip(word, true);
				HobbyBox.Clear();
			}
			// 空格/回车/退格一律不当普通字符落进框里：
			// 这些词是要分开存的一个个标签，框里留空格只会让拆词时多出空段。
			e.Handled = true;
			return;
		}
		// 框空着按退格：把最后一张卡拿回输入框接着改（跟标签框的常规手感一致）
		if (e.Key == Key.Back && (HobbyBox.Text?.Length ?? 0) == 0 && _hobbyChips.Count > 0)
		{
			string last = _hobbyChips[^1];
			RemoveHobbyChipAt(_hobbyChips.Count - 1);
			HobbyBox.Text = last;
			HobbyBox.CaretIndex = last.Length;
			e.Handled = true;
		}
	}

	/// <summary>包一张标签卡。<paramref name="focusBox"/> = 包完把焦点留在输入框，
	/// 让用户接着敲下一个词（从设置里读出来铺卡时不需要）。</summary>
	private void AddHobbyChip(string word, bool focusBox)
	{
		word = (word ?? "").Trim();
		if (word.Length == 0)
		{
			return;
		}
		// 同一个词不铺两张
		foreach (string c in _hobbyChips)
		{
			if (string.Equals(c, word, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		_hobbyChips.Add(word);

		var row = new StackPanel { Orientation = Orientation.Horizontal };
		row.Children.Add(new TextBlock
		{
			Text = word,
			FontSize = 12,
			Foreground = ThemeManager.Primary(),
			VerticalAlignment = VerticalAlignment.Center
		});
		// 关闭符「×」单独可点：点它才撤下这张卡。点卡片本体不再消失（用户要求保留）。
		var close = new TextBlock
		{
			Text = "  ×",
			FontSize = 12,
			Foreground = ThemeManager.Primary(),
			Opacity = 0.6,
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			Tag = word
		};
		close.MouseLeftButtonDown += HobbyChip_Click;
		row.Children.Add(close);
		var card = new Border
		{
			CornerRadius = new CornerRadius(5),
			Background = ThemeManager.AccentSubtle(),
			Padding = new Thickness(7, 1, 8, 2),
			Margin = new Thickness(2, 2, 2, 2),
			VerticalAlignment = VerticalAlignment.Center,
			Tag = word,
			Child = row
		};
		// 点卡片本体把词拿回输入框改成可改的原文（词不丢，失焦时重新包卡存盘）。
		card.MouseLeftButtonDown += HobbyChip_Click;
		HobbyChipHost.Children.Add(card);
		HobbyChipBar.Visibility = Visibility.Visible;
		if (focusBox)
		{
			HobbyBox.Focus();
		}
	}

	/// <summary>点卡片（本体或「×」）把该词拿回输入框改成可改的原文。
	/// 卡片撤下、词不丢：输入框失焦时 HobbyBox_LostFocus 会把它重新包成卡并存盘，
	/// 所以「点一下」= 编辑这个爱好词，而不是删除。</summary>
	private void HobbyChip_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is not FrameworkElement fe || fe.Tag is not string word)
		{
			return;
		}
		e.Handled = true;
		int at = _hobbyChips.FindIndex(x => string.Equals(x, word, StringComparison.OrdinalIgnoreCase));
		if (at >= 0)
		{
			RemoveHobbyChipAt(at);
		}
		string existing = (HobbyBox.Text ?? "").Trim();
		HobbyBox.Text = existing.Length > 0 ? existing + " " + word : word;
		HobbyBox.Focus();
		HobbyBox.CaretIndex = HobbyBox.Text.Length;
	}

	/// <summary>输入框失焦即提交：框里还挂着半个词就包成卡，并把整组爱好词存盘，
	/// 做到「改完即生效」，不必再按保存按钮。</summary>
	private void HobbyBox_LostFocus(object sender, RoutedEventArgs e)
	{
		try
		{
			string tail = (HobbyBox.Text ?? "").Trim();
			if (tail.Length > 0)
			{
				AddHobbyChip(tail, false);
				HobbyBox.Clear();
			}
			var s = SettingsManager.Instance;
			string prev = s.HobbyKeywords ?? "";
			string next = CollectHobbyKeywords();
			if (!string.Equals(prev, next, StringComparison.Ordinal))
			{
				s.HobbyKeywords = next;
				SettingsManager.Save(s);
				if (Application.Current.MainWindow is MainWindow mw)
				{
					mw.ShowToast(Loc.Default["set_hobby_saved"]);
				}
			}
		}
		catch
		{
		}
	}

	private void RemoveHobbyChipAt(int index)
	{
		if (index < 0 || index >= _hobbyChips.Count)
		{
			return;
		}
		_hobbyChips.RemoveAt(index);
		if (index < HobbyChipHost.Children.Count)
		{
			HobbyChipHost.Children.RemoveAt(index);
		}
		if (_hobbyChips.Count == 0)
		{
			HobbyChipBar.Visibility = Visibility.Collapsed;
		}
	}

	/// <summary>卡片 + 框里还没包上的那截，拼成设置里那一串（逗号分隔）。</summary>
	private string CollectHobbyKeywords()
	{
		var all = new List<string>(_hobbyChips);
		string tail = (HobbyBox.Text ?? "").Trim();
		if (tail.Length > 0)
		{
			all.Add(tail);
		}
		return string.Join(",", all);
	}

	private void HobbySaveButton_Click(object sender, RoutedEventArgs e)
	{
		// 框里还挂着半个词就先包成卡，保存的才是用户真正看到的那几张
		string tail = (HobbyBox.Text ?? "").Trim();
		if (tail.Length > 0)
		{
			AddHobbyChip(tail, false);
			HobbyBox.Clear();
		}
		var s = SettingsManager.Instance;
		s.HobbyKeywords = CollectHobbyKeywords();
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_hobby_saved"]);
		}
	}

	/// <summary>首选搜索建议平台下拉：改了就存盘。
	/// 存的值是平台键（netease/qq/kugou，空 = 默认顺序），
	/// 搜索建议取源时按它把选中平台排到队首。</summary>
	private void FirstPlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready || FirstPlatformCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
		{
			return;
		}
		AppSettings s = SettingsManager.Instance;
		if (!string.Equals(s.FirstSearchPlatform ?? "", tag, StringComparison.Ordinal))
		{
			s.FirstSearchPlatform = tag;
			SettingsManager.Save(s);
		}
	}

	private void UpdateBehaviorUI()
	{
		var s = SettingsManager.Instance;
		BehaviorTitleText.Text = Loc.Default["set_behavior_title"];
		AutoStartCheckBox.Content = Loc.Default["set_autostart"];
		ShowTrayIconCheckBox.Content = Loc.Default["set_tray_icon"];
		// 「播放定位小球 / 界面音效 / 最小化时释放物理内存」三个开关行已按产品决策
		// 撤掉（三项恒开，SettingsManager.Load 强制覆盖），这里不再有对应控件。
		VipProbeCheckBox.Content = Loc.Default["set_vip_probe"];
		VipProbeCheckBox.ToolTip = Loc.Default["set_vip_probe_hint"];
		ShowCoversCheckBox.Content = Loc.Default["set_show_covers"];
		ShowCoversHintText.Text = Loc.Default["set_show_covers_hint"];
		AutoMemReleaseCheckBox.Content = Loc.Default["set_auto_mem"];
		AutoMemHintText.Text = Loc.Default["set_auto_mem_hint"];
		AutoMemThresholdLabel.Text = Loc.Default["set_auto_mem_threshold"];
		StartupAutoPlayCheckBox.Content = Loc.Default["set_autoplay_startup"];
		StartupAutoPlayHintText.Text = Loc.Default["set_autoplay_startup_hint"];
		DebugModeCheckBox.Content = Loc.Default["set_debug"];

		AutoStartCheckBox.IsChecked = Services.AutoStart.IsEnabled();
		ShowTrayIconCheckBox.IsChecked = s.ShowTrayIcon;
		VipProbeCheckBox.IsChecked = s.OnlineVipProbe;
		ShowCoversCheckBox.IsChecked = s.ShowListCovers;
		AutoMemReleaseCheckBox.IsChecked = s.AutoMemRelease;
		AutoMemThresholdSlider.Value = Math.Clamp(s.AutoMemReleaseMb, 150, 4096);
		AutoMemThresholdText.Text = $"{(int)Math.Round(AutoMemThresholdSlider.Value)} MB";
		AutoMemThresholdSlider.IsEnabled = s.AutoMemRelease;
		AutoMemThresholdText.Opacity = s.AutoMemRelease ? 1.0 : 0.45;
		StartupAutoPlayCheckBox.IsChecked = s.StartupAutoPlay;
		DebugModeCheckBox.IsChecked = s.DebugMode;

		UpdateRecentUI();
	}

	private void AutoMemReleaseCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.AutoMemRelease = true;
		SettingsManager.Save(s);
		AutoMemThresholdSlider.IsEnabled = true;
		AutoMemThresholdText.Opacity = 1.0;
	}

	private void AutoMemReleaseCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.AutoMemRelease = false;
		SettingsManager.Save(s);
		AutoMemThresholdSlider.IsEnabled = false;
		AutoMemThresholdText.Opacity = 0.45;
	}

	private void AutoMemThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.AutoMemReleaseMb = (int)Math.Round(Math.Clamp(e.NewValue, 150.0, 4096.0));
		AutoMemThresholdText.Text = s.AutoMemReleaseMb + " MB";
		SettingsManager.Save(s);
	}

	/// <summary>最近播放保留策略的文案与当前值。两个数字框用 _syncRecent 压住
	/// 程序赋值触发的事件，和主题下拉同一套做法。</summary>
	private bool _syncRecent;

	private void UpdateRecentUI()
	{
		var s = SettingsManager.Instance;
		RecentTitleText.Text = Loc.Default["set_recent_title"];
		RecentKeepDaysLabel.Text = Loc.Default["set_recent_keep_days"];
		RecentKeepDaysHint.Text = Loc.Default["set_recent_keep_days_hint"];
		RecentCleanupButton.Content = Loc.Default["set_recent_cleanup"];
		RecentMaxCountLabel.Text = Loc.Default["set_recent_max"];
		RecentMaxCountHint.Text = Loc.Default["set_recent_max_hint"];
		_syncRecent = true;
		RecentKeepDaysBox.Text = Math.Clamp(s.RecentKeepDays, 0, 365).ToString();
		RecentMaxCountBox.Text = Math.Max(0, s.RecentMaxCount).ToString();
		_syncRecent = false;
	}

	// ── 最近播放保留策略 ──────────────────────────────────────────

	/// <summary>提交「保留天数」输入：0-365 之外的输入一律弹回当前设置值，
	/// 不猜用户想填什么（留空/乱码就当没改）。</summary>
	private void CommitRecentKeepDays()
	{
		if (_syncRecent) return;
		var s = SettingsManager.Instance;
		if (int.TryParse(RecentKeepDaysBox.Text.Trim(), out int v))
		{
			s.RecentKeepDays = Math.Clamp(v, 0, 365);
			SettingsManager.Save(s);
		}
		_syncRecent = true;
		RecentKeepDaysBox.Text = Math.Clamp(s.RecentKeepDays, 0, 365).ToString();
		_syncRecent = false;
	}

	private void RecentKeepDaysBox_LostFocus(object sender, RoutedEventArgs e) => CommitRecentKeepDays();

	private void RecentKeepDaysBox_KeyDown(object sender, KeyEventArgs e)
	{
		// 回车提交并收键盘：和其它文本输入的交互习惯一致。
		if (e.Key == Key.Enter)
		{
			e.Handled = true;
			CommitRecentKeepDays();
			Keyboard.ClearFocus();
		}
	}

	/// <summary>提交「条数上限」输入：0 = 不限，上限给到 100000 兜底
	///（再大 settings.json 就真的撑不住了）。</summary>
	private void CommitRecentMaxCount()
	{
		if (_syncRecent) return;
		var s = SettingsManager.Instance;
		if (int.TryParse(RecentMaxCountBox.Text.Trim(), out int v))
		{
			s.RecentMaxCount = Math.Clamp(v, 0, 100000);
			SettingsManager.Save(s);
		}
		_syncRecent = true;
		RecentMaxCountBox.Text = Math.Max(0, s.RecentMaxCount).ToString();
		_syncRecent = false;
	}

	private void RecentMaxCountBox_LostFocus(object sender, RoutedEventArgs e) => CommitRecentMaxCount();

	private void RecentMaxCountBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			e.Handled = true;
			CommitRecentMaxCount();
			Keyboard.ClearFocus();
		}
	}

	/// <summary>「清理」按钮：按保留天数立即清一次，Toast 报条数。
	/// 0 = 永久保留时清不掉任何东西（返回 0），Toast 如实说清了 0 条。</summary>
	private void RecentCleanupButton_Click(object sender, RoutedEventArgs e)
	{
		CommitRecentKeepDays();
		int removed = Services.UserLibrary.CleanupRecentByKeepDays();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default.Format("set_recent_cleanup_done", removed));
		}
	}

	// ── 显示封面图片 ─────────────────────────────────────────────

	/// <summary>封面显示总开关（现在管**全部**封面位：列表行、播放条、SMTC、
	/// 沉浸页、歌单卡片 —— CoverLoader 入口统一短路）。翻设置位后立即刷新
	/// 播放条封面：关 = 播放条当场回到纯色占位 + 内存释放立刻做一轮
	///（清已解码封面缓存 + 压缩 GC + 修剪工作集），让「关掉后的内存底线」
	/// 马上可见；开 = 恢复取图（磁盘缓存还在，不用重新下载）。</summary>
	private void ShowCoversCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.ShowListCovers = true;
		SettingsManager.Save(s);
		(Application.Current.MainWindow as MainWindow)?.UpdateMiniAll();
	}

	private void ShowCoversCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.ShowListCovers = false;
		SettingsManager.Save(s);
		// 播放条封面当场撤掉（UpdateMiniCover 读总闸，回纯色占位）。
		(Application.Current.MainWindow as MainWindow)?.UpdateMiniAll();
		// 开→关的瞬间立刻做一次内存释放（清已解码封面缓存 + 压缩 GC + 修剪工作集）：
		// 让「减少内存占用」马上生效，而不是等下次托盘化。只有关的方向才释放，
		// 重新打开不需要（缓存会随列表重画自然重建）。
		MemoryRelease.ReleaseNow();
	}

	// 「最小化时释放物理内存」「播放定位小球」「界面音效」三个开关的处理函数
	// 已随设置行一起撤掉：三项按产品决策恒开（SettingsManager.Load 强制覆盖），
	// 行为代码不再判断开关。

	// ── 播放条标题滚动 ───────────────────────────────────────
	// 走马灯只保留一种效果（持续左移的无缝循环），不再提供样式选择，
	// 设置行与处理函数已随产品决策撤掉。

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

	/// <summary>「启动时自动播放」开关：只落盘。生效点在主窗口 Window_Loaded 末尾
	/// （TryStartupAutoPlay），进程冷启动只跑一次；开着时它接管启动播放，
	/// 老的「续播上一首」让位。</summary>
	private void StartupAutoPlayCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.StartupAutoPlay = true;
		SettingsManager.Save(s);
	}

	private void StartupAutoPlayCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.StartupAutoPlay = false;
		SettingsManager.Save(s);
	}

	private void DebugModeCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		SetDebugMode(true);
	}

	private void VipProbeCheckBox_Checked(object sender, RoutedEventArgs e)
	{
		SetVipProbe(true);
	}

	private void VipProbeCheckBox_Unchecked(object sender, RoutedEventArgs e)
	{
		SetVipProbe(false);
	}

	/// <summary>「主动探测 VIP 音乐」开关。关 = 维持原行为（点播放实测被拒才打标）；
	/// 开 = 之后每次列表渲染（搜索结果/收藏夹）都会在后台逐条实测打标。
	/// 这里只落盘，不打扰正在显示的列表 —— 探测由 VipProbe 调度，
	/// 下一次渲染自然按新开关行事，避免开关一拨就整页重探。</summary>
	private void SetVipProbe(bool value)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		if (s.OnlineVipProbe == value) return;
		s.OnlineVipProbe = value;
		SettingsManager.Save(s);
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["set_vip_probe"] + (value ? " ON" : " OFF"));
		}
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
		// 「调试与路径」区随开关整块显隐；隐藏期间把内存检测的 1s 计时器停掉
		// （省 CPU），开启再恢复。
		ApplyDebugSection();
		if (Application.Current.MainWindow is MainWindow mw)
		{
			mw.ShowToast(Loc.Default["toast_debug"] + (value ? "ON" : "OFF"));
		}
	}

	/// <summary>「调试与路径」区只在调试模式开着时显示（DebugMode=false 整块
	/// Collapsed）；内存检测的 1s DispatcherTimer 跟着显隐启停 —— 区块看不见时
	/// 每秒采样纯属白烧 CPU。进入设置页时也走这里（OnNavigatedTo）。</summary>
	private void ApplyDebugSection()
	{
		bool debug = SettingsManager.Instance.DebugMode;
		DebugSection.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
		if (debug)
		{
			StartMemMonitor();
		}
		else
		{
			StopMemMonitor();
		}
	}

	private void UpdateChooseButtonLabel()
	{
		bool video = BgTypeCombo.SelectedIndex == 1;
		ChooseBgButton.Content = Loc.Default[video ? "set_bg_choose_video" : "set_bg_choose"];
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

	/// <summary>歌词颜色渲染速度滑块（毫秒/整句）：颜色扫完整句的最长用时。
	/// 存盘即生效，ProgressOf 每拍现读这个数，当前播放的那句马上换速。</summary>
	private void LyricRevealSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready) return;
		var s = SettingsManager.Instance;
		s.LyricRevealMs = (int)Math.Round(e.NewValue);
		UpdateLyricRevealValueText();
		SettingsManager.Save(s);
	}

	private void UpdateLyricRevealValueText()
	{
		if (LyricRevealValueText == null) return;
		double ms = LyricRevealSlider?.Value ?? SettingsManager.Instance.LyricRevealMs;
		LyricRevealValueText.Text = (ms / 1000.0).ToString("0.0") + " s";
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

	private void UpdateHotkeyUI()
	{
		HotkeyTitleText.Text = Loc.Default["set_hotkey_title"];
		HotkeyHintText.Text = Loc.Default["set_hotkey_hint"];
		HotkeyList.Children.Clear();

		// 全局键：带修饰键，RegisterHotKey 注册，不占本程序窗口内的裸键。
		var defs = new[]
		{
			new HotkeyRow { Key = "next", Label = Loc.Default["mt_next"] },
			new HotkeyRow { Key = "prev", Label = Loc.Default["mt_prev"] },
			new HotkeyRow { Key = "playpause", Label = Loc.Default["mt_playpause"] },
			new HotkeyRow { Key = "fav", Label = Loc.Default["act_fav"] },
			new HotkeyRow { Key = "toggle", Label = Loc.Default["set_hotkey_toggle"] },
			new HotkeyRow { Key = "lyrics_toggle", Label = Loc.Default["set_hotkey_lyrics_toggle"] },
			new HotkeyRow { Key = "vol_down", Label = Loc.Default["set_hotkey_vol_down"] },
			new HotkeyRow { Key = "vol_up", Label = Loc.Default["set_hotkey_vol_up"] }
		};
		foreach (HotkeyRow row in defs)
		{
			AddHotkeyRow(row, CurrentShortcut(row.Key));
		}

		// 局内键：裸键（Q/E/1/2/3），只在窗口内有焦点时生效。
		// 单独起一段小标题，不然跟上面五行混在一起，用户会以为
		// 「改了 Q 就能在别的程序里按 Q」——那是不可能的，裸键根本注册不了全局。
		AddHotkeySectionHeader(Loc.Default["set_hotkey_local_title"]);
		var localDefs = new[]
		{
			new HotkeyRow { Key = "list_prev", Label = Loc.Default["set_hotkey_list_prev"], Local = true },
			new HotkeyRow { Key = "list_next", Label = Loc.Default["set_hotkey_list_next"], Local = true },
			new HotkeyRow { Key = "prev", Label = Loc.Default["set_hotkey_local_prev"], Local = true },
			new HotkeyRow { Key = "next", Label = Loc.Default["set_hotkey_local_next"], Local = true },
			new HotkeyRow { Key = "playpause", Label = Loc.Default["set_hotkey_local_pp"], Local = true }
		};
		foreach (HotkeyRow row in localDefs)
		{
			AddHotkeyRow(row, LocalHotkeys.Current(row.Key));
		}
	}

	/// <summary>局内键那一段的小标题。</summary>
	private void AddHotkeySectionHeader(string text)
	{
		HotkeyList.Children.Add(new TextBlock
		{
			Text = text,
			FontSize = 12,
			Foreground = ThemeManager.Tertiary(),
			Margin = new Thickness(0, 10, 0, 4),
		});
	}

	private void AddHotkeyRow(HotkeyRow row, string current)
	{
		var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		var label = new TextBlock
		{
			Text = row.Label,
			FontSize = 13,
			Foreground = ThemeManager.Primary(),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(label, 0);
		grid.Children.Add(label);

		var glyph = new TextBlock
		{
			Text = current,
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
			Tag = row.Key + (row.Local ? LocalTagSuffix : ""),
			Style = (Style)FindResource("GhostButtonStyle"),
			Content = glyph
		};
		btn.Click += RebindButton_Click;
		Grid.SetColumn(btn, 1);
		grid.Children.Add(btn);
		HotkeyList.Children.Add(grid);
	}

	private void RebindButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button btn || btn.Tag is not string tag)
		{
			return;
		}
		bool local = tag.EndsWith(LocalTagSuffix, StringComparison.Ordinal);
		string key = local ? tag.Substring(0, tag.Length - LocalTagSuffix.Length) : tag;
		_ready = false;
		btn.Focus();
		string keyCaption = HotkeyLabel(key, local);
		btn.Content = new TextBlock { Text = Loc.Default.Format("set_hotkey_press", keyCaption), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
		btn.KeyDown += RebindKeyDown;
		btn.LostFocus += RebindLostFocus;
		btn.Tag = "capture:" + tag;
	}

	private static string HotkeyLabel(string key, bool local)
	{
		if (local)
		{
			return key switch
			{
				"list_prev" => Loc.Default["set_hotkey_list_prev"],
				"list_next" => Loc.Default["set_hotkey_list_next"],
				"prev" => Loc.Default["set_hotkey_local_prev"],
				"next" => Loc.Default["set_hotkey_local_next"],
				_ => Loc.Default["set_hotkey_local_pp"],
			};
		}
		return key switch
		{
			"next" => Loc.Default["mt_next"],
			"prev" => Loc.Default["mt_prev"],
			"playpause" => Loc.Default["mt_playpause"],
			"fav" => Loc.Default["act_fav"],
			"toggle" => Loc.Default["set_hotkey_toggle"],
			"lyrics_toggle" => Loc.Default["set_hotkey_lyrics_toggle"],
			"vol_down" => Loc.Default["set_hotkey_vol_down"],
			"vol_up" => Loc.Default["set_hotkey_vol_up"],
			_ => Loc.Default["set_hotkey_toggle"]
		};
	}

	private void RebindKeyDown(object sender, KeyEventArgs e)
	{
		if (sender is not Button btn || btn.Tag is not string tag
			|| !tag.StartsWith("capture:", StringComparison.Ordinal))
		{
			return;
		}
		bool local = tag.EndsWith(LocalTagSuffix, StringComparison.Ordinal);
		e.Handled = true;
		ModifierKeys mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
		if (!local && mods == 0)
		{
			// 全局键必须带修饰键：裸键注册上去只会被系统和其他软件抢走，
			// RegisterHotKey 直接失败，用户改了等于没改。
			return;
		}
		if (local && mods != ModifierKeys.None)
		{
			// 局内键反过来只认裸键。带 Ctrl 的 Q 之类不归它管，
			// 否则 Ctrl+Q 关闭窗口这种全局惯例会被抢走。
			return;
		}
		Key realKey = e.Key == Key.System ? e.SystemKey : e.Key;
		if (realKey == Key.LeftCtrl || realKey == Key.RightCtrl || realKey == Key.LeftAlt || realKey == Key.RightAlt ||
			realKey == Key.LeftShift || realKey == Key.RightShift || realKey == Key.LWin || realKey == Key.RWin)
		{
			return;
		}
		if (local)
		{
			string key = tag.Substring("capture:".Length, tag.Length - "capture:".Length - LocalTagSuffix.Length);
			string combo = realKey.ToString();
			// 同一个键不能给两个功能：用户按下去只有一个有反应，还不知道是哪个。
			if (LocalHotkeys.IsTakenByOther(key, combo))
			{
				ShowHotkeyMessage(btn, Loc.Default["set_hotkey_dup"]);
				return;
			}
			EndRebind(btn, combo, local: true);
			return;
		}
		EndRebind(btn, HotkeyManager.BuildComboText(mods, realKey), local: false);
	}

	private void RebindLostFocus(object sender, RoutedEventArgs e)
	{
		if (sender is Button btn)
		{
			EndRebind(btn, null);
		}
	}

	private void EndRebind(Button btn, string? newShortcut, bool local = false)
	{
		btn.KeyDown -= RebindKeyDown;
		btn.LostFocus -= RebindLostFocus;
		if (btn.Tag is not string tag || !tag.StartsWith("capture:", StringComparison.Ordinal))
		{
			return;
		}
		string tagged = tag.Substring("capture:".Length);
		bool taggedLocal = tagged.EndsWith(LocalTagSuffix, StringComparison.Ordinal);
		string key = taggedLocal ? tagged.Substring(0, tagged.Length - LocalTagSuffix.Length) : tagged;
		btn.Tag = tagged;
		if (string.IsNullOrWhiteSpace(newShortcut))
		{
			ShowHotkeyMessage(btn, taggedLocal ? LocalHotkeys.Current(key) : CurrentShortcut(key));
			_ready = true;
			return;
		}
		if (taggedLocal)
		{
			// 局内键不走 ParseCombo：那里认的是 "Ctrl+Alt+Left" 这种串，
			// 裸键存的是 Key 枚举名（Q / D1），两边格式不一样。
			if (!LocalHotkeys.TrySet(key, newShortcut))
			{
				ShowHotkeyMessage(btn, Loc.Default["set_hotkey_invalid"]);
				_ready = true;
				return;
			}
			ShowHotkeyMessage(btn, newShortcut);
			if (Application.Current.MainWindow is MainWindow mw)
			{
				mw.ShowToast(Loc.Default.Format("set_hotkey_saved", newShortcut));
			}
			_ready = true;
			return;
		}
		// 校验组合键
		var (ok, mods, vk) = HotkeyManager.ParseCombo(newShortcut);
		if (!ok || mods == 0 || vk == 0)
		{
			ShowHotkeyMessage(btn, Loc.Default["set_hotkey_invalid"]);
			_ready = true;
			return;
		}
		var globalSettings = SettingsManager.Instance;
		globalSettings.Hotkeys ??= new System.Collections.Generic.Dictionary<string, string>();
		globalSettings.Hotkeys[key] = newShortcut;
		SettingsManager.Save(globalSettings);
		HotkeyManager.RegisterAll();
		ShowHotkeyMessage(btn, newShortcut);
		if (Application.Current.MainWindow is MainWindow win)
		{
			win.ShowToast(Loc.Default.Format("set_hotkey_saved", newShortcut));
		}
		_ready = true;
	}

	private static void ShowHotkeyMessage(Button btn, string text)
	{
		btn.Content = new TextBlock { Text = text, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
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
