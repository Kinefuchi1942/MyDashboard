using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace MyDashboard;

/// <summary>
/// ログオン時の自動起動（タスクスケジューラのタスク「MyDashboard」）。
/// アプリが管理者権限で動いているので、.bat を使わずにここから登録・解除できる。
/// 通常の権限で起動されたときは、このタスクを動かして管理者権限で起動し直す（Program.cs）。
/// 内容は以前の install-autostart.bat と同じ：最上位の特権・ログオン20秒後・72時間制限なし・電池でも止めない。
/// </summary>
public static class AutoStart
{
    private const string TaskName = "MyDashboard";

    public static bool IsRegistered() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>いま動いている exe を自動起動に登録する（既にあれば上書き）。成功したら true。</summary>
    public static bool Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT20S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        // schtasks の /XML は UTF-16 のファイルしか読めない
        var file = Path.Combine(Path.GetTempPath(), "MyDashboard-autostart.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    public static bool Unregister() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

    /// <summary>
    /// タスクがいま動いている exe を指していれば実行する（通常の権限からでも、最上位の特権で UAC なしに起動できる）。
    /// 起動を頼めたら true。
    /// </summary>
    public static bool RunIfForThisExe() =>
        PointsToThisExe() && RunSchtasks($"/Run /TN \"{TaskName}\"") == 0;

    /// <summary>アンインストールの直前に呼ばれる。この exe を指すタスクだけ消す（開発用の publish 版のタスクは残す）。</summary>
    public static void UnregisterOnUninstall()
    {
        if (!PointsToThisExe()) return;
        if (Unregister()) return;
        // 管理者権限で作ったタスクは通常の権限では消せないことがあるので、UAC を出して消す
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(20000);
        }
        catch { }
    }

    private static bool PointsToThisExe()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        if (RunSchtasks($"/Query /TN \"{TaskName}\" /XML", out var xml) != 0) return false;
        var m = Regex.Match(xml, @"<Command>\s*""?([^""<]+?)""?\s*</Command>");
        return m.Success && string.Equals(Path.GetFullPath(m.Groups[1].Value), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
    }

    private static int RunSchtasks(string arguments) => RunSchtasks(arguments, out _);

    private static int RunSchtasks(string arguments, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // schtasks の出力はコンソールのコードページ（日本語 Windows なら CP932）。ユーザー名が日本語のパスを読むため
                StandardOutputEncoding = OemEncoding(),
            });
            if (p == null) return -1;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.Default; }
    }
}
