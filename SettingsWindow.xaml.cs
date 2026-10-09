using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace ExplainingStones;

/// <summary>
/// WinUI 3 设置窗口：左侧导航分为「音乐」与「音效」两页。
/// 音乐页维护播放列表，改动立即生效。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // 斜 45° 视角的投影参数（世界坐标：x 向右、y 向上、z 向远处）
    private const double ViewScale = 48;
    private const double ViewCenterX = 130;
    private const double ViewCenterY = 112;

    private bool _syncingSpatial;
    private int _selectedSlot = -1;
    private RadioButton[] _stoneRadios = null!;
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
            (int)Math.Round(680 * scale), (int)Math.Round(560 * scale)));

        // 关闭时只隐藏，避免 WinUI 消息循环结束（结束后无法再次打开窗口）
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        _stoneRadios = new[] { Stone0Radio, Stone1Radio, Stone2Radio };
        for (int i = 0; i < _stoneRadios.Length; i++)
        {
            int slot = i;
            _stoneRadios[i].Checked += (_, _) => SelectStone(slot);
        }

        BrowseButton.Click += async (_, _) => await BrowseAsync();
        CrackleCheck.Click += (_, _) => AppSettings.CrackleEnabled = CrackleCheck.IsChecked == true;
        HissCheck.Click += (_, _) => AppSettings.HissEnabled = HissCheck.IsChecked == true;
        StereoCheck.Click += (_, _) => AppSettings.StereoEnabled = StereoCheck.IsChecked == true;
        XSlider.ValueChanged += (_, _) => PushSpatialFromPanel();
        YSlider.ValueChanged += (_, _) => PushSpatialFromPanel();
        ZSlider.ValueChanged += (_, _) => PushSpatialFromPanel();
        AppSettings.PlaylistChanged += RefreshPlaylist;
        AppSettings.TrackRequested += OnTrackRequested;
        StoneHost.StonesChanged += OnStonesChanged;

        BuildSpatialView();
    }

    public void Show()
    {
        HissCheck.IsChecked = AppSettings.HissEnabled;
        CrackleCheck.IsChecked = AppSettings.CrackleEnabled;

        // 单扬声器设备不支持立体声：开关置灰并说明
        bool stereoSupported = SpatialMusicPlayer.OutputSupportsStereo();
        StereoCheck.IsEnabled = stereoSupported;
        StereoCheck.IsChecked = stereoSupported && AppSettings.StereoEnabled;
        StereoLabel.Text = stereoSupported ? "立体声" : "立体声：在此设备上不支持";

        RefreshPlaylist();
        RefreshStoneSwitch();
        AppWindow.Show();
        Activate();
    }

    // ===== 石头切换 =====

    /// <summary>当前选中的石头，可能为 null（该槽位已无石头）。</summary>
    private SpatialState? SelectedState => StoneAt(_selectedSlot)?.State;

    private static Stone? StoneAt(int slot)
    {
        foreach (Stone stone in StoneHost.Stones)
        {
            if (stone.Slot == slot)
            {
                return stone;
            }
        }

        return null;
    }

    /// <summary>选中某颗石头；不存在的槽位忽略。</summary>
    private void SelectStone(int slot)
    {
        if (slot == _selectedSlot)
        {
            return;
        }

        if (StoneAt(slot) is null)
        {
            return;
        }

        Stone? old = StoneAt(_selectedSlot);
        if (old is not null)
        {
            old.State.Changed -= OnSpatialStateChanged;
        }

        _selectedSlot = slot;

        Stone? now = StoneAt(slot);
        if (now is not null)
        {
            now.State.Changed += OnSpatialStateChanged;
        }

        SyncSlidersFromState();
    }

    /// <summary>按当前石头集合刷新切换按钮：数量之外的置灰，选中项被移除时回退到第一颗。</summary>
    private void RefreshStoneSwitch()
    {
        if (StoneAt(_selectedSlot) is null && StoneHost.Stones.Count > 0)
        {
            SelectStone(StoneHost.Stones[0].Slot);
        }

        for (int slot = 0; slot < _stoneRadios.Length; slot++)
        {
            bool exists = StoneAt(slot) is not null;
            _stoneRadios[slot].IsEnabled = exists;
            _stoneRadios[slot].IsChecked = slot == _selectedSlot;
        }
    }

    private void OnStonesChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshStoneSwitch();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshStoneSwitch);
        }
    }

    /// <summary>左侧导航切换：在音乐与音效两页之间切换。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool music = (args.SelectedItem as NavigationViewItem)?.Tag as string == "music";
        MusicPage.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        SoundPage.Visibility = music ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>重建播放列表，当前曲目加粗显示。</summary>
    private void RefreshPlaylist()
    {
        PlaylistPanel.Children.Clear();

        IReadOnlyList<PlaylistItem> list = AppSettings.Playlist;
        if (list.Count == 0)
        {
            PlaylistPanel.Children.Add(new TextBlock { Text = "列表为空", Opacity = 0.6 });
            return;
        }

        int current = AppSettings.CurrentIndex;
        for (int i = 0; i < list.Count; i++)
        {
            PlaylistPanel.Children.Add(BuildPlaylistRow(i, list[i], i == current));
        }
    }

    /// <summary>切换当前曲目后刷新列表（高亮移到新的当前项）。</summary>
    private void OnTrackRequested(int index)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshPlaylist();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshPlaylist);
        }
    }

    private FrameworkElement BuildPlaylistRow(int index, PlaylistItem item, bool current)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = System.IO.Path.GetFileName(item.Path),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
        };

        // 条目本身是按钮：点击切到该曲并播放
        var play = new Button
        {
            Content = name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6)
        };
        ToolTipService.SetToolTip(play, item.Path);
        play.Click += (_, _) => AppSettings.RequestTrack(index);
        row.Children.Add(play);

        var loop = new ToggleButton
        {
            IsChecked = item.Loop,
            Content = new FontIcon { Glyph = "\uE8ED", FontSize = 14 }
        };
        ToolTipService.SetToolTip(loop, "单曲循环");
        loop.Click += (_, _) => AppSettings.SetLoop(index, loop.IsChecked == true);
        Grid.SetColumn(loop, 1);
        row.Children.Add(loop);

        var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 } };
        ToolTipService.SetToolTip(remove, "从列表删除");
        remove.Click += (_, _) => AppSettings.RemoveFromPlaylist(index);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    /// <summary>面板滑块变化：更新所选石头的声源位置（并让该石头窗口跟着移动）。</summary>
    private void PushSpatialFromPanel()
    {
        if (_syncingSpatial)
        {
            return;
        }

        SelectedState?.Set(XSlider.Value, YSlider.Value, ZSlider.Value, SpatialOrigin.Panel);
        UpdateSpatialView();
    }

    /// <summary>所选石头被拖动后，回填滑块与示意图。</summary>
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
        SpatialState? state = SelectedState;
        if (state is null)
        {
            return;
        }

        _syncingSpatial = true;
        XSlider.Value = state.X;
        YSlider.Value = state.Y;
        ZSlider.Value = state.Z;
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

    /// <summary>把所选石头的声源位置画到三维视图上，并标出它在屏幕平面上的投影。</summary>
    private void UpdateSpatialView()
    {
        SpatialState? state = SelectedState;
        if (state is null)
        {
            return;
        }

        double z = state.Z;
        Point onPlane = Project(state.X, state.Y, 0);
        Point source = Project(state.X, state.Y, z);

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
            AppSettings.AddToPlaylist(file.Path);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);
}
