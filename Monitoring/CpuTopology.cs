using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MyDashboard.Monitoring;

/// <summary>
/// Windows から物理コアごとの種類（P コア / E コア）を読む。
/// GetLogicalProcessorInformationEx の EfficiencyClass が大きいほど高性能（= P コア）。
/// 全コアが同じ値なら P/E の区別がない CPU とみなす。
/// </summary>
public static class CpuTopology
{
    private static readonly Lazy<bool[]?> _isECore = new(Read);

    /// <summary>コア番号順（1 番目のコア = [0]）に E コアかどうか。区別がない CPU や取得失敗時は null。</summary>
    public static bool[]? IsECore => _isECore.Value;

    private static bool[]? Read()
    {
        try
        {
            const int RelationProcessorCore = 0;
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return null;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return null;

                // 各コア：先頭の論理プロセッサ番号と EfficiencyClass
                var cores = new List<(int first, byte cls)>();
                for (int off = 0; off < len;)
                {
                    var p = buf + off;
                    int size = Marshal.ReadInt32(p, 4);
                    if (size <= 0) break;
                    byte cls = Marshal.ReadByte(p, 9);
                    ulong mask = (ulong)Marshal.ReadInt64(p, 32);
                    ushort group = (ushort)Marshal.ReadInt16(p, 40);
                    if (mask != 0) cores.Add((group * 64 + BitOperations.TrailingZeroCount(mask), cls));
                    off += size;
                }
                if (cores.Count == 0) return null;
                byte max = cores.Max(c => c.cls);
                if (cores.All(c => c.cls == max)) return null;
                return cores.OrderBy(c => c.first).Select(c => c.cls < max).ToArray();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
