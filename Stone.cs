using System;

namespace ExplainingStones;

/// <summary>桌面上的一颗石头：占用一个槽位、拥有自己的空间位置与静音状态。</summary>
internal sealed class Stone
{
    private bool _muted;

    public Stone(int slot, SpatialState state)
    {
        Slot = slot;
        State = state;
    }

    /// <summary>槽位下标（0 ~ 2），决定音频支路与持久化顺序。</summary>
    public int Slot { get; }

    /// <summary>该石头的声源位置。</summary>
    public SpatialState State { get; }

    /// <summary>是否被静音（左键点击切换）。</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
            {
                return;
            }

            _muted = value;
            MutedChanged?.Invoke();
        }
    }

    /// <summary>静音状态变化后触发。</summary>
    public event Action? MutedChanged;
}
