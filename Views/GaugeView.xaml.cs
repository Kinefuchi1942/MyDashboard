using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows;
using MyDashboard.Controls;
using MyDashboard.Info;
using MyDashboard.Monitoring;

namespace MyDashboard.Views;

/// <summary>計器盤レイアウト（案A）。CPU・FPS・GPU を円弧メーターで見せる。</summary>
public partial class GaugeView : UserControl, IDashboardView
{
    private readonly ViewContext _ctx;
    private readonly Brush _cpu = ViewContext.Res("Cpu");
    private readonly Brush _gpu = ViewContext.Res("Gpu");
    private readonly Brush _value = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF8));
    private readonly Brush _sub = new SolidColorBrush(Color.FromRgb(0xAE, 0xB9, 0xCA));
    private readonly Brush _tempNormal = new SolidColorBrush(Color.FromRgb(0x8F, 0xA0, 0xB8));
    private readonly double _refreshRate = Display.PrimaryRefreshRate();
    // DLSS のバッジ（読み込みあり＝NVIDIA の緑、なし＝暗く）
    private readonly Brush _chipOnBorder = new SolidColorBrush(Color.FromArgb(0xB3, 0x76, 0xB9, 0x00));
    private readonly Brush _chipOnFill = new SolidColorBrush(Color.FromArgb(0x29, 0x76, 0xB9, 0x00));
    private readonly Brush _chipOnText = new SolidColorBrush(Color.FromRgb(0xB8, 0xE0, 0x6A));
    private readonly Brush _chipOffBorder = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private readonly Brush _chipOffText = new SolidColorBrush(Color.FromRgb(0x4F, 0x5B, 0x6D));
    private readonly Brush _chipWarnBorder = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xB5, 0x47));
    private readonly Brush _chipWarnFill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xB5, 0x47));

    public GaugeView(ViewContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        foreach (var chart in new[] { MemChart, VramChart, DiskChart, NetChart })
            chart.SetCapacity(Math.Max(20, ctx.HistoryPoints * 2 / 3));
        FtCaption.Text = $"直近10秒 · 点線＝{_refreshRate:0}Hz";

        Threads.Normal = _cpu;
        Threads.ECore = BarMeter.Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0x5C, 0xC8, 0xD7)));  // CPU 色を淡く
        Threads.Label = ViewContext.Res("Dim");
        Threads.Warn = ViewContext.Res("Warn");
        Threads.Crit = ViewContext.Res("Crit");
        Threads.WarnAt = ctx.Settings.LoadWarn;
        Threads.CritAt = ctx.Settings.LoadCrit;
    }

    // ニュースは表示しない（計器盤はメーターに専念する）
    public void ShowNews(NewsState news) { }

    public void UpdateClock(DateTime now) => ClockText.Text = Fmt.Time(now);

    public void SetNotice(string text) => Notice.Text = text;

    public void Render(Snapshot s)
    {
        // ---- CPU：外側＝使用率、内側＝温度（0〜100℃）
        if (s.Cpu is { } cpu)
        {
            SetArc(CpuArc, CpuTrack, cpu.Load, cpu.Temperature, _ctx.LoadBrush(cpu.Load, _cpu),
                   _ctx.CpuTempBrush(cpu.Temperature, _tempNormal));
            CpuValue.Text = Fmt.Num(cpu.Load);
            CpuValue.Foreground = _ctx.LoadBrush(cpu.Load, _value);
            CpuSub.Text = $"{Fmt.Temp(cpu.Temperature)} · {Fmt.Ghz(cpu.ClockMHz)}";
            CpuSub.Foreground = _ctx.CpuTempBrush(cpu.Temperature, _sub);

            // スレッド別：いちばん高いスレッドの % も出す（1スレッドだけ詰まっているのを見つけるため）
            Threads.SetValues(cpu.ThreadLoads, cpu.ThreadIsECore);
            ThreadCount.Text = cpu.ThreadLoads.Count > 0 ? cpu.ThreadLoads.Count.ToString() : "--";
            double? maxLoad = cpu.ThreadLoads.Count > 0 ? cpu.ThreadLoads.Max() : null;
            ThreadMax.Text = Fmt.Num(maxLoad);
            ThreadMax.Foreground = _ctx.LoadBrush(maxLoad, _value);
        }

        // ---- GPU
        if (s.Gpu is { } gpu)
        {
            SetArc(GpuArc, GpuTrack, gpu.Load, gpu.Temperature, _ctx.LoadBrush(gpu.Load, _gpu),
                   _ctx.GpuTempBrush(gpu.Temperature, _tempNormal));
            GpuValue.Text = Fmt.Num(gpu.Load);
            GpuValue.Foreground = _ctx.LoadBrush(gpu.Load, _value);
            GpuSub.Text = $"{Fmt.Temp(gpu.Temperature)} · {Fmt.Watt(gpu.PowerW)}";
            GpuSub.Foreground = _ctx.GpuTempBrush(gpu.Temperature, _sub);
        }

        // ---- FPS：モニターのリフレッシュレートを満点とする
        var f = s.Fps;
        if (f.State == FpsState.Running && f.Fps is { } fps)
        {
            FpsArc.Animate(fps / _refreshRate);
            FpsLabel.Text = f.Application ?? "FPS";
            FpsValue.Text = Fmt.Num(fps);
            FpsSub.Text = $"1% Low {Fmt.Num(f.Low1Percent)} · {Fmt.Ms(f.FrameTimeMs)}";
            Status.Text = "";

            FrameTimePanel.Opacity = 1;
            FtValue.Text = f.FrameTimeMs is { } ms ? ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "--";
            LowValue.Text = Fmt.Num(f.Low1Percent);
            // 1% Low が平均の7割を切ったら琥珀色（カクつきが目立つ状態）
            LowValue.Foreground = f.Low1Percent is { } low && low < fps * 0.7 ? ViewContext.Res("Warn") : _value;
            FrameChart.SetData(f.FrameTimes, 1000.0 / _refreshRate);
            ShowDlss(f.Dlss);
            ShowPresentMode(f.PresentMode);
        }
        else
        {
            FrameTimePanel.Opacity = 0.45;
            FtValue.Text = "--";
            LowValue.Text = "--";
            LowValue.Foreground = _value;
            FrameChart.SetData(null, 1000.0 / _refreshRate);
            ShowDlss(null);
            ShowPresentMode(null);

            FpsArc.Animate(0);
            FpsLabel.Text = "FPS";
            FpsValue.Text = "--";
            FpsSub.Text = f.State switch
            {
                FpsState.Waiting => "ゲームの描画を待っています",
                FpsState.Disabled => "FPS計測はオフです",
                _ => "FPSは計測していません",
            };
            Status.Text = f.State is FpsState.NotInstalled or FpsState.Failed ? f.Message ?? "" : "";
        }

        // ---- 下段
        if (s.Memory.TotalBytes > 0)
        {
            double pct = s.Memory.UsedBytes / s.Memory.TotalBytes * 100;
            MemText.Text = $"{Fmt.Gb(s.Memory.UsedBytes)} / {Fmt.Gb(s.Memory.TotalBytes)} GB";
            MemText.Foreground = _ctx.UsageBrush(pct, _value);
            MemChart.Push(pct);
        }

        if (s.Gpu is { VramUsedBytes: { } vu, VramTotalBytes: { } vt } && vt > 0)
        {
            double pct = vu / vt * 100;
            VramText.Text = $"{Fmt.Gb(vu)} / {Fmt.Gb(vt)} GB";
            VramText.Foreground = _ctx.UsageBrush(pct, _value);
            VramChart.Push(pct);
        }

        double read = s.Disks.Sum(d => d.ReadBps ?? 0), write = s.Disks.Sum(d => d.WriteBps ?? 0);
        DiskText.Text = DiskPair(read, write);
        DiskChart.Push(read, write);

        var (down, unit) = Fmt.BitsSplit(s.Network.DownBps);
        var (up, upUnit) = Fmt.BitsSplit(s.Network.UpBps);
        NetText.Text = unit == upUnit ? $"↓{down}  ↑{up} {unit}" : $"↓{down} {unit}  ↑{up} {upUnit}";
        NetChart.Push(s.Network.DownBps, s.Network.UpBps);

        Power.Show(s.Power);
    }

    /// <summary>DLSS のバッジ。ゲームが DLSS の部品を読み込んでいないときは何も出さない。</summary>
    private void ShowDlss(DlssInfo? d)
    {
        if (d == null || d.State == DlssState.None)
        {
            DlssPanel.Visibility = Visibility.Collapsed;
            return;
        }
        DlssPanel.Visibility = Visibility.Visible;
        if (d.State == DlssState.Unknown)
        {
            SetChip(DlssSrChip, DlssSrText, false);
            DlssSrText.Text = "DLSS: unavailable";
            DlssFgChip.Visibility = DlssRrChip.Visibility = Visibility.Collapsed;
            DlssNote.Text = "Blocked by game protection";
            return;
        }
        DlssSrText.Text = "DLSS SR";
        DlssFgChip.Visibility = DlssRrChip.Visibility = Visibility.Visible;
        SetChip(DlssSrChip, DlssSrText, d.SuperResolution);
        SetChip(DlssFgChip, DlssFgText, d.FrameGeneration);
        SetChip(DlssRrChip, DlssRrText, d.RayReconstruction);
        DlssNote.Text = (d.Version != null ? $"DLSS {d.Version} · " : "") + "detected from loaded DLLs";
    }

    /// <summary>
    /// PresentMon の PresentMode。Hardware…（合成をバイパス）は緑、Composed…（DWM 合成を経由）は琥珀色。
    /// </summary>
    private void ShowPresentMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode))
        {
            PresentPanel.Visibility = Visibility.Collapsed;
            return;
        }
        PresentPanel.Visibility = Visibility.Visible;
        PresentText.Text = mode;
        if (mode.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
        {
            SetChip(PresentChip, PresentText, true);
            PresentNote.Text = mode.StartsWith("Hardware Composed", StringComparison.OrdinalIgnoreCase)
                ? " · オーバーレイで表示（合成をバイパス）"
                : " · 合成をバイパス（フルスクリーン相当）";
        }
        else if (mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase))
        {
            PresentChip.BorderBrush = _chipWarnBorder;
            PresentChip.Background = _chipWarnFill;
            PresentText.Foreground = ViewContext.Res("Warn");
            PresentNote.Text = " · DWM 合成あり（遅延・負荷が増加）";
        }
        else
        {
            SetChip(PresentChip, PresentText, false);
            PresentNote.Text = " · 判定できません";
        }
    }

    private void SetChip(Border chip, TextBlock text, bool on)
    {
        chip.BorderBrush = on ? _chipOnBorder : _chipOffBorder;
        chip.Background = on ? _chipOnFill : Brushes.Transparent;
        text.Foreground = on ? _chipOnText : _chipOffText;
    }

    /// <summary>主の弧（発光する方）と、下地＋温度の弧を更新する。</summary>
    private static void SetArc(ArcGauge value, ArcGauge track, double? load, double? temp, Brush stroke, Brush tempStroke)
    {
        value.Stroke = stroke;
        if (value.Effect is DropShadowEffect glow && stroke is SolidColorBrush sb) glow.Color = sb.Color;
        value.Animate((load ?? 0) / 100);

        track.SecondStroke = tempStroke;
        if (temp is { } t)
        {
            track.Animate(0, t / 100);
        }
        else
        {
            track.BeginAnimation(ArcGauge.SecondValueProperty, null);
            track.SecondValue = -1;
        }
    }

    /// <summary>読み書きの速度を同じ単位でまとめて表示する（例：読 412 · 書 18 MB/s）。</summary>
    private static string DiskPair(double read, double write)
    {
        double max = Math.Max(read, write);
        (double div, string unit) = max >= 1024d * 1024 * 1024 ? (1024d * 1024 * 1024, "GB/s")
                                  : max >= 1024d * 1024 ? (1024d * 1024, "MB/s")
                                  : (1024d, "KB/s");
        string F(double v) => (v / div).ToString(v / div >= 100 || div == 1024 ? "0" : "0.0",
                                                  System.Globalization.CultureInfo.InvariantCulture);
        return $"読 {F(read)} · 書 {F(write)} {unit}";
    }
}

/// <summary>メインモニターのリフレッシュレート（FPSメーターの満点に使う）。</summary>
internal static class Display
{
    public static double PrimaryRefreshRate()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmDisplayFrequency > 1)
                return dm.dmDisplayFrequency;
        }
        catch { }
        return 144;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
}
