using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MyDashboard.Controls;

/// <summary>
/// 270度の円弧メーター。外側の太い弧が主の値（使用率・FPS）、内側の細い弧が副の値（温度）。
/// 値の変化は短いアニメーションで追従させ、計器の針のような動きにする。
/// 発光させるため、主の弧（Part=Value）と、目盛り・下地（Part=Track）を別の要素として重ねて使う。
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double StartAngle = 135;
    private const double Sweep = 270;

    public enum GaugePart { Track, Value }

    public GaugePart Part { get; set; } = GaugePart.Track;

    /// <summary>主の値（0〜1）。</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>副の値（0〜1）。負なら描かない。</summary>
    public static readonly DependencyProperty SecondValueProperty = DependencyProperty.Register(
        nameof(SecondValue), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double SecondValue { get => (double)GetValue(SecondValueProperty); set => SetValue(SecondValueProperty, value); }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ArcGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public static readonly DependencyProperty SecondStrokeProperty = DependencyProperty.Register(
        nameof(SecondStroke), typeof(Brush), typeof(ArcGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush SecondStroke { get => (Brush)GetValue(SecondStrokeProperty); set => SetValue(SecondStrokeProperty, value); }

    /// <summary>主の弧の太さ。</summary>
    public double Thickness { get; set; } = 14;

    /// <summary>目盛りの本数（大目盛りは5本ごと）。</summary>
    public int Ticks { get; set; } = 50;

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)));
    private static readonly Brush InnerTrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)));
    private static readonly Pen MajorTick = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(128, 220, 230, 245))), 2));
    private static readonly Pen MinorTick = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(52, 220, 230, 245))), 1.2));

    /// <summary>値をなめらかに動かして設定する。</summary>
    public void Animate(double value, double second = double.NaN)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(650);
        BeginAnimation(ValueProperty, Limit(new DoubleAnimation(Math.Clamp(value, 0, 1), d) { EasingFunction = ease }));
        if (!double.IsNaN(second))
            BeginAnimation(SecondValueProperty, Limit(new DoubleAnimation(Math.Clamp(second, 0, 1), d) { EasingFunction = ease }));
    }

    /// <summary>1秒20コマに抑える（制限しないとモニターのリフレッシュレートで発光のぼかしごと描き直し、GPU を食う）。</summary>
    private static DoubleAnimation Limit(DoubleAnimation a)
    {
        Timeline.SetDesiredFrameRate(a, 20);
        return a;
    }

    /// <summary>主の弧の半径（目盛りのぶん内側に入れる）。</summary>
    private double Radius => Math.Min(ActualWidth, ActualHeight) / 2 - 30;

    protected override void OnRender(DrawingContext dc)
    {
        double r = Radius;
        if (r <= 10) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        if (Part == GaugePart.Track)
        {
            DrawArc(dc, c, r, 1, new Pen(TrackBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

            for (int i = 0; i <= Ticks; i++)
            {
                double a = (StartAngle + Sweep * i / Ticks) * Math.PI / 180;
                bool major = i % 5 == 0;
                double ro = r + 22, ri = ro - (major ? 12 : 5);
                dc.DrawLine(major ? MajorTick : MinorTick,
                    new Point(c.X + ri * Math.Cos(a), c.Y + ri * Math.Sin(a)),
                    new Point(c.X + ro * Math.Cos(a), c.Y + ro * Math.Sin(a)));
            }

            if (SecondValue >= 0)
            {
                double r2 = r - 30;
                DrawArc(dc, c, r2, 1, new Pen(InnerTrackBrush, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                DrawArc(dc, c, r2, SecondValue, new Pen(SecondStroke, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            }
        }
        else
        {
            DrawArc(dc, c, r, Value, new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        }
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double frac, Pen pen)
    {
        frac = Math.Clamp(frac, 0, 1);
        if (frac < 0.002) return;
        double a0 = StartAngle * Math.PI / 180;
        double a1 = (StartAngle + Sweep * frac) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, Sweep * frac > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
