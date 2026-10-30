using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MyDashboard.Info;
using MyDashboard.Monitoring;

namespace MyDashboard.Views;

/// <summary>差し替え可能な画面（レイアウト）が実装する共通の口。</summary>
public interface IDashboardView
{
    /// <summary>センサーの新しい値で画面を更新する（UIスレッドで呼ばれる）。</summary>
    void Render(Snapshot s);

    /// <summary>1秒ごとに呼ばれる。時計などの更新用。</summary>
    void UpdateClock(DateTime now);

    /// <summary>画面上部に出す注意書き（PawnIO 未導入、エラーなど）。空文字なら非表示。</summary>
    void SetNotice(string text);

    /// <summary>ニュースの見出しが更新されたとき。ニュースを出さないレイアウトは何もしない。</summary>
    void ShowNews(NewsState news) { }

    /// <summary>天気が更新されたとき。天気を出さないレイアウトは何もしない。</summary>
    void ShowWeather(WeatherInfo weather) { }

    /// <summary>再生中の曲が変わったとき（null は何も再生していない）。音楽を出さないレイアウトは何もしない。</summary>
    void ShowMedia(NowPlaying? media) { }
}

/// <summary>各レイアウトで共有する設定・色・しきい値判定。</summary>
public sealed class ViewContext
{
    public AppSettings Settings { get; }

    /// <summary>再生・曲送りボタン用。</summary>
    public MediaService? Media { get; set; }

    public ViewContext(AppSettings settings) => Settings = settings;

    /// <summary>グラフに保持する点の数（historySeconds ÷ 更新間隔）。</summary>
    public int HistoryPoints => Math.Max(20, Settings.HistorySeconds * 1000 / Settings.UpdateIntervalMs);

    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    public static Color ResColor(string key) => (Color)Application.Current.FindResource(key);

    public Brush LoadBrush(double? load, Brush normal) =>
        load >= Settings.LoadCrit ? Res("Crit") : load >= Settings.LoadWarn ? Res("Warn") : normal;

    public Brush TempBrush(double? t, double warn, double crit, Brush normal) =>
        t >= crit ? Res("Crit") : t >= warn ? Res("Warn") : normal;

    public Brush CpuTempBrush(double? t, Brush normal) => TempBrush(t, Settings.CpuTempWarn, Settings.CpuTempCrit, normal);
    public Brush GpuTempBrush(double? t, Brush normal) => TempBrush(t, Settings.GpuTempWarn, Settings.GpuTempCrit, normal);

    /// <summary>メモリ・VRAM の使用率用（settings.json の memWarn / memCrit）。</summary>
    public Brush UsageBrush(double pct, Brush normal) =>
        pct >= Settings.MemCrit ? Res("Crit") : pct >= Settings.MemWarn ? Res("Warn") : normal;
}

/// <summary>数値の表示形式をそろえる。</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");

    public static string Num(double? v) => v is { } x ? Math.Round(x).ToString("0", Inv) : "--";
    public static string Temp(double? v) => v is { } x ? $"{x:0}°C" : "--";
    public static string Gb(double bytes) => (bytes / 1024 / 1024 / 1024).ToString("0.0", Inv);
    public static string Ghz(double? mhz) => mhz is { } c ? (c / 1000).ToString("0.00", Inv) + " GHz" : "--";
    public static string Mhz(double? mhz) => mhz is { } c ? c.ToString("#,0", Inv) + " MHz" : "--";
    public static string Watt(double? w) => w is { } p ? $"{p:0} W" : "--";
    public static string Ms(double? ms) => ms is { } v ? v.ToString("0.0", Inv) + " ms" : "--";
    public static string Rpm(double? rpm) => rpm is { } f ? (f < 1 ? "停止" : f.ToString("#,0", Inv) + " rpm") : "--";
    public static string Time(DateTime t) => t.ToString("HH:mm");
    public static string Date(DateTime t) => t.ToString("M月d日（ddd）", Ja);

    public static string Bytes(double? bps)
    {
        if (bps is not { } v) return "--";
        if (v >= 1024 * 1024 * 1024) return (v / 1024 / 1024 / 1024).ToString("0.00", Inv) + " GB/s";
        if (v >= 1024 * 1024) return (v / 1024 / 1024).ToString(v >= 100 * 1024 * 1024 ? "0" : "0.0", Inv) + " MB/s";
        return (v / 1024).ToString("0", Inv) + " KB/s";
    }

    /// <summary>バイト/秒を数値と単位に分けて返す（MB/s 単位の数字だけ欲しいとき用）。</summary>
    public static (string Value, string Unit) BytesSplit(double bps)
    {
        var s = Bytes(bps);
        int i = s.LastIndexOf(' ');
        return (s[..i], s[(i + 1)..]);
    }

    public static string Bits(double bytesPerSec)
    {
        var (v, u) = BitsSplit(bytesPerSec);
        return v + " " + u;
    }

    public static (string Value, string Unit) BitsSplit(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        if (bits >= 1e9) return ((bits / 1e9).ToString("0.00", Inv), "Gbps");
        if (bits >= 1e6) return ((bits / 1e6).ToString(bits >= 1e8 ? "0" : "0.0", Inv), "Mbps");
        return ((bits / 1e3).ToString("0", Inv), "kbps");
    }

    /// <summary>記事の新しさ（例：たった今、12分前、3時間前、10/24）。</summary>
    public static string Age(DateTimeOffset? published)
    {
        if (published is not { } p) return "";
        var d = DateTimeOffset.Now - p;
        if (d < TimeSpan.FromMinutes(1)) return "たった今";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}分前";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours}時間前";
        return p.ToLocalTime().ToString("M/d", Inv);
    }

    /// <summary>プレイ時間（例：1時間12分、8分）。</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}時間{t.Minutes}分" : $"{Math.Max(0, t.Minutes)}分";
}

/// <summary>
/// ニュース見出しの順送り。一覧が更新されても、表示中の見出しが残っていればその位置を保つ。
/// </summary>
public sealed class NewsRotator
{
    private IReadOnlyList<NewsItem> _items = Array.Empty<NewsItem>();
    public int Index { get; private set; }
    public int Count => _items.Count;
    public NewsItem? Current => _items.Count > 0 ? _items[Index] : null;

    public void Update(IReadOnlyList<NewsItem> items)
    {
        var keep = Current?.Link;
        _items = items;
        int i = keep == null ? -1 : FindIndex(items, keep);
        Index = i >= 0 ? i : 0;
    }

    public void Next()
    {
        if (_items.Count > 0) Index = (Index + 1) % _items.Count;
    }

    public void Previous()
    {
        if (_items.Count > 0) Index = (Index - 1 + _items.Count) % _items.Count;
    }

    /// <summary>現在の次から n 件。</summary>
    public IEnumerable<NewsItem> Upcoming(int n)
    {
        for (int k = 1; k <= n && k < _items.Count; k++)
            yield return _items[(Index + k) % _items.Count];
    }

    private static int FindIndex(IReadOnlyList<NewsItem> items, string link)
    {
        for (int i = 0; i < items.Count; i++) if (items[i].Link == link) return i;
        return -1;
    }

    /// <summary>
    /// 記事をブラウザで開く。このアプリは管理者権限で動くため、explorer 経由で通常権限のブラウザに渡す。
    /// </summary>
    public static void Open(NewsItem? item)
    {
        if (item == null || !Uri.TryCreate(item.Link, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        // 背後モードではこのアプリが前面にならないため、開いたブラウザが前に出られるよう許可を渡しておく
        AllowSetForegroundWindow(ASFW_ANY);
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + uri.AbsoluteUri + "\""); }
        catch { }
    }

    private const int ASFW_ANY = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>
/// 見出しを一定間隔で送るタイマー。画面に出ているレイアウトのときだけ動く。
/// show には「いまの見出しを画面に反映する」処理を渡す（引数は切り替えアニメーションをするか）。
/// </summary>
public sealed class NewsTicker
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly Action<bool> _show;
    private bool _loaded;
    private bool _paused;

    public NewsRotator Rotator { get; } = new();
    public string? Message { get; private set; } = "ニュースを読み込んでいます";
    public TimeSpan Interval => _timer.Interval;

    public NewsTicker(FrameworkElement owner, int seconds, Action<bool> show)
    {
        _show = show;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += (_, _) =>
        {
            if (Rotator.Count < 2) return;
            Rotator.Next();
            _show(true);
        };
        owner.Loaded += (_, _) => { _loaded = true; Restart(); _show(false); };
        owner.Unloaded += (_, _) => { _loaded = false; _timer.Stop(); };
    }

    public void Update(NewsState state)
    {
        bool first = Rotator.Count == 0;
        Rotator.Update(state.Items);
        Message = state.Message;
        if (first) Restart();
        _show(first && Rotator.Count > 0);
    }

    /// <summary>ダブルクリックなどで次へ進めたいとき用（タイマーも最初から数え直す）。</summary>
    public void Restart()
    {
        _timer.Stop();
        if (_loaded && !_paused && Rotator.Count > 1) _timer.Start();
    }

    /// <summary>マウスがカードの上にある間など、自動送りを止める。再開時は最初から数え直す。</summary>
    public bool Paused
    {
        get => _paused;
        set { _paused = value; Restart(); }
    }

    /// <summary>前へ（-1）・次へ（+1）のボタン用。タイマーも最初から数え直す。</summary>
    public void Step(int direction)
    {
        if (Rotator.Count < 2) return;
        if (direction < 0) Rotator.Previous(); else Rotator.Next();
        Restart();
        _show(true);
    }
}

/// <summary>小さな演出の共通処理。</summary>
public static class Fx
{
    /// <summary>アニメーションのコマ数（settings.json の animationFps。レイアウトを作る前に MainWindow が入れる）。</summary>
    public static int FrameRate { get; set; } = 20;

    /// <summary>アニメーションを FrameRate のコマ数に抑える（制限しないとモニターのリフレッシュレートで描き直し、GPU を食う）。</summary>
    public static T Limit<T>(T timeline) where T : System.Windows.Media.Animation.Timeline
    {
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(timeline, FrameRate);
        return timeline;
    }

    /// <summary>要素をふわっと出す（切り替え時）。</summary>
    public static void FadeIn(UIElement e, double ms = 450)
    {
        var a = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
        e.BeginAnimation(UIElement.OpacityProperty, Limit(a));
    }

    /// <summary>ページ位置を示す点（現在だけ横長）を作り直す。</summary>
    public static void Dots(System.Windows.Controls.Panel host, int count, int index, Color on, Color off, double size = 8, int max = 12)
    {
        host.Children.Clear();
        if (count < 2) return;
        // 件数が多いときは現在位置の周辺だけを出す
        int shown = Math.Min(count, max);
        int start = Math.Clamp(index - shown / 2, 0, count - shown);
        for (int i = start; i < start + shown; i++)
        {
            bool cur = i == index;
            host.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = cur ? size * 2.8 : size,
                Height = size,
                RadiusX = size / 2,
                RadiusY = size / 2,
                Margin = new Thickness(0, 0, size * 0.8, 0),
                Fill = new SolidColorBrush(cur ? on : off),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }
}
