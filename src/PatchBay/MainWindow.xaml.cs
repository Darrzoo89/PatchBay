using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PatchBay.Audio;
using PatchBay.Model;
using PatchBay.Ui;

namespace PatchBay;

public partial class MainWindow : Window
{
    private static readonly (int Ms, string Name)[] LatencyChoices =
    {
        (3, "Lowest"), (5, "Very low"), (10, "Low"), (20, "Safe"), (40, "Extra safe"),
    };

    private readonly AudioEngine _engine = new();
    private readonly DeviceCatalog _catalog = new();
    private readonly LayoutFile _layout;

    private List<DeviceGroup> _groups = new();
    private Dictionary<string, JackInfo> _realJacks = new();
    private readonly Dictionary<string, BoxView> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JackView> _jacks = new();
    private readonly Dictionary<Guid, CableView> _cableViews = new();
    private readonly Stack<CableModel> _undo = new();

    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _deviceDebounce;
    private readonly DispatcherTimer _saveDebounce;

    // Drag state
    private BoxView? _dragBox;
    private Vector _dragOffset;
    private JackView? _dragJack;
    private Path? _preview;
    private int _previewColor;
    private JackView? _hoverJack;
    private CableView? _pressedCable;

    public MainWindow()
    {
        InitializeComponent();
        _layout = LayoutStore.Load();
        _engine.LatencyMs = _layout.LatencyMs;

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateMeters(), Dispatcher);
        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); RefreshDevices(); SetStatus("Devices changed — list updated."); };
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); LayoutStore.Save(_layout); };

        _catalog.DevicesChanged += () => Dispatcher.BeginInvoke(() => { _deviceDebounce.Stop(); _deviceDebounce.Start(); });
        _engine.CableFailed += OnCableFailed;

        Guide.Fill(GuideContent);
        GuidePanel.Visibility = _layout.GuideOpen ? Visibility.Visible : Visibility.Collapsed;
        LoopbackCheck.IsChecked = _layout.ShowLoopback;
        UpdatePowerButton();
        UpdateLatencyButton();

        Loaded += (_, _) => { RefreshDevices(); _meterTimer.Start(); };
        Closing += (_, _) =>
        {
            _meterTimer.Stop();
            LayoutStore.Save(_layout);
            _engine.Dispose();
            _catalog.Dispose();
        };
        PreviewKeyDown += OnKeyDown;
        Board.LostMouseCapture += (_, _) => CancelDrags();
    }

    // ---------------------------------------------------------------- devices

    private void RefreshDevices()
    {
        List<DeviceGroup> groups;
        try
        {
            groups = _catalog.Build(includeLoopback: true);
        }
        catch (Exception ex)
        {
            LayoutStore.Log("Device scan failed: " + ex);
            SetStatus("Could not list audio devices: " + ex.Message);
            return;
        }

        _realJacks = groups.SelectMany(g => g.Jacks).GroupBy(j => j.Id).ToDictionary(g => g.Key, g => g.First());

        // Loopback ("what's playing") jacks only show when asked for, or when a cable uses one.
        var used = _layout.Cables.SelectMany(c => new[] { c.From, c.To }).ToHashSet();
        foreach (var g in groups)
            g.Jacks.RemoveAll(j => j.IsLoopback && !_layout.ShowLoopback && !used.Contains(j.Id));
        groups.RemoveAll(g => g.Jacks.Count == 0);

        // Saved cables whose device is unplugged keep a greyed-out "ghost" jack.
        var byKey = groups.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var ghostIds = new HashSet<string>();
        void AddGhost(string id, string label, string group, JackKind kind)
        {
            if (_realJacks.ContainsKey(id) || !ghostIds.Add(id)) return;
            if (string.IsNullOrEmpty(group)) group = "Unplugged device";
            if (!byKey.TryGetValue(group, out var g))
            {
                g = new DeviceGroup { Key = group, Title = group, IsGhost = true };
                byKey[group] = g;
                groups.Add(g);
            }
            int colon = id.IndexOf(':');
            g.Jacks.Add(new JackInfo
            {
                Id = id, EndpointId = colon >= 0 ? id[(colon + 1)..] : id, Label = label, Kind = kind,
                GroupKey = g.Key, GroupTitle = g.Title, IsLoopback = id.StartsWith("loop:"),
            });
        }
        foreach (var c in _layout.Cables)
        {
            AddGhost(c.From, c.FromLabel, c.FromGroup, JackKind.Out);
            AddGhost(c.To, c.ToLabel, c.ToGroup, JackKind.In);
        }

        _groups = groups;
        RebuildBoard();
        SyncEngine();
    }

    private void RebuildBoard()
    {
        foreach (var box in _boxes.Values) Board.Children.Remove(box.Root);
        _boxes.Clear();
        _jacks.Clear();

        var visible = _groups.Where(g => !_layout.Hidden.Contains(g.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        PlaceNewBoxes(visible);

        foreach (var g in visible)
        {
            var box = BoxView.Create(g);
            var pos = _layout.Boxes[g.Key];
            Canvas.SetLeft(box.Root, pos.X);
            Canvas.SetTop(box.Root, pos.Y);
            Board.Children.Add(box.Root);
            _boxes[g.Key] = box;

            box.Header.MouseLeftButtonDown += (_, e) => StartBoxDrag(box, e);
            box.Header.MouseRightButtonUp += (_, e) => { ShowBoxMenu(box); e.Handled = true; };

            foreach (var jv in box.Jacks)
            {
                _jacks[jv.Info.Id] = jv;
                var j = jv;
                jv.Socket.MouseLeftButtonDown += (_, e) => StartCableDrag(j, e);
                jv.Socket.MouseEnter += (_, _) => { if (_dragJack == null && !j.Info.IsMissing) j.SetHover(true); };
                jv.Socket.MouseLeave += (_, _) => { if (_dragJack == null) j.SetHover(false); };
            }
        }

        foreach (var c in _layout.Cables)
            if (!_cableViews.ContainsKey(c.Id)) AddCableView(c);

        UpdateHiddenButton();
        Dispatcher.BeginInvoke(RedrawCables, DispatcherPriority.Loaded);
    }

    private static double EstimateHeight(DeviceGroup g) => 52 + (g.Subtitle != null || g.IsGhost ? 16 : 0) + 41 * g.Jacks.Count;

    /// <summary>Gives every box without a saved position a spot. Boxes that only have OUT jacks
    /// (mics) go first so they tend to land on the left; outputs-only boxes last.</summary>
    private void PlaceNewBoxes(List<DeviceGroup> groups)
    {
        double[] colX = { 40, 450, 860 };
        double[] colY = { 30, 30, 30 };
        int NearestCol(double x) => Enumerable.Range(0, colX.Length).OrderBy(i => Math.Abs(colX[i] - x)).First();

        foreach (var g in groups)
        {
            if (_layout.Boxes.TryGetValue(g.Key, out var p))
            {
                int c = NearestCol(p.X);
                colY[c] = Math.Max(colY[c], p.Y + EstimateHeight(g) + 28);
            }
        }

        int Rank(DeviceGroup g)
        {
            bool hasOut = g.Jacks.Any(j => j.Kind == JackKind.Out);
            bool hasIn = g.Jacks.Any(j => j.Kind == JackKind.In);
            return hasOut && !hasIn ? 0 : hasIn && !hasOut ? 2 : 1;
        }

        foreach (var g in groups.Where(g => !_layout.Boxes.ContainsKey(g.Key)).OrderBy(Rank))
        {
            int col = Enumerable.Range(0, colX.Length).OrderBy(i => colY[i]).ThenBy(i => i).First();
            _layout.Boxes[g.Key] = new BoxPosition { X = colX[col], Y = colY[col] };
            colY[col] += EstimateHeight(g) + 28;
        }
    }

    // ---------------------------------------------------------------- audio

    /// <summary>Starts every cable whose two devices are present, stops the rest.</summary>
    private void SyncEngine()
    {
        foreach (var c in _layout.Cables)
        {
            bool present = _realJacks.TryGetValue(c.From, out var from) & _realJacks.TryGetValue(c.To, out var to);
            if (!_layout.AudioOn || !present)
            {
                _engine.Disconnect(c.Id);
                if (!present) c.Error = null;
                continue;
            }
            if (_engine.IsActive(c.Id)) continue;
            try
            {
                _engine.Connect(c.Id, from!, to!, c.Volume, c.Muted, c.Channels);
                c.Error = null;
            }
            catch (Exception ex)
            {
                c.Error = Friendly(ex);
                LayoutStore.Log($"Cable {c.FromLabel} -> {c.ToLabel} failed: {ex}");
            }
        }
        RedrawCables();
        UpdateCount();
    }

    private void OnCableFailed(Guid id, string message)
    {
        var c = _layout.Cables.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        c.Error = message;
        LayoutStore.Log($"Cable {c.FromLabel} -> {c.ToLabel} stopped: {message}");
        SetStatus($"⚠ {c.FromLabel} → {c.ToLabel} stopped: {message}");
        RedrawCables();
        UpdateCount();
    }

    private static string Friendly(Exception ex)
    {
        string m = ex.Message;
        if (m.Contains("0x8889000A", StringComparison.OrdinalIgnoreCase) || m.Contains("DEVICE_IN_USE", StringComparison.OrdinalIgnoreCase))
            return "Another app is using this device in exclusive mode. Close it (or turn off exclusive mode), then press Refresh devices.";
        if (m.Contains("0x88890004", StringComparison.OrdinalIgnoreCase) || m.Contains("DEVICE_INVALIDATED", StringComparison.OrdinalIgnoreCase))
            return "The device went away. Press Refresh devices.";
        return m;
    }

    // ---------------------------------------------------------------- cables

    private void AddCableView(CableModel c)
    {
        var cv = CableView.Create(c);
        foreach (var el in cv.Elements) Board.Children.Add(el);
        _cableViews[c.Id] = cv;

        cv.Hit.MouseEnter += (_, _) =>
        {
            cv.Hover = true; StyleCable(cv);
            SetStatus($"{c.FromLabel} → {c.ToLabel}   ·   click to unplug · scroll for volume · right-click for options");
        };
        cv.Hit.MouseLeave += (_, _) => { cv.Hover = false; StyleCable(cv); };
        cv.Hit.MouseLeftButtonDown += (_, e) => { _pressedCable = cv; e.Handled = true; };
        cv.Hit.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressedCable == cv && _dragJack == null && _dragBox == null) RemoveCable(cv);
            _pressedCable = null;
            e.Handled = true;
        };
        cv.Hit.MouseRightButtonUp += (_, e) => { ShowCableMenu(cv); e.Handled = true; };
        cv.Hit.MouseWheel += (_, e) =>
        {
            float step = e.Delta > 0 ? 0.05f : -0.05f;
            c.Volume = (float)Math.Round(Math.Clamp(c.Volume + step, 0f, 2f), 2);
            CableSettingsChanged(c);
            SetStatus($"{c.FromLabel} → {c.ToLabel}: volume {c.Volume * 100:0}%");
            e.Handled = true;
        };
    }

    private void AddCable(JackView outJack, JackView inJack)
    {
        var c = new CableModel
        {
            From = outJack.Info.Id, FromLabel = outJack.Info.Label, FromGroup = outJack.Info.GroupKey,
            To = inJack.Info.Id, ToLabel = inJack.Info.Label, ToGroup = inJack.Info.GroupKey,
            Color = _previewColor,
        };
        _layout.Cables.Add(c);
        AddCableView(c);
        SyncEngine();
        Save();
        SetStatus(c.Error == null
            ? $"Plugged {outJack.Info.Label} → {inJack.Info.Label}"
            : $"⚠ Plugged in, but it couldn't start: {c.Error}");
    }

    private void RemoveCable(CableView cv)
    {
        var c = cv.Model;
        _engine.Disconnect(c.Id);
        foreach (var el in cv.Elements) Board.Children.Remove(el);
        _cableViews.Remove(c.Id);
        _layout.Cables.Remove(c);
        _undo.Push(c);
        bool loopbackGone = c.From.StartsWith("loop:") && !_layout.ShowLoopback;
        if (loopbackGone || IsGhost(c.From) || IsGhost(c.To)) RefreshDevices();
        else RedrawCables();
        UpdateCount();
        Save();
        SetStatus($"Unplugged {c.FromLabel} → {c.ToLabel}   (Ctrl+Z to undo)");
    }

    private bool IsGhost(string jackId) => !_realJacks.ContainsKey(jackId);

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var c = _undo.Pop();
        c.Error = null;
        _layout.Cables.Add(c);
        AddCableView(c);
        RefreshDevices();
        Save();
        SetStatus($"Put back {c.FromLabel} → {c.ToLabel}");
    }

    private void CableSettingsChanged(CableModel c)
    {
        _engine.Update(c.Id, c.Volume, c.Muted, c.Channels);
        if (_cableViews.TryGetValue(c.Id, out var cv)) StyleCable(cv);
        Save();
    }

    private void RedrawCables()
    {
        foreach (var jv in _jacks.Values) jv.Hole.Fill = Theme.SocketHole;

        foreach (var cv in _cableViews.Values)
        {
            var c = cv.Model;
            cv.Visible = false;
            if (_jacks.TryGetValue(c.From, out var a) && _jacks.TryGetValue(c.To, out var b))
            {
                var pa = a.Center(Board);
                var pb = b.Center(Board);
                if (!double.IsNaN(pa.X) && !double.IsNaN(pb.X))
                {
                    var (geo, mid) = CableView.Curve(pa, pb);
                    cv.Shadow.Data = cv.Main.Data = cv.Hit.Data = geo;
                    cv.Mid = mid;
                    cv.Visible = true;
                    var plug = new SolidColorBrush(a.Info.IsMissing || b.Info.IsMissing ? Theme.Ghost.ToColor() : Theme.CableColor(c.Color));
                    a.Hole.Fill = plug;
                    b.Hole.Fill = plug;
                }
            }
            StyleCable(cv);
        }
    }

    private void StyleCable(CableView cv)
    {
        var c = cv.Model;
        var vis = cv.Visible ? Visibility.Visible : Visibility.Collapsed;
        cv.Shadow.Visibility = cv.Main.Visibility = cv.Hit.Visibility = vis;

        bool missing = IsGhost(c.From) || IsGhost(c.To);
        bool error = c.Error != null;
        Color col = missing ? Theme.Ghost.ToColor() : Theme.CableColor(c.Color);

        cv.Main.Stroke = new SolidColorBrush(col);
        cv.Main.StrokeThickness = cv.Hover ? 7.5 : 5;
        cv.Shadow.StrokeThickness = cv.Hover ? 10 : 8;
        cv.Main.StrokeDashArray = missing || error ? new DoubleCollection { 1.6, 1.2 } : null;
        double op = c.Muted || !_layout.AudioOn ? 0.4 : missing ? 0.55 : 1;
        cv.Main.Opacity = op;
        cv.Shadow.Opacity = op;

        string text = error ? "⚠ not running" : missing ? "unplugged" : c.Muted ? "MUTED" : $"{c.Volume * 100:0}%";
        cv.BadgeText.Text = text;
        cv.Badge.Background = new SolidColorBrush(error ? Color.FromRgb(0xC6, 0x28, 0x28) : Color.FromArgb(0xE6, (byte)(col.R * 0.55), (byte)(col.G * 0.55), (byte)(col.B * 0.55)));
        bool showBadge = cv.Visible && (cv.Hover || error || c.Muted || Math.Abs(c.Volume - 1f) > 0.005f);
        cv.Badge.Visibility = showBadge ? Visibility.Visible : Visibility.Collapsed;
        if (showBadge)
        {
            cv.Badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(cv.Badge, cv.Mid.X - cv.Badge.DesiredSize.Width / 2);
            Canvas.SetTop(cv.Badge, cv.Mid.Y - cv.Badge.DesiredSize.Height / 2);
        }

        string tip = $"{c.FromLabel}  →  {c.ToLabel}";
        if (error) tip += "\n\n⚠ " + c.Error;
        else if (missing) tip += "\n\nA device on this cable is unplugged. It will reconnect when the device comes back.";
        else if (!_layout.AudioOn) tip += "\n\nAudio is switched off (toolbar).";
        tip += "\n\nClick to unplug · scroll for volume · right-click for options";
        cv.Hit.ToolTip = tip;
    }

    // ---------------------------------------------------------------- mouse

    private void StartBoxDrag(BoxView box, MouseButtonEventArgs e)
    {
        _dragBox = box;
        _dragOffset = e.GetPosition(box.Root) - new Point(0, 0);
        Panel.SetZIndex(box.Root, 11);
        Board.CaptureMouse();
        e.Handled = true;
    }

    private void StartCableDrag(JackView jack, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (jack.Info.IsMissing) return;
        _dragJack = jack;
        var counts = _layout.Cables.GroupBy(c => c.Color).ToDictionary(g => g.Key, g => g.Count());
        _previewColor = Enumerable.Range(0, Theme.Palette.Length).OrderBy(i => counts.GetValueOrDefault(i)).ThenBy(i => i).First();
        _preview = new Path
        {
            Stroke = new SolidColorBrush(Theme.CableColor(_previewColor)), StrokeThickness = 5, Opacity = 0.85,
            IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        Panel.SetZIndex(_preview, 30);
        Board.Children.Add(_preview);
        Board.CaptureMouse();
        UpdatePreview(e.GetPosition(Board));
        SetStatus(jack.Info.Kind == JackKind.Out
            ? $"Drop on an IN jack to send \"{jack.Info.Label}\" there"
            : $"Drop on an OUT jack to feed \"{jack.Info.Label}\"");
    }

    private void Board_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Board);
        if (_dragBox != null)
        {
            double x = Math.Clamp(p.X - _dragOffset.X, 0, Board.Width - 60);
            double y = Math.Clamp(p.Y - _dragOffset.Y, 0, Board.Height - 40);
            Canvas.SetLeft(_dragBox.Root, x);
            Canvas.SetTop(_dragBox.Root, y);
            _dragBox.Root.UpdateLayout();
            RedrawCables();
        }
        else if (_dragJack != null)
        {
            UpdatePreview(p);
        }
    }

    private void Board_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragJack != null)
        {
            var from = _dragJack;
            var target = _hoverJack;
            EndCableDrag();
            if (target != null)
            {
                var pair = Pair(from, target, out var why);
                if (pair != null) AddCable(pair.Value.Out, pair.Value.In);
                else SetStatus(why);
            }
            else SetStatus("Drag from a jack to another jack to plug in a cable.");
        }
        if (_dragBox != null)
        {
            var box = _dragBox;
            _dragBox = null;
            Panel.SetZIndex(box.Root, 10);
            _layout.Boxes[box.Group.Key] = new BoxPosition { X = Canvas.GetLeft(box.Root), Y = Canvas.GetTop(box.Root) };
            Save();
        }
        _pressedCable = null;
        if (Board.IsMouseCaptured) Board.ReleaseMouseCapture();
    }

    private void CancelDrags()
    {
        if (_dragJack != null) EndCableDrag();
        if (_dragBox != null)
        {
            Panel.SetZIndex(_dragBox.Root, 10);
            _layout.Boxes[_dragBox.Group.Key] = new BoxPosition { X = Canvas.GetLeft(_dragBox.Root), Y = Canvas.GetTop(_dragBox.Root) };
            _dragBox = null;
            Save();
        }
    }

    private void EndCableDrag()
    {
        if (_preview != null) Board.Children.Remove(_preview);
        _preview = null;
        _hoverJack?.SetHover(false);
        _dragJack?.SetHover(false);
        _hoverJack = null;
        _dragJack = null;
    }

    private void UpdatePreview(Point mouse)
    {
        if (_dragJack == null || _preview == null) return;
        var target = JackAt(mouse);
        if (target != null && Pair(_dragJack, target, out _) == null) target = null;
        if (target != _hoverJack)
        {
            _hoverJack?.SetHover(false);
            _hoverJack = target;
            _hoverJack?.SetHover(true);
        }
        _dragJack.SetHover(true);

        var end = target?.Center(Board) ?? mouse;
        var start = _dragJack.Center(Board);
        var (geo, _) = _dragJack.Info.Kind == JackKind.Out ? CableView.Curve(start, end) : CableView.Curve(end, start);
        _preview.Data = geo;
    }

    private JackView? JackAt(Point p)
    {
        JackView? found = null;
        VisualTreeHelper.HitTest(Board, null, r =>
        {
            DependencyObject? d = r.VisualHit;
            while (d != null && d != Board)
            {
                if (d is FrameworkElement { Tag: JackView jv }) { found = jv; return HitTestResultBehavior.Stop; }
                d = VisualTreeHelper.GetParent(d);
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return found;
    }

    private (JackView Out, JackView In)? Pair(JackView a, JackView b, out string why)
    {
        why = "";
        if (a == b) { why = "Drop the cable on a different jack."; return null; }
        if (a.Info.Kind == b.Info.Kind)
        {
            why = "Cables go from an OUT jack to an IN jack.";
            return null;
        }
        var o = a.Info.Kind == JackKind.Out ? a : b;
        var i = o == a ? b : a;
        if (o.Info.IsMissing || i.Info.IsMissing) { why = "That device is unplugged."; return null; }
        if (o.Info.IsLoopback && o.Info.EndpointId == i.Info.EndpointId)
        {
            why = "That would feed a device back into itself (instant feedback).";
            return null;
        }
        if (_layout.Cables.Any(c => c.From == o.Info.Id && c.To == i.Info.Id))
        {
            why = "Those two jacks are already connected.";
            return null;
        }
        return (o, i);
    }

    // ---------------------------------------------------------------- menus

    private void ShowCableMenu(CableView cv)
    {
        var c = cv.Model;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{c.FromLabel}  →  {c.ToLabel}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        if (c.Error != null) menu.Items.Add(new MenuItem { Header = "⚠ " + c.Error, IsEnabled = false });
        menu.Items.Add(new Separator());

        var volLabel = new TextBlock { Text = $"Volume {c.Volume * 100:0}%", Width = 90, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 200, Value = c.Volume * 100, Width = 170, TickFrequency = 100, IsSnapToTickEnabled = false };
        slider.ValueChanged += (_, e) =>
        {
            c.Volume = (float)Math.Round(e.NewValue / 100.0, 2);
            volLabel.Text = $"Volume {c.Volume * 100:0}%";
            CableSettingsChanged(c);
        };
        var volPanel = new StackPanel { Orientation = Orientation.Horizontal };
        volPanel.Children.Add(volLabel);
        volPanel.Children.Add(slider);
        menu.Items.Add(new MenuItem { Header = volPanel, StaysOpenOnClick = true });

        var reset = new MenuItem { Header = "Reset volume to 100%" };
        reset.Click += (_, _) => { c.Volume = 1f; CableSettingsChanged(c); };
        menu.Items.Add(reset);

        var mute = new MenuItem { Header = "Mute", IsCheckable = true, IsChecked = c.Muted };
        mute.Click += (_, _) => { c.Muted = mute.IsChecked; CableSettingsChanged(c); };
        menu.Items.Add(mute);

        var channels = new MenuItem { Header = "Channels" };
        foreach (var (mode, name) in new[]
        {
            (ChannelMode.Normal, "Stereo (as is)"),
            (ChannelMode.LeftOnly, "Left only → both sides  (e.g. guitar in input 1)"),
            (ChannelMode.RightOnly, "Right only → both sides  (e.g. input 2)"),
            (ChannelMode.MonoMix, "Mix to mono"),
        })
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = c.Channels == mode };
            item.Click += (_, _) => { c.Channels = mode; CableSettingsChanged(c); SetStatus($"Channels: {name}"); };
            channels.Items.Add(item);
        }
        menu.Items.Add(channels);

        var color = new MenuItem { Header = "Colour" };
        for (int i = 0; i < Theme.Palette.Length; i++)
        {
            int idx = i;
            var swatch = new Rectangle { Width = 60, Height = 12, Fill = new SolidColorBrush(Theme.Palette[i]), RadiusX = 3, RadiusY = 3 };
            var item = new MenuItem { Header = swatch, IsCheckable = true, IsChecked = c.Color == i };
            item.Click += (_, _) => { c.Color = idx; RedrawCables(); Save(); };
            color.Items.Add(item);
        }
        menu.Items.Add(color);

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Unplug cable" };
        remove.Click += (_, _) => RemoveCable(cv);
        menu.Items.Add(remove);

        menu.PlacementTarget = cv.Hit;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void ShowBoxMenu(BoxView box)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = box.Group.Title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        var hide = new MenuItem { Header = "Hide this box" };
        hide.Click += (_, _) =>
        {
            _layout.Hidden.Add(box.Group.Key);
            RebuildBoard();
            Save();
            SetStatus($"Hid \"{box.Group.Title}\". Bring it back from Hidden boxes. Its cables keep working.");
        };
        menu.Items.Add(hide);
        menu.PlacementTarget = box.Header;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void HiddenButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_layout.Hidden.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No hidden boxes. Right-click a box's title to hide it.", IsEnabled = false });
        foreach (var key in _layout.Hidden.ToList())
        {
            var item = new MenuItem { Header = "Show  " + key };
            item.Click += (_, _) => { _layout.Hidden.Remove(key); RebuildBoard(); Save(); };
            menu.Items.Add(item);
        }
        if (_layout.Hidden.Count > 1)
        {
            menu.Items.Add(new Separator());
            var all = new MenuItem { Header = "Show all" };
            all.Click += (_, _) => { _layout.Hidden.Clear(); RebuildBoard(); Save(); };
            menu.Items.Add(all);
        }
        menu.PlacementTarget = HiddenButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LatencyButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (ms, name) in LatencyChoices)
        {
            var item = new MenuItem { Header = $"{name} — {ms} ms", IsCheckable = true, IsChecked = _layout.LatencyMs == ms };
            item.Click += (_, _) =>
            {
                _layout.LatencyMs = ms;
                _engine.DisconnectAll();
                _engine.LatencyMs = ms;
                SyncEngine();
                UpdateLatencyButton();
                Save();
                SetStatus($"Buffer set to {ms} ms. Raise it if you hear crackles, lower it for less delay.");
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = LatencyButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- toolbar

    private void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        _layout.AudioOn = PowerButton.IsChecked == true;
        SyncEngine();
        UpdatePowerButton();
        Save();
        SetStatus(_layout.AudioOn ? "Audio on." : "Audio off — cables are kept but silent.");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        SetStatus($"Found {_realJacks.Count} jacks.");
    }

    private void ArrangeButton_Click(object sender, RoutedEventArgs e)
    {
        _layout.Boxes.Clear();
        RebuildBoard();
        Save();
        BoardScroll.ScrollToHome();
    }

    private void LoopbackCheck_Click(object sender, RoutedEventArgs e)
    {
        _layout.ShowLoopback = LoopbackCheck.IsChecked == true;
        // Existing boxes get taller; let them re-flow.
        _layout.Boxes.Clear();
        RefreshDevices();
        Save();
    }

    private void GuideButton_Click(object sender, RoutedEventArgs e)
    {
        _layout.GuideOpen = GuidePanel.Visibility != Visibility.Visible;
        GuidePanel.Visibility = _layout.GuideOpen ? Visibility.Visible : Visibility.Collapsed;
        Save();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelDrags(); if (Board.IsMouseCaptured) Board.ReleaseMouseCapture(); }
    }

    // ---------------------------------------------------------------- misc

    private void UpdateMeters()
    {
        foreach (var jv in _jacks.Values)
        {
            if (jv.Meter == null) continue;
            float peak;
            try { peak = jv.Meter.MasterPeakValue; }
            catch { jv.Meter = null; peak = 0; }
            jv.Level = Math.Max(peak, jv.Level * 0.8);
            double db = jv.Level <= 1e-5 ? -100 : 20 * Math.Log10(jv.Level);
            jv.MeterScale.ScaleX = Math.Clamp((db + 60) / 60, 0, 1);
            jv.MeterFill.Fill = db > -1 ? Theme.MeterRed : db > -9 ? Theme.MeterYellow : Theme.MeterGreen;
        }
    }

    private void UpdatePowerButton()
    {
        PowerButton.IsChecked = _layout.AudioOn;
        PowerButton.Content = _layout.AudioOn ? "●  Audio ON" : "●  Audio OFF";
    }

    private void UpdateLatencyButton() => LatencyButton.Content = $"Buffer: {_layout.LatencyMs} ms ▾";

    private void UpdateHiddenButton() =>
        HiddenButton.Content = _layout.Hidden.Count == 0 ? "Hidden boxes ▾" : $"Hidden boxes ({_layout.Hidden.Count}) ▾";

    private void UpdateCount()
    {
        int total = _layout.Cables.Count;
        int running = _layout.Cables.Count(c => _engine.IsActive(c.Id));
        CountText.Text = total == 0 ? "No cables yet" : $"{total} cable{(total == 1 ? "" : "s")} · {running} running";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void Save()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }
}

internal static class BrushExtensions
{
    public static Color ToColor(this Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;
}
