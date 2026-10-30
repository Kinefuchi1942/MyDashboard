using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyDashboard.Monitoring;

namespace MyDashboard.Views;

/// <summary>消費電力（推定）の小さな表示。標準・計器盤で共通に使う。</summary>
public partial class PowerSummary : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public PowerSummary() => InitializeComponent();

    /// <summary>右側の区切り線の色（レイアウトの線の色に合わせる）。</summary>
    public Brush RuleBrush
    {
        get => Rule.BorderBrush;
        set => Rule.BorderBrush = value;
    }

    public void Show(PowerInfo? p)
    {
        if (p == null) return;
        TotalText.Text = Math.Round(p.TotalW).ToString("0", Inv);

        // 内訳の帯：合計に対する割合。モニターを含めないときはその分は 0
        double[] parts = { p.CpuW, p.GpuW, p.OtherW, p.MonitorW };
        double sum = Math.Max(1, p.CpuW + p.GpuW + p.OtherW + p.MonitorW);
        for (int i = 0; i < parts.Length; i++)
            Bar.ColumnDefinitions[i].Width = new GridLength(Math.Max(0, parts[i]) / sum, GridUnitType.Star);
        Bar.ColumnDefinitions[4].Width = new GridLength(0, GridUnitType.Star);

        TodayKwh.Text = Kwh(p.TodayKwh);
        TodayYen.Text = Yen(p.TodayKwh, p.YenPerKwh);
        MonthKwh.Text = Kwh(p.MonthKwh);
        MonthYen.Text = Yen(p.MonthKwh, p.YenPerKwh);
    }

    private static string Kwh(double kwh) => kwh.ToString(kwh < 10 ? "0.00" : kwh < 100 ? "0.0" : "0", Inv) + " kWh";

    private static string Yen(double kwh, double yenPerKwh) =>
        yenPerKwh > 0 ? " · 約" + Math.Round(kwh * yenPerKwh).ToString("#,0", Inv) + "円" : "";
}
