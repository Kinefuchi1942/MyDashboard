using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MyDashboard.Views;

/// <summary>
/// WebView2（Edge の部品）の共通設定。Twitch の埋め込みプレーヤーは親ページのドメイン（parent）が必要なため、
/// ローカルのページを https://mydashboard.local/ という仮のドメインで開く。
/// ログイン（Cookie）は publish\webview2 に保存され、プレーヤー・チャット・ログイン画面で共有する。
/// </summary>
public static class TwitchWeb
{
    public const string Host = "mydashboard.local";
    private static Task<CoreWebView2Environment>? _env;

    public static string DataFolder => AppPaths.Local("webview2");

    /// <summary>WebView2 の実行環境。同じフォルダを使う WebView2 は同じ設定で作る必要があるので1つを使い回す。</summary>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_env is { IsFaulted: true } or { IsCanceled: true }) _env = null; // 失敗したら次に呼ばれたときにやり直す
        return _env ??= CoreWebView2Environment.CreateAsync(null, DataFolder, new CoreWebView2EnvironmentOptions
        {
            // 消音での自動再生は既定でも通るが、念のため操作なしでの再生を許可する
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            // 埋め込みのチャットでログイン状態（Twitch の Cookie）を使えるようにする
            EnableTrackingPrevention = false,
        });
    }

    /// <summary>プレーヤーのページを書き出したフォルダ（毎回上書きするので、更新すると新しいページになる）。</summary>
    public static string PageFolder()
    {
        var dir = Path.Combine(DataFolder, "page");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "player.html"), PlayerHtml);
        File.WriteAllText(Path.Combine(dir, "youtube.html"), YouTubePage.Html);
        return dir;
    }

    /// <summary>リンクは通常のブラウザで開く（このアプリは管理者権限なので explorer 経由にする）。</summary>
    public static void OpenExternal(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return;
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + u.AbsoluteUri + "\""); }
        catch { }
    }

    /// <summary>
    /// 映像（Twitch 公式の埋め込みプレーヤー）と、拡張表示のときだけ右に出すチャット。
    /// アプリからは window.dash の関数を呼び、ページからは chrome.webview.postMessage で状態を知らせる。
    /// </summary>
    private const string PlayerHtml = """
<!doctype html>
<html><head><meta charset="utf-8">
<style>
  html, body { margin: 0; height: 100%; overflow: hidden; background: #0E0E14; }
  #wrap { display: flex; height: 100%; }
  #player { flex: 1; min-width: 0; height: 100%; }
  #player iframe { display: block; }
  #chat { display: none; height: 100%; border: 0; border-left: 1px solid #23232F; background: #18181B; }
  body.wide #chat { display: block; width: var(--chat, 30%); }
</style>
<script src="https://player.twitch.tv/js/embed/v1.js"></script>
</head><body>
<div id="wrap"><div id="player"></div><iframe id="chat" title="chat"></iframe></div>
<script>
  const host = location.hostname;
  let player = null, channel = null, chatFor = null, low = false;
  function post(m) { try { chrome.webview.postMessage(m); } catch (e) { } }

  function loadChat() {
    if (!channel || chatFor === channel) return;
    chatFor = channel;
    document.getElementById('chat').src =
      'https://www.twitch.tv/embed/' + encodeURIComponent(channel) + '/chat?parent=' + host + '&darkpopout';
  }

  // ゲーム中は画質を下げて、回線と GPU の負担を減らす（360p 未満には下げない）。
  // 画質を変えると読み込み直しになり PLAYING がまた来るので、設定するのはチャンネルごと・切り替えごとに1回だけ。
  let applied = false;
  function qualityHeight(q) {
    const m = /(\d+)p/.exec(q.group || '') || /(\d+)p/.exec(q.name || '');
    return m ? parseInt(m[1], 10) : (q.height || 0);
  }
  function applyQuality() {
    if (!player || applied) return;
    try {
      let target = 'auto';
      if (low) {
        const qs = (player.getQualities() || [])
          .filter(q => q.group && q.group !== 'auto' && q.group !== 'chunked' && q.group !== 'audio_only');
        if (!qs.length) return; // まだ画質の一覧が来ていない。次の PLAYING で試す
        qs.sort((a, b) => qualityHeight(a) - qualityHeight(b) || (a.bitrate || 0) - (b.bitrate || 0));
        const ok = qs.filter(q => qualityHeight(q) >= 360);
        target = (ok.length ? ok[0] : qs[qs.length - 1]).group;
      }
      applied = true;
      if (player.getQuality() !== target) player.setQuality(target);
    } catch (e) { }
  }

  window.dash = {
    play(ch, muted) {
      channel = ch;
      if (typeof Twitch === 'undefined') { post('error:Twitch のプレーヤーを読み込めませんでした'); return; }
      if (!player) {
        player = new Twitch.Player('player', { channel: ch, parent: [host], width: '100%', height: '100%', muted: muted, autoplay: true });
        player.addEventListener(Twitch.Player.ONLINE, () => post('online'));
        player.addEventListener(Twitch.Player.OFFLINE, () => post('offline'));
        player.addEventListener(Twitch.Player.PLAYING, () => { post('playing'); if (low) applyQuality(); });
      } else {
        player.setChannel(ch);
        applied = false; // チャンネルを変えると画質は自動に戻るので、低画質ならもう一度設定する
      }
      if (document.body.classList.contains('wide')) loadChat();
    },
    wide(on, chatPercent) {
      document.body.style.setProperty('--chat', chatPercent + '%');
      document.body.classList.toggle('wide', on);
      if (on) loadChat();
      else { chatFor = null; document.getElementById('chat').src = 'about:blank'; }
    },
    low(on) { if (low !== on) { low = on; applied = false; applyQuality(); } },
  };
  post('loaded');
</script>
</body></html>
""";
}

/// <summary>オーロラの Twitch 表示で使うプレーヤー1つ分。ニュースに戻すときは Dispose して WebView2 ごと片付ける（メモリを返す）。</summary>
public sealed class TwitchPlayer : WebPlayer
{
    public TwitchPlayer() : base("player.html", "Twitch") { }

    public void Play(string login, bool muted) => Run($"dash.play({Js(login)}, {Js(muted)})");

    public void SetLowQuality(bool on) => Run($"dash.low({Js(on)})");
}
