using System;
using System.Collections.Concurrent;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicBox.Services;

/// <summary>
/// 界面音效引擎：全合成（正弦扫频 + 指数衰减包络 + 带通整音），不引第三方库、不用音频文件。
///
/// 架构（以「简单可靠」为最高优先）：
/// - 独立小输出（WaveOutEvent，80ms 低延迟缓冲），与 MusicService 的 WasapiOut
///   完全解耦 —— 主输出换设备/重建时音效引擎不受牵连；音效设备打开失败也绝不
///   反过来影响主音频（异常全部静默吞掉）。
/// - 参数化：startFreq/endFreq（Hz 指数扫频）、durationMs、volume、带通中心/Q。
///   线程安全：触发方只往 ConcurrentQueue 塞一个描述结构，不碰输出对象。
/// - 无请求时 Read 填零静默：一次 26ms 缓冲的零填充是几 KB 的 memset，
///   开销可以忽略；不采用「用完即停输出」——stop/start 抖动反而容易出爆音。
/// - 输出坏了（设备拔掉等）：下次触发时捕获异常、销毁重建，绝不让音效卡住主流程。
/// </summary>
public static class UiSfx
{
	// ── 对外的两个触发点 ──

	/// <summary>定位球惯性滑行被窗口边夹停：一声轻「嗒」（900→320Hz、60ms、0.25）。</summary>
	public static void PlayBallClamp()
		=> Play(startFreq: 900, endFreq: 320, durationMs: 60, volume: 0.25f);

	/// <summary>单击定位球定位成功：更轻的一声确认（1200→800Hz、40ms、0.18）。</summary>
	public static void PlayLocateConfirm()
		=> Play(startFreq: 1200, endFreq: 800, durationMs: 40, volume: 0.18f);

	// ── 输出与队列 ──

	private static readonly object InitLock = new();
	private static WaveOutEvent? _output;
	private static SfxProvider? _provider;

	/// <summary>扫描频率的安全范围：合成器内部先夹进这个带，避免极端参数
	/// 拉出次声/超声波成分（听不见却占算力，还可能推动低音单元）。</summary>
	private const double MinFreqHz = 80;
	private const double MaxFreqHz = 4000;

	/// <summary>同屏连点时最多同时叠 4 个音效，超过就丢新的：
	/// 音效是提示，不是音墙。</summary>
	private const int MaxPendingClips = 4;

	private static void Play(double startFreq, double endFreq, double durationMs, float volume)
	{
		// 音效开关已按产品决策恒开（SettingsManager.Load 里强制覆盖、设置页
		// 不再提供入口），这里不再判断开关，进来就播。
		startFreq = Math.Clamp(startFreq, MinFreqHz, MaxFreqHz);
		endFreq = Math.Clamp(endFreq, MinFreqHz, MaxFreqHz);
		durationMs = Math.Clamp(durationMs, 10, 500);
		volume = Math.Clamp(volume, 0f, 0.3f);
		try
		{
			SfxProvider? provider;
			lock (InitLock)
			{
				provider = EnsureOutput();
			}
			if (provider == null || provider.PendingCount >= MaxPendingClips)
			{
				return;
			}
			provider.Add(startFreq, endFreq, durationMs, volume);
		}
		catch
		{
			// 音效失败必须静默：销毁输出等下一次触发重建，绝不上抛。
			lock (InitLock)
			{
				TearDownOutput();
			}
		}
	}

	/// <summary>惰性建输出。放在调用者线程（首次触发音效的那一拍）建一次，
	/// 之后一直复用；WaveOutEvent 内部自己起播放线程，构造即就绪。</summary>
	private static SfxProvider? EnsureOutput()
	{
		if (_provider != null && _output != null)
		{
			return _provider;
		}
		var provider = new SfxProvider();
		var output = new WaveOutEvent
		{
			// 80ms 总延迟、3 段缓冲：点下去到出声的延迟体感在「跟手」一档，
			// 又不至于把缓冲压得太碎导致杂音。
			DesiredLatency = 80,
			NumberOfBuffers = 3
		};
		// ISampleProvider 出的是 IEEE float，包一层转 16-bit PCM：
		// winmm 对 16-bit PCM 的兼容性是万无一失的那一档。
		output.Init(new SampleToWaveProvider(provider));
		output.Play();
		_output = output;
		_provider = provider;
		return provider;
	}

	private static void TearDownOutput()
	{
		try { _output?.Dispose(); } catch { }
		_output = null;
		_provider = null;
	}

	/// <summary>音效数据源：48kHz 单声道 IEEE float。多段音效排队串行播放，
	/// 队列空时填零静默（Read 里只做 memset 级别的开销）。</summary>
	private sealed class SfxProvider : ISampleProvider
	{
		private const int SampleRate = 48000;
		private readonly ConcurrentQueue<SfxClip> _clips = new();
		private SfxClip? _current;

		public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

		public int PendingCount => _clips.Count + (_current != null ? 1 : 0);

		public void Add(double startFreq, double endFreq, double durationMs, float volume)
		{
			_clips.Enqueue(new SfxClip(startFreq, endFreq, durationMs, volume, SampleRate));
		}

		public int Read(float[] buffer, int offset, int count)
		{
			int written = 0;
			while (written < count)
			{
				SfxClip? clip = _current;
				if (clip == null)
				{
					if (!_clips.TryDequeue(out _current))
					{
						break;
					}
					clip = _current;
				}
				int generated = clip.ReadInto(buffer, offset + written, count - written);
				written += generated;
				if (clip.Finished)
				{
					_current = null;
				}
			}
			// 队列空了：剩余区间填零静默（WPF 线程塞新片段的间隙就是这一段）。
			for (int i = offset + written; i < offset + count; i++)
			{
				buffer[i] = 0f;
			}
			return count;
		}
	}

	/// <summary>一段音效：正弦按指数律从 startFreq 扫到 endFreq，
	/// 音量走指数衰减包络（开头 2ms 线性起音防爆音），再过一个带通把「嗒」收干净。</summary>
	private sealed class SfxClip
	{
		private readonly double _startFreq;
		private readonly double _freqRatioPerSample; // 每个样本的频率乘子（几何扫频）
		private readonly double _decayPerSample;     // 包络指数衰减系数
		private readonly float _volume;
		private readonly int _totalSamples;
		private readonly BiquadBandPass _bandPass;
		private int _position;
		private double _phase; // 正弦相位（弧度），逐样本累加避免每次都算 sin(freq * t) 的相位跳变

		public bool Finished => _position >= _totalSamples;

		public SfxClip(double startFreq, double endFreq, double durationMs, float volume, int sampleRate)
		{
			_startFreq = startFreq;
			_volume = volume;
			int durationSamples = Math.Max(1, (int)(durationMs / 1000.0 * sampleRate));
			_totalSamples = durationSamples;
			// 指数扫频：freq(t) = start * (end/start)^(t/dur)。
			// 按样本步进存比率，比每样本算 Pow 便宜得多。
			double steps = Math.Max(1.0, durationSamples - 1);
			_freqRatioPerSample = Math.Pow(endFreq / startFreq, 1.0 / steps);
			// 包络：衰减到段尾约 -40dB（exp(-5)≈0.007），听感是「嗒」而不是「哔」。
			double durationSec = durationSamples / (double)sampleRate;
			_decayPerSample = 5.0 / Math.Max(0.005, durationSec * sampleRate);
			// 带通中心取扫频几何均值，Q=1.5：保留主体频段、削掉「嗒」的毛边。
			double center = Math.Sqrt(startFreq * endFreq);
			_bandPass = new BiquadBandPass(center, 1.5, sampleRate);
		}

		/// <summary>生成下一批样本。返回本次实际写入数（最多 remaining）。</summary>
		public int ReadInto(float[] buffer, int offset, int remaining)
		{
			int attackSamples = (int)(0.002 * 48000); // 2ms 起音，防咔哒
			int n = Math.Min(remaining, _totalSamples - _position);
			double twoPiOverSr = 2 * Math.PI / 48000.0;
			double freq = _startFreq * Math.Pow(_freqRatioPerSample, _position);
			for (int i = 0; i < n; i++)
			{
				int pos = _position + i;
				// 包络 = 指数衰减 × 2ms 线性起音。衰减按样本累乘，不做每样本 Exp。
				double env = _volume * Math.Exp(-_decayPerSample * pos);
				if (pos < attackSamples)
				{
					env *= pos / (double)attackSamples;
				}
				_phase += twoPiOverSr * freq;
				if (_phase > 2 * Math.PI)
				{
					_phase -= 2 * Math.PI;
				}
				// 带通滤波逐样本推进（状态在 _bandPass 里），扫频时滤芯不动、只滤固定带。
				float s = _bandPass.Process((float)(Math.Sin(_phase) * env));
				buffer[offset + i] = s;
				freq *= _freqRatioPerSample;
			}
			_position += n;
			return n;
		}
	}

	/// <summary>最小 RBJ 双二阶带通（constant skirt gain、peak gain = Q）。
	/// 每段音效算一次系数，逐样本两次乘加，成本忽略不计。</summary>
	private sealed class BiquadBandPass
	{
		private readonly double _b0, _b1, _b2, _a1, _a2;
		private double _x1, _x2, _y1, _y2;

		public BiquadBandPass(double centerFreq, double q, int sampleRate)
		{
			double w0 = 2 * Math.PI * centerFreq / sampleRate;
			double alpha = Math.Sin(w0) / (2 * q);
			double cosW0 = Math.Cos(w0);
			double a0 = 1 + alpha;
			_b0 = alpha / a0;
			_b1 = 0;
			_b2 = -alpha / a0;
			_a1 = -2 * cosW0 / a0;
			_a2 = (1 - alpha) / a0;
		}

		public float Process(float x)
		{
			double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
			_x2 = _x1;
			_x1 = x;
			_y2 = _y1;
			_y1 = y;
			// 防数值漂移把音量顶过 1：夹一道，宁可轻也不爆。
			return (float)Math.Clamp(y, -1.0, 1.0);
		}
	}
}
