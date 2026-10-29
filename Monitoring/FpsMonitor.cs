using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MyDashboard.Monitoring;

/// <summary>
/// Intel PresentMon（コンソール版）を子プロセスとして起動し、CSV出力からFPSを計算する。
/// PresentMon は exe と同じフォルダ（または tools フォルダ）に置かれていれば自動で使う。
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private const string SessionName = "MyDashboard";
    private readonly HashSet<string> _excluded;
    private readonly object _lock = new();
    private readonly LinkedList<Frame> _frames = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Process? _process;
    private Thread? _reader;
    private string? _presentMonPath;
    private volatile FpsState _state = FpsState.Starting;
    private string? _error;
    private int _restartCount;
    private DateTime _nextRestart = DateTime.MinValue;

    // ---- 診断用カウンタ（fps-diagnostics.txt に書き出す）
    private long _stdoutLines, _framesParsed, _framesExcluded, _framesBadValue;
    private string? _headerLine, _sampleLine, _arguments;
    private readonly Queue<string> _stderr = new();
    private readonly Dictionary<string, long> _appCounts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextDiag = DateTime.MinValue;
    private DateTime _startedAt;
    private readonly DlssDetector _dlss = new();
    private string? _lastMode;

    private readonly record struct Frame(double Time, int Pid, string App, double Ms, string? Mode);

    public FpsMonitor(IEnumerable<string> excludedProcesses, bool enabled)
    {
        _excluded = new HashSet<string>(excludedProcesses, StringComparer.OrdinalIgnoreCase);
        // 自分自身は settings.json に書いていなくても常に除外する（exe 名を変えても FPS を拾わないように）
        _excluded.Add(Path.GetFileName(Environment.ProcessPath ?? "MyDashboard.exe"));
        // オーロラの Twitch 表示（WebView2）の描画もゲームと間違えないよう、常に除外する
        _excluded.Add("msedgewebview2.exe");
        if (!enabled) { _state = FpsState.Disabled; return; }

        _presentMonPath = FindPresentMon();
        if (_presentMonPath == null) { _state = FpsState.NotInstalled; return; }
        Start();
    }

    public static string? FindPresentMon()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "tools") })
        {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir, "PresentMon*.exe")
                               .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                               .FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    private void Start()
    {
        try
        {
            var excludes = string.Join(" ", _excluded.Select(e => $"--exclude \"{e}\""));
            _arguments = $"--output_stdout --no_console_stats --stop_existing_session " +
                         $"--session_name {SessionName} --v1_metrics {excludes}";
            _startedAt = DateTime.Now;
            var psi = new ProcessStartInfo(_presentMonPath!)
            {
                Arguments = _arguments,
                WorkingDirectory = Path.GetDirectoryName(_presentMonPath!)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _process = Process.Start(psi);
            if (_process == null) throw new InvalidOperationException("PresentMon を起動できませんでした");
            ChildProcessJob.Assign(_process);
            _process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                _error = e.Data;
                lock (_stderr)
                {
                    _stderr.Enqueue($"{DateTime.Now:HH:mm:ss} {e.Data}");
                    while (_stderr.Count > 20) _stderr.Dequeue();
                }
            };
            _process.BeginErrorReadLine();

            _state = FpsState.Waiting;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PresentMon reader" };
            _reader.Start(_process);
        }
        catch (Exception ex)
        {
            _state = FpsState.Failed;
            _error = ex.Message;
        }
    }

    private void ReadLoop(object? arg)
    {
        var proc = (Process)arg!;
        int colApp = -1, colPid = -1, colMs = -1, colMode = -1;
        try
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                Interlocked.Increment(ref _stdoutLines);
                var cols = line.Split(',');
                if (colMs < 0)
                {
                    _headerLine ??= line;
                    // ヘッダー行。バージョン差を吸収するため列名で探す
                    // PresentMon のバージョンにより msBetweenPresents / MsBetweenPresents と表記が揺れるため、大文字小文字は区別しない
                    colApp = Col(cols, "Application");
                    colPid = Col(cols, "ProcessID");
                    colMs = Col(cols, "MsBetweenPresents");
                    if (colMs < 0) colMs = Col(cols, "MsBetweenAppStart");
                    if (colMs < 0) colMs = Col(cols, "FrameTime");
                    colMode = Col(cols, "PresentMode");
                    if (colMs >= 0) _headerLine = line;
                    continue;
                }
                _sampleLine ??= line;
                if (colApp < 0 || colPid < 0) continue; // 想定外の出力形式
                if (cols.Length <= Math.Max(colMs, Math.Max(colApp, colPid))) continue;
                if (!double.TryParse(cols[colMs], System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var ms)
                    || ms <= 0 || ms > 2000)
                {
                    Interlocked.Increment(ref _framesBadValue);
                    continue;
                }
                int.TryParse(cols[colPid], out var pid);
                var app = cols[colApp].Trim();
                var mode = colMode >= 0 && colMode < cols.Length ? cols[colMode].Trim() : null;
                lock (_appCounts) _appCounts[app] = _appCounts.TryGetValue(app, out var n) ? n + 1 : 1;
                if (_excluded.Contains(app)) { Interlocked.Increment(ref _framesExcluded); continue; }
                Interlocked.Increment(ref _framesParsed);

                lock (_lock)
                {
                    _frames.AddLast(new Frame(_clock.Elapsed.TotalSeconds, pid, app, ms, mode));
                    // 直近15秒分だけ保持
                    double cutoff = _clock.Elapsed.TotalSeconds - 15;
                    while (_frames.First != null && _frames.First.Value.Time < cutoff) _frames.RemoveFirst();
                }
            }
        }
        catch (Exception ex)
        {
            lock (_stderr) _stderr.Enqueue($"{DateTime.Now:HH:mm:ss} [reader] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Col(string[] cols, string name) =>
        Array.FindIndex(cols, c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>FPSが出ないときの原因調査用。5秒ごとに exe と同じフォルダへ書き出す。</summary>
    private void WriteDiagnostics()
    {
        if (DateTime.Now < _nextDiag) return;
        _nextDiag = DateTime.Now.AddSeconds(5);
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"更新: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"状態: {_state}");
            sb.AppendLine($"PresentMon: {_presentMonPath}");
            if (_presentMonPath != null)
                sb.AppendLine($"バージョン: {FileVersionInfo.GetVersionInfo(_presentMonPath).FileVersion}");
            sb.AppendLine($"引数: {_arguments}");
            sb.AppendLine($"起動時刻: {_startedAt:HH:mm:ss}  再起動回数: {_restartCount}");
            try { sb.AppendLine($"プロセス: PID {_process?.Id}  終了済み: {_process?.HasExited}" + (_process is { HasExited: true } ? $"  終了コード: {_process.ExitCode}" : "")); }
            catch (Exception ex) { sb.AppendLine($"プロセス: 取得失敗 {ex.Message}"); }
            sb.AppendLine($"前面ウィンドウのPID: {ForegroundPid()}");
            sb.AppendLine($"受信行数: {Interlocked.Read(ref _stdoutLines)}");
            sb.AppendLine($"採用フレーム: {Interlocked.Read(ref _framesParsed)}  除外: {Interlocked.Read(ref _framesExcluded)}  値が不正: {Interlocked.Read(ref _framesBadValue)}");
            sb.AppendLine($"ヘッダー: {_headerLine}");
            sb.AppendLine($"データ例: {_sampleLine}");
            sb.AppendLine($"Present Mode: {_lastMode ?? "（なし）"}");
            sb.AppendLine("アプリ別フレーム数:");
            lock (_appCounts)
                foreach (var kv in _appCounts.OrderByDescending(k => k.Value).Take(15))
                    sb.AppendLine($"  {kv.Key}: {kv.Value}");
            sb.AppendLine("エラー出力:");
            lock (_stderr) foreach (var l in _stderr) sb.AppendLine("  " + l);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fps-diagnostics.txt"), sb.ToString());
        }
        catch { }
    }

    public FpsInfo Read()
    {
        switch (_state)
        {
            case FpsState.Disabled:
                return new FpsInfo(FpsState.Disabled, null, null, null, null, "FPS計測はオフです");
            case FpsState.NotInstalled:
                return new FpsInfo(FpsState.NotInstalled, null, null, null, null,
                    "PresentMon.exe を exe と同じフォルダに置くと FPS を表示します");
        }

        WriteDiagnostics();

        // PresentMon が落ちていたら、間隔を空けて再起動（最大5回）
        if (_process is { HasExited: true } && _restartCount < 5 && DateTime.Now >= _nextRestart)
        {
            _restartCount++;
            _nextRestart = DateTime.Now.AddSeconds(5);
            Start();
        }
        if (_state == FpsState.Failed || _process is { HasExited: true })
            return new FpsInfo(FpsState.Failed, null, null, null, null, _error ?? "PresentMon が停止しました");

        double now = _clock.Elapsed.TotalSeconds;
        Frame[] recent;
        lock (_lock) recent = _frames.ToArray();

        var lastSecond = recent.Where(f => f.Time >= now - 1.0).ToArray();
        if (lastSecond.Length == 0)
            return new FpsInfo(FpsState.Waiting, null, null, null, null,
                $"PresentMon から {Interlocked.Read(ref _stdoutLines):#,0} 行受信、採用フレーム {Interlocked.Read(ref _framesParsed):#,0}。" +
                "FPSが出ない場合は fps-diagnostics.txt を確認してください");

        // 対象アプリ：前面ウィンドウのプロセスが描画していればそれ、なければ最もフレーム数が多いもの
        int fg = ForegroundPid();
        var groups = lastSecond.GroupBy(f => f.Pid).ToList();
        var target = groups.FirstOrDefault(g => g.Key == fg)
                  ?? groups.OrderByDescending(g => g.Count()).First();

        double avgMs = target.Average(f => f.Ms);
        var longWindow = recent.Where(f => f.Pid == target.Key && f.Time >= now - 10).Select(f => f.Ms).OrderBy(x => x).ToArray();
        double? low1 = null;
        if (longWindow.Length >= 30)
        {
            // 1% low = フレーム時間の99パーセンタイルをFPSに換算
            int idx = (int)Math.Ceiling(longWindow.Length * 0.99) - 1;
            low1 = 1000.0 / longWindow[Math.Clamp(idx, 0, longWindow.Length - 1)];
        }

        // フレームタイムのグラフ用（直近10秒、古い順）
        var frameTimes = recent.Where(f => f.Pid == target.Key && f.Time >= now - 10).Select(f => f.Ms).ToArray();

        // 表示方式（全画面・ウィンドウの切り替えで変わるので、いちばん新しいフレームのものを使う）
        var mode = target.Last().Mode;
        _lastMode = mode;

        _state = FpsState.Running;
        var app = target.First().App;
        if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app[..^4];
        return new FpsInfo(FpsState.Running, app, 1000.0 / avgMs, low1, avgMs, null, frameTimes, _dlss.Check(target.Key),
                           string.IsNullOrEmpty(mode) ? null : mode);
    }

    private static int ForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(true);
        }
        catch { }
        // ETW セッションが残らないよう明示的に止める
        try
        {
            if (_presentMonPath != null && _state != FpsState.Disabled)
            {
                using var stop = Process.Start(new ProcessStartInfo(_presentMonPath,
                    $"--terminate_existing_session --session_name {SessionName}")
                { UseShellExecute = false, CreateNoWindow = true });
                stop?.WaitForExit(2000);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

/// <summary>ダッシュボードが異常終了しても子プロセス（PresentMon）が残らないようにするジョブオブジェクト。</summary>
internal static class ChildProcessJob
{
    private static readonly IntPtr Job = Create();

    public static void Assign(Process p)
    {
        if (Job != IntPtr.Zero) AssignProcessToJobObject(Job, p.Handle);
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)len);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
