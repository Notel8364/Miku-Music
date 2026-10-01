using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

public sealed class ExciterProvider : ISampleProvider
{
	private readonly ISampleProvider _source;
	private readonly int _channels;
	private float _amount;
	private float _hpStateL, _hpStateR;
	private float _lpStateL, _lpStateR;
	private readonly float _hpAlpha;
	private readonly float _lpAlpha;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public ExciterProvider(ISampleProvider source, float amount)
	{
		_source = source;
		_channels = source.WaveFormat.Channels;
		_amount = Math.Clamp(amount, 0f, 1f);
		float sr = source.WaveFormat.SampleRate;
		float hpCutoff = 2000f;
		float lpCutoff = 8000f;
		_hpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * hpCutoff / sr);
		_lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / sr);
	}

	public void SetAmount(float amount)
	{
		_amount = Math.Clamp(amount, 0f, 1f);
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		if (_amount <= 0.001f) return read;
		for (int i = 0; i < read; i += _channels)
		{
			float inL = buffer[offset + i];
			float inR = (_channels >= 2 && i + 1 < read) ? buffer[offset + i + 1] : inL;
			_hpStateL += _hpAlpha * (inL - _hpStateL);
			_lpStateL += _lpAlpha * (_hpStateL - _lpStateL);
			float highL = _hpStateL - _lpStateL;
			float satL = (float)Math.Tanh(highL * 3.0);
			buffer[offset + i] = inL + satL * _amount * 0.4f;
			if (_channels >= 2 && i + 1 < read)
			{
				_hpStateR += _hpAlpha * (inR - _hpStateR);
				_lpStateR += _lpAlpha * (_hpStateR - _lpStateR);
				float highR = _hpStateR - _lpStateR;
				float satR = (float)Math.Tanh(highR * 3.0);
				buffer[offset + i + 1] = inR + satR * _amount * 0.4f;
			}
		}
		return read;
	}
}
