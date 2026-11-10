using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MusicBox.Services;
using MusicBox.Services.Effects;
using MusicBox.Settings;

namespace MusicBox.Pages;

public partial class FxPresetPage : UserControl, IMusicPage
{
	private readonly System.Windows.Threading.DispatcherTimer _strengthDebounce =
		new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

	private bool _ready;

	private bool _eqUpdating;

	private readonly System.Windows.Threading.DispatcherTimer _eqDebounce =
		new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };

	private readonly string[] _eqLabels =
		EqualizerProvider.CenterFrequencies.Select(f => f >= 1000 ? (f / 1000).ToString("0") + "k" : f.ToString("0")).ToArray();

	private sealed class EqSliderTag
	{
		public int Band;
		public TextBlock? Val;
	}

	public FxPresetPage()
	{
		InitializeComponent();
		_strengthDebounce.Tick += (_, _) =>
		{
			_strengthDebounce.Stop();
			SettingsManager.Save(SettingsManager.Instance);
			MusicService.RestartSurround();
		};
		_eqDebounce.Tick += (_, _) =>
		{
			_eqDebounce.Stop();
			SettingsManager.Save(SettingsManager.Instance);
			MusicService.ApplyFxRestart();
			if (_ready) SyncComboToCustom();
		};
		MusicService.StateChanged += OnStateChanged;
	}

	public void OnNavigatedTo()
	{
		var s = SettingsManager.Instance;
		_ready = false;
		SurroundSwitch.IsChecked = s.SurroundEnable;
		SurroundStrengthSlider.Value = s.SurroundStrength;
		FxPresetCombo.Items.Clear();
		foreach (FxPreset p in FxPreset.Presets)
		{
			FxPresetCombo.Items.Add(p.Name);
		}
		// 若当前是「自定义」均衡（不在预设里），追加进下拉
		string curName = s.Fx?.PresetName ?? "";
		if (!string.IsNullOrEmpty(curName) && curName != "关闭"
			&& !FxPreset.Presets.Any(p => string.Equals(p.Name, curName, StringComparison.OrdinalIgnoreCase)))
		{
			FxPresetCombo.Items.Add(curName);
			FxPresetCombo.SelectedIndex = FxPresetCombo.Items.Count - 1;
			FxPresetDescText.Text = "手工调节的均衡器（可复位）";
		}
		else
		{
			string presetName = s.Fx?.PresetName ?? "";
			int presetIndex = Math.Max(0, FxPreset.Presets.FindIndex(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase)));
			FxPresetCombo.SelectedIndex = presetIndex;
			FxPresetDescText.Text = FxPreset.Presets[presetIndex].Description;
		}
		_ready = true;
		BuildSurroundModePanel(s.SurroundMode);
		RefreshEq();
	}

	/// <summary>四个变体的可点卡片。每次进页面重建，避免和外层 StackPanel 重复添加。
	/// 选中后立刻 Rebuild 音频链——试听本来就是这个控件的目的，
	/// 让用户点一下就能听到差别，而不是改完还得自己找地方应用。</summary>
	private void BuildSurroundModePanel(SurroundMode selected)
	{
		SurroundModePanel.Children.Clear();
		foreach (SurroundModeInfo info in SurroundModeInfos)
		{
			var btn = new RadioButton
			{
				Content = info.Title,
				Tag = info.Mode,
				GroupName = "SurroundMode",
				IsChecked = info.Mode == selected,
				Margin = new Thickness(0, 0, 0, 4),
				FontSize = 12.5,
				ToolTip = info.Hint
			};
			// 用 SetResourceReference 而不是 Foreground = ThemeManager.Primary()。
			// 后者是一次快照：ThemeManager.Primary() 把当下那支画Brush取出来
			// 直接赋过去，主题切换后这些控件还拿着旧颜色 —— 深色主题下看着正常，
			// 切到浅色就成了白底白字（等于「字体全黑/看不见」这一类问题的另一种样子）。
			// SetResourceReference 让控件自己跟着资源走，换主题即时生效。
			btn.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
			btn.Checked += SurroundModeRadio_Checked;
			SurroundModePanel.Children.Add(btn);

			var hint = new TextBlock
			{
				Text = info.Hint,
				FontSize = 11,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(22, 0, 0, 4)
			};
			hint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
			SurroundModePanel.Children.Add(hint);
		}
		UpdateSurroundModeHint(selected);
	}

	private void SurroundModeRadio_Checked(object sender, RoutedEventArgs e)
	{
		if (sender is not RadioButton { Tag: SurroundMode mode })
		{
			return;
		}
		if (!_ready)
		{
			return;
		}
		var s = SettingsManager.Instance;
		s.SurroundMode = mode;
		s.Fx.SurroundMode = mode;
		SettingsManager.Save(s);
		UpdateSurroundModeHint(mode);
		MusicService.RestartSurround();
	}

	private void UpdateSurroundModeHint(SurroundMode mode)
	{
		foreach (SurroundModeInfo info in SurroundModeInfos)
		{
			if (info.Mode == mode)
			{
				SurroundModeHintText.Text = info.Hint;
				return;
			}
		}
		SurroundModeHintText.Text = "";
	}

	private sealed class SurroundModeInfo
	{
		public SurroundMode Mode { get; init; }
		public string Title { get; init; } = "";
		public string Hint { get; init; } = "";
	}

	/// <summary>变体说明。这里写的顺序和 SurroundDsp.cs 里的实现一一对应，
	/// 改了算法参数就同步改这里，不然用户听到的和看到的对不上。</summary>
	private static readonly SurroundModeInfo[] SurroundModeInfos = new[]
	{
		new SurroundModeInfo
		{
			Mode = SurroundMode.SamePhase,
			Title = "A · 同相加宽（当前默认）",
			Hint = "空间分量左右同相加入，不加任何延迟。声像最稳、中心人声不飘，没有回声。"
		},
		new SurroundModeInfo
		{
			Mode = SurroundMode.HaasRear,
			Title = "B · Haas + 后环绕（旧算法备份）",
			Hint = "26ms 侧环 Haas 延迟 + 31ms 后环绕（2500Hz 低通）+ 140Hz 虚拟低音。"
				+ "环绕感明显更强，但会听到回声和嘶嘶声——这正是当初把它换掉的原因。代码在 SurroundDsp.cs 的 HaasRear 分支，参数记录在 SurroundDsp_orig.bak.txt。"
		},
		new SurroundModeInfo
		{
			Mode = SurroundMode.Echo,
			Title = "C · 同相 + 回声反馈",
			Hint = "在 A 的基础上加 110ms 反馈延迟回声（左右交叉、3500Hz 低通）。"
				+ "能明确听到回声，用来确认你要的到底是回声还是环绕。代码在 SurroundDsp.cs 的 Echo 分支。"
		},
		new SurroundModeInfo
		{
			Mode = SurroundMode.Combined,
			Title = "D · B + C 合并",
			Hint = "Haas 后环绕与反馈回声叠加。空间感最夸张，也最容易听着发脏，用来做上限参考。"
		},
		new SurroundModeInfo
		{
			Mode = SurroundMode.HifiMerged,
			Title = "模拟Hifi环绕 + B + D（回声为主）",
			Hint = "同相 HIFI 加宽打底 + B 的 Haas/后环绕压到三成当空间垫 + D 的 110ms 反馈回声当主角（湿声 45%、反馈 0.44）。"
				+ "三层叠加余量最小，所以总输出额外压低 16%。代码在 SurroundDsp.cs 的 ReadHifiMerged。"
		}
	};

	private void SyncComboToCustom()
	{
		string name = SettingsManager.Instance.Fx.PresetName ?? "";
		if (string.IsNullOrEmpty(name)) return;
		if (!FxPresetCombo.Items.Contains(name))
		{
			FxPresetCombo.Items.Add(name);
		}
		FxPresetCombo.SelectedIndex = FxPresetCombo.Items.IndexOf(name);
		FxPresetDescText.Text = "手工调节的均衡器（可复位）";
	}

	private void RefreshEq()
	{
		if (EqPanel == null) return;
		var s = SettingsManager.Instance;
		float[] gains = s.Fx?.EqGains ?? new float[10];
		EqPanel.Items.Clear();
		_eqUpdating = true;
		for (int b = 0; b < 10; b++)
		{
			float g = b < gains.Length ? gains[b] : 0f;
			var col = new StackPanel { Margin = new Thickness(0, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Center };

			var val = new TextBlock
			{
				Text = (g > 0 ? "+" : "") + g.ToString("0.#"),
				FontSize = 10.5,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 6, 0, 0)
			};
			// 同样是 SetResourceReference：EQ 数值和刻度也跟着主题走，
			// 不靠 ThemeManager 的快照（原因见 BuildSurroundModePanel）。
			val.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
			var slider = new Slider
			{
				Orientation = Orientation.Vertical,
				Width = 32,
				Height = 138,
				Minimum = -12,
				Maximum = 12,
				Value = Math.Clamp(g, -12, 12),
				Tag = new EqSliderTag { Band = b, Val = val }
			};
			slider.ValueChanged += EqSlider_ValueChanged;
			var lbl = new TextBlock
			{
				Text = _eqLabels[b],
				FontSize = 10.5,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 3, 0, 0)
			};
			lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
			col.Children.Add(slider);
			col.Children.Add(val);
			col.Children.Add(lbl);
			EqPanel.Items.Add(col);
		}
		_eqUpdating = false;
	}

	private void EqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_eqUpdating || !_ready || sender is not Slider { Tag: EqSliderTag tg } sl || tg.Val == null)
		{
			return;
		}
		var s = SettingsManager.Instance;
		s.Fx ??= new FxSettings();
		if (s.Fx.EqGains == null || s.Fx.EqGains.Length != 10) s.Fx.EqGains = new float[10];
		s.Fx.EqGains[tg.Band] = (float)e.NewValue;
		s.Fx.EqEnabled = s.Fx.EqGains.Any(g => Math.Abs(g) > 0.05f);
		s.Fx.PresetName = "自定义";
		tg.Val.Text = (e.NewValue > 0 ? "+" : "") + e.NewValue.ToString("0.#");
		SettingsManager.Save(s);
		_eqDebounce.Stop();
		_eqDebounce.Start();
	}

	private void EqResetButton_Click(object sender, RoutedEventArgs e)
	{
		var s = SettingsManager.Instance;
		s.Fx ??= new FxSettings();
		s.Fx.EqGains = new float[10];
		s.Fx.EqEnabled = false;
		s.Fx.PresetName = "自定义";
		SettingsManager.Save(s);
		RefreshEq();
		if (_ready)
		{
			SyncComboToCustom();
			MusicService.ApplyFxRestart();
		}
	}

	private void OnStateChanged()
	{
		// 环绕状态可能被外部切换（如播放模式切换），刷新开关视觉
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action(RefreshSurroundVisual));
			return;
		}
		RefreshSurroundVisual();
	}

	private void RefreshSurroundVisual()
	{
		if (_ready)
		{
			SurroundSwitch.IsChecked = SettingsManager.Instance.SurroundEnable;
		}
	}

	private void SurroundSwitch_Checked(object sender, RoutedEventArgs e)
	{
		if (_ready)
		{
			ApplySurround(true);
		}
	}

	private void SurroundSwitch_Unchecked(object sender, RoutedEventArgs e)
	{
		if (_ready)
		{
			ApplySurround(false);
		}
	}

	private void ApplySurround(bool enabled)
	{
		var s = SettingsManager.Instance;
		s.SurroundEnable = enabled;
		SettingsManager.Save(s);
		MusicService.ApplySurroundToggle();
	}

	private void SurroundStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_ready)
		{
			return;
		}
		var s = SettingsManager.Instance;
		s.SurroundStrength = Math.Round(e.NewValue, 2);
		StrengthValueText.Text = s.SurroundStrength.ToString("0.00", CultureInfo.InvariantCulture);
		_strengthDebounce.Stop();
		_strengthDebounce.Start();
	}

	private void FxPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_ready || FxPresetCombo.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
		{
			return;
		}
		FxPreset preset = FxPreset.Find(name);
		ApplyPreset(name);
		FxPresetDescText.Text = preset.Description;
		if (_ready) RefreshEq();
	}

	/// <summary>应用某个音效预设（预设选择 + 底部音效弹窗共用）。</summary>
	public static void ApplyPreset(string name)
	{
		FxPreset preset = FxPreset.Find(name);
		var s = SettingsManager.Instance;
		s.Fx.PresetName = preset.Name;
		s.Fx.EqEnabled = preset.EqGains.Any(g => Math.Abs(g) > 0.05f);
		s.Fx.EqGains = (float[])preset.EqGains.Clone();
		s.Fx.BassBoostEnabled = preset.BassBoostEnabled;
		s.Fx.BassBoostGain = preset.BassBoostGain;
		s.Fx.ReverbEnabled = preset.ReverbEnabled;
		s.Fx.ReverbRoomSize = preset.ReverbRoomSize;
		s.Fx.ReverbWetLevel = preset.ReverbWetLevel;
		s.Fx.ExciterEnabled = preset.ExciterEnabled;
		s.Fx.ExciterAmount = preset.ExciterAmount;
		s.Fx.CompressorEnabled = preset.CompressorEnabled;
		s.Fx.CompressorThreshold = preset.CompressorThreshold;
		s.Fx.CompressorRatio = preset.CompressorRatio;
		s.Fx.WidthEnabled = preset.WidthEnabled;
		s.Fx.WidthAmount = preset.WidthAmount;
		s.Fx.SurroundEnabled = preset.SurroundEnabled;
		s.SurroundEnable = preset.SurroundEnabled;
		s.SurroundStrength = preset.SurroundStrength;
		// 环绕算法变体也随预设走，否则四个试听预设听感会一模一样。
		// 注意 FxSettings.SurroundEnabled 一直是没人读的死设置（链里读的是
		// AppSettings.SurroundEnable），赋值保留是为了不破坏既有配置文件形状。
		s.Fx.SurroundMode = preset.SurroundMode;
		s.SurroundMode = preset.SurroundMode;
		SettingsManager.Save(s);
		MusicService.ApplyFxRestart();
	}
}
