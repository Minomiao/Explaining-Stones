using Microsoft.UI.Xaml;

namespace ExplainingStones;

/// <summary>
/// WinUI 应用对象。只负责加载 App.xaml 中的控件资源，窗口由 WinUiHost 按需创建。
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }
}
