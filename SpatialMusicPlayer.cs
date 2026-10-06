using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ExplainingStones;

/// <summary>
/// 基于 NAudio 的播放器：把音乐与老式底噪混合后，按声源在三维空间中的位置
/// 做左右等功率声像、双扬声器先后延迟（ITD）与距离衰减，再输出到默认扬声器。
/// </summary>
internal sealed class SpatialMusicPlayer : IDisposable
{
    private IWavePlayer? _output;
    private AudioFileReader? _reader;
    private NoiseSampleProvider? _noise;

    /// <summary>是否已经打开了某个媒体文件。</summary>
    public bool IsOpened => _reader is not null;

    /// <summary>播放指定文件，成功返回 true。</summary>
    public bool Play(string path)
    {
        Close();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var reader = new AudioFileReader(path);
            ISampleProvider music = new LoopingSampleProvider(reader);
            if (reader.WaveFormat.Channels == 1)
            {
                music = new MonoToStereoSampleProvider(music);
            }

            // 老式播放器：转动不稳导致走音与音量起伏
            music = new FlutterSampleProvider(music);

            var noise = new NoiseSampleProvider(reader.WaveFormat.SampleRate, 2);
            var mix = new MixingSampleProvider(new[] { music, (ISampleProvider)noise })
            {
                ReadFully = true
            };
            var spatial = new SpatialSampleProvider(mix);

            var output = new WaveOutEvent { DesiredLatency = 150 };
            output.Init(spatial);
            output.Play();

            _reader = reader;
            _noise = noise;
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

    /// <summary>是否启用老式底噪。</summary>
    public bool NoiseEnabled
    {
        get => _noise?.Enabled ?? false;
        set
        {
            if (_noise is not null)
            {
                _noise.Enabled = value;
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

        _noise = null;
    }

    public void Dispose() => Close();
}
