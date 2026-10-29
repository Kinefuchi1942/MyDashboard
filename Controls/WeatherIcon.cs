using System;
using System.Windows;
using System.Windows.Media;
using MyDashboard.Info;

namespace MyDashboard.Controls;

/// <summary>天気コードに応じた簡素なアイコン（晴れ・月・雲・雨・雪・雷・霧）を自前で描く。</summary>
public sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(int?), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public int? Code { get => (int?)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(
        nameof(IsDay), typeof(bool), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsDay { get => (bool)GetValue(IsDayProperty); set => SetValue(IsDayProperty, value); }

    /// <summary>明るい背景の上に描くとき true。白っぽい雲や月が背景に溶けないよう、濃い色で描く。</summary>
    public static readonly DependencyProperty OnLightProperty = DependencyProperty.Register(
        nameof(OnLight), typeof(bool), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool OnLight { get => (bool)GetValue(OnLightProperty); set => SetValue(OnLightProperty, value); }

    private sealed record Palette(Brush Sun, Brush Moon, Brush Cloud, Brush DarkCloud, Brush Rain, Brush Snow);

    private static readonly Palette DarkBg = new(
        Frozen(Color.FromRgb(0xFF, 0xD2, 0x7A)), Frozen(Color.FromRgb(0xE9, 0xE4, 0xC9)),
        Frozen(Color.FromRgb(0xE3, 0xE8, 0xF0)), Frozen(Color.FromRgb(0xB4, 0xBD, 0xCB)),
        Frozen(Color.FromRgb(0x7F, 0xB2, 0xF0)), Frozen(Color.FromRgb(0xF4, 0xF7, 0xFB)));

    private static readonly Palette LightBg = new(
        Frozen(Color.FromRgb(0xF5, 0xA1, 0x0F)), Frozen(Color.FromRgb(0x6B, 0x66, 0xD2)),
        Frozen(Color.FromRgb(0x9D, 0xB0, 0xC8)), Frozen(Color.FromRgb(0x7E, 0x8D, 0xA6)),
        Frozen(Color.FromRgb(0x1F, 0x6F, 0xC0)), Frozen(Color.FromRgb(0x8F, 0xA3, 0xBF)));

    private Brush SunBrush => (OnLight ? LightBg : DarkBg).Sun;
    private Brush MoonBrush => (OnLight ? LightBg : DarkBg).Moon;
    private Brush CloudBrush => (OnLight ? LightBg : DarkBg).Cloud;
    private Brush DarkCloudBrush => (OnLight ? LightBg : DarkBg).DarkCloud;
    private Brush RainBrush => (OnLight ? LightBg : DarkBg).Rain;
    private Brush SnowBrush => (OnLight ? LightBg : DarkBg).Snow;

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 2 || Code is null) return;
        double ox = (ActualWidth - s) / 2, oy = (ActualHeight - s) / 2;
        dc.PushTransform(new TranslateTransform(ox, oy));

        switch (WeatherCodes.KindOf(Code))
        {
            case WeatherCodes.Kind.Clear:
                if (IsDay) Sun(dc, s / 2, s / 2, s * 0.2, s); else Moon(dc, s / 2, s / 2, s * 0.3);
                break;
            case WeatherCodes.Kind.PartlyCloudy:
                if (IsDay) Sun(dc, s * 0.64, s * 0.36, s * 0.15, s * 0.75); else Moon(dc, s * 0.64, s * 0.36, s * 0.2);
                Cloud(dc, s, CloudBrush, 0.06);
                break;
            case WeatherCodes.Kind.Cloudy:
                Cloud(dc, s, CloudBrush, 0);
                break;
            case WeatherCodes.Kind.Fog:
                var fogPen = new Pen(CloudBrush, s * 0.07) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < 3; i++)
                {
                    double y = s * (0.35 + i * 0.16), inset = i == 1 ? 0.12 : 0.2;
                    dc.DrawLine(fogPen, new Point(s * inset, y), new Point(s * (1 - inset), y));
                }
                break;
            case WeatherCodes.Kind.Rain:
                Cloud(dc, s, DarkCloudBrush, -0.08);
                var rainPen = new Pen(RainBrush, s * 0.055) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                foreach (var x in new[] { 0.36, 0.52, 0.68 })
                    dc.DrawLine(rainPen, new Point(s * x, s * 0.72), new Point(s * (x - 0.05), s * 0.88));
                break;
            case WeatherCodes.Kind.Snow:
                Cloud(dc, s, DarkCloudBrush, -0.08);
                foreach (var (x, y) in new[] { (0.36, 0.76), (0.52, 0.86), (0.68, 0.76) })
                    dc.DrawEllipse(SnowBrush, null, new Point(s * x, s * y), s * 0.045, s * 0.045);
                break;
            case WeatherCodes.Kind.Thunder:
                Cloud(dc, s, DarkCloudBrush, -0.08);
                var bolt = new StreamGeometry();
                using (var g = bolt.Open())
                {
                    g.BeginFigure(new Point(s * 0.55, s * 0.62), true, true);
                    g.LineTo(new Point(s * 0.42, s * 0.8), true, false);
                    g.LineTo(new Point(s * 0.51, s * 0.8), true, false);
                    g.LineTo(new Point(s * 0.45, s * 0.95), true, false);
                    g.LineTo(new Point(s * 0.62, s * 0.74), true, false);
                    g.LineTo(new Point(s * 0.53, s * 0.74), true, false);
                }
                bolt.Freeze();
                dc.DrawGeometry(SunBrush, null, bolt);
                break;
        }
        dc.Pop();
    }

    private void Sun(DrawingContext dc, double cx, double cy, double r, double s)
    {
        dc.DrawEllipse(SunBrush, null, new Point(cx, cy), r, r);
        var pen = new Pen(SunBrush, s * 0.05) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            dc.DrawLine(pen, new Point(cx + Math.Cos(a) * r * 1.5, cy + Math.Sin(a) * r * 1.5),
                             new Point(cx + Math.Cos(a) * r * 2.0, cy + Math.Sin(a) * r * 2.0));
        }
    }

    private void Moon(DrawingContext dc, double cx, double cy, double r)
    {
        var full = new EllipseGeometry(new Point(cx, cy), r, r);
        var cut = new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.85, r * 0.85);
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, full, cut);
        crescent.Freeze();
        dc.DrawGeometry(MoonBrush, null, crescent);
    }

    /// <summary>雲（円3つと角丸の帯の合成）。dy で上下にずらす（雨・雪のときは少し上に）。</summary>
    private static void Cloud(DrawingContext dc, double s, Brush brush, double dy)
    {
        double o = s * dy;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(s * 0.38, s * 0.58 + o), s * 0.17, s * 0.17));
        g.Children.Add(new EllipseGeometry(new Point(s * 0.55, s * 0.5 + o), s * 0.2, s * 0.2));
        g.Children.Add(new EllipseGeometry(new Point(s * 0.7, s * 0.62 + o), s * 0.14, s * 0.14));
        g.Children.Add(new RectangleGeometry(new Rect(s * 0.22, s * 0.6 + o, s * 0.58, s * 0.16), s * 0.08, s * 0.08));
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
