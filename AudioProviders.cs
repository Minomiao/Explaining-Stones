using System;
using NAudio.Wave;

namespace ExplainingStones;

/// <summary>把文件音频做成单曲循环，或播放到结尾时通知一次以便切换下一首。</summary>
internal sealed class LoopingSampleProvider : ISampleProvider
{
    private readonly AudioFileReader _reader;
    private bool _finished;

    public LoopingSampleProvider(AudioFileReader reader, bool loop)
    {
        _reader = reader;
        Loop = loop;
    }

    public WaveFormat WaveFormat => _reader.WaveFormat;

    /// <summary>是否单曲循环。</summary>
    public bool Loop { get; set; }

    /// <summary>播放到结尾且不循环时触发一次（在音频线程上）。</summary>
    public event Action? Finished;

    public int Read(float[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = _reader.Read(buffer, offset + total, count - total);
            if (read > 0)
            {
                total += read;
                continue;
            }

            if (_reader.TotalTime <= TimeSpan.Zero)
            {
                break;
            }

            if (Loop)
            {
                _reader.Position = 0;   // 播放到头后回到开头
                _finished = false;
                continue;
            }

            if (!_finished)
            {
                _finished = true;
                Finished?.Invoke();
            }

            break;
        }

        return total;
    }
}

/// <summary>循环读取一段预先合成的噪声样本，可随时开关。</summary>
internal sealed class NoiseSampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public NoiseSampleProvider(int sampleRate, int channels, float[] samples)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _samples = samples;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>是否启用该噪声，关闭时输出静音。</summary>
    public bool Enabled { get; set; } = true;

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        int frames = count / channels;
        for (int f = 0; f < frames; f++)
        {
            float value = 0f;
            if (Enabled)
            {
                value = _samples[_position];
                if (++_position >= _samples.Length)
                {
                    _position = 0;
                }
            }

            for (int c = 0; c < channels; c++)
            {
                buffer[offset + f * channels + c] = value;
            }
        }

        return frames * channels;
    }
}

/// <summary>
/// 按声源的三维位置做左右等功率声像、双扬声器先后延迟（ITD）与距离衰减。
/// </summary>
internal sealed class SpatialSampleProvider : ISampleProvider
{
    private const int MaxDelayMs = 2;
    private const int MaxDelaySamples = 2;   // 双扬声器最大到达时间差约 2ms

    private readonly ISampleProvider _source;
    private readonly float[] _lineL;
    private readonly float[] _lineR;
    private int _linePos;
    private float _gainL = 1f;
    private float _gainR = 1f;
    private float _distance = 1f;

    public SpatialSampleProvider(ISampleProvider source)
    {
        _source = source;
        int length = Math.Max(MaxDelaySamples, source.WaveFormat.SampleRate * MaxDelayMs / 1000);
        _lineL = new float[length];
        _lineR = new float[length];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>是否启用立体声处理；关闭后左右声道相同。</summary>
    public bool Stereo { get; set; } = true;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int frames = read / 2;
        if (frames == 0)
        {
            return read;
        }

        if (!Stereo)
        {
            // 单声道播放：先合并左右再原样送到两个声道，声像与延迟不再参与
            for (int f = 0; f < frames; f++)
            {
                int i = offset + f * 2;
                float mono = (buffer[i] + buffer[i + 1]) * 0.5f;
                buffer[i] = mono;
                buffer[i + 1] = mono;
            }

            return read;
        }

        SpatialState.Snapshot(out double x, out _, out double z);

        // 等功率声像：x=-1 全左，x=0 居中，x=1 全右
        double t = (x + 1) / 2.0;
        float targetL = (float)Math.Sqrt(1 - t);
        float targetR = (float)Math.Sqrt(t);
        float targetDistance = (float)(1.0 / (1.0 + 2.0 * z));

        // 先响的一侧不延迟，较远的一侧按 ITD 延迟
        int lineLength = _lineL.Length;
        int delayL = x > 0 ? Math.Min(lineLength - 1, (int)Math.Round(x * lineLength)) : 0;
        int delayR = x < 0 ? Math.Min(lineLength - 1, (int)Math.Round(-x * lineLength)) : 0;

        float startL = _gainL;
        float startR = _gainR;
        float startDistance = _distance;

        for (int f = 0; f < frames; f++)
        {
            int i = offset + f * 2;
            float inL = buffer[i];
            float inR = buffer[i + 1];

            _lineL[_linePos] = inL;
            _lineR[_linePos] = inR;
            int idxL = _linePos - delayL;
            if (idxL < 0)
            {
                idxL += lineLength;
            }

            int idxR = _linePos - delayR;
            if (idxR < 0)
            {
                idxR += lineLength;
            }

            if (++_linePos >= lineLength)
            {
                _linePos = 0;
            }

            // 逐样本插值，避免拖动位置时出现音量台阶噪声
            float k = (float)f / frames;
            float gainL = startL + (targetL - startL) * k;
            float gainR = startR + (targetR - startR) * k;
            float distance = startDistance + (targetDistance - startDistance) * k;

            buffer[i] = _lineL[idxL] * gainL * distance;
            buffer[i + 1] = _lineR[idxR] * gainR * distance;
        }

        _gainL = targetL;
        _gainR = targetR;
        _distance = targetDistance;
        return read;
    }
}

/// <summary>合成老式播放器的两种噪声（单声道浮点），首尾均可无缝循环。</summary>
internal static class NoiseGenerator
{
    private const float Seconds = 4f;

    /// <summary>老式收音机声：低通白噪声的嘶嘶声。</summary>
    public static float[] CreateHiss(int sampleRate)
    {
        int loopCount = (int)(sampleRate * Seconds);
        int fade = sampleRate / 100;              // 10ms 交叉淡化，保证循环处无接缝
        var buffer = new float[loopCount + fade];

        var random = new Random(1);
        float hissState = 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            // 嘶嘶声：低通白噪声，削弱高频毛刺；再叠一点全频白噪音，听起来更"脏"
            float white = (float)(random.NextDouble() * 2.0 - 1.0);
            hissState += (white - hissState) * 0.35f;
            buffer[i] = hissState * 0.045f + white * 0.012f;
        }

        return Loop(buffer, loopCount, fade);
    }

    /// <summary>爆豆声：随机触发、快速衰减的噪声脉冲。</summary>
    public static float[] CreateCrackle(int sampleRate)
    {
        int loopCount = (int)(sampleRate * Seconds);
        int fade = sampleRate / 100;
        var buffer = new float[loopCount + fade];

        var random = new Random(2);
        int remaining = 0;
        float gain = 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            float value = 0f;
            if (remaining == 0 && random.NextDouble() < 0.0008)
            {
                remaining = random.Next(20, 80);
                gain = (float)(0.10 + random.NextDouble() * 0.45);
            }
            if (remaining > 0)
            {
                value = (float)(random.NextDouble() * 2.0 - 1.0) * gain;
                gain *= 0.9f;
                remaining--;
            }

            buffer[i] = value;
        }

        return Loop(buffer, loopCount, fade);
    }

    /// <summary>用末尾多出的样本与开头交叉淡化，循环播放时听不出接缝。</summary>
    private static float[] Loop(float[] buffer, int loopCount, int fade)
    {
        var samples = new float[loopCount];
        for (int i = 0; i < loopCount; i++)
        {
            if (i < fade)
            {
                float k = (float)i / fade;
                samples[i] = buffer[i] * k + buffer[loopCount + i] * (1 - k);
            }
            else
            {
                samples[i] = buffer[i];
            }
        }

        return samples;
    }
}

/// <summary>
/// 模拟老式播放器"不稳"的转动：用可变延迟线制造音高抖动（wow &amp; flutter），
/// 再叠加轻微的音量起伏。
/// </summary>
internal sealed class FlutterSampleProvider : ISampleProvider
{
    private const double WowHz = 0.37;        // 慢速晃动
    private const double FlutterHz = 5.6;     // 快速抖动
    private const double WowMs = 4.0;
    private const double FlutterMs = 1.2;
    private const double BaseDelayMs = 18.0;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _line;
    private readonly int _lineLength;
    private int _writePos;
    private double _time;   // 已处理的时间（秒）

    public FlutterSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _lineLength = Math.Max(1, (int)(source.WaveFormat.SampleRate * 0.1));
        _line = new float[_lineLength * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int frames = read / _channels;
        if (frames == 0)
        {
            return read;
        }

        double duration = (double)frames / WaveFormat.SampleRate;
        double baseDelay = WaveFormat.SampleRate * BaseDelayMs / 1000.0;

        // 在一个缓冲区内把延迟和音量线性过渡，跨缓冲区保持连续
        double startDelay = baseDelay + Modulation(_time);
        double endDelay = baseDelay + Modulation(_time + duration);
        float startGain = Gain(_time);
        float endGain = Gain(_time + duration);
        _time += duration;

        for (int f = 0; f < frames; f++)
        {
            int index = offset + f * _channels;
            int writeIndex = _writePos * _channels;
            for (int c = 0; c < _channels; c++)
            {
                _line[writeIndex + c] = buffer[index + c];
            }

            double k = frames > 1 ? (double)f / (frames - 1) : 0;
            double delay = startDelay + (endDelay - startDelay) * k;
            float gain = startGain + (endGain - startGain) * (float)k;

            double readPos = _writePos - delay;
            while (readPos < 0)
            {
                readPos += _lineLength;
            }

            int i0 = (int)readPos;
            if (i0 >= _lineLength)
            {
                i0 -= _lineLength;
            }

            int i1 = i0 + 1 < _lineLength ? i0 + 1 : 0;
            float frac = (float)(readPos - Math.Floor(readPos));

            for (int c = 0; c < _channels; c++)
            {
                float a = _line[i0 * _channels + c];
                float b = _line[i1 * _channels + c];
                buffer[index + c] = (a + (b - a) * frac) * gain;
            }

            if (++_writePos >= _lineLength)
            {
                _writePos = 0;
            }
        }

        return read;
    }

    /// <summary>wow（慢）+ flutter（快）合成的延迟偏移量（单位：采样）。</summary>
    private double Modulation(double time)
    {
        double rate = WaveFormat.SampleRate;
        double wow = Math.Sin(2 * Math.PI * WowHz * time) * (rate * WowMs / 1000.0);
        double flutter = Math.Sin(2 * Math.PI * FlutterHz * time + 1.1) * (rate * FlutterMs / 1000.0);
        return wow + flutter;
    }

    /// <summary>轻微的音量起伏（0.94 ~ 1.0）。</summary>
    private static float Gain(double time)
        => (float)(0.97 + 0.03 * Math.Sin(2 * Math.PI * 0.23 * time));
}
