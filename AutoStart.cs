using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace MyDashboard;

/// <summary>
/// ログオン時の自動起動（タスクスケジューラのタスク「MyDashboard」）。
/// アプリが管理者権限で動いているので、.bat を使わずにここから登録・解除できる。
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

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
