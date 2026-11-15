using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MyDashboard.Controls;

/// <summary>
/// 直近のフレームタイムから、0.25 秒ごとの FPS の推移を線グラフで描く（高いほど良い）。
/// 左に FPS の目盛り（60 / 120 など）、点線でモニターのリフレッシュレートを示し、
/// 大きく遅れたフレーム（カクつき）は下端に赤い点で示す。
/// （2026-11-15 に1フレームごとの描画から変更。モックアップ mockups/frametime.png の案 A）
/// </summary>
public sealed class FrameTimeChart : FrameworkElement
{
    private const double LabelWidth = 38;   // 左の目盛りの文字の幅
    private const double BinMs = 250;       // FPS を求める区切り

    private double[] _ms = Array.Empty<double>();
    private double _refreshHz;

    /// <summary>直近の表示でカクつきとして数えたフレームの数。</summary>
    public int StutterCount { get; private set; }

    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromArgb(6, 255, 255, 255)));
    private static readonly Pen LinePen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(235, 0xF2, 0xF5, 0xFA))), 2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen GridPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255))), 1));
    private static readonly Pen RefPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255))), 1)
    { DashStyle = new DashStyle(new double[] { 5, 6 }, 0) });
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x65, 0x78)));
    private static readonly Brush DotBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5D, 0x5D)));
    private static readonly Brush Fill = Freeze(new LinearGradientBrush(
        Color.FromArgb(56, 0xF2, 0xF5, 0xFA), Color.FromArgb(5, 0xF2, 0xF5, 0xFA), 90));
    private static readonly Typeface LabelFace = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>フレームタイム（ms、古い順）とモニターのリフレッシュレート（Hz）を渡して描き直す。</summary>
    public void SetData(IReadOnlyList<double>? frameTimes, double refreshHz)
    {
        _ms = frameTimes?.Where(v => v > 0).ToArray() ?? Array.Empty<double>();
        _refreshHz = refreshHz;
        StutterCount = CountStutters(_ms, out _);
        InvalidateVisual();
    }

    /// <summary>カクつき＝33ms（30fps）を超え、かつ普段（中央値）の2.5倍を超えて遅れたフレーム。</summary>
    private static int CountStutters(double[] ms, out bool[] flags)
    {
        flags = new bool[ms.Length];
        if (ms.Length < 2) return 0;
        var sorted = (double[])ms.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        int n = 0;
        for (int i = 0; i < ms.Length; i++)
            if (ms[i] > 33.3 && ms[i] > median * 2.5) { flags[i] = true; n++; }
        return n;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < LabelWidth + 10 || h < 10) return;
        double x0 = LabelWidth, pw = w - x0;
        dc.DrawRoundedRectangle(Back, null, new Rect(x0, 0, pw, h), 4, 4);

        double hz = _refreshHz > 0 ? _refreshHz : 60;

        // 0.25 秒ごとの FPS（区切りの終わりの時刻と一緒に持つ）
        var times = new List<double>();
        var fps = new List<double>();
        double total = 0, sum = 0; int cnt = 0;
        foreach (var v in _ms)
        {
            total += v; sum += v; cnt++;
            if (sum >= BinMs) { times.Add(total); fps.Add(1000.0 * cnt / sum); sum = 0; cnt = 0; }
        }
        if (cnt > 0 && sum >= BinMs * 0.4) { times.Add(total); fps.Add(1000.0 * cnt / sum); }

        // 縦軸：リフレッシュレートの1.12倍か、最高値の1.05倍の大きい方を上端にする
        double top = Math.Max(hz * 1.12, (fps.Count > 0 ? fps.Max() : 0) * 1.05);
        double Y(double v) => h - h * Math.Clamp(v, 0, top) / top;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(double v, double y)
        {
            var ft = new FormattedText(v.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, 14, LabelBrush, ppd);
            dc.DrawText(ft, new Point(x0 - 7 - ft.Width, Math.Clamp(y - ft.Height / 2, -2, h - ft.Height + 2)));
        }

        // 目盛り：60・120・240 のうちリフレッシュレートより十分低いもの＋リフレッシュレート（点線）
        foreach (var g in new double[] { 60, 120, 240 })
        {
            if (g > hz * 0.88) continue;
            double gy = Math.Round(Y(g)) + 0.5;
            dc.DrawLine(GridPen, new Point(x0, gy), new Point(w, gy));
            if (gy - Y(hz) >= 17) Label(g, gy);   // リフレッシュレートの数字と重なるときは数字を省く
        }
        double ry = Math.Round(Y(hz)) + 0.5;
        dc.DrawLine(RefPen, new Point(x0, ry), new Point(w, ry));
        Label(hz, ry);

        if (fps.Count < 2 || total <= 0) return;
        double X(double t) => x0 + pw * t / total;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            double first = X(times[0]);
            ac.BeginFigure(new Point(first, h), true, true);
            for (int i = 0; i < fps.Count; i++)
            {
                var p = new Point(X(times[i]), Y(fps[i]));
                if (i == 0) lc.BeginFigure(p, false, false); else lc.LineTo(p, true, false);
                ac.LineTo(p, false, false);
            }
            ac.LineTo(new Point(X(times[^1]), h), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(Fill, null, area);
        dc.DrawGeometry(null, LinePen, line);

        // カクつきの赤い点（近いものは1つにまとめる）
        CountStutters(_ms, out var flags);
        double t2 = 0, lastX = double.NegativeInfinity;
        for (int i = 0; i < _ms.Length; i++)
        {
            t2 += _ms[i];
            if (!flags[i]) continue;
            double x = X(t2);
            if (x - lastX < 9) continue;
            dc.DrawEllipse(DotBrush, null, new Point(Math.Clamp(x, x0 + 4, w - 4), h - 7), 4, 4);
            lastX = x;
        }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
