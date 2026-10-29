using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyDashboard.Info;
using MyDashboard.Views;

namespace MyDashboard;

/// <summary>
/// Twitch のログイン画面（設定画面の「Twitch」から開く）。
/// ログインが済むと Twitch が http://localhost/#access_token=… へ移動させるので、その移動を止めてトークンを受け取る。
/// 同じ WebView2 のフォルダを使うため、ここでログインするとプレーヤーとチャットもログイン状態になる。
/// </summary>
public sealed class TwitchLoginWindow : Window
{
    private readonly string _clientId;
    private readonly string _state = Guid.NewGuid().ToString("N");
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(16, 10, 16, 10),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0xCF, 0xDB)),
        TextWrapping = TextWrapping.Wrap,
        Text = "Twitch のログイン画面を開いています…",
    };
    private bool _done;

    /// <summary>ログインできたときのログイン情報。</summary>
    public TwitchAuth? Result { get; private set; }

    public TwitchLoginWindow(string clientId)
    {
        _clientId = clientId.Trim();
        Title = "Twitch にログイン";
        Width = 560;
        Height = 760;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x13, 0x1A));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(_web);
        Content = root;
        Loaded += async (_, _) =>
        {
            try
            {
                await _web.EnsureCoreWebView2Async(await TwitchWeb.GetEnvironmentAsync());
                _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _web.CoreWebView2.NavigationStarting += OnNavigationStarting;
                _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; _web.CoreWebView2.Navigate(e.Uri); };
                _web.CoreWebView2.Navigate(TwitchService.AuthorizeUrl(_clientId, _state, forceVerify: true));
                _status.Text = "Twitch のアカウントでログインし、「許可」を押してください";
            }
            catch (Exception ex)
            {
                _status.Text = "ログイン画面を開けませんでした（WebView2：" + ex.Message + "）";
            }
        };
        Closed += (_, _) => { try { _web.Dispose(); } catch { } };
    }

    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_done || !e.Uri.StartsWith(TwitchService.RedirectUri, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        _done = true;

        // 成功：http://localhost/#access_token=…&state=…　失敗：http://localhost/?error=…&error_description=…
        var uri = new Uri(e.Uri);
        var values = (uri.Fragment.TrimStart('#') + "&" + uri.Query.TrimStart('?'))
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')) : "");

        if (!values.TryGetValue("access_token", out var token) || token.Length == 0 || values.GetValueOrDefault("state") != _state)
        {
            var why = values.GetValueOrDefault("error_description") ?? values.GetValueOrDefault("error") ?? "トークンを受け取れませんでした";
            _status.Text = "ログインできませんでした（" + why + "）。Client ID と、アプリ登録の Redirect URL（http://localhost）を確かめてください";
            return;
        }

        _status.Text = "ログインを確認しています…";
        try
        {
            var auth = await TwitchService.ValidateAsync(token);
            if (auth == null)
            {
                _status.Text = "受け取ったトークンが使えませんでした。もう一度お試しください";
                return;
            }
            TwitchService.SaveAuth(auth);
            Result = auth;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _status.Text = "ログインを確認できませんでした（" + ex.Message + "）";
        }
    }
}
