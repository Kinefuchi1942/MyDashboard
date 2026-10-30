using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyDashboard.Monitoring;

/// <summary>設定画面の「見つかった構成」の1行。MonitorKey があればモニターの行（W 数を自分で直せる）。</summary>
public sealed record PowerRow(string Label, string Detail, double Watts, bool Measured, string? Note,
    string? MonitorKey = null, double? AutoWatts = null);

/// <summary>
/// PC 全体（＋モニター）の消費電力を推定し、今日・今月の電力量を積算する。
/// CPU・GPU はセンサーの実測、ほかは SystemScanner で調べた部品の目安を足し、電源の効率で割ってコンセント側の値にする。
/// 設定の保存で Collector が作り直されても積算が切れないように、アプリ全体で1つだけ持つ。
/// </summary>
public sealed class PowerMeter
{
    public static PowerMeter Shared { get; } = new();

    /// <summary>画面が点いているか（Windows の画面の電源。MainWindow が通知を受けて書く）。消えている間はモニターを 0.5 W で数える。</summary>
    public static volatile bool DisplayOn = true;

    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "power-log.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly object _lock = new();
    private volatile SystemConfig? _config;
    private volatile IReadOnlyList<PowerRow> _rows = Array.Empty<PowerRow>();
    private volatile Dictionary<string, double> _brightness = new();
    private Task? _scan;
    private bool _brightnessBusy;
    private DateTime _nextBrightness = DateTime.MinValue;

    private EnergyLog _log = new();
    private bool _logLoaded;
    private DateTime _lastTick = DateTime.MinValue;
    private DateTime _nextSave = DateTime.MinValue;

    public SystemConfig? Config => _config;
    /// <summary>直近の見積もりの内訳（設定画面に出す）。</summary>
    public IReadOnlyList<PowerRow> Rows => _rows;

    /// <summary>構成を調べ直す（起動時と、設定画面の「構成を読み直す」）。</summary>
    public Task Rescan()
    {
        lock (_lock)
        {
            if (_scan is { IsCompleted: false }) return _scan;
            return _scan = Task.Run(() =>
            {
                var c = SystemScanner.Scan();
                var b = ReadBrightnessSafe();
                _brightness = b;
                _config = c;
                SystemScanner.WriteDiagnostics(c, b);
            });
        }
    }

    /// <summary>1回分のセンサーの値から消費電力を出し、電力量に足す。Collector から毎回呼ぶ。</summary>
    /// <param name="fans">回っているファン・ポンプの数（マザーボードから読めなければ null）。</param>
    public PowerInfo Estimate(AppSettings s, CpuInfo? cpu, GpuInfo? gpu, IReadOnlyList<DiskInfo> disks, int? fans)
    {
        if (_scan == null) Rescan();
        RefreshBrightnessIfDue();
        var c = _config;
        var rows = new List<PowerRow>();

        // ---- CPU・GPU（実測。読めないときは使用率からの目安）
        double cpuW = cpu?.PowerW ?? 15 + (cpu?.Load ?? 10) * 1.5;
        rows.Add(new PowerRow("CPU", cpu?.Name ?? "不明", cpuW, cpu?.PowerW != null, cpu?.PowerW == null ? "使用率からの目安" : null));
        double gpuW = gpu == null ? 0 : gpu.PowerW ?? 20 + (gpu.Load ?? 0) * 2;
        if (gpu != null) rows.Add(new PowerRow("GPU", gpu.Name, gpuW, gpu.PowerW != null, gpu.PowerW == null ? "使用率からの目安" : null));

        // ---- マザーボード（チップセットの種類で分ける）。CPU への電源回路の損失（約 1 割）もここに入れる
        double boardBase = BoardWatts(c?.Board);
        double boardW = boardBase + cpuW * 0.1;
        rows.Add(new PowerRow("マザーボード", c?.Board ?? "不明", boardW, false, "CPU の電源回路の損失を含む"));

        // ---- メモリ
        int modules = c?.MemoryModules > 0 ? c.MemoryModules : 2;
        double perModule = c?.MemoryType == "DDR5" ? 4 : 3;
        double memW = modules * perModule;
        string memDetail = c?.MemoryModules > 0
            ? $"{c.MemoryType ?? "メモリ"} {Math.Round(c.MemoryTotalGb / modules)} GB × {modules} 枚"
            : "不明（2 枚と仮定）";
        rows.Add(new PowerRow("メモリ", memDetail, memW, false, null));

        // ---- ストレージ（読み書きしている間は増やす）
        var scanned = c?.Disks is { Count: > 0 } d ? d : new List<ScannedDisk> { new("不明", DiskKind.Nvme) };
        double diskW = 0;
        foreach (var disk in scanned)
        {
            var (idle, active) = disk.Kind switch
            {
                DiskKind.Hdd => (5.0, 8.0),
                DiskKind.SataSsd => (0.8, 3.0),
                _ => (1.5, 6.0),
            };
            var live = disks.FirstOrDefault(x => SameModel(x.Name, disk.Model));
            double bps = (live?.ReadBps ?? 0) + (live?.WriteBps ?? 0);
            diskW += idle + (active - idle) * Math.Min(1, bps / (100.0 * 1024 * 1024));
        }
        rows.Add(new PowerRow("ストレージ", c?.Disks is { Count: > 0 } ? DiskSummary(c.Disks) : "不明（NVMe SSD 1 台と仮定）", diskW, false, "使用中は増える"));

        // ---- ファン・ポンプ
        double fanW = (fans ?? 4) * 1.5;
        rows.Add(new PowerRow("ファン・ポンプ", fans is { } f ? $"{f} 個（回転数から）" : "読めない（4 個と仮定）", fanW, false, null));

        const double otherW = 5;
        rows.Add(new PowerRow("その他", "USB 機器など（固定値）", otherW, false, null));

        // ---- 電源の効率でコンセント側の値にする
        double dc = cpuW + gpuW + boardW + memW + diskW + fanW + otherW;
        double eff = Efficiency(s.PsuRating, dc / Math.Max(200, s.PsuWatts));
        double pcW = Math.Max(0, dc / eff + s.PowerOffsetW);
        double cpuWall = cpuW / eff, gpuWall = gpuW / eff;
        double restWall = Math.Max(0, pcW - cpuWall - gpuWall);

        // ---- モニター（画面が消えている間は 0.5 W）
        double monW = 0;
        bool on = DisplayOn;
        var br = _brightness;   // 裏で差し替わるので、この回はこれを使う
        foreach (var m in c?.Monitors ?? (IReadOnlyList<ScannedMonitor>)Array.Empty<ScannedMonitor>())
        {
            double auto = MonitorAuto(m, br, out bool brightnessKnown);
            double w = on ? (s.MonitorWatts.TryGetValue(m.Key, out var o) ? o : auto) : 0.5;
            monW += w;
            string detail = m.Name + (m.Inches is { } inch ? $"（{inch:0} 型・{m.RefreshHz}Hz）" : $"（{m.RefreshHz}Hz）");
            string note = !on ? "消灯中" : brightnessKnown ? $"明るさ {br[m.GdiName!]:0}% から" : "消灯中は 0.5 W";
            rows.Add(new PowerRow("モニター", detail, w, false, note, m.Key, auto));
        }
        if (!s.PowerIncludeMonitors) monW = 0;

        double total = pcW + monW;
        _rows = rows;
        var (today, month) = Accumulate(total);
        return new PowerInfo(total, cpuWall, gpuWall, restWall, monW, today, month, s.YenPerKwh);
    }

    // ---------------------------------------------------------------- 目安の値

    private static double BoardWatts(string? board)
    {
        if (board == null) return 15;
        var m = System.Text.RegularExpressions.Regex.Match(board, @"\b([ZXBHA])\d{3}");
        return !m.Success ? 15 : m.Groups[1].Value is "Z" or "X" ? 18 : 12;
    }

    /// <summary>モニターの見積もり：大きさ（インチ）× 1 W ＋ 高リフレッシュレートの分を「明るさ最大」とし、明るさで減らす。明るさが読めなければ 50% とみなす。</summary>
    private static double MonitorAuto(ScannedMonitor m, Dictionary<string, double> brightness, out bool brightnessKnown)
    {
        double full = (m.Inches ?? 27) + (m.RefreshHz >= 200 ? 8 : m.RefreshHz >= 120 ? 5 : 0);
        brightnessKnown = m.GdiName != null && brightness.ContainsKey(m.GdiName);
        double b = brightnessKnown ? brightness[m.GdiName!] : 50;
        return Math.Round(full * (0.4 + 0.6 * b / 100), 1);
    }

    /// <summary>電源の効率（0〜1）。80 PLUS の各等級の 10・20・50・100% 負荷の値を直線でつなぐ。10% より軽いときはさらに下げる。</summary>
    public static double Efficiency(string rating, double load)
    {
        double[] p = (rating ?? "").ToLowerInvariant() switch
        {
            "none" => new[] { 0.70, 0.75, 0.78, 0.75 },
            "standard" => new[] { 0.75, 0.80, 0.80, 0.80 },
            "bronze" => new[] { 0.77, 0.82, 0.85, 0.82 },
            "silver" => new[] { 0.80, 0.85, 0.88, 0.85 },
            "platinum" => new[] { 0.86, 0.90, 0.92, 0.89 },
            "titanium" => new[] { 0.90, 0.92, 0.94, 0.90 },
            _ => new[] { 0.83, 0.87, 0.90, 0.87 },   // gold
        };
        double[] x = { 0.1, 0.2, 0.5, 1.0 };
        load = Math.Clamp(load, 0.02, 1.0);
        if (load < 0.1) return p[0] - (0.1 - load) / 0.08 * 0.10;
        for (int i = 1; i < x.Length; i++)
            if (load <= x[i]) return p[i - 1] + (p[i] - p[i - 1]) * (load - x[i - 1]) / (x[i] - x[i - 1]);
        return p[^1];
    }

    private static bool SameModel(string lhmName, string model)
    {
        string a = Norm(lhmName), b = Norm(model);
        return a.Length > 0 && b.Length > 0 && (a.Contains(b) || b.Contains(a));
        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static string DiskSummary(IReadOnlyList<ScannedDisk> disks) => string.Join("、", disks
        .GroupBy(d => d.Kind).OrderBy(g => g.Key)
        .Select(g => $"{(g.Key switch { DiskKind.Nvme => "NVMe SSD", DiskKind.SataSsd => "SATA SSD", _ => "HDD" })} × {g.Count()}"));

    // ---------------------------------------------------------------- 明るさ（1分ごとに裏で読む）

    private void RefreshBrightnessIfDue()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_brightnessBusy || now < _nextBrightness || _config == null) return;
            _brightnessBusy = true;
            _nextBrightness = now.AddMinutes(1);
        }
        Task.Run(() =>
        {
            try { if (DisplayOn) _brightness = ReadBrightnessSafe(); }
            finally { lock (_lock) _brightnessBusy = false; }
        });
    }

    private static Dictionary<string, double> ReadBrightnessSafe()
    {
        try { return SystemScanner.ReadBrightness(); } catch { return new(); }
    }

    // ---------------------------------------------------------------- 電力量の積算（power-log.json に保存）

    private sealed class EnergyLog
    {
        public string Day { get; set; } = "";
        public double DayWh { get; set; }
        public string Month { get; set; } = "";
        public double MonthWh { get; set; }
    }

    private (double TodayKwh, double MonthKwh) Accumulate(double watts)
    {
        lock (_lock)
        {
            if (!_logLoaded)
            {
                _logLoaded = true;
                try { if (File.Exists(LogPath)) _log = JsonSerializer.Deserialize<EnergyLog>(File.ReadAllText(LogPath), JsonOptions) ?? new(); }
                catch { _log = new(); }
            }
            var now = DateTime.Now;
            string day = now.ToString("yyyy-MM-dd"), month = now.ToString("yyyy-MM");
            if (_log.Day != day) { _log.Day = day; _log.DayWh = 0; }
            if (_log.Month != month) { _log.Month = month; _log.MonthWh = 0; }

            // スリープ明けなどで間が空いたときは、その間を数えない
            if (_lastTick != DateTime.MinValue)
            {
                double sec = (now - _lastTick).TotalSeconds;
                if (sec > 0 && sec <= 5)
                {
                    double wh = watts * sec / 3600;
                    _log.DayWh += wh;
                    _log.MonthWh += wh;
                }
            }
            _lastTick = now;
            if (now >= _nextSave) { _nextSave = now.AddMinutes(1); SaveLocked(); }
            return (_log.DayWh / 1000, _log.MonthWh / 1000);
        }
    }

    /// <summary>積算した電力量をファイルに書く（終了時にも呼ぶ）。</summary>
    public void Save()
    {
        lock (_lock) if (_logLoaded) SaveLocked();
    }

    private void SaveLocked()
    {
        try { File.WriteAllText(LogPath, JsonSerializer.Serialize(_log, JsonOptions)); } catch { }
    }
}
