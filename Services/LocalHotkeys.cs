using System;
using System.Collections.Generic;
using System.Windows.Input;
using MusicBox.Settings;

namespace MusicBox.Services;

/// <summary>程序内快捷键（Q/E/1/2/3 这类不带修饰键的键）。
///
/// 和 <see cref="HotkeyManager"/> 分成两套，原因是机制完全不同：
/// 全局键走 RegisterHotKey，系统层面注册，不抢任何程序的键，但要 Ctrl/Alt 才不撞车；
/// 局内键走 WPF 的 PreviewKeyDown，只有本窗口有焦点才生效，
/// 好处是能占用 Q/E/数字这种「裸键」——它们在全局层面根本注册不了
/// （光一个 E 谁都能按，一注册就被输入法和其他软件抢走）。
///
/// 键位存在 SettingsManager.LocalHotkeys，和全局那份 Hotkeys 分开两个字典，
/// 免得改局内键时把全局键的表也搅乱。
/// </summary>
public enum LocalAction
{
	/// <summary>列表里按 Q：上一首。</summary>
	ListPrev,
	/// <summary>列表里按 E：下一首。</summary>
	ListNext,
	/// <summary>局内按 1：上一首。</summary>
	Prev,
	/// <summary>局内按 2：下一首。</summary>
	Next,
	/// <summary>局内按 3：播放 / 暂停。</summary>
	PlayPause,
}

/// <summary>局内快捷键的绑定表。只读配置 + 匹配，不负责执行动作。</summary>
public static class LocalHotkeys
{
	/// <summary>与 <see cref="LocalAction"/> 的取值顺序严格对齐，不能调换。</summary>
	private static readonly string[] ActionNames =
		{ "list_prev", "list_next", "prev", "next", "playpause" };

	/// <summary>默认键位。跟 <see cref="ActionNames"/> 对齐。
	/// D1/D2/D3 而不是 1/2/3：存的是 Key 枚举名，反解回来是 D1 这类名字，
	/// 写成 "1" 的话 <see cref="Key"/> 里根本没有这个名字。</summary>
	private static readonly string[] Defaults = { "Q", "E", "D1", "D2", "D3" };

	/// <summary>这个键位字符串对应的 Key；解析不了返回 Key.None。</summary>
	public static Key KeyOf(string action)
	{
		int i = Array.IndexOf(ActionNames, action);
		if (i < 0)
		{
			return Key.None;
		}
		return Enum.TryParse(Current(action), true, out Key key) ? key : Key.None;
	}

	/// <summary>当前生效的键位文本（没配就用默认值）。设置页显示这个。</summary>
	public static string Current(string action)
	{
		int i = Array.IndexOf(ActionNames, action);
		if (i < 0)
		{
			return "";
		}
		var s = SettingsManager.Instance;
		string? custom = s.LocalHotkeys != null && s.LocalHotkeys.TryGetValue(action, out string? c) ? c : null;
		return string.IsNullOrWhiteSpace(custom) ? Defaults[i] : custom!;
	}

	/// <summary>把一个键位文本写成设置里的样子。写之前先验一遍，
	/// 解析不了的直接返回 false —— 设置页拿到 false 就不落盘，
	/// 免得存进去一个下次启动认不出来的字符串。</summary>
	public static bool TrySet(string action, string combo)
	{
		if (Array.IndexOf(ActionNames, action) < 0)
		{
			return false;
		}
		if (!Enum.TryParse(combo.Trim(), true, out Key key) || key == Key.None)
		{
			return false;
		}
		var s = SettingsManager.Instance;
		s.LocalHotkeys ??= new Dictionary<string, string>();
		s.LocalHotkeys[action] = key.ToString();
		SettingsManager.Save(s);
		return true;
	}

	/// <summary>这个键位是不是已经被别的功能占了。设置页改键时用它拦一下，
	/// 否则两个功能抢同一个键，用户按下去只有一个有反应。</summary>
	public static bool IsTakenByOther(string action, string combo)
	{
		foreach (string other in ActionNames)
		{
			if (string.Equals(other, action, StringComparison.Ordinal))
			{
				continue;
			}
			if (string.Equals(Current(other), combo.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>把一次按键翻成动作。
	///
	/// 带修饰键的一律不认：Ctrl+E 是「光标移到行尾」，Alt+2 在某些输入法里
	/// 是切中文/英文数字，这些都得留给原来的功能。裸键才当快捷键。
	/// </summary>
	public static LocalAction? Resolve(Key key, ModifierKeys mods)
	{
		if (mods != ModifierKeys.None || key == Key.None || key == Key.ImeProcessed)
		{
			return null;
		}
		for (int i = 0; i < ActionNames.Length; i++)
		{
			if (KeyOf(ActionNames[i]) == key)
			{
				return (LocalAction)i;
			}
		}
		return null;
	}

	/// <summary>把缺项补成默认值，返回补全后的字典。
	///
	/// 只收字典、返回字典，不碰 SettingsManager.Instance：这段是在
	/// SettingsManager.Load() 里对刚反序列化出来的对象跑的，
	/// 那时 _instance 还没赋值，碰 Instance 会递归进 Load 自己。
	/// </summary>
	public static Dictionary<string, string> WithDefaults(Dictionary<string, string>? existing)
	{
		var dict = existing ?? new Dictionary<string, string>();
		foreach (string name in ActionNames)
		{
			if (!dict.ContainsKey(name))
			{
				dict[name] = Defaults[Array.IndexOf(ActionNames, name)];
			}
		}
		return dict;
	}
}