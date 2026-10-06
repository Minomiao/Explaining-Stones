using System;

namespace ExplainingStones;

/// <summary>位置变更来自哪个入口，用于避免桌宠与面板互相回灌。</summary>
internal enum SpatialOrigin
{
    Panel,
    Pet
}

/// <summary>
/// 声源在三维空间中的位置：x 为左右（-1 左 ~ 1 右），y 为上下（-1 下 ~ 1 上），
/// z 为远近（0 贴近屏幕平面 ~ 1 最远）。
/// </summary>
internal static class SpatialState
{
    private static readonly object Gate = new();

    private static double _x;
    private static double _y;
    private static double _z = AppSettings.SpatialDepth;

    /// <summary>位置被修改后触发（可能在任意线程）。</summary>
    public static event Action<SpatialOrigin>? Changed;

    public static double X
    {
        get { lock (Gate) { return _x; } }
    }

    public static double Y
    {
        get { lock (Gate) { return _y; } }
    }

    public static double Z
    {
        get { lock (Gate) { return _z; } }
    }

    /// <summary>一次性读取三个维度，供音频线程使用。</summary>
    public static void Snapshot(out double x, out double y, out double z)
    {
        lock (Gate)
        {
            x = _x;
            y = _y;
            z = _z;
        }
    }

    /// <summary>更新位置，超出范围的值会被收拢。</summary>
    public static void Set(double x, double y, double z, SpatialOrigin origin)
    {
        x = Math.Clamp(x, -1, 1);
        y = Math.Clamp(y, -1, 1);
        z = Math.Clamp(z, 0, 1);

        bool depthChanged;
        lock (Gate)
        {
            if (x == _x && y == _y && z == _z)
            {
                return;
            }

            depthChanged = z != _z;
            _x = x;
            _y = y;
            _z = z;
        }

        if (depthChanged)
        {
            AppSettings.SpatialDepth = z;
        }

        Changed?.Invoke(origin);
    }
}
