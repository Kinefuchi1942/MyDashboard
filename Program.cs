using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using Velopack;

namespace MyDashboard;

/// <summary>
/// エントリポイント。Velopack のインストール・更新・アンインストール時の処理は、WPF を起動する前に済ませる。
/// exe 自体は通常の権限で起動する（app.manifest は asInvoker）。インストーラーや更新プログラムから
/// 起動できるようにするためで、管理者権限でなければここで自分を管理者として起動し直す。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // アンインストール後に消えた exe を自動起動しようとしないよう、タスクを消す
            .OnBeforeUninstallFastCallback(_ => AutoStart.UnregisterOnUninstall())
            .Run();

        if (!IsAdministrator())
        {
            RelaunchAsAdministrator(args);
            return;
        }

        var app = new App { IsDashboard = true };
        app.InitializeComponent();
        app.Run();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 自動起動のタスクがこの exe を指していれば、それを動かす（最上位の特権で動くので UAC が出ない）。
    /// なければ UAC を出して管理者として起動し直す。
    /// </summary>
    private static void RelaunchAsAdministrator(string[] args)
    {
        if (AutoStart.RunIfForThisExe()) return;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            });
        }
        catch (Win32Exception) { /* UAC で「いいえ」を選んだときは何もしない */ }
    }
}
