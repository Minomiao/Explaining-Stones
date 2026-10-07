using System;

namespace ExplainingStones;

/// <summary>位置变更来自哪个入口，用于避免桌宠与面板互相回灌。</summary>
internal enum SpatialOrigin
{
    Panel,
    Pet
}

/// <summary>
/// 单颗石头的声源位置：x 为左右（-1 左 ~ 1 右），y 为上下（-1 下 ~ 1 上），
/// z 为远近（0 贴近屏幕平面 ~ 1 最远）。每颗石头各持一份。
/// </summary>
internal sealed class SpatialState
{
    private readonly object _gate = new();

    private double _x;
    private double _y;
    private double _z;

    public SpatialState(double x, double y, double z)
    {
        _x = Math.Clamp(x, -1, 1);
        _y = Math.Clamp(y, -1, 1);
        _z = Math.Clamp(z, 0, 1);
    }

    /// <summary>位置被修改后触发（可能在任意线程）。</summary>
    public event Action<SpatialOrigin>? Changed;

    public double X
    {
        get { lock (_gate) { return _x; } }
    }

    public double Y
    {
        get { lock (_gate) { return _y; } }
    }

    public double Z
    {
        get { lock (_gate) { return _z; } }
    }

    /// <summary>一次性读取三个维度，供音频线程使用。</summary>
    public void Snapshot(out double x, out double y, out double z)
    {
        lock (_gate)
        {
            x = _x;
            y = _y;
            z = _z;
        }
    }

    /// <summary>更新位置，超出范围的值会被收拢。</summary>
    public void Set(double x, double y, double z, SpatialOrigin origin)
    {
        x = Math.Clamp(x, -1, 1);
        y = Math.Clamp(y, -1, 1);
        z = Math.Clamp(z, 0, 1);

        lock (_gate)
        {
            if (x == _x && y == _y && z == _z)
            {
                return;
            }

            _x = x;
            _y = y;
            _z = z;
        }

        Changed?.Invoke(origin);
    }
}
