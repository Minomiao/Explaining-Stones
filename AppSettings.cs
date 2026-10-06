using System;
using System.Globalization;
using System.IO;

namespace ExplainingStones;

/// <summary>
/// 应用设置，目前只有音乐文件路径，保存在 %AppData%\ExplainingStones\music.txt。
/// </summary>
internal static class AppSettings
{
    /// <summary>尚未设置时使用的默认音乐路径。</summary>
    public const string DefaultMusicPath = @"D:\codes\Explaining Stones\music.mp3";

    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExplainingStones");

    private static readonly string SettingsFile = Path.Combine(SettingsDirectory, "music.txt");

    private static readonly string NoiseFile = Path.Combine(SettingsDirectory, "noise.txt");

    private static readonly string SpatialFile = Path.Combine(SettingsDirectory, "spatial.txt");

    /// <summary>音乐路径被修改后触发（可能在任意线程）。</summary>
    public static event Action? MusicPathChanged;

    /// <summary>底噪开关被修改后触发（可能在任意线程）。</summary>
    public static event Action? NoiseEnabledChanged;

    public static string MusicPath
    {
        get
        {
            if (File.Exists(SettingsFile))
            {
                string saved = File.ReadAllText(SettingsFile).Trim();
                if (saved.Length > 0)
                {
                    return saved;
                }
            }

            return DefaultMusicPath;
        }
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFile, value);
            MusicPathChanged?.Invoke();
        }
    }

    /// <summary>播放音乐时是否叠加老式播放器底噪，默认开启。</summary>
    public static bool NoiseEnabled
    {
        get
        {
            if (File.Exists(NoiseFile))
            {
                return File.ReadAllText(NoiseFile).Trim() != "false";
            }

            return true;
        }
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(NoiseFile, value ? "true" : "false");
            NoiseEnabledChanged?.Invoke();
        }
    }

    /// <summary>声源的远近（0 贴近屏幕平面 ~ 1 最远），默认 0。</summary>
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
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SpatialFile, value.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }
}
