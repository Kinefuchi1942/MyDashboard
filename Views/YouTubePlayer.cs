using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MyDashboard.Info;

namespace MyDashboard.Views;

/// <summary>
/// オーロラの YouTube 表示で使うプレーヤー1つ分（YouTube 公式の埋め込みプレーヤー）。
/// 拡張表示のときは右側に、配信ならライブチャット、動画なら同じチャンネルの新着を出す。
/// </summary>
public sealed class YouTubePlayer : WebPlayer
{
    public YouTubePlayer() : base("youtube.html", "YouTube") { }

    /// <summary>動画を再生する。最初の1本は消音（音はプレーヤーのスピーカーのボタンで出す）。</summary>
    public void Play(string videoId, bool live, bool muted) => Run($"dash.play({Js(videoId)}, {Js(live)}, {Js(muted)})");

    /// <summary>拡張表示で動画の右に並べる一覧（見出しと動画）。</summary>
    public void SetSide(string heading, IEnumerable<YouTubeVideo> videos)
    {
        var items = videos.Select(v => new { id = v.VideoId, title = v.Title, dur = v.Duration, age = AuroraView.Ago(v.Published) });
        Run($"dash.side({Js(heading)}, {JsonSerializer.Serialize(items)})");
    }
}

/// <summary>YouTube のプレーヤーのページ（TwitchWeb.PageFolder が youtube.html として書き出す）。</summary>
public static class YouTubePage
{
    public const string Html = """
<!doctype html>
<html><head><meta charset="utf-8">
<style>
  html, body { margin: 0; height: 100%; overflow: hidden; background: #0E0E14; font-family: "Yu Gothic UI", sans-serif; }
  #wrap { display: flex; height: 100%; }
  #player { flex: 1; min-width: 0; height: 100%; }
  #player iframe { display: block; width: 100%; height: 100%; border: 0; }
  #chat, #side { display: none; height: 100%; border: 0; border-left: 1px solid #26262E; background: #0F0F0F; box-sizing: border-box; }
  body.wide.live #chat { display: block; width: var(--side, 30%); }
  body.wide:not(.live) #side { display: block; width: var(--side, 30%); overflow-y: auto; padding: 14px 16px; }
  #side::-webkit-scrollbar { width: 8px; } #side::-webkit-scrollbar-thumb { background: #3A3A44; border-radius: 4px; }
  #side h2 { margin: 0 0 12px; font-size: 18px; font-weight: 600; color: #F1F1F1; }
  .it { display: flex; gap: 12px; padding: 6px; margin: 0 -6px 4px; border-radius: 8px; cursor: pointer; }
  .it:hover { background: #26262E; }
  .th { position: relative; flex: none; width: 168px; height: 94px; border-radius: 8px; background: #26262E center/cover no-repeat; }
  .th span { position: absolute; right: 4px; bottom: 4px; background: rgba(0,0,0,.8); color: #fff; font: 600 12px Bahnschrift, sans-serif; padding: 1px 4px; border-radius: 4px; }
  .tx { min-width: 0; }
  .tt { color: #F1F1F1; font-size: 16px; line-height: 1.35; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
  .ag { color: #AAAAAA; font-size: 13px; margin-top: 4px; }
  #side p { color: #888; font-size: 15px; }
</style>
</head><body>
<div id="wrap"><div id="player"><div id="yt"></div></div><iframe id="chat" title="chat"></iframe><div id="side"></div></div>
<script>
  const host = location.hostname;
  let player = null, ready = false, queued = null, waitId = null, video = null, live = false, chatFor = null;
  function post(m) { try { chrome.webview.postMessage(m); } catch (e) { } }

  function loadChat() {
    if (!video || !live || chatFor === video) return;
    chatFor = video;
    document.getElementById('chat').src =
      'https://www.youtube.com/live_chat?v=' + encodeURIComponent(video) + '&embed_domain=' + host + '&dark_theme=1';
  }
  function clearChat() { chatFor = null; document.getElementById('chat').src = 'about:blank'; }

  function info() {
    try {
      const d = player.getVideoData();
      // コピーした URL の動画は、配信かどうかを再生が始まってから知る
      if (d.video_id === video && !!d.isLive !== live) {
        live = !!d.isLive;
        document.body.classList.toggle('live', live);
        if (document.body.classList.contains('wide')) { if (live) loadChat(); else clearChat(); }
      }
      post('info:' + JSON.stringify({ id: d.video_id || '', title: d.title || '', author: d.author || '', live: !!d.isLive }));
    } catch (e) { }
  }

  function start(id, muted) {
    if (!player) {
      player = new YT.Player('yt', {
        width: '100%', height: '100%', videoId: id,
        playerVars: { autoplay: 1, mute: muted ? 1 : 0, playsinline: 1, rel: 0, origin: location.origin },
        events: {
          onReady: e => { ready = true; if (muted) e.target.mute(); e.target.playVideo(); },
          onStateChange: e => { post('state:' + e.data); if (e.data === 1) info(); },
          onError: e => post('yterror:' + e.data),
        },
      });
    } else if (ready) {
      player.loadVideoById(id);
    } else {
      // まだ準備中：準備ができてから読み込む
      if (!waitId) { const t = setInterval(() => { if (ready) { clearInterval(t); player.loadVideoById(waitId); waitId = null; } }, 200); }
      waitId = id;
    }
  }

  window.dash = {
    play(id, isLive, muted) {
      video = id; live = isLive;
      document.body.classList.toggle('live', live);
      if (typeof YT === 'undefined' || !YT.Player) { queued = { id, muted }; return; }
      start(id, muted);
      if (document.body.classList.contains('wide')) { if (live) loadChat(); else clearChat(); }
    },
    wide(on, sidePercent) {
      document.body.style.setProperty('--side', sidePercent + '%');
      document.body.classList.toggle('wide', on);
      if (on && live) loadChat(); else clearChat();
    },
    side(heading, items) {
      const box = document.getElementById('side');
      box.textContent = '';
      const h = document.createElement('h2'); h.textContent = heading; box.appendChild(h);
      if (!items.length) { const p = document.createElement('p'); p.textContent = 'ほかの動画はありません'; box.appendChild(p); }
      for (const v of items) {
        const it = document.createElement('div'); it.className = 'it';
        const th = document.createElement('div'); th.className = 'th';
        th.style.backgroundImage = 'url(https://i.ytimg.com/vi/' + encodeURIComponent(v.id) + '/mqdefault.jpg)';
        if (v.dur) { const s = document.createElement('span'); s.textContent = v.dur; th.appendChild(s); }
        const tx = document.createElement('div'); tx.className = 'tx';
        const tt = document.createElement('div'); tt.className = 'tt'; tt.textContent = v.title;
        const ag = document.createElement('div'); ag.className = 'ag'; ag.textContent = v.age;
        tx.appendChild(tt); tx.appendChild(ag);
        it.appendChild(th); it.appendChild(tx);
        it.onclick = () => post('pick:' + v.id);
        box.appendChild(it);
      }
    },
  };

  window.onYouTubeIframeAPIReady = () => {
    if (queued) { const q = queued; queued = null; start(q.id, q.muted); if (document.body.classList.contains('wide') && live) loadChat(); }
  };
  post('loaded');
</script>
<script src="https://www.youtube.com/iframe_api" onerror="post('error:YouTube のプレーヤーを読み込めませんでした')"></script>
</body></html>
""";
}
