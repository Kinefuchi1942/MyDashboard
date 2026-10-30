using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyDashboard.Info;
using MyDashboard.Monitoring;
using Screen = System.Windows.Forms.Screen;

namespace MyDashboard;

/// <summary>
/// settings.json を画面で編集するウィンドウ。右クリックメニューの「設定…」から開く。
/// 「保存」で Result に新しい設定を入れて閉じ、反映は MainWindow が行う。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>天気の地点検索の結果1件。</summary>
    public sealed record Place(string Name, string Region, double Latitude, double Longitude);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HttpClient Http = CreateHttp();

    private readonly ObservableCollection<FeedSource> _feeds = new();
    private readonly ObservableCollection<string> _excludes = new();
    private readonly ObservableCollection<YouTubeChannel> _ytChannels = new();
    /// <summary>消費電力のページのモニターの W 数の欄（キーはモニターの目印）。</summary>
    private readonly Dictionary<string, TextBox> _monitorBoxes = new();
    /// <summary>画面にない設定（最後に見ていた Twitch のチャンネルなど）を保存で消さないための元の値。</summary>
    private readonly AppSettings _original;

    /// <summary>「保存」で閉じたときの新しい設定。キャンセルなら null。</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        // 拡大率の高い画面でもはみ出さないように（中身は縦にスクロールできる）
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        FeedList.ItemsSource = _feeds;
        ExcludeList.ItemsSource = _excludes;
        YouTubeList.ItemsSource = _ytChannels;
        _original = current.Clone();
        Load(current.Clone());
        NavDisplay.IsChecked = true;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MyDashboard/1.0 (+personal desktop dashboard)");
        return http;
    }

    // =========================================================== 設定 → 画面

    private void Load(AppSettings s)
    {
        LayoutClassic.IsChecked = s.Layout == "classic";
        LayoutGauge.IsChecked = s.Layout == "gauge";
        LayoutAurora.IsChecked = s.Layout == "aurora";
        if (LayoutClassic.IsChecked != true && LayoutGauge.IsChecked != true && LayoutAurora.IsChecked != true)
            LayoutClassic.IsChecked = true;

        BuildMonitorChoices(s.Monitor);
        BehindBox.IsChecked = s.AlwaysBehind;
        IntervalSlider.Value = s.UpdateIntervalMs;
        HistorySlider.Value = s.HistorySeconds;
        FpsSlider.Value = s.AnimationFps;

        var theme = ThemePanel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == s.AuroraTheme)
                    ?? ThemePanel.Children.OfType<RadioButton>().First();
        theme.IsChecked = true;
        BrightnessSlider.Value = Math.Round(s.AuroraDayBrightness * 100);
        MotionBox.IsChecked = s.AuroraMotion;

        CpuWarnBox.Text = Num(s.CpuTempWarn);
        CpuCritBox.Text = Num(s.CpuTempCrit);
        GpuWarnBox.Text = Num(s.GpuTempWarn);
        GpuCritBox.Text = Num(s.GpuTempCrit);
        LoadWarnBox.Text = Num(s.LoadWarn);
        LoadCritBox.Text = Num(s.LoadCrit);
        MemWarnBox.Text = Num(s.MemWarn);
        MemCritBox.Text = Num(s.MemCrit);

        BuildPowerRows(s.MonitorWatts);
        PowerMonitorBox.IsChecked = s.PowerIncludeMonitors;
        PsuBox.Text = s.PsuWatts.ToString(Inv);
        (PsuRatingPanel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == s.PsuRating)
            ?? PsuRatingPanel.Children.OfType<RadioButton>().First(r => (string)r.Tag == "gold")).IsChecked = true;
        PowerOffsetBox.Text = Num(s.PowerOffsetW);
        YenBox.Text = Num(s.YenPerKwh);

        NewsBox.IsChecked = s.EnableNews;
        RotateSlider.Value = Math.Clamp(s.NewsRotateSeconds, 3, 60);
        RefreshSlider.Value = Math.Clamp(s.NewsRefreshMinutes, 3, 60);
        _feeds.Clear();
        foreach (var f in s.NewsFeeds) _feeds.Add(new FeedSource { Name = f.Name, Url = f.Url });

        WeatherBox.IsChecked = s.EnableWeather;
        PlaceBox.Text = s.WeatherPlace;
        LatBox.Text = s.WeatherLatitude.ToString("0.####", Inv);
        LonBox.Text = s.WeatherLongitude.ToString("0.####", Inv);
        SearchBox.Text = "";
        ResultBorder.Visibility = Visibility.Collapsed;
        SearchStatus.Visibility = Visibility.Collapsed;

        MediaBox.IsChecked = s.EnableMedia;
        SpotifyOnlyBox.IsChecked = s.MediaSpotifyOnly;

        TwitchBox.IsChecked = s.EnableTwitch;
        TwitchClientBox.Text = s.TwitchClientId;
        TwitchLowBox.IsChecked = s.TwitchLowQualityInGame;
        ShowTwitchLogin(TwitchService.LoadAuth());

        YouTubeBox.IsChecked = s.EnableYouTube;
        _ytChannels.Clear();
        foreach (var c in s.YouTubeChannels) _ytChannels.Add(new YouTubeChannel { Id = c.Id, Name = c.Name });
        YouTubeInput.Text = "";
        ShowYouTubeStatus(null);

        FpsBox.IsChecked = s.EnableFps;
        _excludes.Clear();
        foreach (var x in s.FpsExcludeProcesses) _excludes.Add(x);

        UpdateSliderTexts();
        ShowNote(null);
    }

    private static string Num(double v) => v.ToString("0.#", Inv);

    /// <summary>モニターの選択肢：「自動」と、つながっているモニターを番号順に。</summary>
    private void BuildMonitorChoices(int selected)
    {
        MonitorPanel.Children.Clear();
        var screens = Screen.AllScreens;
        AddMonitorChoice(-1, "自動（メイン以外）", selected);
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            AddMonitorChoice(i, $"{i + 1}：{(screens[i].Primary ? "メイン" : "サブ")}（{b.Width}×{b.Height}）", selected);
        }
        if (!MonitorPanel.Children.OfType<RadioButton>().Any(r => r.IsChecked == true))
            ((RadioButton)MonitorPanel.Children[0]).IsChecked = true;
    }

    private void AddMonitorChoice(int index, string text, int selected) =>
        MonitorPanel.Children.Add(new RadioButton
        {
            Style = (Style)FindResource("Chip"),
            GroupName = "Monitor",
            Content = text,
            Tag = index,
            IsChecked = index == selected,
        });

    // =========================================================== 画面 → 設定

    /// <summary>入力を確かめて設定を作る。おかしな値があれば message に理由を入れて null を返す。</summary>
    private AppSettings? Collect(out string? message)
    {
        message = null;
        var s = new AppSettings
        {
            Layout = LayoutGauge.IsChecked == true ? "gauge" : LayoutAurora.IsChecked == true ? "aurora" : "classic",
            Monitor = MonitorPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? -1,
            AlwaysBehind = BehindBox.IsChecked == true,
            UpdateIntervalMs = (int)IntervalSlider.Value,
            HistorySeconds = (int)HistorySlider.Value,
            AnimationFps = (int)FpsSlider.Value,
            AuroraTheme = ThemePanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "auto",
            AuroraDayBrightness = BrightnessSlider.Value / 100.0,
            AuroraMotion = MotionBox.IsChecked == true,
            EnableNews = NewsBox.IsChecked == true,
            NewsRotateSeconds = (int)RotateSlider.Value,
            NewsRefreshMinutes = (int)RefreshSlider.Value,
            EnableWeather = WeatherBox.IsChecked == true,
            WeatherPlace = PlaceBox.Text.Trim(),
            EnableMedia = MediaBox.IsChecked == true,
            MediaSpotifyOnly = SpotifyOnlyBox.IsChecked == true,
            EnableTwitch = TwitchBox.IsChecked == true,
            TwitchClientId = TwitchClientBox.Text.Trim(),
            TwitchLowQualityInGame = TwitchLowBox.IsChecked == true,
            TwitchLastChannel = _original.TwitchLastChannel,
            EnableYouTube = YouTubeBox.IsChecked == true,
            YouTubeChannels = _ytChannels.Select(c => new YouTubeChannel { Id = c.Id, Name = c.Name }).ToList(),
            YouTubeLastVideo = _original.YouTubeLastVideo,
            PowerIncludeMonitors = PowerMonitorBox.IsChecked == true,
            PsuRating = PsuRatingPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "gold",
            EnableFps = FpsBox.IsChecked == true,
            FpsExcludeProcesses = _excludes.ToList(),
        };

        // ---- 警告の目安
        if (!Pair(CpuWarnBox, CpuCritBox, 30, 115, "CPU 温度", out var cw, out var cc, out message)) return Fail(NavIndex.Thresholds);
        if (!Pair(GpuWarnBox, GpuCritBox, 30, 115, "GPU 温度", out var gw, out var gc, out message)) return Fail(NavIndex.Thresholds);
        if (!Pair(LoadWarnBox, LoadCritBox, 10, 100, "CPU・GPU 使用率", out var lw, out var lc, out message)) return Fail(NavIndex.Thresholds);
        if (!Pair(MemWarnBox, MemCritBox, 10, 100, "メモリ・VRAM 使用率", out var mw, out var mc, out message)) return Fail(NavIndex.Thresholds);
        (s.CpuTempWarn, s.CpuTempCrit, s.GpuTempWarn, s.GpuTempCrit, s.LoadWarn, s.LoadCrit) = (cw, cc, gw, gc, lw, lc);
        (s.MemWarn, s.MemCrit) = (mw, mc);

        // ---- 消費電力
        if (!TryNum(PsuBox.Text, out var psu) || psu < 200 || psu > 2000)
        {
            message = "消費電力：電源の容量は 200〜2000 W の数字で入れてください";
            return Fail(NavIndex.Power);
        }
        if (!TryNum(PowerOffsetBox.Text, out var offset) || offset < -300 || offset > 300)
        {
            message = "消費電力：補正は -300〜300 W の数字で入れてください（なければ 0）";
            return Fail(NavIndex.Power);
        }
        if (!TryNum(YenBox.Text, out var yen) || yen < 0 || yen > 200)
        {
            message = "消費電力：電気代の単価は 0〜200 円の数字で入れてください";
            return Fail(NavIndex.Power);
        }
        (s.PsuWatts, s.PowerOffsetW, s.YenPerKwh) = ((int)Math.Round(psu), offset, yen);
        // いまつないでいないモニターの値は消さずに残す
        s.MonitorWatts = _original.MonitorWatts.Where(kv => !_monitorBoxes.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (key, box) in _monitorBoxes)
        {
            if (box.Text.Trim().Length == 0) continue;
            if (!TryNum(box.Text, out var w) || w < 0 || w > 500)
            {
                message = "消費電力：モニターの W 数は 0〜500 の数字で入れてください（空欄なら自動）";
                return Fail(NavIndex.Power);
            }
            s.MonitorWatts[key] = w;
        }

        // ---- ニュースの配信元（URL が空の行は捨てる）
        s.NewsFeeds = new List<FeedSource>();
        foreach (var f in _feeds)
        {
            var url = (f.Url ?? "").Trim();
            var name = (f.Name ?? "").Trim();
            if (url.Length == 0) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            {
                message = $"ニュース：「{(name.Length > 0 ? name : url)}」の URL が正しくありません（https:// で始まる RSS の URL を入れてください）";
                return Fail(NavIndex.News);
            }
            s.NewsFeeds.Add(new FeedSource { Name = name, Url = url });
        }

        // ---- 天気の地点
        if (!TryNum(LatBox.Text, out var lat) || lat < -90 || lat > 90 ||
            !TryNum(LonBox.Text, out var lon) || lon < -180 || lon > 180)
        {
            message = "天気：緯度・経度が正しくありません。地名を検索して選び直してください";
            return Fail(NavIndex.Weather);
        }
        s.WeatherLatitude = lat;
        s.WeatherLongitude = lon;
        if (s.WeatherPlace.Length == 0) s.WeatherPlace = "現在地";

        return s;
    }

    private enum NavIndex { Display, Aurora, Thresholds, Power, News, Weather, Media, Twitch, YouTube, Fps }

    /// <summary>入力に問題があったページを開いて null を返す。</summary>
    private AppSettings? Fail(NavIndex page)
    {
        var nav = ((Panel)NavDisplay.Parent).Children.OfType<RadioButton>().ElementAt((int)page);
        nav.IsChecked = true;
        return null;
    }

    private static bool Pair(TextBox warnBox, TextBox critBox, double min, double max, string label,
        out double warn, out double crit, out string? message)
    {
        message = null;
        crit = 0;
        if (!TryNum(warnBox.Text, out warn) || !TryNum(critBox.Text, out crit) ||
            warn < min || warn > max || crit < min || crit > max)
        {
            message = $"警告の目安：{label}は {min:0}〜{max:0} の数字で入れてください";
            return false;
        }
        if (warn > crit)
        {
            message = $"警告の目安：{label}は「注意」を「危険」以下の値にしてください";
            return false;
        }
        return true;
    }

    /// <summary>数字を読む。全角数字や「,」の小数点も受け付ける。</summary>
    private static bool TryNum(string text, out double value)
    {
        text = (text ?? "").Trim().Normalize(System.Text.NormalizationForm.FormKC).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, Inv, out value) && double.IsFinite(value);
    }

    // =========================================================== ページ切り替え・スライダー

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string pageName } || PageScroll == null) return;
        foreach (var page in new FrameworkElement[] { PageDisplay, PageAurora, PageThresholds, PagePower, PageNews, PageWeather, PageMedia, PageTwitch, PageYouTube, PageFps })
            page.Visibility = page.Name == pageName ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderTexts();

    private void UpdateSliderTexts()
    {
        // InitializeComponent の途中（まだ部品がそろっていない）でも呼ばれるため null を確かめる
        if (IntervalText == null || HistoryText == null || FpsText == null || BrightnessText == null || RotateText == null || RefreshText == null) return;
        IntervalText.Text = $"{IntervalSlider.Value / 1000.0:0.00} 秒";
        HistoryText.Text = $"{HistorySlider.Value:0} 秒";
        FpsText.Text = $"{FpsSlider.Value:0} コマ/秒";
        BrightnessText.Text = $"{BrightnessSlider.Value:0} %";
        RotateText.Text = $"{RotateSlider.Value:0} 秒";
        RefreshText.Text = $"{RefreshSlider.Value:0} 分";
    }

    // =========================================================== ニュースの配信元

    private void OnFeedAdd(object sender, RoutedEventArgs e)
    {
        _feeds.Add(new FeedSource { Name = "", Url = "https://" });
        // 追加した行の名前欄にカーソルを置く
        Dispatcher.InvokeAsync(() =>
        {
            if (FeedList.ItemContainerGenerator.ContainerFromIndex(_feeds.Count - 1) is DependencyObject row)
                FindChild<TextBox>(row)?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnFeedRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FeedSource f }) _feeds.Remove(f);
    }

    private void OnFeedUp(object sender, RoutedEventArgs e) => MoveFeed(sender, -1);
    private void OnFeedDown(object sender, RoutedEventArgs e) => MoveFeed(sender, +1);

    private void MoveFeed(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: FeedSource f }) return;
        int i = _feeds.IndexOf(f), j = i + delta;
        if (i < 0 || j < 0 || j >= _feeds.Count) return;
        _feeds.Move(i, j);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    // =========================================================== 天気の地点検索（Open-Meteo の地名検索。API キー不要）

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnSearch(sender, e); }
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        SearchButton.IsEnabled = false;
        ShowSearchStatus("検索しています…");
        ResultBorder.Visibility = Visibility.Collapsed;
        try
        {
            var places = await SearchPlaces(query);
            if (places.Count == 0)
            {
                ShowSearchStatus("見つかりませんでした。市区町村名（例：大阪、札幌、横浜）で試してください");
                return;
            }
            ResultList.ItemsSource = places;
            ResultBorder.Visibility = Visibility.Visible;
            ShowSearchStatus(null);
        }
        catch (Exception ex)
        {
            ShowSearchStatus("検索できませんでした（" + ex.Message + "）");
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void ShowSearchStatus(string? text)
    {
        SearchStatus.Text = text ?? "";
        SearchStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static async Task<List<Place>> SearchPlaces(string query)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=8&language=ja&format=json&name=" +
                  Uri.EscapeDataString(query);
        var json = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var list = new List<Place>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("latitude", out var la) || !r.TryGetProperty("longitude", out var lo)) continue;
            var name = Str(r, "name");
            var region = string.Join("・", new[] { Str(r, "admin1"), Str(r, "country") }.Where(x => x.Length > 0).Distinct());
            list.Add(new Place(name, region, la.GetDouble(), lo.GetDouble()));
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void OnPickPlace(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Place p }) return;
        PlaceBox.Text = p.Name;
        LatBox.Text = p.Latitude.ToString("0.####", Inv);
        LonBox.Text = p.Longitude.ToString("0.####", Inv);
        ResultBorder.Visibility = Visibility.Collapsed;
        ShowSearchStatus($"「{p.Name}」を選びました。保存すると天気が切り替わります");
    }

    // =========================================================== Twitch

    private void ShowTwitchLogin(TwitchAuth? auth)
    {
        TwitchLoginText.Text = auth != null ? $"ログイン中：{auth.Login}" : "ログインしていません";
        TwitchLoginButton.Content = auth != null ? "別のアカウントでログイン" : "ログイン";
        TwitchLogoutButton.Visibility = auth != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTwitchRegister(object sender, RoutedEventArgs e) => MyDashboard.Views.TwitchWeb.OpenExternal(TwitchService.RegisterUrl);

    private void OnTwitchLogin(object sender, RoutedEventArgs e)
    {
        // 空欄なら組み込みの ID でログインする
        var w = new TwitchLoginWindow(TwitchService.ResolveClientId(TwitchClientBox.Text)) { Owner = this };
        if (w.ShowDialog() == true && w.Result != null)
        {
            ShowTwitchLogin(w.Result);
            ShowNote($"{w.Result.Login} でログインしました。「保存」を押すと反映されます");
        }
    }

    private void OnTwitchLogout(object sender, RoutedEventArgs e)
    {
        TwitchService.ClearAuth();
        ShowTwitchLogin(null);
        ShowNote("ログアウトしました");
    }

    // =========================================================== YouTube のチャンネル

    private void OnYouTubeKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnYouTubeAdd(sender, e); }
    }

    /// <summary>入れられた URL や @ハンドル からチャンネルを探して一覧に足す。</summary>
    private async void OnYouTubeAdd(object sender, RoutedEventArgs e)
    {
        var input = YouTubeInput.Text.Trim();
        if (input.Length == 0 || !YouTubeAddButton.IsEnabled) return;
        YouTubeAddButton.IsEnabled = false;
        ShowYouTubeStatus("チャンネルを探しています…");
        try
        {
            var (channel, error) = await YouTubeService.ResolveAsync(input);
            if (channel == null)
            {
                ShowYouTubeStatus(error ?? "チャンネルが見つかりませんでした");
                return;
            }
            if (_ytChannels.Any(c => c.Id == channel.Id))
            {
                ShowYouTubeStatus($"「{channel.Name}」はもう登録してあります");
                return;
            }
            _ytChannels.Add(channel);
            YouTubeInput.Text = "";
            ShowYouTubeStatus($"「{channel.Name}」を追加しました。「保存」を押すと反映されます");
        }
        finally
        {
            YouTubeAddButton.IsEnabled = true;
        }
    }

    private void OnYouTubeRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: YouTubeChannel c }) _ytChannels.Remove(c);
    }

    private void ShowYouTubeStatus(string? text)
    {
        YouTubeStatus.Text = text ?? "";
        YouTubeStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // =========================================================== FPS の除外アプリ

    private void OnExcludeKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnExcludeAdd(sender, e); }
    }

    private void OnExcludeAdd(object sender, RoutedEventArgs e)
    {
        var name = ExcludeBox.Text.Trim().Trim('"');
        if (name.Length == 0) return;
        // PresentMon はプロセス名を「〇〇.exe」で出すので、拡張子がなければ付ける
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!_excludes.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) _excludes.Add(name);
        ExcludeBox.Text = "";
    }

    private void OnExcludeRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) _excludes.Remove(name);
    }

    // =========================================================== 消費電力：見つかった構成

    /// <summary>見つかった部品と W 数の表を作る。モニターの行には W 数を直す欄を付ける（overrides は欄に入れておく値）。</summary>
    private void BuildPowerRows(IReadOnlyDictionary<string, double> overrides)
    {
        PowerRows.Children.Clear();
        _monitorBoxes.Clear();
        var rows = PowerMeter.Shared.Rows;
        if (rows.Count == 0)
        {
            PowerRows.Children.Add(new TextBlock { Text = "まだ調べています。少し待ってから開き直してください", Foreground = Res("Dim"), Margin = new Thickness(0, 6, 0, 6) });
            return;
        }
        int monitorNo = 0;
        foreach (var r in rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string label = r.MonitorKey != null && rows.Count(x => x.MonitorKey != null) > 1 ? $"{r.Label} {++monitorNo}" : r.Label;
            grid.Children.Add(new TextBlock { Text = label, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Center });

            var detail = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            detail.Children.Add(new TextBlock { Text = r.Detail, Foreground = Res("Soft"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            detail.Children.Add(PowerTag(r.Measured ? "実測" : "見積もり" + (r.Note != null ? "・" + r.Note : ""), r.Measured));
            Grid.SetColumn(detail, 1);
            grid.Children.Add(detail);

            FrameworkElement right;
            if (r.MonitorKey != null)
            {
                var box = new TextBox { Style = (Style)FindResource("NumBox"), Width = 76, FontSize = 17, Padding = new Thickness(8, 2, 8, 2),
                                        Text = overrides.TryGetValue(r.MonitorKey, out var o) ? Num(o) : "" };
                _monitorBoxes[r.MonitorKey] = box;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = $"自動 {r.AutoWatts:0} W", Foreground = Res("Dim"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                sp.Children.Add(box);
                sp.Children.Add(new TextBlock { Text = "W", Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                right = sp;
            }
            else
            {
                right = new TextBlock { Text = $"{r.Watts:0} W", FontFamily = (System.Windows.Media.FontFamily)FindResource("NumFont"), FontSize = 17,
                                        VerticalAlignment = VerticalAlignment.Center };
            }
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            PowerRows.Children.Add(new Border { Style = (Style)FindResource("RowLine"), Padding = new Thickness(0, 8, 0, 8), Child = grid });
        }
    }

    private Border PowerTag(string text, bool measured) => new()
    {
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        BorderBrush = measured ? Res("Accent") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x56, 0x68)),
        Background = measured ? Res("AccentSoft") : System.Windows.Media.Brushes.Transparent,
        Padding = new Thickness(8, 1, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = measured ? Res("Accent") : Res("Dim") },
    };

    private System.Windows.Media.Brush Res(string key) => (System.Windows.Media.Brush)FindResource(key);

    private async void OnPowerRescan(object sender, RoutedEventArgs e)
    {
        // 欄に入力中の値は残す
        var typed = _monitorBoxes.Where(kv => TryNum(kv.Value.Text, out _)).ToDictionary(kv => kv.Key, kv => { TryNum(kv.Value.Text, out var v); return v; });
        RescanButton.IsEnabled = false;
        ShowNote("構成を調べています…");
        try
        {
            await PowerMeter.Shared.Rescan();
            await Task.Delay(1500);   // 新しい構成での見積もりが一度出るのを待つ
            BuildPowerRows(typed);
            ShowNote("構成を読み直しました");
        }
        catch { ShowNote("構成を読み直せませんでした", error: true); }
        finally { RescanButton.IsEnabled = true; }
    }

    // =========================================================== 下のボタン・タイトルバー

    private void OnDefaults(object sender, RoutedEventArgs e)
    {
        // Twitch の Client ID と YouTube のチャンネルは登録し直しが手間なので残す
        Load(new AppSettings
        {
            TwitchClientId = TwitchClientBox.Text.Trim(),
            YouTubeChannels = _ytChannels.Select(c => new YouTubeChannel { Id = c.Id, Name = c.Name }).ToList(),
        });
        ShowNote("初期値に戻しました。「保存」を押すと反映されます");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = Collect(out var message);
        if (s == null)
        {
            ShowNote(message, error: true);
            return;
        }
        Result = s;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowNote(string? text, bool error = false)
    {
        FootNote.Text = text ?? "保存すると画面にすぐ反映されます";
        FootNote.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Warn" : "Dim");
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
