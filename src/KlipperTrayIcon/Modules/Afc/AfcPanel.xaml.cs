using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KlipperTrayIcon.Modules.Spoolman;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Afc;

/// <summary>Which sensor an AFC error is about, read from its message: ringed amber on the page.</summary>
public enum Culprit { None, Prep, Load, Hub, Pre, Post }

/// <summary>What the drawing shows that isn't simply a sensor: where the filament is, and what an error is about.</summary>
public static class AfcLook
{
    /// <summary>
    /// The lane whose filament is past the hub, colouring the path from there on: the one in the
    /// toolhead, else the one AFC is loading or unloading; null for none (or bypass).
    /// </summary>
    public static AfcLane? PathLane(AfcState state) =>
        state.Bypass ? null : state.Loaded ?? (state.Busy ? state.Working : null);

    /// <summary>The filament is in the path after the hub (from the lane at the hub to the toolhead's entry).</summary>
    public static bool PastHub(AfcState state, AfcLane lane)
    {
        var tool = state.Extruders.FirstOrDefault();
        return lane.ToolLoaded || tool?.Pre == true || tool?.Post == true
               || state.Hubs.FirstOrDefault(h => h.Name == lane.Hub) is { State: true };
    }

    /// <summary>
    /// The sensor AFC's error is about, and its lane (null: not a lane's): "lane2 failed to load to hub" is
    /// lane2's hub. Only while there's an error to show.
    /// </summary>
    public static (Culprit Sensor, string? Lane) Blame(AfcState state)
    {
        string message = state.Problem;
        if (message.Length == 0)
            return (Afc.Culprit.None, null);
        string? lane = state.Lanes.Select(l => l.Name)
            .FirstOrDefault(n => Regex.IsMatch(message, $@"\b{Regex.Escape(n)}\b", RegexOptions.IgnoreCase));
        string m = message.ToLowerInvariant();
        var sensor = m.Contains("tool_end") || m.Contains("tool end") ? Afc.Culprit.Post
            : m.Contains("tool_start") || m.Contains("tool start") || m.Contains("toolhead") || m.Contains("extruder") ? Afc.Culprit.Pre
            : m.Contains("hub") ? Afc.Culprit.Hub
            : m.Contains("prep") ? Afc.Culprit.Prep
            : lane != null && m.Contains("load") ? Afc.Culprit.Load
            : Afc.Culprit.None;
        return (sensor, lane);
    }

    /// <summary>The filament's colour, or null when AFC has none (or not a colour).</summary>
    public static Color? ColorOf(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !hex.All(Uri.IsHexDigit))
            return null;
        uint v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length == 6 ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                               : Color.FromRgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8)); // RRGGBBAA
    }

    /// <summary>Too dark to see on the panel by itself: drawn with a grey edge.</summary>
    public static bool Dark(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 40;
}

/// <summary>
/// The AFC page: the unit drawn top to bottom the way filament travels, from each lane's spool through
/// its Prep and Load sensors to the hub, the buffer and the toolhead's Pre and Post sensors. The path
/// is drawn in the colour of the filament in it, sensors green when they see filament. Hovering a lane
/// shows Load and Eject, hovering the toolhead Unload; Resume shows on an AFC error. Each needs a
/// second click. Refreshed once a second while the page shows.
/// </summary>
public partial class AfcPanel : ModulePanel
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1), ConfirmTime = TimeSpan.FromSeconds(4),
                                     NoteTime = TimeSpan.FromSeconds(8);

    private static readonly Brush Fg = Frozen("#DADADA"), Dim = Frozen("#8A8A8A"), Blue = Frozen("#60CDFF"), Amber = Frozen("#E3B341"),
        Green = Frozen("#3FB950"), Open = Frozen("#5A5A5A"), Hole = Frozen("#161616"), Track = Frozen("#2C2C2C"),
        Edge = Frozen("#5C5C5C"), Box = Frozen("#1F1F1F"), BoxLine = Frozen("#3A3A3A"), CardLine = Frozen("#2E2E2E"),
        InUseLine = Frozen("#2F5D75"), Badge = Frozen("#333333"), BadgeInUse = Blue, BadgeText = Frozen("#002033"),
        Bypassed = Frozen("#D0D0D0"), NoColor = Frozen("#777777"), Tool = Frozen("#1C1C1C"), ToolLine = Frozen("#333333"),
        ToolHover = Frozen("#4A4A4A"), Nozzle = Frozen("#9A9A9A"), Faint = Frozen("#6A6A6A");

    private const double Hx = 200, HubY = 166, Width_ = 400, TallHeight = 348;
    // The wide drawing: lanes on the left, the path running right to the toolhead.
    private const double WideWidth = 620;

    private readonly AfcSource _source;
    private readonly IMoonrakerApi _printer;
    private readonly IPrinterBackend _backend;
    private readonly CancellationToken _lifetime;
    private volatile bool _wake = true;
    private PrinterStatus _status = PrinterStatus.Connecting;
    private AfcState? _state;
    private string? _error;
    // The button waiting for its second click ("load lane1", "eject lane1", "unload", "resume"), and the one running.
    private string? _armed, _acting;
    private readonly DispatcherTimer _disarm = new() { Interval = ConfirmTime };
    // A command's failure, shown in place of AFC's message for a while.
    private string? _note;
    private readonly DispatcherTimer _noteTimer = new() { Interval = NoteTime };

    // The drawing, made for the state's shape (lanes, hubs, sensors) and updated in place.
    private string _shape = "";
    // Drawn left to right: the panel is too low for the drawing top to bottom (under the webcam, say).
    private bool _wide;
    private readonly List<LaneView> _lanes = new();
    private readonly Dictionary<string, (Ellipse Dot, Segment Out)> _hubs = new();
    private Segment? _hubOut, _bufferOut, _toPre, _toPost, _join;
    private Path? _bypassBranch;
    private TextBlock? _bypassLabel, _bufferState;
    private Ellipse? _pre, _post;
    private TextBlock? _preLabel, _postLabel;
    private Rectangle? _toolBox;
    private Canvas? _toolGroup;
    private Button? _unload;

    public AfcPanel(AfcSource source, ModuleContext context)
    {
        InitializeComponent();
        _source = source;
        _backend = context.Printer;
        _printer = (IMoonrakerApi)context.Printer;
        _lifetime = context.Lifetime;
        _disarm.Tick += (_, _) => Disarm();
        _noteTimer.Tick += (_, _) =>
        {
            _noteTimer.Stop();
            _note = null;
            Update();
        };
        SizeChanged += (_, _) => Fit();
        Update();
        _ = Task.Run(() => LoopAsync(_lifetime));
    }

    public override void OnPopupShown() => _wake = true;

    public override void OnSelected(bool selected)
    {
        if (selected)
            _wake = true;
        else
            Disarm();
    }

    public override void OnStatus(PrinterStatus status)
    {
        bool changed = status.State != _status.State || status.Halted != _status.Halted;
        _status = status;
        if (changed && IsVisible)
        {
            if (Printing)
                Disarm();
            Update();
        }
    }

    private bool Printing => _status.State == "printing";
    private bool Down => _status.State == "offline" || _status.Halted != null;

    // ---- refreshing ------------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        var since = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            bool showing = await Dispatcher.InvokeAsync(() => IsVisible);
            if (!_wake && !(showing && since.Elapsed >= Every))
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            _wake = false;
            since.Restart();
            try
            {
                var state = await _source.GetAsync(Every - TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => { _state = state; _error = null; Update(); });
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                string message = e is InvalidOperationException ? "No AFC" : "Offline";
                await Dispatcher.InvokeAsync(() => { _error = message; Update(); });
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- the header and the message -------------------------------------------------------------

    private void Update()
    {
        var state = _state;
        UnitText.Text = state is { Units.Count: > 0 } ? state.UnitTitle : "AFC";
        bool down = Down || _error != null;
        StateText.Text = _status.Halted != null ? "Shutdown" : _status.State == "offline" ? "Offline" : _error
                         ?? state?.Describe(_status.State is "printing" or "paused") ?? "";
        StateText.Foreground = down ? Dim : state is { Error: true } ? Amber : state is { Busy: true } ? Blue : Fg;

        string message = _note ?? (down ? "" : state?.Problem ?? "");
        MessageText.Text = message;
        bool resume = !down && state is { Error: true };
        ResumeButton.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
        MessageRow.Visibility = message.Length > 0 || resume ? Visibility.Visible : Visibility.Collapsed;
        Arm(ResumeButton, "resume", Why("resume"));

        if (state == null)
        {
            Drawing.Children.Clear();
            _shape = "";
            return;
        }
        string shape = Shape(state);
        if (shape != _shape)
        {
            Build(state);
            _shape = shape;
        }
        Paint(state);
        // Offline or shut down: the last drawing, greyed.
        Drawing.Opacity = down ? 0.45 : 1;
    }

    /// <summary>
    /// Top to bottom if it fits the panel's height, else left to right when the panel is wide enough
    /// for that, or at least wider (for its height) than the drawing top to bottom is.
    /// </summary>
    private void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;
        double room = ActualHeight - Header.ActualHeight - Header.Margin.Top - Header.Margin.Bottom - 10;
        bool wide = room < TallHeight && (ActualWidth >= WideWidth || ActualWidth / Math.Max(room, 1) > Width_ / TallHeight);
        if (wide == _wide)
            return;
        _wide = wide;
        _shape = "";
        Update();
    }

    /// <summary>What the drawing is made of: it's made again only when this changes (Klipper's config did, or the layout).</summary>
    private string Shape(AfcState s) => _wide + "|" + string.Join("|",
        string.Join(",", s.Lanes.Select(l => l.Name + ">" + l.Hub)), string.Join(",", s.Hubs.Select(h => h.Name)),
        s.Buffers.Count, s.Extruders.FirstOrDefault() is { } x ? $"{x.Pre != null}{x.Post != null}" : "-", s.HasBypass);

    /// <summary>Why a button can't run now, or null if it can.</summary>
    private string? Why(string action)
    {
        if (_status.Halted != null)
            return "Klipper shut down";
        if (_status.State == "offline" || _error != null || _state is not { } state)
            return "Offline";
        if (Printing)
            return "Printing";
        if (_acting != null)
            return "Busy";
        string[] parts = action.Split(' ');
        var lane = parts.Length > 1 ? state.Lane(parts[1]) : null;
        return parts[0] switch
        {
            "load" when lane!.Empty => "Empty",
            "load" when state.Loaded == lane => "Loaded",
            "load" when state.Bypass => "Bypass",
            "eject" when lane!.Empty => "Empty",
            "eject" when state.Loaded == lane => "Loaded",
            "unload" when state.Loaded == null => "Unloaded",
            _ => null,
        };
    }

    private void Arm(Button button, string action, string? why)
    {
        button.IsEnabled = why == null;
        button.ToolTip = why;
        button.Tag = _armed == action && why == null ? "armed" : null;
    }

    private void Disarm()
    {
        _disarm.Stop();
        if (_armed == null)
            return;
        _armed = null;
        Update();
    }

    private void OnResumeClick(object sender, RoutedEventArgs e) => Click("resume");

    private async void Click(string action)
    {
        if (Why(action) != null || _state is not { } state)
            return;
        // Each moves filament (or carries on a paused print): a second click.
        if (_armed != action)
        {
            _armed = action;
            _disarm.Stop();
            _disarm.Start();
            Update();
            return;
        }
        _disarm.Stop();
        _armed = null;
        string[] parts = action.Split(' ');
        string? lane = parts.Length > 1 ? parts[1] : state.Loaded?.Name;
        if (lane == null && parts[0] != "resume")
            return;
        (string script, string verb) = parts[0] switch
        {
            "load" => (AfcApi.LoadScript(lane!), "Load"),
            "eject" => (AfcApi.EjectScript(lane!), "Eject"),
            "unload" => (AfcApi.UnloadScript(lane!), "Unload"),
            _ => (AfcApi.ResumeScript, "Resume"),
        };
        _acting = action;
        ShowNote(null);
        Update();
        try { await _printer.RunGcodeAsync(script, _lifetime); }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            ShowNote($"{verb} failed: " + (ex is GcodeException ? ex.Message : _backend.DescribeError(ex)));
        }
        catch (OperationCanceledException) { return; }
        finally { _acting = null; }
        _wake = true;
        Update();
    }

    private void ShowNote(string? note)
    {
        _note = note;
        _noteTimer.Stop();
        if (note != null)
            _noteTimer.Start();
    }

    // ---- the drawing -----------------------------------------------------------------------------

    /// <summary>A lane: its card (spool, tool, name, buttons), its path to the hub and its two sensors.</summary>
    private sealed class LaneView
    {
        public required string Name;
        public required double X;
        public required Grid Card;
        public required Rectangle Frame;
        public required SpoolRing Ring;
        public required Ellipse RingEdge;
        public required Border Badge;
        public required TextBlock BadgeText, Title, Sub;
        public required StackPanel Buttons;
        public required Button Load, Eject;
        public required Segment Top, Curve;
        public required Ellipse Prep, LoadDot;
        public required TextBlock? PrepLabel, LoadLabel;
        public required FrameworkElement Sensors;
    }

    /// <summary>A stretch of the path: grey when empty, else the filament's colour (a dark one with a grey edge).</summary>
    private sealed class Segment(Path edge, Path main)
    {
        public void Set(Brush? color, bool dark = false)
        {
            main.Stroke = color ?? Track;
            main.StrokeThickness = color == null ? 3 : 3.5;
            edge.Visibility = color != null && dark ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Set(AfcLane? lane) => Set(lane == null ? null : LaneBrush(lane), lane != null && LaneDark(lane));
    }

    private static Brush LaneBrush(AfcLane lane) => AfcLook.ColorOf(lane.Color) is { } c ? Frozen(c) : NoColor;
    private static bool LaneDark(AfcLane lane) => AfcLook.ColorOf(lane.Color) is { } c && AfcLook.Dark(c);

    private void Build(AfcState state)
    {
        Drawing.Children.Clear();
        _lanes.Clear();
        _hubs.Clear();
        if (_wide)
        {
            BuildWide(state);
            return;
        }
        (Drawing.Width, Drawing.Height) = (Width_, TallHeight);
        int n = Math.Max(state.Lanes.Count, 1);
        const double gap = 6, left = 10;
        double w = (Width_ - 2 * left - gap * (n - 1)) / n;
        bool compact = w < 150, labels = w >= 70;

        // Hubs: one in the middle, or each over its lanes.
        var hubX = new Dictionary<string, double>();
        for (int i = 0; i < state.Hubs.Count; i++)
        {
            var mine = state.Lanes.Select((l, j) => (l, j)).Where(p => p.l.Hub == state.Hubs[i].Name).Select(p => left + p.j * (w + gap) + w / 2).ToList();
            hubX[state.Hubs[i].Name] = state.Hubs.Count == 1 || mine.Count == 0 ? Hx : mine.Average();
        }

        // Lanes: card → Prep → Load → their hub.
        for (int i = 0; i < state.Lanes.Count; i++)
        {
            var lane = state.Lanes[i];
            double x = left + i * (w + gap), cx = Math.Round(x + w / 2);
            double hx = hubX.TryGetValue(lane.Hub, out double h) ? h : hubX.Values.DefaultIfEmpty(Hx).First();
            var top = Seg($"M{F(cx)},67 L{F(cx)},120");
            var curve = Math.Abs(cx - hx) < 1
                ? Seg($"M{F(cx)},120 L{F(cx)},{F(HubY - 12)}")
                : Seg($"M{F(cx)},120 L{F(cx)},134 Q{F(cx)},150 {F((cx + hx) / 2)},152 Q{F(hx)},154 {F(hx)},{F(HubY - 12)}");
            bool right = cx < Hx - 1 || state.Lanes.Count == 1;
            var sensors = new Canvas();
            var (prep, prepLabel) = Sensor(sensors, cx, 90, "Prep", right, labels, lane.Name + "_prep");
            var (load, loadLabel) = Sensor(sensors, cx, 114, "Load", right, labels, lane.Name + "_load");
            Drawing.Children.Add(sensors);
            var view = Card(lane, x, w, compact);
            (view.Top, view.Curve, view.Prep, view.LoadDot, view.PrepLabel, view.LoadLabel, view.Sensors, view.X) =
                (top, curve, prep, load, prepLabel, loadLabel, sensors, cx);
            _lanes.Add(view);
        }

        // Hubs and their way on to the buffer.
        foreach (var hub in state.Hubs)
        {
            double x = hubX[hub.Name];
            var output = Math.Abs(x - Hx) < 1 ? Seg($"M{F(Hx)},{F(HubY + 12)} L{F(Hx)},210")
                : Seg($"M{F(x)},{F(HubY + 12)} L{F(x)},190 Q{F(x)},200 {F((x + Hx) / 2)},201 Q{F(Hx)},202 {F(Hx)},210");
            Add(new Rectangle { Width = 68, Height = 24, RadiusX = 4, RadiusY = 4, Fill = Box, Stroke = BoxLine }, x - 34, HubY - 12);
            Add(MakeText("HUB", 10.5, Fg, bold: true, spacing: true), x - 26, HubY - 7.5);
            var dot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.5, ToolTip = Tip(hub.Sensor) };
            Add(dot, x + 20 - 4.5, HubY - 4.5);
            _hubs[hub.Name] = (dot, output);
        }
        _hubOut = state.Hubs.Count == 0 ? Seg($"M{F(Hx)},{F(HubY - 12)} L{F(Hx)},210") : null;

        // The buffer.
        if (state.Buffers.Count > 0)
        {
            Add(new Rectangle { Width = 68, Height = 24, RadiusX = 4, RadiusY = 4, Fill = Box, Stroke = BoxLine }, Hx - 34, 210);
            var name = MakeText("BUFFER", 10.5, Fg, bold: true, spacing: true);
            name.Width = 68;
            name.TextAlignment = TextAlignment.Center;
            name.ToolTip = Tip(state.Buffers[0].Name);
            Add(name, Hx - 34, 214.5);
            _bufferState = MakeText("", 11, Dim);
            Add(_bufferState, Hx + 44, 213.5);
            _bufferOut = Seg($"M{F(Hx)},234 L{F(Hx)},262");
        }
        else
        {
            _bufferState = null;
            _bufferOut = Seg($"M{F(Hx)},210 L{F(Hx)},262");
        }

        // Bypass: a thin branch joining under the buffer, only on a printer with a bypass sensor.
        if (state.HasBypass)
        {
            _bypassBranch = new Path
            {
                Data = Geometry.Parse($"M360,250 L{F(Hx + 6)},250 Q{F(Hx)},250 {F(Hx)},256"),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            Drawing.Children.Add(_bypassBranch);
            _bypassLabel = MakeText("Bypass", 10.5, Faint);
            _bypassLabel.Width = 60;
            _bypassLabel.TextAlignment = TextAlignment.Right;
            Add(_bypassLabel, 302, 234);
            _join = Seg($"M{F(Hx)},256 L{F(Hx)},262");
        }
        else
            (_bypassBranch, _bypassLabel, _join) = (null, null, null);

        // The toolhead, its sensors and the nozzle; hovering it shows Unload.
        var tool = state.Extruders.FirstOrDefault();
        const double gx = Hx - 80, gy = 256;
        _toolGroup = new Canvas { Width = 200, Height = 88, Background = Brushes.Transparent };
        Canvas.SetLeft(_toolGroup, gx);
        Canvas.SetTop(_toolGroup, gy);
        Drawing.Children.Add(_toolGroup);
        _toolBox = new Rectangle { Width = 60, Height = 78, RadiusX = 6, RadiusY = 6, Fill = Tool, Stroke = ToolLine };
        Canvas.SetLeft(_toolBox, 50);
        Canvas.SetTop(_toolBox, 6);
        _toolGroup.Children.Add(_toolBox);
        _toPre = Seg("M80,6 L80,40", _toolGroup);
        _toPost = Seg("M80,40 L80,70", _toolGroup);
        _toolGroup.Children.Add(new Path { Data = Geometry.Parse("M71,70 L89,70 L83,80 L77,80 Z"), Fill = Nozzle });
        (_pre, _preLabel) = tool?.Pre != null ? Sensor(_toolGroup, 80, 24, "Pre", false, true, tool.PreSensor) : (null, null);
        (_post, _postLabel) = tool?.Post != null ? Sensor(_toolGroup, 80, 50, "Post", false, true, tool.PostSensor) : (null, null);
        _unload = MakeButton("Unload", "unload");
        Canvas.SetLeft(_unload, 122);
        Canvas.SetTop(_unload, 34);
        _toolGroup.Children.Add(_unload);
        _toolGroup.MouseEnter += (_, _) => Update();
        _toolGroup.MouseLeave += (_, _) => Update();
    }

    /// <summary>
    /// The same drawing left to right: the lanes stacked on the left, each through its Prep and Load
    /// sensors to the hub, then the buffer, the bypass joining from above, and the toolhead on the right.
    /// </summary>
    private void BuildWide(AfcState state)
    {
        int n = Math.Max(state.Lanes.Count, 1);
        const double cardW = 186, cardH = 62, gap = 6, left = 10;
        double lanesH = n * cardH + (n - 1) * gap;
        double height = Math.Max(lanesH + 10, 110), mid = Math.Round(height / 2), top = (height - lanesH) / 2;
        (Drawing.Width, Drawing.Height) = (WideWidth, height);
        const double laneEnd = left + cardW, hubX = 296, bufferX = 400, joinX = 492, toolX = 512;

        // Lanes: card → Prep → Load → the hub.
        for (int i = 0; i < state.Lanes.Count; i++)
        {
            var lane = state.Lanes[i];
            double y = top + i * (cardH + gap), cy = Math.Round(y + cardH / 2);
            var line = Seg($"M{F(laneEnd)},{F(cy)} L268,{F(cy)}");
            var curve = Math.Abs(cy - mid) < 1
                ? Seg($"M268,{F(cy)} L{F(hubX)},{F(mid)}")
                : Seg($"M268,{F(cy)} C284,{F(cy)} 280,{F(mid)} {F(hubX)},{F(mid)}");
            var sensors = new Canvas();
            var (prep, prepLabel) = Sensor(sensors, 222, cy, "Prep", true, true, lane.Name + "_prep");
            var (load, loadLabel) = Sensor(sensors, 250, cy, "Load", true, true, lane.Name + "_load");
            Above(prepLabel, 222, cy);
            Above(loadLabel, 250, cy);
            Drawing.Children.Add(sensors);
            var view = Card(lane, left, cardW, compact: false, y + 0.5);
            (view.Top, view.Curve, view.Prep, view.LoadDot, view.PrepLabel, view.LoadLabel, view.Sensors, view.X) =
                (line, curve, prep, load, prepLabel, loadLabel, sensors, mid);
            _lanes.Add(view);
        }

        // The hub (several share the one box: drawn as the first), on to the buffer.
        foreach (var hub in state.Hubs)
        {
            var output = Seg($"M{F(hubX + 68)},{F(mid)} L{F(bufferX)},{F(mid)}");
            Add(new Rectangle { Width = 68, Height = 24, RadiusX = 4, RadiusY = 4, Fill = Box, Stroke = BoxLine }, hubX, mid - 12);
            Add(MakeText("HUB", 10.5, Fg, bold: true, spacing: true), hubX + 8, mid - 7.5);
            var dot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.5, ToolTip = Tip(hub.Sensor) };
            Add(dot, hubX + 54 - 4.5, mid - 4.5);
            _hubs[hub.Name] = (dot, output);
        }
        _hubOut = state.Hubs.Count == 0 ? Seg($"M{F(hubX)},{F(mid)} L{F(bufferX)},{F(mid)}") : null;

        // The buffer, its state under it.
        double bufferEnd = state.HasBypass ? joinX : toolX;
        if (state.Buffers.Count > 0)
        {
            Add(new Rectangle { Width = 68, Height = 24, RadiusX = 4, RadiusY = 4, Fill = Box, Stroke = BoxLine }, bufferX, mid - 12);
            var name = MakeText("BUFFER", 10.5, Fg, bold: true, spacing: true);
            name.Width = 68;
            name.TextAlignment = TextAlignment.Center;
            name.ToolTip = Tip(state.Buffers[0].Name);
            Add(name, bufferX, mid - 7.5);
            _bufferState = MakeText("", 11, Dim);
            _bufferState.Width = 68;
            _bufferState.TextAlignment = TextAlignment.Center;
            Add(_bufferState, bufferX, mid + 15);
            _bufferOut = Seg($"M{F(bufferX + 68)},{F(mid)} L{F(bufferEnd)},{F(mid)}");
        }
        else
        {
            _bufferState = null;
            _bufferOut = Seg($"M{F(bufferX)},{F(mid)} L{F(bufferEnd)},{F(mid)}");
        }

        // Bypass: a thin branch coming down to join before the toolhead.
        if (state.HasBypass)
        {
            _bypassBranch = new Path
            {
                Data = Geometry.Parse($"M{F(joinX)},{F(mid - 40)} L{F(joinX)},{F(mid - 6)} Q{F(joinX)},{F(mid)} {F(joinX + 6)},{F(mid)}"),
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            Drawing.Children.Add(_bypassBranch);
            _bypassLabel = MakeText("Bypass", 10.5, Faint);
            _bypassLabel.Width = 60;
            _bypassLabel.TextAlignment = TextAlignment.Center;
            Add(_bypassLabel, joinX - 30, mid - 56);
            _join = Seg($"M{F(joinX + 6)},{F(mid)} L{F(toolX)},{F(mid)}");
        }
        else
            (_bypassBranch, _bypassLabel, _join) = (null, null, null);

        // The toolhead, lying down: in from the left, its sensors, the nozzle on the right; Unload under it.
        var tool = state.Extruders.FirstOrDefault();
        _toolGroup = new Canvas { Width = 100, Height = 74, Background = Brushes.Transparent };
        Canvas.SetLeft(_toolGroup, toolX);
        Canvas.SetTop(_toolGroup, mid - 22);
        Drawing.Children.Add(_toolGroup);
        _toolBox = new Rectangle { Width = 96, Height = 44, RadiusX = 6, RadiusY = 6, Fill = Tool, Stroke = ToolLine };
        _toolGroup.Children.Add(_toolBox);
        _toPre = Seg("M0,22 L44,22", _toolGroup);
        _toPost = Seg("M44,22 L76,22", _toolGroup);
        _toolGroup.Children.Add(new Path { Data = Geometry.Parse("M76,13 L76,31 L86,25 L86,19 Z"), Fill = Nozzle });
        (_pre, _preLabel) = tool?.Pre != null ? Sensor(_toolGroup, 28, 22, "Pre", true, true, tool.PreSensor) : (null, null);
        (_post, _postLabel) = tool?.Post != null ? Sensor(_toolGroup, 58, 22, "Post", true, true, tool.PostSensor) : (null, null);
        Above(_preLabel, 28, 22);
        Above(_postLabel, 58, 22);
        _unload = MakeButton("Unload", "unload");
        Canvas.SetLeft(_unload, 0);
        Canvas.SetTop(_unload, 50);
        _toolGroup.Children.Add(_unload);
        _toolGroup.MouseEnter += (_, _) => Update();
        _toolGroup.MouseLeave += (_, _) => Update();
    }

    /// <summary>A sensor's name over its dot at (x, y), in the drawing left to right.</summary>
    private static void Above(TextBlock? label, double x, double y)
    {
        if (label == null)
            return;
        label.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(label, x - label.Width / 2);
        Canvas.SetTop(label, y - 22);
    }

    private LaneView Card(AfcLane lane, double x, double w, bool compact, double y = 4.5)
    {
        var card = new Grid { Width = w, Height = 62, Background = Brushes.Transparent, ClipToBounds = true };
        var frame = new Rectangle { RadiusX = 5, RadiusY = 5, StrokeThickness = 1 };
        card.Children.Add(frame);
        double size = compact ? 22 : 36;
        var ring = new SpoolRing { Width = size, Height = size, Hub = Box };
        // A dark spool gets a grey rim, to show on the dark card.
        var ringEdge = new Ellipse { Width = size + 4, Height = size + 4, Stroke = ToolHover, StrokeThickness = 1 };
        var ringBox = new Grid { Width = size + 4, Height = size + 4, VerticalAlignment = VerticalAlignment.Center };
        ringBox.Children.Add(ringEdge);
        ringBox.Children.Add(new Border { Width = size, Height = size, Child = ring });
        var badgeText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var badge = new Border { CornerRadius = new CornerRadius(3), Height = 16, MinWidth = 24, Padding = new Thickness(4, 0, 4, 0),
                                 Child = badgeText, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontSize = compact ? 11.5 : 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { FontSize = 11, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis };
        var load = MakeButton("Load", "load " + lane.Name);
        var eject = MakeButton("Eject", "eject " + lane.Name);
        eject.Margin = new Thickness(4, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        buttons.Children.Add(load);
        buttons.Children.Add(eject);
        if (compact)
        {
            // Narrow cards (three lanes or more): spool and tool on top, the name, then the grams or the buttons.
            var stack = new StackPanel { Margin = new Thickness(7, 6, 6, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(ringBox);
            badge.Margin = new Thickness(6, 0, 0, 0);
            head.Children.Add(badge);
            stack.Children.Add(head);
            title.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(title);
            var bottom = new Grid { Height = 20 };
            bottom.Children.Add(sub);
            bottom.Children.Add(buttons);
            sub.VerticalAlignment = VerticalAlignment.Top;
            buttons.VerticalAlignment = VerticalAlignment.Top;
            stack.Children.Add(bottom);
            card.Children.Add(stack);
        }
        else
        {
            var row = new Grid { Margin = new Thickness(10, 0, 8, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            ringBox.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(ringBox);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1);
            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(badge, Dock.Left);
            head.Children.Add(badge);
            title.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(title);
            right.Children.Add(head);
            var bottom = new Grid { Height = 20, Margin = new Thickness(0, 5, 0, 0) };
            bottom.Children.Add(sub);
            bottom.Children.Add(buttons);
            sub.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(bottom);
            row.Children.Add(right);
            card.Children.Add(row);
        }
        card.MouseEnter += (_, _) => Update();
        card.MouseLeave += (_, _) => Update();
        Canvas.SetLeft(card, x + 0.5);
        Canvas.SetTop(card, y);
        Drawing.Children.Add(card);
        return new LaneView
        {
            Name = lane.Name, X = x, Card = card, Frame = frame, Ring = ring, RingEdge = ringEdge, Badge = badge, BadgeText = badgeText, Title = title,
            Sub = sub, Buttons = buttons, Load = load, Eject = eject,
            Top = null!, Curve = null!, Prep = null!, LoadDot = null!, PrepLabel = null, LoadLabel = null, Sensors = null!,
        };
    }

    private void Paint(AfcState state)
    {
        var path = AfcLook.PathLane(state);
        var (culprit, culpritLane) = AfcLook.Blame(state);
        var tool = state.Extruders.FirstOrDefault();
        double dimmed = state.Bypass ? 0.45 : 1;

        foreach (var view in _lanes)
        {
            if (state.Lane(view.Name) is not { } lane)
                continue;
            bool inUse = state.Loaded == lane;
            view.Card.Opacity = view.Sensors.Opacity = dimmed;
            view.Frame.Fill = lane.Empty ? Brushes.Transparent : Box;
            view.Frame.Stroke = inUse ? InUseLine : lane.Empty ? BoxLine : CardLine;
            view.Frame.StrokeDashArray = lane.Empty ? new DoubleCollection { 4, 3 } : null;
            view.Ring.Colors = lane.Empty || AfcLook.ColorOf(lane.Color) == null ? [] : [lane.Color.TrimStart('#')];
            view.Ring.Fraction = lane.Empty ? 0 : lane.Fraction ?? double.NaN;
            view.RingEdge.Visibility = !lane.Empty && LaneDark(lane) ? Visibility.Visible : Visibility.Hidden;
            view.Badge.Background = inUse ? BadgeInUse : Badge;
            view.BadgeText.Foreground = inUse ? BadgeText : Fg;
            view.BadgeText.Text = lane.Title;
            view.Title.Text = lane.Empty ? "Empty" : lane.Filament.Length > 0 ? lane.Filament
                : lane.Material.Length > 0 ? lane.Material : lane.SpoolId == null ? "No spool" : $"Spool {lane.SpoolId}";
            view.Title.Foreground = lane.Empty || (lane.Filament.Length == 0 && lane.Material.Length == 0) ? Dim : Fg;
            view.Title.FontWeight = view.Title.Foreground == Fg ? FontWeights.SemiBold : FontWeights.Normal;
            view.Card.ToolTip = null;
            var sub = new List<string>();
            if (!lane.Empty && lane.Filament.Length > 0 && lane.Material.Length > 0)
                sub.Add(lane.Material);
            if (!lane.Empty && lane.SpoolId != null && lane.Weight is double g)
                sub.Add(Spools.Grams(g));
            view.Sub.Text = string.Join(" · ", sub);
            bool armed = _armed is { } a && a.EndsWith(" " + lane.Name, StringComparison.Ordinal);
            bool hover = (view.Card.IsMouseOver || armed) && !lane.Empty;
            view.Buttons.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            view.Sub.Visibility = hover ? Visibility.Hidden : Visibility.Visible;
            Arm(view.Load, "load " + lane.Name, Why("load " + lane.Name));
            Arm(view.Eject, "eject " + lane.Name, Why("eject " + lane.Name));

            view.Top.Set(lane.Empty ? null : lane);
            view.Curve.Set(!lane.Empty && (lane.LoadedToHub || lane.ToolLoaded || (lane == path && AfcLook.PastHub(state, lane))) ? lane : null);
            bool mine = culpritLane == lane.Name;
            Dot(view.Prep, lane.Prep, mine && culprit == Culprit.Prep, view.PrepLabel);
            Dot(view.LoadDot, lane.Load, mine && culprit == Culprit.Load, view.LoadLabel);
        }

        bool beyond = path != null && AfcLook.PastHub(state, path);
        foreach (var hub in state.Hubs)
        {
            if (!_hubs.TryGetValue(hub.Name, out var view))
                continue;
            bool warn = culprit == Culprit.Hub && (culpritLane == null || state.Lane(culpritLane)?.Hub == hub.Name);
            Dot(view.Dot, hub.State && !state.Bypass, warn, null);
            view.Out.Set(beyond && path!.Hub == hub.Name ? path : null);
        }
        _hubOut?.Set(beyond ? path : null);
        _bufferOut?.Set(beyond ? path : null);
        if (_bufferState != null)
            _bufferState.Text = state.Buffers[0] is { } buffer ? (state.Bypass ? "Off" : buffer.State) : "";

        if (_bypassBranch != null)
        {
            _bypassBranch.Stroke = state.Bypass ? Bypassed : Track;
            _bypassBranch.StrokeThickness = state.Bypass ? 3.5 : 2;
            _bypassLabel!.Foreground = state.Bypass ? Fg : Faint;
            _join!.Set(state.Bypass ? Bypassed : beyond ? LaneBrush(path!) : null, !state.Bypass && beyond && LaneDark(path!));
        }

        // In the toolhead: up to Pre, and on past Post to the nozzle.
        bool toolLoaded = path?.ToolLoaded == true || state.Loaded != null;
        bool pre = tool?.Pre ?? toolLoaded, post = tool?.Post ?? (pre && toolLoaded);
        void Into(Segment seg, bool on)
        {
            if (state.Bypass)
                seg.Set(on ? Bypassed : null);
            else
                seg.Set(on && path != null ? path : null);
        }
        Into(_toPre!, pre);
        Into(_toPost!, post);
        if (_pre != null)
            Dot(_pre, tool!.Pre == true, culprit == Culprit.Pre, _preLabel);
        if (_post != null)
            Dot(_post, tool!.Post == true, culprit == Culprit.Post, _postLabel);
        bool toolHover = (_toolGroup!.IsMouseOver || _armed == "unload") && state.Loaded != null;
        _toolBox!.Stroke = _toolGroup.IsMouseOver ? ToolHover : ToolLine;
        _unload!.Visibility = toolHover ? Visibility.Visible : Visibility.Collapsed;
        Arm(_unload, "unload", Why("unload"));
    }

    // ---- drawing helpers ------------------------------------------------------------------------

    private Button MakeButton(string text, string action)
    {
        var button = new Button { Style = (Style)FindResource("ActButton"), Content = text };
        button.Click += (_, _) => Click(action);
        return button;
    }

    /// <summary>A sensor's dot on the path at (x, y), and its short name beside it (on the right, else the left).</summary>
    private static (Ellipse Dot, TextBlock? Label) Sensor(Canvas canvas, double x, double y, string name, bool right, bool label, string klipper)
    {
        var tip = Tip(klipper);
        var dot = new Ellipse { Width = 10, Height = 10, ToolTip = tip };
        Canvas.SetLeft(dot, x - 5);
        Canvas.SetTop(dot, y - 5);
        canvas.Children.Add(dot);
        if (!label)
            return (dot, null);
        var text = MakeText(name, 10.5, Dim);
        text.ToolTip = Tip(klipper);
        text.Width = 50;
        text.TextAlignment = right ? TextAlignment.Left : TextAlignment.Right;
        Canvas.SetLeft(text, right ? x + 10 : x - 60);
        Canvas.SetTop(text, y - 7.5);
        canvas.Children.Add(text);
        return (dot, text);
    }

    /// <summary>A sensor: filled green when it sees filament, else hollow; ringed amber when an error is about it.</summary>
    private static void Dot(Ellipse dot, bool on, bool warn, TextBlock? label)
    {
        dot.Fill = on ? Green : Hole;
        dot.Stroke = warn ? Amber : on ? Hole : Open;
        dot.StrokeThickness = warn ? 2.5 : on ? 2 : 1.5;
        if (label != null)
            label.Foreground = warn ? Amber : Dim;
    }

    private static ToolTip Tip(string text) => new()
    {
        Content = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 11 },
    };

    private static TextBlock MakeText(string text, double size, Brush color, bool bold = false, bool spacing = false) => new()
    {
        Text = spacing ? string.Join(" ", text.ToCharArray()) : text,
        FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    private void Add(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Drawing.Children.Add(element);
    }

    /// <summary>A stretch of path in the drawing (or another canvas), under what's drawn after it.</summary>
    private Segment Seg(string data, Canvas? canvas = null)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        Path Line(Brush stroke, double width) => new()
        {
            Data = geometry, Stroke = stroke, StrokeThickness = width, IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var edge = Line(Edge, 5.5);
        var main = Line(Track, 3);
        (canvas ?? Drawing).Children.Add(edge);
        (canvas ?? Drawing).Children.Add(main);
        var segment = new Segment(edge, main);
        segment.Set((Brush?)null);
        return segment;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static Brush Frozen(string hex) => Frozen((Color)ColorConverter.ConvertFromString(hex));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
