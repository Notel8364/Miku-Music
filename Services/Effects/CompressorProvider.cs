using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

public sealed class CompressorProvider : ISampleProvider
{
	private readonly ISampleProvider _source;
	private readonly int _channels;
	private float _threshold;
	private float _ratio;
	private float _attackCoeff;
	private float _releaseCoeff;
	private float _envelope;
	private float _gain;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public CompressorProvider(ISampleProvider source, float thresholdDb, float ratio)
	{
		_source = source;
		_channels = source.WaveFormat.Channels;
		int sr = source.WaveFormat.SampleRate;
		_threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);
		_ratio = Math.Max(1f, ratio);
		_attackCoeff = (float)Math.Exp(-1.0 / (sr * 0.005));
		_releaseCoeff = (float)Math.Exp(-1.0 / (sr * 0.050));
		_envelope = 0f;
		_gain = 1f;
	}

	public void SetThreshold(float thresholdDb)
	{
		_threshold = (float)Math.Pow(10.0, thresholdDb / 20.0);
	}

	public void SetRatio(float ratio)
	{
		_ratio = Math.Max(1f, ratio);
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		for (int i = 0; i < read; i += _channels)
		{
			float absL = Math.Abs(buffer[offset + i]);
			float absR = (_channels >= 2 && i + 1 < read) ? Math.Abs(buffer[offset + i + 1]) : 0f;
			float peak = Math.Max(absL, absR);
			if (peak > _envelope)
				_envelope = _attackCoeff * (_envelope - peak) + peak;
			else
				_envelope = _releaseCoeff * (_envelope - peak) + peak;
			if (_envelope > _threshold)
			{
				float overDb = 20f * (float)Math.Log10(_envelope / _threshold);
				float compressedDb = overDb / _ratio;
				_gain = _threshold * (float)Math.Pow(10.0, compressedDb / 20.0) / _envelope;
			}
			else
			{
				_gain += (1f - _gain) * 0.01f;
			}
			buffer[offset + i] *= _gain;
			if (_channels >= 2 && i + 1 < read)
				buffer[offset + i + 1] *= _gain;
		}
		return read;
	}
}
