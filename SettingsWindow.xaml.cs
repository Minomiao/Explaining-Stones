using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace ExplainingStones;

/// <summary>
/// WinUI 3 设置窗口：选择音乐文件位置，并调整声源在三维空间中的位置。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // 斜 45° 视角的投影参数（世界坐标：x 向右、y 向上、z 向远处）
    private const double ViewScale = 48;
    private const double ViewCenterX = 130;
    private const double ViewCenterY = 112;

    private bool _syncingSpatial;
    private Ellipse _sourceDot = null!;
    private Ellipse _sourceShadow = null!;
    private Line _dropLine = null!;

    public SettingsWindow()
    {
        InitializeComponent();

        // 顶部沉浸式：内容延伸到标题栏区域，AppTitleBar 作为窗口拖动区域
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // 右侧留出系统按钮的位置，避免标题与最小化/最大化/关闭按钮重叠
        RightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset);

        // 窗口尺寸按屏幕 DPI 换算，保证与 96 DPI 下的设计尺寸一致
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        int dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 96;
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(560 * scale), (int)Math.Round(540 * scale)));

        // 关闭时只隐藏，避免 WinUI 消息循环结束（结束后无法再次打开窗口）
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        BrowseButton.Click += async (_, _) => await BrowseAsync();
        SaveButton.Click += (_, _) => Save();
        NoiseCheck.Click += (_, _) => AppSettings.NoiseEnabled = NoiseCheck.IsChecked == true;
        XSlider.ValueChanged += (_, _) => PushSpatialFromPanel();
        YSlider.ValueChanged += (_, _) => PushSpatialFromPanel();
        ZSlider.ValueChanged += (_, _) => PushSpatialFromPanel();
        SpatialState.Changed += OnSpatialStateChanged;

        BuildSpatialView();
    }

    public void Show()
    {
        PathBox.Text = AppSettings.MusicPath;
        NoiseCheck.IsChecked = AppSettings.NoiseEnabled;
        SyncSlidersFromState();
        AppWindow.Show();
        Activate();
    }

    /// <summary>面板滑块变化：更新声源位置（并让桌宠跟着移动）。</summary>
    private void PushSpatialFromPanel()
    {
        if (_syncingSpatial)
        {
            return;
        }

        SpatialState.Set(XSlider.Value, YSlider.Value, ZSlider.Value, SpatialOrigin.Panel);
        UpdateSpatialView();
    }

    /// <summary>桌宠被拖动后，回填滑块与示意图。</summary>
    private void OnSpatialStateChanged(SpatialOrigin origin)
    {
        if (origin != SpatialOrigin.Pet)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            SyncSlidersFromState();
        }
        else
        {
            DispatcherQueue.TryEnqueue(SyncSlidersFromState);
        }
    }

    private void SyncSlidersFromState()
    {
        _syncingSpatial = true;
        XSlider.Value = SpatialState.X;
        YSlider.Value = SpatialState.Y;
        ZSlider.Value = SpatialState.Z;
        _syncingSpatial = false;
        UpdateSpatialView();
    }

    /// <summary>构建斜 45° 视角的静态部分：屏幕平面（平视面）与向远处延伸的深度棱。</summary>
    private void BuildSpatialView()
    {
        var planeFill = new SolidColorBrush(Color.FromArgb(36, 76, 110, 145));
        var edge = new SolidColorBrush(Color.FromArgb(150, 76, 110, 145));
        var faint = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128));
        var dotFill = new SolidColorBrush(Color.FromArgb(255, 76, 110, 145));

        var front = new[]
        {
            Project(-1, 1, 0), Project(1, 1, 0), Project(1, -1, 0), Project(-1, -1, 0)
        };
        var back = new[]
        {
            Project(-1, 1, 1), Project(1, 1, 1), Project(1, -1, 1), Project(-1, -1, 1)
        };

        SpatialView.Children.Add(new Polygon { Points = ToPoints(back), Stroke = faint, StrokeThickness = 1 });
        for (int i = 0; i < 4; i++)
        {
            SpatialView.Children.Add(new Line
            {
                X1 = front[i].X,
                Y1 = front[i].Y,
                X2 = back[i].X,
                Y2 = back[i].Y,
                Stroke = faint,
                StrokeThickness = 1
            });
        }

        SpatialView.Children.Add(new Polygon
        {
            Points = ToPoints(front),
            Fill = planeFill,
            Stroke = edge,
            StrokeThickness = 1.5
        });

        _sourceShadow = new Ellipse { Width = 8, Height = 8, Stroke = edge, StrokeThickness = 1.5 };
        SpatialView.Children.Add(_sourceShadow);

        _dropLine = new Line { Stroke = faint, StrokeThickness = 1 };
        SpatialView.Children.Add(_dropLine);

        _sourceDot = new Ellipse { Width = 14, Height = 14, Fill = dotFill };
        SpatialView.Children.Add(_sourceDot);
    }

    /// <summary>世界坐标 (x, y, z) → 画布坐标：平视面正对（上下不斜），纵深向左上方 45° 延伸。</summary>
    private static Point Project(double x, double y, double z)
    {
        const double depth = 0.7071;   // 45° 的横/纵分量
        double sx = ViewCenterX + (x - z * depth) * ViewScale;
        double sy = ViewCenterY - (y + z * depth) * ViewScale;
        return new Point(sx, sy);
    }

    private static PointCollection ToPoints(IEnumerable<Point> points)
    {
        var collection = new PointCollection();
        foreach (var point in points)
        {
            collection.Add(point);
        }

        return collection;
    }

    /// <summary>把声源位置画到三维视图上，并标出它在屏幕平面上的投影。</summary>
    private void UpdateSpatialView()
    {
        double z = SpatialState.Z;
        Point onPlane = Project(SpatialState.X, SpatialState.Y, 0);
        Point source = Project(SpatialState.X, SpatialState.Y, z);

        Canvas.SetLeft(_sourceShadow, onPlane.X - 4);
        Canvas.SetTop(_sourceShadow, onPlane.Y - 4);

        _dropLine.X1 = onPlane.X;
        _dropLine.Y1 = onPlane.Y;
        _dropLine.X2 = source.X;
        _dropLine.Y2 = source.Y;

        double size = 16 - z * 6;   // 越远越小
        _sourceDot.Width = size;
        _sourceDot.Height = size;
        Canvas.SetLeft(_sourceDot, source.X - size / 2);
        Canvas.SetTop(_sourceDot, source.Y - size / 2);
    }

    private async Task BrowseAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".flac");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            PathBox.Text = file.Path;
        }
    }

    private void Save()
    {
        string path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            return;
        }

        AppSettings.MusicPath = path;
        AppWindow.Hide();
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);
}
