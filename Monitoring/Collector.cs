using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MyDashboard.Monitoring;

/// <summary>一定間隔でセンサーを読み、Snapshot をイベントで通知する（UIスレッド外で動く）。</summary>
public sealed class Collector : IDisposable
{
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private HardwareMonitor? _hw;
    private FpsMonitor? _fps;
    private readonly NetworkMonitor _net = new();
    private Task? _loop;

    public event Action<Snapshot>? Updated;
    public event Action<string>? Error;

    public Collector(AppSettings settings) => _settings = settings;

    public void Start()
    {
        // 設定の保存で作り直したとき、二重に動かさない
        if (_loop != null || _cts.IsCancellationRequested) return;
        _loop = Task.Run(() => Run(_cts.Token));
    }

    private async Task Run(CancellationToken ct)
    {
        bool driverMissing = false;
        try
        {
            _hw = new HardwareMonitor();
            driverMissing = !HardwareMonitor.IsPawnIoInstalled;
        }
        catch (Exception ex)
        {
            Error?.Invoke("センサーの初期化に失敗しました: " + ex.Message);
        }
        _fps = new FpsMonitor(_settings.FpsExcludeProcesses, _settings.EnableFps);

        var interval = TimeSpan.FromMilliseconds(_settings.UpdateIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                CpuInfo? cpu = null;
                GpuInfo? gpu = null;
                IReadOnlyList<DiskInfo> disks = Array.Empty<DiskInfo>();
                if (_hw != null)
                {
                    _hw.Update();
                    cpu = _hw.ReadCpu();
                    gpu = _hw.ReadGpu();
                    disks = _hw.ReadDisks();
                }
                var snap = new Snapshot(
                    cpu, gpu,
                    HardwareMonitor.ReadMemory(),
                    disks,
                    _net.Read(),
                    _fps.Read(),
                    driverMissing && cpu?.Temperature == null);
                Updated?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
            }

            var wait = interval - (DateTime.UtcNow - started);
            if (wait < TimeSpan.FromMilliseconds(50)) wait = TimeSpan.FromMilliseconds(50);
            try { await Task.Delay(wait, ct); } catch (TaskCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        _fps?.Dispose();
        _hw?.Dispose();
    }
}
