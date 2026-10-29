using System;
using System.Windows;
using System.Windows.Media;

namespace MyDashboard.Controls;

/// <summary>
/// 直近の推移を面グラフで描く軽量コントロール。最大2系列（例：受信／送信）。
/// パネル背景いっぱいに敷いて、数値の「過去」を見せる用途を想定。
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    private double?[] _a = new double?[90];
    private double?[] _b = new double?[90];
    private int _count;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Color), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Color Stroke { get => (Color)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public static readonly DependencyProperty SecondStrokeProperty = DependencyProperty.Register(
        nameof(SecondStroke), typeof(Color), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Colors.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Color SecondStroke { get => (Color)GetValue(SecondStrokeProperty); set => SetValue(SecondStrokeProperty, value); }

    /// <summary>縦軸の最大値。0 なら表示中データの最大値に合わせる。</summary>
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(HistoryChart),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }

    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
        nameof(FillOpacity), typeof(double), typeof(HistoryChart),
        new FrameworkPropertyMetadata(0.30, FrameworkPropertyMetadataOptions.AffectsRender));
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }

    public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
        nameof(LineThickness), typeof(double), typeof(HistoryChart),
        new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));
    public double LineThickness { get => (double)GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(HistoryChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public void SetCapacity(int points)
    {
        points = Math.Max(10, points);
        _a = new double?[points];
        _b = new double?[points];
        _count = 0;
        InvalidateVisual();
    }

    /// <summary>新しい値を右端に追加する（古い値は左に流れる）。</summary>
    public void Push(double? a, double? b = null)
    {
        int n = _a.Length;
        Array.Copy(_a, 1, _a, 0, n - 1);
        Array.Copy(_b, 1, _b, 0, n - 1);
        _a[n - 1] = a;
        _b[n - 1] = b;
        _count = Math.Min(n, _count + 1);
        InvalidateVisual();
    }

    /// <summary>自動スケール時に使う、現在の表示上の最大値。</summary>
    public double CurrentMax { get; private set; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double max = Max;
        if (max <= 0)
        {
            double m = 0;
            foreach (var v in _a) if (v > m) m = v.Value;
            foreach (var v in _b) if (v > m) m = v.Value;
            max = NiceCeiling(m);
        }
        CurrentMax = max;

        if (ShowGrid)
        {
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
            gridPen.Freeze();
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        DrawSeries(dc, _b, SecondStroke, max, w, h, FillOpacity * 0.6);
        DrawSeries(dc, _a, Stroke, max, w, h, FillOpacity);
    }

    private void DrawSeries(DrawingContext dc, double?[] data, Color color, double max, double w, double h, double fillOpacity)
    {
        int n = data.Length;
        double step = w / (n - 1);
        int first = Array.FindIndex(data, v => v.HasValue);
        if (first < 0 || first == n - 1) return;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            Point P(int i) => new(i * step, h - Math.Clamp((data[i] ?? 0) / max, 0, 1) * (h - 2) - 1);
            lc.BeginFigure(P(first), false, false);
            ac.BeginFigure(new Point(first * step, h), true, true);
            ac.LineTo(P(first), false, false);
            for (int i = first + 1; i < n; i++)
            {
                lc.LineTo(P(i), true, true);
                ac.LineTo(P(i), false, false);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb((byte)(255 * fillOpacity), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(color), LineThickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();

        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (v <= m * exp) return m * exp;
        return 10 * exp;
    }
}
