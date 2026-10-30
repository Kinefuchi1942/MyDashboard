using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyDashboard.Info;
using MyDashboard.Monitoring;
using MyDashboard.Views;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace MyDashboard;

/// <summary>
/// ウィンドウ本体。センサー収集・時計・モニター配置・操作を受け持ち、
/// 実際の見た目は差し替え可能なレイアウト（Views フォルダ）に任せる。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>切り替えの順番。settings.json の "layout" にこの名前で保存する。</summary>
    private static readonly string[] LayoutOrder = { "classic", "gauge", "aurora" };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ViewContext _ctx;
    private Collector _collector;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<string, IDashboardView> _views = new();
    private IDashboardView? _view;
    private Snapshot? _last;
    private InfoService _info;
    private NewsState? _lastNews;
    private WeatherInfo? _lastWeather;
    private MediaService _media;
    private NowPlaying? _lastMedia;
    private int _monitorIndex;
    private string? _lastError;

    public MainWindow()
    {
        InitializeComponent();
        _ctx = new ViewContext(_settings);
        _media = CreateMedia();

        ApplyStacking();
        SwitchLayout(_settings.Layout, save: false);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => _view?.UpdateClock(DateTime.Now);
        _clock.Start();

        _collector = CreateCollector();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            // 画面の点灯・消灯を受け取る（消えている間はモニターの消費電力を待機分だけにする）
            var displayState = GUID_CONSOLE_DISPLAY_STATE;
            _powerNotify = RegisterPowerSettingNotification(hwnd, ref displayState, 0);
            _monitorIndex = ResolveMonitor(_settings.Monitor);
            PlaceOnMonitor(_monitorIndex);
        };
        _info = CreateInfo();

        Loaded += (_, _) =>
        {
            _collector.Start();
            _info.Start();
            _media.Start();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        KeyDown += OnKeyPressed;
    }

    // 設定の保存で作り直すことがあるため、古いものからの通知は無視する（_collector などと同じものか確かめる）
    private Collector CreateCollector()
    {
        var c = new Collector(_settings);
        c.Updated += s => Dispatcher.InvokeAsync(() => { if (c == _collector) Render(s); });
        c.Error += msg => Dispatcher.InvokeAsync(() => { if (c == _collector) { _lastError = msg; _view?.SetNotice(msg); } });
        return c;
    }

    private InfoService CreateInfo()
    {
        var info = new InfoService(_settings);
        info.NewsUpdated += n => Dispatcher.InvokeAsync(() => { if (info == _info) { _lastNews = n; _view?.ShowNews(n); } });
        info.WeatherUpdated += w => Dispatcher.InvokeAsync(() => { if (info == _info) { _lastWeather = w; _view?.ShowWeather(w); } });
        return info;
    }

    private MediaService CreateMedia()
    {
        var media = new MediaService(_settings);
        media.Updated += m => Dispatcher.InvokeAsync(() => { if (media == _media) { _lastMedia = m; _view?.ShowMedia(m); } });
        _ctx.Media = media;
        return media;
    }

    private void Render(Snapshot s)
    {
        _last = s;
        _view?.Render(s);
        _view?.SetNotice(NoticeFor(s));
    }

    private string NoticeFor(Snapshot s) =>
        _lastError ?? (s.SensorDriverMissing ? "CPU温度を出すには PawnIO が必要です（README を参照）" : "");

    // =========================================================== レイアウト切り替え

    private void SwitchLayout(string? name, bool save = true)
    {
        name = Array.IndexOf(LayoutOrder, name?.ToLowerInvariant()) >= 0 ? name!.ToLowerInvariant() : "classic";
        Fx.FrameRate = _settings.AnimationFps;

        // 一度作ったレイアウトは使い回す（戻ったときにグラフの履歴が残る）
        if (!_views.TryGetValue(name, out var view))
        {
            view = name switch
            {
                "gauge" => new GaugeView(_ctx),
                "aurora" => new AuroraView(_ctx),
                _ => new ClassicView(_ctx),
            };
            _views[name] = view;
        }

        _view = view;
        Host.Content = view;
        Background = view is Control { Background: { } bg } ? bg : ViewContext.Res("Ground");

        view.UpdateClock(DateTime.Now);
        if (_lastNews != null) view.ShowNews(_lastNews);
        if (_lastWeather != null) view.ShowWeather(_lastWeather);
        view.ShowMedia(_lastMedia);
        if (_last != null)
        {
            view.Render(_last);
            view.SetNotice(NoticeFor(_last));
        }

        LayoutClassicItem.IsChecked = name == "classic";
        LayoutGaugeItem.IsChecked = name == "gauge";
        LayoutAuroraItem.IsChecked = name == "aurora";

        if (save && _settings.Layout != name)
        {
            _settings.Layout = name;
            _settings.Save();
        }
    }

    private void NextLayout()
    {
        int i = Array.IndexOf(LayoutOrder, _settings.Layout);
        SwitchLayout(LayoutOrder[(i + 1 + LayoutOrder.Length) % LayoutOrder.Length]);
    }

    private void OnLayoutMenu(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name }) SwitchLayout(name);
    }

    private void OnNextLayout(object sender, RoutedEventArgs e) => NextLayout();

    /// <summary>
    /// 3つのレイアウトを順に表示して 1920×1080 の PNG に保存し、元のレイアウトに戻す（README の画像用）。
    /// WebView2（Twitch・YouTube の映像）は写らない。
    /// </summary>
    public async Task CaptureLayoutsAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        string original = _settings.Layout;
        foreach (var name in LayoutOrder)
        {
            SwitchLayout(name, save: false);
            await Task.Delay(TimeSpan.FromSeconds(4));   // フェードやグラフが落ち着くまで待つ

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, Host.Width, Host.Height);
                dc.DrawRectangle(Background, null, rect);
                dc.DrawRectangle(new VisualBrush(Host), null, rect);
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Host.Width, (int)Host.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
            png.Save(file);
        }
        SwitchLayout(original, save: false);
    }

    // =========================================================== モニター配置

    private static int ResolveMonitor(int requested)
    {
        var screens = Screen.AllScreens;
        if (requested >= 0 && requested < screens.Length) return requested;
        int sub = Array.FindIndex(screens, s => !s.Primary);
        return sub >= 0 ? sub : 0;
    }

    private void PlaceOnMonitor(int index)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        index = Math.Clamp(index, 0, screens.Length - 1);
        var b = screens[index].Bounds; // 物理ピクセル（PerMonitorV2）
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        WindowState = WindowState.Normal;
        SetWindowPos(hwnd, _settings.AlwaysBehind ? HWND_BOTTOM : HWND_NOTOPMOST, b.X, b.Y, b.Width, b.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            _monitorIndex = ResolveMonitor(_settings.Monitor);
            PlaceOnMonitor(_monitorIndex);
        });

    private void MoveToNextMonitor()
    {
        int count = Screen.AllScreens.Length;
        _monitorIndex = (_monitorIndex + 1) % Math.Max(1, count);
        _settings.Monitor = _monitorIndex;
        _settings.Save();
        PlaceOnMonitor(_monitorIndex);
    }

    // =========================================================== 操作

    private void OnKeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L) NextLayout();
        else if (e.Key == Key.M) MoveToNextMonitor();
        else if (e.Key == Key.B) ToggleBehind();
        else if (e.Key == Key.S) OpenSettingsWindow();
        else if (e.Key == Key.Q && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Close();
    }

    private void OnNextMonitor(object sender, RoutedEventArgs e) => MoveToNextMonitor();
    private void OnToggleBehind(object sender, RoutedEventArgs e) => ToggleBehind();
    private void OnQuit(object sender, RoutedEventArgs e) => Close();

    private void OnOpenSettingsWindow(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    // =========================================================== 設定画面

    private SettingsWindow? _settingsWindow;

    private void OpenSettingsWindow()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var w = new SettingsWindow(_settings);
        if (_settings.AlwaysBehind)
        {
            // 背後モードでは持ち主（このウィンドウ）と一緒に奥へ回されないよう、持ち主なしで同じモニターの中央に出す
            var a = SystemParameters.WorkArea;
            var area = Screen.AllScreens.Length > _monitorIndex ? Screen.AllScreens[_monitorIndex].WorkingArea : default;
            var src = PresentationSource.FromVisual(this)?.CompositionTarget;
            if (src != null && area.Width > 0)
            {
                var tl = src.TransformFromDevice.Transform(new Point(area.Left, area.Top));
                var br = src.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
                a = new Rect(tl, br);
            }
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = a.Left + Math.Max(0, (a.Width - w.Width) / 2);
            w.Top = a.Top + Math.Max(0, (a.Height - w.Height) / 2);
        }
        else w.Owner = this;
        _settingsWindow = w;
        try { if (w.ShowDialog() == true && w.Result != null) ApplySettings(w.Result); }
        finally { _settingsWindow = null; }
    }

    /// <summary>設定画面で保存した内容を、アプリを再起動せずに反映する。変わった部分のサービスだけ作り直す。</summary>
    private void ApplySettings(AppSettings next)
    {
        var old = _settings.Clone();
        _settings.CopyFrom(next);
        _settings.Save();

        // ---- センサー（更新間隔・FPS）。古い PresentMon を止め終えてから新しく始める
        if (!AppSettings.Same(old, _settings, s => new { s.UpdateIntervalMs, s.EnableFps, s.FpsExcludeProcesses }))
        {
            var oldCollector = _collector;
            var newCollector = CreateCollector();
            _collector = newCollector;
            _lastError = null;
            Task.Run(oldCollector.Dispose).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
            {
                if (_collector == newCollector) newCollector.Start();
            }));
        }

        // ---- ニュース・天気
        if (!AppSettings.Same(old, _settings, s => new
            {
                s.EnableNews, s.NewsFeeds, s.NewsRefreshMinutes,
                s.EnableWeather, s.WeatherPlace, s.WeatherLatitude, s.WeatherLongitude,
            }))
        {
            _info.Dispose();
            _lastNews = null;
            _lastWeather = null;
            _info = CreateInfo();
            _info.Start();
        }

        // ---- 再生中の曲
        if (!AppSettings.Same(old, _settings, s => new { s.EnableMedia, s.MediaSpotifyOnly }))
        {
            _media.Dispose();
            _lastMedia = null;
            _media = CreateMedia();
            _media.Start();
        }

        // ---- 画面：各レイアウトは作るときに設定を読むので、作り直す
        _views.Clear();
        SwitchLayout(_settings.Layout, save: false);

        ApplyStacking();
        _monitorIndex = ResolveMonitor(_settings.Monitor);
        PlaceOnMonitor(_monitorIndex);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppSettings.FilePath}\"") { UseShellExecute = true }); }
        catch { }
    }

    // =========================================================== 重なり順（常に背後）

    private void ToggleBehind()
    {
        _settings.AlwaysBehind = !_settings.AlwaysBehind;
        _settings.Save();
        ApplyStacking();
        PlaceOnMonitor(_monitorIndex);
    }

    private void ApplyStacking()
    {
        BehindItem.IsChecked = _settings.AlwaysBehind;
    }

    /// <summary>
    /// 背後モードの仕組み。クリックしても前に出さず（WM_MOUSEACTIVATE で MA_NOACTIVATE）、
    /// 何かの拍子に前へ出されそうになっても重なり順を一番後ろに書き換える（WM_WINDOWPOSCHANGING で HWND_BOTTOM）。
    /// クリック自体はアプリに届くので、右クリックメニューやニュースのダブルクリックはそのまま使える。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING：GUID（16）・データ長（4）のあとに 0＝消灯 / 1＝点灯 / 2＝暗くなった
            if (Marshal.PtrToStructure<Guid>(lParam) == GUID_CONSOLE_DISPLAY_STATE)
                PowerMeter.DisplayOn = Marshal.ReadInt32(lParam, 20) != 0;
        }
        if (!_settings.AlwaysBehind) return IntPtr.Zero;
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0 && pos.hwndInsertAfter != HWND_BOTTOM)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _clock.Stop();
        _collector.Dispose();
        _info.Dispose();
        _media.Dispose();
        PowerMeter.Shared.Save();
        if (_powerNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotify);
        base.OnClosed(e);
    }

    // =========================================================== Win32

    private IntPtr _powerNotify;
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    [DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int MA_NOACTIVATE = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
