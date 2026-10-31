using System;
using System.Collections.Generic;
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

        (config.IsBambu ? BambuRadio : KlipperRadio).IsChecked = true;
        UrlBox.Text = config.MoonrakerUrl;
        ApiKeyHidden.Password = config.MoonrakerApiKey;
        ConsoleCheck.IsChecked = config.WebcamConsole;
        HideTimelapseCheck.IsChecked = config.ConsoleHideTimelapse;
        FillSources(null, config.WebcamName, config.SecondViewName);
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

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        if (KlipperPanel == null)
            return; // still loading
        KlipperPanel.Visibility = IsBambu ? Visibility.Collapsed : Visibility.Visible;
        BambuPanel.Visibility = IsBambu ? Visibility.Visible : Visibility.Collapsed;
        TestResult.Text = "";
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
        config.PrinterType = IsBambu ? "bambu" : "klipper";
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
            config.ConsoleHideTimelapse = HideTimelapseCheck.IsChecked == true;
            config.WebcamName = Selected(CameraCombo);
            config.SecondViewName = Selected(SecondViewCombo);
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

    /// <summary>An entry in the camera lists; Name is what's saved (empty: automatic / none).</summary>
    public sealed record SourceItem(string Name, string Label)
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
            FillSources(webcams, Selected(CameraCombo), Selected(SecondViewCombo));
        }
        catch (Exception) { }
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

        Fill(CameraCombo, Items(webcams, new SourceItem("", "Automatic (the first camera)"), camera), camera);
        Fill(SecondViewCombo, Items(webcams, new SourceItem("", "None"), secondView), secondView);
        if (webcams != null)
            SourcesHint.Text = webcams.Count == 0
                ? "The printer has no cameras set up (in Mainsail/Fluidd: Settings → Webcams)."
                : "The webcam popup gets a button to switch to the second view, e.g. a remote touchscreen, which you can click on.";
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
