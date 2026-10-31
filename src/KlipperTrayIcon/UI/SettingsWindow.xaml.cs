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
        TouchscreenCheck.IsChecked = config.U1Touchscreen;
        UrlBox.Text = config.MoonrakerUrl;
        ApiKeyHidden.Password = config.MoonrakerApiKey;
        ConsoleCheck.IsChecked = config.WebcamConsole;
        FillSources(null, config.WebcamName, config.SecondViewName);
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
        if (KlipperPanel == null || TouchscreenCheck == null)
            return; // still loading
        KlipperPanel.Visibility = IsBambu ? Visibility.Collapsed : Visibility.Visible;
        BambuPanel.Visibility = IsBambu ? Visibility.Visible : Visibility.Collapsed;
        // A U1 has its touchscreen where Klipper has a second camera.
        var second = IsU1 ? Visibility.Collapsed : Visibility.Visible;
        SecondViewLabel.Visibility = SecondViewCombo.Visibility = SecondViewStreamCombo.Visibility = second;
        TouchscreenCheck.Visibility = IsU1 ? Visibility.Visible : Visibility.Collapsed;
        AddressHint.Text = IsU1
            ? "The printer's address, e.g. 192.168.1.60"
            : "The address you open Mainsail or Fluidd at, e.g. mainsail.local or 192.168.1.50";
        UpdateSourcesHint();
        TestResult.Text = "";
    }

    private void UpdateSourcesHint()
    {
        if (IsU1)
            SourcesHint.Text = "The touchscreen needs paxx12's extended firmware with Remote Screen turned on " +
                               "(http://<printer>/firmware-config). In the popup, a button switches to it; touch starts " +
                               "locked, so the screen is view-only until you unlock it.";
        else if (_webcams == null)
            SourcesHint.Text = "Test connection lists the printer's cameras and the streams each one offers. " +
                               "The webcam popup gets a button to switch to the second camera.";
        else if (_webcams.Count == 0)
            SourcesHint.Text = "The printer has no cameras set up (in Mainsail/Fluidd: Settings → Webcams).";
        else if (MoonrakerBackend.LooksLikeU1(_webcams))
            SourcesHint.Text = "This looks like a Snapmaker U1 with the extended firmware: choose U1 Extended above to use its touchscreen.";
        else
            SourcesHint.Text = "Next to each camera: how it's fetched (only what it offers is listed). " +
                               "The webcam popup gets a button to switch to the second camera.";
    }

    private void OnShowApiKeyChanged(object sender, RoutedEventArgs e) =>
        ToggleSecret(ShowApiKey.IsChecked == true, ApiKeyHidden, ApiKeyVisible);

    private void OnShowCodeChanged(object sender, RoutedEventArgs e) =>
        ToggleSecret(ShowCode.IsChecked == true, CodeHidden, CodeVisible);

    /// <summary>PasswordBox can't reveal its text, so swap it for a TextBox.</summary>
    private static void ToggleSecret(bool show, PasswordBox hidden, TextBox visible)
    {
        if (show)
        {
            visible.Text = hidden.Password;
            hidden.Visibility = Visibility.Collapsed;
            visible.Visibility = Visibility.Visible;
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
            config.WebcamName = Selected(CameraCombo);
            config.SecondViewName = Selected(SecondViewCombo);
            config.WebcamStream = Selected(CameraStreamCombo);
            config.SecondViewStream = Selected(SecondViewStreamCombo);
            config.U1Touchscreen = TouchscreenCheck.IsChecked == true;
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
        await Task.WhenAll(LoadStreamsAsync(CameraView.Main), LoadStreamsAsync(CameraView.Second));
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
        if (_reached == null || (view == CameraView.Second && camera.Length == 0))
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
        combo.IsEnabled = view == CameraView.Main || Selected(SecondViewCombo).Length > 0;
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
        Fill(SecondViewCombo, Items(webcams, new SourceItem("", "None"), secondView), secondView);
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
        TestButton.IsEnabled = false;
        TestResult.ClearValue(TextBlock.ForegroundProperty);
        TestResult.Text = "Testing…";
        var (ok, message) = await Task.Run(() => PrinterBackends.TestConnectionAsync(config));
        TestResult.Text = (ok ? "✔ " : "✖ ") + message;
        TestResult.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x2E, 0x9E, 0x4F) : Color.FromRgb(0xD1, 0x34, 0x38));
        if (ok && !config.IsBambu)
            await LoadSourcesAsync(config);
        TestButton.IsEnabled = true;
        _testing = false;
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
