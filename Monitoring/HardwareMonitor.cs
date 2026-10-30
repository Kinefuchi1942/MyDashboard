using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace MyDashboard.Monitoring;

/// <summary>
/// LibreHardwareMonitor で CPU / GPU / ストレージのセンサーを読む。
/// メモリ量は Windows API (GlobalMemoryStatusEx) から直接取得する。
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,   // RAMはWindows APIで取る
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,   // ファンの数（消費電力の見積もり）
            IsNetworkEnabled = false,  // ネットワークは NetworkMonitor で取る
            IsControllerEnabled = false,
        };
        _computer.Open();
    }

    /// <summary>CPU温度に必要なカーネルドライバ PawnIO が入っているか。</summary>
    public static bool IsPawnIoInstalled
    {
        get
        {
            const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
            try
            {
                using var k64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(key);
                if (k64 != null) return true;
                using var k32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(key);
                return k32 != null;
            }
            catch { return false; }
        }
    }

    public void Update() => _computer.Accept(_visitor);

    // ---------------------------------------------------------------- CPU

    public CpuInfo? ReadCpu()
    {
        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return null;
        var sensors = AllSensors(cpu).ToList();

        double? total = Value(sensors, SensorType.Load, "CPU Total");

        // スレッド別負荷：「CPU Core #1 Thread #1」や「CPU Core #3」など。Index 順に並べる
        var threadSensors = sensors
            .Where(s => s.SensorType == SensorType.Load
                        && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal)
                        && s.Value.HasValue)
            .OrderBy(s => s.Index)
            .ToList();
        var threads = threadSensors.Select(s => (double)s.Value!.Value).ToList();
        total ??= threads.Count > 0 ? threads.Average() : null;
        var isECore = ClassifyThreads(threadSensors);

        // 温度：Intel は CPU Package、AMD は Tctl/Tdie を優先。無ければ最大値
        double? temp = Value(sensors, SensorType.Temperature, "CPU Package")
                    ?? Value(sensors, SensorType.Temperature, "Core (Tctl/Tdie)")
                    ?? Value(sensors, SensorType.Temperature, "Core (Tdie)")
                    ?? Value(sensors, SensorType.Temperature, "Core (Tctl)")
                    ?? Value(sensors, SensorType.Temperature, "Core Max")
                    ?? MaxOf(sensors.Where(s => s.SensorType == SensorType.Temperature
                                                && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)));

        // クロック：コア別クロックの平均と最大（バス速度や Effective 値は除外）
        var clocks = sensors
            .Where(s => s.SensorType == SensorType.Clock && s.Value is > 0
                        && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)
                        && !s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
            .Select(s => (double)s.Value!.Value)
            .ToList();
        double? clockAvg = Value(sensors, SensorType.Clock, "Cores (Average)")
                        ?? (clocks.Count > 0 ? clocks.Average() : null);
        double? clockMax = clocks.Count > 0 ? clocks.Max() : clockAvg;

        double? power = Value(sensors, SensorType.Power, "CPU Package")
                     ?? Value(sensors, SensorType.Power, "Package");

        return new CpuInfo(Clean(cpu.Name), total, threads, temp, clockAvg, clockMax, power, isECore);
    }

    /// <summary>
    /// スレッドごとに E コアかを判定する。名前の「CPU Core #N」の N を Windows のコア順に当てる。
    /// Windows から取れないときは「1 スレッドだけのコア（HT なし）が混ざっていればそれが E コア」で推定する。
    /// </summary>
    private static List<bool>? ClassifyThreads(List<ISensor> threadSensors)
    {
        var coreNo = threadSensors.Select(s => CoreNumber(s.Name)).ToList();
        if (coreNo.Count == 0 || coreNo.Any(n => n <= 0)) return null;

        if (CpuTopology.IsECore is { } topo && coreNo.Max() <= topo.Length)
            return coreNo.Select(n => topo[n - 1]).ToList();

        var perCore = coreNo.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        if (perCore.Values.Distinct().Count() != 2 || !perCore.Values.Contains(1)) return null;
        return coreNo.Select(n => perCore[n] == 1).ToList();
    }

    private static int CoreNumber(string name)
    {
        // 「CPU Core #12 Thread #1」→ 12
        int i = "CPU Core #".Length, j = i;
        while (j < name.Length && char.IsDigit(name[j])) j++;
        return j > i && int.TryParse(name.AsSpan(i, j - i), out var n) ? n : 0;
    }

    // ---------------------------------------------------------------- GPU

    public GpuInfo? ReadGpu()
    {
        var gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
               ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel);
        if (gpu == null) return null;
        var sensors = AllSensors(gpu).ToList();

        // VRAM はライブラリのバージョンにより種類（Data / SmallData）と単位（GB / MB / バイト）が異なるため、
        // 種類を問わず名前で探し、NormalizeBytes で単位をそろえる
        double? used = ByName(sensors, "GPU Memory Used") ?? ByName(sensors, "D3D Dedicated Memory Used");
        double? total = ByName(sensors, "GPU Memory Total");
        if (total == null && used != null && ByName(sensors, "GPU Memory Free") is { } free)
            total = used + free;

        double? fan = sensors.Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue)
                             .Select(s => (double?)s.Value!.Value).FirstOrDefault();

        return new GpuInfo(
            Clean(gpu.Name),
            Value(sensors, SensorType.Load, "GPU Core"),
            Value(sensors, SensorType.Temperature, "GPU Core"),
            Value(sensors, SensorType.Temperature, "GPU Hot Spot"),
            Value(sensors, SensorType.Clock, "GPU Core"),
            Value(sensors, SensorType.Clock, "GPU Memory"),
            Value(sensors, SensorType.Power, "GPU Package") ?? Value(sensors, SensorType.Power, "GPU Power"),
            NormalizeBytes(used),
            NormalizeBytes(total),
            fan);
    }

    // ---------------------------------------------------------------- ファン（マザーボード）

    /// <summary>回っているファン・ポンプの数（100 rpm 超）。マザーボードにファンのセンサーがなければ null。</summary>
    public int? CountFans()
    {
        bool any = false;
        int spinning = 0;
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
            foreach (var s in AllSensors(hw).Where(s => s.SensorType == SensorType.Fan))
            {
                any = true;
                if (s.Value is > 100) spinning++;
            }
        return any ? spinning : null;
    }

    // ---------------------------------------------------------------- ストレージ

    public IReadOnlyList<DiskInfo> ReadDisks()
    {
        var list = new List<DiskInfo>();
        foreach (var disk in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            var sensors = AllSensors(disk).ToList();
            list.Add(new DiskInfo(
                Clean(disk.Name),
                Value(sensors, SensorType.Throughput, "Read Rate"),
                Value(sensors, SensorType.Throughput, "Write Rate"),
                Value(sensors, SensorType.Load, "Used Space"),
                sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                       .Select(s => (double?)s.Value!.Value).FirstOrDefault()));
        }
        return list;
    }

    // ---------------------------------------------------------------- メモリ

    public static MemoryInfo ReadMemory()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return new MemoryInfo(0, 0);
        return new MemoryInfo(st.ullTotalPhys - st.ullAvailPhys, st.ullTotalPhys);
    }

    // ---------------------------------------------------------------- 補助

    private static IEnumerable<ISensor> AllSensors(IHardware hw)
    {
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
            foreach (var s in AllSensors(sub)) yield return s;
    }

    private static double? Value(IEnumerable<ISensor> sensors, SensorType type, string name)
    {
        var s = sensors.FirstOrDefault(x => x.SensorType == type && x.Name == name && x.Value.HasValue);
        return s?.Value;
    }

    /// <summary>温度・負荷など数値の種類以外（容量系）のセンサーを名前だけで探す。</summary>
    private static double? ByName(IEnumerable<ISensor> sensors, string name)
    {
        var s = sensors.FirstOrDefault(x => x.Name == name && x.Value.HasValue
                                            && x.SensorType is not (SensorType.Load or SensorType.Clock or SensorType.Temperature));
        return s?.Value;
    }

    private static double? MaxOf(IEnumerable<ISensor> sensors)
    {
        var values = sensors.Where(s => s.Value.HasValue).Select(s => (double)s.Value!.Value).ToList();
        return values.Count > 0 ? values.Max() : null;
    }

    /// <summary>LHM のバージョンにより Data が GB / MB / バイトのことがあるため正規化する。</summary>
    private static double? NormalizeBytes(double? v)
    {
        if (v is null) return null;
        if (v < 512) return v * 1024d * 1024 * 1024;        // GB とみなす
        if (v < 4_000_000) return v * 1024d * 1024;          // MB とみなす
        return v;                                             // バイト
    }

    private static string Clean(string name) =>
        name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
