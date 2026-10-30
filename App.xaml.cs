using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MyDashboard;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 二重起動を防ぐ（PresentMon セッションが衝突するため）
        _single = new Mutex(true, @"Global\MyDashboard.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("MyDashboard はすでに起動しています。", "MyDashboard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("予期しないエラーで終了します：\n" + args.Exception.Message, "MyDashboard",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };

        base.OnStartup(e);
        new MainWindow().Show();
        WatchQuitRequest();
        WatchScreenshotRequest();
    }

    /// <summary>
    /// exe の隣に screenshot.request が置かれたら、3つのレイアウトの画面を screenshots フォルダに PNG で保存する。
    /// quit.request と同じく、通常権限の側から管理者権限のアプリに頼むための入口。
    /// </summary>
    private void WatchScreenshotRequest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "screenshot.request");
        try { File.Delete(path); } catch { }

        bool busy = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            if (busy || !File.Exists(path)) return;
            busy = true;
            try { File.Delete(path); } catch { }
            try
            {
                if (MainWindow is MainWindow w)
                    await w.CaptureLayoutsAsync(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            }
            catch { }
            busy = false;
        };
        timer.Start();
    }

    /// <summary>
    /// exe の隣に quit.request が置かれたら、Ctrl+Q と同じように自分から終了する。
    /// 管理者権限で動いているため、通常権限の側（スクリプトなど）から UAC なしで止めるための入口。
    /// </summary>
    private void WatchQuitRequest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "quit.request");
        try { File.Delete(path); } catch { }   // 前回の残りで即終了しないように

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(path)) return;
            timer.Stop();
            try { File.Delete(path); } catch { }
            MainWindow?.Close();
            Shutdown();
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
