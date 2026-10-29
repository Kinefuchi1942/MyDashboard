using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MyDashboard.Monitoring;

/// <summary>
/// ゲームのプロセスが読み込んでいる DLL から DLSS の使用状況を推定する。
/// PresentMon は NVIDIA のフレーム生成を見分けられないため、この方法をとる。
/// DLSS をオフにしても DLL を読み込んだままのゲームがあるので、あくまで目安。
/// 判定の手がかりは exe と同じフォルダの dlss-diagnostics.txt に書き出す。
/// </summary>
public sealed class DlssDetector
{
    private int _pid = -1;
    private DateTime _next = DateTime.MinValue;
    private DlssInfo _last = new(DlssState.None, false, false, false, null);
    private string? _lastDiag;

    public DlssInfo Check(int pid)
    {
        // 同じゲームなら5秒に1回だけ調べる（途中で DLSS を切り替えると DLL が後から読み込まれるため）
        if (pid == _pid && DateTime.Now < _next) return _last;
        _pid = pid;
        _next = DateTime.Now.AddSeconds(5);
        _last = Scan(pid);
        return _last;
    }

    private DlssInfo Scan(int pid)
    {
        bool sr = false, fg = false, rr = false;
        string? version = null;
        var hits = new List<string>();
        int count = 0;
        string procName = "?";
        try
        {
            using var p = Process.GetProcessById(pid);
            procName = p.ProcessName;
            foreach (ProcessModule m in p.Modules)
            {
                count++;
                var name = (m.ModuleName ?? "").ToLowerInvariant();
                var path = (m.FileName ?? "").ToLowerInvariant();
                if (name.Contains("dlss") || name.Contains("ngx") || name.StartsWith("sl.") || path.Contains(@"\ngx\"))
                    hits.Add($"{m.ModuleName}  {FormatVersion(m)}  {m.FileName}");

                // 本体の DLL（ゲーム同梱）と、NVIDIA App が差し替える最新モデル（ProgramData\NVIDIA\NGX\models\…）の両方を見る
                if (name is "nvngx_dlssg.dll" || path.Contains(@"\models\dlssg\")) fg = true;
                else if (name is "nvngx_dlssd.dll" || path.Contains(@"\models\dlssd\"))
                {
                    rr = true;
                    version ??= FormatVersion(m);
                }
                else if (name is "nvngx_dlss.dll" || path.Contains(@"\models\dlss\"))
                {
                    sr = true;
                    version = FormatVersion(m) ?? version;
                }
                m.Dispose();
            }
        }
        catch (Exception ex)
        {
            // アンチチートで保護されたゲームなどは中を見られない
            WriteDiagnostics($"PID {pid} ({procName}): 調べられませんでした {ex.GetType().Name}: {ex.Message}");
            return new DlssInfo(DlssState.Unknown, false, false, false, null);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"PID {pid} ({procName})  モジュール数 {count}");
        sb.AppendLine($"判定: SR={sr} FG={fg} RR={rr} 版={version}");
        sb.AppendLine("DLSS・NGX・Streamline 関連:");
        foreach (var h in hits) sb.AppendLine("  " + h);
        WriteDiagnostics(sb.ToString());

        return new DlssInfo(sr || fg || rr ? DlssState.Detected : DlssState.None, sr, fg, rr, version);
    }

    private void WriteDiagnostics(string text)
    {
        if (text == _lastDiag) return;
        _lastDiag = text;
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dlss-diagnostics.txt"),
                $"更新: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{text}");
        }
        catch { }
    }

    /// <summary>"310.2.1.0" → "310.2.1"</summary>
    private static string? FormatVersion(ProcessModule m)
    {
        try
        {
            var v = m.FileVersionInfo;
            if (v.FileMajorPart == 0 && v.FileMinorPart == 0) return null;
            return v.FileBuildPart > 0
                ? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}"
                : $"{v.FileMajorPart}.{v.FileMinorPart}";
        }
        catch { return null; }
    }
}
