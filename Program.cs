using System;
using System.Threading;
using System.Windows.Forms;

namespace ExplainingStones;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 单实例：只允许一个桌宠窗口，重复启动时直接退出
        using var single = new Mutex(true, @"Local\ExplainingStones.SingleInstance", out bool first);
        if (!first)
        {
            return;
        }

        // 声明 DPI 感知，避免高分屏（如 200% 缩放）下被系统整体位图拉伸变糊
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new PetForm());
    }
}
