using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyDashboard.Info;

/// <summary>
/// ニュース（RSS）と天気（Open-Meteo）を一定間隔で取得する。
/// センサーの収集（Collector）とは別のループで動かし、通信の遅れが表示更新に影響しないようにする。
/// </summary>
public sealed class InfoService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;

    public event Action<NewsState>? NewsUpdated;
    public event Action<WeatherInfo>? WeatherUpdated;

    static InfoService()
    {
        // Shift_JIS や EUC-JP で配信している RSS を読めるようにする
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public InfoService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyDashboard/1.0 (+personal desktop dashboard)");
    }

    public void Start()
    {
        if (_settings.EnableNews) _ = Task.Run(() => NewsLoop(_cts.Token));
        if (_settings.EnableWeather) _ = Task.Run(() => WeatherLoop(_cts.Token));
    }

    // =========================================================== ニュース

    private async Task NewsLoop(CancellationToken ct)
    {
        NewsUpdated?.Invoke(new NewsState(Array.Empty<NewsItem>(), "ニュースを読み込んでいます"));
        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.NewsRefreshMinutes, 3, 120));
        List<NewsItem>? last = null;

        while (!ct.IsCancellationRequested)
        {
            var feeds = _settings.NewsFeeds.Where(f => Uri.TryCreate(f.Url, UriKind.Absolute, out _)).ToList();
            var results = await Task.WhenAll(feeds.Select(f => FetchFeed(f, ct)));
            var ok = results.Where(r => r != null).Select(r => r!).ToList();

            if (ok.Count > 0)
            {
                last = FeedParser.Interleave(ok, 24);
                NewsUpdated?.Invoke(new NewsState(last, null));
            }
            else if (last == null)
            {
                NewsUpdated?.Invoke(new NewsState(Array.Empty<NewsItem>(),
                    feeds.Count == 0 ? "settings.json の newsFeeds に配信元がありません" : "ニュースを取得できませんでした。再試行します"));
            }
            // 取得に失敗したときは前回の見出しを表示し続ける

            var wait = ok.Count > 0 ? interval : TimeSpan.FromMinutes(2);
            try { await Task.Delay(wait, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task<List<NewsItem>?> FetchFeed(FeedSource f, CancellationToken ct)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(f.Url, ct);
            string xml = DecodeXml(bytes);
            var name = string.IsNullOrWhiteSpace(f.Name) ? new Uri(f.Url).Host : f.Name;
            return FeedParser.Parse(xml, name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>XML 宣言の encoding を見て文字列にする（既定は UTF-8）。</summary>
    private static string DecodeXml(byte[] bytes)
    {
        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));
        var m = System.Text.RegularExpressions.Regex.Match(head, "encoding=[\"']([A-Za-z0-9_\\-]+)[\"']");
        var enc = System.Text.Encoding.UTF8;
        if (m.Success)
        {
            try { enc = System.Text.Encoding.GetEncoding(m.Groups[1].Value); }
            catch { /* 不明な文字コードは UTF-8 とみなす */ }
        }
        var text = enc.GetString(bytes);
        // 先頭の BOM と XML 宣言の encoding 指定を外しておく（文字列として既に復号済みのため）
        text = text.TrimStart('﻿');
        return System.Text.RegularExpressions.Regex.Replace(text, "^<\\?xml[^>]*\\?>", "");
    }

    // =========================================================== 天気

    private async Task WeatherLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(15);
        WeatherInfo? last = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                last = await FetchWeather(ct);
                WeatherUpdated?.Invoke(last);
                try { await Task.Delay(interval, ct); } catch (TaskCanceledException) { break; }
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // 前回の値があればそのまま表示し続ける
                if (last == null)
                    WeatherUpdated?.Invoke(new WeatherInfo(_settings.WeatherPlace, null, null, true, null, null,
                        Array.Empty<HourlyForecast>(), DateTime.Now, "天気を取得できませんでした（" + ex.Message + "）"));
            }
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task<WeatherInfo> FetchWeather(CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        string url = "https://api.open-meteo.com/v1/forecast" +
                     $"?latitude={_settings.WeatherLatitude.ToString(inv)}&longitude={_settings.WeatherLongitude.ToString(inv)}" +
                     "&current=temperature_2m,weather_code,is_day" +
                     "&hourly=temperature_2m,precipitation_probability,weather_code,is_day" +
                     "&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset" +
                     "&timezone=auto&forecast_days=2";

        var json = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cur = root.GetProperty("current");
        double? temp = Num(cur, "temperature_2m");
        int? code = (int?)Num(cur, "weather_code");
        bool isDay = (Num(cur, "is_day") ?? 1) >= 1;

        var daily = root.GetProperty("daily");
        double? max = At(daily, "temperature_2m_max", 0);
        double? min = At(daily, "temperature_2m_min", 0);
        DateTime? sunrise = TimeAt(daily, "sunrise", 0);
        DateTime? sunset = TimeAt(daily, "sunset", 0);

        // 時間ごとの予報：次の「3の倍数の時刻」から3時間おきに6コマ
        var hourly = root.GetProperty("hourly");
        var times = hourly.GetProperty("time").EnumerateArray()
            .Select(t => DateTime.ParseExact(t.GetString()!, "yyyy-MM-dd'T'HH:mm", inv)).ToList();
        var now = DateTime.Now;
        var list = new List<HourlyForecast>();
        for (int i = 0; i < times.Count && list.Count < 6; i++)
        {
            var t = times[i];
            if (t <= now || t.Hour % 3 != 0) continue;
            list.Add(new HourlyForecast(t,
                At(hourly, "temperature_2m", i),
                At(hourly, "precipitation_probability", i),
                (int?)At(hourly, "weather_code", i),
                (At(hourly, "is_day", i) ?? 1) >= 1));
        }

        return new WeatherInfo(_settings.WeatherPlace, temp, code, isDay, max, min, list, DateTime.Now, null, sunrise, sunset);
    }

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double? At(JsonElement obj, string name, int index)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        if (index >= arr.GetArrayLength()) return null;
        var v = arr[index];
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    /// <summary>"2026-10-26T05:58" 形式の時刻（地点の現地時刻）を読む。</summary>
    private static DateTime? TimeAt(JsonElement obj, string name, int index)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        if (index >= arr.GetArrayLength() || arr[index].ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParseExact(arr[index].GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
