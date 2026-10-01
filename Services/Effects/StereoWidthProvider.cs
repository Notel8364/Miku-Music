using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

public sealed class StereoWidthProvider : ISampleProvider
{
	private readonly ISampleProvider _source;
	private float _width;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public StereoWidthProvider(ISampleProvider source, float width)
	{
		_source = source;
		_width = Math.Clamp(width, 0f, 2f);
	}

	public void SetWidth(float width)
	{
		_width = Math.Clamp(width, 0f, 2f);
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		if (_width == 1f) return read;
		int channels = _source.WaveFormat.Channels;
		for (int i = 0; i < read; i += channels)
		{
			if (channels >= 2 && i + 1 < read)
			{
				float L = buffer[offset + i];
				float R = buffer[offset + i + 1];
				float mid = (L + R) * 0.5f;
				float side = (L - R) * 0.5f;
				buffer[offset + i] = mid + side * _width;
				buffer[offset + i + 1] = mid - side * _width;
			}
		}
		return read;
	}
}
