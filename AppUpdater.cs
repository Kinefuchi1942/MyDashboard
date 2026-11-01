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

    private readonly UpdateManager _manager = Environment.GetEnvironmentVariable(LocalSourceVariable) is { Length: > 0 } dir
        ? new(new SimpleFileSource(new DirectoryInfo(dir)))
        : new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>今のバージョン（1.2.3 の形）。</summary>
    public static string CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>新しい版を確認し、あればダウンロードまで行う。適用できる版を返す（なければ null）。</summary>
    public async Task<VelopackAsset?> CheckAndDownloadAsync()
    {
        if (!IsInstalled) return null;

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
        _manager.WaitExitThenApplyUpdates(update, silent: false, restart: true, restartArgs: Array.Empty<string>());
}
