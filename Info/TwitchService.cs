using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyDashboard.Info;

/// <summary>Twitch のログイン情報（twitch-auth.json に保存）。</summary>
public sealed record TwitchAuth(string AccessToken, string UserId, string Login);

/// <summary>フォロー中で配信している人の1件。</summary>
public sealed record TwitchStream(string UserId, string Login, string Name, string Game, string Title, int Viewers, DateTime StartedAt)
{
    /// <summary>アイコンの URL（取れなかったときは null）。</summary>
    public string? Avatar { get; init; }
}

/// <summary>フォロー中の配信一覧の取得結果。NeedLogin なら設定画面でログインし直す。</summary>
public sealed record TwitchFollowed(IReadOnlyList<TwitchStream> Streams, string? Error, bool NeedLogin);

/// <summary>
/// Twitch 公式 API（Helix）。dev.twitch.tv で登録したアプリの Client ID（既定は依頼者のアプリ。設定で自分の ID に変えられる）と、
/// ログインで受け取ったアクセストークン（Implicit grant、scope=user:read:follows）で呼ぶ。
/// </summary>
public static class TwitchService
{
    /// <summary>dev.twitch.tv のアプリ登録で「OAuth Redirect URLs」に入れる値。ログイン画面はここへの移動を横取りしてトークンを受け取る。</summary>
    public const string RedirectUri = "http://localhost";
    public const string RegisterUrl = "https://dev.twitch.tv/console/apps/create";
    /// <summary>
    /// 組み込みの Client ID（依頼者が登録したアプリ。利用者が自分でアプリを登録しなくて済むように）。
    /// Implicit grant なので Client Secret は使わず、Client ID は公開してよい値。API の回数制限はログインした利用者ごとに数えられる。
    /// </summary>
    public const string DefaultClientId = "h8opdk2mciz3xd869am8n6tn2b5oo8";

    /// <summary>設定の Client ID が空なら組み込みの ID を使う。</summary>
    public static string ResolveClientId(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultClientId : configured.Trim();

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, string> AvatarCache = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string AuthPath => AppPaths.Data("twitch-auth.json");

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MyDashboard/1.0 (+personal desktop dashboard)");
        return http;
    }

    // =========================================================== ログイン情報

    public static TwitchAuth? LoadAuth()
    {
        try
        {
            if (!File.Exists(AuthPath)) return null;
            var a = JsonSerializer.Deserialize<TwitchAuth>(File.ReadAllText(AuthPath), JsonOptions);
            return a is { AccessToken.Length: > 0 } ? a : null;
        }
        catch { return null; }
    }

    public static void SaveAuth(TwitchAuth auth)
    {
        try { File.WriteAllText(AuthPath, JsonSerializer.Serialize(auth, JsonOptions)); }
        catch { }
    }

    public static void ClearAuth()
    {
        try { File.Delete(AuthPath); }
        catch { }
    }

    /// <summary>ログイン画面の URL。forceVerify なら別のアカウントを選び直せるよう毎回確認画面を出す。</summary>
    public static string AuthorizeUrl(string clientId, string state, bool forceVerify) =>
        "https://id.twitch.tv/oauth2/authorize?response_type=token" +
        "&client_id=" + Uri.EscapeDataString(clientId) +
        "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
        "&scope=" + Uri.EscapeDataString("user:read:follows") +
        "&state=" + Uri.EscapeDataString(state) +
        (forceVerify ? "&force_verify=true" : "");

    /// <summary>トークンが使えるか確かめ、ユーザー ID とログイン名を返す。期限切れなどで使えなければ null。通信できないときは例外。</summary>
    public static async Task<TwitchAuth?> ValidateAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
        using var res = await Http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        var id = Str(r, "user_id");
        return id.Length == 0 ? null : new TwitchAuth(token, id, Str(r, "login"));
    }

    // =========================================================== フォロー中の配信

    public static async Task<TwitchFollowed> GetFollowedLiveAsync(string clientId, TwitchAuth? auth)
    {
        if (auth == null)
            return new(Array.Empty<TwitchStream>(), "設定画面の「Twitch」でログインしてください", true);

        try
        {
            using var doc = await Helix(clientId, auth, "streams/followed?first=100&user_id=" + Uri.EscapeDataString(auth.UserId));
            if (doc == null)
                return new(Array.Empty<TwitchStream>(), "Twitch のログインの有効期限が切れました。設定画面からログインし直してください", true);

            var list = new List<TwitchStream>();
            foreach (var s in Data(doc))
            {
                DateTime.TryParse(Str(s, "started_at"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var started);
                list.Add(new TwitchStream(Str(s, "user_id"), Str(s, "user_login"), Str(s, "user_name"), Str(s, "game_name"), Str(s, "title"),
                    s.TryGetProperty("viewer_count", out var v) && v.TryGetInt32(out var n) ? n : 0, started.ToLocalTime()));
            }

            await FillAvatars(clientId, auth, list);
            return new(list.Select(x => x with { Avatar = AvatarCache.GetValueOrDefault(x.UserId) }).ToList(), null, false);
        }
        catch (Exception ex)
        {
            return new(Array.Empty<TwitchStream>(), "Twitch に接続できませんでした（" + ex.Message + "）", false);
        }
    }

    /// <summary>アイコンの URL を /helix/users でまとめて取る（一度取った人は覚えておく）。</summary>
    private static async Task FillAvatars(string clientId, TwitchAuth auth, List<TwitchStream> list)
    {
        var missing = list.Select(x => x.UserId).Where(id => id.Length > 0 && !AvatarCache.ContainsKey(id)).Distinct().Take(100).ToList();
        if (missing.Count == 0) return;
        try
        {
            using var doc = await Helix(clientId, auth, "users?" + string.Join("&", missing.Select(id => "id=" + Uri.EscapeDataString(id))));
            if (doc == null) return;
            foreach (var u in Data(doc))
                AvatarCache[Str(u, "id")] = Str(u, "profile_image_url");
        }
        catch { /* アイコンがなくても一覧は出す */ }
    }

    /// <summary>Helix を呼ぶ。トークンが使えない（401）ときは null。</summary>
    private static async Task<JsonDocument?> Helix(string clientId, TwitchAuth auth, string pathAndQuery)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/" + pathAndQuery);
        req.Headers.TryAddWithoutValidation("Client-Id", clientId.Trim());
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.AccessToken);
        using var res = await Http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static IEnumerable<JsonElement> Data(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
