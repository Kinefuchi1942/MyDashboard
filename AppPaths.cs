using System;
using System.IO;

namespace MyDashboard;

/// <summary>
/// 設定や記録を置く場所。exe の隣ではなく Windows のユーザーごとのフォルダに置く
/// （exe 1つで配布するので、exe をどこに置いても・置き直しても設定が残るように）。
/// </summary>
public static class AppPaths
{
    /// <summary>%APPDATA%\MyDashboard：settings.json・twitch-auth.json・power-log.json・診断ファイル。</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyDashboard");

    /// <summary>%LOCALAPPDATA%\MyDashboard：WebView2 のデータと PresentMon.exe（大きく、ほかの PC へ持ち回らなくてよいもの）。</summary>
    public static string LocalFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyDashboard");

    public static string Data(string name) => Path.Combine(DataFolder, name);
    public static string Local(string name) => Path.Combine(LocalFolder, name);

    /// <summary>
    /// フォルダを用意し、以前の版が exe の隣に作ったファイルがあれば移す（起動時に1回）。
    /// 移し先に同じものがあればそちらを優先して、exe の隣のものは残す。
    /// </summary>
    public static void Prepare()
    {
        try { Directory.CreateDirectory(DataFolder); } catch { }
        try { Directory.CreateDirectory(LocalFolder); } catch { }

        var old = AppContext.BaseDirectory;
        foreach (var name in new[] { "settings.json", "twitch-auth.json", "power-log.json" })
            MoveFile(Path.Combine(old, name), Data(name));
        MoveFolder(Path.Combine(old, "webview2"), Local("webview2"));
    }

    private static void MoveFile(string from, string to)
    {
        try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
        catch { /* 移せなければ既定値で始まるだけ */ }
    }

    private static void MoveFolder(string from, string to)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        try
        {
            // ドライブをまたぐと Directory.Move できないので、写してから消す
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            Directory.Delete(from, true);
        }
        catch { /* 途中で失敗しても Twitch・YouTube に入り直せば済む */ }
    }
}
