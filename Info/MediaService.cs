using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinMedia = global::Windows.Media.Control;

namespace MyDashboard.Info;

/// <summary>
/// Windows の「再生中のメディア」（音量ボタンを押すと出る曲名の窓と同じ情報源）から、
/// Spotify などで再生中の曲を読む。ログインや API キーは不要。
/// ・曲が変わった／再生・一時停止したときに Windows から通知が来る仕組みなので、常時の負荷はほぼない
/// ・再生位置は Windows 側の最終更新時刻から計算で進める（Spotify は数秒おきにしか知らせないため）
/// ・状況を publish\media-diagnostics.txt に書き出す
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WinMedia.GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<WinMedia.GlobalSystemMediaTransportControlsSession> _watched = new();
    private WinMedia.GlobalSystemMediaTransportControlsSession? _current;

    // ジャケット画像は曲が変わったときだけ読み直す
    private string? _artKey;
    private ImageSource? _art;
    private Color? _artColor;
    private string? _lastError;
    private bool _disposed;

    /// <summary>再生中の曲が変わったとき（何も再生していなければ null）。別スレッドから呼ばれる。</summary>
    public event Action<NowPlaying?>? Updated;

    public MediaService(AppSettings settings) => _settings = settings;

    public async void Start()
    {
        if (!_settings.EnableMedia) return;
        try
        {
            _manager = await WinMedia.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            WatchSessions();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _lastError = "開始できませんでした: " + ex.Message;
            WriteDiagnostics();
            Updated?.Invoke(null);
        }
    }

    // =========================================================== 操作（ボタン用）

    public Task PlayPauseAsync() => Control(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Control(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Control(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Control(Func<WinMedia.GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var s = _current;
        if (s == null) return;
        try { await action(s); }
        catch { /* 相手のアプリが閉じた直後など。次の通知で表示が直る */ }
    }

    // =========================================================== 通知の受け取り

    private void OnSessionsChanged(WinMedia.GlobalSystemMediaTransportControlsSessionManager sender, WinMedia.SessionsChangedEventArgs args)
    {
        WatchSessions();
        _ = RefreshAsync();
    }

    private void OnCurrentSessionChanged(WinMedia.GlobalSystemMediaTransportControlsSessionManager sender, WinMedia.CurrentSessionChangedEventArgs args) => _ = RefreshAsync();
    private void OnMediaChanged(WinMedia.GlobalSystemMediaTransportControlsSession sender, WinMedia.MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();
    private void OnPlaybackChanged(WinMedia.GlobalSystemMediaTransportControlsSession sender, WinMedia.PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();
    private void OnTimelineChanged(WinMedia.GlobalSystemMediaTransportControlsSession sender, WinMedia.TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    /// <summary>再生アプリ（セッション）の一覧が変わったら、全部の通知を受け取り直す。</summary>
    private void WatchSessions()
    {
        if (_manager == null) return;
        lock (_watched)
        {
            foreach (var s in _watched)
            {
                s.MediaPropertiesChanged -= OnMediaChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _watched.Clear();
            try
            {
                foreach (var s in _manager.GetSessions())
                {
                    s.MediaPropertiesChanged += OnMediaChanged;
                    s.PlaybackInfoChanged += OnPlaybackChanged;
                    s.TimelinePropertiesChanged += OnTimelineChanged;
                    _watched.Add(s);
                }
            }
            catch (Exception ex) { _lastError = "セッション一覧: " + ex.Message; }
        }
        WriteDiagnostics();
    }

    // =========================================================== 状態の読み取り

    private async Task RefreshAsync()
    {
        if (_disposed || _manager == null) return;
        await _gate.WaitAsync();
        try
        {
            var s = Pick();
            _current = s;
            if (s == null) { Updated?.Invoke(null); return; }

            var playback = s.GetPlaybackInfo();
            var status = playback.PlaybackStatus;
            bool playing = status == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool paused = status == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            if (!playing && !paused) { Updated?.Invoke(null); return; }

            var props = await s.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) { Updated?.Invoke(null); return; }

            string app = AppName(s.SourceAppUserModelId);
            string key = $"{s.SourceAppUserModelId}|{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key != _artKey)
            {
                _art = null;
                _artColor = null;
                // Spotify は曲名より少し遅れて画像を送ってくることがある。取れなかったら次の通知で再挑戦する
                if (props.Thumbnail != null && await LoadArtAsync(props.Thumbnail) is { } art)
                {
                    _art = art;
                    _artColor = AccentOf(art);
                    _artKey = key;
                }
            }

            var timeline = s.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            var updatedAt = timeline.LastUpdatedTime;
            var positionAt = updatedAt.Year < 2000 || updatedAt > DateTimeOffset.Now
                ? DateTime.Now
                : updatedAt.LocalDateTime;

            _lastError = null;
            Updated?.Invoke(new NowPlaying(
                app, props.Title, props.Artist ?? "", props.AlbumTitle ?? "", playing,
                position < TimeSpan.Zero ? TimeSpan.Zero : position,
                duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                positionAt, playback.PlaybackRate ?? 1.0, _art, _artColor));
        }
        catch (Exception ex)
        {
            _lastError = "読み取り: " + ex.Message;
            WriteDiagnostics();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 表示する再生アプリを選ぶ。mediaSpotifyOnly が true なら Spotify だけ、
    /// false なら再生中のものを優先して、Windows が「現在」とみなすもの → その他の順。
    /// </summary>
    private WinMedia.GlobalSystemMediaTransportControlsSession? Pick()
    {
        var all = _manager!.GetSessions().ToList();
        var current = _manager.GetCurrentSession();
        var candidates = all.Where(s => !_settings.MediaSpotifyOnly || IsSpotify(s.SourceAppUserModelId)).ToList();
        if (candidates.Count == 0) return null;

        if (current != null)
        {
            int i = candidates.FindIndex(s => s.SourceAppUserModelId == current.SourceAppUserModelId);
            if (i > 0) { var c = candidates[i]; candidates.RemoveAt(i); candidates.Insert(0, c); }
        }
        return candidates.FirstOrDefault(IsPlaying) ?? candidates[0];
    }

    private static bool IsPlaying(WinMedia.GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    private static bool IsSpotify(string? aumid) => aumid?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>アプリの識別名（例：Spotify.exe、MSEdge）を表示用の名前にする。</summary>
    private static string AppName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "メディア";
        if (IsSpotify(aumid)) return "Spotify";
        var a = aumid.ToLowerInvariant();
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("firefox")) return "Firefox";
        var name = aumid.Split('!')[^1];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length > 20 ? name[..20] : name;
    }

    // =========================================================== ジャケット画像

    private static async Task<ImageSource?> LoadArtAsync(global::Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await input.CopyToAsync(ms);
            if (ms.Length == 0) return null;
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 200; // 表示は96px。高DPIでもにじまない程度に縮めて読む
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ジャケットの代表色（背景の光に使う）。鮮やかな画素ほど重く数え、
    /// 暗い部屋でまぶしくならないよう明るさをそろえる。
    /// </summary>
    private static Color? AccentOf(ImageSource image)
    {
        try
        {
            if (image is not BitmapSource src) return null;
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double weight = 0.05 + sat * sat * max;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            if (total <= 0) return null;
            r /= total; g /= total; b /= total;

            // 最大成分を 0.85 にそろえる（真っ黒・真っ白なジャケットでも光として見えるように）
            double peak = Math.Max(r, Math.Max(g, b));
            if (peak < 0.01) return null;
            double k = 0.85 / peak;
            return Color.FromRgb((byte)(Math.Min(1, r * k) * 255), (byte)(Math.Min(1, g * k) * 255), (byte)(Math.Min(1, b * k) * 255));
        }
        catch
        {
            return null;
        }
    }

    // =========================================================== 診断

    private void WriteDiagnostics()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"更新時刻: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Spotify のみ表示: {_settings.MediaSpotifyOnly}");
            if (_lastError != null) sb.AppendLine($"エラー: {_lastError}");
            sb.AppendLine("再生アプリ一覧:");
            lock (_watched)
            {
                if (_watched.Count == 0) sb.AppendLine("  （なし）");
                foreach (var s in _watched)
                {
                    string status;
                    try { status = s.GetPlaybackInfo().PlaybackStatus.ToString(); }
                    catch { status = "?"; }
                    sb.AppendLine($"  {s.SourceAppUserModelId}  [{status}]");
                }
            }
            File.WriteAllText(AppPaths.Data("media-diagnostics.txt"), sb.ToString());
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_manager != null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        lock (_watched)
        {
            foreach (var s in _watched)
            {
                s.MediaPropertiesChanged -= OnMediaChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _watched.Clear();
        }
    }
}
