using System;
using System.Windows.Media;

namespace MyDashboard.Views;

/// <summary>背景の光1つ（色と強さ）。</summary>
public readonly record struct SkyGlow(Color Color, double Opacity);

/// <summary>
/// オーロラの配色1組。
/// 背景（空・光・暗さ）は時間帯の境目でゆっくり混ぜ、文字まわり（Text 以降の色）は境目の中間で切り替える。
/// Vignette＝画面の縁の暗さ、LoadGlow＝負荷に連動する光の強さの倍率、Dim＝画面全体を暗くする量、
/// Sun＝明るいテーマの度合い（settings.json の auroraDayBrightness を効かせる割合）。
/// </summary>
public sealed record AuroraPalette(
    string Name,
    bool Light,
    Color SkyTop, Color SkyMid, Color SkyBottom,
    SkyGlow TopLeft, SkyGlow TopRight, SkyGlow BottomLeft, SkyGlow BottomRight,
    double Vignette,
    double LoadGlow,
    double Dim,
    double Sun,
    Color Glass, Color Line,
    Color Text, Color Accent, Color CpuValue, Color GpuValue, Color Rain);

/// <summary>
/// 時間帯（朝・昼・夕方・夜）の配色と、いまの時刻に合う配色の計算。
/// 朝＝日の出〜10時、昼＝10時〜日の入り1時間前、夕方＝日の入りの前後1時間、夜＝それ以外。
/// 境目の前後15分（計30分）で次の配色へ移る。
/// </summary>
public static class AuroraTheme
{
    private static Color C(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    private static Color Rgb(uint rgb) => C(0xFF000000 | rgb);
    private static SkyGlow G(uint rgb, double opacity) => new(Rgb(rgb), opacity);
    private static readonly SkyGlow None = new(Color.FromArgb(0, 0, 0, 0), 0);

    // 明るいテーマでは CPU・GPU の色を同じ色味の濃い版にして、白っぽい背景でも読めるようにする
    private static readonly Color CpuDark = Rgb(0x7FE3F0), GpuDark = Rgb(0xB7A6FF), RainDark = Rgb(0x7FB2F0);
    private static readonly Color CpuLight = Rgb(0x0B8494), GpuLight = Rgb(0x5A52D0), RainLight = Rgb(0x1F6FC0);

    /// <summary>これまでのオーロラ（時間帯で変えない）。</summary>
    public static readonly AuroraPalette Classic = new("classic", false,
        Rgb(0x070A12), Rgb(0x070A12), Rgb(0x070A12),
        None, None, None, None,
        Vignette: 1, LoadGlow: 1, Dim: 0, Sun: 0,
        Glass: C(0x12FFFFFF), Line: C(0x1FFFFFFF),
        Text: Rgb(0xF4F7FB), Accent: Rgb(0x7FE3F0), CpuValue: CpuDark, GpuValue: GpuDark, Rain: RainDark);

    /// <summary>朝：淡い水色から朝焼けの橙へ。濃い文字。</summary>
    public static readonly AuroraPalette Morning = new("morning", true,
        Rgb(0xC9DDF2), Rgb(0xEBDCE0), Rgb(0xFAD9BE),
        TopLeft: G(0xF4A9C0, 0.30), TopRight: G(0x9CCBF2, 0.55), BottomLeft: G(0xFFB774, 0.85), BottomRight: G(0xFFE1A8, 0.55),
        Vignette: 0, LoadGlow: 0.45, Dim: 0, Sun: 1,
        Glass: C(0x80FFFFFF), Line: C(0x1F3C3250),
        Text: Rgb(0x2B2A3F), Accent: Rgb(0xE2793A), CpuValue: CpuLight, GpuValue: GpuLight, Rain: RainLight);

    /// <summary>昼：晴れた空の明るい青。左上に太陽の光。濃い文字。</summary>
    public static readonly AuroraPalette Day = new("day", true,
        Rgb(0x6FB8EE), Rgb(0xA8D8F6), Rgb(0xE4F3FC),
        TopLeft: G(0xFFFFFF, 0.85), TopRight: G(0x4E9EEA, 0.45), BottomLeft: G(0x7FE0D8, 0.45), BottomRight: G(0xFFF3C4, 0.30),
        Vignette: 0, LoadGlow: 0.45, Dim: 0, Sun: 1,
        Glass: C(0x94FFFFFF), Line: C(0x1F14325A),
        Text: Rgb(0x0F2238), Accent: Rgb(0x1786C8), CpuValue: CpuLight, GpuValue: GpuLight, Rain: RainLight);

    /// <summary>夕方：紫の空に橙と赤紫の光。</summary>
    public static readonly AuroraPalette Evening = new("evening", false,
        Rgb(0x2A1A44), Rgb(0x3A1D3E), Rgb(0x4A2230),
        TopLeft: None, TopRight: G(0x6A4BD8, 0.45), BottomLeft: G(0xC2468E, 0.45), BottomRight: G(0xF07A3A, 0.55),
        Vignette: 1, LoadGlow: 0.8, Dim: 0, Sun: 0,
        Glass: C(0x14FFFFFF), Line: C(0x24FFFFFF),
        Text: Rgb(0xFFF1E8), Accent: Rgb(0xFFB38A), CpuValue: CpuDark, GpuValue: GpuDark, Rain: RainDark);

    /// <summary>夜：ほぼ黒にごく弱い藍色。文字も少し暗く。</summary>
    public static readonly AuroraPalette Night = new("night", false,
        Rgb(0x06080F), Rgb(0x070912), Rgb(0x05060B),
        TopLeft: None, TopRight: G(0x2B3480, 0.38), BottomLeft: G(0x173C5A, 0.30), BottomRight: None,
        Vignette: 1, LoadGlow: 0.8, Dim: 0.12, Sun: 0,
        Glass: C(0x0DFFFFFF), Line: C(0x17FFFFFF),
        Text: Rgb(0xC9CFDB), Accent: Rgb(0x8E9CFF), CpuValue: CpuDark, GpuValue: GpuDark, Rain: RainDark);

    private static readonly TimeSpan HalfFade = TimeSpan.FromMinutes(15);

    /// <summary>settings.json の auroraTheme で固定された配色。"auto" や不明な値なら null。</summary>
    public static AuroraPalette? Fixed(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "classic" => Classic,
        "morning" => Morning,
        "day" => Day,
        "evening" => Evening,
        "night" => Night,
        _ => null,
    };

    /// <summary>いまの時刻の配色。日の出・日の入りが分からないときは 6:00 / 17:30 とみなす。</summary>
    public static AuroraPalette At(DateTime now, DateTime? sunrise, DateTime? sunset)
    {
        var day = now.Date;
        // 取得した日の出・日の入りは「時刻」だけ使う（日付が変わっても前日の値で十分近い）
        var rise = day + (sunrise?.TimeOfDay ?? new TimeSpan(6, 0, 0));
        var set = day + (sunset?.TimeOfDay ?? new TimeSpan(17, 30, 0));

        // 境目：日の出 → 10時 → 日の入り1時間前 → 日の入り1時間後。順番が崩れないように間隔を確保する
        var b1 = rise;
        var b2 = Max(day.AddHours(10), b1.AddHours(1));
        var b3 = Max(set.AddHours(-1), b2.AddHours(1));
        var b4 = Max(set.AddHours(1), b3.AddHours(1));

        var bounds = new[] { (b1, Night, Morning), (b2, Morning, Day), (b3, Day, Evening), (b4, Evening, Night) };
        foreach (var (b, from, to) in bounds)
        {
            if (now >= b - HalfFade && now < b + HalfFade)
                return Blend(from, to, (now - (b - HalfFade)).TotalSeconds / (2 * HalfFade.TotalSeconds));
        }
        if (now < b1) return Night;
        if (now < b2) return Morning;
        if (now < b3) return Day;
        if (now < b4) return Evening;
        return Night;
    }

    /// <summary>a から b へ t（0〜1）だけ進めた配色。文字まわりは t=0.5 で切り替える。</summary>
    public static AuroraPalette Blend(AuroraPalette a, AuroraPalette b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        var fg = t < 0.5 ? a : b;
        return fg with
        {
            SkyTop = Mix(a.SkyTop, b.SkyTop, t),
            SkyMid = Mix(a.SkyMid, b.SkyMid, t),
            SkyBottom = Mix(a.SkyBottom, b.SkyBottom, t),
            TopLeft = Mix(a.TopLeft, b.TopLeft, t),
            TopRight = Mix(a.TopRight, b.TopRight, t),
            BottomLeft = Mix(a.BottomLeft, b.BottomLeft, t),
            BottomRight = Mix(a.BottomRight, b.BottomRight, t),
            Vignette = Lerp(a.Vignette, b.Vignette, t),
            LoadGlow = Lerp(a.LoadGlow, b.LoadGlow, t),
            Dim = Lerp(a.Dim, b.Dim, t),
            Sun = Lerp(a.Sun, b.Sun, t),
            Glass = Mix(a.Glass, b.Glass, t),
            Line = Mix(a.Line, b.Line, t),
        };
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb(Lerp(a.A, b.A, t), Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    /// <summary>光は色が無い側（強さ0）からは色を借りて、強さだけを変える。</summary>
    private static SkyGlow Mix(SkyGlow a, SkyGlow b, double t)
    {
        var ca = a.Opacity > 0 ? a.Color : b.Color;
        var cb = b.Opacity > 0 ? b.Color : a.Color;
        return new SkyGlow(Mix(ca, cb, t), Lerp(a.Opacity, b.Opacity, t));
    }

    /// <summary>同じ色で透明度だけを変える。</summary>
    public static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}
