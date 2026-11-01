using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.UI;

/// <summary>First-run setup and the Settings window.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _original;
    private readonly bool _firstRun;
    private bool _testing;
    // The printer's cameras once it has been reached, and the settings that reached it.
    private IReadOnlyList<WebcamInfo>? _webcams;
    private AppConfig? _reached;
    private bool _filling;
    private readonly int[] _streamRequests = new int[2]; // by CameraView: only the latest answer counts
    // The stats strip picker: the printer's other sensors (once reached), and whether the user changed
    // the tiles (if not, the default stays the default, with the chamber if the printer has one).
    private IReadOnlyList<string> _sensors = [];
    // U1 Extended: whether the printer serves its touchscreen (Remote Screen on); null until asked.
    private bool? _touchscreen;
    // The second view's entry for the U1's touchscreen (camera names can't start with ':').
    private const string Touchscreen = ":touchscreen";
    private bool _tilesChanged;

    /// <summary>The saved config, or null if the user cancelled.</summary>
    public AppConfig? Result { get; private set; }

    public SettingsWindow(AppConfig config, bool firstRun)
    {
        InitializeComponent();
        _original = config;
        _firstRun = firstRun;

        if (firstRun)
        {
            Title = "Set up Klipper Tray Icon";
            WelcomePanel.Visibility = Visibility.Visible;
            SaveButton.Content = "Start";
            CancelButton.Content = "Quit";
        }

        (config.IsBambu ? BambuRadio : config.IsU1 ? U1Radio : KlipperRadio).IsChecked = true;
        UrlBox.Text = config.MoonrakerUrl;
        ApiKeyHidden.Password = config.MoonrakerApiKey;
        ConsoleCheck.IsChecked = config.WebcamConsole;
        QueueCheck.IsChecked = config.PrintQueue;
        // Sensors already chosen stay listed even before the printer answers.
        _sensors = config.StatsTiles?.Where(StatusText.IsSensorTile).ToList() ?? [];
        FillStatsPicker(config.StatsTiles ?? StatusText.DefaultTiles(null));
        FillSources(null, config.WebcamName, config.IsU1 && config.U1Touchscreen ? Touchscreen : config.SecondViewName);
        FillStreams(CameraView.Main, null, config.WebcamStream);
        FillStreams(CameraView.Second, null, config.SecondViewStream);
        IpBox.Text = config.BambuIp;
        SerialBox.Text = config.BambuSerial;
        CodeHidden.Password = config.BambuAccessCode;
        IntervalBox.Text = config.UpdateIntervalSeconds.ToString("0");
        NotifyCheck.IsChecked = config.Notifications;
        AutostartCheck.IsChecked = firstRun || Autostart.IsEnabled;

        Loaded += (_, _) => (config.IsBambu ? (Control)IpBox : UrlBox).Focus();
        // A printer that's already set up: list its cameras straight away.
        if (!firstRun && !config.IsBambu)
            Loaded += async (_, _) => await LoadSourcesAsync(config);
    }

    // ---- form behaviour ----------------------------------------------------

    private bool IsBambu => BambuRadio.IsChecked == true;
    private bool IsU1 => U1Radio.IsChecked == true;

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        if (KlipperPanel == null || KlipperOptions == null || SecondViewCombo == null)
            return; // still loading
        KlipperPanel.Visibility = KlipperOptions.Visibility = IsBambu ? Visibility.Collapsed : Visibility.Visible;
        BambuPanel.Visibility = IsBambu ? Visibility.Visible : Visibility.Collapsed;
        // A U1 lists its touchscreen as a second view, and picks it unless something else is chosen.
        string second = Selected(SecondViewCombo);
        second = IsU1 ? (second.Length == 0 && _touchscreen != false ? Touchscreen : second) : (second == Touchscreen ? "" : second);
        FillSources(_webcams, Selected(CameraCombo), second);
        if (IsU1 && _touchscreen == null && _reached != null)
            _ = CheckTouchscreenAsync(_reached);
        AddressHint.Text = IsU1
            ? "The printer's address, e.g. 192.168.1.60"
            : "The address you open Mainsail or Fluidd at, e.g. mainsail.local or 192.168.1.50";
        UpdateSourcesHint();
        ShowTestResult(null);
    }

    private void UpdateSourcesHint()
    {
        if (_webcams == null)
            SourcesHint.Text = "Test connection lists the printer's cameras and the streams each one offers. " +
                               "The webcam popup gets a button to switch to the second view.";
        else if (_webcams.Count == 0)
            SourcesHint.Text = "The printer has no cameras set up (in Mainsail/Fluidd: Settings → Webcams).";
        else if (!IsU1 && MoonrakerBackend.LooksLikeU1(_webcams))
            SourcesHint.Text = "This looks like a Snapmaker U1 with the extended firmware: choose U1 Extended above to use its touchscreen.";
        else
            SourcesHint.Text = "Next to each camera: how it's fetched (only what it offers is listed). " +
                               "The webcam popup gets a button to switch to the second view.";
    }

    private void OnShowApiKeyChanged(object sender, RoutedEventArgs e) =>
        ToggleSecret(ShowApiKey.IsChecked == true, ApiKeyHidden, ApiKeyVisible);

    private void OnShowCodeChanged(object sender, RoutedEventArgs e) =>
        ToggleSecret(ShowCode.IsChecked == true, CodeHidden, CodeVisible);

    /// <summary>PasswordBox can't reveal its text, so swap it for a TextBox.</summary>
    /// <summary>
    /// The eye sits where a text box puts its ✕ (clear) button while typing: a secret doesn't
    /// need one, so the revealed box goes without.
    /// </summary>
    private static void HideClearButton(TextBox box)
    {
        box.ApplyTemplate();
        box.UpdateLayout();
        foreach (var button in Descendants(box).OfType<System.Windows.Controls.Primitives.ButtonBase>())
            button.Visibility = Visibility.Collapsed;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static void ToggleSecret(bool show, PasswordBox hidden, TextBox visible)
    {
        if (show)
        {
            visible.Text = hidden.Password;
            hidden.Visibility = Visibility.Collapsed;
            visible.Visibility = Visibility.Visible;
            HideClearButton(visible);
        }
        else
        {
            hidden.Password = visible.Text;
            visible.Visibility = Visibility.Collapsed;
            hidden.Visibility = Visibility.Visible;
        }
    }

    private static string Secret(PasswordBox hidden, TextBox visible) =>
        (visible.Visibility == Visibility.Visible ? visible.Text : hidden.Password).Trim();

    private void OnIntervalPreviewInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
            if (!char.IsDigit(c))
                e.Handled = true;
    }

    /// <summary>Config from the form, or null (after telling the user) if something's missing.</summary>
    private AppConfig? Collect()
    {
        var config = _original.Clone();
        config.PrinterType = IsBambu ? "bambu" : IsU1 ? "u1" : "klipper";
        if (!IsBambu)
        {
            string url = MoonrakerBackend.NormalizeUrl(UrlBox.Text);
            if (url.Length == 0)
            {
                Warn("Missing address", "Enter your printer's address.");
                return null;
            }
            config.MoonrakerUrl = url;
            config.MoonrakerApiKey = Secret(ApiKeyHidden, ApiKeyVisible);
            config.WebcamConsole = ConsoleCheck.IsChecked == true;
            config.PrintQueue = QueueCheck.IsChecked == true;
            config.WebcamName = Selected(CameraCombo);
            string second = Selected(SecondViewCombo);
            config.U1Touchscreen = IsU1 && second == Touchscreen;
            config.SecondViewName = second == Touchscreen ? "" : second;
            config.WebcamStream = Selected(CameraStreamCombo);
            config.SecondViewStream = Selected(SecondViewStreamCombo);
            if (_tilesChanged)
                config.StatsTiles = ChosenTiles();
        }
        else
        {
            string ip = IpBox.Text.Trim(), serial = SerialBox.Text.Trim().ToUpperInvariant();
            string code = Secret(CodeHidden, CodeVisible);
            var missing = new List<string>();
            if (ip.Length == 0) missing.Add("IP address");
            if (serial.Length == 0) missing.Add("serial number");
            if (code.Length == 0) missing.Add("access code");
            if (missing.Count > 0)
            {
                Warn("Missing details", "Enter the printer's " + string.Join(", ", missing) + ".");
                return null;
            }
            config.BambuIp = ip;
            config.BambuSerial = serial;
            config.BambuAccessCode = code;
        }
        config.UpdateIntervalSeconds = int.TryParse(IntervalBox.Text, out int interval) ? Math.Clamp(interval, 1, 60) : 2;
        config.Notifications = NotifyCheck.IsChecked == true;
        return config;
    }

    // ---- camera sources --------------------------------------------------------

    /// <summary>
    /// An entry in the camera lists; Name is what's saved (empty: automatic / none).
    /// Not Available: shown greyed out and can't be picked.
    /// </summary>
    public sealed record SourceItem(string Name, string Label, string Description = "", bool Available = true)
    {
        public override string ToString() => Label; // what screen readers announce
    }

    private static string Selected(ComboBox combo) => (combo.SelectedItem as SourceItem)?.Name ?? "";

    /// <summary>Asks the printer for its cameras; on failure the lists just stay as they are.</summary>
    private async Task LoadSourcesAsync(AppConfig config)
    {
        try
        {
            var webcams = await Task.Run(() => PrinterBackends.ListWebcamsAsync(config));
            (_webcams, _reached) = (webcams, config);
            FillSources(webcams, Selected(CameraCombo), Selected(SecondViewCombo));
        }
        catch (Exception) { return; }
        await Task.WhenAll(LoadStreamsAsync(CameraView.Main), LoadStreamsAsync(CameraView.Second), CheckPrintQueueAsync(config),
                           LoadSensorsAsync(config), IsU1 ? CheckTouchscreenAsync(config) : Task.CompletedTask);
    }

    /// <summary>A U1 Extended lists its touchscreen only if the printer serves it (Remote Screen turned on).</summary>
    private async Task CheckTouchscreenAsync(AppConfig config)
    {
        _touchscreen = await Task.Run(() => PrinterBackends.HasTouchscreenAsync(config));
        FillSources(_webcams, Selected(CameraCombo), Selected(SecondViewCombo));
    }

    // ---- stats strip -------------------------------------------------------------

    /// <summary>Lists the printer's other sensors as tiles to pick from; on failure the list stays as it is.</summary>
    private async Task LoadSensorsAsync(AppConfig config)
    {
        IReadOnlyList<string> sensors;
        try { sensors = await Task.Run(() => PrinterBackends.ListSensorsAsync(config)); }
        catch (Exception) { return; }
        var chosen = ChosenTiles();
        _sensors = sensors.Union(chosen.Where(StatusText.IsSensorTile)).ToList();
        // Untouched: the default, now that it's known whether there's a chamber.
        FillStatsPicker(_tilesChanged || _original.StatsTiles != null ? chosen
                        : StatusText.DefaultTiles(MoonrakerBackend.FindChamber(sensors)));
    }

    /// <summary>
    /// The strip as it'll look: six slots, left to right, each with the label it'll show. Clicking
    /// a slot picks what goes in it; picking a tile shown in another slot swaps the two.
    /// </summary>
    private void FillStatsPicker(IEnumerable<string> chosen)
    {
        _chosenTiles = chosen.Take(StatusText.MaxStatsTiles).ToList();
        StatsSlots.Children.Clear();
        for (int i = 0; i < StatusText.MaxStatsTiles; i++)
        {
            string? tile = i < _chosenTiles.Count ? _chosenTiles[i] : null;
            var label = new TextBlock
            {
                Text = tile == null ? "Add" : StatusText.TileLabel(tile),
                FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = tile == null ? 0.55 : 1,
            };
            var slot = new Button
            {
                Style = (Style)FindResource("StatsSlot"), Content = label, Tag = i,
                ToolTip = tile == null ? "Add a tile"
                    : StatusText.IsSensorTile(tile) ? $"{TileName(tile)} (Klipper's [{tile}])" : TileName(tile),
            };
            slot.Click += OnStatsSlotClick;
            StatsSlots.Children.Add(slot);
        }
        StatsHint.Text = "Click a tile to change it. Picking one shown elsewhere swaps the two."
                         + (_sensors.Count == 0 ? " Test connection lists the printer's other sensors." : "");
    }

    /// <summary>A menu of everything a slot can show: temperatures, then the print's progress, then none.</summary>
    private void OnStatsSlotClick(object sender, RoutedEventArgs e)
    {
        var slot = (Button)sender;
        int index = (int)slot.Tag;
        string? current = index < _chosenTiles.Count ? _chosenTiles[index] : null;
        var menu = new ContextMenu { PlacementTarget = slot, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string? tile)
        {
            var item = new MenuItem { Header = tile == null ? "None" : TileName(tile), IsCheckable = true,
                                     IsChecked = tile != null && tile == current };
            int elsewhere = tile == null ? -1 : _chosenTiles.IndexOf(tile);
            if (elsewhere >= 0 && elsewhere != index)
                item.InputGestureText = $"tile {elsewhere + 1}"; // picking it swaps
            item.Click += (_, _) => PickTile(index, tile);
            menu.Items.Add(item);
        }
        Add(StatusText.Nozzle);
        Add(StatusText.Bed);
        foreach (string sensor in _sensors)
            Add(sensor);
        menu.Items.Add(new Separator());
        Add(StatusText.Progress);
        Add(StatusText.Layer);
        Add(StatusText.TimeLeft);
        if (current != null)
        {
            menu.Items.Add(new Separator());
            Add(null);
        }
        menu.IsOpen = true;
    }

    private void PickTile(int index, string? tile)
    {
        var tiles = new List<string?>(_chosenTiles);
        while (tiles.Count <= index)
            tiles.Add(null);
        int elsewhere = tile == null ? -1 : tiles.IndexOf(tile);
        if (elsewhere >= 0)
            tiles[elsewhere] = tiles[index]; // swap (or just move it, from an empty slot)
        tiles[index] = tile;
        _tilesChanged = true;
        // Empty slots close up, as in the strip.
        FillStatsPicker(tiles.OfType<string>());
    }

    private static string TileName(string tile) => tile switch
    {
        StatusText.Nozzle => "Nozzle",
        StatusText.Bed => "Bed",
        StatusText.Progress => "Progress",
        StatusText.Layer => "Layer",
        StatusText.TimeLeft => "Time left",
        // "beacon_coil" -> "Beacon coil"; names in capitals ("MCU") stay as they are.
        _ => StatusText.SensorName(tile).Replace('_', ' ') is { Length: > 0 } name
             ? char.ToUpperInvariant(name[0]) + name[1..] : tile,
    };

    private List<string> _chosenTiles = [];

    private List<string> ChosenTiles() => _chosenTiles;

    /// <summary>The queue option only works if the printer's Moonraker has the job queue (it's optional).</summary>
    private async Task CheckPrintQueueAsync(AppConfig config)
    {
        bool has;
        try { has = await Task.Run(() => PrinterBackends.HasPrintQueueAsync(config)); }
        catch (Exception) { return; } // can't tell: leave it as it is
        QueueCheck.IsEnabled = has;
        QueueCheck.Content = has
            ? "Show the print queue in the webcam popup"
            : "Show the print queue in the webcam popup (this printer has no print queue: add [job_queue] to moonraker.conf)";
    }

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && _webcams != null)
            _ = LoadStreamsAsync(sender == CameraCombo ? CameraView.Main : CameraView.Second);
    }

    /// <summary>Asks the picked camera which streams it offers and lists them.</summary>
    private async Task LoadStreamsAsync(CameraView view)
    {
        string camera = Selected(view == CameraView.Main ? CameraCombo : SecondViewCombo);
        var streamCombo = view == CameraView.Main ? CameraStreamCombo : SecondViewStreamCombo;
        int request = ++_streamRequests[(int)view];
        if (_reached == null || (view == CameraView.Second && camera is "" or Touchscreen))
        {
            FillStreams(view, [], Selected(streamCombo));
            return;
        }
        streamCombo.IsEnabled = false;
        IReadOnlyList<StreamKind>? kinds;
        try { kinds = await Task.Run(() => PrinterBackends.ListStreamsAsync(_reached, camera, view)); }
        catch (Exception) { kinds = null; }
        if (request == _streamRequests[(int)view]) // not overtaken by another pick
            FillStreams(view, kinds, Selected(streamCombo));
    }

    private static string StreamDescription(StreamKind kind) => kind switch
    {
        StreamKind.Rtsp => "Compressed H.264 video over RTSP, the standard protocol for network cameras. " +
                           "Smooth, with little delay and low network use.",
        StreamKind.H264 => "The same compressed video, sent as a plain stream over HTTP. It has no timing " +
                           "information like RTSP, so RTSP is the better choice when both are offered.",
        StreamKind.Mjpeg => "A continuous series of JPEG pictures. Works with almost every camera, but uses much " +
                            "more network bandwidth, especially at high resolutions.",
        _ => "Single pictures, fetched about ten times a second. Works with any camera or screen that offers " +
             "a snapshot, at a lower frame rate.",
    };

    /// <summary>
    /// "Automatic (RTSP)" plus every stream the app supports, best first. Those
    /// the camera didn't offer are greyed out; a saved one among them stays
    /// selected, so it isn't lost (Automatic is used for it). Before the camera
    /// has been asked (kinds null or empty) all of them can be picked.
    /// </summary>
    internal static List<SourceItem> StreamItems(IReadOnlyList<StreamKind>? kinds, string selected)
    {
        bool known = kinds is { Count: > 0 };
        var items = new List<SourceItem>
        {
            known
                ? new("", $"Automatic ({StreamKinds.Label(kinds![0])})",
                      $"Uses {StreamKinds.Label(kinds[0])}, the best stream this camera offers." +
                      (kinds[0] is StreamKind.Rtsp or StreamKind.H264 ? " Switches to MJPEG if it keeps dropping." : ""))
                : new("", "Automatic", "Uses the best stream the camera offers."),
        };
        var saved = StreamKinds.Parse(selected);
        foreach (var kind in Enum.GetValues<StreamKind>())
        {
            bool offered = !known || kinds!.Contains(kind);
            string description = offered ? StreamDescription(kind)
                : kind == saved ? "Not offered by this camera, so Automatic is used instead."
                : "Not offered by this camera.";
            items.Add(new SourceItem(StreamKinds.ToConfig(kind), StreamKinds.Label(kind), description, offered));
        }
        return items;
    }

    /// <summary>Lists the streams for a view, keeping the selection. Off while the second view is None.</summary>
    private void FillStreams(CameraView view, IReadOnlyList<StreamKind>? kinds, string selected)
    {
        var combo = view == CameraView.Main ? CameraStreamCombo : SecondViewStreamCombo;
        var items = StreamItems(kinds, selected);
        combo.ItemsSource = items;
        combo.SelectedItem = items.Find(i => i.Name == (StreamKinds.Parse(selected) is { } k ? StreamKinds.ToConfig(k) : "")) ?? items[0];
        combo.IsEnabled = view == CameraView.Main || Selected(SecondViewCombo) is not ("" or Touchscreen);
    }

    /// <summary>
    /// Fills both lists, keeping the selections. A saved camera the printer
    /// didn't list (or that hasn't been asked yet) still shows, so it isn't lost.
    /// </summary>
    private void FillSources(IReadOnlyList<WebcamInfo>? webcams, string camera, string secondView)
    {
        static List<SourceItem> Items(IReadOnlyList<WebcamInfo>? webcams, SourceItem first, string selected)
        {
            var items = new List<SourceItem> { first };
            foreach (var cam in webcams ?? [])
                items.Add(new SourceItem(cam.Name, $"{cam.Name}  ·  {cam.Service}{(cam.Enabled ? "" : ", disabled")}"));
            if (selected.Length > 0 && !items.Exists(i => string.Equals(i.Name, selected, StringComparison.OrdinalIgnoreCase)))
                items.Add(new SourceItem(selected, webcams == null ? selected : $"{selected}  ·  not found on the printer"));
            return items;
        }

        static void Fill(ComboBox combo, List<SourceItem> items, string selected)
        {
            combo.ItemsSource = items;
            combo.SelectedItem = items.Find(i => string.Equals(i.Name, selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        }

        // Automatic names the camera it would pick, once the list is known.
        string automatic = webcams != null && MoonrakerBackend.PickCamera(webcams, "", CameraView.Main) is { } first
            ? $"Automatic ({first.Name})"
            : "Automatic";
        _filling = true;
        Fill(CameraCombo, Items(webcams, new SourceItem("", automatic), camera), camera);
        var second = Items(webcams, new SourceItem("", "None"), secondView == Touchscreen ? "" : secondView);
        // A U1 Extended's touchscreen, unless the printer is known not to serve it (then it's None).
        if (IsU1 && _touchscreen != false)
            second.Insert(1, new SourceItem(Touchscreen, "Touchscreen",
                "The printer's own screen, live, from the extended firmware's Remote Screen. Touch starts " +
                "locked in the popup, so it's view-only until you unlock it."));
        Fill(SecondViewCombo, second, secondView);
        _filling = false;
        UpdateSourcesHint();
    }

    private void Warn(string title, string text) =>
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- buttons ---------------------------------------------------------------

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_testing || Collect() is not { } config)
            return;
        _testing = true;
        var button = (Button)sender;
        button.IsEnabled = false;
        ShowTestResult("Testing…");
        var (ok, message) = await Task.Run(() => PrinterBackends.TestConnectionAsync(config));
        ShowTestResult((ok ? "✔ " : "✖ ") + message,
                       new SolidColorBrush(ok ? Color.FromRgb(0x2E, 0x9E, 0x4F) : Color.FromRgb(0xD1, 0x34, 0x38)));
        if (ok && !config.IsBambu)
            await LoadSourcesAsync(config);
        button.IsEnabled = true;
        _testing = false;
    }

    /// <summary>The test's result, in place of the hint under the field (null: the hint again).</summary>
    private void ShowTestResult(string? text, Brush? color = null)
    {
        var (result, hint) = IsBambu ? (BambuTestResult, BambuHint) : (TestResult, ApiKeyHint);
        foreach (var other in new[] { TestResult, BambuTestResult })
            if (other != result)
                other.Visibility = Visibility.Collapsed;
        ApiKeyHint.Visibility = BambuHint.Visibility = Visibility.Visible;
        result.Text = text ?? "";
        if (color != null)
            result.Foreground = color;
        else
            result.ClearValue(TextBlock.ForegroundProperty);
        result.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        hint.Visibility = text == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>IsCancel only closes a dialog (first run); from the tray menu the window isn't one.</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_firstRun)
            DialogResult = false;
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Collect() is not { } config)
            return;
        try
        {
            Autostart.SetEnabled(AutostartCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            Warn("Start with Windows", $"Couldn't update the startup setting:\n{ex.Message}");
        }
        Result = config;
        if (_firstRun)
            DialogResult = true; // shown with ShowDialog
        Close();
    }
}
