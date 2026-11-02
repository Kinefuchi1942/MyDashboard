using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyDashboard.Controls;

/// <summary>
/// 画面の右端に縦に並ぶレイアウト切替（標準・計器盤・オーロラ）。オーロラのテーマ切替と同じ形。
/// いまのレイアウトを枠で囲み、ほかは薄くする。クリックで Picked が呼ばれる。
/// </summary>
public sealed class LayoutPicker : Border
{
    private static readonly (string Name, string Tip, string Icon)[] Items =
    {
        ("classic", "標準", "M5,5 L23,5 L23,23 L5,23 Z M14,5 L14,23 M5,14 L23,14"),
        ("gauge", "計器盤", "M5,20 A9,9 0 1 1 23,20 M14,20 L19,12"),
        ("aurora", "オーロラ", "M4,17 C8,8 11,18 14,12 C17,6 20,7 24,9 M4,22 L24,22"),
    };

    private readonly Border[] _buttons;
    private readonly Path[] _icons;
    private string _current = "";

    /// <summary>ボタンが押されたときに、そのレイアウトの名前（classic / gauge / aurora）で呼ばれる。</summary>
    public event Action<string>? Picked;

    public LayoutPicker()
    {
        Width = 68;
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(6, 4, 6, 4);
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1D, 0x26));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x40));

        var stack = new StackPanel();
        _buttons = new Border[Items.Length];
        _icons = new Path[Items.Length];
        for (int i = 0; i < Items.Length; i++)
        {
            var (name, tip, icon) = Items[i];
            var path = new Path
            {
                Width = 28, Height = 28, Stretch = Stretch.None, StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)),
                Data = Geometry.Parse(icon),
            };
            var button = new Border
            {
                Tag = name, ToolTip = tip, Width = 54, Height = 54, CornerRadius = new CornerRadius(14),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xD9, 0xE8, 0xEC, 0xF2)),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = path,
            };
            button.MouseLeftButtonDown += (_, e) => e.Handled = true;
            button.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                var picked = (string)((Border)s).Tag;
                // レイアウトの差し替えはマウスの処理が終わってから
                if (picked != _current) Dispatcher.BeginInvoke(() => Picked?.Invoke(picked));
            };
            button.MouseEnter += (_, _) => Refresh();
            button.MouseLeave += (_, _) => Refresh();
            _buttons[i] = button;
            _icons[i] = path;
            stack.Children.Add(button);
        }
        Child = stack;
        Refresh();
    }

    /// <summary>いま表示しているレイアウトの名前。</summary>
    public string Current
    {
        get => _current;
        set { _current = value; Refresh(); }
    }

    /// <summary>
    /// オーロラの時間帯テーマに色を合わせる（リソースの差し替えについていくよう、コードから参照を張る）。
    /// </summary>
    public void UseAuroraColors()
    {
        SetResourceReference(BackgroundProperty, "AGlass");
        SetResourceReference(BorderBrushProperty, "ALine");
        foreach (var icon in _icons) icon.SetResourceReference(Shape.StrokeProperty, "AText");
        foreach (var button in _buttons) button.SetResourceReference(BorderBrushProperty, "AT85");
        Refresh();
    }

    private void Refresh()
    {
        foreach (var button in _buttons)
        {
            // 枠の色はテーマについていく参照のまま残し、太さで出し入れする（中身がずれないよう余白で埋める）
            bool on = (string)button.Tag == _current;
            button.BorderThickness = new Thickness(on ? 2 : 0);
            button.Padding = new Thickness(on ? 0 : 2);
            button.Opacity = on ? 1 : button.IsMouseOver ? 0.85 : 0.55;
        }
    }
}
