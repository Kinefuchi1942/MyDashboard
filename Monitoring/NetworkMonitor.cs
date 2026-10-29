using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;

namespace MyDashboard.Monitoring;

/// <summary>有線・無線アダプタの合計送受信速度（バイト/秒）。OSの言語に依存しない取り方。</summary>
public sealed class NetworkMonitor
{
    private long _lastRx, _lastTx;
    private readonly Stopwatch _sw = new();

    public NetworkInfo Read()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // 仮想アダプタ（Hyper-V, VPN など）の二重計上を避ける
            var desc = nic.Description;
            if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Filter", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var st = nic.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
            catch { /* 一時的に取れないアダプタは飛ばす */ }
        }

        double seconds = _sw.IsRunning ? _sw.Elapsed.TotalSeconds : 0;
        _sw.Restart();

        NetworkInfo result = new(0, 0);
        if (seconds > 0 && _lastRx > 0)
        {
            result = new NetworkInfo(
                Math.Max(0, (rx - _lastRx) / seconds),
                Math.Max(0, (tx - _lastTx) / seconds));
        }
        _lastRx = rx;
        _lastTx = tx;
        return result;
    }
}
