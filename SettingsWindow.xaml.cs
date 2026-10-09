using System;
using System.Collections.Generic;
using System.Linq;
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
/// WinUI 3 设置窗口：左侧导航分为「音乐」「石头」「音效」三页。
/// 音乐页维护播放列表，石头页管理石头（只读位置视图 + 条目列表），音效页调整声源位置与音效开关。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    // 斜 45° 视角的投影参数（世界坐标：x 向右、y 向上、z 向远处）
    private const double ViewScale = 48;
    private const double ViewCenterX = 130;
    private const double ViewCenterY = 112;

    // 石头页只读二维视图的画布尺寸与留白
    private const double MapWidth = 460;
    private const double MapHeight = 200;
    private const double MapPad = 28;

    private bool _syncingSpatial;
    private int _selectedSlot = -1;
    private readonly List<(int Slot, ToggleButton Button)> _stoneButtons = new();
    private Ellipse _sourceDot = null!;
    private Ellipse _sourceShadow = null!;
    private Line _dropLine = null!;

    // 石头页当前已订阅状态变化的石头，重建列表时先退订
    private readonly List<Stone> _subscribedStones = new();

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

        BrowseButton.Click += async (_, _) => await BrowseAsync();
        StonePageAddButton.Click += (_, _) => StoneHost.Current?.AddStone();
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
        RefreshStoneSelection();
        RebuildStonePage();
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
        RefreshStoneSelection();
        RefreshStoneAddButton();
        RebuildStonePage();
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

    /// <summary>按槽位排序的现有石头（展示顺序即命名顺序）。</summary>
    private static List<Stone> OrderedStones() => StoneHost.Stones.OrderBy(s => s.Slot).ToList();

    /// <summary>重建石头选择器：只列出现有石头，末尾是「＋」添加，满三颗时「＋」置灰。</summary>
    private void RefreshStoneSelection()
    {
        List<Stone> stones = OrderedStones();
        if (StoneAt(_selectedSlot) is null && stones.Count > 0)
        {
            SelectStone(stones[0].Slot);
        }

        StoneSelectorPanel.Children.Clear();
        _stoneButtons.Clear();

        for (int i = 0; i < stones.Count; i++)
        {
            Stone stone = stones[i];
            int slot = stone.Slot;
            var button = new ToggleButton { Content = $"石头 {i + 1}" };
            button.Click += (_, _) =>
            {
                SelectStone(slot);
                SyncStoneButtons();
            };

            // 右键删除：仅一颗时不提供该选项
            if (stones.Count > 1)
            {
                var deleteItem = new MenuFlyoutItem { Text = "删除石头" };
                deleteItem.Click += (_, _) => StoneHost.Current?.RemoveStone(stone);
                var flyout = new MenuFlyout();
                flyout.Items.Add(deleteItem);
                button.ContextFlyout = flyout;
            }

            _stoneButtons.Add((slot, button));
            StoneSelectorPanel.Children.Add(button);
        }

        var add = new Button
        {
            Content = new FontIcon { Glyph = "\uE710", FontSize = 14 },
            IsEnabled = stones.Count < StoneHost.MaxStones
        };
        ToolTipService.SetToolTip(add, "添加石头");
        add.Click += (_, _) => StoneHost.Current?.AddStone();
        StoneSelectorPanel.Children.Add(add);

        SyncStoneButtons();
    }

    /// <summary>让选择器的高亮与当前选中的石头一致。</summary>
    private void SyncStoneButtons()
    {
        foreach ((int slot, ToggleButton button) in _stoneButtons)
        {
            button.IsChecked = slot == _selectedSlot;
        }
    }

    private void OnStonesChanged()
    {
        // 统一排队处理：避免在按钮点击 / 事件回调过程中就地移除控件
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshStoneSelection();
            RefreshStoneAddButton();
            RebuildStonePage();
        });
    }

    /// <summary>石头数量到上限时置灰石头页的添加按钮。</summary>
    private void RefreshStoneAddButton()
    {
        StonePageAddButton.IsEnabled = StoneHost.Stones.Count < StoneHost.MaxStones;
    }

    // ===== 石头页：只读位置视图 + 条目列表 =====

    /// <summary>重建石头条目列表与位置视图，并重新订阅各石头的状态变化。</summary>
    private void RebuildStonePage()
    {
        foreach (Stone stone in _subscribedStones)
        {
            stone.State.Changed -= OnStoneMapStateChanged;
            stone.MutedChanged -= OnStoneMutedChanged;
        }

        _subscribedStones.Clear();
        StoneListPanel.Children.Clear();

        List<Stone> stones = OrderedStones();
        bool canRemove = stones.Count > 1;
        for (int i = 0; i < stones.Count; i++)
        {
            Stone stone = stones[i];
            stone.State.Changed += OnStoneMapStateChanged;
            stone.MutedChanged += OnStoneMutedChanged;
            _subscribedStones.Add(stone);
            StoneListPanel.Children.Add(BuildStoneRow(stone, i + 1, canRemove));
        }

        UpdateStoneMap(stones);
    }

    /// <summary>构建一条石头条目：名称 + 静音状态 + 静音开关 + 移除。</summary>
    private FrameworkElement BuildStoneRow(Stone stone, int index, bool canRemove)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        name.Children.Add(new TextBlock { Text = $"石头 {index}", VerticalAlignment = VerticalAlignment.Center });
        if (stone.Muted)
        {
            name.Children.Add(new TextBlock { Text = "已静音", Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
        }

        row.Children.Add(name);

        var mute = new ToggleButton
        {
            IsChecked = stone.Muted,
            Content = new FontIcon { Glyph = stone.Muted ? "\uE74F" : "\uE767", FontSize = 14 }
        };
        ToolTipService.SetToolTip(mute, "静音 / 取消静音");
        mute.Click += (_, _) => StoneHost.Current?.ToggleStoneSound(stone);
        Grid.SetColumn(mute, 1);
        row.Children.Add(mute);

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "\uE738", FontSize = 14 },
            IsEnabled = canRemove
        };
        ToolTipService.SetToolTip(remove, canRemove ? "移除这颗石头" : "至少保留一颗石头");
        remove.Click += (_, _) => StoneHost.Current?.RemoveStone(stone);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    /// <summary>把三颗石头画到只读二维视图：圆点是位置，旁边是名称标签。</summary>
    private void UpdateStoneMap(List<Stone> stones)
    {
        StoneMap.Children.Clear();

        var grid = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128));
        var dot = new SolidColorBrush(Color.FromArgb(255, 76, 110, 145));
        var dotMuted = new SolidColorBrush(Color.FromArgb(110, 76, 110, 145));

        StoneMap.Children.Add(new Line { X1 = 0, Y1 = MapHeight / 2, X2 = MapWidth, Y2 = MapHeight / 2, Stroke = grid, StrokeThickness = 1 });
        StoneMap.Children.Add(new Line { X1 = MapWidth / 2, Y1 = 0, X2 = MapWidth / 2, Y2 = MapHeight, Stroke = grid, StrokeThickness = 1 });

        double scaleX = (MapWidth - MapPad * 2) / 2;
        double scaleY = (MapHeight - MapPad * 2) / 2;

        for (int i = 0; i < stones.Count; i++)
        {
            Stone stone = stones[i];
            double px = MapWidth / 2 + stone.State.X * scaleX;
            double py = MapHeight / 2 - stone.State.Y * scaleY;

            const double size = 14;
            var point = new Ellipse { Width = size, Height = size, Fill = stone.Muted ? dotMuted : dot };
            Canvas.SetLeft(point, px - size / 2);
            Canvas.SetTop(point, py - size / 2);
            StoneMap.Children.Add(point);

            var label = new TextBlock
            {
                Text = $"石头 {i + 1}",
                Opacity = stone.Muted ? 0.45 : 0.9
            };
            Canvas.SetLeft(label, px - 22);
            Canvas.SetTop(label, py - 26);
            StoneMap.Children.Add(label);
        }
    }

    /// <summary>任一石头位置变化（桌面拖动或音效页滑块）：刷新二维视图。</summary>
    private void OnStoneMapStateChanged(SpatialOrigin origin) => DispatcherQueue.TryEnqueue(() => UpdateStoneMap(OrderedStones()));

    /// <summary>任一石头静音状态变化：重建条目与视图。</summary>
    private void OnStoneMutedChanged() => DispatcherQueue.TryEnqueue(RebuildStonePage);

    /// <summary>左侧导航切换：在音乐、石头与音效三页之间切换。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        MusicPage.Visibility = tag == "music" ? Visibility.Visible : Visibility.Collapsed;
        StonePage.Visibility = tag == "stone" ? Visibility.Visible : Visibility.Collapsed;
        SoundPage.Visibility = tag == "sound" ? Visibility.Visible : Visibility.Collapsed;
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
