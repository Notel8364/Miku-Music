using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using MusicBox.Settings;

namespace MusicBox.Services;

public enum HotkeyAction
{
	Next,
	Prev,
	PlayPause,
	Favorite,
	ToggleWindow,
	/// <summary>歌词窗「背景鼠标穿透」开关。
	/// 这一项必须能脱离鼠标操作：开了穿透之后窗口自己就点不中了，
	/// 唯一能把鼠标交还给用户的路就是全局快捷键（或托盘菜单）。</summary>
	ToggleClickThrough,
	/// <summary>音量小步调节（下=减 5%，上=加 5%），跟随系统媒体键习惯：
	/// 播放器全屏/歌词窗在别的应用之上时，音量条点不到，热键是唯一入口。
	/// 长按时由 MainWindow 启动定时器连续步进并逐渐加速（见 IsComboDown）。</summary>
	VolumeDown,
	VolumeUp,
	/// <summary>切换歌词窗显示/隐藏。桌面歌词开与关原本只能去设置页或歌词窗标题栏，
	/// 歌词窗开了穿透之后连标题栏都点不中，全局热键是唯一可靠入口。</summary>
	LyricsToggle,
}

/// <summary>全局快捷键：基于 RegisterHotKey / WM_HOTKEY。</summary>
public static class HotkeyManager
{
	public const int WM_HOTKEY = 0x0312;

	private const uint MOD_ALT = 0x1;
	private const uint MOD_CONTROL = 0x2;
	private const uint MOD_SHIFT = 0x4;
	private const uint MOD_WIN = 0x8;
	private const uint MOD_NOREPEAT = 0x4000;

	// 注意顺序：ActionNames[i] 必须与 HotkeyAction 枚举值 i 一一对应
	//（TryRegister 用数组下标反查枚举）。
	private static readonly string[] ActionNames = { "next", "prev", "playpause", "fav", "toggle", "lyric_passthrough", "vol_down", "vol_up", "lyrics_toggle" };

	/// <summary>首选组合。刻意避开 Ctrl+Shift+字母 / Ctrl+Alt+字母：
	/// 截图工具和中文输入法几乎都占这一片，日志里三个组合同时冲突就是这个原因。
	/// 方向键、空格这类非字母键被占的概率低得多。
	///
	/// 字母键要再加一层 Shift 才够空：实测同一台机器上 Ctrl+Alt+W 注册得上，
	/// 同一档的 Ctrl+Alt+F 和 Ctrl+Alt+L 却连备选全部失败，
	/// fav / lyric_passthrough 两个功能直接没有快捷键可用。
	/// 三层修饰键（Ctrl+Alt+Shift+字母）几乎没人占，所以字母键走这一档。
	/// 唯独 Ctrl+Alt+W 给了「切换歌词」（用户指定），
	/// 原来占着它的「切换主窗」让位改用 Ctrl+Alt+M（旧配置见 SettingsManager 迁移）。</summary>
	public static readonly string[] DefaultShortcuts =
		{ "Ctrl+Alt+Right", "Ctrl+Alt+Left", "Ctrl+Alt+Space", "Ctrl+Alt+Shift+F", "Ctrl+Alt+M", "Ctrl+Alt+Shift+L", "Ctrl+Alt+Down", "Ctrl+Alt+Up", "Ctrl+Alt+W" };

	/// <summary>首选组合被占用时的备选。按顺序试，第一个注册成功的生效。
	/// 没有这条链的话，「换个键」只是把冲突概率推给下一个用户——
	/// 同一个键被别的软件抢走是常态，不是意外。</summary>
	private static readonly string[][] FallbackShortcuts =
	{
		new[] { "Ctrl+G", "Ctrl+." },
		new[] { "Ctrl+Shift+G", "Ctrl+," },
		new[] { "Ctrl+P", "Ctrl+Space" },
		new[] { "Ctrl+Alt+F", "Ctrl+L", "Ctrl+Shift+F" },
		new[] { "Ctrl+Shift+M", "Ctrl+M" },
		new[] { "Ctrl+Alt+L", "Ctrl+K", "Ctrl+Shift+L", "Ctrl+Alt+P" },
		new[] { "Ctrl+Alt+Shift+Down", "Ctrl+Down" },
		new[] { "Ctrl+Alt+Shift+Up", "Ctrl+Up" },
		new[] { "Ctrl+Alt+Shift+W", "Ctrl+Alt+K", "Ctrl+Shift+W" },
	};

	/// <summary>实际注册成功的组合。下标与 <see cref="ActionNames"/> 对齐，
	/// 没注册上就是 null。设置页要显示这个而不是配置值，
	/// 否则用户看到的和真正生效的不是一回事。</summary>
	private static readonly string?[] Effective = new string?[ActionNames.Length];

	/// <summary>某个功能当前实际生效的组合，没有则返回配置里写的那个。</summary>
	public static string? EffectiveCombo(string action)
	{
		int i = Array.IndexOf(ActionNames, action);
		if (i < 0) return null;
		return Effective[i];
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

	private static IntPtr _hwnd;

	private static readonly Dictionary<int, HotkeyAction> ReverseMap = new();

	public static event Action<HotkeyAction>? Triggered;

	public static void Bind(IntPtr hwnd)
	{
		_hwnd = hwnd;
		RegisterAll();
	}

	public static bool IsRegistered => _hwnd != IntPtr.Zero;

	private static int IdOf(HotkeyAction action) => (int)action + 1;

	public static void RegisterAll()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		UnregisterAll();
		Array.Clear(Effective);
		var s = SettingsManager.Instance;
		for (int i = 0; i < ActionNames.Length; i++)
		{
			string? combo = s.Hotkeys != null && s.Hotkeys.TryGetValue(ActionNames[i], out string? c) ? c : null;
			if (string.IsNullOrWhiteSpace(combo))
			{
				combo = DefaultShortcuts[i];
			}
			// 首选注册成功（TryRegister 返回 true）才跳过备选链；
			// 失败要往下走，顺着备选一个个试。之前的条件写反了：
			// 失败时 continue 把备选链整个跳过，FallbackShortcuts 成了死代码 ——
			// 实测同一台机器上 Ctrl+Alt+F / Ctrl+Alt+L 被占用后日志里报
			// 「全部组合注册失败」，实际上一个备选都没试过。
			if (TryRegister(i, combo, s.DebugMode)) continue;
			foreach (string alt in FallbackShortcuts[i])
			{
				if (TryRegister(i, alt, s.DebugMode))
				{
					LogHelper.Log($"快捷键 {ActionNames[i]} 首选 {combo} 被占用，已改用 {alt}");
					break;
				}
			}
		}
		// 备选也全挂了才是真没戏，这时候一定要说一声。
		// 之前这里只在 DebugMode 下打一行「注册冲突」，非调试模式下
		// 快捷键悄无声息地全废了，界面上还是原来的字，看不出没生效。
		for (int i = 0; i < ActionNames.Length; i++)
		{
			if (Effective[i] == null)
			{
				LogHelper.Log($"快捷键 {ActionNames[i]} 全部组合注册失败（配置={SettingsManager.Instance.Hotkeys?.GetValueOrDefault(ActionNames[i]) ?? DefaultShortcuts[i]}），该功能当前只能用界面操作");
			}
		}
	}

	/// <summary>试一个组合。成功返回 true 并记下 ReverseMap / Effective。</summary>
	private static bool TryRegister(int index, string combo, bool debug)
	{
		var (ok, mods, vk) = ParseCombo(combo);
		if (!ok || vk == 0)
		{
			if (debug) LogHelper.Log("快捷键解析失败: " + ActionNames[index] + " = " + combo);
			return false;
		}
		int id = IdOf((HotkeyAction)index);
		if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
		{
			// 每次失败都要把上一次的登记清掉：RegisterHotKey 失败时
			// 这个 id 可能还挂着上一轮残留的 ReverseMap 条目。
			ReverseMap.Remove(id);
			return false;
		}
		ReverseMap[id] = (HotkeyAction)index;
		Effective[index] = combo;
		return true;
	}

	public static void UnregisterAll()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		foreach (int id in ReverseMap.Keys.ToList())
		{
			UnregisterHotKey(_hwnd, id);
		}
		ReverseMap.Clear();
	}

	/// <summary>处理 WM_HOTKEY 消息。</summary>
	public static bool HandleHotkey(IntPtr wParam, IntPtr lParam)
	{
		int id = wParam.ToInt32();
		if (ReverseMap.TryGetValue(id, out HotkeyAction action))
		{
			Triggered?.Invoke(action);
			return true;
		}
		return false;
	}

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	private const int VK_SHIFT = 0x10;
	private const int VK_CONTROL = 0x11;
	private const int VK_MENU = 0x12;
	private const int VK_LWIN = 0x5B;
	private const int VK_RWIN = 0x5C;

	/// <summary>判断某热键的组合键此刻是否仍被物理按住。
	/// 给「音量长按连续步进」用：WM_HOTKEY 挂了 MOD_NOREPEAT，按住不放也只来一条，
	/// 要不要继续步进、什么时候停，只能靠轮询真实按键状态来决定。
	/// 修饰键按注册组合里实际带的来查（用户可能改过键，不能写死 Ctrl+Alt）。</summary>
	public static bool IsComboDown(HotkeyAction action)
	{
		// ActionNames 与枚举值下标一一对应，直接反查配置名拿生效组合。
		string? combo = EffectiveCombo(ActionNames[(int)action]);
		if (string.IsNullOrWhiteSpace(combo))
		{
			return false;
		}
		var (ok, mods, vk) = ParseCombo(combo);
		if (!ok || vk == 0)
		{
			return false;
		}
		bool down(int k) => (GetAsyncKeyState(k) & 0x8000) != 0;
		if ((mods & MOD_CONTROL) != 0 && !down(VK_CONTROL)) return false;
		if ((mods & MOD_ALT) != 0 && !down(VK_MENU)) return false;
		if ((mods & MOD_SHIFT) != 0 && !down(VK_SHIFT)) return false;
		if ((mods & MOD_WIN) != 0 && !(down(VK_LWIN) || down(VK_RWIN))) return false;
		return down((int)vk);
	}

	public static (bool ok, uint mods, uint vk) ParseCombo(string combo)
	{
		try
		{
			string[] parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			uint mods = 0;
			string? keyPart = null;
			foreach (string p in parts)
			{
				string lower = p.ToLowerInvariant();
				switch (lower)
				{
					case "ctrl":
					case "control":
						mods |= MOD_CONTROL;
						break;
					case "alt":
						mods |= MOD_ALT;
						break;
					case "shift":
						mods |= MOD_SHIFT;
						break;
					case "win":
					case "cmd":
						mods |= MOD_WIN;
						break;
					default:
						keyPart = p;
						break;
				}
			}
			if (keyPart == null)
			{
				return (false, 0, 0);
			}
			uint vk;
			if (keyPart.Length == 1 && (char.IsLetterOrDigit(keyPart[0])))
			{
				vk = (uint)char.ToUpperInvariant(keyPart[0]);
			}
			else
			{
				if (Enum.TryParse(keyPart, true, out Key key))
				{
					vk = (uint)KeyInterop.VirtualKeyFromKey(key);
				}
				else
				{
					return (false, 0, 0);
				}
			}
			if (vk == 0)
			{
				return (false, 0, 0);
			}
			return (true, mods, vk);
		}
		catch
		{
			return (false, 0, 0);
		}
	}

	public static string BuildComboText(ModifierKeys mods, Key key)
	{
		var list = new List<string>();
		if ((mods & ModifierKeys.Control) != 0) list.Add("Ctrl");
		if ((mods & ModifierKeys.Alt) != 0) list.Add("Alt");
		if ((mods & ModifierKeys.Shift) != 0) list.Add("Shift");
		if ((mods & ModifierKeys.Windows) != 0) list.Add("Win");
		string keyName;
		try
		{
			keyName = KeyInterop.VirtualKeyFromKey(key) switch
			{
				0x30 => "0", 0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4",
				0x35 => "5", 0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9",
				_ => key.ToString()
			};
		}
		catch
		{
			keyName = key.ToString();
		}
		list.Add(keyName);
		return string.Join("+", list);
	}

	public static bool Validate(string combo)
	{
		var (ok, mods, vk) = ParseCombo(combo);
		return ok && mods != 0;
	}
}