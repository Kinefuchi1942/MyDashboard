using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDashboard.Info;

namespace MyDashboard.Controls;

/// <summary>
/// 画面の右端（レイアウト切替の下）のスピーカーのボタン。押すと左に音量ミキサーのパネルを出す（サイドバーが左端なら右に出す）（モックアップ mockups/mixer-pop-*.png）。
/// ・パネルは Popup（別のウィンドウ）なので、オーロラの Twitch・YouTube の映像（WebView2）の上にも出せる
/// ・ほかの場所をクリックするか、10秒さわらないと閉じる。開いている間だけ音声を読む（1秒20回）
/// </summary>
public sealed class MixerButton : Border
{
    private const string SpeakerIcon = "M6,11 L6,17 L10,17 L15,21 L15,7 L10,11 Z M18.5,11 A4,4 0 0 1 18.5,17 M21,8.5 A7.5,7.5 0 0 1 21,19.5";
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(10);

    private readonly Border _button;
    private readonly Path _icon;
    private readonly Popup _popup;
    private readonly MixerPanel _panel;
    private readonly DispatcherTimer _timer;
    private DateTime _lastTouch;
    private Window? _window;

    public MixerButton()
    {
        Width = 68;
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(6, 4, 6, 4);
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1D, 0x26));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x40));

        _icon = new Path
        {
            Width = 28, Height = 28, Stretch = Stretch.None, StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)),
            Data = Geometry.Parse(SpeakerIcon),
        };
        _button = new Border
        {
            ToolTip = "音量ミキサー", Width = 54, Height = 54, CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD9, 0xE8, 0xEC, 0xF2)),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = _icon,
        };
        _button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _button.MouseLeftButtonUp += (_, e) => { e.Handled = true; Toggle(); };
        _button.MouseEnter += (_, _) => Refresh();
        _button.MouseLeave += (_, _) => Refresh();
        Child = _button;

        _panel = new MixerPanel();
        _panel.Touched += () => _lastTouch = DateTime.Now;
        _popup = new Popup
        {
            Child = _panel, PlacementTarget = this, Placement = PlacementMode.Custom,
            AllowsTransparency = true, StaysOpen = true, Focusable = false,
            CustomPopupPlacementCallback = Place,
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            if (DateTime.Now - _lastTouch > IdleClose && !_panel.Dragging) Close();
            else _panel.Tick();
        };
        Unloaded += (_, _) => Close();
        Refresh();
    }

    /// <summary>サイドバーが左端にあるときは true にして、パネルをボタンの右に出す。</summary>
    public bool OpenToRight { get; set; }

    /// <summary>オーロラの時間帯テーマに色を合わせる（LayoutPicker と同じ）。パネルは暗い色のまま。</summary>
    public void UseAuroraColors()
    {
        SetResourceReference(BackgroundProperty, "AGlass");
        SetResourceReference(BorderBrushProperty, "ALine");
        _icon.SetResourceReference(Shape.StrokeProperty, "AText");
        _button.SetResourceReference(BorderBrushProperty, "AT85");
        Refresh();
    }

    private void Toggle()
    {
        if (_popup.IsOpen) Close();
        else Open();
    }

    private void Open()
    {
        _lastTouch = DateTime.Now;
        _panel.Load();
        _popup.IsOpen = true;
        _timer.Start();
        // ほかの場所のクリックで閉じる（Popup の StaysOpen=False はマウスを奪うので使わない）
        _window = Window.GetWindow(this);
        if (_window != null) _window.PreviewMouseDown += OnWindowMouseDown;
        Refresh();
    }

    private void Close()
    {
        if (!_popup.IsOpen) return;
        _popup.IsOpen = false;
        _timer.Stop();
        if (_window != null) _window.PreviewMouseDown -= OnWindowMouseDown;
        _window = null;
        Refresh();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_button.IsMouseOver) Close();
    }

    /// <summary>ボタンの左（OpenToRight なら右）に、上下の中心をそろえて置く。</summary>
    private CustomPopupPlacement[] Place(Size popup, Size target, Point offset)
    {
        double gap = 16 * target.Width / Width;  // 1920×1080 基準で 16 の隙間（拡大率に合わせる）
        double x = OpenToRight ? target.Width + gap : -popup.Width - gap;
        return new[] { new CustomPopupPlacement(new Point(x, (target.Height - popup.Height) / 2), PopupPrimaryAxis.Horizontal) };
    }

    private void Refresh()
    {
        bool on = _popup?.IsOpen == true;
        _button.BorderThickness = new Thickness(on ? 2 : 0);
        _button.Padding = new Thickness(on ? 0 : 2);
        _button.Opacity = on ? 1 : _button.IsMouseOver ? 0.85 : 0.55;
    }
}

/// <summary>音量ミキサーのパネル本体（全体＋アプリごとの行）。色は暗い色で固定。</summary>
internal sealed class MixerPanel : Border
{
    private static readonly Brush TextBrush = Frozen(0xF4, 0xF7, 0xFB);
    private static readonly Brush SubBrush = Frozen(0xF4, 0xF7, 0xFB, 0x73);
    private static readonly Brush LineBrush = Frozen(0xFF, 0xFF, 0xFF, 0x1A);
    private static readonly Brush MuteBrush = Frozen(0xFF, 0xB5, 0x47);
    private static readonly Brush AppFill = Frozen(0x8E, 0x9C, 0xFF);
    private static readonly Brush MasterFill = Frozen(0xF4, 0xF7, 0xFB);
    private static readonly Brush HoverBrush = Frozen(0xFF, 0xFF, 0xFF, 0x14);
    private static readonly Brush ChipBrush = Frozen(0xFF, 0xFF, 0xFF, 0x0F);
    private static readonly Brush CheckBrush = Frozen(0x6F, 0xD3, 0xA8);
    private static readonly Geometry SpeakerOn = Geometry.Parse("M6,11 L6,17 L10,17 L15,21 L15,7 L10,11 Z M18.5,11 A4,4 0 0 1 18.5,17");
    private static readonly Geometry SpeakerOff = Geometry.Parse("M6,11 L6,17 L10,17 L15,21 L15,7 L10,11 Z M18,11 L24,17 M24,11 L18,17");
    private static readonly FontFamily UiFont = (FontFamily)Application.Current.FindResource("UiFont");
    private static readonly FontFamily NumFont = (FontFamily)Application.Current.FindResource("NumFont");
    private static readonly Color[] LetterColors =
    {
        Color.FromRgb(0x2B, 0xB6, 0x73), Color.FromRgb(0x5A, 0x64, 0xD8), Color.FromRgb(0xD8, 0x7A, 0x3A),
        Color.FromRgb(0xC9, 0x46, 0x60), Color.FromRgb(0x3A, 0x9E, 0xB8), Color.FromRgb(0x8A, 0x5C, 0xC8),
    };

    private readonly AudioMixer _mixer = new();
    private readonly TextBlock _device;
    private readonly Border _deviceChip;
    private readonly StackPanel _deviceList = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 8) };
    private readonly StackPanel _rows = new();
    private readonly List<MixerRow> _rowList = new();
    private int _ticks;

    /// <summary>パネルがさわられた（閉じるまでの10秒を数え直す）。</summary>
    public event Action? Touched;
    public bool Dragging => _rowList.Any(r => r.Slider.Dragging);

    public MixerPanel()
    {
        Width = 626;
        CornerRadius = new CornerRadius(24);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(24, 18, 24, 14);
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x1F));
        BorderBrush = Frozen(0xFF, 0xFF, 0xFF, 0x29);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Opacity = 0.6, Color = Colors.Black };
        System.Windows.Documents.TextElement.SetFontFamily(this, UiFont);
        System.Windows.Documents.TextElement.SetForeground(this, TextBrush);
        Margin = new Thickness(0, 0, 0, 30);  // 影が切れないように

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Path
        {
            Width = 28, Height = 28, Stretch = Stretch.None, Stroke = TextBrush, StrokeThickness = 2, Opacity = 0.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Data = SpeakerOn, Margin = new Thickness(-4, 0, 6, 0),
        });
        title.Children.Add(new TextBlock { Text = "音量ミキサー", FontSize = 20, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });
        // 右上の「出力：機器名 ▾」。押すと下に出力機器の一覧を出して、選ぶと既定の出力機器を切り替える
        _device = new TextBlock { FontSize = 15, Foreground = SubBrush, MaxWidth = 330, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chip = new StackPanel { Orientation = Orientation.Horizontal };
        chip.Children.Add(_device);
        chip.Children.Add(new Path
        {
            Width = 12, Height = 12, Stretch = Stretch.None, Stroke = SubBrush, StrokeThickness = 1.6, Margin = new Thickness(8, 1, 0, 0),
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M2,4 L6,8 L10,4"), VerticalAlignment = VerticalAlignment.Center,
        });
        _deviceChip = new Border
        {
            Child = chip, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), Background = ChipBrush,
            Cursor = Cursors.Hand, ToolTip = "出力機器を変える", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        _deviceChip.MouseEnter += (_, _) => _deviceChip.Background = HoverBrush;
        _deviceChip.MouseLeave += (_, _) => _deviceChip.Background = ChipBrush;
        _deviceChip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _deviceChip.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleDevices(); };
        header.Children.Add(title);
        header.Children.Add(_deviceChip);

        var foot = new TextBlock
        {
            Text = "ほかの場所をクリックするか、10秒さわらないと閉じる", FontSize = 13, Foreground = SubBrush, Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_deviceList);
        stack.Children.Add(_rows);
        stack.Children.Add(foot);
        Child = stack;

        MouseMove += (_, _) => Touched?.Invoke();
        PreviewMouseDown += (_, _) => Touched?.Invoke();
        PreviewMouseWheel += (_, _) => Touched?.Invoke();
    }

    /// <summary>開くときに一覧を読み直す。</summary>
    public void Load()
    {
        _ticks = 0;
        _deviceList.Visibility = Visibility.Collapsed;
        Rebuild(_mixer.Refresh());
        Tick();
    }

    /// <summary>1秒20回。音の大きさを動かし、1秒ごとに一覧と音量を読み直す。</summary>
    public void Tick()
    {
        if (++_ticks % 20 == 0)
        {
            var entries = _mixer.Refresh();
            ShowDeviceName();
            if (!entries.Select(e => e.Key).SequenceEqual(_rowList.Select(r => r.Entry.Key))) Rebuild(entries);
            else for (int i = 0; i < entries.Count; i++) _rowList[i].Entry = entries[i];
        }
        _mixer.ReadPeaks();
        foreach (var row in _rowList) row.Update();
    }

    private void Rebuild(IReadOnlyList<MixerEntry> entries)
    {
        ShowDeviceName();
        _rows.Children.Clear();
        _rowList.Clear();
        if (entries.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "出力の機器が見つかりません", FontSize = 18, Foreground = SubBrush, Margin = new Thickness(0, 16, 0, 16) });
            return;
        }
        foreach (var e in entries)
        {
            var row = new MixerRow(_mixer, e);
            _rowList.Add(row);
            _rows.Children.Add(row.Root);
        }
    }

    private void ShowDeviceName()
    {
        _device.Text = "出力：" + (_mixer.DeviceName.Length > 0 ? _mixer.DeviceName : "なし");
    }

    private void ToggleDevices()
    {
        if (_deviceList.Visibility == Visibility.Visible)
        {
            _deviceList.Visibility = Visibility.Collapsed;
            return;
        }
        _deviceList.Children.Clear();
        var devices = _mixer.Devices();
        if (devices.Count == 0)
            _deviceList.Children.Add(new TextBlock { Text = "出力の機器が見つかりません", FontSize = 16, Foreground = SubBrush, Margin = new Thickness(12, 8, 0, 8) });
        foreach (var d in devices) _deviceList.Children.Add(DeviceRow(d));
        _deviceList.Children.Add(new Rectangle { Height = 1, Fill = LineBrush, Margin = new Thickness(0, 8, 0, 0) });
        _deviceList.Visibility = Visibility.Visible;
    }

    /// <summary>出力機器の一覧の1行。今の機器には緑のチェック。</summary>
    private FrameworkElement DeviceRow(OutputDevice d)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (d.IsDefault)
            grid.Children.Add(new Path
            {
                Width = 18, Height = 18, Stretch = Stretch.None, Stroke = CheckBrush, StrokeThickness = 2.2, VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M3,9 L7.5,13.5 L15,5"),
            });
        var name = new TextBlock
        {
            Text = d.Name, FontSize = 17, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = d.IsDefault ? TextBrush : Frozen(0xF4, 0xF7, 0xFB, 0xC0),
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var row = new Border
        {
            Child = grid, Height = 40, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent, Cursor = d.IsDefault ? Cursors.Arrow : Cursors.Hand,
        };
        row.MouseEnter += (_, _) => row.Background = HoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, e) => e.Handled = true;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!d.IsDefault) AudioMixer.SetDefaultDevice(d.Id);
            Load();  // 機器が変わるとアプリの音声も変わるので、一覧を読み直す（一覧は閉じる）
        };
        return row;
    }

    private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>1行：アイコン・名前・つまみ・数字・消音ボタン。</summary>
    private sealed class MixerRow
    {
        private readonly AudioMixer _mixer;
        private readonly TextBlock _value;
        private readonly Path _speaker;
        private readonly FrameworkElement _icon, _name;
        public readonly Grid Root;
        public readonly VolumeSlider Slider;
        public MixerEntry Entry;

        public MixerRow(AudioMixer mixer, MixerEntry entry)
        {
            _mixer = mixer;
            Entry = entry;
            Root = new Grid { Height = entry.IsMaster ? 62 : 56 };
            foreach (var w in new[] { 40, 14, 150, 14, -1, 14, 52, 10, 30 })
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            if (entry.IsMaster)
            {
                Root.Margin = new Thickness(0, 0, 0, 6);
                Root.Children.Add(new Rectangle { Height = 1, Fill = LineBrush, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false });
                Grid.SetColumnSpan(Root.Children[0], 9);
            }

            _icon = MakeIcon(entry);
            Add(_icon, 0);

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = entry.Name, FontSize = 19, TextTrimming = TextTrimming.CharacterEllipsis });
            if (entry.Sub.Length > 0) name.Children.Add(new TextBlock { Text = entry.Sub, FontSize = 13, Foreground = SubBrush });
            _name = name;
            Add(name, 2);

            Slider = new VolumeSlider { Fill = entry.IsMaster ? MasterFill : AppFill };
            Slider.Changed += level => _mixer.SetLevel(Entry, level);
            Add(Slider, 4);

            _value = new TextBlock { FontSize = 21, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Add(_value, 6);

            _speaker = new Path
            {
                Width = 28, Height = 28, Stretch = Stretch.None, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            var mute = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = _speaker, ToolTip = "消音／戻す", VerticalAlignment = VerticalAlignment.Center };
            mute.MouseLeftButtonDown += (_, e) => e.Handled = true;
            mute.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _mixer.SetMute(Entry, !Entry.Muted);
                Update();
            };
            Add(mute, 8);
            Update();
        }

        public void Update()
        {
            bool muted = Entry.Muted;
            if (!Slider.Dragging) Slider.Value = Entry.Level;
            Slider.Peak = muted ? 0 : Entry.Peak;
            _value.Text = muted ? "消音" : Math.Round(Entry.Level * 100).ToString("0");
            _value.FontFamily = muted ? UiFont : NumFont;
            _value.FontSize = muted ? 17 : 21;
            _value.Foreground = muted ? MuteBrush : TextBrush;
            _speaker.Stroke = muted ? MuteBrush : SubBrush;
            _speaker.Data = muted ? SpeakerOff : SpeakerOn;
            double dim = muted ? 0.38 : 1;
            _icon.Opacity = dim;
            _name.Opacity = dim;
            Slider.Opacity = dim;
        }

        private void Add(UIElement e, int column)
        {
            Grid.SetColumn(e, column);
            Root.Children.Add(e);
        }

        private static FrameworkElement MakeIcon(MixerEntry e)
        {
            var box = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center };
            if (e.IsMaster)
            {
                box.Background = new LinearGradientBrush(Color.FromRgb(0x5C, 0x63, 0x78), Color.FromRgb(0x2E, 0x33, 0x46), 45);
                box.Child = new Path
                {
                    Width = 28, Height = 28, Stretch = Stretch.None, Stroke = TextBrush, StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Data = SpeakerOn,
                };
            }
            else if (e.Icon != null)
            {
                box.Child = new Image { Source = e.Icon, Width = 32, Height = 32 };
                RenderOptions.SetBitmapScalingMode(box.Child, BitmapScalingMode.HighQuality);
            }
            else
            {
                // アイコンが取れないアプリ（ストアのアプリなど）は頭文字
                var c = LetterColors[(uint)e.Key.GetHashCode() % LetterColors.Length];
                box.Background = new SolidColorBrush(c);
                box.Child = new TextBlock
                {
                    Text = e.Name.Length > 0 ? e.Name[..1].ToUpperInvariant() : "?", FontSize = 20, FontWeight = FontWeights.SemiBold,
                    FontFamily = NumFont,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            return box;
        }
    }
}

/// <summary>横のつまみ。ドラッグ・クリック・ホイール（2 ずつ）で 0〜1 を変える。下の緑の線は今の音の大きさ。</summary>
internal sealed class VolumeSlider : Grid
{
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xBF, 0x6F, 0xD3, 0xA8)));

    private readonly Rectangle _fill, _peak;
    private readonly Ellipse _knob;
    private double _value, _peakShown;

    /// <summary>利用者が動かした（0〜1）。</summary>
    public event Action<float>? Changed;
    public bool Dragging { get; private set; }

    public VolumeSlider()
    {
        Height = 24;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Fill = TrackBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) });
        _fill = new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
        _peak = new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = PeakBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 19, 0, 0) };
        _knob = new Ellipse { Width = 16, Height = 16, Fill = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFB))) };
        Children.Add(_fill);
        Children.Add(_peak);
        // つまみは端で半分はみ出すので、切り取られない Canvas に置く
        var canvas = new Canvas();
        canvas.Children.Add(_knob);
        Children.Add(canvas);
        SizeChanged += (_, _) => Layout();
    }

    public Brush Fill { set => _fill.Fill = value; }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Layout(); }
    }

    /// <summary>今の音の大きさ。下がるときはゆっくり下げる。</summary>
    public double Peak
    {
        set { _peakShown = Math.Max(Math.Clamp(value, 0, 1), _peakShown * 0.85); Layout(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dragging = true;
        CaptureMouse();
        SetFrom(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Dragging) SetFrom(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => Dragging = false;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        Value = Math.Round(_value * 50 + Math.Sign(e.Delta)) / 50;
        Changed?.Invoke((float)_value);
    }

    private void SetFrom(double x)
    {
        Value = ActualWidth > 0 ? x / ActualWidth : 0;
        Changed?.Invoke((float)_value);
    }

    private void Layout()
    {
        double w = ActualWidth;
        _fill.Width = w * _value;
        _peak.Width = w * _peakShown;
        Canvas.SetLeft(_knob, w * _value - 8);
        Canvas.SetTop(_knob, 4);
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
