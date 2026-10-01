using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

/// <summary>
/// 简单单频带峰值限制器：对峰值包络做快速起音 / 慢速释放的增益压缩，
/// 把输出钳制在 ceiling 以下，避免 EQ/低音提升叠加后在 0dBFS 硬削波（炸麦）。
/// 作用在 FX 链末尾，任何音效增益之下都保证不破音。
/// </summary>
public sealed class LimiterProvider : ISampleProvider
{
	private const float Ceiling = 0.995f;
	private const float Threshold = 0.95f; // 很接近 0dBFS 才介入，瞬态鼓点不易被削出"咔嚓/爆鸣"
	private const float AttackMs = 4.0f; // 慢起音避免对瞬态突然压出哔咔
	private const float ReleaseMs = 70.0f;

	private readonly ISampleProvider _source;
	private readonly float _attack;
	private readonly float _release;
	private float _envelope;
	private float _gain;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public LimiterProvider(ISampleProvider source)
	{
		_source = source;
		float sr = source.WaveFormat.SampleRate;
		_attack = 1f - (float)Math.Exp(-1.0 / (sr * AttackMs * 0.001));
		_release = 1f - (float)Math.Exp(-1.0 / (sr * ReleaseMs * 0.001));
		_envelope = 0f;
		_gain = 1f;
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		for (int i = 0; i < read; i++)
		{
			float x = buffer[offset + i];
			float abs = x < 0f ? -x : x;
			if (abs > _envelope)
				_envelope = _envelope + _attack * (abs - _envelope);
			else
				_envelope = _envelope + _release * (abs - _envelope);

			float desired = 1f;
			if (_envelope > Threshold)
				desired = Ceiling / _envelope;

			if (desired < _gain)
				_gain = desired + _attack * (_gain - desired);   // 快速压低
			else if (desired > _gain)
				_gain = _gain + _release * (desired - _gain);    // 缓慢回升

			buffer[offset + i] = x * _gain;
		}
		return read;
	}
}