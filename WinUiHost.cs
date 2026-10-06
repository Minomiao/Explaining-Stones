using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using WinUiApplication = Microsoft.UI.Xaml.Application;

namespace ExplainingStones;

/// <summary>
/// 在独立的 STA 线程上承载 WinUI 3。WinUI 的消息循环一个进程只能启动一次，
/// 所以这里延迟到第一次使用时才启动，之后复用同一个线程与窗口。
/// </summary>
internal static class WinUiHost
{
    private static readonly object Gate = new();

    private static DispatcherQueue? _dispatcher;
    private static SettingsWindow? _window;

    /// <summary>唤起（或首次创建）设置窗口。</summary>
    public static void ShowSettings()
    {
        DispatcherQueue? queue = EnsureStarted();
        if (queue is null)
        {
            System.Windows.Forms.MessageBox.Show(
                "WinUI 启动失败，请确认已安装 Windows App Runtime。",
                "Explaining Stones");
            return;
        }

        queue.TryEnqueue(() =>
        {
            _window ??= new SettingsWindow();
            _window.Show();
        });
    }

    private static DispatcherQueue? EnsureStarted()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    WinUiApplication.Start(_ =>
                    {
                        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
                        SynchronizationContext.SetSynchronizationContext(
                            new DispatcherQueueSynchronizationContext(dispatcher));

                        new App();

                        _dispatcher = dispatcher;
                        ready.Set();
                    });
                }
                catch
                {
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "WinUI"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            ready.Wait(TimeSpan.FromSeconds(30));
            return _dispatcher;
        }
    }
}
