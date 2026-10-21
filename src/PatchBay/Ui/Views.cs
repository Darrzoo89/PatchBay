using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NAudio.CoreAudioApi;
using PatchBay.Audio;
using PatchBay.Model;

namespace PatchBay.Ui;

internal static class Theme
{
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x5A, 0x5F), // red
        Color.FromRgb(0x3F, 0xA7, 0xFF), // blue
        Color.FromRgb(0xFF, 0xB4, 0x00), // amber
        Color.FromRgb(0x3D, 0xDC, 0x84), // green
        Color.FromRgb(0xB5, 0x7B, 0xFF), // purple
        Color.FromRgb(0xFF, 0x7A, 0xC6), // pink
        Color.FromRgb(0x2E, 0xE6, 0xD6), // teal
        Color.FromRgb(0xFF, 0x8A, 0x3D), // orange
    };

    public static Color CableColor(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];
    public static SolidColorBrush Brush(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public static readonly Brush BoxBg = Brush(0x26, 0x2A, 0x31);
    public static readonly Brush BoxBorder = Brush(0x3A, 0x3F, 0x48);
    public static readonly Brush HeaderBg = Brush(0x31, 0x36, 0x3F);
    public static readonly Brush Muted = Brush(0x8B, 0x93, 0xA1);
    public static readonly Brush SocketRing = Brush(0x9A, 0xA0, 0xAA);
    public static readonly Brush SocketHole = Brush(0x0E, 0x0F, 0x12);
    public static readonly Brush MeterBg = Brush(0x1A, 0x1C, 0x21);
    public static readonly Brush MeterGreen = Brush(0x3D, 0xDC, 0x84);
    public static readonly Brush MeterYellow = Brush(0xFF, 0xC8, 0x3D);
    public static readonly Brush MeterRed = Brush(0xFF, 0x4D, 0x4D);
    public static readonly Brush Highlight = Brush(0xFF, 0xFF, 0xFF);
    public static readonly Brush Ghost = Brush(0x6B, 0x70, 0x7A);
}

internal sealed class JackView
{
    public required JackInfo Info { get; init; }
    public required BoxView Box { get; init; }
    public required FrameworkElement Socket { get; init; }
    public required Ellipse Ring { get; init; }
    public required Ellipse Hole { get; init; }
    public required ScaleTransform MeterScale { get; init; }
    public required Rectangle MeterFill { get; init; }
    public AudioMeterInformation? Meter { get; set; }
    public double Level { get; set; }

    public Point Center(Visual board)
    {
        try
        {
            return Socket.TransformToAncestor(board).Transform(new Point(Socket.ActualWidth / 2, Socket.ActualHeight / 2));
        }
        catch (InvalidOperationException)
        {
            return new Point(double.NaN, double.NaN);
        }
    }

    public void SetHover(bool on)
    {
        Ring.Stroke = on ? Theme.Highlight : null;
        Ring.StrokeThickness = on ? 2.5 : 0;
    }
}

internal sealed class BoxView
{
    public required DeviceGroup Group { get; init; }
    public required Border Root { get; init; }
    public required FrameworkElement Header { get; init; }
    public List<JackView> Jacks { get; } = new();

    public static BoxView Create(DeviceGroup group)
    {
        var root = new Border
        {
            Background = Theme.BoxBg,
            BorderBrush = Theme.BoxBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            MinWidth = 230,
            MaxWidth = 340,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
            Opacity = group.IsGhost ? 0.55 : 1,
        };
        Panel.SetZIndex(root, 10);

        var stack = new StackPanel();
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = group.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap,
        });
        string? sub = group.IsGhost ? "Unplugged — cables come back when it's reconnected" : group.Subtitle;
        if (sub != null)
            headerStack.Children.Add(new TextBlock { Text = sub, Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var header = new Border
        {
            Background = Theme.HeaderBg,
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.SizeAll,
            Child = headerStack,
            ToolTip = "Drag to move · right-click to hide",
        };
        stack.Children.Add(header);

        var view = new BoxView { Group = group, Root = root, Header = header };
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var jack in group.Jacks) body.Children.Add(view.CreateJackRow(jack));
        stack.Children.Add(body);
        root.Child = stack;
        return view;
    }

    private FrameworkElement CreateJackRow(JackInfo jack)
    {
        bool isIn = jack.Kind == JackKind.In;

        var ring = new Ellipse { Width = 20, Height = 20, Fill = Theme.SocketRing };
        var hole = new Ellipse { Width = 9, Height = 9, Fill = Theme.SocketHole, IsHitTestVisible = false };
        var socket = new Grid
        {
            Width = 20, Height = 20, Cursor = jack.IsMissing ? Cursors.Arrow : Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            // Half the socket hangs off the edge of the box, like a real jack.
            RenderTransform = new TranslateTransform(isIn ? -10 : 10, 0),
            ToolTip = jack.IsMissing ? "Device is unplugged"
                : isIn ? $"IN: drop a cable here to send sound INTO \"{jack.Label}\""
                       : $"OUT: sound coming FROM \"{jack.Label}\" — drag a cable from here",
        };
        socket.Children.Add(ring);
        socket.Children.Add(hole);

        var tag = new Border
        {
            Background = isIn ? Theme.Brush(0x2A, 0x44, 0x5E) : Theme.Brush(0x5E, 0x3A, 0x2A),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 1),
            Margin = isIn ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = isIn ? "IN" : "OUT", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Theme.Brush(0xDD, 0xE3, 0xEA) },
        };
        var label = new TextBlock
        {
            Text = jack.Label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = jack.Label,
        };
        var labelLine = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tag, isIn ? Dock.Left : Dock.Right);
        labelLine.Children.Add(tag);
        label.TextAlignment = isIn ? TextAlignment.Left : TextAlignment.Right;
        labelLine.Children.Add(label);

        var meterScale = new ScaleTransform(0, 1);
        var meterFill = new Rectangle { Fill = Theme.MeterGreen, RenderTransform = meterScale, RadiusX = 2, RadiusY = 2 };
        var meter = new Grid { Height = 4, Margin = new Thickness(0, 4, 0, 0) };
        meter.Children.Add(new Rectangle { Fill = Theme.MeterBg, RadiusX = 2, RadiusY = 2 });
        meter.Children.Add(meterFill);
        if (isIn) meterScale.CenterX = 0;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(labelLine);
        text.Children.Add(meter);

        var row = new Grid { Margin = new Thickness(0, 5, 0, 5), MinHeight = 30 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = isIn ? new GridLength(14) : new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = isIn ? new GridLength(1, GridUnitType.Star) : new GridLength(14) });
        text.Margin = isIn ? new Thickness(4, 0, 14, 0) : new Thickness(14, 0, 4, 0);
        Grid.SetColumn(socket, isIn ? 0 : 1);
        Grid.SetColumn(text, isIn ? 1 : 0);
        socket.HorizontalAlignment = HorizontalAlignment.Center;
        row.Children.Add(socket);
        row.Children.Add(text);

        var jv = new JackView
        {
            Info = jack, Box = this, Socket = socket, Ring = ring, Hole = hole, MeterScale = meterScale, MeterFill = meterFill,
        };
        // Out-jack meters fill from the right so they grow toward the socket.
        if (!isIn) meterFill.RenderTransformOrigin = new Point(1, 0.5);
        socket.Tag = jv;
        ring.Tag = jv;
        if (jack.Device != null)
        {
            try { jv.Meter = jack.Device.AudioMeterInformation; } catch { }
        }
        Jacks.Add(jv);
        return row;
    }
}

internal sealed class CableView
{
    public required CableModel Model { get; init; }
    public required Path Shadow { get; init; }
    public required Path Main { get; init; }
    public required Path Hit { get; init; }
    public required Border Badge { get; init; }
    public required TextBlock BadgeText { get; init; }
    public bool Hover { get; set; }
    public bool Visible { get; set; }
    public Point Mid { get; set; }

    public static CableView Create(CableModel model)
    {
        var shadow = new Path { Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), StrokeThickness = 8, IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = new TranslateTransform(1.5, 3) };
        var main = new Path { StrokeThickness = 5, IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var hit = new Path { Stroke = Brushes.Transparent, StrokeThickness = 16, Cursor = Cursors.Hand };
        var badgeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 2), Child = badgeText,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(shadow, 20);
        Panel.SetZIndex(main, 21);
        Panel.SetZIndex(hit, 22);
        Panel.SetZIndex(badge, 23);
        return new CableView { Model = model, Shadow = shadow, Main = main, Hit = hit, Badge = badge, BadgeText = badgeText };
    }

    public IEnumerable<UIElement> Elements => new UIElement[] { Shadow, Main, Hit, Badge };

    /// <summary>A drooping patch-cable curve from an OUT socket (a) to an IN socket (b).</summary>
    public static (Geometry Geometry, Point Mid) Curve(Point a, Point b)
    {
        double dist = (b - a).Length;
        double dx = Math.Max(45, Math.Abs(b.X - a.X) * 0.45);
        double sag = Math.Min(170, 25 + dist * 0.18);
        var c1 = new Point(a.X + dx, a.Y + sag);
        var c2 = new Point(b.X - dx, b.Y + sag);
        var fig = new PathFigure { StartPoint = a, IsFilled = false };
        fig.Segments.Add(new BezierSegment(c1, c2, b, true));
        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();
        var mid = new Point((a.X + 3 * c1.X + 3 * c2.X + b.X) / 8, (a.Y + 3 * c1.Y + 3 * c2.Y + b.Y) / 8);
        return (geo, mid);
    }
}
