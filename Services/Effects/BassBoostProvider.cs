using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

public sealed class BassBoostProvider : ISampleProvider
{
	private readonly ISampleProvider _source;
	private readonly int _channels;
	private Biquad _shelfL;
	private Biquad _shelfR;
	private float _gainDb;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public BassBoostProvider(ISampleProvider source, float gainDb)
	{
		_source = source;
		_channels = source.WaveFormat.Channels;
		_gainDb = Math.Clamp(gainDb, 0f, 20f);
		float sr = source.WaveFormat.SampleRate;
		_shelfL = Biquad.Create(FilterType.LowShelf, sr, 120f, 0.707f, _gainDb);
		_shelfR = Biquad.Create(FilterType.LowShelf, sr, 120f, 0.707f, _gainDb);
	}

	public void SetGain(float db)
	{
		_gainDb = Math.Clamp(db, 0f, 20f);
		float sr = _source.WaveFormat.SampleRate;
		_shelfL = Biquad.Create(FilterType.LowShelf, sr, 120f, 0.707f, _gainDb);
		_shelfR = Biquad.Create(FilterType.LowShelf, sr, 120f, 0.707f, _gainDb);
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		for (int i = 0; i < read; i += _channels)
		{
			buffer[offset + i] = _shelfL.Process(buffer[offset + i]);
			if (_channels >= 2 && i + 1 < read)
				buffer[offset + i + 1] = _shelfR.Process(buffer[offset + i + 1]);
		}
		return read;
	}
}
