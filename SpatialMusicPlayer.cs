using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExplainingStones;

/// <summary>
/// 基于 NAudio 的播放器：把音乐与老式播放器噪声混合后，按声源在三维空间中的位置
/// 做左右等功率声像、双扬声器先后延迟（ITD）与距离衰减，再输出到默认扬声器。
/// 输出设备只有单声道时会自动降为单声道，保证单扬声器也能正常播放。
/// </summary>
internal sealed class SpatialMusicPlayer : IDisposable
{
    private IWavePlayer? _output;
    private AudioFileReader? _reader;
    private LoopingSampleProvider? _music;
    private NoiseSampleProvider? _hiss;
    private NoiseSampleProvider? _crackle;
    private SpatialSampleProvider? _spatial;

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

            // 老式播放器：转动不稳导致走音与音量起伏
            chain = new FlutterSampleProvider(chain);

            int rate = reader.WaveFormat.SampleRate;
            var hiss = new NoiseSampleProvider(rate, 2, NoiseGenerator.CreateHiss(rate));
            var crackle = new NoiseSampleProvider(rate, 2, NoiseGenerator.CreateCrackle(rate));
            var mix = new MixingSampleProvider(new ISampleProvider[] { chain, hiss, crackle })
            {
                ReadFully = true
            };
            var spatial = new SpatialSampleProvider(mix);

            var output = new WaveOutEvent { DesiredLatency = 150 };
            try
            {
                output.Init(OutputSupportsStereo() ? spatial : new StereoToMonoSampleProvider(spatial));
            }
            catch
            {
                // 设备实际不接受立体声时退回单声道，保证单扬声器也能播放
                output.Init(new StereoToMonoSampleProvider(spatial));
            }

            output.Play();
            music.Finished += () => TrackEnded?.Invoke();

            _reader = reader;
            _music = music;
            _hiss = hiss;
            _crackle = crackle;
            _spatial = spatial;
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

    /// <summary>老式收音机声（嘶嘶底噪）开关。</summary>
    public bool HissEnabled
    {
        get => _hiss?.Enabled ?? false;
        set
        {
            if (_hiss is not null)
            {
                _hiss.Enabled = value;
            }
        }
    }

    /// <summary>爆豆声开关。</summary>
    public bool CrackleEnabled
    {
        get => _crackle?.Enabled ?? false;
        set
        {
            if (_crackle is not null)
            {
                _crackle.Enabled = value;
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
        get => _spatial?.Stereo ?? true;
        set
        {
            if (_spatial is not null)
            {
                _spatial.Stereo = value;
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
        _hiss = null;
        _crackle = null;
        _spatial = null;
    }

    public void Dispose() => Close();
}
