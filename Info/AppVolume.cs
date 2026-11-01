using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyDashboard.Info;

/// <summary>
/// Windows の「音量ミキサー」のアプリごとの音量を読み書きする（Core Audio）。
/// ・PC 全体の音量や他のアプリには触れない。Spotify アプリ内の音量バーとは別のもの
/// ・アプリはプロセス名で探す（Spotify は複数のプロセスに分かれるので、名前が合う音声すべてに同じ値を設定する）
/// ・音声の一覧は数秒だけ覚えておき、ドラッグ中に何度も探し直さない
/// </summary>
public sealed class AppVolume
{
    private readonly object _lock = new();
    private string? _cachedName;
    private DateTime _cachedAt;
    private List<ISimpleAudioVolume> _cached = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

    /// <summary>今の音量（0〜1）と消音か。音声が見つからなければ null（そのアプリがまだ音を出していないなど）。</summary>
    public (float Level, bool Muted)? Get(string processName)
    {
        lock (_lock)
        {
            foreach (var v in Sessions(processName))
            {
                try
                {
                    v.GetMasterVolume(out float level);
                    v.GetMute(out int muted);
                    return (level, muted != 0);
                }
                catch { /* 閉じた直後の音声。次を見る */ }
            }
            return null;
        }
    }

    public void SetLevel(string processName, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        var ctx = Guid.Empty;
        lock (_lock)
        {
            foreach (var v in Sessions(processName))
            {
                try
                {
                    v.SetMasterVolume(level, ref ctx);
                    // 消音中に音量を動かしたら消音も解く（ミキサーと同じ動き）
                    v.SetMute(0, ref ctx);
                }
                catch { }
            }
        }
    }

    public void SetMute(string processName, bool muted)
    {
        var ctx = Guid.Empty;
        lock (_lock)
        {
            foreach (var v in Sessions(processName))
            {
                try { v.SetMute(muted ? 1 : 0, ref ctx); }
                catch { }
            }
        }
    }

    /// <summary>MediaService のアプリ識別名（例：Spotify.exe、SpotifyAB.SpotifyMusic_…!Spotify、MSEdge）からプロセス名を推定する。</summary>
    public static string ProcessNameOf(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "spotify";
        if (a.Contains("msedge")) return "msedge";
        if (a.Contains("chrome")) return "chrome";
        if (a.Contains("firefox") || a == "308046b0af4a39cb") return "firefox";
        var name = a.Split('!')[^1].Split('\\')[^1];
        return name.EndsWith(".exe") ? name[..^4] : name;
    }

    private List<ISimpleAudioVolume> Sessions(string processName)
    {
        if (_cachedName == processName && _cached.Count > 0 && DateTime.Now - _cachedAt < CacheFor) return _cached;
        _cachedName = processName;
        _cachedAt = DateTime.Now;
        _cached = Find(processName);
        return _cached;
    }

    /// <summary>出力中のすべての機器（スピーカー・ヘッドホンなど）から、そのプロセスの音声を集める。</summary>
    private static List<ISimpleAudioVolume> Find(string processName)
    {
        var found = new List<ISimpleAudioVolume>();
        var names = new Dictionary<uint, string>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.EnumAudioEndpoints(0 /* 出力 */, 1 /* 有効な機器 */, out var devices);
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    devices.Item(i, out var device);
                    var iid = typeof(IAudioSessionManager2).GUID;
                    device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out object o);
                    var manager = (IAudioSessionManager2)o;
                    manager.GetSessionEnumerator(out var sessions);
                    sessions.GetCount(out int n);
                    for (int j = 0; j < n; j++)
                    {
                        try
                        {
                            sessions.GetSession(j, out var control);
                            control.GetState(out int state);
                            if (state == 2 /* 終了済み */) continue;
                            control.GetProcessId(out uint pid);
                            if (pid == 0) continue;
                            if (!names.TryGetValue(pid, out var name))
                            {
                                try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
                                catch { name = ""; }
                                names[pid] = name;
                            }
                            if (name == processName) found.Add((ISimpleAudioVolume)control);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return found;
    }

    // =========================================================== Core Audio の COM 定義

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int muted, ref Guid ctx);
        [PreserveSig] int GetMute(out int muted);
    }
}
