using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDashboard.Info;

namespace MyDashboard.Views;

/// <summary>
/// オーロラの YouTube 表示。Twitch と同じカード（通常／拡張）を使う。
/// ・一覧：設定で登録したチャンネルのうち配信中のもの（先頭）と、新着動画（新しい順）
/// ・「＋ URL」：コピーした YouTube の動画の URL を再生する
/// ・拡張表示：配信ならライブチャット、動画なら同じチャンネルの新着を映像の右に出す（YouTubePlayer のページの中）
/// 新着と配信中は YouTube の公開ページから読む（YouTubeService）。表示している間だけ、配信中は3分ごと・新着は15分ごとに確かめる。
/// 動画が終わったら止めたまま（次へは進まない）。
/// </summary>
public partial class AuroraView
{
    private static readonly Brush YtChipOnFill = Frozen(Color.FromArgb(0x2E, 0xE5, 0x32, 0x2D));
    private static readonly Brush YtChipOnLine = Frozen(Color.FromArgb(0xCC, 0xE5, 0x32, 0x2D));
    private static readonly Brush LiveRed = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush BadgeBack = Frozen(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
    private static readonly TimeSpan YtLiveEvery = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan YtVideosEvery = TimeSpan.FromMinutes(15);
    private const int YtVideosPerChannel = 6;
    private const int YtVideosMax = 30;

    private readonly DispatcherTimer _ytTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<string, YouTubeChannelPage> _ytPages = new();
    private readonly Dictionary<string, YouTubeVideo> _ytLive = new();
    private DateTime _ytVideosAt, _ytLiveAt;
    private bool _ytLoaded;
    private YouTubeVideo? _ytCurrent;
    private string? _ytPlayingId, _ytFailedId, _ytError;
    private string? _ytNote;
    private DateTime _ytNoteUntil;
    private int _ytSeq;

    private void InitYouTube()
    {
        OnClick(CardUrl, PlayFromClipboard);
        OnClick(WideUrl, PlayFromClipboard);
        _ytTimer.Tick += (_, _) => _ = RefreshYouTube();
    }

    private void StartYouTube()
    {
        _ytTimer.Start();
        ShowYouTube();
        _ = RefreshYouTube();
    }

    private void StopYouTube()
    {
        _ytTimer.Stop();
        _ytSeq++; // 取りに行っている途中の結果は捨てる
    }

    private bool YouTubeShown => _twitchMode != TwitchMode.Off && _source == StreamSource.YouTube;

    /// <summary>配信中（3分ごと）と新着（15分ごと）を、時間が来ている方だけ取り直す。</summary>
    private async Task RefreshYouTube()
    {
        int seq = ++_ytSeq;
        var chans = _ctx.Settings.YouTubeChannels.ToList();
        if (chans.Count == 0)
        {
            _ytLoaded = true;
            _ytError = "設定画面の「YouTube」でチャンネルを登録してください\n（「＋ URL」でコピーした動画も再生できます）";
            ShowYouTube();
            return;
        }
        var now = DateTime.Now;
        bool needVideos = now - _ytVideosAt >= YtVideosEvery;
        bool needLive = now - _ytLiveAt >= YtLiveEvery - TimeSpan.FromSeconds(5);
        if (!needVideos && !needLive) return;

        // 一度にたくさん取りに行かない（ページ1つが1MB ほどある）
        using var gate = new SemaphoreSlim(4);
        async Task<T> Limit<T>(Func<Task<T>> f)
        {
            await gate.WaitAsync();
            try { return await f(); }
            finally { gate.Release(); }
        }
        var videoTasks = needVideos ? chans.Select(c => Limit(() => YouTubeService.GetChannelAsync(c.Id, YtVideosPerChannel))).ToList() : null;
        var liveTasks = needLive ? chans.Select(c => Limit(() => YouTubeService.GetLiveAsync(c.Id))).ToList() : null;
        var videos = videoTasks != null ? await Task.WhenAll(videoTasks) : null;
        var lives = liveTasks != null ? await Task.WhenAll(liveTasks) : null;
        if (seq != _ytSeq || !YouTubeShown) return;

        string? error = null;
        var ids = chans.Select(c => c.Id).ToHashSet();
        foreach (var gone in _ytPages.Keys.Where(k => !ids.Contains(k)).ToList()) _ytPages.Remove(gone);
        foreach (var gone in _ytLive.Keys.Where(k => !ids.Contains(k)).ToList()) _ytLive.Remove(gone);
        if (videos != null)
        {
            _ytVideosAt = now;
            for (int i = 0; i < chans.Count; i++)
            {
                if (videos[i].Page is { } page) _ytPages[chans[i].Id] = page;
                else error ??= $"{chans[i].Name}：{videos[i].Error}";
            }
        }
        if (lives != null)
        {
            _ytLiveAt = now;
            for (int i = 0; i < chans.Count; i++)
            {
                var id = chans[i].Id;
                if (lives[i].Video is { } v)
                {
                    _ytPages.TryGetValue(id, out var page);
                    _ytLive[id] = v with
                    {
                        ChannelName = page?.Name ?? (v.ChannelName.Length > 0 ? v.ChannelName : chans[i].Name),
                        Avatar = page?.Avatar,
                    };
                }
                else if (lives[i].Error == null)
                {
                    _ytLive.Remove(id); // 通信に失敗したときは前の結果を残す
                }
            }
        }
        _ytLoaded = true;
        _ytError = _ytPages.Count == 0 && _ytLive.Count == 0 ? error ?? "新着の動画が見つかりませんでした" : null;
        ShowYouTube();
    }

    /// <summary>一覧に並べるもの：配信中（視聴者の多い順）→ 新着動画（新しい順）。</summary>
    private List<YouTubeVideo> YouTubeItems() =>
        _ytLive.Values.OrderByDescending(v => v.Viewers)
            .Concat(_ytPages.Values.SelectMany(p => p.Videos).OrderByDescending(v => v.Published).Take(YtVideosMax))
            .ToList();

    /// <summary>いま見る動画を決めて、映像・名前・一覧を出す。</summary>
    private void ShowYouTube()
    {
        if (!YouTubeShown) return;
        var items = YouTubeItems();

        // 見ているものは勝手に替えない（配信が終わっても、一覧から選び直してもらう）。一覧にあれば新しい情報（視聴者数など）にする
        var cur = _ytCurrent;
        if (cur != null && items.FirstOrDefault(v => v.VideoId == cur.VideoId) is { } fresh) cur = fresh;
        cur ??= items.FirstOrDefault(v => v.VideoId == _ctx.Settings.YouTubeLastVideo) ?? items.FirstOrDefault(v => v.Live) ?? items.FirstOrDefault();
        _ytCurrent = cur;

        ShowYouTubeInfo(cur);
        BuildYouTubeChips(TwitchChips, items);
        BuildYouTubeChips(WideChips, items);
        UpdateYouTubeLabel(DateTime.Now);

        if (cur == null)
        {
            SetTwitchMessage(_ytError ?? (_ytLoaded ? "新着の動画が見つかりませんでした" : "YouTube の新着を確かめています…"));
            ClosePlayer();
            return;
        }
        if (cur.VideoId == _ytFailedId) return; // 再生できなかった動画は、選び直されるまでメッセージのまま
        SetTwitchMessage(null);
        var player = EnsurePlayer<YouTubePlayer>();
        if (_ytPlayingId != cur.VideoId)
        {
            player.Play(cur.VideoId, cur.Live, muted: true); // 最初は消音（音はプレーヤーのスピーカーのボタンで出す）
            _ytPlayingId = cur.VideoId;
        }
        UpdateYouTubeSide(player, cur);
    }

    /// <summary>拡張表示で動画の右に出す一覧：同じチャンネルの新着（コピーした動画ならすべての新着）。</summary>
    private void UpdateYouTubeSide(YouTubePlayer player, YouTubeVideo cur)
    {
        if (_ytPages.TryGetValue(cur.ChannelId, out var page))
            player.SetSide($"{page.Name} の新着", page.Videos.Where(v => v.VideoId != cur.VideoId));
        else
            player.SetSide("新着動画", _ytPages.Values.SelectMany(p => p.Videos).Where(v => v.VideoId != cur.VideoId).OrderByDescending(v => v.Published).Take(12));
    }

    private void SelectYouTube(YouTubeVideo v)
    {
        _ytCurrent = v;
        _ytFailedId = null;
        if (v.ChannelId.Length > 0 && _ctx.Settings.YouTubeLastVideo != v.VideoId)
        {
            _ctx.Settings.YouTubeLastVideo = v.VideoId;
            _ctx.Settings.Save();
        }
        ShowYouTube();
    }

    /// <summary>「＋ URL」：コピーしてある YouTube の動画の URL を再生する。</summary>
    private void PlayFromClipboard()
    {
        string text = "";
        try { text = Clipboard.GetText(); }
        catch { }
        var id = YouTubeService.ParseVideoId(text);
        if (id == null)
        {
            ShowYouTubeNote("YouTube の動画の URL をコピーしてから押してください");
            return;
        }
        SelectYouTube(YouTubeItems().FirstOrDefault(v => v.VideoId == id) ?? new YouTubeVideo(id, "", "", "", "", DateTime.Now));
    }

    private void ShowYouTubeNote(string text)
    {
        _ytNote = text;
        _ytNoteUntil = DateTime.Now.AddSeconds(5);
        UpdateYouTubeLabel(DateTime.Now);
    }

    /// <summary>一覧の見出し。「＋ URL」の知らせがあれば5秒だけそちらを出す。</summary>
    private void UpdateYouTubeLabel(DateTime now)
    {
        if (!YouTubeShown) return;
        string label;
        if (_ytNote != null && now < _ytNoteUntil) label = _ytNote;
        else
        {
            _ytNote = null;
            bool any = _ytLive.Count > 0 || _ytPages.Values.Any(p => p.Videos.Count > 0);
            label = !any ? "" : _ytLive.Count > 0 ? $"配信中（{_ytLive.Count}）・新着動画" : "新着動画";
        }
        if (TwitchListLabel.Text != label) TwitchListLabel.Text = WideListLabel.Text = label;
    }

    private void ShowYouTubeInfo(YouTubeVideo? v)
    {
        string? avatar = v?.Avatar ?? (v != null && _ytPages.TryGetValue(v.ChannelId, out var p) ? p.Avatar : null);
        TwitchName.Text = WideName.Text = v == null ? "" : v.ChannelName.Length > 0 ? v.ChannelName : "コピーした動画";
        TwitchTitle.Text = WideTitle.Text = v == null ? "" : v.Title.Length > 0 ? v.Title : "読み込んでいます…";
        TwitchAvatar.Fill = WideAvatar.Fill = AvatarBrush(avatar);
        TwitchLiveInfo.Visibility = WideLiveInfo.Visibility = v?.Live == true ? Visibility.Visible : Visibility.Collapsed;
        TwitchViewers.Text = WideViewers.Text = v is { Live: true, Viewers: > 0 } ? $"{v.Viewers.ToString("N0", CultureInfo.InvariantCulture)} 人" : "";
        UpdateYouTubeUptime(DateTime.Now);
    }

    private void UpdateYouTubeUptime(DateTime now)
    {
        var v = _ytCurrent;
        TwitchUptime.Text = WideUptime.Text = v is { Live: true } && v.StartedAt != default ? PlayTime(now - v.StartedAt) : "";
        UpdateYouTubeLabel(now);
    }

    /// <summary>ページ（YouTubePlayer）からの知らせ。</summary>
    private void OnYouTubeMessage(string m)
    {
        if (m.StartsWith("pick:", StringComparison.Ordinal))
        {
            var id = m.Substring(5);
            var v = YouTubeItems().FirstOrDefault(x => x.VideoId == id) ?? _ytPages.Values.SelectMany(p => p.Videos).FirstOrDefault(x => x.VideoId == id);
            if (v != null) SelectYouTube(v);
        }
        else if (m.StartsWith("info:", StringComparison.Ordinal))
        {
            // コピーした動画は、再生が始まってから題名・チャンネル名が分かる
            if (_ytCurrent is not { ChannelId.Length: 0 } cur) return;
            try
            {
                using var doc = JsonDocument.Parse(m.Substring(5));
                var r = doc.RootElement;
                if (r.GetProperty("id").GetString() != cur.VideoId) return;
                _ytCurrent = cur with
                {
                    Title = r.GetProperty("title").GetString() ?? "",
                    ChannelName = r.GetProperty("author").GetString() ?? "",
                    Live = r.GetProperty("live").GetBoolean(),
                };
                ShowYouTubeInfo(_ytCurrent);
            }
            catch { }
        }
        else if (m == "state:0")
        {
            // 終わった：動画は止めたまま。配信なら一覧を取り直す（終わった配信を一覧から消す）
            if (_ytCurrent?.Live == true)
            {
                _ytLiveAt = default;
                _ = RefreshYouTube();
            }
        }
        else if (m.StartsWith("yterror:", StringComparison.Ordinal))
        {
            _ytFailedId = _ytCurrent?.VideoId;
            ClosePlayer();
            SetTwitchMessage(m.Substring(8) switch
            {
                "101" or "150" or "152" => "この動画はアプリの中では再生できません\n（投稿者が埋め込みを許可していません）",
                "100" => "動画が見つかりません（削除されたか非公開です）",
                "2" => "動画の URL が正しくありません",
                _ => "この動画を再生できませんでした",
            });
        }
        else if (m.StartsWith("error:", StringComparison.Ordinal))
        {
            ClosePlayer();
            SetTwitchMessage(m.Substring(6));
        }
    }

    // =========================================================== 一覧のボタン

    /// <summary>配信中は丸いボタン（アイコン＋LIVE）、動画はサムネイル付きの角丸のボタンで並べる。押すとその動画に切り替える。</summary>
    private void BuildYouTubeChips(StackPanel panel, List<YouTubeVideo> items)
    {
        panel.Children.Clear();
        var ui = (FontFamily)Application.Current.FindResource("UiFont");
        var num = (FontFamily)Application.Current.FindResource("NumFont");

        foreach (var v in items)
        {
            bool on = _ytCurrent != null && _ytCurrent.VideoId == v.VideoId;
            var lines = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var second = new TextBlock
            {
                FontFamily = ui, FontSize = 14, Foreground = _t50, Margin = new Thickness(0, 1, 0, 0),
                MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            lines.Children.Add(head);
            lines.Children.Add(second);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            Border chip;

            if (v.Live)
            {
                // 1行目：チャンネル名＋LIVE＋視聴者数、2行目：配信タイトル
                head.Children.Add(new TextBlock
                {
                    Text = v.ChannelName, FontFamily = ui, FontSize = 18, Foreground = _t85,
                    VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                head.Children.Add(new Border
                {
                    Background = LiveRed, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                    Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "LIVE", FontFamily = num, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Brushes.White },
                });
                if (v.Viewers > 0)
                    head.Children.Add(new TextBlock
                    {
                        Text = Viewers(v.Viewers), FontFamily = num, FontSize = 14, Foreground = _t50,
                        Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    });
                second.Text = v.Title;
                var avatar = new Grid { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center };
                avatar.Children.Add(new Ellipse { Fill = AvatarBrush(v.Avatar) });
                avatar.Children.Add(new Ellipse
                {
                    Width = 13, Height = 13, Fill = LiveRed, Stroke = _glass, StrokeThickness = 2,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2),
                });
                row.Children.Add(avatar);
                row.Children.Add(lines);
                chip = Chip(row, false);
            }
            else
            {
                // 1行目：動画のタイトル、2行目：チャンネル名・いつ
                head.Children.Add(new TextBlock
                {
                    Text = v.Title, FontFamily = ui, FontSize = 17, Foreground = _t85,
                    MaxWidth = 250, TextTrimming = TextTrimming.CharacterEllipsis,
                });
                second.Text = $"{v.ChannelName}・{Ago(v.Published)}";
                var thumb = new Border
                {
                    Width = 88, Height = 50, CornerRadius = new CornerRadius(7), Background = AvatarBrush(v.Thumbnail, 192),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (v.Duration.Length > 0)
                    thumb.Child = new Border
                    {
                        Background = BadgeBack, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(0, 0, 3, 3),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                        Child = new TextBlock { Text = v.Duration, FontFamily = num, FontSize = 11, Foreground = Brushes.White },
                    };
                row.Children.Add(thumb);
                row.Children.Add(lines);
                chip = Chip(row, false);
                chip.CornerRadius = new CornerRadius(12);
                chip.Padding = new Thickness(4, 3, 18, 3);
            }
            if (on)
            {
                chip.Background = YtChipOnFill;
                chip.BorderBrush = YtChipOnLine;
            }
            chip.ToolTip = v.Live ? $"{v.ChannelName}\n{v.Title}" : $"{v.Title}\n{v.ChannelName}・{Ago(v.Published)}";
            OnClick(chip, () => SelectYouTube(v));
            panel.Children.Add(chip);
        }
    }

    /// <summary>「3時間前」など（YouTube の表示に合わせたおおよその時間）。</summary>
    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "たった今";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}分前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}時間前";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}日前";
        if (d.TotalDays < 30) return $"{(int)(d.TotalDays / 7)}週間前";
        if (d.TotalDays < 365) return $"{(int)(d.TotalDays / 30)}か月前";
        return $"{(int)(d.TotalDays / 365)}年前";
    }
}
