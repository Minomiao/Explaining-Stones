using System;
using System.Windows.Forms;

namespace ExplainingStones;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 声明 DPI 感知，避免高分屏（如 200% 缩放）下被系统整体位图拉伸变糊
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new PetForm());
    }
}
