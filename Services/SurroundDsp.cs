using System;
using NAudio.Wave;

namespace MusicBox.Services;

/// <summary>环绕算法变体。这几套是同一件事的四种做法，
/// 之前只留了 <see cref="SamePhase"/> 一套，因为另外几套听着都「有回声 /
/// 有耳鸣」，用户要求先试听再决定，所以拆成可切换的变体而不是直接删掉。</summary>
public enum SurroundMode
{
	/// <summary>同相加宽：所有空间分量左右同相加入，不引入耳间延迟。
	/// 声像稳定、中心人声不飘，是目前默认的一套。</summary>
	SamePhase = 0,

	/// <summary>Haas 延迟 + 后环绕：恢复 SurroundDsp_orig.bak.txt 记录的旧算法。
	/// 26ms 侧环 Haas、31ms 后环绕、2500Hz 后环绕低通、140Hz 虚拟低音。
	/// 环绕感明显得多，但会引入可听的「回声 / 耳鸣嘶嘶」——
	/// 这正是当初把它换掉的原因，所以只作为候选留着。</summary>
	HaasRear = 1,

	/// <summary>同相加宽 + 反馈延迟回声：显式加一条可听的回声，
	/// 用来确认用户说的「回声」到底是想要延迟回声还是环绕。</summary>
	Echo = 2,

	/// <summary>HaasRear + 回声：两者叠加，最夸张的一套。</summary>
	Combined = 3
}

/// <summary>
/// 模拟 Hifi 环绕：对称 M/S 加宽 + 带限去相关环境（Schroeder，g 小避免梳状嗡鸣）
/// + 单声道虚拟低音。所有空间分量都"同相位"加入左右声道（无 ITD/先到延迟），
/// 因此声像不会偏左/偏右、中心人声不会飘到脑后方。低频瞬态（鼓/贝斯）不进入
/// 去相关通路，避免低频被打散成刺耳鸣音。
/// Strength(0..1) 统一缩放效果分量。
/// </summary>
public sealed class SurroundEffectSampleProvider : ISampleProvider
{
	private const float DecorrDelayMs = 5f;

	private const float AmbLowHz = 350f;

	private const float AmbHighHz = 2400f;

	private const float SubHz = 90f;

	private const float AllpassG = 0.28f;

	// ---- HaasRear 变体参数（来自 SurroundDsp_orig.bak.txt 的原始常量）----
	private const float HaasMs = 26f;

	private const float RearDelayMs = 31f;

	private const float RearLowpassHz = 2500f;

	private const float BassHz = 140f;

	private const float HaasAllpassMs = 13f;

	private const float HaasAllpassG = 0.62f;

	// ---- Echo 变体参数 ----
	// 110ms 大约是 1/4 拍（60bpm）附近的听感回声间隔，再短就变成梳状染色。
	private const float EchoDelayMs = 110f;

	private const float EchoFeedback = 0.36f;

	private const float EchoLowpassHz = 3500f;

	private readonly ISampleProvider _source;

	private readonly float _strength;

	private readonly SurroundMode _mode;

	private float[] _inBuf;

	private readonly float[] _allpassIn;

	private readonly float[] _allpassOut;

	private readonly int _allpassDelay;

	private int _allpassPos;

	private float _lpAmbLow;

	private float _lpAmbHigh;

	private float _lpSub;

	private readonly float _lpAlphaAmbLow;

	private readonly float _lpAlphaAmbHigh;

	private readonly float _lpAlphaSub;

	// ---- HaasRear 状态 ----
	// Haas 延迟线和全通去相关必须各用各的缓冲与相位指针：
	// 共用一条线的话两个效果会互相踩对方的样本，回声深度每次都不一样。
	private readonly float[] _haasIn;

	private readonly int _haasDelay;

	private int _haasPos;

	private readonly float[] _haasApIn;

	private readonly float[] _haasApOut;

	private readonly int _haasApDelay;

	private int _haasApPos;

	private readonly float[] _rearBuf;

	private readonly int _rearDelay;

	private int _rearPos;

	private float _lpRear;

	private float _lpBass;

	private readonly float _lpAlphaRear;

	private readonly float _lpAlphaBass;

	// ---- Echo 状态（左右各一条反馈延迟线，互相串声做出横向回响）----
	private readonly float[] _echoL;

	private readonly float[] _echoR;

	private readonly int _echoDelay;

	private int _echoPos;

	private float _lpEchoL;

	private float _lpEchoR;

	private readonly float _lpAlphaEcho;

	private bool _usesHaas => _mode == SurroundMode.HaasRear || _mode == SurroundMode.Combined;

	private bool _usesEcho => _mode == SurroundMode.Echo || _mode == SurroundMode.Combined;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public SurroundEffectSampleProvider(ISampleProvider source, float strength, SurroundMode mode = SurroundMode.SamePhase)
	{
		_source = source;
		_strength = Math.Clamp(strength, 0f, 1f);
		_mode = mode;
		int sampleRate = source.WaveFormat.SampleRate;
		_allpassDelay = Math.Max(1, (int)(DecorrDelayMs * (float)sampleRate / 1000f));
		_allpassIn = new float[_allpassDelay + 1];
		_allpassOut = new float[_allpassDelay + 1];
		_lpAlphaAmbLow = 1f - (float)Math.Exp((double)(0f - 2f * (double)Math.PI * AmbLowHz) / (double)sampleRate);
		_lpAlphaAmbHigh = 1f - (float)Math.Exp((double)(0f - 2f * (double)Math.PI * AmbHighHz) / (double)sampleRate);
		_lpAlphaSub = 1f - (float)Math.Exp((double)(0f - 2f * (double)Math.PI * SubHz) / (double)sampleRate);

		_haasDelay = Math.Max(1, (int)(HaasMs * (float)sampleRate / 1000f));
		_haasIn = new float[_haasDelay + 1];
		_haasApDelay = Math.Max(1, (int)(HaasAllpassMs * (float)sampleRate / 1000f));
		_haasApIn = new float[_haasApDelay + 1];
		_haasApOut = new float[_haasApDelay + 1];
		_rearDelay = Math.Max(1, (int)(RearDelayMs * (float)sampleRate / 1000f));
		_rearBuf = new float[_rearDelay + 1];
		_lpAlphaRear = 1f - (float)Math.Exp((double)(0f - 2f * (double)Math.PI * RearLowpassHz) / (double)sampleRate);
		_lpAlphaBass = 1f - (float)Math.Exp((double)(0f - 2f * (double)Math.PI * BassHz) / (double)sampleRate);

		_echoDelay = Math.Max(1, (int)(EchoDelayMs * (float)sampleRate / 1000f));
		_echoL = new float[_echoDelay + 1];
		_echoR = new float[_echoDelay + 1];
		_lpAlphaEcho = 1f - (float)Math.Exp((double)(0f - 2f * (double)Math.PI * EchoLowpassHz) / (double)sampleRate);

		_inBuf = new float[source.WaveFormat.Channels * 4096];
	}

	public int Read(float[] buffer, int offset, int count)
	{
		float strength = _strength;
		int channels = _source.WaveFormat.Channels;
		if (_inBuf.Length < count)
		{
			_inBuf = new float[count];
		}
		int num = _source.Read(_inBuf, 0, count);
		int frames = num / channels;

		if (_usesHaas)
		{
			return ReadHaas(buffer, offset, frames, strength);
		}

		// 空间分量全部同相注入 L/R（amb/dec 加到两边、wide 以 M/S 对称展开），
		// 不引入耳间延迟与极性不对称，声像稳定在原始位置。
		float sideGain = 1f + 0.16f * strength;
		float ambGain = 0.20f * strength;
		float subBoost = 0.08f * strength;
		float outScale = 1f - 0.05f * strength; // 给环绕叠加留余量，避免输入驱动限幅器而发硬
		for (int i = 0; i < frames; i++)
		{
			float left = _inBuf[i * channels];
			float right = _inBuf[i * channels + 1];
			float mid = (left + right) * 0.5f;
			float side = (left - right) * 0.5f;
			float dec = AllpassDiff(mid);
			_lpAmbLow += _lpAlphaAmbLow * (dec - _lpAmbLow);
			float band = dec - _lpAmbLow; // 高通 350Hz：低频不去相关，防止鼓/贝斯被梳出鸣音或串到反相声道
			_lpAmbHigh += _lpAlphaAmbHigh * (band - _lpAmbHigh);
			float amb = _lpAmbHigh * ambGain; // 350~2400Hz 单声道环境，左右同相加
			_lpSub += _lpAlphaSub * (mid - _lpSub);
			float sub = _lpSub * subBoost; // 单声道虚拟低音，居中不漂移
			float wide = side * sideGain;

			float outL = mid + wide + amb + sub;
			float outR = mid - wide + amb + sub;
			if (_usesEcho)
			{
				EchoTick(outL, outR, ref outL, ref outR, strength);
			}
			buffer[offset + i * 2] = SoftClip(outL * outScale);
			buffer[offset + i * 2 + 1] = SoftClip(outR * outScale);
		}
		return num;
	}

	/// <summary>HaasRear / Combined 变体：恢复旧算法那套带延迟的空间展开。
	/// 与同相版的区别就是这里引入了耳间延迟（26ms Haas）和后环绕（31ms），
	/// 环绕感更强，但代价就是可听的回声/染色——所以它不是默认值。</summary>
	private int ReadHaas(float[] buffer, int offset, int frames, float strength)
	{
		int channels = _source.WaveFormat.Channels;
		float sideGain = 1f + 0.55f * strength;
		float decorrMix = 0.5f * strength;
		float rearGain = 0.4f * strength;
		float bassBoost = 0.45f * strength;
		float outScale = 1f - 0.08f * strength; // 这套叠加更多，留更多余量
		for (int i = 0; i < frames; i++)
		{
			float left = _inBuf[i * channels];
			float right = _inBuf[i * channels + 1];
			float mid = (left + right) * 0.5f;
			float side = (left - right) * 0.5f;

			// 26ms Haas 延迟作用在侧环信号上，串到对侧声道制造方位感。
			// 延迟只喂给 side，不碰 mid，中心人声因此不会被推到脑后方。
			float sideDelayed = HaasDelay(side);
			float wide = side * sideGain;
			float haas = sideDelayed * 0.5f * strength;

			// 13ms 全通去相关（g=0.62），比同相版更「开」也更容易出嘶嘶
			float dec = HaasAllpass(mid) * decorrMix;

			// 31ms 后环绕 + 2500Hz 低通，加在左右两边（居中，不偏）
			float rear = RearDelay(mid);
			_lpRear += _lpAlphaRear * (rear - _lpRear);
			float rearOut = _lpRear * rearGain;

			// 140Hz 虚拟低音
			_lpBass += _lpAlphaBass * (mid - _lpBass);
			float bass = _lpBass * bassBoost;

			float outL = mid + wide + haas + dec + rearOut + bass;
			float outR = mid - wide - haas + dec + rearOut + bass;
			if (_usesEcho)
			{
				EchoTick(outL, outR, ref outL, ref outR, strength);
			}
			buffer[offset + i * 2] = SoftClip(outL * outScale);
			buffer[offset + i * 2 + 1] = SoftClip(outR * outScale);
		}
		return frames * 2;
	}

	/// <summary>反馈延迟回声。左右两条延迟线互相串声，
	/// 出来的回声在两侧都有，比单声道回声宽。回声路带 3500Hz 低通避免刺耳。</summary>
	private void EchoTick(float inL, float inR, ref float outL, ref float outR, float strength)
	{
		int pos = _echoPos;
		float delayedL = _echoL[pos];
		float delayedR = _echoR[pos];
		_lpEchoL += _lpAlphaEcho * (delayedL - _lpEchoL);
		_lpEchoR += _lpAlphaEcho * (delayedR - _lpEchoR);
		float wetL = _lpEchoL;
		float wetR = _lpEchoR;

		// 反馈：把（干声+湿声）写回延迟线。反馈量固定 0.36，
		// 再高就会自激，链末的 Limiter 只能压住峰值、压不住这种累积增益。
		_echoL[pos] = inL + wetL * EchoFeedback;
		_echoR[pos] = inR + wetR * EchoFeedback;
		_echoPos = (pos + 1) % _echoDelay;

		float mix = 0.28f * strength;
		outL = inL + wetR * mix; // 交叉：右回声喂左，听感更宽
		outR = inR + wetL * mix;
	}

	private float HaasDelay(float x)
	{
		int delay = _haasDelay;
		int pos = _haasPos;
		float xD = _haasIn[pos];
		_haasIn[pos] = x;
		_haasPos = (pos + 1) % delay;
		return xD;
	}

	/// <summary>13ms / g=0.62 全通，用来实现旧算法的去相关成分。</summary>
	private float HaasAllpass(float x)
	{
		int delay = _haasApDelay;
		int pos = _haasApPos;
		float xD = _haasApIn[pos];
		float yD = _haasApOut[pos];
		_haasApIn[pos] = x;
		float y = xD - HaasAllpassG * x + HaasAllpassG * yD;
		_haasApOut[pos] = y;
		_haasApPos = (pos + 1) % delay;
		return y - x;
	}

	private float RearDelay(float x)
	{
		int pos = _rearPos;
		float old = _rearBuf[pos];
		_rearBuf[pos] = x;
		_rearPos = (pos + 1) % _rearDelay;
		return old;
	}

	/// <summary>Schroeder 全通输出与输入之差：相位打散后与原信号去相关，可提取环境成分。</summary>
	private float AllpassDiff(float x)
	{
		int delay = _allpassDelay;
		int pos = _allpassPos;
		float xD = _allpassIn[pos];
		float yD = _allpassOut[pos];
		_allpassIn[pos] = x;
		float y = xD - AllpassG * x + AllpassG * yD;
		_allpassOut[pos] = y;
		_allpassPos = (pos + 1) % delay;
		return y - x;
	}

	private static float SoftClip(float x)
	{
		if (x > 1f || x < -1f)
		{
			return (float)Math.Tanh(x);
		}
		return x;
	}
}

public sealed class AutoStereoSampleProvider : ISampleProvider
{
	private readonly ISampleProvider _source;

	private readonly int _srcChannels;

	private float[] _scaled;

	public WaveFormat WaveFormat { get; }

	public AutoStereoSampleProvider(ISampleProvider source)
	{
		_source = source;
		_srcChannels = source.WaveFormat.Channels;
		WaveFormat = (source.WaveFormat.Channels == 2) ? source.WaveFormat : WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
		_scaled = new float[Math.Max(_srcChannels, 2) * 4096];
	}

	public int Read(float[] buffer, int offset, int count)
	{
		if (_srcChannels == 2)
		{
			return _source.Read(buffer, offset, count);
		}
		int need = (count / 2) * _srcChannels;
		if (_scaled.Length < need)
		{
			_scaled = new float[need];
		}
		int num = _source.Read(_scaled, 0, need);
		int frames = num / _srcChannels;
		if (frames * 2 > buffer.Length - offset)
		{
			frames = (buffer.Length - offset) / 2;
			num = frames * _srcChannels;
		}
		for (int i = 0; i < frames; i++)
		{
			float l = _scaled[i * _srcChannels];
			float r = (_srcChannels == 1) ? l : _scaled[i * _srcChannels + 1];
			buffer[offset + i * 2] = l;
			buffer[offset + i * 2 + 1] = r;
		}
		return frames * 2;
	}
}