using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDashboard.Info;

namespace MyDashboard.Views;

/// <summary>
/// オーロラの Twitch 表示。ニュースカードの「ニュース｜Twitch｜YouTube」で手動で切り替える（起動時はニュース）。
/// ・通常表示：ニュースカードの場所に映像（16:9）と、フォロー中で配信中の人の一覧
/// ・拡張表示：時計・天気・再生中の曲の場所まで広げ、映像の右にチャットを出す
/// 映像とチャットは WebView2（TwitchPlayer）で、ニュースに戻すと WebView2 ごと片付ける。
/// YouTube（AuroraView.YouTube.cs）も同じカード・同じ切り替え（TwitchMode）を使い、_source で見分ける。
/// </summary>
public partial class AuroraView
{
    private enum TwitchMode { Off, Card, Wide }
    private enum StreamSource { Twitch, YouTube }

    private static readonly Brush ChipOnFill = Frozen(Color.FromArgb(0x2E, 0x91, 0x46, 0xFF));
    private static readonly Brush ChipOnLine = Frozen(Color.FromArgb(0xCC, 0x91, 0x46, 0xFF));
    private static readonly Brush TabTwitch = Frozen(Color.FromArgb(0x66, 0x91, 0x46, 0xFF));
    private static readonly Brush TabYouTube = Frozen(Color.FromArgb(0xB3, 0xC4, 0x30, 0x2B));

    private readonly DispatcherTimer _twitchTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private readonly Dictionary<string, Brush> _avatars = new();
    private TwitchMode _twitchMode = TwitchMode.Off;
    private StreamSource _source = StreamSource.Twitch;
    private WebPlayer? _player;
    private string? _playingLogin;
    private bool _lowQuality;
    private DateTime? _lowPendingSince; // 画質を切り替えたい状態になった時刻（すぐには切り替えない）
    private double _webZoom;
    private TwitchAuth? _twitchAuth;
    private IReadOnlyList<TwitchStream> _streams = Array.Empty<TwitchStream>();
    private TwitchStream? _current;
    private string? _twitchError;
    private int _refreshSeq;

    private void InitTwitch()
    {
        var set = _ctx.Settings;
        NewsSeg.Visibility = set.EnableTwitch || set.EnableYouTube ? Visibility.Visible : Visibility.Collapsed;
        NewsTabTwitch.Visibility = CardTabTwitch.Visibility = WideTabTwitch.Visibility = set.EnableTwitch ? Visibility.Visible : Visibility.Collapsed;
        NewsTabYouTube.Visibility = CardTabYouTube.Visibility = WideTabYouTube.Visibility = set.EnableYouTube ? Visibility.Visible : Visibility.Collapsed;
        OnClick(NewsTabTwitch, () => SetTwitchMode(TwitchMode.Card, StreamSource.Twitch));
        OnClick(NewsTabYouTube, () => SetTwitchMode(TwitchMode.Card, StreamSource.YouTube));
        OnClick(TwitchTabNews, () => SetTwitchMode(TwitchMode.Off));
        OnClick(WideTabNews, () => SetTwitchMode(TwitchMode.Off));
        foreach (var b in new[] { CardTabTwitch, WideTabTwitch }) OnClick(b, () => SetTwitchMode(_twitchMode, StreamSource.Twitch));
        foreach (var b in new[] { CardTabYouTube, WideTabYouTube }) OnClick(b, () => SetTwitchMode(_twitchMode, StreamSource.YouTube));
        OnClick(TwitchExpand, () => SetTwitchMode(TwitchMode.Wide));
        OnClick(WideCollapse, () => SetTwitchMode(TwitchMode.Card));
        InitChipScroll(TwitchChipsScroll, TwitchChipsLeft, TwitchChipsRight);
        InitChipScroll(WideChipsScroll, WideChipsLeft, WideChipsRight);
        foreach (var b in new[] { NewsTabTwitch, NewsTabYouTube, TwitchTabNews, WideTabNews, TwitchExpand, WideCollapse,
                                  CardUrl, WideUrl, TwitchChipsLeft, TwitchChipsRight, WideChipsLeft, WideChipsRight })
        {
            b.MouseEnter += (s, _) => ((Border)s).Background = _line;
            b.MouseLeave += (s, _) => ((Border)s).Background = Brushes.Transparent;
        }
        // 選んでいる方のタブは色を付けたままにする
        foreach (var b in new[] { CardTabTwitch, CardTabYouTube, WideTabTwitch, WideTabYouTube })
        {
            b.MouseEnter += (s, _) => { if (!IsSelectedTab((Border)s)) ((Border)s).Background = _line; };
            b.MouseLeave += (_, _) => UpdateSourceTabs();
        }
        _twitchTimer.Tick += (_, _) => _ = RefreshTwitch();
        InitYouTube();
    }

    private bool IsSelectedTab(Border b) =>
        _source == StreamSource.Twitch ? b == CardTabTwitch || b == WideTabTwitch : b == CardTabYouTube || b == WideTabYouTube;

    /// <summary>カード上の「Twitch｜YouTube」のうち、いま見ている方に色を付ける。「＋ URL」は YouTube のときだけ。</summary>
    private void UpdateSourceTabs()
    {
        bool yt = _source == StreamSource.YouTube;
        foreach (var b in new[] { CardTabTwitch, WideTabTwitch, CardTabYouTube, WideTabYouTube })
        {
            bool on = IsSelectedTab(b);
            b.Background = on ? (yt ? TabYouTube : TabTwitch) : Brushes.Transparent;
            b.Cursor = on ? null : Cursors.Hand;
            ((TextBlock)b.Child).Foreground = on ? _text : _t60;
        }
        CardUrl.Visibility = WideUrl.Visibility = yt ? Visibility.Visible : Visibility.Collapsed;
        TwitchExpand.ToolTip = yt ? "拡張表示（配信ならチャット、動画なら新着の一覧付き）" : "拡張表示（チャット付き）";
    }

    /// <summary>フォロー中の一覧が入りきらないとき、左右の矢印（見える幅の8割ずつ）とマウスホイールで横に送る。</summary>
    private static void InitChipScroll(ScrollViewer sv, Border left, Border right)
    {
        OnClick(left, () => sv.ScrollToHorizontalOffset(sv.HorizontalOffset - sv.ViewportWidth * 0.8));
        OnClick(right, () => sv.ScrollToHorizontalOffset(sv.HorizontalOffset + sv.ViewportWidth * 0.8));
        sv.PreviewMouseWheel += (_, e) =>
        {
            if (sv.ScrollableWidth <= 0) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        sv.ScrollChanged += (_, _) =>
        {
            // 入りきるときは矢印を消す。端まで来た側は薄くして押せなくする
            var show = sv.ScrollableWidth > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            left.Visibility = right.Visibility = show;
            bool atStart = sv.HorizontalOffset <= 0.5, atEnd = sv.HorizontalOffset >= sv.ScrollableWidth - 0.5;
            left.Opacity = atStart ? 0.35 : 1;
            right.Opacity = atEnd ? 0.35 : 1;
            left.IsHitTestVisible = !atStart;
            right.IsHitTestVisible = !atEnd;
        };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>クリックで動くボタン。押下も止めて、ニュースカードのダブルクリック（記事を開く）にならないようにする。</summary>
    private static void OnClick(Border b, Action act)
    {
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
    }

    // =========================================================== 表示の切り替え

    private void SetTwitchMode(TwitchMode mode, StreamSource? source = null)
    {
        var src = source ?? _source;
        if (!(src == StreamSource.Twitch ? _ctx.Settings.EnableTwitch : _ctx.Settings.EnableYouTube)) mode = TwitchMode.Off;
        var old = _twitchMode;
        var oldSource = _source;
        if (mode == old && (mode == TwitchMode.Off || src == oldSource)) return;
        _twitchMode = mode;
        if (mode != TwitchMode.Off) _source = src;
        bool on = mode != TwitchMode.Off, wide = mode == TwitchMode.Wide;
        UpdateSourceTabs();

        NewsCard.Visibility = !on && _ctx.Settings.EnableNews ? Visibility.Visible : Visibility.Collapsed;
        TwitchCard.Visibility = mode == TwitchMode.Card ? Visibility.Visible : Visibility.Collapsed;
        TwitchWide.Visibility = WideClockPanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        DailyLayer.Visibility = MediaLayer.Visibility = wide ? Visibility.Hidden : Visibility.Visible;
        _news.Paused = false;

        if (!on)
        {
            _twitchTimer.Stop();
            StopYouTube();
            ClosePlayer();
            return;
        }
        if (wide) UpdateClock(DateTime.Now);
        if (old == TwitchMode.Off || _source != oldSource)
        {
            // 見るものを替えた：前の方を止め、映像も作り直す
            _twitchTimer.Stop();
            StopYouTube();
            ClosePlayer();
            ShowTwitchInfo(null, false);
            SetTwitchMessage(null);
            TwitchChips.Children.Clear();
            WideChips.Children.Clear();
            TwitchListLabel.Text = WideListLabel.Text = "";
            if (_source == StreamSource.Twitch)
            {
                _twitchTimer.Start();
                ShowStreams();
                _ = RefreshTwitch();
            }
            else
            {
                StartYouTube();
            }
        }
        else if (_source == StreamSource.Twitch)
        {
            ShowStreams();
        }
        else
        {
            ShowYouTube();
        }
        PlaceWeb();
    }

    /// <summary>フォロー中の配信を取り直す（表示中は2分ごと、配信が終わったときにも）。</summary>
    private async Task RefreshTwitch()
    {
        int seq = ++_refreshSeq;
        var s = _ctx.Settings;
        _twitchAuth ??= TwitchService.LoadAuth(); // 設定画面でログインし直した分も読む
        if (_streams.Count == 0 && _twitchError == null) SetTwitchMessage("フォロー中の配信を確かめています…");

        var r = await TwitchService.GetFollowedLiveAsync(TwitchService.ResolveClientId(s.TwitchClientId), _twitchAuth);
        if (seq != _refreshSeq || _twitchMode == TwitchMode.Off || _source != StreamSource.Twitch) return;

        if (r.NeedLogin)
        {
            _twitchAuth = null;
            _streams = Array.Empty<TwitchStream>();
            _twitchError = r.Error;
        }
        else if (r.Error != null)
        {
            // 通信の失敗：一覧があればそのまま見せ続ける
            if (_streams.Count == 0) _twitchError = r.Error;
        }
        else
        {
            _streams = r.Streams;
            _twitchError = null;
        }
        ShowStreams();
    }

    private TwitchStream? FindStream(string? login) =>
        string.IsNullOrEmpty(login) ? null : _streams.FirstOrDefault(x => string.Equals(x.Login, login, StringComparison.OrdinalIgnoreCase));

    /// <summary>いま見るチャンネルを決めて、映像・名前・一覧を出す。</summary>
    private void ShowStreams()
    {
        if (_twitchMode == TwitchMode.Off || _source != StreamSource.Twitch) return;

        // 見ている人が配信を終えても、勝手に別の人へは移らない（一覧から選び直してもらう）
        var cur = FindStream(_current?.Login);
        if (cur == null && _current != null && _player != null && _twitchError == null && _streams.Count > 0) cur = _current;
        cur ??= _streams.Count == 0 ? null : FindStream(_ctx.Settings.TwitchLastChannel) ?? _streams[0];
        _current = cur;
        bool live = cur != null && FindStream(cur.Login) != null;

        ShowTwitchInfo(cur, live);
        BuildChips(TwitchChips);
        BuildChips(WideChips);
        var label = _streams.Count > 0 ? $"フォロー中で配信中（{_streams.Count}）" : "";
        TwitchListLabel.Text = WideListLabel.Text = label;

        if (cur == null)
        {
            SetTwitchMessage(_twitchError ?? "フォロー中で配信している人はいません");
            ClosePlayer();
            return;
        }
        SetTwitchMessage(null);
        Play(cur.Login);
    }

    private void SelectChannel(TwitchStream st)
    {
        _current = st;
        if (!string.Equals(_ctx.Settings.TwitchLastChannel, st.Login, StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Settings.TwitchLastChannel = st.Login;
            _ctx.Settings.Save();
        }
        ShowStreams();
    }

    private void SetTwitchMessage(string? text) => TwitchMessage.Text = WideMessage.Text = text ?? "";

    private void ShowTwitchInfo(TwitchStream? st, bool live)
    {
        TwitchName.Text = WideName.Text = st == null ? "" : st.Name.Length > 0 ? st.Name : st.Login;
        WideTitle.Text = st?.Title ?? "";
        TwitchTitle.Text = st == null ? "" : st.Game.Length > 0 ? $"{st.Title} ／ {st.Game}" : st.Title;
        TwitchAvatar.Fill = WideAvatar.Fill = AvatarBrush(st?.Avatar);
        TwitchLiveInfo.Visibility = WideLiveInfo.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        TwitchViewers.Text = WideViewers.Text = st == null ? "" : $"{st.Viewers.ToString("N0", CultureInfo.InvariantCulture)} 人";
        UpdateTwitchUptime(DateTime.Now);
    }

    private void UpdateTwitchUptime(DateTime now)
    {
        if (_source == StreamSource.YouTube) { UpdateYouTubeUptime(now); return; }
        var st = _current;
        TwitchUptime.Text = WideUptime.Text = st == null || st.StartedAt == default ? "" : PlayTime(now - st.StartedAt);
    }

    /// <summary>アイコンやサムネイル（URL が同じものは使い回す。読み込みは WPF が裏で行う）。decode は読み込む幅。</summary>
    private Brush AvatarBrush(string? url, int decode = 64)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return _line;
        if (_avatars.TryGetValue(url, out var b)) return b;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = decode;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        }
        catch
        {
            b = _line;
        }
        _avatars[url] = b;
        return b;
    }

    /// <summary>フォロー中で配信中の人を丸いボタンで並べる（全員。入りきらない分は横に送って見る）。押すとその人に切り替える。</summary>
    private void BuildChips(StackPanel panel)
    {
        panel.Children.Clear();
        var ui = (FontFamily)Application.Current.FindResource("UiFont");
        var num = (FontFamily)Application.Current.FindResource("NumFont");

        foreach (var st in _streams)
        {
            bool on = _current != null && string.Equals(_current.Login, st.Login, StringComparison.OrdinalIgnoreCase);
            // 1行目：名前＋視聴者数、2行目：配信タイトル
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = st.Name.Length > 0 ? st.Name : st.Login, FontFamily = ui, FontSize = 18, Foreground = _t85,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            head.Children.Add(new TextBlock
            {
                Text = Viewers(st.Viewers), FontFamily = num, FontSize = 14, Foreground = _t50,
                Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            var lines = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(head);
            lines.Children.Add(new TextBlock
            {
                Text = st.Title, FontFamily = ui, FontSize = 14, Foreground = _t50, Margin = new Thickness(0, 1, 0, 0),
                MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 40, Height = 40, Fill = AvatarBrush(st.Avatar), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(lines);
            var chip = Chip(row, on);
            chip.ToolTip = st.Game.Length > 0 ? $"{st.Title}\n{st.Game}" : st.Title;
            OnClick(chip, () => SelectChannel(st));
            panel.Children.Add(chip);
        }
    }

    private Border Chip(UIElement content, bool on) => new()
    {
        Child = content,
        Height = 58,
        CornerRadius = new CornerRadius(29),
        Padding = new Thickness(8, 6, 20, 6),
        Margin = new Thickness(0, 0, 10, 0),
        BorderThickness = new Thickness(1),
        Background = on ? ChipOnFill : _glass,
        BorderBrush = on ? ChipOnLine : _line,
        Cursor = Cursors.Hand,
    };

    private static string Viewers(int n) =>
        n >= 10000 ? (n / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k"
        : n >= 1000 ? (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k"
        : n.ToString(CultureInfo.InvariantCulture);

    // =========================================================== 映像（WebView2）

    private void Play(string login)
    {
        var player = EnsurePlayer<TwitchPlayer>();
        if (_playingLogin != login)
        {
            player.Play(login, muted: true); // 最初は消音（音はプレーヤーのスピーカーのボタンで出す）
            _playingLogin = login;
        }
        UpdateTwitchGame();
    }

    /// <summary>映像の WebView2 を用意する（種類が違うものが出ていれば作り直す）。</summary>
    private T EnsurePlayer<T>() where T : WebPlayer, new()
    {
        if (_player is T same) return same;
        ClosePlayer();
        var player = new T();
        _player = player;
        player.Message += OnPlayerMessage;
        TwitchLayer.Children.Add(player.View);
        _lowQuality = false;
        _lowPendingSince = null;
        _webZoom = 0;
        PlaceWeb();
        _ = player.StartAsync();
        return player;
    }

    private void ClosePlayer()
    {
        if (_player == null) return;
        _player.Message -= OnPlayerMessage;
        TwitchLayer.Children.Remove(_player.View);
        _player.Dispose();
        _player = null;
        _playingLogin = null;
        _ytPlayingId = null;
    }

    private void OnPlayerMessage(string m)
    {
        if (_player is YouTubePlayer) { OnYouTubeMessage(m); return; }
        if (m == "offline")
        {
            // 配信が終わった：一覧を取り直す（見ている人が一覧から消えたら、ほかの人を選べるようにする）
            _ = RefreshTwitch();
        }
        else if (m.StartsWith("error:", StringComparison.Ordinal))
        {
            ClosePlayer();
            SetTwitchMessage(m.Substring(6));
        }
    }

    /// <summary>WebView2 を、いまの表示（通常／拡張）の映像の場所にぴったり重ねる。</summary>
    private void PlaceWeb()
    {
        if (_player == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_player == null) return;
            var slot = _twitchMode == TwitchMode.Wide ? WideSlot : TwitchSlot;
            if (!slot.IsVisible || slot.ActualWidth <= 0) return;
            var p = slot.TranslatePoint(new Point(0, 0), Root);
            var v = _player.View;
            Canvas.SetLeft(v, p.X);
            Canvas.SetTop(v, p.Y);
            v.Width = slot.ActualWidth;
            v.Height = slot.ActualHeight;
            UpdateWebZoom();
            // 拡張表示：映像を 16:9 で高さいっぱいにし、残りの幅をチャット（YouTube の動画なら新着の一覧）にする
            double chat = WideSlot.ActualWidth > 0
                ? (WideSlot.ActualWidth - WideSlot.ActualHeight * 16 / 9) / WideSlot.ActualWidth * 100
                : 30;
            _player.SetWide(_twitchMode == TwitchMode.Wide, chat);
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 画面は 1920×1080 で設計して拡大縮小しているので、WebView2 の中（チャットの文字など）も同じ倍率にする。
    /// モニターを移したときに合わせられるよう、時計の更新（1秒ごと）でも確かめる。
    /// </summary>
    private void UpdateWebZoom()
    {
        if (_player == null || Window.GetWindow(this) is not { ActualWidth: > 0, ActualHeight: > 0 } w) return;
        double zoom = Math.Clamp(Math.Min(w.ActualWidth / 1920, w.ActualHeight / 1080), 0.25, 4);
        if (Math.Abs(zoom - _webZoom) < 0.001) return;
        _webZoom = zoom;
        _player.View.ZoomFactor = zoom;
    }

    /// <summary>
    /// ゲーム中は画質を下げる（設定でオン）。画質を変えるたびに映像が読み込み直しになるので、
    /// ゲームが30秒続いてから下げ、ゲームが終わって1分たってから戻す（判定のぶれで何度も切り替えない）。
    /// </summary>
    private void UpdateTwitchGame()
    {
        if (_player is not TwitchPlayer player) return;
        bool want = _playing && _ctx.Settings.TwitchLowQualityInGame;
        var now = DateTime.Now;
        if (want != _lowQuality)
        {
            _lowPendingSince ??= now;
            var wait = want ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(1);
            // 設定でオフにしたときはすぐ戻す
            if (_ctx.Settings.TwitchLowQualityInGame && now - _lowPendingSince < wait) return;
        }
        _lowPendingSince = null;
        bool low = want;
        if (low == _lowQuality) return;
        _lowQuality = low;
        player.SetLowQuality(low);
    }

    /// <summary>時計の更新（1秒ごと）から呼ぶ。</summary>
    private void TickTwitch(DateTime now)
    {
        if (_twitchMode == TwitchMode.Off) return;
        UpdateTwitchUptime(now);
        if (_twitchMode == TwitchMode.Wide) WideClock.Text = Fmt.Time(now);
        UpdateWebZoom();
    }
}
