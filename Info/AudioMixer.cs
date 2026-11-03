using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MyDashboard.Info.CoreAudio;

namespace MyDashboard.Info;

/// <summary>音量ミキサーの1行（全体、またはアプリ1つ）。</summary>
public sealed class MixerEntry
{
    /// <summary>行を見分ける名前（master／self／プロセス名）。一覧を読み直しても同じアプリなら同じ。</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public bool IsMaster => Key == AudioMixer.MasterKey;
    public float Level { get; internal set; }
    public bool Muted { get; internal set; }
    /// <summary>今鳴っている音の大きさ（0〜1）。</summary>
    public float Peak { get; internal set; }

    internal IAudioEndpointVolume? Endpoint;
    internal readonly List<ISimpleAudioVolume> Volumes = new();
    internal readonly List<IAudioMeterInformation> Meters = new();
}

/// <summary>出力機器の1つ（スピーカー・ヘッドホンなど）。</summary>
public sealed record OutputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Windows の「音量ミキサー」と同じもの（既定の出力機器の全体の音量と、アプリごとの音量）を読み書きする。
/// ・同じプロセス名の音声は1行にまとめる（Spotify やブラウザは複数のプロセスに分かれるため）
/// ・このダッシュボードの Twitch・YouTube の音（子プロセスの msedgewebview2）は「このダッシュボード」の1行にする
/// ・システム音と終了済みの音声は出さない。音を出したことのあるアプリが多いときは、鳴っているもの優先で MaxApps 個まで
/// </summary>
public sealed class AudioMixer
{
    public const string MasterKey = "master";
    public const string SelfKey = "self";
    private const int MaxApps = 7;

    private readonly Dictionary<string, (string Name, ImageSource? Icon)> _apps = new();
    private List<MixerEntry> _entries = new();

    /// <summary>既定の出力機器の名前（例：スピーカー (Realtek(R) Audio)）。</summary>
    public string DeviceName { get; private set; } = "";
    private string _deviceId = "";

    /// <summary>有効な出力機器の一覧（名前順）。</summary>
    public IReadOnlyList<OutputDevice> Devices()
    {
        var list = new List<OutputDevice>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(Render, ActiveDevices, out var devices) != 0) return list;
            devices.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                try
                {
                    if (devices.Item(i, out var d) != 0 || d.GetId(out var id) != 0) continue;
                    list.Add(new OutputDevice(id, FriendlyName(d) ?? id, id == _deviceId));
                }
                catch { }
            }
        }
        catch { }
        return list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>既定の出力機器を変える（Windows のサウンドの設定と同じ。音楽・通話の両方）。</summary>
    public static bool SetDefaultDevice(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            bool ok = true;
            for (int role = 0; role < 3; role++)  // 0＝コンソール、1＝マルチメディア、2＝通信
                ok &= policy.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }
        catch { return false; }
    }

    /// <summary>一覧を読み直す。先頭が全体、続いてアプリ。</summary>
    public IReadOnlyList<MixerEntry> Refresh()
    {
        var list = new List<MixerEntry>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var device) != 0) return _entries = list;
            DeviceName = FriendlyName(device) ?? "";
            _deviceId = device.GetId(out var did) == 0 ? did : "";

            var master = new MixerEntry { Key = MasterKey, Name = "全体", Sub = "PC 全体の音量" };
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, InprocServer, IntPtr.Zero, out object ev) == 0) master.Endpoint = (IAudioEndpointVolume)ev;
            iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, InprocServer, IntPtr.Zero, out object mm) == 0) master.Meters.Add((IAudioMeterInformation)mm);
            list.Add(master);

            iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, InprocServer, IntPtr.Zero, out object sm);
            ((IAudioSessionManager2)sm).GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int n);

            var procs = ProcessTable();
            var groups = new Dictionary<string, (MixerEntry Entry, bool Active)>();
            for (int i = 0; i < n; i++)
            {
                try
                {
                    sessions.GetSession(i, out var control);
                    control.GetState(out int state);
                    if (state == SessionExpired || control.IsSystemSoundsSession() == 0) continue;
                    control.GetProcessId(out uint pid);
                    if (pid == 0 || !procs.TryGetValue(pid, out var proc)) continue;

                    string key = IsOurs(pid, procs) ? SelfKey : proc.Exe;
                    if (!groups.TryGetValue(key, out var g))
                    {
                        var (name, icon) = AppInfo(key, pid, control);
                        g = (new MixerEntry
                        {
                            Key = key, Name = key == SelfKey ? "このダッシュボード" : name, Icon = icon,
                            Sub = key == SelfKey ? "Twitch・YouTube" : "",
                        }, false);
                    }
                    g.Entry.Volumes.Add((ISimpleAudioVolume)control);
                    g.Entry.Meters.Add((IAudioMeterInformation)control);
                    groups[key] = (g.Entry, g.Active || state == 1 /* 鳴っている */);
                }
                catch { /* 読んでいる間に閉じた音声 */ }
            }
            list.AddRange(groups.Values
                .OrderByDescending(g => g.Active)
                .ThenBy(g => g.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxApps)
                .Select(g => g.Entry)
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        catch { }
        _entries = list;
        foreach (var e in list) ReadVolume(e);
        return list;
    }

    /// <summary>音量と消音を読み直す（ほかの場所で変えられたとき用）。</summary>
    public void ReadVolumes()
    {
        foreach (var e in _entries) ReadVolume(e);
    }

    /// <summary>今鳴っている音の大きさを読む。1秒に20回ほど呼ぶ。</summary>
    public void ReadPeaks()
    {
        foreach (var e in _entries)
        {
            float peak = 0;
            foreach (var m in e.Meters)
            {
                try { if (m.GetPeakValue(out float p) == 0) peak = Math.Max(peak, p); }
                catch { }
            }
            e.Peak = peak;
        }
    }

    public void SetLevel(MixerEntry e, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        var ctx = Guid.Empty;
        try
        {
            if (e.Endpoint is { } ep)
            {
                ep.SetMasterVolumeLevelScalar(level, ref ctx);
                ep.SetMute(0, ref ctx);
            }
            foreach (var v in e.Volumes)
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
        catch { }
        e.Level = level;
        e.Muted = false;
    }

    public void SetMute(MixerEntry e, bool muted)
    {
        var ctx = Guid.Empty;
        try { e.Endpoint?.SetMute(muted ? 1 : 0, ref ctx); }
        catch { }
        foreach (var v in e.Volumes)
        {
            try { v.SetMute(muted ? 1 : 0, ref ctx); }
            catch { }
        }
        e.Muted = muted;
    }

    private static void ReadVolume(MixerEntry e)
    {
        try
        {
            if (e.Endpoint is { } ep)
            {
                ep.GetMasterVolumeLevelScalar(out float level);
                ep.GetMute(out int muted);
                e.Level = level;
                e.Muted = muted != 0;
                return;
            }
            foreach (var v in e.Volumes)
            {
                try
                {
                    v.GetMasterVolume(out float level);
                    v.GetMute(out int muted);
                    e.Level = level;
                    e.Muted = muted != 0;
                    return;
                }
                catch { }
            }
        }
        catch { }
    }

    // =========================================================== アプリの名前とアイコン

    /// <summary>アプリの表示名とアイコン。ファイルの説明（例：Google Chrome）を名前にする。プロセス名ごとに一度だけ調べる。</summary>
    private (string Name, ImageSource? Icon) AppInfo(string key, uint pid, IAudioSessionControl2 control)
    {
        if (_apps.TryGetValue(key, out var known)) return known;
        string name = key;
        ImageSource? icon = null;
        try
        {
            string? path = key == SelfKey ? Environment.ProcessPath : ImagePath(pid);
            if (path != null)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var desc = string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
                icon = IconOf(path);
            }
            // アプリが自分で名前を付けている音声はそれを使う（「@%SystemRoot%…」のような資源の参照は読まない）
            if (control.GetDisplayName(out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                var display = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) name = display.Trim();
            }
        }
        catch { }
        if (name == key && name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        return _apps[key] = (name, icon);
    }

    private static string? ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static ImageSource? IconOf(string path)
    {
        IntPtr[] large = new IntPtr[1], small = new IntPtr[1];
        if (ExtractIconEx(path, 0, large, small, 1) == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch { return null; }
        finally
        {
            DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }
    }

    // =========================================================== プロセスの親子

    /// <summary>この音声が自分（または自分の子孫の WebView2）のものか。</summary>
    private static bool IsOurs(uint pid, Dictionary<uint, (uint Parent, string Exe)> procs)
    {
        uint self = (uint)Environment.ProcessId;
        for (int depth = 0; depth < 5 && pid != 0; depth++)
        {
            if (pid == self) return true;
            if (!procs.TryGetValue(pid, out var p)) return false;
            pid = p.Parent;
        }
        return false;
    }

    /// <summary>全プロセスの親とプロセス名（小文字、.exe なし）。</summary>
    private static Dictionary<uint, (uint Parent, string Exe)> ProcessTable()
    {
        var map = new Dictionary<uint, (uint, string)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                var exe = e.ExeFile.ToLowerInvariant();
                if (exe.EndsWith(".exe")) exe = exe[..^4];
                map[e.ProcessId] = (e.ParentProcessId, exe);
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
