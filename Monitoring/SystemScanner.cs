using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MyDashboard.Monitoring;

public enum DiskKind { Nvme, SataSsd, Hdd }

public sealed record ScannedDisk(string Model, DiskKind Kind);

/// <summary>つないでいるモニター1台。Key は設定で W 数を直すときの目印（機種コード＋接続先）。</summary>
public sealed record ScannedMonitor(string Key, string Name, double? Inches, int RefreshHz, string? GdiName);

/// <summary>消費電力の見積もりに使う PC の構成。起動時と「構成を読み直す」で調べる。</summary>
public sealed record SystemConfig(
    string? Board,
    string? MemoryType,             // "DDR5" など。分からなければ null
    int MemoryModules,
    double MemoryTotalGb,
    IReadOnlyList<ScannedDisk> Disks,
    IReadOnlyList<ScannedMonitor> Monitors,
    DateTime ScannedAt);

/// <summary>
/// PC の構成を調べる。マザーボードはレジストリ、メモリは SMBIOS、ドライブは各ドライブへの問い合わせ、
/// モニターは Windows の画面構成と EDID から読む。ファンは HardwareMonitor が毎回数える
/// （LibreHardwareMonitor の Computer を別に開くと、閉じたときにドライバーが共有ごと閉じられて本体のセンサーが読めなくなるため）。
/// どれも失敗したら「分からない」として先へ進む（管理者権限で動いている前提）。
/// </summary>
public static class SystemScanner
{
    public static SystemConfig Scan()
    {
        var (memType, modules, totalGb) = Try(ReadMemory, (null, 0, 0));
        return new SystemConfig(
            Try(ReadBoard, null),
            memType, modules, totalGb,
            Try(ReadDisks, new List<ScannedDisk>()),
            Try(ReadMonitors, new List<ScannedMonitor>()),
            DateTime.Now);
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    // ---------------------------------------------------------------- マザーボード

    private static string? ReadBoard()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var maker = (k?.GetValue("BaseBoardManufacturer") as string)?.Trim();
        var product = (k?.GetValue("BaseBoardProduct") as string)?.Trim();
        if (string.IsNullOrEmpty(product)) return null;
        maker = maker?.Replace("ASUSTeK COMPUTER INC.", "ASUS").Replace("Micro-Star International Co., Ltd.", "MSI")
                     .Replace("Gigabyte Technology Co., Ltd.", "GIGABYTE").Replace("ASRock", "ASRock");
        return string.IsNullOrEmpty(maker) ? product : $"{maker} {product}";
    }

    // ---------------------------------------------------------------- メモリ（SMBIOS の Type 17）

    private static (string?, int, double) ReadMemory()
    {
        const uint RSMB = 0x52534D42;
        uint size = GetSystemFirmwareTable(RSMB, 0, IntPtr.Zero, 0);
        if (size == 0) return (null, 0, 0);
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetSystemFirmwareTable(RSMB, 0, buf, size) == 0) return (null, 0, 0);
            var raw = new byte[size];
            Marshal.Copy(buf, raw, 0, (int)size);

            // 先頭 8 バイトは見出し（バージョンと長さ）、そのあとに構造体が並ぶ
            int len = BitConverter.ToInt32(raw, 4), i = 8, end = Math.Min(raw.Length, 8 + len);
            int modules = 0;
            double totalMb = 0;
            var types = new Dictionary<string, int>();
            while (i + 4 <= end)
            {
                byte type = raw[i], hlen = raw[i + 1];
                if (hlen < 4 || type == 127) break;
                if (type == 17 && hlen >= 0x13 && i + hlen <= end)
                {
                    int sz = BitConverter.ToUInt16(raw, i + 0x0C);
                    double mb = sz switch
                    {
                        0 or 0xFFFF => 0,
                        0x7FFF when hlen >= 0x20 => BitConverter.ToUInt32(raw, i + 0x1C) & 0x7FFFFFFF,
                        _ => (sz & 0x8000) != 0 ? (sz & 0x7FFF) / 1024.0 : sz,
                    };
                    if (mb > 0)
                    {
                        modules++;
                        totalMb += mb;
                        string? t = raw[i + 0x12] switch
                        {
                            0x18 => "DDR3", 0x1A => "DDR4", 0x1B or 0x1C => "LPDDR", 0x1D => "LPDDR3",
                            0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => null,
                        };
                        if (t != null) types[t] = types.GetValueOrDefault(t) + 1;
                    }
                }
                // 構造体のあとに文字列が続き、0 が2つ並んだところで終わる
                int j = i + hlen;
                while (j + 1 < end && !(raw[j] == 0 && raw[j + 1] == 0)) j++;
                i = j + 2;
            }
            var memType = types.Count > 0 ? types.OrderByDescending(kv => kv.Value).First().Key : null;
            return (memType, modules, Math.Round(totalMb / 1024));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------------------------------------------------------------- ドライブ（\\.\PhysicalDriveN に問い合わせ）

    private static List<ScannedDisk> ReadDisks()
    {
        var list = new List<ScannedDisk>();
        int misses = 0;
        for (int n = 0; n < 32 && misses < 4; n++)
        {
            using var h = CreateFile($@"\\.\PhysicalDrive{n}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { misses++; continue; }
            misses = 0;

            var desc = QueryProperty(h, 0);   // StorageDeviceProperty
            if (desc == null || desc.Length < 28) continue;
            // STORAGE_DEVICE_DESCRIPTOR：VendorIdOffset は 8、ProductIdOffset は 12、BusType は 24 バイト目
            uint busType = BitConverter.ToUInt32(desc, 24);
            if (busType is 7 or 12 or 13 or 14 or 15 or 16) continue;   // USB・SD・MMC・仮想ディスクは数えない（電源が別、または実体がない）
            string model = ReadAnsi(desc, BitConverter.ToInt32(desc, 12));
            string vendor = ReadAnsi(desc, BitConverter.ToInt32(desc, 8));
            // 「NVMe」「ATA」などは接続の種類でメーカー名ではないので付けない
            if (vendor.Length > 0 && vendor.ToUpperInvariant() is not ("NVME" or "ATA" or "SCSI" or "SATA")
                && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) model = vendor + " " + model;

            DiskKind kind;
            if (busType == 17) kind = DiskKind.Nvme;
            else
            {
                var seek = QueryProperty(h, 7);   // StorageDeviceSeekPenaltyProperty
                kind = seek is { Length: >= 9 } && seek[8] != 0 ? DiskKind.Hdd : DiskKind.SataSsd;
            }
            list.Add(new ScannedDisk(model.Trim(), kind));
        }
        return list;
    }

    private static byte[]? QueryProperty(SafeFileHandle h, int propertyId)
    {
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);   // PropertyId、QueryType = 0（PropertyStandardQuery）
        var outBuf = new byte[1024];
        return DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, outBuf, outBuf.Length, out int got, IntPtr.Zero) && got > 0
            ? outBuf[..got] : null;
    }

    private static string ReadAnsi(byte[] b, int offset)
    {
        if (offset <= 0 || offset >= b.Length) return "";
        int e = offset;
        while (e < b.Length && b[e] != 0) e++;
        return Encoding.ASCII.GetString(b, offset, e - offset).Trim();
    }

    // ---------------------------------------------------------------- モニター（画面構成＋EDID）

    private static List<ScannedMonitor> ReadMonitors()
    {
        var list = new List<ScannedMonitor>();
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return list;
        var paths = new DISPLAYCONFIG_PATH_INFO[np];
        var modes = new DISPLAYCONFIG_MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;

        var seen = new HashSet<string>();
        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = 2;   // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
            target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            target.header.adapterId = p.targetInfo.adapterId;
            target.header.id = p.targetInfo.id;
            if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = 1;   // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
            source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            source.header.adapterId = p.sourceInfo.adapterId;
            source.header.id = p.sourceInfo.id;
            string? gdi = DisplayConfigGetDeviceInfo(ref source) == 0 ? source.viewGdiDeviceName : null;

            // \\?\DISPLAY#GSM5BBF#5&2a6b...&UID4353#{e6f07b5f-...} → 機種コードと接続先
            var parts = (target.monitorDevicePath ?? "").Split('#');
            string key = parts.Length >= 3 ? parts[1] + "#" + parts[2] : $"target{p.targetInfo.id}";
            if (!seen.Add(key)) continue;   // 複製表示で同じモニターが2回出ることがある

            double? inches = parts.Length >= 3 ? Try(() => EdidInches(parts[1], parts[2]), null) : null;
            var rr = p.targetInfo.refreshRate;
            int hz = rr.Denominator > 0 ? (int)Math.Round((double)rr.Numerator / rr.Denominator) : 60;
            string name = string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                ? (parts.Length >= 2 ? parts[1] : "モニター") : target.monitorFriendlyDeviceName.Trim();
            list.Add(new ScannedMonitor(key, name, inches, hz, gdi));
        }
        return list;
    }

    /// <summary>EDID から画面の対角（インチ）を出す。細かい寸法（mm）があればそれを、なければ cm の値を使う。</summary>
    private static double? EdidInches(string hwId, string instance)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hwId}\{instance}\Device Parameters");
        if (k?.GetValue("EDID") is not byte[] e || e.Length < 128) return null;
        double wMm = e[66] | ((e[68] & 0xF0) << 4);
        double hMm = e[67] | ((e[68] & 0x0F) << 8);
        if (wMm < 100 || hMm < 50) { wMm = e[21] * 10; hMm = e[22] * 10; }
        if (wMm < 100 || hMm < 50) return null;
        return Math.Round(Math.Sqrt(wMm * wMm + hMm * hMm) / 25.4, 1);
    }

    // ---------------------------------------------------------------- 明るさ（DDC/CI。対応していないモニターは null）

    /// <summary>GDI 名（\\.\DISPLAY1 など）ごとの明るさ（0〜100）。読めたものだけ入る。時間がかかることがあるので UI スレッドで呼ばない。</summary>
    public static Dictionary<string, double> ReadBrightness()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        foreach (var hMon in handles)
        {
            try
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMon, ref info)) continue;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0) continue;
                var phys = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, phys)) continue;
                try
                {
                    if (GetMonitorBrightness(phys[0].hPhysicalMonitor, out uint min, out uint cur, out uint max) && max > min)
                        result[info.szDevice] = Math.Clamp((cur - min) * 100.0 / (max - min), 0, 100);
                }
                finally { DestroyPhysicalMonitors(count, phys); }
            }
            catch { }
        }
        return result;
    }

    /// <summary>調べた結果を publish フォルダの power-diagnostics.txt に書く（実機で見積もりがおかしいときの確認用）。</summary>
    public static void WriteDiagnostics(SystemConfig c, IReadOnlyDictionary<string, double>? brightness)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"調べた日時: {c.ScannedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"マザーボード: {c.Board ?? "(不明)"}");
            sb.AppendLine($"メモリ: {c.MemoryType ?? "(種類不明)"} {c.MemoryModules} 枚 {c.MemoryTotalGb} GB");
            sb.AppendLine($"ドライブ: {c.Disks.Count} 台");
            foreach (var d in c.Disks) sb.AppendLine($"  {d.Kind}: {d.Model}");
            sb.AppendLine($"モニター: {c.Monitors.Count} 台");
            foreach (var m in c.Monitors)
            {
                string b = m.GdiName != null && brightness != null && brightness.TryGetValue(m.GdiName, out var v) ? $"{v:0}%" : "(読めない)";
                sb.AppendLine($"  {m.Name} / {(m.Inches?.ToString("0.0") ?? "?")} 型 / {m.RefreshHz} Hz / {m.GdiName} / 明るさ {b} / key={m.Key}");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "power-diagnostics.txt"), sb.ToString());
        }
        catch { }
    }

    // ---------------------------------------------------------------- Win32

    [DllImport("kernel32.dll")]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, IntPtr buffer, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { [FieldOffset(0)] public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public int rcMonitorLeft, rcMonitorTop, rcMonitorRight, rcMonitorBottom;
        public int rcWorkLeft, rcWorkTop, rcWorkRight, rcWorkBottom;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);
}
