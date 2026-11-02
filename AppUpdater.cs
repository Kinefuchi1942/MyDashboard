using System;
using System.IO;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace MyDashboard;

/// <summary>
/// GitHub Releases に新しい版があればダウンロードしておき、右クリックメニューの操作で適用する（Velopack）。
/// インストーラー（Setup.exe）で入れたときだけ使える。publish フォルダの開発版などでは IsInstalled が false。
/// </summary>
public sealed class AppUpdater
{
    private const string RepositoryUrl = "https://github.com/Kinefuchi1942/MyDashboard";

    /// <summary>手元で更新を試すときに、vpk pack の出力フォルダを更新元にする環境変数。</summary>
    private const string LocalSourceVariable = "MYDASHBOARD_UPDATE_SOURCE";

    private readonly UpdateManager? _manager = Create();

    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>
    /// 更新の仕組みを用意する。Program.Main（VelopackApp.Run）を通らずに起動したときなどは作れないので、
    /// そのときはアプリを落とさず「インストール版ではない」扱いにする。
    /// </summary>
    private static UpdateManager? Create()
    {
        try
        {
            var manager = Environment.GetEnvironmentVariable(LocalSourceVariable) is { Length: > 0 } dir
                ? new UpdateManager(new SimpleFileSource(new DirectoryInfo(dir)))
                : new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            _ = manager.IsInstalled;
            return manager;
        }
        catch (Exception ex)
        {
            CrashLog.Write("更新の仕組みを用意できませんでした", ex);
            return null;
        }
    }

    /// <summary>今のバージョン（1.2.3 の形）。</summary>
    public static string CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>新しい版を確認し、あればダウンロードまで行う。適用できる版を返す（なければ null）。</summary>
    public async Task<VelopackAsset?> CheckAndDownloadAsync()
    {
        if (_manager is null || !_manager.IsInstalled) return null;

        var info = await _manager.CheckForUpdatesAsync();
        if (info is null) return null;

        // ダウンロード済みなら Velopack が再利用する
        await _manager.DownloadUpdatesAsync(info);
        return info.TargetFullRelease;
    }

    /// <summary>
    /// このプロセスの終了を待って更新を適用し、新しい版を起動する更新プログラムを走らせる。
    /// 呼んだあとはすぐにアプリを終了すること。
    /// </summary>
    public void ApplyAfterExit(VelopackAsset update) =>
        (_manager ?? throw new InvalidOperationException("インストール版ではありません。")).WaitExitThenApplyUpdates(update, silent: false, restart: true, restartArgs: Array.Empty<string>());
}
