using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

public enum FilterType { Peaking, LowShelf, HighShelf }

internal struct Biquad
{
	public float B0, B1, B2, A1, A2;
	private float _x1, _x2, _y1, _y2;

	public float Process(float input)
	{
		float output = B0 * input + B1 * _x1 + B2 * _x2 - A1 * _y1 - A2 * _y2;
		_x2 = _x1;
		_x1 = input;
		_y2 = _y1;
		_y1 = output;
		return output;
	}

	public void Reset()
	{
		_x1 = _x2 = _y1 = _y2 = 0f;
	}

	public static Biquad Create(FilterType type, float sampleRate, float freq, float q, float gainDb)
	{
		double w0 = 2.0 * Math.PI * freq / sampleRate;
		double alpha = Math.Sin(w0) / (2.0 * q);
		double cosW = Math.Cos(w0);
		double A = Math.Pow(10.0, gainDb / 20.0);
		double b0, b1, b2, a0, a1, a2;
		switch (type)
		{
			case FilterType.Peaking:
				b0 = 1.0 + alpha * A;
				b1 = -2.0 * cosW;
				b2 = 1.0 - alpha * A;
				a0 = 1.0 + alpha / A;
				a1 = -2.0 * cosW;
				a2 = 1.0 - alpha / A;
				break;
			case FilterType.LowShelf:
				{
					double sqA = 2.0 * Math.Sqrt(A) * alpha;
					b0 = A * ((A + 1.0) - (A - 1.0) * cosW + sqA);
					b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosW);
					b2 = A * ((A + 1.0) - (A - 1.0) * cosW - sqA);
					a0 = (A + 1.0) + (A - 1.0) * cosW + sqA;
					a1 = -2.0 * ((A - 1.0) + (A + 1.0) * cosW);
					a2 = (A + 1.0) + (A - 1.0) * cosW - sqA;
				}
				break;
			default:
				{
					double sqA = 2.0 * Math.Sqrt(A) * alpha;
					b0 = A * ((A + 1.0) + (A - 1.0) * cosW + sqA);
					b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosW);
					b2 = A * ((A + 1.0) + (A - 1.0) * cosW - sqA);
					a0 = (A + 1.0) - (A - 1.0) * cosW + sqA;
					a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cosW);
					a2 = (A + 1.0) - (A - 1.0) * cosW - sqA;
				}
				break;
		}
		double inv = 1.0 / a0;
		return new Biquad
		{
			B0 = (float)(b0 * inv),
			B1 = (float)(b1 * inv),
			B2 = (float)(b2 * inv),
			A1 = (float)(a1 * inv),
			A2 = (float)(a2 * inv)
		};
	}
}

public sealed class EqualizerProvider : ISampleProvider
{
	private const int BandCount = 10;

	public static readonly float[] CenterFrequencies = { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

	private readonly ISampleProvider _source;
	private readonly int _channels;
	private readonly Biquad[] _filtersL;
	private readonly Biquad[] _filtersR;
	private readonly float[] _gains;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public EqualizerProvider(ISampleProvider source, float[] gains)
	{
		_source = source;
		_channels = source.WaveFormat.Channels;
		_filtersL = new Biquad[BandCount];
		_filtersR = new Biquad[BandCount];
		_gains = new float[BandCount];
		float sr = source.WaveFormat.SampleRate;
		for (int i = 0; i < BandCount; i++)
		{
			_gains[i] = i < gains.Length ? gains[i] : 0f;
			_filtersL[i] = Biquad.Create(FilterType.Peaking, sr, CenterFrequencies[i], 1.414f, _gains[i]);
			_filtersR[i] = Biquad.Create(FilterType.Peaking, sr, CenterFrequencies[i], 1.414f, _gains[i]);
		}
	}

	public void SetGain(int band, float db)
	{
		if (band < 0 || band >= BandCount) return;
		_gains[band] = db;
		float sr = _source.WaveFormat.SampleRate;
		_filtersL[band] = Biquad.Create(FilterType.Peaking, sr, CenterFrequencies[band], 1.414f, db);
		_filtersR[band] = Biquad.Create(FilterType.Peaking, sr, CenterFrequencies[band], 1.414f, db);
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		for (int i = 0; i < read; i += _channels)
		{
			for (int b = 0; b < BandCount; b++)
			{
				buffer[offset + i] = _filtersL[b].Process(buffer[offset + i]);
				if (_channels >= 2 && i + 1 < read)
					buffer[offset + i + 1] = _filtersR[b].Process(buffer[offset + i + 1]);
			}
		}
		return read;
	}
}
