using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ExplainingStones;

/// <summary>播放列表条目：音乐文件路径与是否单曲循环。</summary>
internal sealed class PlaylistItem
{
    public PlaylistItem(string path, bool loop)
    {
        Path = path;
        Loop = loop;
    }

    public string Path { get; }

    /// <summary>是否单曲循环。</summary>
    public bool Loop { get; set; }
}

/// <summary>一颗石头的落盘状态：声源位置与是否静音。</summary>
internal readonly record struct StoneSnapshot(double X, double Y, double Z, bool Muted);

/// <summary>
/// 应用设置：播放列表与音效开关，保存在 %AppData%\ExplainingStones\。
/// </summary>
internal static class AppSettings
{
    /// <summary>列表为空时使用的默认音乐路径，随应用安装目录解析。</summary>
    public static string DefaultMusicPath => Path.Combine(AppContext.BaseDirectory, "theme.mp3");

    private static readonly object Gate = new();

    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplainingStones");

    private static readonly string PlaylistFile = Path.Combine(SettingsDirectory, "playlist.txt");

    private static readonly string CurrentFile = Path.Combine(SettingsDirectory, "current.txt");

    private static readonly string HissFile = Path.Combine(SettingsDirectory, "hiss.txt");

    private static readonly string CrackleFile = Path.Combine(SettingsDirectory, "crackle.txt");

    private static readonly string StereoFile = Path.Combine(SettingsDirectory, "stereo.txt");

    private static readonly string SpatialFile = Path.Combine(SettingsDirectory, "spatial.txt");

    private static readonly string StonesFile = Path.Combine(SettingsDirectory, "stones.txt");

    private static List<PlaylistItem>? _playlist;
    private static int _current;
    private static List<StoneSnapshot>? _stones;

    /// <summary>播放列表被修改后触发（可能在任意线程）。</summary>
    public static event Action? PlaylistChanged;

    /// <summary>音效开关被修改后触发（可能在任意线程）。</summary>
    public static event Action? AudioEffectsChanged;

    /// <summary>当前播放列表的快照。</summary>
    public static IReadOnlyList<PlaylistItem> Playlist
    {
        get
        {
            lock (Gate)
            {
                return Load().ToArray();
            }
        }
    }

    /// <summary>当前曲目在列表中的下标，越界时回绕。</summary>
    public static int CurrentIndex
    {
        get
        {
            lock (Gate)
            {
                Load();
                return _current;
            }
        }
        set
        {
            lock (Gate)
            {
                List<PlaylistItem> list = Load();
                if (list.Count == 0)
                {
                    return;
                }

                _current = ((value % list.Count) + list.Count) % list.Count;
                File.WriteAllText(CurrentFile, _current.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>当前曲目，列表为空时为 null。</summary>
    public static PlaylistItem? Current
    {
        get
        {
            lock (Gate)
            {
                List<PlaylistItem> list = Load();
                return list.Count == 0 ? null : list[_current];
            }
        }
    }

    /// <summary>把音乐文件加入列表末尾。</summary>
    public static void AddToPlaylist(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (Gate)
        {
            Load().Add(new PlaylistItem(path, false));
            SavePlaylist();
        }

        PlaylistChanged?.Invoke();
    }

    public static void RemoveFromPlaylist(int index)
    {
        lock (Gate)
        {
            List<PlaylistItem> list = Load();
            if (index < 0 || index >= list.Count)
            {
                return;
            }

            list.RemoveAt(index);
            if (_current >= list.Count)
            {
                _current = 0;
            }

            SavePlaylist();
            File.WriteAllText(CurrentFile, _current.ToString(CultureInfo.InvariantCulture));
        }

        PlaylistChanged?.Invoke();
    }

    /// <summary>设置某条目的单曲循环开关；单曲循环同时只能有一条。</summary>
    public static void SetLoop(int index, bool loop)
    {
        lock (Gate)
        {
            List<PlaylistItem> list = Load();
            if (index < 0 || index >= list.Count)
            {
                return;
            }

            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                bool value = loop && i == index;
                if (list[i].Loop != value)
                {
                    list[i].Loop = value;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            SavePlaylist();
        }

        PlaylistChanged?.Invoke();
    }

    /// <summary>老式收音机声（嘶嘶底噪），默认开启。</summary>
    public static bool HissEnabled
    {
        get => ReadFlag(HissFile, true);
        set => WriteFlag(HissFile, value);
    }

    /// <summary>爆豆声，默认开启。</summary>
    public static bool CrackleEnabled
    {
        get => ReadFlag(CrackleFile, true);
        set => WriteFlag(CrackleFile, value);
    }

    /// <summary>立体声播放，默认开启。</summary>
    public static bool StereoEnabled
    {
        get => ReadFlag(StereoFile, true);
        set => WriteFlag(StereoFile, value);
    }

    /// <summary>新增石头时默认的远近（0 贴近屏幕平面 ~ 1 最远），首次填充时沿用旧值。</summary>
    public static double SpatialDepth
    {
        get
        {
            if (File.Exists(SpatialFile) &&
                double.TryParse(File.ReadAllText(SpatialFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return Math.Clamp(value, 0, 1);
            }

            return 0;
        }
    }

    /// <summary>已落盘的石头状态快照，行数即石头数量（最多三颗）。</summary>
    public static IReadOnlyList<StoneSnapshot> Stones
    {
        get
        {
            lock (Gate)
            {
                return LoadStones().ToArray();
            }
        }
    }

    /// <summary>落盘石头状态；行数即石头数量。</summary>
    public static void SaveStones(IReadOnlyList<StoneSnapshot> stones)
    {
        lock (Gate)
        {
            var list = new List<StoneSnapshot>(stones);
            while (list.Count > 3)
            {
                list.RemoveAt(list.Count - 1);
            }

            _stones = list;
            SaveStonesFile();
        }
    }

    /// <summary>读取石子列表，无文件时返回空（由宿主决定默认数量）。调用方需持有 Gate。</summary>
    private static List<StoneSnapshot> LoadStones()
    {
        if (_stones is not null)
        {
            return _stones;
        }

        var list = new List<StoneSnapshot>();
        if (File.Exists(StonesFile))
        {
            foreach (string line in File.ReadAllLines(StonesFile))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 4 ||
                    !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                {
                    continue;
                }

                list.Add(new StoneSnapshot(
                    Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1), Math.Clamp(z, 0, 1), parts[3] == "1"));
            }
        }

        _stones = list;
        return list;
    }

    private static void SaveStonesFile()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var lines = new List<string>(_stones!.Count);
        foreach (StoneSnapshot stone in _stones)
        {
            lines.Add(string.Join('\t',
                stone.X.ToString("0.###", CultureInfo.InvariantCulture),
                stone.Y.ToString("0.###", CultureInfo.InvariantCulture),
                stone.Z.ToString("0.###", CultureInfo.InvariantCulture),
                stone.Muted ? "1" : "0"));
        }

        File.WriteAllLines(StonesFile, lines);
    }

    /// <summary>读取列表，首次使用时放入内置默认曲目。调用方需持有 Gate。</summary>
    private static List<PlaylistItem> Load()
    {
        if (_playlist is not null)
        {
            return _playlist;
        }

        var list = new List<PlaylistItem>();
        bool loopTaken = false;
        if (File.Exists(PlaylistFile))
        {
            foreach (string line in File.ReadAllLines(PlaylistFile))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && tab + 1 < line.Length)
                {
                    // 单曲循环唯一：只认文件中第一个标记
                    bool loop = line[..tab] == "1" && !loopTaken;
                    loopTaken |= loop;
                    list.Add(new PlaylistItem(line[(tab + 1)..], loop));
                }
            }
        }
        else
        {
            list.Add(new PlaylistItem(DefaultMusicPath, false));
        }

        _current = ReadIndex(list.Count);
        _playlist = list;
        return list;
    }

    private static int ReadIndex(int count)
    {
        if (count == 0 || !File.Exists(CurrentFile))
        {
            return 0;
        }

        return int.TryParse(File.ReadAllText(CurrentFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, 0, count - 1)
            : 0;
    }

    private static void SavePlaylist()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var lines = new List<string>(_playlist!.Count);
        foreach (PlaylistItem item in _playlist)
        {
            lines.Add((item.Loop ? "1" : "0") + "\t" + item.Path);
        }

        File.WriteAllLines(PlaylistFile, lines);
    }

    private static bool ReadFlag(string file, bool fallback)
    {
        if (File.Exists(file))
        {
            return File.ReadAllText(file).Trim() != "false";
        }

        return fallback;
    }

    private static void WriteFlag(string file, bool value)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(file, value ? "true" : "false");
        AudioEffectsChanged?.Invoke();
    }
}
