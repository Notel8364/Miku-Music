using System;
using NAudio.Wave;

namespace MusicBox.Services.Effects;

public sealed class ReverbProvider : ISampleProvider
{
	private const int NumCombs = 4;
	private const int NumAllpasses = 2;

	private readonly ISampleProvider _source;
	private readonly int _channels;
	private readonly float[][] _combBuffers;
	private readonly int[] _combDelays;
	private readonly float[] _combFeedback;
	private readonly int[] _combPos;
	private readonly float[][] _allpassBuffers;
	private readonly int[] _allpassDelays;
	private readonly int[] _allpassPos;
	private readonly float _wetGain;
	private readonly float _dryGain;
	private readonly float _roomSize;
	private float _damp;
	private float _dampState;

	public WaveFormat WaveFormat => _source.WaveFormat;

	public ReverbProvider(ISampleProvider source, float roomSize, float wetLevel)
	{
		_source = source;
		_channels = source.WaveFormat.Channels;
		float sr = source.WaveFormat.SampleRate;
		float scale = sr / 44100f;
		_roomSize = Math.Clamp(roomSize, 0.1f, 0.95f);
		float wet = Math.Clamp(wetLevel, 0f, 1f);
		_dryGain = 1f - wet * 0.5f;
		_wetGain = wet * 0.6f;
		_damp = 0.5f;
		_dampState = 0f;
		int[] combDelaysMs = { 25, 29, 32, 35 };
		int[] allpassDelaysMs = { 5, 7 };
		_combBuffers = new float[NumCombs][];
		_combDelays = new int[NumCombs];
		_combFeedback = new float[NumCombs];
		_combPos = new int[NumCombs];
		_allpassBuffers = new float[NumAllpasses][];
		_allpassDelays = new int[NumAllpasses];
		_allpassPos = new int[NumAllpasses];
		for (int i = 0; i < NumCombs; i++)
		{
			_combDelays[i] = Math.Max(4, (int)(combDelaysMs[i] * scale * sr / 1000f));
			_combBuffers[i] = new float[_combDelays[i]];
			_combFeedback[i] = _roomSize;
			_combPos[i] = 0;
		}
		for (int i = 0; i < NumAllpasses; i++)
		{
			_allpassDelays[i] = Math.Max(2, (int)(allpassDelaysMs[i] * scale * sr / 1000f));
			_allpassBuffers[i] = new float[_allpassDelays[i]];
			_allpassPos[i] = 0;
		}
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int read = _source.Read(buffer, offset, count);
		for (int i = 0; i < read; i += _channels)
		{
			float inL = buffer[offset + i];
			float inR = (_channels >= 2 && i + 1 < read) ? buffer[offset + i + 1] : inL;
			float mix = (inL + inR) * 0.5f;
			float combOut = 0f;
			for (int c = 0; c < NumCombs; c++)
			{
				int pos = _combPos[c];
				float del = _combBuffers[c][pos];
				_dampState += _damp * (del - _dampState);
				float fb = _dampState * _combFeedback[c];
				_combBuffers[c][pos] = mix + fb;
				combOut += fb;
				_combPos[c] = (pos + 1) % _combDelays[c];
			}
			combOut /= NumCombs;
			for (int a = 0; a < NumAllpasses; a++)
			{
				int pos = _allpassPos[a];
				float buf = _allpassBuffers[a][pos];
				_allpassBuffers[a][pos] = combOut + buf * 0.5f;
				combOut = buf - _allpassBuffers[a][pos] * 0.5f;
				_allpassPos[a] = (pos + 1) % _allpassDelays[a];
			}
			float wet = combOut * _wetGain;
			buffer[offset + i] = inL * _dryGain + wet;
			if (_channels >= 2 && i + 1 < read)
				buffer[offset + i + 1] = inR * _dryGain + wet;
		}
		return read;
	}
}