using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplainingStones;

/// <summary>弹出菜单项：Fluent 图标 + 文本。</summary>
public readonly record struct FlyoutMenuItem(string Glyph, string Text, Action? Click, bool SeparatorBefore = false);

/// <summary>
/// Windows 11 风格弹出菜单：圆角、跟随系统深浅色、图标 + 文本、悬停高亮，点击外部自动关闭。
/// </summary>
internal static class FlyoutMenu
{
    private static FlyoutMenuWindow? _current;

    /// <summary>在锚点（屏幕物理像素坐标）附近弹出菜单；重复调用会先收起旧菜单。</summary>
    public static void Show(IReadOnlyList<FlyoutMenuItem> items, Point anchor)
    {
        Close();
        var window = new FlyoutMenuWindow(items, anchor);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
            {
                _current = null;
            }
        };
        _current = window;
        window.Show();
    }

    public static void Close() => _current?.Close();
}

internal sealed class FlyoutMenuWindow : Form
{
    // ===== 布局（96 DPI 逻辑像素）=====
    private const int ItemHeight = 34;
    private const int MenuWidth = 216;
    private const int Edge = 8;
    private const int HoverInset = 6;
    private const int HoverRadius = 5;
    private const int IconSlot = 18;
    private const int IconTextGap = 12;
    private const int SeparatorHeight = 6;

    private readonly IReadOnlyList<FlyoutMenuItem> _items;
    private readonly List<Rectangle> _rowRects = new();   // 物理像素行矩形
    private readonly List<int> _rowItemIndex = new();     // 行对应的菜单项下标（-1 为分隔线）
    private readonly bool _dark;
    private readonly float _scale;
    private readonly Font _textFont;
    private readonly Font _iconFont;
    private int _hover = -1;

    public FlyoutMenuWindow(IReadOnlyList<FlyoutMenuItem> items, Point anchor)
    {
        _items = items;
        _dark = IsDarkMode();
        _scale = GetDpiForSystem() / 96f;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        MinimizeBox = MaximizeBox = false;
        KeyPreview = true;

        _textFont = new Font("Segoe UI", 9f);
        _iconFont = HasFontFamily("Segoe Fluent Icons")
            ? new Font("Segoe Fluent Icons", 10.5f)
            : new Font("Segoe MDL2 Assets", 10.5f);

        BuildRows();

        int width = P(MenuWidth);
        int height = _rowRects.Count > 0 ? _rowRects[^1].Bottom + P(Edge) : P(Edge);

        // 默认在锚点上方居中弹出，并夹在屏幕工作区内
        var screen = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Clamp(anchor.X - width / 2, screen.Left + P(8), Math.Max(screen.Left + P(8), screen.Right - width - P(8)));
        int y = anchor.Y - height - P(12);
        if (y < screen.Top + P(8))
        {
            y = anchor.Y + P(16);   // 锚点靠上（如托盘在屏幕顶部）时改为下方弹出
        }
        y = Math.Clamp(y, screen.Top + P(8), Math.Max(screen.Top + P(8), screen.Bottom - height - P(8)));

        Bounds = new Rectangle(x, y, width, height);
    }

    // 点击菜单外部（窗口失活）时自动收起
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW：弹出窗口带投影
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 圆角窗口（Win11；旧系统忽略）
        int preference = 2;   // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = HitIndex(e.Location);
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = HitIndex(e.Location);
        if (index >= 0)
        {
            Action? click = _items[index].Click;
            Close();
            click?.Invoke();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var background = new SolidBrush(_dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249));
        g.FillRectangle(background, ClientRectangle);

        Color text = _dark ? Color.White : Color.FromArgb(27, 27, 27);
        Color hover = _dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(233, 233, 233);
        Color separator = _dark ? Color.FromArgb(63, 63, 63) : Color.FromArgb(224, 224, 224);
        int lineInset = P(Edge) / 2;

        for (int row = 0; row < _rowRects.Count; row++)
        {
            if (_rowItemIndex[row] < 0)
            {
                using var pen = new Pen(separator, 1);
                int lineY = _rowRects[row].Bottom - 1;
                g.DrawLine(pen, lineInset, lineY, ClientRectangle.Width - lineInset, lineY);
                continue;
            }

            var bounds = _rowRects[row];
            if (row == _hover)
            {
                using var path = RoundedRect(bounds, P(HoverRadius));
                using var fill = new SolidBrush(hover);
                g.FillPath(fill, path);
            }

            var item = _items[_rowItemIndex[row]];
            var iconRect = new Rectangle(bounds.Left + P(HoverInset), bounds.Top, P(IconSlot), bounds.Height);
            TextRenderer.DrawText(g, item.Glyph, _iconFont, iconRect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var textRect = new Rectangle(iconRect.Right + P(IconTextGap), bounds.Top,
                bounds.Right - iconRect.Right - P(IconTextGap) - P(HoverInset), bounds.Height);
            TextRenderer.DrawText(g, item.Text, _textFont, textRect, text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // ===== 内部 =====

    /// <summary>按 DPI 把布局（逻辑像素）展开成物理像素的行矩形。</summary>
    private void BuildRows()
    {
        int y = P(Edge);
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].SeparatorBefore)
            {
                _rowRects.Add(new Rectangle(0, y, 1, P(SeparatorHeight)));
                _rowItemIndex.Add(-1);
                y += P(SeparatorHeight);
            }

            _rowRects.Add(new Rectangle(P(HoverInset), y, P(MenuWidth) - P(HoverInset) * 2, P(ItemHeight)));
            _rowItemIndex.Add(i);
            y += P(ItemHeight);
        }
    }

    private int HitIndex(Point point)
    {
        for (int row = 0; row < _rowRects.Count; row++)
        {
            if (_rowItemIndex[row] >= 0 && _rowRects[row].Contains(point))
            {
                return _rowItemIndex[row];
            }
        }

        return -1;
    }

    private int P(int logical) => (int)Math.Round(logical * _scale);

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool HasFontFamily(string name)
    {
        foreach (var family in FontFamily.Families)
        {
            if (family.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDarkMode()
    {
        object? value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1);
        return value is int light && light == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();
}
