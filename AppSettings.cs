using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyDashboard;

/// <summary>%APPDATA%MyDashboardsettings.json を読み書きする（AppPaths）。</summary>
public sealed class AppSettings
{
    /// <summary>表示するモニター番号（0始まり）。-1 ならメイン以外の最初のモニター。</summary>
    public int Monitor { get; set; } = -1;
    public int UpdateIntervalMs { get; set; } = 1000;
    public int HistorySeconds { get; set; } = 90;
    /// <summary>常に背後に表示（壁紙のように、クリックしても他のウィンドウの後ろのまま）。</summary>
    public bool AlwaysBehind { get; set; }

    /// <summary>画面のレイアウト："classic"（標準）/ "gauge"（計器盤）/ "aurora"（オーロラ）。</summary>
    public string Layout { get; set; } = "classic";

    /// <summary>アニメーションのコマ数（1秒あたり、10〜60）。上げるとなめらかになるが GPU を使う。</summary>
    public int AnimationFps { get; set; } = 20;

    /// <summary>オーロラの背景アニメーション。false にすると背景の動きを止める（色の変化は残る）。</summary>
    public bool AuroraMotion { get; set; } = true;

    /// <summary>
    /// オーロラの色テーマ。"auto"＝時間帯で切り替え（朝・昼・夕方・夜）。
    /// "morning" / "day" / "evening" / "night" で固定、"classic" で以前の暗い配色に固定。
    /// </summary>
    public string AuroraTheme { get; set; } = "auto";

    /// <summary>明るいテーマ（朝・昼）の明るさ。1.0＝そのまま、0.7＝3割暗く（0.5〜1.0）。</summary>
    public double AuroraDayBrightness { get; set; } = 1.0;

    public double CpuTempWarn { get; set; } = 80;
    public double CpuTempCrit { get; set; } = 90;
    public double GpuTempWarn { get; set; } = 75;
    public double GpuTempCrit { get; set; } = 85;
    public double LoadWarn { get; set; } = 85;
    public double LoadCrit { get; set; } = 97;
    /// <summary>メモリ・VRAM 使用率の注意／危険（%）。</summary>
    public double MemWarn { get; set; } = 88;
    public double MemCrit { get; set; } = 95;

    // ---- ニュース
    public bool EnableNews { get; set; } = true;
    public int NewsRefreshMinutes { get; set; } = 10;
    public int NewsRotateSeconds { get; set; } = 8;
    public List<MyDashboard.Info.FeedSource> NewsFeeds { get; set; } = new()
    {
        new() { Name = "NHK", Url = "https://www.nhk.or.jp/rss/news/cat0.xml" },
        new() { Name = "Yahoo!ニュース", Url = "https://news.yahoo.co.jp/rss/topics/top-picks.xml" },
        new() { Name = "ITmedia", Url = "https://rss.itmedia.co.jp/rss/2.0/itmedia_all.xml" },
        new() { Name = "PC Watch", Url = "https://pc.watch.impress.co.jp/data/rss/1.0/pcw/feed.rdf" },
        new() { Name = "GIGAZINE", Url = "https://gigazine.net/news/rss_2.0/" },
    };

    // ---- 天気（Open-Meteo。緯度・経度で地点を指定）
    public bool EnableWeather { get; set; } = true;
    public string WeatherPlace { get; set; } = "東京";
    public double WeatherLatitude { get; set; } = 35.6895;
    public double WeatherLongitude { get; set; } = 139.6917;

    // ---- 再生中の音楽（Windows の再生中メディア情報。オーロラに表示）
    public bool EnableMedia { get; set; } = true;
    /// <summary>true なら Spotify だけを表示。false ならブラウザの動画なども含めて再生中のものを表示。</summary>
    public bool MediaSpotifyOnly { get; set; } = true;

    // ---- Twitch（オーロラのニュースカードと切り替え。フォロー中の配信を Twitch 公式 API で取る）
    public bool EnableTwitch { get; set; } = true;
    /// <summary>dev.twitch.tv で登録したアプリの Client ID。ログイン情報は別ファイル（twitch-auth.json）に保存する。</summary>
    public string TwitchClientId { get; set; } = "";
    /// <summary>ゲーム中は Twitch の画質を下げる（360p 以上）。既定はオフ。</summary>
    public bool TwitchLowQualityInGame { get; set; } = false;
    /// <summary>最後に見ていたチャンネル（配信中なら次もこの人から表示する）。</summary>
    public string TwitchLastChannel { get; set; } = "";

    // ---- YouTube（オーロラのニュースカードと切り替え。登録したチャンネルの配信中と新着を、YouTube の公開ページから読む）
    public bool EnableYouTube { get; set; } = true;
    public List<MyDashboard.Info.YouTubeChannel> YouTubeChannels { get; set; } = new();
    /// <summary>最後に見ていた動画（一覧にあれば次もこれから表示する）。</summary>
    public string YouTubeLastVideo { get; set; } = "";

    // ---- 消費電力（推定。標準・計器盤に表示）
    /// <summary>モニターの消費電力を合計に含める。</summary>
    public bool PowerIncludeMonitors { get; set; } = true;
    /// <summary>電源の容量（W）。効率の計算に使う。</summary>
    public int PsuWatts { get; set; } = 850;
    /// <summary>80 PLUS の等級："none" / "standard" / "bronze" / "silver" / "gold" / "platinum" / "titanium"。</summary>
    public string PsuRating { get; set; } = "gold";
    /// <summary>補正（W）。ワットチェッカーで測った値とずれるときに足し引きする。</summary>
    public double PowerOffsetW { get; set; }
    /// <summary>電気代の単価（円/kWh）。</summary>
    public double YenPerKwh { get; set; } = 31;
    /// <summary>モニターごとに自分で決めた W 数（キーはモニターの機種コード＋接続先）。ないモニターは自動の見積もり。</summary>
    public Dictionary<string, double> MonitorWatts { get; set; } = new();

    public bool EnableFps { get; set; } = true;
    public List<string> FpsExcludeProcesses { get; set; } = new()
    {
        "dwm.exe", "explorer.exe", "MyDashboard.exe", "msedgewebview2.exe"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string FilePath => AppPaths.Data("settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s != null) return s.Sanitized();
            }
        }
        catch
        {
            // 壊れた設定ファイルは無視して既定値で起動する
        }
        var d = new AppSettings();
        d.Save();
        return d;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions)); }
        catch { /* 書き込み不可の場所でも動作は続ける */ }
    }

    /// <summary>中身をまるごと写した別のインスタンス（設定画面の編集用）。</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!.Sanitized();

    /// <summary>other の値をこのインスタンスに写す。各レイアウトやサービスは同じインスタンスを見ているため、作り直さずに値だけ入れ替える。</summary>
    public void CopyFrom(AppSettings other)
    {
        var copy = other.Clone();
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            p.SetValue(this, p.GetValue(copy));
    }

    /// <summary>設定のうち pick で選んだ部分が a と b で同じか（変わった部分のサービスだけ再起動するため）。</summary>
    public static bool Same(AppSettings a, AppSettings b, Func<AppSettings, object> pick) =>
        JsonSerializer.Serialize(pick(a), JsonOptions) == JsonSerializer.Serialize(pick(b), JsonOptions);

    private AppSettings Sanitized()
    {
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 250, 5000);
        HistorySeconds = Math.Clamp(HistorySeconds, 20, 600);
        AnimationFps = Math.Clamp(AnimationFps, 10, 60);
        FpsExcludeProcesses ??= new List<string>();
        NewsFeeds ??= new List<MyDashboard.Info.FeedSource>();
        NewsRotateSeconds = Math.Clamp(NewsRotateSeconds, 3, 120);
        NewsRefreshMinutes = Math.Clamp(NewsRefreshMinutes, 3, 120);
        WeatherPlace ??= "";
        TwitchClientId = (TwitchClientId ?? "").Trim();
        TwitchLastChannel = (TwitchLastChannel ?? "").Trim();
        YouTubeChannels = (YouTubeChannels ?? new()).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => c.Id.Trim()).Select(g => new MyDashboard.Info.YouTubeChannel { Id = g.Key, Name = (g.First().Name ?? "").Trim() }).ToList();
        YouTubeLastVideo = (YouTubeLastVideo ?? "").Trim();
        Layout = string.IsNullOrWhiteSpace(Layout) ? "classic" : Layout.Trim().ToLowerInvariant();
        AuroraTheme = string.IsNullOrWhiteSpace(AuroraTheme) ? "auto" : AuroraTheme.Trim().ToLowerInvariant();
        AuroraDayBrightness = Math.Clamp(AuroraDayBrightness, 0.5, 1.0);
        PsuWatts = Math.Clamp(PsuWatts, 200, 2000);
        PsuRating = string.IsNullOrWhiteSpace(PsuRating) ? "gold" : PsuRating.Trim().ToLowerInvariant();
        PowerOffsetW = Math.Clamp(PowerOffsetW, -300, 300);
        YenPerKwh = Math.Clamp(YenPerKwh, 0, 200);
        MonitorWatts = (MonitorWatts ?? new()).Where(kv => kv.Value is >= 0 and <= 500)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return this;
    }
}
