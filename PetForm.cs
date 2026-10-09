using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplainingStones;

/// <summary>
/// 单颗石头的桌宠窗体：无边框、置顶、支持逐像素透明与投影阴影，可拖动，
/// 单击开关这一颗石头的声音（静音时半透明绘制），右键打开应用菜单。
/// </summary>
internal sealed class PetForm : Form
{
    // ===== 配置 =====

    /// <summary>桌宠显示的最大边长（96 DPI 下的逻辑像素，实际渲染时按屏幕 DPI 放大）。</summary>
    private const int PetSize = 180;

    /// <summary>画布四周为阴影预留的留白（物理像素）。</summary>
    private const int ShadowPad = 40;

    /// <summary>静音时桌宠的不透明度。</summary>
    private const float MutedOpacity = 0.35f;

    // ===== Win32 常量 =====
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private readonly Stone _stone;
    private readonly StoneHost _host;
    private readonly Bitmap? _source;
    private Bitmap? _bitmap;

    private bool _dragging;
    private Point _dragOffset;
    private Point _dragStart;              // 按下位置，用于区分单击与拖动
    private bool _movedFar;                // 按下后是否位移超过阈值
    private bool _applyingSpatialMove;     // 面板在移动桌宠时，忽略位置回灌
    private readonly float _dpiScale;      // 系统 DPI 缩放
    private int _contentWidth;             // 内容尺寸（不含阴影留白）
    private int _contentHeight;
    private int _canvasWidth;              // 分层窗口画布宽（固定）
    private int _canvasHeight;             // 分层窗口画布高（固定）
    private byte[]? _alphaMask;            // 画布 alpha 掩码，用于透明区鼠标穿透
    private double _lastZ;                 // 上次的远近，用于判断内容是否需要重绘

    public PetForm(Stone stone, StoneHost host)
    {
        _stone = stone;
        _host = host;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Explaining Stones";
        // 分层窗口由 UpdateLayeredWindow 按物理像素直接绘制，禁止 WinForms 再叠加一次 DPI 缩放
        AutoScaleMode = AutoScaleMode.None;

        _dpiScale = GetDpiForSystem() / 96f;
        _source = LoadSourceImage();
        int maxPixels = LogicalToPixels(PetSize);
        if (_source is not null)
        {
            float fit = Math.Min((float)maxPixels / _source.Width, (float)maxPixels / _source.Height);
            _contentWidth = Math.Max(1, (int)(_source.Width * fit));
            _contentHeight = Math.Max(1, (int)(_source.Height * fit));
        }
        else
        {
            _contentWidth = maxPixels;
            _contentHeight = maxPixels;
        }

        _canvasWidth = _contentWidth + ShadowPad * 2;
        _canvasHeight = _contentHeight + ShadowPad * 2;

        _bitmap = ComposePet();
        ClientSize = new Size(_canvasWidth, _canvasHeight);
        _lastZ = stone.State.Z;

        stone.MutedChanged += OnMutedChanged;
        stone.State.Changed += OnSpatialChanged;

        // 默认显示在屏幕右下角（随后由宿主按落盘位置复位）
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 40, area.Bottom - Height - 40);
    }

    // ===== 逐像素透明（分层窗口）=====

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RenderLayered();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RenderLayered();
        SyncSpatialFromLocation();
    }

    // 分层窗口由 UpdateLayeredWindow 负责绘制，屏蔽普通绘制以避免闪烁
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // 阴影等低透明度区域允许鼠标穿透
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == (IntPtr)HTCLIENT && _alphaMask is not null)
        {
            long lp = m.LParam;
            var client = PointToClient(new Point((short)lp, (short)(lp >> 16)));
            if (client.X >= 0 && client.X < _canvasWidth && client.Y >= 0 && client.Y < _canvasHeight
                && _alphaMask[client.Y * _canvasWidth + client.X] < 32)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
            }
        }
    }

    private void RenderLayered()
    {
        if (!IsHandleCreated || _bitmap is null)
        {
            return;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = _bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE(_bitmap.Width, _bitmap.Height);
            var srcPoint = new POINT(0, 0);
            var topPoint = new POINT(Left, Top);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref topPoint, ref size, memDc, ref srcPoint, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }
    }

    /// <summary>把 96 DPI 的逻辑像素换算成当前系统 DPI 下的物理像素。</summary>
    internal static int LogicalToPixels(int logical)
    {
        int dpi = GetDpiForSystem();
        if (dpi <= 0)
        {
            dpi = 96;
        }

        return (int)Math.Round(logical * (dpi / 96.0));
    }

    private static Bitmap? LoadSourceImage()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Stone.png");
        if (!File.Exists(path))
        {
            return null;
        }

        using var file = new Bitmap(path);
        return new Bitmap(file);   // 复制一份，避免锁定图片文件
    }

    /// <summary>按当前远近合成桌宠位图：内容按远近缩放并居中（越远越小），并绘制投影阴影。</summary>
    private Bitmap? ComposePet()
    {
        if (_source is null)
        {
            return null;
        }

        float scale = 1f - 0.5f * (float)_stone.State.Z;
        int width = Math.Max(1, (int)Math.Round(_contentWidth * scale));
        int height = Math.Max(1, (int)Math.Round(_contentHeight * scale));
        int x0 = (_canvasWidth - width) / 2;
        int y0 = (_canvasHeight - height) / 2;

        var bitmap = new Bitmap(_canvasWidth, _canvasHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int blur = Math.Max(1, (int)Math.Round(4 * _dpiScale * scale));
            int drop = Math.Max(1, (int)Math.Round(6 * _dpiScale * scale));
            using var shadow = CreateShadow(_source, width, height, blur, 0.6f);
            g.DrawImage(shadow, new Rectangle(x0, y0 + drop, width, height));
            g.DrawImage(_source, new Rectangle(x0, y0, width, height));
        }

        if (_stone.Muted)
        {
            ApplyOpacity(bitmap, MutedOpacity);
        }

        _alphaMask = ExtractAlpha(bitmap);
        return bitmap;
    }

    /// <summary>整体缩放位图的 alpha，用于静音时半透明显示。</summary>
    private static void ApplyOpacity(Bitmap bitmap, float opacity)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int y = 0; y < bitmap.Height; y++)
            {
                int rowStart = y * data.Stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int offset = rowStart + x * 4 + 3;
                    buffer[offset] = (byte)(buffer[offset] * opacity);
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>生成黑色剪影模糊阴影。</summary>
    private static Bitmap CreateShadow(Bitmap source, int width, int height, int blur, float opacity)
    {
        using var silhouette = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(silhouette))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var rect = new Rectangle(0, 0, width, height);
        var data = silhouette.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var alpha = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    alpha[y * width + x] = buffer[rowStart + x * 4 + 3];
                }
            }

            // 三次盒式模糊近似高斯模糊
            for (int i = 0; i < 3; i++)
            {
                BoxBlur(alpha, width, height, blur, horizontal: true);
                BoxBlur(alpha, width, height, blur, horizontal: false);
            }

            byte op = (byte)Math.Clamp((int)Math.Round(opacity * 255f), 0, 255);
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    int offset = rowStart + x * 4;
                    buffer[offset] = 0;
                    buffer[offset + 1] = 0;
                    buffer[offset + 2] = 0;
                    buffer[offset + 3] = (byte)(alpha[y * width + x] * op / 255);
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            silhouette.UnlockBits(data);
        }

        return new Bitmap(silhouette);
    }

    /// <summary>单方向盒式模糊（滑窗均值）。</summary>
    private static void BoxBlur(byte[] values, int width, int height, int radius, bool horizontal)
    {
        int length = horizontal ? width : height;
        int other = horizontal ? height : width;
        int window = radius * 2 + 1;
        var result = new byte[values.Length];

        for (int o = 0; o < other; o++)
        {
            int sum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                int index = Math.Clamp(i, 0, length - 1);
                sum += horizontal ? values[o * width + index] : values[index * width + o];
            }

            for (int i = 0; i < length; i++)
            {
                if (horizontal)
                {
                    result[o * width + i] = (byte)(sum / window);
                }
                else
                {
                    result[i * width + o] = (byte)(sum / window);
                }

                int added = Math.Clamp(i + radius + 1, 0, length - 1);
                int removed = Math.Clamp(i - radius, 0, length - 1);
                sum += horizontal ? values[o * width + added] - values[o * width + removed]
                                  : values[added * width + o] - values[removed * width + o];
            }
        }

        Array.Copy(result, values, values.Length);
    }

    /// <summary>提取位图的 alpha 通道为一维掩码。</summary>
    private static byte[] ExtractAlpha(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var mask = new byte[bitmap.Width * bitmap.Height];
            var buffer = new byte[data.Stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (int y = 0; y < bitmap.Height; y++)
            {
                int rowStart = y * data.Stride;
                int maskRow = y * bitmap.Width;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    mask[maskRow + x] = buffer[rowStart + x * 4 + 3];
                }
            }

            return mask;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // ===== 交互：拖动 / 单击静音 / 右键菜单 =====

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _movedFar = false;
            _dragStart = Cursor.Position;
            _dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var position = Cursor.Position;
            // 位移超过阈值视为拖动，否则松开时按单击处理
            if (Math.Abs(position.X - _dragStart.X) + Math.Abs(position.Y - _dragStart.Y) > 8)
            {
                _movedFar = true;
            }

            Location = new Point(position.X - _dragOffset.X, position.Y - _dragOffset.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
            if (!_movedFar)
            {
                _host.ToggleStoneSound(_stone);
            }
            else
            {
                _host.PersistStones();   // 拖动结束才落盘，避免拖动过程中频繁写文件
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            _host.ShowAppMenu(Cursor.Position);
        }
    }

    /// <summary>把桌宠在桌面上的位置映射为这一颗石头的声源左右（x）与上下（y）。</summary>
    private void SyncSpatialFromLocation()
    {
        if (_applyingSpatialMove)
        {
            return;
        }

        var area = Screen.PrimaryScreen!.WorkingArea;
        double x = (Left + Width / 2.0 - (area.Left + area.Width / 2.0)) / (area.Width / 2.0);
        double y = (area.Top + area.Height / 2.0 - (Top + Height / 2.0)) / (area.Height / 2.0);
        _stone.State.Set(x, y, _stone.State.Z, SpatialOrigin.Pet);
    }

    /// <summary>位置变化：面板改 x/y 时移动桌宠；远近 z 变化时重新缩放桌宠。</summary>
    private void OnSpatialChanged(SpatialOrigin origin)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        double z = _stone.State.Z;
        bool zChanged = z != _lastZ;
        _lastZ = z;

        if (origin != SpatialOrigin.Panel && !zChanged)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            if (origin == SpatialOrigin.Panel)
            {
                ApplySpatialPosition();
            }

            if (zChanged)
            {
                RefreshPetSize();
            }
        }));
    }

    /// <summary>静音状态变化：重绘为半透明 / 恢复正常。</summary>
    private void OnMutedChanged()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(RefreshPetSize));
    }

    /// <summary>按这一颗石头的 x/y 复位窗口位置（供宿主在显示前调用）。</summary>
    public void ApplySpatialPosition()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        int centerX = (int)Math.Round(area.Left + area.Width / 2.0 + _stone.State.X * (area.Width / 2.0));
        int centerY = (int)Math.Round(area.Top + area.Height / 2.0 - _stone.State.Y * (area.Height / 2.0));

        _applyingSpatialMove = true;
        Location = new Point(centerX - Width / 2, centerY - Height / 2);
        _applyingSpatialMove = false;
    }

    /// <summary>远近或静音变化时重绘桌宠内容（窗口尺寸不变，透明区域不影响点击）。</summary>
    private void RefreshPetSize()
    {
        if (_source is null || _bitmap is null)
        {
            return;
        }

        var old = _bitmap;
        _bitmap = ComposePet();
        old?.Dispose();
        RenderLayered();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stone.MutedChanged -= OnMutedChanged;
        _stone.State.Changed -= OnSpatialChanged;

        _bitmap?.Dispose();
        _source?.Dispose();

        base.OnFormClosed(e);
    }

    // ===== Win32 互操作 =====

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int cx, int cy)
    {
        public int cx = cx;
        public int cy = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
