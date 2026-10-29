using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace MyDashboard.Controls;

/// <summary>横棒メーター。Value は 0〜1。</summary>
public sealed class BarMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BarMeter),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        double fw = Math.Clamp(Value, 0, 1) * w;
        if (fw > 0.5)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fw, h), h), r, r);
    }

    internal static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

/// <summary>
/// CPUスレッドごとの負荷を縦棒で並べる。しきい値を超えた棒は色が変わる。
/// P/E コアの区別が渡されたときは、E コアを淡い色にし、種類の境目に間を空けて下にラベルを付ける。
/// </summary>
public sealed class CoreBars : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private IReadOnlyList<bool>? _isECore;

    public Brush Normal { get; set; } = Brushes.White;
    public Brush ECore { get; set; } = Brushes.Gray;
    public Brush Label { get; set; } = Brushes.Gray;
    public Brush Warn { get; set; } = Brushes.Orange;
    public Brush Crit { get; set; } = Brushes.Red;
    public double WarnAt { get; set; } = 85;
    public double CritAt { get; set; } = 97;

    private static readonly Brush Track = BarMeter.Freeze(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));

    public void SetValues(IReadOnlyList<double> values, IReadOnlyList<bool>? isECore = null)
    {
        _values = values;
        _isECore = isECore != null && isECore.Count == values.Count ? isECore : null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int n = _values.Count;
        if (w <= 0 || h <= 0 || n == 0) return;

        if (_isECore != null && n <= 32) { RenderGrouped(dc, w, h, _isECore); return; }

        // スレッド数が多い（32超）ときは2段に分ける
        int rows = n > 32 ? 2 : 1;
        int perRow = (int)Math.Ceiling(n / (double)rows);
        double rowGap = 14;
        double rowH = (h - rowGap * (rows - 1)) / rows;
        double gap = Math.Max(2, Math.Min(8, w / perRow * 0.22));
        double bw = (w - gap * (perRow - 1)) / perRow;

        for (int i = 0; i < n; i++)
        {
            int row = i / perRow, col = i % perRow;
            double x = col * (bw + gap);
            double top = row * (rowH + rowGap);
            dc.DrawRectangle(Track, null, new Rect(x, top, bw, rowH));
            double v = Math.Clamp(_values[i], 0, 100);
            double bh = rowH * v / 100;
            if (bh < 1) continue;
            var brush = v >= CritAt ? Crit : v >= WarnAt ? Warn : Normal;
            dc.DrawRectangle(brush, null, new Rect(x, top + rowH - bh, bw, bh));
        }
    }

    /// <summary>1段で並べ、P/E が切り替わる所に間を空け、各まとまりの下に線とラベルを描く。</summary>
    private void RenderGrouped(DrawingContext dc, double w, double h, IReadOnlyList<bool> isE)
    {
        int n = _values.Count;
        int breaks = 0;
        for (int i = 1; i < n; i++) if (isE[i] != isE[i - 1]) breaks++;

        const double labelH = 30, groupGap = 22;
        double barH = h - labelH;
        if (barH <= 0) return;
        double gap = Math.Max(2, Math.Min(8, (w - groupGap * breaks) / n * 0.22));
        double bw = (w - groupGap * breaks - gap * (n - 1 - breaks)) / n;
        if (bw <= 0) return;

        var tf = new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var linePen = new Pen(Label, 1.5);

        double x = 0, runStart = 0;
        for (int i = 0; i < n; i++)
        {
            dc.DrawRectangle(Track, null, new Rect(x, 0, bw, barH));
            double v = Math.Clamp(_values[i], 0, 100);
            double bh = barH * v / 100;
            if (bh >= 1)
            {
                var brush = v >= CritAt ? Crit : v >= WarnAt ? Warn : isE[i] ? ECore : Normal;
                dc.DrawRectangle(brush, null, new Rect(x, barH - bh, bw, bh));
            }

            bool last = i == n - 1 || isE[i + 1] != isE[i];
            if (last)
            {
                double runEnd = x + bw;
                double ly = barH + 8;
                dc.DrawLine(linePen, new Point(runStart, ly), new Point(runEnd, ly));
                var text = new FormattedText(isE[i] ? "E-Core" : "P-Core", System.Globalization.CultureInfo.CurrentUICulture,
                                             FlowDirection.LeftToRight, tf, 16, Label, dpi);
                dc.DrawText(text, new Point((runStart + runEnd - text.Width) / 2, ly + 3));
                x = runEnd + groupGap;
                runStart = x;
            }
            else x += bw + gap;
        }
    }
}
