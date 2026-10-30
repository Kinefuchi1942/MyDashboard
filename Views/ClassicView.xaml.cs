using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyDashboard.Controls;
using MyDashboard.Monitoring;

namespace MyDashboard.Views;

/// <summary>標準レイアウト：CPU / GPU / FPS を上段に、詳細を下段に並べるパネル型。</summary>
public partial class ClassicView : UserControl, IDashboardView
{
    private readonly ViewContext _ctx;
    private readonly List<DiskRow> _diskRows = new();
    private readonly Brush _text = ViewContext.Res("Text");

    public ClassicView(ViewContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        foreach (var chart in new[] { CpuChart, GpuChart, FpsChart, MemChart, VramChart, NetChart })
            chart.SetCapacity(ctx.HistoryPoints);

        Threads.Normal = ViewContext.Res("Cpu");
        Threads.ECore = Controls.BarMeter.Freeze(new SolidColorBrush(Color.FromArgb(0x8C, 0x5C, 0xC8, 0xD7)));  // CPU 色を淡く
        Threads.Label = ViewContext.Res("Dim");
        Threads.Warn = ViewContext.Res("Warn");
        Threads.Crit = ViewContext.Res("Crit");
        Threads.WarnAt = ctx.Settings.LoadWarn;
        Threads.CritAt = ctx.Settings.LoadCrit;
    }

    public void UpdateClock(DateTime now)
    {
        ClockText.Text = Fmt.Time(now);
        DateText.Text = Fmt.Date(now);
    }

    public void SetNotice(string text) => Notice.Text = text;

    public void Render(Snapshot s)
    {
        // ---- CPU
        if (s.Cpu is { } cpu)
        {
            CpuName.Text = cpu.Name;
            CpuLoad.Text = Fmt.Num(cpu.Load);
            CpuLoad.Foreground = _ctx.LoadBrush(cpu.Load, _text);
            CpuTemp.Text = Fmt.Temp(cpu.Temperature);
            CpuTemp.Foreground = _ctx.CpuTempBrush(cpu.Temperature, _text);
            CpuClock.Text = Fmt.Ghz(cpu.ClockMHz);
            CpuPower.Text = Fmt.Watt(cpu.PowerW);
            CpuChart.Push(cpu.Load);

            Threads.SetValues(cpu.ThreadLoads, cpu.ThreadIsECore);
            int eCount = System.Linq.Enumerable.Count(cpu.ThreadIsECore ?? Array.Empty<bool>(), e => e);
            ThreadInfo.Text = cpu.ThreadLoads.Count == 0 ? "取得できません"
                : eCount > 0 ? $"{cpu.ThreadLoads.Count} スレッド（P-Core {cpu.ThreadLoads.Count - eCount}・E-Core {eCount}）"
                : $"{cpu.ThreadLoads.Count} スレッド";
        }

        // ---- GPU
        if (s.Gpu is { } gpu)
        {
            GpuName.Text = gpu.Name;
            GpuLoad.Text = Fmt.Num(gpu.Load);
            GpuLoad.Foreground = _ctx.LoadBrush(gpu.Load, _text);
            GpuTemp.Text = Fmt.Temp(gpu.Temperature);
            GpuTemp.Foreground = _ctx.GpuTempBrush(gpu.Temperature, _text);
            GpuClock.Text = Fmt.Mhz(gpu.ClockMHz);
            GpuPower.Text = Fmt.Watt(gpu.PowerW);
            GpuFan.Text = Fmt.Rpm(gpu.FanRpm);
            GpuChart.Push(gpu.Load);

            if (gpu.VramUsedBytes is { } vu && gpu.VramTotalBytes is { } vt && vt > 0)
            {
                double pct = vu / vt * 100;
                VramValue.Text = $"{Fmt.Gb(vu)} / {Fmt.Gb(vt)} GB";
                VramPct.Text = $"{pct:0}%";
                VramBar.Value = pct / 100;
                VramBar.Fill = _ctx.UsageBrush(pct, ViewContext.Res("Gpu"));
                VramChart.Push(pct);
            }
        }
        else
        {
            GpuName.Text = "GPU が見つかりません";
        }

        // ---- メモリ
        if (s.Memory.TotalBytes > 0)
        {
            double pct = s.Memory.UsedBytes / s.Memory.TotalBytes * 100;
            MemValue.Text = $"{Fmt.Gb(s.Memory.UsedBytes)} / {Fmt.Gb(s.Memory.TotalBytes)} GB";
            MemPct.Text = $"{pct:0}%";
            MemBar.Value = pct / 100;
            MemBar.Fill = _ctx.UsageBrush(pct, ViewContext.Res("Mem"));
            MemChart.Push(pct);
        }

        // ---- ネットワーク（ビット毎秒で表示）
        NetDown.Text = Fmt.Bits(s.Network.DownBps);
        NetUp.Text = Fmt.Bits(s.Network.UpBps);
        NetChart.Push(s.Network.DownBps, s.Network.UpBps);

        RenderDisks(s.Disks);
        RenderFps(s.Fps);
        Power.Show(s.Power);
    }

    private void RenderFps(FpsInfo f)
    {
        bool running = f.State == FpsState.Running;
        LiveDot.Fill = ViewContext.Res(running ? "Mem" : "Dim");

        if (running)
        {
            GameTitle.Text = f.Application ?? "";
            FpsApp.Text = f.Application ?? "";
            FpsValue.Text = Fmt.Num(f.Fps);
            FpsLow.Text = Fmt.Num(f.Low1Percent);
            FpsFrameTime.Text = Fmt.Ms(f.FrameTimeMs);
            FpsHint.Text = "";
            FpsChart.Push(f.Fps);
            FpsScale.Text = $"{FpsChart.CurrentMax:0}";
        }
        else
        {
            GameTitle.Text = f.State switch
            {
                FpsState.Waiting => "ゲームの描画を待っています",
                FpsState.Starting => "計測を準備しています",
                _ => "システムの状態",
            };
            FpsApp.Text = "—";
            FpsValue.Text = "--";
            FpsLow.Text = "--";
            FpsFrameTime.Text = "--";
            FpsHint.Text = f.Message ?? "";
            FpsChart.Push(null);
            FpsScale.Text = "";
        }
    }

    private void RenderDisks(IReadOnlyList<DiskInfo> disks)
    {
        if (_diskRows.Count != disks.Count)
        {
            DiskList.Children.Clear();
            _diskRows.Clear();
            double scale = disks.Count <= 3 ? 1.0 : disks.Count <= 4 ? 0.85 : 0.72;
            foreach (var _ in disks)
            {
                var row = new DiskRow(scale);
                _diskRows.Add(row);
                DiskList.Children.Add(row.Root);
            }
        }
        for (int i = 0; i < disks.Count; i++) _diskRows[i].Update(disks[i]);
    }

    /// <summary>ディスク1台ぶんの表示行。毎回作り直さずテキストだけ更新する。</summary>
    private sealed class DiskRow
    {
        public readonly Grid Root = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name, _read, _write, _pct;
        private readonly BarMeter _bar;

        public DiskRow(double scale)
        {
            var num = (FontFamily)Application.Current.FindResource("NumFont");
            var ui = (FontFamily)Application.Current.FindResource("UiFont");
            var dim = ViewContext.Res("Dim");
            var text = ViewContext.Res("Text");
            var disk = ViewContext.Res("Disk");

            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145 * scale) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145 * scale) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110 * scale) });

            _name = new TextBlock { FontFamily = ui, FontSize = 19 * scale, Foreground = dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _read = Value(num, 26 * scale, disk);
            _write = Value(num, 26 * scale, text);
            _pct = new TextBlock { FontFamily = num, FontSize = 19 * scale, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
            _bar = new BarMeter { Height = 5, Fill = disk, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18 * scale, 0, 0, 4) };

            var used = new Grid();
            used.Children.Add(_pct);
            used.Children.Add(_bar);

            Grid.SetColumn(_read, 1);
            Grid.SetColumn(_write, 2);
            Grid.SetColumn(used, 3);
            Root.Children.Add(_name);
            Root.Children.Add(_read);
            Root.Children.Add(_write);
            Root.Children.Add(used);
        }

        private static TextBlock Value(FontFamily f, double size, Brush b) => new()
        {
            FontFamily = f, FontSize = size, FontWeight = FontWeights.SemiBold, FontStretch = FontStretches.SemiCondensed,
            Foreground = b, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };

        public void Update(DiskInfo d)
        {
            _name.Text = d.Name;
            _read.Text = Fmt.Bytes(d.ReadBps);
            _write.Text = Fmt.Bytes(d.WriteBps);
            _pct.Text = d.UsedPercent is { } u ? $"{u:0}%" : "";
            _bar.Value = (d.UsedPercent ?? 0) / 100;
        }
    }
}
