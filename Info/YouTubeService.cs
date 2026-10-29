using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MyDashboard.Info;

/// <summary>設定に登録した YouTube のチャンネル（settings.json に保存）。</summary>
public sealed class YouTubeChannel
{
    /// <summary>チャンネル ID（UC で始まる24文字）。</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>動画または配信の1件。Published は「8 時間前」などから逆算したおおよその時刻。</summary>
public sealed record YouTubeVideo(string VideoId, string ChannelId, string ChannelName, string Title, string Duration, DateTime Published)
{
    public bool Live { get; init; }
    public int Viewers { get; init; }
    /// <summary>配信の開始時刻（取れなかったときは default）。</summary>
    public DateTime StartedAt { get; init; }
    public string? Avatar { get; init; }
    public string Thumbnail => $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg";
}

/// <summary>チャンネルの「動画」タブから読んだ名前・アイコン・新着動画。</summary>
public sealed record YouTubeChannelPage(string Id, string Name, string? Avatar, IReadOnlyList<YouTubeVideo> Videos);

/// <summary>
/// YouTube の公開ページ（キー不要）からチャンネルの新着動画と配信中かどうかを読む。
/// RSS はチャンネルによって 404 になることがあるため使わない。ページの中の ytInitialData / ytInitialPlayerResponse を読むので、
/// YouTube がページの作りを変えると取れなくなることがある（そのときはここを直す）。
/// </summary>
public static class YouTubeService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly Regex ChannelIdRx = new(@"^UC[\w-]{22}$", RegexOptions.Compiled);
    private static readonly Regex VideoIdRx = new(@"^[\w-]{11}$", RegexOptions.Compiled);
    private static readonly Regex AgeRx = new(@"(\d+)\s*(秒|分|時間|日|週間|か月|ヶ月|年)", RegexOptions.Compiled);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        // 日本語のページ（「8 時間前」など）を受け取るため、普通のブラウザとして日本語で取る
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja");
        return http;
    }

    // =========================================================== チャンネルの登録（設定画面）

    /// <summary>
    /// 設定画面で入れられた文字（チャンネルの URL、@ハンドル、チャンネル ID、動画の URL）からチャンネルを探す。
    /// </summary>
    public static async Task<(YouTubeChannel? Channel, string? Error)> ResolveAsync(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return (null, "チャンネルの URL か @ハンドル を入れてください");
        try
        {
            string? id = ChannelIdRx.IsMatch(input) ? input : null;
            if (id == null && ParseVideoId(input) is { } vid)
            {
                // 動画の URL なら、その動画のチャンネル
                var html = await Http.GetStringAsync("https://www.youtube.com/watch?v=" + vid);
                using var player = ExtractJson(html, "ytInitialPlayerResponse");
                if (player != null && player.RootElement.TryGetProperty("videoDetails", out var d)) id = Str(d, "channelId");
            }
            if (id == null)
            {
                var url = ChannelUrl(input);
                if (url == null) return (null, "YouTube のチャンネルの URL か @ハンドル を入れてください");
                var html = await Http.GetStringAsync(url);
                using var data = ExtractJson(html, "ytInitialData");
                if (data != null && data.RootElement.TryGetProperty("metadata", out var m) && m.TryGetProperty("channelMetadataRenderer", out var c))
                    id = Str(c, "externalId");
            }
            if (string.IsNullOrEmpty(id) || !ChannelIdRx.IsMatch(id)) return (null, "チャンネルが見つかりませんでした");
            var page = await GetChannelAsync(id, 1);
            return page.Page == null ? (null, page.Error) : (new YouTubeChannel { Id = id, Name = page.Page.Name }, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, "チャンネルが見つかりませんでした");
        }
        catch (Exception ex)
        {
            return (null, "YouTube に接続できませんでした（" + ex.Message + "）");
        }
    }

    /// <summary>入力をチャンネルのページの URL にする（@ハンドル、/channel/、/c/、/user/）。</summary>
    private static string? ChannelUrl(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var u))
        {
            if (!u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            if (parts[0].StartsWith('@')) return "https://www.youtube.com/" + parts[0];
            if (parts.Length >= 2 && parts[0] is "channel" or "c" or "user") return $"https://www.youtube.com/{parts[0]}/{parts[1]}";
            return null;
        }
        var handle = input.TrimStart('@');
        if (handle.Length == 0 || handle.Any(ch => char.IsWhiteSpace(ch) || ch == '/')) return null;
        return "https://www.youtube.com/@" + Uri.EscapeDataString(handle);
    }

    /// <summary>コピーした文字から動画の ID を取り出す（watch?v=、youtu.be/、/live/、/shorts/、/embed/）。動画でなければ null。</summary>
    public static string? ParseVideoId(string? text)
    {
        text = (text ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        string? id = null;
        if (host == "youtu.be")
        {
            id = u.AbsolutePath.Trim('/');
        }
        else if (host.EndsWith("youtube.com"))
        {
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && parts[0] == "watch")
                id = Query(u, "v");
            else if (parts.Length >= 2 && parts[0] is "live" or "shorts" or "embed")
                id = parts[1];
        }
        return id != null && VideoIdRx.IsMatch(id) ? id : null;
    }

    private static string? Query(Uri u, string key)
    {
        foreach (var kv in u.Query.TrimStart('?').Split('&'))
        {
            var i = kv.IndexOf('=');
            if (i > 0 && kv[..i] == key) return Uri.UnescapeDataString(kv[(i + 1)..]);
        }
        return null;
    }

    // =========================================================== 新着動画・配信中

    /// <summary>チャンネルの「動画」タブから、名前・アイコンと新しい順に max 本（ショートと配信は入らない）。</summary>
    public static async Task<(YouTubeChannelPage? Page, string? Error)> GetChannelAsync(string channelId, int max)
    {
        try
        {
            var html = await Http.GetStringAsync($"https://www.youtube.com/channel/{channelId}/videos");
            using var data = ExtractJson(html, "ytInitialData");
            if (data == null) return (null, "YouTube のページを読めませんでした");

            string name = channelId, avatar = "";
            if (data.RootElement.TryGetProperty("metadata", out var m) && m.TryGetProperty("channelMetadataRenderer", out var c))
            {
                name = Str(c, "title") is { Length: > 0 } t ? t : channelId;
                if (c.TryGetProperty("avatar", out var av) && av.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Array && th.GetArrayLength() > 0)
                    avatar = Str(th[0], "url");
            }
            // アイコンは小さいもので足りる（=s900 → =s88）
            avatar = Regex.Replace(avatar, @"=s\d+", "=s88");

            var now = DateTime.Now;
            var list = new List<YouTubeVideo>();
            foreach (var lockup in FindAll(data.RootElement, "lockupViewModel"))
            {
                if (list.Count >= max) break;
                var id = Str(lockup, "contentId");
                if (!VideoIdRx.IsMatch(id) || list.Any(v => v.VideoId == id)) continue;
                if (Str(lockup, "contentType") is { Length: > 0 } type && type != "LOCKUP_CONTENT_TYPE_VIDEO") continue;
                string title = "", duration = "", age = "";
                if (lockup.TryGetProperty("metadata", out var md) && md.TryGetProperty("lockupMetadataViewModel", out var lm))
                {
                    if (lm.TryGetProperty("title", out var tt)) title = Str(tt, "content");
                    // 2つ目以降の並びの最後が「8 時間前」
                    age = FindAll(lm, "metadataParts").SelectMany(p => p.ValueKind == JsonValueKind.Array ? p.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        .Select(p => p.TryGetProperty("text", out var x) ? Str(x, "content") : "").LastOrDefault(s => AgeRx.IsMatch(s)) ?? "";
                }
                if (lockup.TryGetProperty("contentImage", out var ci))
                    duration = FindAll(ci, "thumbnailBadgeViewModel").Select(b => Str(b, "text")).FirstOrDefault(s => s.Contains(':')) ?? "";
                list.Add(new YouTubeVideo(id, channelId, name, title, duration, now - ParseAge(age)) { Avatar = avatar.Length > 0 ? avatar : null });
            }
            return (new YouTubeChannelPage(channelId, name, avatar.Length > 0 ? avatar : null, list), null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, "チャンネルが見つかりませんでした");
        }
        catch (Exception ex)
        {
            return (null, "YouTube に接続できませんでした（" + ex.Message + "）");
        }
    }

    /// <summary>チャンネルがいま配信中なら、その配信（/channel/〇〇/live を見る）。配信していなければ Video は null。</summary>
    public static async Task<(YouTubeVideo? Video, string? Error)> GetLiveAsync(string channelId)
    {
        try
        {
            var html = await Http.GetStringAsync($"https://www.youtube.com/channel/{channelId}/live");
            using var player = ExtractJson(html, "ytInitialPlayerResponse");
            if (player == null || !player.RootElement.TryGetProperty("videoDetails", out var d)) return (null, null);
            // 配信予定（待機所）は配信中に数えない
            if (!Bool(d, "isLive") || Bool(d, "isUpcoming") || Str(d, "channelId") != channelId) return (null, null);
            var id = Str(d, "videoId");
            if (!VideoIdRx.IsMatch(id)) return (null, null);

            DateTime started = default;
            if (player.RootElement.TryGetProperty("microformat", out var mf) && mf.TryGetProperty("playerMicroformatRenderer", out var pm) &&
                pm.TryGetProperty("liveBroadcastDetails", out var lb) &&
                DateTime.TryParse(Str(lb, "startTimestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var st))
                started = st.ToLocalTime();
            var viewers = Regex.Match(html, "\"originalViewCount\":\"(\\d+)\"");
            return (new YouTubeVideo(id, channelId, Str(d, "author"), Str(d, "title"), "", started == default ? DateTime.Now : started)
            {
                Live = true,
                Viewers = viewers.Success && int.TryParse(viewers.Groups[1].Value, out var n) ? n : 0,
                StartedAt = started,
            }, null);
        }
        catch (Exception ex)
        {
            return (null, "YouTube に接続できませんでした（" + ex.Message + "）");
        }
    }

    /// <summary>「8 時間前」「2 週間前」などをおおよその時間にする（読めなければ0）。</summary>
    private static TimeSpan ParseAge(string text)
    {
        var m = AgeRx.Match(text ?? "");
        if (!m.Success) return TimeSpan.Zero;
        double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "秒" => TimeSpan.FromSeconds(n),
            "分" => TimeSpan.FromMinutes(n),
            "時間" => TimeSpan.FromHours(n),
            "日" => TimeSpan.FromDays(n),
            "週間" => TimeSpan.FromDays(n * 7),
            "か月" or "ヶ月" => TimeSpan.FromDays(n * 30),
            _ => TimeSpan.FromDays(n * 365),
        };
    }

    // =========================================================== ページの中の JSON

    /// <summary>ページに埋め込まれた「var 名前 = {...};」の JSON を読む（見つからなければ null）。</summary>
    private static JsonDocument? ExtractJson(string html, string name)
    {
        int i = html.IndexOf(name + " = {", StringComparison.Ordinal);
        if (i < 0) i = html.IndexOf(name + "={", StringComparison.Ordinal);
        if (i < 0) return null;
        i = html.IndexOf('{', i);
        try
        {
            // 1つの値だけを読む（後ろに続くスクリプトは読まない）
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html.Substring(i)), new JsonReaderOptions { AllowTrailingCommas = true });
            return JsonDocument.ParseValue(ref reader);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>JSON の中から、名前が name の値を出てくる順にすべて探す。</summary>
    private static IEnumerable<JsonElement> FindAll(JsonElement e, string name)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(e);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                var children = new List<JsonElement>();
                foreach (var p in cur.EnumerateObject())
                {
                    if (p.Name == name) yield return p.Value;
                    else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) children.Add(p.Value);
                }
                for (int k = children.Count - 1; k >= 0; k--) stack.Push(children[k]);
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                var items = cur.EnumerateArray().ToList();
                for (int k = items.Count - 1; k >= 0; k--) stack.Push(items[k]);
            }
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
