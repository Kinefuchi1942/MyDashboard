using System;
using System.IO;

namespace MyDashboard;

/// <summary>
/// 予期しないエラーの詳しい内容（どこで起きたか）を %APPDATA%\MyDashboard\crash-log.txt に書き足す。
/// 画面のメッセージには一行しか出ないので、原因を調べるときはこのファイルをもらう。
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;

    public static void Write(string what, Exception ex)
    {
        try
        {
            string path = AppPaths.Data("crash-log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
            File.AppendAllText(path,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}（バージョン {AppUpdater.CurrentVersion}、{Environment.ProcessPath}）{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* 記録に失敗しても本体は止めない */ }
    }
}
