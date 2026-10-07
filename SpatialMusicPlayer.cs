using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExplainingStones;

/// <summary>
/// 基于 NAudio 的播放器：音乐与老式播放器噪声按石头分为多条支路，各自按该石头的
/// 三维位置做左右等功率声像、双耳时间差（ITD）与距离衰减，再汇入**单个**输出设备。
/// 单设备可避免多路输出间的启动偏差造成的回声 / 梳状滤波；单声道设备会自动降混。
/// </summary>
internal sealed class SpatialMusicPlayer : IDisposable
{
    /// <summary>固定的支路数（最多三颗石头），支路常驻、不随石头增删改变结构。</summary>
    public const int Slots = 3;

    private readonly SpatialState[] _states;
    private readonly SpatialSampleProvider?[] _spatial = new SpatialSampleProvider?[Slots];
    private readonly NoiseSampleProvider?[] _hiss = new NoiseSampleProvider?[Slots];
    private readonly NoiseSampleProvider?[] _crackle = new NoiseSampleProvider?[Slots];
    private readonly float[] _levels = new float[Slots];

    private IWavePlayer? _output;
    private AudioFileReader? _reader;
    private LoopingSampleProvider? _music;

    public SpatialMusicPlayer(SpatialState[] states)
    {
        _states = states;
    }

    /// <summary>当前曲目是否已经打开。</summary>
    public bool IsOpened => _reader is not null;

    /// <summary>当前曲目播放到结尾（非单曲循环时触发，可能在任意线程）。</summary>
    public event Action? TrackEnded;

    /// <summary>默认输出设备是否支持立体声；无法判断时按支持处理。</summary>
    public static bool OutputSupportsStereo()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioClient.MixFormat.Channels >= 2;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>设置某条支路的音量（0 静音 / 不存在，其余按发声石头数归一）。</summary>
    public void SetStoneLevel(int slot, float level)
    {
        if (slot < 0 || slot >= Slots)
        {
            return;
        }

        _levels[slot] = level;
        if (_spatial[slot] is not null)
        {
            _spatial[slot]!.TargetLevel = level;
        }
    }

    /// <summary>播放指定文件，成功返回 true。</summary>
    public bool Play(string path, bool loop)
    {
        Close();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var reader = new AudioFileReader(path);
            var music = new LoopingSampleProvider(reader, loop);
            ISampleProvider chain = music;
            if (reader.WaveFormat.Channels == 1)
            {
                chain = new MonoToStereoSampleProvider(chain);
            }

            // 老式播放器：转动不稳导致走音与音量起伏；只做一次，三颗石头共享
            chain = new FlutterSampleProvider(chain);
            var fanOut = new FanOutSampleProvider(chain, Slots);

            int rate = reader.WaveFormat.SampleRate;
            var branches = new ISampleProvider[Slots];
            for (int slot = 0; slot < Slots; slot++)
            {
                // 三支路噪声用不同种子，让三颗石头的底噪互不相关
                var hiss = new NoiseSampleProvider(rate, 2, NoiseGenerator.CreateHiss(rate, 1 + slot * 2));
                var crackle = new NoiseSampleProvider(rate, 2, NoiseGenerator.CreateCrackle(rate, 2 + slot * 2));
                var branch = new MixingSampleProvider(new ISampleProvider[] { fanOut, hiss, crackle })
                {
                    ReadFully = true
                };

                var spatial = new SpatialSampleProvider(branch, _states[slot], _levels[slot]);
                branches[slot] = spatial;
                _spatial[slot] = spatial;
                _hiss[slot] = hiss;
                _crackle[slot] = crackle;
            }

            var mix = new MixingSampleProvider(branches) { ReadFully = true };

            var output = new WaveOutEvent { DesiredLatency = 150 };
            try
            {
                output.Init(OutputSupportsStereo() ? mix : new StereoToMonoSampleProvider(mix));
            }
            catch
            {
                // 设备实际不接受立体声时退回单声道，保证单扬声器也能播放
                output.Init(new StereoToMonoSampleProvider(mix));
            }

            output.Play();
            music.Finished += () => TrackEnded?.Invoke();

            _reader = reader;
            _music = music;
            _output = output;
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public void Pause() => _output?.Pause();

    public void Resume() => _output?.Play();

    /// <summary>老式收音机声（嘶嘶底噪）开关，对所有石头生效。</summary>
    public bool HissEnabled
    {
        get => _hiss[0]?.Enabled ?? false;
        set
        {
            foreach (NoiseSampleProvider? hiss in _hiss)
            {
                if (hiss is not null)
                {
                    hiss.Enabled = value;
                }
            }
        }
    }

    /// <summary>爆豆声开关，对所有石头生效。</summary>
    public bool CrackleEnabled
    {
        get => _crackle[0]?.Enabled ?? false;
        set
        {
            foreach (NoiseSampleProvider? crackle in _crackle)
            {
                if (crackle is not null)
                {
                    crackle.Enabled = value;
                }
            }
        }
    }

    /// <summary>当前曲目的单曲循环开关。</summary>
    public bool Loop
    {
        get => _music?.Loop ?? false;
        set
        {
            if (_music is not null)
            {
                _music.Loop = value;
            }
        }
    }

    /// <summary>立体声开关，关闭后左右声道相同。</summary>
    public bool StereoEnabled
    {
        get => _spatial[0]?.Stereo ?? true;
        set
        {
            foreach (SpatialSampleProvider? spatial in _spatial)
            {
                if (spatial is not null)
                {
                    spatial.Stereo = value;
                }
            }
        }
    }

    public void Close()
    {
        if (_output is not null)
        {
            _output.Stop();
            _output.Dispose();
            _output = null;
        }

        if (_reader is not null)
        {
            _reader.Dispose();
            _reader = null;
        }

        _music = null;
        for (int slot = 0; slot < Slots; slot++)
        {
            _spatial[slot] = null;
            _hiss[slot] = null;
            _crackle[slot] = null;
        }
    }

    public void Dispose() => Close();
}
