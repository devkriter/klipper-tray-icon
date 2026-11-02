using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Spoolman;

/// <summary>
/// One of the other spools in the list. Fraction is NaN without a starting weight. The tooltip's
/// facts are lines of labels and their values.
/// </summary>
public sealed record SpoolRow(Spool Spool, string Title, string Sub, string Weight, string Length, bool Low, double Fraction,
                              string Use, bool Armed, string TipTitle, string TipId, string TipLabels, string TipValues);

/// <summary>
/// The Filament page: the spool in the printer (or one card per toolhead) with what's left and
/// whether it's enough for the print, and the other spools, most recently used first, with Use to
/// switch (a second click while printing) and a filter.
/// </summary>
public partial class SpoolmanPanel : ModulePanel
{
    private static readonly TimeSpan StateRefresh = TimeSpan.FromSeconds(5), ListRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(4);

    private readonly SpoolmanSource _source;
    private readonly IPrinterBackend _printer;
    private readonly CancellationToken _lifetime;
    private volatile bool _wake = true, _listWake = true;
    private PrinterStatus _status = PrinterStatus.Connecting;
    private volatile string _filename = "";
    private SpoolmanState? _state;
    private IReadOnlyList<Spool>? _spools;
    private string? _error, _currency, _spoolmanUrl;
    private readonly ObservableCollection<SpoolRow> _rows = new();
    // Use while printing: the spool waiting for its second click.
    private int? _armed;
    private readonly DispatcherTimer _disarm = new() { Interval = ConfirmTime };
    // The toolhead Use puts a spool in, on a printer with several (its index; -1: the one in use).
    private int _target = -1;
    private bool _switching;

    public SpoolmanPanel(SpoolmanSource source, ModuleContext context)
    {
        InitializeComponent();
        _source = source;
        _printer = context.Printer;
        _lifetime = context.Lifetime;
        SpoolList.ItemsSource = _rows;
        _disarm.Tick += (_, _) => Disarm();
        ShowCards();
        _ = Task.Run(() => LoopAsync(_lifetime));
    }

    public override void OnPopupShown() => _wake = _listWake = true;

    public override void OnSelected(bool selected)
    {
        if (selected)
            _wake = _listWake = true;
        else
            Disarm();
    }

    public override void OnStatus(PrinterStatus status)
    {
        bool printingChanged = (status.State is "printing" or "paused") != (_status.State is "printing" or "paused");
        bool redraw = printingChanged || status.Progress != _status.Progress || status.Filename != _status.Filename;
        _status = status;
        _filename = status.Filename;
        if (printingChanged)
            Disarm();
        if (redraw && _state != null && IsVisible)
            ShowCards();
    }

    // ---- refreshing ------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        var sinceState = Stopwatch.StartNew();
        var sinceList = Stopwatch.StartNew();
        bool askedUrl = false, listKnown = false;
        while (!ct.IsCancellationRequested)
        {
            bool showing = await Dispatcher.InvokeAsync(() => IsVisible);
            if (!_wake && !(showing && sinceState.Elapsed >= StateRefresh))
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            _wake = false;
            sinceState.Restart();
            try
            {
                // The tile may have just asked: then this is its answer.
                var state = await _source.GetAsync(_filename, StateRefresh - TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                IReadOnlyList<Spool>? spools = null;
                string? currency = null;
                // The list when the page is opened, then now and then; until it has come once, every time.
                if (!state.Stale && (_listWake || !listKnown || sinceList.Elapsed >= ListRefresh))
                {
                    try
                    {
                        spools = await _source.Api.GetSpoolsAsync(ct).ConfigureAwait(false);
                        currency = await _source.Api.GetCurrencyAsync(ct).ConfigureAwait(false);
                        (_listWake, listKnown) = (false, true);
                        sinceList.Restart();
                    }
                    catch (SpoolmanException) { } // gone between the two calls: the banner says so next time
                }
                string? url = null;
                if (!askedUrl)
                {
                    try { url = await _source.Api.GetSpoolmanUrlAsync(ct).ConfigureAwait(false); }
                    catch (Exception) when (!ct.IsCancellationRequested) { } // no link, then
                    askedUrl = true;
                }
                await Dispatcher.InvokeAsync(() => Show(state, spools, currency, url));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                string message = e is SpoolmanException ? "Spoolman isn't answering." : _printer.DescribeError(e);
                await Dispatcher.InvokeAsync(() => ShowError(message));
            }
        }
    }

    private void Show(SpoolmanState state, IReadOnlyList<Spool>? spools, string? currency, string? url)
    {
        (_state, _error) = (state, null);
        if (spools != null)
            (_spools, _currency) = (spools, currency);
        if (url != null)
        {
            _spoolmanUrl = url;
            OpenButton.Visibility = Visibility.Visible;
        }
        if (_target >= state.Tools.Tools.Count)
            _target = -1;
        ShowCards();
        ShowList();
    }

    private void ShowError(string message)
    {
        _error = message;
        ShowCards();
    }

    // ---- in the printer ------------------------------------------------------------------

    private void ShowCards()
    {
        var state = _state;
        // Spoolman down: Moonraker keeps counting and sends the reports later.
        string? banner = _error ?? (state is { Stale: true }
            ? state.Status.PendingReports switch
            {
                0 => "Spoolman isn't answering. Moonraker keeps counting until it's back.",
                1 => "Spoolman isn't answering. Moonraker keeps counting and will send 1 report when it's back.",
                var n => $"Spoolman isn't answering. Moonraker keeps counting and will send {n} reports when it's back.",
            }
            : null);
        Banner.Visibility = banner != null ? Visibility.Visible : Visibility.Collapsed;
        BannerText.Text = banner ?? "";
        bool connected = _error == null && state is { Stale: false };
        FootDot.Foreground = connected ? OkBrush : state == null && _error == null ? DimBrush : WarnBrush;
        FootText.Text = connected ? " Spoolman connected" : state == null && _error == null ? " Connecting…" : " Spoolman not connected";
        // The last known values stay, greyed.
        Cards.Opacity = connected || state == null ? 1 : 0.5;

        Cards.Children.Clear();
        if (state == null)
        {
            Cards.Children.Add(EmptyBox(_error == null ? "Loading…" : "Nothing to show yet.", ""));
            return;
        }
        if (state.MultiTool)
        {
            for (int i = 0; i < state.Tools.Tools.Count; i++)
                Cards.Children.Add(ToolCard(state, i));
            return;
        }
        if (state.ActiveSpool is { } spool)
            Cards.Children.Add(BigCard(spool, Spools.Check(spool, state, -1, _status)));
        else if (state.ActiveSpoolId is int missing)
            Cards.Children.Add(state.Stale
                ? EmptyBox($"Spool #{missing}.", " Its details show once Spoolman answers.")
                : EmptyBox($"Spool #{missing} isn't in Spoolman.", " Pick the spool that's loaded below, with Use."));
        else
            Cards.Children.Add(EmptyBox("No spool picked.", " Moonraker isn't tracking usage. Pick the spool that's loaded below, with Use."));
    }

    /// <summary>The spool in a printer with one toolhead: big, with a bar for what's left and a line about the print.</summary>
    private Border BigCard(Spool spool, SpoolCheck check)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(grid, Ring(spool, 46, CardBrush), 0, 0, margin: new Thickness(0, 0, 12, 0));
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(spool.Name.Length > 0 ? spool.Name : spool.Title, 14, TextBrush, bold: true));
        words.Children.Add(Text(Join(spool.Vendor, $"#{spool.Id}", spool.Location), 11.5, DimBrush));
        Add(grid, words, 1, 0);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        left.Children.Add(Text(spool.Remaining is double g ? Spools.Grams(g) : "?", 18, spool.Remaining is < Spools.LowGrams ? WarnBrush : TextBrush,
                               bold: true, right: true));
        if (spool.Length is double mm)
            left.Children.Add(Text($"{Spools.Metres(mm)} left", 11.5, DimBrush, right: true));
        Add(grid, left, 2, 0);
        if (spool.Fraction is double f)
            Add(grid, Bar(spool, f), 0, 1, span: 3, margin: new Thickness(0, 9, 0, 0));
        var lines = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var line in NeedLines(spool, check, compact: false))
            lines.Children.Add(line);
        if (lines.Children.Count > 0)
            Add(grid, lines, 0, 2, span: 3);
        return new Border
        {
            Background = CardBrush, BorderBrush = CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10), Child = grid,
        };
    }

    /// <summary>
    /// One toolhead's card, on a printer with several: its spool, small. The tool in use has a blue
    /// label; the one Use puts spools in (click a card to pick it) has a blue edge.
    /// </summary>
    private Border ToolCard(SpoolmanState state, int index)
    {
        var tool = state.Tools.Tools[index];
        bool inUse = index == state.Tools.Active;
        bool target = index == Target(state);
        var spool = tool.SpoolId is int id ? state.Spools.GetValueOrDefault(id) : null;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new Border
        {
            Background = inUse ? BlueBrush : LabelBrush, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0), MinWidth = 26,
            Child = new TextBlock
            {
                Text = tool.Name, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = inUse ? OnBlueBrush : TextBrush,
            },
            ToolTip = inUse ? "The toolhead in use" : null,
        };
        Add(grid, label, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (spool != null)
        {
            Add(grid, Ring(spool, 26, CardBrush), 1, 0, margin: new Thickness(0, 0, 9, 0));
            words.Children.Add(Text(spool.Title, 12.5, TextBrush, bold: true));
            words.Children.Add(Text(Join(inUse ? "In use" : "", spool.Material, spool.Fraction is double f ? Spools.Percent(f) : "",
                                         spool.Location), 11, DimBrush));
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            left.Children.Add(Text(spool.Remaining is double g ? Spools.Grams(g) : "?", 13,
                                   spool.Remaining is < Spools.LowGrams ? WarnBrush : TextBrush, bold: true, right: true));
            if (spool.Length is double mm)
                left.Children.Add(Text(Spools.Metres(mm), 11, DimBrush, right: true));
            Add(grid, left, 3, 0);
            var lines = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var line in NeedLines(spool, Spools.Check(spool, state, index, _status), compact: true))
                lines.Children.Add(line);
            if (lines.Children.Count > 0)
                Add(grid, lines, 1, 1, span: 3);
        }
        else
        {
            Add(grid, Ring(null, 26, CardBrush), 1, 0, margin: new Thickness(0, 0, 9, 0));
            words.Children.Add(Text(tool.SpoolId is not int missing ? "No spool"
                                    : state.Stale ? $"Spool #{missing}" : $"Spool #{missing} isn't in Spoolman", 12.5, DimBrush));
            words.Children.Add(Text(target ? "Pick one below with Use" : "Click here, then Use on a spool below", 11, DimBrush));
        }
        Add(grid, words, 2, 0);
        var card = new Border
        {
            Background = CardBrush, BorderBrush = target ? BlueBrush : CardEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4),
            Child = grid, Cursor = Cursors.Hand,
            ToolTip = target ? $"Use puts spools in {tool.Name}" : $"Click to pick {tool.Name} for Use",
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            _target = index;
            Disarm();
            ShowCards();
            ShowList();
        };
        return card;
    }

    /// <summary>The spool against the print: enough (with what's to spare), how short it is, or the wrong material.</summary>
    private static IEnumerable<TextBlock> NeedLines(Spool spool, SpoolCheck check, bool compact)
    {
        if (check.WrongMaterial is string type)
            yield return Line(WarnBrush, "⚠ ", compact ? ("Sliced for ", false) : ("This file is sliced for ", false), (type, true),
                              ($"; the spool is {spool.Material}", false));
        if (check.Needed is not double needed)
            yield break;
        if (Spools.Short(spool, check) is double missing)
            yield return compact
                ? Line(WarnBrush, "⚠ ", ("Needs ", false), (Spools.Grams(needed), true), (": ", false), ($"{Spools.Grams(missing)} short", true))
                : Line(WarnBrush, "⚠ ", ("This print needs ", false), (Spools.Grams(needed), true), (": about ", false),
                       ($"{Spools.Grams(missing)} short", true));
        else if (spool.Remaining is double left)
            yield return compact
                ? Line(DimBrush, "✓ ", ("Needs ", false), (Spools.Grams(needed), true), ($" more, {Spools.Grams(left - needed)} to spare", false))
                : Line(DimBrush, "✓ ", ("This print needs ", false), (Spools.Grams(needed), true),
                       ($" more: enough, with {Spools.Grams(left - needed)} to spare", false));
        else
            yield return Line(DimBrush, "", ("This print needs ", false), (Spools.Grams(needed), true), (" more", false));
    }

    private static TextBlock Line(Brush brush, string mark, params (string Text, bool Bold)[] parts)
    {
        var line = new TextBlock { FontSize = 12, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        if (mark.Length > 0)
            line.Inlines.Add(new Run(mark) { Foreground = brush == DimBrush ? OkBrush : brush });
        foreach (var (text, bold) in parts)
            line.Inlines.Add(new Run(text)
            {
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = bold && brush == DimBrush ? TextBrush : brush,
            });
        return line;
    }

    private Border EmptyBox(string bold, string rest)
    {
        var text = new TextBlock { FontSize = 12.5, Foreground = DimBrush, TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run(bold) { Foreground = TextBrush, FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run(rest));
        return new Border
        {
            BorderBrush = DashBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10), Child = text,
        };
    }

    private static SpoolRing Ring(Spool? spool, double size, Brush hub) => new()
    {
        Width = size, Height = size, Colors = spool?.Colors, Fraction = spool?.Fraction ?? (spool == null ? 0 : double.NaN), Hub = hub,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Grid Bar(Spool spool, double fraction)
    {
        var bar = new Grid { Height = 4 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        var fill = new Border
        {
            Background = spool.Colors.Select(SpoolRing.ToBrush).FirstOrDefault(b => b != null) ?? TextBrush, CornerRadius = new CornerRadius(2),
        };
        bar.Children.Add(new Border { Background = UsedBrush, CornerRadius = new CornerRadius(2) });
        Grid.SetColumnSpan(bar.Children[0], 2);
        bar.Children.Add(fill);
        return bar;
    }

    private static TextBlock Text(string text, double size, Brush brush, bool bold = false, bool right = false) => new()
    {
        Text = text, FontSize = size, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
    };

    private static void Add(Grid grid, UIElement child, int column, int row, int span = 1, Thickness? margin = null)
    {
        Grid.SetColumn(child, column);
        Grid.SetRow(child, row);
        Grid.SetColumnSpan(child, span);
        if (margin is { } m && child is FrameworkElement element)
            element.Margin = m;
        grid.Children.Add(child);
    }

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));

    // ---- other spools -----------------------------------------------------------------------

    /// <summary>The tool Use puts a spool in: the one picked, else the one in use (else the first).</summary>
    private int Target(SpoolmanState state) =>
        _target >= 0 ? _target : state.Tools.Active >= 0 ? state.Tools.Active : state.Tools.Tools.Count > 0 ? 0 : -1;

    private void ShowList()
    {
        var state = _state;
        if (_spools == null || state == null)
            return;
        var loaded = state.LoadedIds.ToHashSet();
        var others = _spools.Where(s => !loaded.Contains(s.Id)).ToList();
        string filter = FilterBox.Text.Trim();
        var shown = filter.Length == 0 ? others : others.Where(s => Matches(s, filter)).ToList();
        OthersHeader.Text = $"OTHER SPOOLS · {others.Count}";
        ListNote.Text = others.Count == 0 ? "No other spools in Spoolman." : shown.Count == 0 ? "No spools match." : "";
        ListNote.Visibility = ListNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        string use = state.MultiTool && Target(state) is var t and >= 0 ? $"Use in {state.Tools.Tools[t].Name}" : "Use";
        var fresh = shown.Select(s => Row(s, use)).ToList();
        // Only the rows that changed, so the one under the mouse stays put.
        for (int i = 0; i < fresh.Count; i++)
        {
            if (i < _rows.Count)
            {
                if (!Equals(_rows[i], fresh[i]))
                    _rows[i] = fresh[i];
            }
            else
                _rows.Add(fresh[i]);
        }
        while (_rows.Count > fresh.Count)
            _rows.RemoveAt(_rows.Count - 1);
    }

    /// <summary>Name, vendor, material, place or "#12" contain the filter.</summary>
    internal static bool Matches(Spool spool, string filter) =>
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            new[] { spool.Title, spool.Vendor, spool.Material, spool.Location, $"#{spool.Id}" }
                .Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));

    private SpoolRow Row(Spool s, string use)
    {
        bool armed = _armed == s.Id;
        // The tooltip: only what Spoolman has.
        var facts = new List<(string, string)>();
        if (s.Remaining is double left)
            facts.Add(("Left", s.Initial is > 0 and var start ? $"{Spools.Grams(left)} of {Spools.Grams(start)} ({Spools.Percent(left / start)})" : Spools.Grams(left)));
        if (s.Length is double mm)
            facts.Add(("Length", $"{Spools.Metres(mm)} left"));
        if (s.ExtruderTemp is double nozzle)
            facts.Add(("Print at", s.BedTemp is double bed ? $"{nozzle:0}° · bed {bed:0}°" : $"{nozzle:0}°"));
        else if (s.BedTemp is double bedOnly)
            facts.Add(("Print at", $"bed {bedOnly:0}°"));
        if (s.Location.Length > 0)
            facts.Add(("Place", s.Location));
        if (s.Lot.Length > 0)
            facts.Add(("Lot", s.Lot));
        if (s.Price is double price)
            facts.Add(("Price", Money(price) + (s.Fraction is double f && f < 1 ? $" ({Money(price * f)} left)" : "")));
        if (s.FirstUsed is { } first)
            facts.Add(("First used", first.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)));
        if (s.Comment.Length > 0)
            facts.Add(("Note", s.Comment.ReplaceLineEndings(" ")));
        return new SpoolRow(s, s.Title, Join(s.Material, s.Location, Spools.Used(s.LastUsed)),
                            s.Remaining is double g ? Spools.Grams(g) : "", s.Length is double l ? Spools.Metres(l) : "",
                            s.Remaining is < Spools.LowGrams, s.Fraction ?? double.NaN,
                            armed ? "Click again to switch" : use, armed,
                            s.Title, $"· #{s.Id}", string.Join("\n", facts.Select(f => f.Item1)), string.Join("\n", facts.Select(f => f.Item2)));
    }

    private string Money(double amount) =>
        _currency != null ? $"{amount.ToString("0.00", CultureInfo.CurrentCulture)} {_currency}" : amount.ToString("0.00", CultureInfo.CurrentCulture);

    // ---- switching -----------------------------------------------------------------------------

    private async void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpoolRow row } || _state is not { } state || _switching)
            return;
        // While printing, a second click: the rest of the print's usage goes to the new spool.
        if (_status.State is "printing" or "paused" && _armed != row.Spool.Id)
        {
            _armed = row.Spool.Id;
            _disarm.Stop();
            _disarm.Start();
            // Only the tool in use is counting this print's usage.
            int tool = Target(state);
            SwitchNote.Visibility = tool < 0 || tool == state.Tools.Active || !state.MultiTool ? Visibility.Visible : Visibility.Collapsed;
            ShowList();
            return;
        }
        Disarm();
        _switching = true;
        try
        {
            int target = Target(state);
            if (target >= 0)
                await _source.Api.SetToolSpoolAsync(state.Tools.Tools[target].Name, row.Spool.Id,
                                                    target == state.Tools.Active || !state.MultiTool, _lifetime);
            else
                await _source.Api.SetActiveSpoolAsync(row.Spool.Id, _lifetime);
            _wake = true;
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            ShowError("Couldn't switch: " + (ex is SpoolmanException ? ex.Message : _printer.DescribeError(ex)));
        }
        finally { _switching = false; }
        // Right away, not up to a refresh later (the tile too).
        await Task.Delay(300);
        _wake = true;
    }

    private void Disarm()
    {
        _disarm.Stop();
        SwitchNote.Visibility = Visibility.Collapsed;
        if (_armed == null)
            return;
        _armed = null;
        ShowList();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_spoolmanUrl == null)
            return;
        try { Process.Start(new ProcessStartInfo(_spoolmanUrl) { UseShellExecute = true }); }
        catch (Exception ex) { App.LogError("Open Spoolman", ex); }
    }

    // ---- filter -----------------------------------------------------------------------------

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (FilterBox.Visibility == Visibility.Visible)
        {
            CloseFilter();
            return;
        }
        OthersHeader.Visibility = Visibility.Collapsed;
        FilterBox.Visibility = Visibility.Visible;
        FilterBox.Focus();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        // Cleared: back to the heading.
        if (FilterBox.Text.Length == 0)
            CloseFilter();
        ShowList();
    }

    private void OnFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFilter();
            e.Handled = true;
        }
    }

    private void OnFilterLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (FilterBox.Text.Length == 0)
            CloseFilter();
    }

    private void CloseFilter()
    {
        FilterBox.Visibility = Visibility.Collapsed;
        OthersHeader.Visibility = Visibility.Visible;
        if (FilterBox.Text.Length > 0)
            FilterBox.Text = ""; // shows the whole list again
    }

    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xDA, 0xDA, 0xDA)),
                                  DimBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                                  OkBrush = Frozen(Color.FromRgb(0x3F, 0xB9, 0x50)),
                                  WarnBrush = Frozen(Color.FromRgb(0xE3, 0xB3, 0x41)),
                                  BlueBrush = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF)),
                                  OnBlueBrush = Frozen(Color.FromRgb(0x00, 0x20, 0x33)),
                                  LabelBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33)),
                                  CardBrush = Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                                  CardEdge = Frozen(Color.FromRgb(0x2E, 0x2E, 0x2E)),
                                  DashBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                                  UsedBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
