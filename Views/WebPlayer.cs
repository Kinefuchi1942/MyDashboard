using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MyDashboard.Views;

/// <summary>
/// オーロラのメディアカードで映像を出す WebView2 1つ分（Twitch・YouTube 共通）。
/// ページ（TwitchWeb.PageFolder に書き出したもの）を https://mydashboard.local/ で開き、
/// アプリからは window.dash の関数を呼び、ページからは chrome.webview.postMessage で状態を知らせる。
/// ニュースに戻すときは Dispose して WebView2 ごと片付ける（メモリを返す）。
/// </summary>
public abstract class WebPlayer : IDisposable
{
    private readonly List<string> _pending = new();
    private readonly string _page, _label;
    private bool _loaded, _disposed;

    public WebView2 View { get; }

    /// <summary>ページからの知らせ（"loaded" 以外）。UI スレッドで呼ばれる。"error:…" は表示できなかったとき。</summary>
    public event Action<string>? Message;

    protected WebPlayer(string page, string label)
    {
        _page = page;
        _label = label;
        View = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0E, 0x0E, 0x14),
            Focusable = false,
        };
    }

    /// <summary>View を画面に置いてから呼ぶ（WebView2 は画面に出てから初期化が終わる）。</summary>
    public async Task StartAsync()
    {
        try
        {
            var env = await TwitchWeb.GetEnvironmentAsync();
            if (_disposed) return;
            await View.EnsureCoreWebView2Async(env);
            if (_disposed) return;

            var core = View.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            Configure(core);
            core.SetVirtualHostNameToFolderMapping(TwitchWeb.Host, TwitchWeb.PageFolder(), CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (_, e) =>
            {
                // 「Twitch で見る」「YouTube で見る」などのリンクは通常のブラウザで開く
                e.Handled = true;
                TwitchWeb.OpenExternal(e.Uri);
            };
            core.WebMessageReceived += (_, e) =>
            {
                string m;
                try { m = e.TryGetWebMessageAsString() ?? ""; }
                catch { return; }
                if (m == "loaded") Flush();
                else Message?.Invoke(m);
            };
            core.Navigate($"https://{TwitchWeb.Host}/{_page}");
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Message?.Invoke($"error:{_label} を表示できませんでした（WebView2：{ex.Message}）");
        }
    }

    /// <summary>ページを開く前の、プレーヤーごとの設定。</summary>
    protected virtual void Configure(CoreWebView2 core) { }

    private void Flush()
    {
        _loaded = true;
        foreach (var js in _pending) Exec(js);
        _pending.Clear();
    }

    protected void Run(string js)
    {
        if (_disposed) return;
        if (!_loaded) { _pending.Add(js); return; }
        Exec(js);
    }

    private void Exec(string js)
    {
        try { _ = View.CoreWebView2?.ExecuteScriptAsync(js); }
        catch { }
    }

    protected static string Js(string s) => JsonSerializer.Serialize(s);
    protected static string Js(bool b) => b ? "true" : "false";

    /// <summary>拡張表示（右にチャットなど）。sidePercent はページ幅に対する右側の幅（%）。</summary>
    public void SetWide(bool wide, double sidePercent) =>
        Run($"dash.wide({Js(wide)}, {Math.Clamp(sidePercent, 15, 60).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Clear();
        try { View.Dispose(); }
        catch { }
    }
}
