using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KlipperTrayIcon.Modules;

namespace KlipperTrayIcon.UI;

/// <summary>
/// Settings' list of modules: each one with its on/off on the left; the picked one's details on
/// the right, with its settings. Edits a copy of which are on and of their settings: Done hands
/// them back to Settings, which saves them.
/// </summary>
public partial class ModulesWindow : Window
{
    /// <summary>A module's row: whether it's on, and why it can't be used with this printer (null: it can).</summary>
    public sealed class Item(IModule module, bool isOn) : INotifyPropertyChanged
    {
        public IModule Module { get; } = module;

        public bool IsOn
        {
            get => isOn;
            set { isOn = value; Changed(nameof(IsOn)); Changed(nameof(State)); }
        }

        private string? _problem;
        public string? Problem
        {
            get => _problem;
            set { _problem = value; Changed(nameof(Problem)); Changed(nameof(Available)); Changed(nameof(State)); }
        }

        public bool Available => Problem == null;
        // The line under its name in the list.
        public string State => !Available ? (IsOn ? "On, but not available here" : "Not available here") : IsOn ? "On" : "Off";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly List<Item> _items;
    private readonly Func<IModule, string?> _problem;
    // Modules' settings the user changed, by module id and setting id (as saved in config.json).
    private readonly Dictionary<string, Dictionary<string, double>> _settings;
    private readonly Dictionary<string, Dictionary<string, string>> _textSettings;
    private readonly Func<IModule, ModuleSetting, IReadOnlyList<string>> _choices;

    /// <summary>The modules turned on when Done was clicked (registry order), or null if cancelled.</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    /// <summary>The modules' settings when Done was clicked: only those that differ from their defaults.</summary>
    public Dictionary<string, Dictionary<string, double>> SettingsResult => _settings;

    /// <summary>The modules' text settings when Done was clicked, the same way.</summary>
    public Dictionary<string, Dictionary<string, string>> TextSettingsResult => _textSettings;

    /// <param name="enabled">The ids of the modules on now.</param>
    /// <param name="problem">Why a module can't be used with the printer in Settings, or null if it can.</param>
    /// <param name="settings">Modules' settings as saved (copied: they change only with Done).</param>
    /// <param name="textSettings">Modules' text settings as saved (copied too).</param>
    /// <param name="choices">A text setting's choices from the printer (<see cref="IModule.ChoicesAsync"/>); empty if none.</param>
    public ModulesWindow(IReadOnlyCollection<string> enabled, Func<IModule, string?> problem,
                         IReadOnlyDictionary<string, Dictionary<string, double>> settings,
                         IReadOnlyDictionary<string, Dictionary<string, string>>? textSettings = null,
                         Func<IModule, ModuleSetting, IReadOnlyList<string>>? choices = null)
    {
        InitializeComponent();
        _problem = problem;
        _choices = choices ?? ((_, _) => []);
        _settings = settings.ToDictionary(p => p.Key, p => new Dictionary<string, double>(p.Value));
        _textSettings = (textSettings ?? new Dictionary<string, Dictionary<string, string>>())
            .ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value));
        _items = ModuleRegistry.All.Select(m => new Item(m, enabled.Contains(m.Id))).ToList();
        Refresh();
        ModuleList.ItemsSource = _items;
        if (_items.Count > 0)
            ModuleList.SelectedIndex = 0; // never an empty right side
        else
            Details.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => (ModuleList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement ?? DoneButton).Focus();
    }

    /// <summary>Asks again why each module can't be used (Settings has heard back from the printer).</summary>
    public void Refresh()
    {
        foreach (var item in _items)
            item.Problem = _problem(item.Module);
        ShowPicked();
    }

    private void OnPicked(object sender, SelectionChangedEventArgs e) => ShowPicked();

    private void ShowPicked()
    {
        if (ModuleList.SelectedItem is not Item item)
            return;
        var m = item.Module;
        Details.DataContext = item;
        static Visibility If(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        ProblemBox.Visibility = If(item.Problem != null);
        DetailsText.Visibility = If(m.Details != m.Description);
        NeedsPanel.Visibility = If(m.Needs.Length > 0);
        AuthorText.Text = m.Author.Length > 0 ? "Made by " + m.Author : "";
        AuthorText.Visibility = If(m.Author.Length > 0);
        HelpLink.Visibility = If(m.HelpUrl != null);
        AboutPanel.Visibility = If(m.Author.Length > 0 || m.HelpUrl != null);
        SettingsList.Children.Clear();
        foreach (var setting in m.Settings)
            SettingsList.Children.Add(setting.IsText ? TextRow(m, setting) : SettingRow(m, setting));
        SettingsPanel.Visibility = If(m.Settings.Count > 0);
    }

    /// <summary>A setting's name, its number box and unit, and its hint under them.</summary>
    private UIElement SettingRow(IModule module, ModuleSetting setting)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = setting.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        double Current() => setting.ValueIn(_settings.GetValueOrDefault(module.Id));
        var box = new TextBox { Text = Number(Current()), Width = 90, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, setting.Name);
        // A number in range is kept as it's typed; anything else goes back to the last one on leaving the box.
        box.TextChanged += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
                || value < setting.Min || value > setting.Max)
                return;
            if (!_settings.TryGetValue(module.Id, out var saved))
                _settings[module.Id] = saved = [];
            if (value == setting.Default)
                saved.Remove(setting.Id);
            else
                saved[setting.Id] = value;
            if (saved.Count == 0)
                _settings.Remove(module.Id);
        };
        box.LostFocus += (_, _) => box.Text = Number(Current());
        Grid.SetColumn(box, 1);
        var unit = new TextBlock { Text = setting.Unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(unit, 2);
        grid.Children.Add(name);
        grid.Children.Add(box);
        grid.Children.Add(unit);
        return WithHint(grid, setting);
    }

    /// <summary>
    /// A text setting's name and its box (emptied: back to the default on leaving it), and its hint. With
    /// choices from the printer, the box is a dropdown of them that still takes anything typed.
    /// </summary>
    private UIElement TextRow(IModule module, ModuleSetting setting)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = setting.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        string Current() => setting.TextIn(_textSettings.GetValueOrDefault(module.Id));
        IReadOnlyList<string> choices = setting.IsMultiLine ? [] : _choices(module, setting);
        Control box = choices.Count > 0
            ? new ComboBox { IsEditable = true, ItemsSource = choices, Text = Current(), MaxDropDownHeight = 260 }
            : new TextBox { Text = Current() };
        box.VerticalAlignment = VerticalAlignment.Center;
        // Several lines: a taller box in the console's font, scrolling past its height.
        if (setting.IsMultiLine && box is TextBox lines)
        {
            lines.AcceptsReturn = true;
            lines.TextWrapping = TextWrapping.NoWrap;
            lines.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            lines.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            lines.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont");
            lines.FontSize = 12;
            lines.Height = setting.Lines * 18 + 10;
            lines.VerticalContentAlignment = VerticalAlignment.Top;
            name.VerticalAlignment = VerticalAlignment.Top;
            name.Margin = new Thickness(0, 4, 0, 0);
        }
        AutomationProperties.SetName(box, setting.Name);
        string Typed() => box is ComboBox combo ? combo.Text : ((TextBox)box).Text;
        void Keep(string typed)
        {
            string value = typed.Replace("\r\n", "\n").Trim();
            if (!_textSettings.TryGetValue(module.Id, out var saved))
                _textSettings[module.Id] = saved = [];
            // A list can be emptied; a single line emptied is its default again.
            if (value.Length == 0 && !setting.IsMultiLine || value == setting.Text)
                saved.Remove(setting.Id);
            else
                saved[setting.Id] = value;
            if (saved.Count == 0)
                _textSettings.Remove(module.Id);
        }
        void Show(string text)
        {
            if (setting.IsMultiLine)
                text = text.Replace("\n", Environment.NewLine);
            if (box is ComboBox combo)
                combo.Text = text;
            else
                ((TextBox)box).Text = text;
        }
        box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Keep(Typed())));
        if (box is ComboBox dropdown)
            dropdown.SelectionChanged += (_, _) =>
            {
                if (dropdown.SelectedItem is string picked)
                    Keep(picked);
            };
        if (setting.IsMultiLine)
            Show(Current());
        // Emptied: the default again, once the box is left.
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!box.IsKeyboardFocusWithin && box is not ComboBox { IsDropDownOpen: true } && !setting.IsMultiLine)
                Show(Current());
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(name);
        grid.Children.Add(box);
        return WithHint(grid, setting);
    }

    private UIElement WithHint(Grid grid, ModuleSetting setting)
    {
        if (setting.Hint.Length == 0)
            return grid;
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(grid);
        row.Children.Add(new TextBlock { Text = setting.Hint, Style = (Style)FindResource("Hint") });
        return row;
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if ((ModuleList.SelectedItem as Item)?.Module.HelpUrl is string url)
            Updater.OpenPage(url);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        Result = _items.Where(i => i.IsOn).Select(i => i.Module.Id).ToList();
        DialogResult = true;
    }
}
