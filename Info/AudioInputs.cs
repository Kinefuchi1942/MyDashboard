using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static MyDashboard.Info.CoreAudio;

namespace MyDashboard.Info;

/// <summary>入力機器の1つ（ライン入力・マイクなど）。音量ミキサーの「入力を聴く」の1行。</summary>
public sealed class InputEntry
{
    public string Id { get; init; } = "";
    /// <summary>機器の名前（例：ライン入力）。</summary>
    public string Name { get; init; } = "";
    /// <summary>機器のつながっている先（例：Realtek(R) Audio）。</summary>
    public string Sub { get; init; } = "";
    public float Level { get; internal set; }
    public float Peak { get; internal set; }
    /// <summary>「このデバイスを聴く」がオンか。</summary>
    public bool Listening { get; internal set; }
    /// <summary>聴く音を鳴らす出力機器の ID。空なら既定の再生デバイス。</summary>
    public string Target { get; internal set; } = "";

    internal IAudioEndpointVolume? Endpoint;
    internal IAudioMeterInformation? Meter;
    internal IAudioClient? Client;
}

/// <summary>
/// 入力機器の音量と、Windows の「このデバイスを聴く」（録音機器のプロパティ → 聴く）を読み書きする。
/// ・「聴く」は機器の設定値 {24DBB0FC-…},1（オン／オフ）と ,0（鳴らす先の出力機器の ID）。書き込みは IPolicyConfig.SetPropertyValue
///   （プロパティストアに直接書くと音声エンジンに伝わらない）。管理者権限が要る（このアプリは管理者で動く）
/// ・入力の音の大きさは、だれかが録音していないと 0 のままなので、パネルを開いている間だけ自分で録音の流れを開く（中身は読まない）
/// ・切り替えの結果は %APPDATA%\MyDashboard\audio-diagnostics.txt に書く（実機での確認用）
/// </summary>
public sealed class AudioInputs
{
    private const int MaxInputs = 4;
    private static readonly Guid ListenFormat = new("24DBB0FC-9311-4B3D-9CF0-18FF155639D4");
    private static PropertyKey ListenTargetKey => new() { FormatId = ListenFormat, PropertyId = 0 };
    private static PropertyKey ListenEnabledKey => new() { FormatId = ListenFormat, PropertyId = 1 };
    private const ushort VtEmpty = 0, VtBool = 11, VtLpwstr = 31;

    private readonly Dictionary<string, InputEntry> _inputs = new();
    private readonly List<string> _log = new();

    /// <summary>有効な入力機器を読み直す（名前順）。同じ機器は同じ InputEntry のまま（録音の流れを開き直さないため）。</summary>
    public IReadOnlyList<InputEntry> Refresh()
    {
        var seen = new HashSet<string>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(Capture, ActiveDevices, out var devices) == 0)
            {
                devices.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    try
                    {
                        if (devices.Item(i, out var d) != 0 || d.GetId(out var id) != 0) continue;
                        if (!_inputs.TryGetValue(id, out var e))
                        {
                            var (name, sub) = Split(FriendlyName(d) ?? id);
                            e = new InputEntry { Id = id, Name = name, Sub = sub };
                            Open(d, e);
                            _inputs[id] = e;
                        }
                        ReadListen(d, e);
                        ReadVolume(e);
                        seen.Add(id);
                    }
                    catch { }
                }
            }
        }
        catch { }
        foreach (var gone in _inputs.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Close(_inputs[gone]);
            _inputs.Remove(gone);
        }
        return _inputs.Values
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Sub, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxInputs)
            .ToList();
    }

    /// <summary>パネルを閉じたとき。録音の流れを閉じる。</summary>
    public void Stop()
    {
        foreach (var e in _inputs.Values) Close(e);
        _inputs.Clear();
    }

    /// <summary>今入ってきている音の大きさを読む。1秒に20回ほど呼ぶ。</summary>
    public void ReadPeaks()
    {
        foreach (var e in _inputs.Values)
        {
            try { e.Peak = e.Meter != null && e.Meter.GetPeakValue(out float p) == 0 ? p : 0; }
            catch { e.Peak = 0; }
        }
    }

    public void SetLevel(InputEntry e, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        var ctx = Guid.Empty;
        try
        {
            e.Endpoint?.SetMasterVolumeLevelScalar(level, ref ctx);
            e.Endpoint?.SetMute(0, ref ctx);
        }
        catch { }
        e.Level = level;
    }

    /// <summary>「このデバイスを聴く」をオン／オフする。</summary>
    public bool SetListening(InputEntry e, bool on)
    {
        bool ok = WriteBool(e.Id, on, out int hr);
        Log($"聴く {(on ? "オン" : "オフ")}：{e.Name}（{e.Sub}） → 0x{hr:X8}");
        if (ok) e.Listening = on;
        VerifyAndSave(e);
        return ok;
    }

    /// <summary>聴く音を鳴らす出力機器を変える（空なら既定の再生デバイス）。オンのときは一度切って入れ直す（そうしないと鳴る先が変わらないことがある）。</summary>
    public bool SetTarget(InputEntry e, string outputId)
    {
        bool ok = WriteString(e.Id, outputId, out int hr);
        Log($"鳴らす先：{e.Name} → {(outputId.Length > 0 ? outputId : "既定の再生デバイス")} → 0x{hr:X8}");
        if (ok)
        {
            e.Target = outputId;
            if (e.Listening)
            {
                WriteBool(e.Id, false, out _);
                WriteBool(e.Id, true, out int hr2);
                Log($"  入れ直し → 0x{hr2:X8}");
            }
        }
        VerifyAndSave(e);
        return ok;
    }

    // =========================================================== 読み書き

    private static void Open(IMMDevice d, InputEntry e)
    {
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (d.Activate(ref iid, InprocServer, IntPtr.Zero, out object ev) == 0) e.Endpoint = (IAudioEndpointVolume)ev;
            iid = typeof(IAudioMeterInformation).GUID;
            if (d.Activate(ref iid, InprocServer, IntPtr.Zero, out object mm) == 0) e.Meter = (IAudioMeterInformation)mm;
            iid = typeof(IAudioClient).GUID;
            if (d.Activate(ref iid, InprocServer, IntPtr.Zero, out object ac) != 0) return;
            var client = (IAudioClient)ac;
            if (client.GetMixFormat(out IntPtr format) != 0) return;
            try
            {
                // 共有モード・1秒のバッファ。中身は読まないので、あふれた分は捨てられるだけ
                if (client.Initialize(0, 0, 10_000_000, 0, format, IntPtr.Zero) == 0 && client.Start() == 0) e.Client = client;
            }
            finally { Marshal.FreeCoTaskMem(format); }
        }
        catch { }
    }

    private static void Close(InputEntry e)
    {
        try { e.Client?.Stop(); } catch { }
        foreach (var o in new object?[] { e.Client, e.Meter, e.Endpoint })
        {
            try { if (o != null) Marshal.ReleaseComObject(o); } catch { }
        }
        e.Client = null;
        e.Meter = null;
        e.Endpoint = null;
    }

    private static void ReadVolume(InputEntry e)
    {
        try
        {
            if (e.Endpoint != null && e.Endpoint.GetMasterVolumeLevelScalar(out float level) == 0) e.Level = level;
        }
        catch { }
    }

    private static void ReadListen(IMMDevice d, InputEntry e)
    {
        try
        {
            if (d.OpenPropertyStore(0 /* 読み取り */, out var store) != 0) return;
            var key = ListenEnabledKey;
            if (store.GetValue(ref key, out var v) == 0)
            {
                try { e.Listening = v.Type == VtBool && (short)(v.Value.ToInt64() & 0xFFFF) != 0; }
                finally { PropVariantClear(ref v); }
            }
            key = ListenTargetKey;
            if (store.GetValue(ref key, out var t) == 0)
            {
                try { e.Target = t.Type == VtLpwstr ? Marshal.PtrToStringUni(t.Value) ?? "" : ""; }
                finally { PropVariantClear(ref t); }
            }
        }
        catch { }
    }

    private static bool WriteBool(string deviceId, bool on, out int hr)
    {
        hr = -1;
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            var key = ListenEnabledKey;
            var v = new PropVariant { Type = VtBool, Value = new IntPtr(on ? 0xFFFF : 0) };  // VARIANT_TRUE＝-1
            hr = policy.SetPropertyValue(deviceId, 0, ref key, ref v);
            return hr == 0;
        }
        catch (Exception ex) { hr = ex.HResult; return false; }
    }

    private static bool WriteString(string deviceId, string value, out int hr)
    {
        hr = -1;
        IntPtr text = IntPtr.Zero;
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            var key = ListenTargetKey;
            text = Marshal.StringToCoTaskMemUni(value);
            var v = new PropVariant { Type = VtLpwstr, Value = text };
            hr = policy.SetPropertyValue(deviceId, 0, ref key, ref v);
            return hr == 0;
        }
        catch (Exception ex) { hr = ex.HResult; return false; }
        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
    }

    /// <summary>「ライン入力 (Realtek(R) Audio)」を名前とつながっている先に分ける。</summary>
    internal static (string Name, string Sub) Split(string friendly)
    {
        int i = friendly.IndexOf(" (", StringComparison.Ordinal);
        if (i <= 0 || !friendly.EndsWith(')')) return (friendly, "");
        return (friendly[..i], friendly[(i + 2)..^1]);
    }

    // =========================================================== 診断

    private void Log(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (_log.Count > 40) _log.RemoveAt(0);
    }

    /// <summary>書いたあとの値を読み直して、診断ファイルに残す。</summary>
    private void VerifyAndSave(InputEntry e)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(Capture, ActiveDevices, out var devices) == 0)
            {
                devices.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (devices.Item(i, out var d) != 0 || d.GetId(out var id) != 0 || id != e.Id) continue;
                    var check = new InputEntry { Id = id };
                    ReadListen(d, check);
                    Log($"  読み直し：聴く={(check.Listening ? "オン" : "オフ")} 鳴らす先={(check.Target.Length > 0 ? check.Target : "既定")}");
                }
            }
        }
        catch (Exception ex) { Log("  読み直し失敗：" + ex.Message); }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("音量ミキサー「入力を聴く」の診断");
            sb.AppendLine($"作成：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("0x00000000 なら成功。それ以外は Windows が受け付けなかった");
            sb.AppendLine();
            sb.AppendLine("[入力機器]");
            foreach (var x in _inputs.Values)
                sb.AppendLine($"{x.Name}（{x.Sub}） 聴く={(x.Listening ? "オン" : "オフ")} 音量={x.Level * 100:0} 録音の流れ={(x.Client != null ? "開いた" : "開けない")} ID={x.Id}");
            sb.AppendLine();
            sb.AppendLine("[操作の記録]");
            foreach (var l in _log) sb.AppendLine(l);
            File.WriteAllText(AppPaths.Data("audio-diagnostics.txt"), sb.ToString());
        }
        catch { }
    }
}
