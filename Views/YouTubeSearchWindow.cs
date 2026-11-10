using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Shapes;

namespace MyDashboard.Views;

/// <summary>
/// YouTube の検索の入力欄（オーロラのカードの 🔍 を押すと、その下に出る小さな窓）。
/// ダッシュボード本体はフォーカスを取らない（常に背後モードではキーも届かない）ので、文字を打つ所だけ別の窓にする。
/// 別の窓なので映像（WebView2）の上にも重ねられる。Enter で検索、Esc かほかの所を押すと閉じる。
/// </summary>
public sealed class YouTubeSearchWindow : Window
{
    private static readonly Brush Back = Make(Color.FromRgb(0x1A, 0x1F, 0x2E));
    private static readonly Brush Edge = Make(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Red = Make(Color.FromRgb(0xE5, 0x32, 0x2D));
    private static readonly Brush Text = Make(Color.FromRgb(0xF4, 0xF7, 0xFB));
    private static readonly Brush T75 = Make(Color.FromArgb(0xBF, 0xF4, 0xF7, 0xFB));
    private static readonly Brush T50 = Make(Color.FromArgb(0x80, 0xF4, 0xF7, 0xFB));
    private static readonly Brush Line15 = Make(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Hover = Make(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BoxBack = Make(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));

    private readonly TextBox _box;
    private readonly Action<string> _search;
    private bool _closing;

    /// <param name="history">最近の検索（新しい順）。押すとその言葉で検索する。</param>
    /// <param name="scale">ダッシュボードの拡大率（Viewbox）。窓もカードと同じ大きさに見えるようにする。</param>
    /// <param name="search">検索する言葉を受け取る（閉じたあとに呼ぶ）。</param>
    public YouTubeSearchWindow(string current, IReadOnlyList<string> history, double scale, Action<string> search)
    {
        _search = search;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "YouTube を検索";

        var ui = (FontFamily)Application.Current.FindResource("UiFont");

        _box = new TextBox
        {
            Text = current,
            FontFamily = ui, FontSize = 22, Foreground = Text, CaretBrush = Text, SelectionBrush = Red,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0),
        };
        var hint = new TextBlock { Text = "Enter で検索", FontFamily = ui, FontSize = 15, Foreground = T50, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { LastChildFill = true };
        var icon = new Canvas { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new Ellipse { Width = 13, Height = 13, Stroke = T75, StrokeThickness = 2, Margin = new Thickness(1, 1, 0, 0) });
        icon.Children.Add(new Line { X1 = 12, Y1 = 12, X2 = 19, Y2 = 19, Stroke = T75, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(hint, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(hint);
        row.Children.Add(_box);
        var box = new Border
        {
            Height = 52, Padding = new Thickness(16, 0, 16, 0), CornerRadius = new CornerRadius(26),
            BorderThickness = new Thickness(2), BorderBrush = Red, Background = BoxBack, Child = row,
        };

        var panel = new StackPanel();
        panel.Children.Add(box);
        if (history.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "最近の検索", FontFamily = ui, FontSize = 15, Foreground = T50, Margin = new Thickness(2, 14, 0, 0) });
            var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var q in history)
            {
                var chip = new Border
                {
                    Margin = new Thickness(0, 6, 8, 0), Padding = new Thickness(14, 4, 14, 6), CornerRadius = new CornerRadius(16),
                    BorderThickness = new Thickness(1), BorderBrush = Line15, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = q, FontFamily = ui, FontSize = 16, Foreground = T75, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis },
                };
                chip.MouseEnter += (_, _) => chip.Background = Hover;
                chip.MouseLeave += (_, _) => chip.Background = Brushes.Transparent;
                chip.MouseLeftButtonUp += (_, _) => Finish(q);
                wrap.Children.Add(chip);
            }
            panel.Children.Add(wrap);
        }

        Content = new Border
        {
            Width = 520, Padding = new Thickness(18), CornerRadius = new CornerRadius(18),
            Background = Back, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = panel,
            LayoutTransform = new ScaleTransform(scale, scale),
        };

        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(_box.Text); }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(null); }
        };
        // ほかの所（ダッシュボードやほかのアプリ）を押したら閉じる
        Deactivated += (_, _) => Finish(null);
        Loaded += (_, _) =>
        {
            Activate();
            _box.Focus();
            Keyboard.Focus(_box);
            _box.SelectAll();
        };
    }

    /// <summary>窓の右端を right、上端を top（どちらも画面のピクセル）に合わせる。モニターの外にははみ出さない。</summary>
    public void PlaceRightAligned(double right, double top)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int x = (int)Math.Round(right) - w, y = (int)Math.Round(top);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)right - 1, (int)top));
        var a = screen.WorkingArea;
        x = Math.Clamp(x, a.Left, Math.Max(a.Left, a.Right - w));
        y = Math.Clamp(y, a.Top, Math.Max(a.Top, a.Bottom - h));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void Finish(string? query)
    {
        if (_closing) return;
        _closing = true;
        Close();
        query = query?.Trim();
        if (!string.IsNullOrEmpty(query)) _search(query);
    }

    private static Brush Make(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
