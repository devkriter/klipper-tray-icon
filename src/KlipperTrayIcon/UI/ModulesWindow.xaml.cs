using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KlipperTrayIcon.Modules;

namespace KlipperTrayIcon.UI;

/// <summary>
/// Settings' list of modules: each one with its on/off on the left; the picked one's details on
/// the right. Edits a copy of which are on: Done hands it back to Settings, which saves it.
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

    /// <summary>The modules turned on when Done was clicked (registry order), or null if cancelled.</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    /// <param name="enabled">The ids of the modules on now.</param>
    /// <param name="problem">Why a module can't be used with the printer in Settings, or null if it can.</param>
    public ModulesWindow(IReadOnlyCollection<string> enabled, Func<IModule, string?> problem)
    {
        InitializeComponent();
        _problem = problem;
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
    }

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
