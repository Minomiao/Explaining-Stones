using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ExplainingStones;

/// <summary>
/// 应用宿主：承载托盘图标与菜单、播放列表与自动切歌、音频引擎，以及桌面上最多三颗石头。
/// </summary>
internal sealed class StoneHost : ApplicationContext
{
    /// <summary>桌面上最多的石头数量。</summary>
    public const int MaxStones = 3;

    /// <summary>各槽位新石头的默认位置（与其它石头错开）。</summary>
    private static readonly (double X, double Y)[] DefaultPosition =
    {
        (0.7, -0.7),
        (0.0, 0.0),
        (-0.7, 0.7),
    };

    private static readonly object Gate = new();
    private static Stone[] _snapshot = Array.Empty<Stone>();

    /// <summary>当前石头集合的快照（按槽位顺序）。</summary>
    public static IReadOnlyList<Stone> Stones
    {
        get
        {
            lock (Gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>石头集合变化后触发（可能在任意线程）。</summary>
    public static event Action? StonesChanged;

    private readonly SpatialState[] _slots = new SpatialState[MaxStones];
    private readonly List<Stone> _stones = new();
    private readonly Dictionary<Stone, PetForm> _forms = new();
    private readonly SpatialMusicPlayer _player;
    private readonly Form _marshal;
    private readonly NotifyIcon _tray;
    private readonly Icon _appIcon;

    private bool _wantPlay;
    private bool _paused;
    private string? _playingPath;

    public StoneHost()
    {
        double defaultDepth = AppSettings.SpatialDepth;
        IReadOnlyList<StoneSnapshot> saved = AppSettings.Stones;

        for (int slot = 0; slot < MaxStones; slot++)
        {
            if (slot < saved.Count)
            {
                _slots[slot] = new SpatialState(saved[slot].X, saved[slot].Y, saved[slot].Z);
            }
            else
            {
                _slots[slot] = new SpatialState(DefaultPosition[slot].X, DefaultPosition[slot].Y, defaultDepth);
            }
        }

        // 首次运行没有落盘文件时，默认只放一颗石头
        int count = saved.Count > 0 ? Math.Min(saved.Count, MaxStones) : 1;
        for (int slot = 0; slot < count; slot++)
        {
            var stone = new Stone(slot, _slots[slot])
            {
                Muted = saved.Count > slot && saved[slot].Muted
            };
            _stones.Add(stone);
            stone.State.Changed += OnStoneSpatialChanged;

            var form = new PetForm(stone, this);
            _forms[stone] = form;
            form.ApplySpatialPosition();   // 先定位再显示，避免默认位置闪一下
            form.Show();
        }

        _marshal = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000),
            Size = new Size(1, 1),
        };
        _ = _marshal.Handle;   // 创建句柄以供 BeginInvoke 使用

        _player = new SpatialMusicPlayer(_slots);
        _player.TrackEnded += OnTrackEnded;

        _appIcon = SvgIcons.CreateAppIcon(PetForm.LogicalToPixels(16));
        _tray = BuildTrayIcon();

        AppSettings.PlaylistChanged += OnPlaylistChanged;
        AppSettings.TrackRequested += OnTrackRequested;
        AppSettings.AudioEffectsChanged += OnAudioEffectsChanged;

        PublishStones();
        RefreshLevels();
        ApplyAudioEffects();
    }

    // ===== 石头集合 =====

    /// <summary>添加一颗石头（最多三颗）。</summary>
    public void AddStone() => AddStoneInternal();

    /// <summary>移除最后一颗石头（最少保留一颗）。</summary>
    public void RemoveStone() => RemoveStoneInternal();

    /// <summary>开关某颗石头的声音（左键点击桌宠）。</summary>
    public void ToggleStoneSound(Stone stone)
    {
        if (!_stones.Contains(stone))
        {
            return;
        }

        stone.Muted = !stone.Muted;
        PersistStones();
        RefreshLevels();
    }

    private void AddStoneInternal()
    {
        if (_stones.Count >= MaxStones)
        {
            return;
        }

        int slot = _stones.Count;
        SpatialState state = _slots[slot];
        state.Set(DefaultPosition[slot].X, DefaultPosition[slot].Y, AppSettings.SpatialDepth, SpatialOrigin.Panel);

        var stone = new Stone(slot, state);
        _stones.Add(stone);
        stone.State.Changed += OnStoneSpatialChanged;

        var form = new PetForm(stone, this);
        _forms[stone] = form;
        form.ApplySpatialPosition();
        form.Show();

        PersistStones();
        PublishStones();
        RefreshLevels();
    }

    private void RemoveStoneInternal()
    {
        if (_stones.Count <= 1)
        {
            return;
        }

        Stone stone = _stones[^1];
        _stones.RemoveAt(_stones.Count - 1);
        stone.State.Changed -= OnStoneSpatialChanged;
        if (_forms.Remove(stone, out PetForm? form))
        {
            form.Close();
        }

        PersistStones();
        PublishStones();
        RefreshLevels();
    }

    /// <summary>把每颗石头的落盘状态写入 stones.txt。</summary>
    internal void PersistStones()
    {
        var list = new List<StoneSnapshot>(_stones.Count);
        foreach (Stone stone in _stones.OrderBy(s => s.Slot))
        {
            list.Add(new StoneSnapshot(stone.State.X, stone.State.Y, stone.State.Z, stone.Muted));
        }

        AppSettings.SaveStones(list);
    }

    /// <summary>面板改坐标（滑块）后立即落盘；窗口拖动由 PetForm 在松手时统一落盘。</summary>
    private void OnStoneSpatialChanged(SpatialOrigin origin)
    {
        if (origin == SpatialOrigin.Panel)
        {
            PersistStones();
        }
    }

    /// <summary>按发声石头数归一各路音量：总响度稳定，且静音 / 不存在的石头不出声。</summary>
    private void RefreshLevels()
    {
        int sounding = _stones.Count(s => !s.Muted);
        float level = sounding == 0 ? 0f : 1f / sounding;
        for (int slot = 0; slot < MaxStones; slot++)
        {
            Stone? stone = _stones.FirstOrDefault(s => s.Slot == slot);
            _player.SetStoneLevel(slot, stone is not null && !stone.Muted ? level : 0f);
        }
    }

    private void PublishStones()
    {
        lock (Gate)
        {
            _snapshot = _stones.ToArray();
        }

        StonesChanged?.Invoke();
    }

    // ===== 托盘 =====

    private NotifyIcon BuildTrayIcon()
    {
        var tray = new NotifyIcon
        {
            Text = "Explaining Stones",
            Visible = true,
            Icon = _appIcon
        };

        // 左键 / 右键均弹出现代菜单
        tray.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
            {
                ShowAppMenu(Cursor.Position);
            }
        };
        return tray;
    }

    /// <summary>弹出应用菜单（托盘或右键桌宠）。</summary>
    public void ShowAppMenu(Point anchor)
    {
        bool playing = _player.IsOpened && !_paused;
        FlyoutMenu.Show(new[]
        {
            new FlyoutMenuItem(playing ? "\uE769" : "\uE768", playing ? "暂停音乐" : "播放音乐", ToggleMusic),
            new FlyoutMenuItem("\uE710", "添加石头", AddStoneInternal, Enabled: _stones.Count < MaxStones),
            new FlyoutMenuItem("\uE738", "移除石头", RemoveStoneInternal, Enabled: _stones.Count > 1),
            new FlyoutMenuItem("\uE713", "设置…", WinUiHost.ShowSettings, SeparatorBefore: true),
            new FlyoutMenuItem("\uE711", "退出", ExitThread, SeparatorBefore: true),
        }, anchor);
    }

    // ===== 音乐 =====

    private void ToggleMusic()
    {
        if (!_player.IsOpened)
        {
            StartMusic();
            return;
        }

        if (_paused)
        {
            _player.Resume();
            _paused = false;
        }
        else
        {
            _player.Pause();
            _paused = true;
        }
    }

    private void StartMusic()
    {
        if (AppSettings.Current is null)
        {
            MessageBox.Show("播放列表为空，请在托盘菜单中选择“设置…”添加音乐。", "Explaining Stones",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        PlayCurrent();
    }

    /// <summary>播放列表中的当前曲目。</summary>
    private void PlayCurrent()
    {
        PlaylistItem? item = AppSettings.Current;
        if (item is null)
        {
            _player.Close();
            _wantPlay = false;
            _playingPath = null;
            return;
        }

        if (!File.Exists(item.Path))
        {
            MessageBox.Show($"未找到音乐文件：\n{item.Path}\n\n请在托盘菜单中选择“设置…”。", "Explaining Stones",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _wantPlay = false;
            return;
        }

        _paused = false;
        _wantPlay = _player.Play(item.Path, item.Loop);
        if (!_wantPlay)
        {
            MessageBox.Show("音乐播放失败，请确认文件格式是否受支持（mp3 / wav）。", "Explaining Stones");
            return;
        }

        _playingPath = item.Path;
        RefreshLevels();
        ApplyAudioEffects();
    }

    /// <summary>当前曲目播完：切到列表中的下一首。</summary>
    private void OnTrackEnded() => Post(() =>
    {
        if (!_wantPlay)
        {
            return;
        }

        AppSettings.CurrentIndex += 1;   // 到列表末尾后回到第一首
        PlayCurrent();
    });

    /// <summary>把音效开关同步到播放器。</summary>
    private void ApplyAudioEffects()
    {
        _player.HissEnabled = AppSettings.HissEnabled;
        _player.CrackleEnabled = AppSettings.CrackleEnabled;
        _player.StereoEnabled = AppSettings.StereoEnabled;
    }

    /// <summary>设置窗口改了播放列表：正在播放的曲目被删除或替换时跟随更新。</summary>
    private void OnPlaylistChanged() => Post(() =>
    {
        if (!_wantPlay)
        {
            return;
        }

        PlaylistItem? item = AppSettings.Current;
        if (item is null)
        {
            _player.Close();
            _wantPlay = false;
            _playingPath = null;
            return;
        }

        if (item.Path == _playingPath)
        {
            _player.Loop = item.Loop;   // 只改了循环标记，无需重播
            return;
        }

        PlayCurrent();
    });

    /// <summary>设置窗口改了音效开关，立即生效。</summary>
    private void OnAudioEffectsChanged() => Post(ApplyAudioEffects);

    /// <summary>用户点击播放列表条目：切到该曲并播放（点当前曲目则恢复播放，不重头）。</summary>
    private void OnTrackRequested(int index) => Post(() =>
    {
        PlaylistItem? item = AppSettings.Current;
        if (item is null)
        {
            return;
        }

        if (item.Path == _playingPath && _player.IsOpened)
        {
            _player.Loop = item.Loop;
            if (_paused)
            {
                _player.Resume();
                _paused = false;
            }

            return;
        }

        _wantPlay = true;
        PlayCurrent();
    });

    /// <summary>切回 UI 线程执行（音频线程与 WinUI 线程都会调用到这里）。</summary>
    private void Post(Action action) => _marshal.BeginInvoke(action);

    protected override void ExitThreadCore()
    {
        AppSettings.PlaylistChanged -= OnPlaylistChanged;
        AppSettings.TrackRequested -= OnTrackRequested;
        AppSettings.AudioEffectsChanged -= OnAudioEffectsChanged;
        _player.TrackEnded -= OnTrackEnded;
        _player.Dispose();

        _tray.Visible = false;
        _tray.Dispose();
        _appIcon.Dispose();

        foreach (Stone stone in _stones)
        {
            stone.State.Changed -= OnStoneSpatialChanged;
        }

        foreach (PetForm form in _forms.Values)
        {
            form.Close();
        }

        _forms.Clear();
        _marshal.Dispose();

        base.ExitThreadCore();
    }
}
