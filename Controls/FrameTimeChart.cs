using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MyDashboard.Controls;

/// <summary>
/// 直近のフレームタイム（1フレームごとの描画時間）を線グラフで描く。
/// 平均より大きく遅れたフレーム（カクつき）は琥珀色・赤の縦線で目立たせ、
/// 点線でモニターのリフレッシュレートの目安を示す。
/// </summary>
public sealed class FrameTimeChart : FrameworkElement
{
    private double[] _ms = Array.Empty<double>();
    private double _refMs;

    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(6, 255, 255, 255)));
    private static readonly Pen LinePen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(230, 0xF2, 0xF5, 0xFA))), 1.6) { LineJoin = PenLineJoin.Round });
    private static readonly Pen WarnPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(230, 0xFF, 0xB5, 0x47))), 2));
    private static readonly Pen CritPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(230, 0xFF, 0x5D, 0x5D))), 2));
    private static readonly Pen RefPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(180, 0x7F, 0x8D, 0xA3))), 1)
    { DashStyle = new DashStyle(new double[] { 5, 6 }, 0) });
    private static readonly Brush Fill = Freeze(new LinearGradientBrush(
        Color.FromArgb(56, 0xF2, 0xF5, 0xFA), Color.FromArgb(5, 0xF2, 0xF5, 0xFA), 90));

    /// <summary>フレームタイム（ms、古い順）と、目安の線の位置（ms）を渡して描き直す。</summary>
    public void SetData(IReadOnlyList<double>? frameTimes, double refMs)
    {
        _ms = frameTimes?.ToArray() ?? Array.Empty<double>();
        _refMs = refMs;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, w, h), 4, 4);
        if (_ms.Length < 2) return;

        double avg = _ms.Average();
        // 縦軸：平均の2.2倍か目安の線の1.3倍の大きい方を上端にする（それを超えるカクつきは上端で切る）
        double top = Math.Max(avg * 2.2, _refMs * 1.3);
        double Y(double v) => h - h * Math.Min(v, top) / top;

        // 1ピクセル半ごとにまとめる。カクつきが消えないよう、まとめた中の最大値を使う
        int buckets = Math.Max(2, Math.Min(_ms.Length, (int)(w / 1.5)));
        var max = new double[buckets];
        for (int i = 0; i < _ms.Length; i++)
        {
            int b = (int)((long)i * buckets / _ms.Length);
            if (_ms[i] > max[b]) max[b] = _ms[i];
        }

        if (_refMs > 0 && _refMs < top)
        {
            double ry = Math.Round(Y(_refMs)) + 0.5;
            dc.DrawLine(RefPen, new Point(0, ry), new Point(w, ry));
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            ac.BeginFigure(new Point(0, h), true, true);
            for (int b = 0; b < buckets; b++)
            {
                var p = new Point(w * b / (buckets - 1), Y(max[b]));
                if (b == 0) lc.BeginFigure(p, false, false); else lc.LineTo(p, true, false);
                ac.LineTo(p, false, false);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(Fill, null, area);

        for (int b = 0; b < buckets; b++)
        {
            if (max[b] <= avg * 1.6) continue;
            double x = Math.Round(w * b / (buckets - 1)) + 0.5;
            dc.DrawLine(max[b] > avg * 2.2 ? CritPen : WarnPen, new Point(x, h), new Point(x, Y(max[b])));
        }
        dc.DrawGeometry(null, LinePen, line);
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
