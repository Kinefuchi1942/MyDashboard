using System.Collections.Generic;

namespace MyDashboard.Monitoring;

public sealed record CpuInfo(
    string Name,
    double? Load,
    IReadOnlyList<double> ThreadLoads,
    double? Temperature,
    double? ClockMHz,
    double? MaxClockMHz,
    double? PowerW,
    IReadOnlyList<bool>? ThreadIsECore = null);   // ThreadLoads と同じ並びで E コアのスレッドか。P/E の区別がない CPU は null

public sealed record GpuInfo(
    string Name,
    double? Load,
    double? Temperature,
    double? HotSpot,
    double? ClockMHz,
    double? MemoryClockMHz,
    double? PowerW,
    double? VramUsedBytes,
    double? VramTotalBytes,
    double? FanRpm);

public sealed record MemoryInfo(double UsedBytes, double TotalBytes);

public sealed record DiskInfo(
    string Name,
    double? ReadBps,
    double? WriteBps,
    double? UsedPercent,
    double? Temperature);

public sealed record NetworkInfo(double DownBps, double UpBps);

public sealed record FpsInfo(
    FpsState State,
    string? Application,
    double? Fps,
    double? Low1Percent,
    double? FrameTimeMs,
    string? Message,
    IReadOnlyList<double>? FrameTimes = null,   // 直近10秒の1フレームごとの時間（ms、古い順）
    DlssInfo? Dlss = null,
    string? PresentMode = null);               // PresentMon の PresentMode 列（例 "Hardware: Independent Flip"）

/// <summary>ゲームが読み込んでいる DLSS の部品（DLL）から推定した状態。オン・オフの設定そのものは分からない。</summary>
public sealed record DlssInfo(
    DlssState State,
    bool SuperResolution,     // nvngx_dlss.dll
    bool FrameGeneration,     // nvngx_dlssg.dll
    bool RayReconstruction,   // nvngx_dlssd.dll
    string? Version);

public enum DlssState
{
    None,       // DLSS の部品を読み込んでいない
    Detected,   // 1つ以上読み込んでいる
    Unknown,    // ゲーム側の保護などで中を確認できない
}

public enum FpsState
{
    Disabled,       // 設定でオフ
    NotInstalled,   // PresentMon.exe が見つからない
    Starting,
    Waiting,        // 起動済みだが描画中のアプリがない
    Running,
    Failed,
}

public sealed record Snapshot(
    CpuInfo? Cpu,
    GpuInfo? Gpu,
    MemoryInfo Memory,
    IReadOnlyList<DiskInfo> Disks,
    NetworkInfo Network,
    FpsInfo Fps,
    bool SensorDriverMissing,
    PowerInfo? Power = null);

/// <summary>消費電力の推定（すべてコンセント側の W）。OtherW は CPU・GPU 以外の PC 本体、MonitorW は合計に含めないときは 0。</summary>
public sealed record PowerInfo(
    double TotalW,
    double CpuW,
    double GpuW,
    double OtherW,
    double MonitorW,
    double TodayKwh,
    double MonthKwh,
    double YenPerKwh);
