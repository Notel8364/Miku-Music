using System;
using System.Collections.Generic;
using MusicBox.Services;

namespace MusicBox.Services.Effects;

public sealed class FxPreset
{
	public string Name { get; }
	public string Description { get; }
	public float[] EqGains { get; }
	public bool BassBoostEnabled { get; }
	public float BassBoostGain { get; }
	public bool ReverbEnabled { get; }
	public float ReverbRoomSize { get; }
	public float ReverbWetLevel { get; }
	public bool ExciterEnabled { get; }
	public float ExciterAmount { get; }
	public bool CompressorEnabled { get; }
	public float CompressorThreshold { get; }
	public float CompressorRatio { get; }
	public bool WidthEnabled { get; }
	public float WidthAmount { get; }
	public bool SurroundEnabled { get; }
	public float SurroundStrength { get; }
	public SurroundMode SurroundMode { get; }

	public FxPreset(string name, string description,
		float[] eqGains,
		bool bassEnabled = false, float bassGain = 0f,
		bool reverbEnabled = false, float reverbRoom = 0.3f, float reverbWet = 0.2f,
		bool exciterEnabled = false, float exciterAmount = 0f,
		bool compressorEnabled = false, float compressorThreshold = -20f, float compressorRatio = 2f,
		bool widthEnabled = false, float widthAmount = 1f,
		bool surroundEnabled = false, float surroundStrength = 0.85f,
		SurroundMode surroundMode = SurroundMode.SamePhase)
	{
		Name = name;
		Description = description;
		EqGains = eqGains;
		BassBoostEnabled = bassEnabled;
		BassBoostGain = bassGain;
		ReverbEnabled = reverbEnabled;
		ReverbRoomSize = reverbRoom;
		ReverbWetLevel = reverbWet;
		ExciterEnabled = exciterEnabled;
		ExciterAmount = exciterAmount;
		CompressorEnabled = compressorEnabled;
		CompressorThreshold = compressorThreshold;
		CompressorRatio = compressorRatio;
		WidthEnabled = widthEnabled;
		WidthAmount = widthAmount;
		SurroundEnabled = surroundEnabled;
		SurroundStrength = surroundStrength;
		SurroundMode = surroundMode;
	}

	public static readonly FxPreset None = new FxPreset("关闭", "无音效",
		new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

	public static readonly List<FxPreset> Presets = new List<FxPreset>
	{
		None,
		new FxPreset("模拟Hifi环绕", "纯净环绕场（立体声加宽 + 虚拟环绕，默认强度 1.0）",
			new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 1.0f,
			widthEnabled: true, widthAmount: 1.05f),

		// 「模拟Hifi环绕」按要求合并了 HIFI 基础 + B + D，并以 D 为主。
		// 强度压到 0.85：三层都在加东西，1.0 时 outScale 只剩 0.84，
		// 试听 D 时觉得「顶」，这里往下收一点。
		new FxPreset("模拟Hifi环绕+回声主", "合并版：同相 HIFI 加宽打底 + B 的 Haas/后环绕压低当空间垫 + D 的 110ms 反馈回声当主角（湿声 45%）。",
			new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 0.85f,
			surroundMode: SurroundMode.HifiMerged,
			widthEnabled: true, widthAmount: 1.05f),

		// ---- 环绕算法试听变体 ----
		// 这四套是同一件事的四种做法，拆成独立预设是为了让用户直接 A/B 试听。
		// 听感反馈之前不要把其中任何一套设成默认。
		new FxPreset("环绕·A 同相加宽", "试听 A：空间分量左右同相加入，无延迟。声像最稳、中心人声不飘，没有回声。",
			new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 1.0f,
			surroundMode: SurroundMode.SamePhase,
			widthEnabled: true, widthAmount: 1.05f),
		new FxPreset("环绕·B Haas后环绕", "试听 B：恢复旧算法（26ms Haas + 31ms 后环绕 + 2500Hz 低通 + 140Hz 虚拟低音）。环绕感最强，但会听到回声和嘶嘶声。",
			new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 0.8f,
			surroundMode: SurroundMode.HaasRear,
			widthEnabled: true, widthAmount: 1.05f),
		new FxPreset("环绕·C 回声反馈", "试听 C：A 的加宽 + 110ms 反馈延迟回声（左右交叉）。能听出明确的回声，用来确认你要的是回声还是环绕。",
			new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 0.9f,
			surroundMode: SurroundMode.Echo,
			widthEnabled: true, widthAmount: 1.05f),
		new FxPreset("环绕·D 合并", "试听 D：B + C 叠加。空间感最夸张，也最容易觉得「脏」，用来做上限参考。",
			new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 0.75f,
			surroundMode: SurroundMode.Combined,
			widthEnabled: true, widthAmount: 1.05f),

		new FxPreset("全景环绕", "模拟全景声环绕场",
			new float[] { 1, 0.5f, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f },
			surroundEnabled: true, surroundStrength: 0.65f,
			widthEnabled: true, widthAmount: 1.05f),
		// 超重低音 备份（上一版）：Eq {5, 4, 1.2, -0.8, -0.8, -0.3, -0.2, -0.8, -0.6, -1.2} / bassGain 4
		// （再上一版：Eq {5.5, 4.5, 1.6, 0.3, 0, -0.3, -0.8, -1.3, -0.9, -1.3} / bassGain 4.2）
		// 方向：保留深低音随节拍起伏的波浪/现场律动感（依赖限幅器慢释放的"呼吸"），
		// 收掉 31/62 的过量堆积仍保冲击；125 归零、250 加深到 -1.2，避开 120Hz 低架的"方盒"重叠区，
		// 让低频与人声/军鼓分离、不再闷实；4k(-0.3)/8k(0)/16k(-0.5) 保留鼓皮哒声与镲片/空气感。
		new FxPreset("超重低音", "强化低频冲击力（深低音 + 干净鼓点，随节拍起伏律动）",
			new float[] { 3.0f, 3.0f, 0f, -1.2f, -0.8f, -0.3f, 0.2f, -0.3f, 0f, -0.5f },
			bassEnabled: true, bassGain: 3.0f),
		new FxPreset("清澈人声", "突出人声清晰度（自然不刺耳，不闷）",
			new float[] { -1, 0, 1, 2, 3, 3, 2.5f, 1.5f, 0.5f, 0 }),
		new FxPreset("现场律动", "模拟现场演出空间感（软压缩钳制动态，防炸麦）",
			new float[] { 1, 0.5f, 0, 1, 2, 2, 1, 0, -1, -2 },
			reverbEnabled: true, reverbRoom: 0.4f, reverbWet: 0.2f,
			compressorEnabled: true, compressorThreshold: -18f, compressorRatio: 2f,
			widthEnabled: true, widthAmount: 1.1f),
		new FxPreset("温暖轻柔", "温暖舒适的听感",
			new float[] { 3, 2.5f, 1.5f, 0, 0, 0, -1, -3, -4, -4.5f },
			bassEnabled: true, bassGain: 2.5f,
			reverbEnabled: true, reverbRoom: 0.18f, reverbWet: 0.08f),
		new FxPreset("华丽复古", "复古黑胶唱片质感",
			new float[] { 3, 2, 0.5f, -1, 1, 2, 3, 1, 0, -1 },
			exciterEnabled: true, exciterAmount: 0.25f,
			widthEnabled: true, widthAmount: 0.9f)
	};

	public static FxPreset Find(string name)
	{
		foreach (var p in Presets)
		{
			if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
				return p;
		}
		return None;
	}
}
