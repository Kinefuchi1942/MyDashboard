using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace MyDashboard.Info;

/// <summary>ニュースの見出し1件。Summary は RSS の本文の要約（配信元が出していなければ空）。</summary>
public sealed record NewsItem(string Source, string Title, string Link, DateTimeOffset? Published, string Summary = "");

/// <summary>ニュース全体の状態。Items が空のときは Message に理由（読み込み中・失敗など）が入る。</summary>
public sealed record NewsState(IReadOnlyList<NewsItem> Items, string? Message);

/// <summary>時間ごとの予報1コマ。</summary>
public sealed record HourlyForecast(DateTime Time, double? Temperature, double? PrecipitationProbability, int? Code, bool IsDay);

/// <summary>天気の状態。Error が入っているときは取得に失敗している。Sunrise / Sunset は今日の日の出・日の入り。</summary>
public sealed record WeatherInfo(
    string Place,
    double? Temperature,
    int? Code,
    bool IsDay,
    double? TodayMax,
    double? TodayMin,
    IReadOnlyList<HourlyForecast> Hourly,
    DateTime UpdatedAt,
    string? Error,
    DateTime? Sunrise = null,
    DateTime? Sunset = null);

/// <summary>
/// 再生中の曲。Position は PositionAt 時点の再生位置で、再生中は PositionNow で今の位置を計算する。
/// Art はジャケット画像、ArtColor はその代表色（取れなければ null）。
/// </summary>
public sealed record NowPlaying(
    string App,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTime PositionAt,
    double Rate,
    ImageSource? Art,
    Color? ArtColor)
{
    public TimeSpan PositionNow(DateTime now)
    {
        var p = IsPlaying ? Position + TimeSpan.FromTicks((long)((now - PositionAt).Ticks * Rate)) : Position;
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
    }
}

/// <summary>RSS の登録情報（settings.json の newsFeeds）。</summary>
public sealed class FeedSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>WMO 天気コード（Open-Meteo）の分類と日本語表記。</summary>
public static class WeatherCodes
{
    public enum Kind { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Thunder }

    public static Kind KindOf(int? code) => code switch
    {
        0 or 1 => Kind.Clear,
        2 => Kind.PartlyCloudy,
        3 => Kind.Cloudy,
        45 or 48 => Kind.Fog,
        >= 51 and <= 67 => Kind.Rain,
        >= 80 and <= 82 => Kind.Rain,
        >= 71 and <= 77 => Kind.Snow,
        85 or 86 => Kind.Snow,
        >= 95 => Kind.Thunder,
        _ => Kind.Cloudy,
    };

    public static string Text(int? code) => code switch
    {
        0 => "快晴",
        1 => "晴れ",
        2 => "晴れ時々曇り",
        3 => "曇り",
        45 or 48 => "霧",
        51 or 53 or 55 => "霧雨",
        56 or 57 => "着氷性の霧雨",
        61 => "小雨",
        63 => "雨",
        65 => "大雨",
        66 or 67 => "着氷性の雨",
        71 => "小雪",
        73 => "雪",
        75 => "大雪",
        77 => "霧雪",
        80 or 81 => "にわか雨",
        82 => "激しいにわか雨",
        85 or 86 => "にわか雪",
        95 => "雷雨",
        96 or 99 => "ひょうを伴う雷雨",
        97 => "激しい雷雨",
        null => "--",
        _ => "不明",
    };
}
