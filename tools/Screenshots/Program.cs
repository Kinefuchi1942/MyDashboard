using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyDashboard;
using MyDashboard.Info;
using MyDashboard.Monitoring;
using MyDashboard.Views;

namespace MyDashboard.Screenshots;

/// <summary>
/// 3つのレイアウトに見本データ（ゲーム中を想定）を流し込み、1920×1080 の PNG に保存する。
/// 本体のレイアウトをそのまま使うので、見た目を変えたら実行し直すだけで README の写真が新しくなる。
/// </summary>
public static class Program
{
    private static readonly Random Rng = new(7);   // 毎回同じ絵になるように固定

    [STAThread]
    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : FindImagesFolder();
        Directory.CreateDirectory(outDir);

        // 本体の App.xaml の色・文字スタイルを読み込む（Run はしないので本体のウィンドウは開かない）
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        int code = 0;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Capture("classic", null, outDir);
                await Capture("gauge", null, outDir);
                await Capture("aurora", "night", outDir);
                await Capture("aurora", "day", outDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                code = 1;
            }
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        Dispatcher.Run();
        return code;
    }

    /// <summary>リポジトリの docs\images（このツールの位置から上にたどる）。</summary>
    private static string FindImagesFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MyDashboard.csproj"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "docs", "images");
    }

    private static async Task Capture(string layout, string? theme, string outDir)
    {
        var settings = new AppSettings
        {
            Layout = layout,
            AuroraTheme = theme ?? "night",
            AuroraMotion = false,
            NewsRotateSeconds = 120,
            WeatherPlace = "東京",
        };
        var ctx = new ViewContext(settings);
        IDashboardView view = layout switch
        {
            "gauge" => new GaugeView(ctx),
            "aurora" => new AuroraView(ctx),
            _ => new ClassicView(ctx),
        };
        var host = new ContentControl { Width = 1920, Height = 1080, Content = view };
        var background = view is Control { Background: { } bg } ? bg : ViewContext.Res("Ground");

        // 画面の外に置いたウィンドウで動かす（レイアウトの Loaded やタイマーを本物どおりに動かすため）
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -30000,
            Top = -30000,
            Width = 1920,
            Height = 1080,
            Background = background,
            Content = host,
        };
        window.Show();

        var clock = theme == "day" ? new DateTime(2026, 10, 30, 13, 20, 0) : new DateTime(2026, 10, 30, 21, 48, 0);
        view.UpdateClock(clock);
        view.ShowWeather(SampleWeather(clock, day: theme == "day"));
        view.ShowNews(SampleNews());
        view.ShowMedia(SampleMedia());
        view.SetNotice("");

        // グラフの履歴を埋める（直近90秒ぶん）
        for (int i = 0; i < 90; i++) view.Render(SampleSnapshot(i));
        await Task.Delay(TimeSpan.FromSeconds(5));   // 数字やメーターのアニメーション、色の切り替えが落ち着くまで
        view.UpdateClock(clock);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, 1920, 1080);
            dc.DrawRectangle(background, null, rect);
            dc.DrawRectangle(new VisualBrush(host), null, rect);
        }
        var bmp = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        string name = theme == null ? layout : $"{layout}-{theme}";
        using (var file = File.Create(Path.Combine(outDir, name + ".png"))) png.Save(file);
        Console.WriteLine($"{name}.png");

        window.Close();
    }

    // =========================================================== 見本データ

    private const double GB = 1024d * 1024 * 1024;

    private static double Wave(int i, double center, double amp, double period) =>
        center + amp * Math.Sin(i * 2 * Math.PI / period) + (Rng.NextDouble() - 0.5) * amp * 0.6;

    private static Snapshot SampleSnapshot(int i)
    {
        // 14700K：P コア 8（HT で 16 スレッド）＋ E コア 12
        var threads = new List<double>();
        var isE = new List<bool>();
        for (int t = 0; t < 16; t++) { threads.Add(Math.Clamp(Wave(i + t * 5, t < 4 ? 78 : 52, 18, 23), 3, 100)); isE.Add(false); }
        for (int t = 0; t < 12; t++) { threads.Add(Math.Clamp(Wave(i + t * 3, 24, 14, 17), 1, 100)); isE.Add(true); }

        var cpu = new CpuInfo("Intel Core i7-14700K", Math.Clamp(Wave(i, 44, 8, 30), 0, 100), threads,
            Wave(i, 68, 3, 40), 5300, 5600, Wave(i, 128, 12, 30), isE);
        var gpu = new GpuInfo("NVIDIA GeForce RTX 5070 Ti", Math.Clamp(Wave(i, 94, 3, 20), 0, 99), Wave(i, 70, 2, 50), 82,
            2715, 14000, Wave(i, 268, 14, 25), 11.6 * GB, 15.9 * GB, 1650);

        var frames = new List<double>();
        for (int f = 0; f < 1400; f++) frames.Add(1000.0 / 142 + (Rng.NextDouble() - 0.5) * 1.6 + (f % 230 == 0 ? 6 : 0));
        var fps = new FpsInfo(FpsState.Running, "SampleGame", Wave(i, 142, 6, 15), 118, 7.0, null, frames,
            new DlssInfo(DlssState.Detected, true, true, false, "310.2.1"), "Hardware: Independent Flip");

        var disks = new List<DiskInfo>
        {
            new("Samsung SSD 990 PRO 2TB", Math.Max(0, Wave(i, 42e6, 30e6, 12)), Math.Max(0, Wave(i, 3e6, 2e6, 9)), 61, 44),
            new("WDC WD40EZAZ", 0, 0, 48, 36),
        };
        var power = new PowerInfo(Wave(i, 548, 18, 25), 140, 290, 70, 48, 2.58, 54.1, 31);
        return new Snapshot(cpu, gpu, new MemoryInfo(18.4 * GB, 31.7 * GB), disks,
            new NetworkInfo(Math.Max(0, Wave(i, 2.4e6, 1.8e6, 11)), Math.Max(0, Wave(i, 0.3e6, 0.2e6, 7))),
            fps, false, power);
    }

    private static NewsState SampleNews()
    {
        var now = DateTimeOffset.Now;
        var items = new List<NewsItem>
        {
            new("テックニュース", "新しいグラフィックスドライバーが公開　人気タイトルで平均 8% の性能向上",
                "https://example.com/1", now.AddHours(-1),
                "最新のドライバーでは、複数の人気タイトルで描画性能が向上したほか、フレーム生成の安定性も改善された。配信ソフトとの組み合わせで起きていた不具合も修正されている。"),
            new("ゲームニュース", "冬のセールが今週末から開始　過去最大の 3,000 本以上が対象", "https://example.com/2", now.AddHours(-2)),
            new("サイエンス", "今夜はふたご座流星群が見ごろ　広い範囲で晴れの予報", "https://example.com/3", now.AddHours(-3)),
            new("テックニュース", "小型 PC 向けの新しい省電力 CPU が登場　ファンレス機も", "https://example.com/4", now.AddHours(-4)),
            new("ライフ", "朝晩の冷え込みが強まる　暖房の電気代を抑えるコツ", "https://example.com/5", now.AddHours(-5)),
        };
        for (int k = 0; k < 19; k++)
            items.Add(new("ニュース", $"見本の見出し {k + 6}", $"https://example.com/{k + 6}", now.AddHours(-6 - k)));
        return new NewsState(items, null);
    }

    private static WeatherInfo SampleWeather(DateTime now, bool day)
    {
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour / 3 * 3, 0, 0).AddHours(3);
        int[] codes = day ? new[] { 1, 2, 3, 3, 0, 0 } : new[] { 0, 0, 1, 2, 2, 1 };
        double[] temps = day ? new[] { 20.0, 18, 16, 15, 14, 15 } : new[] { 15.0, 14, 13, 13, 15, 18 };
        int[] rain = day ? new[] { 0, 10, 20, 20, 10, 0 } : new[] { 0, 0, 10, 10, 0, 0 };
        var hourly = Enumerable.Range(0, 6).Select(k =>
        {
            var t = start.AddHours(k * 3);
            return new HourlyForecast(t, temps[k], rain[k], codes[k], t.Hour is >= 6 and < 17);
        }).ToList();
        var sunrise = now.Date.AddHours(6).AddMinutes(4);
        var sunset = now.Date.AddHours(16).AddMinutes(49);
        return new WeatherInfo("東京", day ? 21 : 16, day ? 1 : 0, day, 22, 13, hourly, now, null, sunrise, sunset);
    }

    private static NowPlaying SampleMedia()
    {
        // ジャケットの代わりのグラデーション
        var art = new DrawingImage(new GeometryDrawing(
            new LinearGradientBrush(Color.FromRgb(0x3A, 0x2E, 0x8C), Color.FromRgb(0xE0, 0x6C, 0x9F), 45),
            null, new RectangleGeometry(new Rect(0, 0, 300, 300))));
        art.Freeze();
        return new NowPlaying("Spotify", "見本の曲", "見本のアーティスト", "見本のアルバム", true,
            TimeSpan.FromSeconds(97), TimeSpan.FromSeconds(232), DateTime.Now, 1.0, art, Color.FromRgb(0xB0, 0x5C, 0xC8));
    }
}
