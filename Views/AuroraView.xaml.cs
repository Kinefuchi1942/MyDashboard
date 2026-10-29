using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDashboard.Controls;
using MyDashboard.Info;
using MyDashboard.Monitoring;

namespace MyDashboard.Views;

/// <summary>
/// オーロラレイアウト（案C・日常使い版）。
/// ・主役は時計・天気・ニュース（FPS は出さない。計器盤・標準レイアウトで見る）
/// ・背景の光は負荷に連動（CPU＝左下の青緑、GPU＝右上の紫、高温で右下が赤く）
/// ・PC の状態は下段の1本のバーに控えめにまとめる
/// ・色は時間帯（朝・昼・夕方・夜）で変わる。配色は AuroraTheme にまとめてある
/// ・ニュースカードは Twitch の表示と切り替えられる（AuroraView.Twitch.cs）
/// </summary>
public partial class AuroraView : UserControl, IDashboardView
{
    private readonly ViewContext _ctx;

    // 時間帯テーマで色が変わるブラシ（XAML の DynamicResource と同じもの）
    private readonly SolidColorBrush _text, _t85, _t80, _t70, _t60, _t50, _t40, _glass, _line, _accent, _cpu, _gpu, _rain;
    private readonly Brush _cpuValue, _gpuValue, _plain, _side;
    private readonly LinearGradientBrush _sky;
    private readonly (Ellipse Shape, RadialGradientBrush Fill)[] _skyGlows;
    private AuroraPalette? _palette;
    private DateTime _themeCheckedAt;
    private DateTime? _sunrise, _sunset;
    /// <summary>負荷に連動する光の強さの倍率（明るいテーマでは弱める）。</summary>
    private double _loadGlow = 1;

    private readonly NewsTicker _news;
    private const int NewsNextCount = 4;
    private const int NewsPreviewMaxLines = 5;
    private const double NewsPreviewLine = 44;
    /// <summary>再生中カードが出ている間、時計・日付・天気を上に寄せる量（1920×1080 基準）。</summary>
    private const double MediaLift = 50;

    // 再生中の曲
    private readonly Brush _mediaOn = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
    private static readonly Color MusicGlowDefault = Color.FromRgb(0x7F, 0xB2, 0xF0);
    /// <summary>一時停止のままこの時間が過ぎたらカードを隠す。</summary>
    private static readonly TimeSpan HidePausedAfter = TimeSpan.FromMinutes(5);
    private NowPlaying? _media;
    private DateTime? _pausedSince;
    private Color? _glowColor;

    // ゲーム中かどうか（Twitch をゲーム中だけ低画質にするのに使う）
    private DateTime _sessionLastSeen;
    private bool _playing;

    public AuroraView(ViewContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        // 色を後から変えられるよう、XAML のブラシを同じ色の新しいブラシに置き換える
        _text = ThemeBrush("AText");
        _t85 = ThemeBrush("AT85");
        _t80 = ThemeBrush("AT80");
        _t70 = ThemeBrush("AT70");
        _t60 = ThemeBrush("AT60");
        _t50 = ThemeBrush("AT50");
        _t40 = ThemeBrush("AT40");
        _glass = ThemeBrush("AGlass");
        _line = ThemeBrush("ALine");
        _accent = ThemeBrush("AAccent");
        _cpu = ThemeBrush("ACpu");
        _gpu = ThemeBrush("AGpu");
        _rain = ThemeBrush("ARain");
        _cpuValue = _cpu;
        _gpuValue = _gpu;
        _plain = _text;
        _side = _t50;

        _sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        _sky.GradientStops.Add(new GradientStop(Colors.Black, 0));
        _sky.GradientStops.Add(new GradientStop(Colors.Black, 0.5));
        _sky.GradientStops.Add(new GradientStop(Colors.Black, 1));
        Background = _sky;
        _skyGlows = new[] { SkyTopLeft, SkyTopRight, SkyBottomLeft, SkyBottomRight }.Select(e =>
        {
            var fill = new RadialGradientBrush();
            fill.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            fill.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
            fill.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            e.Fill = fill;
            return (e, fill);
        }).ToArray();
        InitThemePicker();
        UpdateTheme(DateTime.Now, force: true);
        foreach (var chart in new[] { CpuChart, GpuChart, MemChart, VramChart, NetChart })
            chart.SetCapacity(Math.Max(20, ctx.HistoryPoints / 2));
        NetChart.Max = 0; // ネットワークは自動スケール

        _news = new NewsTicker(this, ctx.Settings.NewsRotateSeconds, ShowCurrentNews);
        NewsCard.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) NewsRotator.Open(_news.Rotator.Current);
        };
        // マウスがカードの上にある間は自動で次へ進めない
        NewsCard.MouseEnter += (_, _) => _news.Paused = true;
        NewsCard.MouseLeave += (_, _) => _news.Paused = false;
        foreach (var (button, step) in new[] { (NewsPrev, -1), (NewsNextButton, 1) })
        {
            // ボタンの連打がカードのダブルクリック（記事を開く）にならないよう、押下もここで止める
            button.MouseLeftButtonDown += (_, e) => e.Handled = true;
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; _news.Step(step); };
            button.MouseEnter += (s, _) => ((Border)s).Background = _line;
            button.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;
        }

        if (!ctx.Settings.EnableNews) NewsCard.Visibility = Visibility.Collapsed;
        if (!ctx.Settings.EnableWeather) WeatherCard.Visibility = Visibility.Collapsed;
        Place.Text = ctx.Settings.WeatherPlace;

        MediaPrev.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = _ctx.Media?.PreviousAsync(); };
        MediaPlayPause.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = _ctx.Media?.PlayPauseAsync(); };
        MediaNext.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = _ctx.Media?.NextAsync(); };
        foreach (var button in new[] { MediaPrev, MediaPlayPause, MediaNext })
        {
            // マウスを乗せたときの背景（Style のトリガーで ALine を使うと WPF がブラシを凍結し、色を変えられなくなる）
            button.MouseEnter += (s, _) => ((Border)s).Background = _line;
            button.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;
        }
        MediaTrack.SizeChanged += (_, _) => UpdateMediaProgress(DateTime.Now);
        SetMusicGlowColor(MusicGlowDefault);

        InitTwitch();

        // 画面に出ているあいだだけ背景を動かす。別のレイアウトに切り替えたら Twitch は止めてニュースに戻す
        Loaded += (_, _) => StartMotion();
        Unloaded += (_, _) =>
        {
            StopMotion();
            SetTwitchMode(TwitchMode.Off);
        };
    }

    // =========================================================== 背景の動き

    private void StartMotion()
    {
        if (!_ctx.Settings.AuroraMotion) return;
        Drift(BlobCpu, 140, 90, 29, 37);
        Drift(BlobGpu, 160, 110, 41, 33);
        Drift(BlobHeat, 120, 80, 35, 27);
        Drift(BlobMusic, 90, 60, 31, 39);
    }

    private void StopMotion()
    {
        foreach (var blob in new[] { BlobCpu, BlobGpu, BlobHeat, BlobMusic })
        {
            if (blob.RenderTransform is TranslateTransform t)
            {
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }
    }

    /// <summary>光の塊をゆっくり漂わせる。描画負荷を抑えるため 1秒20コマに制限する。</summary>
    private static void Drift(Ellipse blob, double dx, double dy, double secondsX, double secondsY)
    {
        if (blob.RenderTransform is not TranslateTransform t) return;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var ax = new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(secondsX))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        var ay = new DoubleAnimation(-dy, dy, TimeSpan.FromSeconds(secondsY))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        Timeline.SetDesiredFrameRate(ax, 20);
        Timeline.SetDesiredFrameRate(ay, 20);
        t.BeginAnimation(TranslateTransform.XProperty, ax);
        t.BeginAnimation(TranslateTransform.YProperty, ay);
    }

    /// <summary>光の強さを2秒かけて変える（急な点滅を避ける）。</summary>
    private static void Glow(UIElement blob, double opacity)
    {
        var a = new DoubleAnimation(Math.Clamp(opacity, 0, 1), TimeSpan.FromSeconds(2))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(a, 20);
        blob.BeginAnimation(OpacityProperty, a);
    }

    // =========================================================== 時間帯テーマ

    /// <remarks>
    /// このブラシは Style の Setter や Trigger から DynamicResource で参照しないこと。
    /// WPF は Style 経由で取ったブラシを凍結するため、色のアニメーションで例外になる。
    /// </remarks>
    private SolidColorBrush ThemeBrush(string key)
    {
        var b = new SolidColorBrush(((SolidColorBrush)Resources[key]).Color);
        Resources[key] = b;
        return b;
    }

    /// <summary>いまの時刻に合う配色にする。10秒ごとに確かめ、境目の30分でゆっくり移り変わる。</summary>
    private void UpdateTheme(DateTime now, bool force = false)
    {
        if (!force && (now - _themeCheckedAt).Duration() < TimeSpan.FromSeconds(10)) return;
        _themeCheckedAt = now;

        var p = AuroraTheme.Fixed(_ctx.Settings.AuroraTheme) ?? AuroraTheme.At(now, _sunrise, _sunset);
        UpdateThemePicker(p);
        var old = _palette;
        if (p == old) return;
        _palette = p;
        bool animate = old != null;

        // ---- 背景（少しずつ変わるのでそのまま入れる）
        _sky.GradientStops[0].Color = p.SkyTop;
        _sky.GradientStops[1].Color = p.SkyMid;
        _sky.GradientStops[2].Color = p.SkyBottom;
        var glows = new[] { p.TopLeft, p.TopRight, p.BottomLeft, p.BottomRight };
        for (int i = 0; i < _skyGlows.Length; i++)
        {
            var (shape, fill) = _skyGlows[i];
            var c = glows[i].Color;
            fill.GradientStops[0].Color = AuroraTheme.Alpha(c, 0xCC);
            fill.GradientStops[1].Color = AuroraTheme.Alpha(c, 0x44);
            fill.GradientStops[2].Color = AuroraTheme.Alpha(c, 0x00);
            shape.Opacity = glows[i].Opacity;
        }
        Vignette.Opacity = p.Vignette;
        DimLayer.Opacity = Math.Clamp(p.Dim + p.Sun * (1 - _ctx.Settings.AuroraDayBrightness), 0, 0.6);
        _loadGlow = p.LoadGlow;

        // ---- 文字・カード（変わるときは3秒かけて色を移す）
        Fade(_glass, p.Glass, animate);
        Fade(_line, p.Line, animate);
        if (old != null && old.Text == p.Text && old.Accent == p.Accent && old.CpuValue == p.CpuValue
            && old.GpuValue == p.GpuValue && old.Rain == p.Rain && old.Light == p.Light) return;

        Fade(_text, p.Text, animate);
        Fade(_t85, AuroraTheme.Alpha(p.Text, 0xD9), animate);
        Fade(_t80, AuroraTheme.Alpha(p.Text, 0xCC), animate);
        Fade(_t70, AuroraTheme.Alpha(p.Text, 0xB3), animate);
        Fade(_t60, AuroraTheme.Alpha(p.Text, 0x99), animate);
        Fade(_t50, AuroraTheme.Alpha(p.Text, 0x80), animate);
        Fade(_t40, AuroraTheme.Alpha(p.Text, 0x66), animate);
        Fade(_accent, p.Accent, animate);
        Fade(_cpu, p.CpuValue, animate);
        Fade(_gpu, p.GpuValue, animate);
        Fade(_rain, p.Rain, animate);

        CpuChart.Stroke = p.CpuValue;
        GpuChart.Stroke = p.GpuValue;
        MemChart.Stroke = VramChart.Stroke = NetChart.Stroke = p.Text;
        foreach (var chart in new[] { CpuChart, GpuChart, MemChart, VramChart, NetChart })
            chart.SecondStroke = AuroraTheme.Alpha(p.Text, 0x66);
        foreach (var icon in new[] { NowIcon, Hour0Icon, Hour1Icon, Hour2Icon, Hour3Icon, Hour4Icon, Hour5Icon })
            icon.OnLight = p.Light;
        if (old != null) ShowCurrentNews(false); // 送りの点の色を合わせる
    }

    private Border[] ThemeButtons => new[] { ThemeMorning, ThemeDay, ThemeEvening, ThemeNight };

    /// <summary>右端のテーマ切替。クリックでそのテーマに固定し、固定中のものをもう一度押すと自動（時間帯）に戻す。</summary>
    private void InitThemePicker()
    {
        foreach (var button in ThemeButtons)
        {
            button.MouseLeftButtonDown += (_, e) => e.Handled = true;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                var name = (string)((Border)s).Tag;
                _ctx.Settings.AuroraTheme = _ctx.Settings.AuroraTheme == name ? "auto" : name;
                _ctx.Settings.Save();
                UpdateTheme(DateTime.Now, force: true);
            };
            button.MouseEnter += (_, _) => UpdateThemePicker(_palette);
            button.MouseLeave += (_, _) => UpdateThemePicker(_palette);
        }
    }

    /// <summary>いまのテーマの四角を枠で囲み、ほかは薄くする。自動のときは上に「自動」と出す。</summary>
    private void UpdateThemePicker(AuroraPalette? p)
    {
        if (p == null) return;
        bool auto = AuroraTheme.Fixed(_ctx.Settings.AuroraTheme) == null;
        ThemeAuto.Visibility = auto ? Visibility.Visible : Visibility.Hidden;
        var ring = new SolidColorBrush(AuroraTheme.Alpha(p.Text, 0xD9));
        foreach (var button in ThemeButtons)
        {
            bool on = (string)button.Tag == p.Name;
            button.BorderBrush = on ? ring : Brushes.Transparent;
            button.Opacity = on ? 1 : button.IsMouseOver ? 0.85 : 0.55;
        }
    }

    private static void Fade(SolidColorBrush brush, Color to, bool animate)
    {
        if (brush.IsFrozen) return; // 念のため（凍結されたブラシは変えられない）
        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        var a = new ColorAnimation(to, TimeSpan.FromSeconds(3))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(a, 20);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, a);
    }

    // =========================================================== 時計

    public void SetNotice(string text) => Notice.Text = text;

    public void UpdateClock(DateTime now)
    {
        UpdateTheme(now);
        ClockText.Text = Fmt.Time(now);
        DateText.Text = Fmt.Date(now);
        TickTwitch(now);

        if (_media != null)
        {
            if (MediaCard.Visibility == Visibility.Visible && _pausedSince is { } since && now - since > HidePausedAfter)
                SetMediaVisible(false);
            else
                UpdateMediaProgress(now);
        }
    }

    // =========================================================== PC の状態

    public void Render(Snapshot s)
    {
        var settings = _ctx.Settings;

        // ---- 背景：負荷と温度に連動
        double cpuLoad = s.Cpu?.Load ?? 0, gpuLoad = s.Gpu?.Load ?? 0;
        Glow(BlobCpu, (0.25 + 0.75 * cpuLoad / 100) * _loadGlow);
        Glow(BlobGpu, (0.25 + 0.75 * gpuLoad / 100) * _loadGlow);
        double heat = Math.Max(
            Heat(s.Cpu?.Temperature, settings.CpuTempWarn, settings.CpuTempCrit),
            Heat(s.Gpu?.Temperature, settings.GpuTempWarn, settings.GpuTempCrit));
        Glow(BlobHeat, heat * 0.9);

        // ---- ゲーム中かどうか
        UpdateSession(s.Fps);
        UpdateTwitchGame();

        // ---- 下段のバー
        if (s.Cpu is { } cpu)
        {
            CpuValue.Text = $"{Fmt.Num(cpu.Load)}%";
            CpuValue.Foreground = _ctx.LoadBrush(cpu.Load, _cpuValue);
            CpuSide.Text = Fmt.Temp(cpu.Temperature);
            CpuSide.Foreground = _ctx.CpuTempBrush(cpu.Temperature, _side);
            CpuChart.Push(cpu.Load);
        }
        if (s.Gpu is { } gpu)
        {
            GpuValue.Text = $"{Fmt.Num(gpu.Load)}%";
            GpuValue.Foreground = _ctx.LoadBrush(gpu.Load, _gpuValue);
            GpuSide.Text = Fmt.Temp(gpu.Temperature);
            GpuSide.Foreground = _ctx.GpuTempBrush(gpu.Temperature, _side);
            GpuChart.Push(gpu.Load);

            if (gpu.VramUsedBytes is { } vu && gpu.VramTotalBytes is { } vt && vt > 0)
            {
                double pct = vu / vt * 100;
                VramValue.Text = $"{pct:0}%";
                VramValue.Foreground = _ctx.UsageBrush(pct, _plain);
                VramSide.Text = $"{Fmt.Gb(vu)} GB";
                VramChart.Push(pct);
            }
        }
        if (s.Memory.TotalBytes > 0)
        {
            double pct = s.Memory.UsedBytes / s.Memory.TotalBytes * 100;
            MemValue.Text = $"{pct:0}%";
            MemValue.Foreground = _ctx.UsageBrush(pct, _plain);
            MemSide.Text = $"{Fmt.Gb(s.Memory.UsedBytes)} GB";
            MemChart.Push(pct);
        }

        NetValue.Text = Fmt.Bits(s.Network.DownBps);
        NetSide.Text = "↑ " + Fmt.Bits(s.Network.UpBps);
        NetChart.Push(s.Network.DownBps, s.Network.UpBps);
    }

    /// <summary>注意温度の10℃手前から赤みを出し始め、危険温度で最大にする（0〜1）。</summary>
    private static double Heat(double? temp, double warn, double crit)
    {
        if (temp is not { } t) return 0;
        double start = warn - 10;
        return Math.Clamp((t - start) / Math.Max(1, crit - start), 0, 1);
    }

    /// <summary>FPS が計測できている間をゲーム中とみなす。</summary>
    private void UpdateSession(FpsInfo f)
    {
        var now = DateTime.Now;
        if (f.State == FpsState.Running && f.Application != null)
        {
            _sessionLastSeen = now;
            _playing = true;
        }
        else if (_playing && now - _sessionLastSeen > TimeSpan.FromSeconds(10))
        {
            // ロード画面などで一瞬止まってもゲーム中のまま扱うよう、10秒待ってから解除する
            _playing = false;
        }
    }

    // =========================================================== 天気

    public void ShowWeather(WeatherInfo w)
    {
        // 日の出・日の入りが分かったら、時間帯の境目をそれに合わせる
        if (w.Sunrise != null || w.Sunset != null)
        {
            _sunrise = w.Sunrise ?? _sunrise;
            _sunset = w.Sunset ?? _sunset;
            UpdateTheme(DateTime.Now, force: true);
        }

        if (w.Error != null && w.Temperature == null)
        {
            WeatherBody.Visibility = Visibility.Hidden;
            WeatherMessage.Visibility = Visibility.Visible;
            WeatherMessage.Text = w.Error;
            return;
        }
        WeatherBody.Visibility = Visibility.Visible;
        WeatherMessage.Visibility = Visibility.Collapsed;

        NowIcon.Code = w.Code;
        NowIcon.IsDay = w.IsDay;
        NowTemp.Text = Deg(w.Temperature);
        WideTemp.Text = Deg(w.Temperature);
        NowText.Text = WeatherCodes.Text(w.Code);
        NowRange.Text = $"{Deg(w.TodayMax)} / {Deg(w.TodayMin)}";
        Place.Text = w.Place;

        var cells = new[]
        {
            (Hour0Time, Hour0Icon, Hour0Temp, Hour0Rain), (Hour1Time, Hour1Icon, Hour1Temp, Hour1Rain),
            (Hour2Time, Hour2Icon, Hour2Temp, Hour2Rain), (Hour3Time, Hour3Icon, Hour3Temp, Hour3Rain),
            (Hour4Time, Hour4Icon, Hour4Temp, Hour4Rain), (Hour5Time, Hour5Icon, Hour5Temp, Hour5Rain),
        };
        for (int i = 0; i < cells.Length; i++)
        {
            var (time, icon, temp, rain) = cells[i];
            if (i < w.Hourly.Count)
            {
                var h = w.Hourly[i];
                time.Text = $"{h.Time.Hour}時";
                icon.Code = h.Code;
                icon.IsDay = h.IsDay;
                temp.Text = Deg(h.Temperature);
                rain.Text = h.PrecipitationProbability is { } p ? $"{p:0}%" : "";
            }
            else
            {
                time.Text = temp.Text = rain.Text = "";
                icon.Code = null;
            }
        }
    }

    private static string Deg(double? t) => t is { } v ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "°" : "--";

    // =========================================================== ニュース

    public void ShowNews(NewsState news) => _news.Update(news);

    private void ShowCurrentNews(bool animate)
    {
        var r = _news.Rotator;
        var item = r.Current;
        NewsNext.Children.Clear();

        if (item == null)
        {
            NewsSource.Text = "ニュース";
            NewsAge.Text = "";
            NewsTitle.Text = _news.Message ?? "";
            NewsTitle.Foreground = _side;
            NewsDots.Children.Clear();
            NewsCount.Text = "";
            NewsNav.Visibility = Visibility.Collapsed;
            NewsPreviewBox.Visibility = Visibility.Collapsed;
            return;
        }

        NewsSource.Text = item.Source;
        NewsAge.Text = Fmt.Age(item.Published);
        NewsTitle.Text = item.Title;
        NewsTitle.Foreground = _plain;
        NewsPreview.Text = item.Summary;
        NewsPreviewBox.Visibility = item.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 次の見出しを小さく（再生中カードがあるときは2件、ないときは4件）
        var ui = (FontFamily)Application.Current.FindResource("UiFont");
        var num = (FontFamily)Application.Current.FindResource("NumFont");
        bool firstRow = true;
        foreach (var next in r.Upcoming(NewsNextCount))
        {
            var row = new Grid { Margin = new Thickness(0, firstRow ? 20 : 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = next.Source, FontFamily = num, FontSize = 20,
                Foreground = _t40,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 14, 0),
            });
            var title = new TextBlock
            {
                Text = next.Title, FontFamily = ui, FontSize = 22,
                Foreground = _t60,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            NewsNext.Children.Add(row);
            firstRow = false;
        }
        if (NewsNext.Children.Count > 0)
            NewsNext.Children.Insert(0, new Rectangle { Height = 1, Fill = _line });

        var dot = _palette?.Text ?? Color.FromRgb(0xF4, 0xF7, 0xFB);
        Fx.Dots(NewsDots, r.Count, r.Index, AuroraTheme.Alpha(dot, 0xE6), AuroraTheme.Alpha(dot, 0x40), 9);
        NewsCount.Text = $"{r.Index + 1} / {r.Count}";
        NewsNav.Visibility = r.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (animate)
        {
            Fx.FadeIn(NewsTitle);
            Fx.FadeIn(NewsSource);
            Fx.FadeIn(NewsAge);
            Fx.FadeIn(NewsNext);
            Fx.FadeIn(NewsPreviewBox);
        }
        // 見出しと「次の見出し」の高さが決まってから、要約に使える行数を決める
        Dispatcher.BeginInvoke(new Action(FitNewsPreview), DispatcherPriority.Loaded);
    }

    /// <summary>見出しの長さで空きが変わるので、要約は入る行数だけ出す（最後の行は「…」で切る）。</summary>
    private void FitNewsPreview()
    {
        if (NewsPreview.Text.Length == 0) return;
        double free = NewsBody.ActualHeight - NewsNext.ActualHeight - NewsNext.Margin.Bottom
                    - NewsPreviewBox.Margin.Top - 16;
        int lines = Math.Clamp((int)Math.Floor(free / NewsPreviewLine), 0, NewsPreviewMaxLines);
        NewsPreview.MaxHeight = lines * NewsPreviewLine;
        NewsPreviewBox.Visibility = lines > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // =========================================================== 再生中の曲

    public void ShowMedia(NowPlaying? m)
    {
        if (!_ctx.Settings.EnableMedia) m = null;
        bool newTrack = m != null && (_media == null || m.Title != _media.Title || m.Artist != _media.Artist);
        _media = m;
        if (m == null)
        {
            _pausedSince = null;
            SetMediaVisible(false);
            return;
        }

        // 一時停止が続いたら隠す（再生が始まればまた出す）
        _pausedSince = m.IsPlaying ? null : _pausedSince ?? DateTime.Now;

        MediaSource.Text = m.IsPlaying ? $"{m.App} で再生中" : $"{m.App}・一時停止中";
        MediaDot.Fill = m.IsPlaying ? _mediaOn : _t40;
        MediaPauseIcon.Visibility = m.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        MediaPlayIcon.Visibility = m.IsPlaying ? Visibility.Collapsed : Visibility.Visible;

        MediaTitle.Text = m.Title;
        var sub = string.Join(" ・ ", new[] { m.Artist, m.Album }.Where(t => !string.IsNullOrWhiteSpace(t)));
        MediaSub.Text = sub.Length > 0 ? "　" + sub : "";

        if (m.Art != null)
        {
            MediaArt.Background = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill };
            MediaArtNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            MediaArt.Background = _line;
            MediaArtNote.Visibility = Visibility.Visible;
        }
        SetMusicGlowColor(m.ArtColor ?? MusicGlowDefault);

        bool wasVisible = MediaCard.Visibility == Visibility.Visible;
        SetMediaVisible(true);
        UpdateMediaProgress(DateTime.Now);
        if (wasVisible && newTrack) Fx.FadeIn(MediaBody);
    }

    /// <summary>再生中カードを天気の下に出し入れする。出ている間は時計・日付・天気を少し上に寄せてすき間を作る。</summary>
    private void SetMediaVisible(bool visible)
    {
        if (visible && _pausedSince is { } since && DateTime.Now - since > HidePausedAfter) visible = false;
        Glow(BlobMusic, !visible ? 0 : _media?.IsPlaying == true ? 0.45 : 0.2);

        if (visible == (MediaCard.Visibility == Visibility.Visible)) return;
        MediaCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        double lift = visible ? MediaLift : 0;
        ClockText.Margin = new Thickness(90, 150 - lift, 0, 0);
        DateText.Margin = new Thickness(112, 470 - lift, 0, 0);
        WeatherCard.Margin = new Thickness(96, 556 - lift, 0, 0);
        if (visible) Fx.FadeIn(MediaCard);
    }

    /// <summary>再生位置の表示を進める（1秒ごと）。</summary>
    private void UpdateMediaProgress(DateTime now)
    {
        var m = _media;
        if (m == null || MediaCard.Visibility != Visibility.Visible) return;
        var pos = m.PositionNow(now);
        bool hasLength = m.Duration > TimeSpan.Zero;
        MediaPos.Text = PlayTime(pos);
        MediaDur.Text = hasLength ? PlayTime(m.Duration) : "";
        double f = hasLength ? Math.Clamp(pos.TotalSeconds / m.Duration.TotalSeconds, 0, 1) : 0;
        double w = Math.Max(0, MediaTrack.ActualWidth * f);
        MediaFill.Width = w;
        MediaKnob.Margin = new Thickness(Math.Max(0, w - 6), 0, 0, 0);
        MediaKnob.Visibility = hasLength ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string PlayTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>背景の光をジャケットの色にする（同じ色なら作り直さない）。</summary>
    private void SetMusicGlowColor(Color c)
    {
        if (_glowColor == c) return;
        _glowColor = c;
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xA0, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, c.R, c.G, c.B), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1));
        brush.Freeze();
        BlobMusic.Fill = brush;
    }
}
