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
/// One of the other spools in the list. Fraction is NaN without a starting weight. Picking: Use was
/// clicked on a printer with several toolheads, and Picks are its toolhead buttons. The tooltip's
/// facts are lines of labels and their values.
/// </summary>
public sealed record SpoolRow(Spool Spool, string Title, string Sub, string Weight, string Length, bool Low, bool VeryLow, double Fraction,
                              bool Armed, bool Picking, IReadOnlyList<ToolPick> Picks,
                              string TipTitle, string TipId, string TipLabels, string TipValues);

/// <summary>A toolhead button under Use: puts the spool in that toolhead. Armed: waiting for its second click.</summary>
public sealed record ToolPick(int SpoolId, int Index, string Name, bool Armed);

/// <summary>
/// The Filament page: the spool in the printer (or one card per toolhead) with what's left and
/// whether it's enough for the print, Load and Unload on each card, and the other spools, most
/// recently used first, with Use to switch (a second click while printing) and a filter.
/// </summary>
public partial class SpoolmanPanel : ModulePanel
{
    private static readonly TimeSpan StateRefresh = TimeSpan.FromSeconds(5), ListRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(4);

    private readonly SpoolmanSource _source;
    private readonly IPrinterBackend _printer;
    private readonly CancellationToken _lifetime;
    private readonly Levels _levels;
    private volatile bool _wake = true, _listWake = true;
    // After a change of ours: fetched again, not the tile's answer from a moment ago.
    private volatile bool _fresh;
    private PrinterStatus _status = PrinterStatus.Connecting;
    private volatile string _filename = "";
    private SpoolmanState? _state;
    private IReadOnlyList<Spool>? _spools;
    private string? _error, _currency, _spoolmanUrl;
    private readonly ObservableCollection<SpoolRow> _rows = new();
    // Use while printing: the spool and toolhead (-1: none) waiting for the second click.
    private (int Spool, int Tool)? _armed;
    private readonly DispatcherTimer _disarm = new() { Interval = ConfirmTime };
    // With several toolheads: the spool whose toolhead buttons are open, and those buttons.
    private int? _picking;
    private IReadOnlyList<ToolPick> _picks = NoPicks;
    private static readonly IReadOnlyList<ToolPick> NoPicks = [];
    private bool _switching;
    // Load and Unload: the printer's macros, the one waiting for its second click, and the one running,
    // each with its toolhead's index (-1: none).
    private FilamentMacros _macros = FilamentMacros.None;
    private readonly string _loadMacro, _unloadMacro; // the module's settings
    private (bool Load, int Tool)? _actionArmed, _acting;

    public SpoolmanPanel(SpoolmanSource source, ModuleContext context)
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
        _source = source;
        _printer = context.Printer;
        _lifetime = context.Lifetime;
        _levels = Levels.From(context);
        (_loadMacro, _unloadMacro) = (context.Text(SpoolmanModule.LoadMacro), context.Text(SpoolmanModule.UnloadMacro));
        SpoolList.ItemsSource = _rows;
        _disarm.Tick += (_, _) =>
        {
            // Toolhead buttons stay open while the mouse is on the list.
            if (_armed == null && _actionArmed == null && _picking != null && SpoolList.IsMouseOver)
                return;
            Disarm();
        };
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
        bool redraw = printingChanged || status.State != _status.State || status.Progress != _status.Progress || status.Filename != _status.Filename;
        _status = status;
        _filename = status.Filename;
        if (printingChanged)
        {
            Disarm();
            // Load and Unload's last word is old news once a print starts.
            if (_acting == null)
                ActionNote.Visibility = Visibility.Collapsed;
        }
        if (redraw && _state != null && IsVisible)
            ShowCards();
    }

    // ---- refreshing ------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        var sinceState = Stopwatch.StartNew();
        var sinceList = Stopwatch.StartNew();
        bool askedUrl = false, listKnown = false, macrosKnown = false;
        while (!ct.IsCancellationRequested)
        {
            bool showing = await Dispatcher.InvokeAsync(() => IsVisible);
            if (!_wake && !(showing && sinceState.Elapsed >= StateRefresh))
            {
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            bool fresh = _fresh;
            (_wake, _fresh) = (false, false);
            sinceState.Restart();
            try
            {
                // The tile may have just asked: then this is its answer.
                var state = await _source.GetAsync(_filename, fresh ? TimeSpan.Zero : StateRefresh - TimeSpan.FromSeconds(1), ct)
                    .ConfigureAwait(false);
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
                FilamentMacros? macros = null;
                if (!macrosKnown)
                {
                    try { (macros, macrosKnown) = (await _source.Api.GetFilamentMacrosAsync(ct).ConfigureAwait(false), true); }
                    catch (Exception) when (!ct.IsCancellationRequested) { } // asked again next time
                }
                await Dispatcher.InvokeAsync(() => Show(state, spools, currency, url, macros));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                string message = e is SpoolmanException ? "Spoolman offline" : _printer.DescribeError(e);
                await Dispatcher.InvokeAsync(() => ShowError(message));
            }
        }
    }

    private void Show(SpoolmanState state, IReadOnlyList<Spool>? spools, string? currency, string? url, FilamentMacros? macros)
    {
        (_state, _error) = (state, null);
        if (macros != null)
            _macros = macros;
        if (spools != null)
            (_spools, _currency) = (spools, currency);
        if (url != null)
        {
            _spoolmanUrl = url;
            OpenButton.Visibility = Visibility.Visible;
        }
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
        string? banner = _error ?? (state is { Stale: true } ? "Spoolman offline" : null);
        Banner.Visibility = banner != null ? Visibility.Visible : Visibility.Collapsed;
        BannerText.Text = banner ?? "";
        // The last known values stay, greyed.
        Cards.Opacity = _error == null && state is not { Stale: true } ? 1 : 0.5;

        Cards.Children.Clear();
        if (state == null)
        {
            if (_error == null)
                Cards.Children.Add(EmptyBox("Loading…", -1, actions: false));
            return;
        }
        if (state.MultiTool)
        {
            for (int i = 0; i < state.Tools.Tools.Count; i++)
                Cards.Children.Add(ToolCard(state, i));
            return;
        }
        int tool = SingleTool(state);
        if (state.ActiveSpool is { } spool)
            Cards.Children.Add(BigCard(spool, Spools.Check(spool, state, -1, _status), tool));
        else if (state.ActiveSpoolId is int missing)
            Cards.Children.Add(EmptyBox(state.Stale ? $"Spool #{missing}" : $"Spool #{missing} not found", tool));
        else
            Cards.Children.Add(EmptyBox("No spool", tool));
    }

    /// <summary>The toolhead of a printer with one: the one in use, else the first (-1: no tool macros).</summary>
    private static int SingleTool(SpoolmanState state) =>
        state.Tools.Active >= 0 ? state.Tools.Active : state.Tools.Tools.Count > 0 ? 0 : -1;

    /// <summary>The spool in a printer with one toolhead: big, with a bar for what's left and a line about the print.</summary>
    private Border BigCard(Spool spool, SpoolCheck check, int tool)
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
        left.Children.Add(Text(spool.Remaining is double g ? Spools.Grams(g) : "?", 18, GramsBrush(spool, check),
                               bold: true, right: true));
        if (spool.Length is double mm)
            left.Children.Add(Text(Spools.Metres(mm), 11.5, DimBrush, right: true));
        Add(grid, left, 2, 0);
        if (spool.Fraction is double f)
            Add(grid, Bar(spool, f), 0, 1, span: 3, margin: new Thickness(0, 9, 0, 0));
        var lines = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var line in NeedLines(spool, check))
            lines.Children.Add(line);
        if (lines.Children.Count > 0)
            Add(grid, lines, 0, 2, span: 3);
        var card = new Border
        {
            Background = CardBrush, BorderBrush = CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10), Child = grid,
        };
        Hover(card, grid, left, 2, tool);
        return card;
    }

    /// <summary>One toolhead's card, on a printer with several: its spool, small. The tool in use has a blue label.</summary>
    private Border ToolCard(SpoolmanState state, int index)
    {
        var tool = state.Tools.Tools[index];
        bool inUse = index == state.Tools.Active;
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
        };
        Add(grid, label, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        StackPanel? weight = null;
        if (spool != null)
        {
            Add(grid, Ring(spool, 26, CardBrush), 1, 0, margin: new Thickness(0, 0, 9, 0));
            words.Children.Add(Text(spool.Title, 12.5, TextBrush, bold: true));
            words.Children.Add(Text(Join(inUse ? "In use" : tool.Filament ? "" : "Empty", spool.Material, spool.Fraction is double f ? Spools.Percent(f) : "",
                                         spool.Location), 11, DimBrush));
            var check = Spools.Check(spool, state, index, _status);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            left.Children.Add(Text(spool.Remaining is double g ? Spools.Grams(g) : "?", 13,
                                   GramsBrush(spool, check), bold: true, right: true));
            if (spool.Length is double mm)
                left.Children.Add(Text(Spools.Metres(mm), 11, DimBrush, right: true));
            Add(grid, left, 3, 0);
            weight = left;
            var lines = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var line in NeedLines(spool, check))
                lines.Children.Add(line);
            if (lines.Children.Count > 0)
                Add(grid, lines, 1, 1, span: 3);
        }
        else
        {
            Add(grid, Ring(null, 26, CardBrush), 1, 0, margin: new Thickness(0, 0, 9, 0));
            words.Children.Add(Text(tool.SpoolId is not int missing ? "No spool"
                                    : state.Stale ? $"Spool #{missing}" : $"Spool #{missing} not found", 12.5, DimBrush));
        }
        Add(grid, words, 2, 0);
        var card = new Border
        {
            Background = CardBrush, BorderBrush = CardEdge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4),
            Child = grid,
        };
        Hover(card, grid, weight, 3, index);
        return card;
    }

    /// <summary>The spool against the print: enough (with what's to spare), or the wrong material. Short: the grams turn amber.</summary>
    private static IEnumerable<TextBlock> NeedLines(Spool spool, SpoolCheck check)
    {
        if (check.WrongMaterial is string type)
            yield return Line(WarnBrush, "⚠ ", ("File is ", false), (type, true), ($", spool is {spool.Material}", false));
        if (check.Needed is not double needed)
            yield break;
        // Not enough: no line, the grams left are in amber (GramsBrush).
        if (Spools.Short(spool, check) != null)
            yield break;
        if (spool.Remaining is double left)
            yield return Line(DimBrush, "✓ ", ("Needs ", false), (Spools.Grams(needed), true), ($", {Spools.Grams(left - needed)} spare", false));
        else
            yield return Line(DimBrush, "", ("Needs ", false), (Spools.Grams(needed), true));
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

    /// <summary>The colour of the grams left: red when nearly empty, amber when low or it won't last the print.</summary>
    private Brush GramsBrush(Spool spool, SpoolCheck check) =>
        spool.Remaining < _levels.Empty ? EmptyBrush
        : spool.Remaining < _levels.Low || Spools.Short(spool, check) != null ? WarnBrush
        : TextBrush;

    /// <summary>No spool to show on a printer with one toolhead; Load and Unload still work.</summary>
    private Border EmptyBox(string text, int tool, bool actions = true)
    {
        var grid = new Grid { MinHeight = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = Text(text, 12.5, TextBrush, bold: true);
        words.VerticalAlignment = VerticalAlignment.Center;
        Add(grid, words, 0, 0);
        var box = new Border
        {
            Background = Brushes.Transparent, BorderBrush = DashBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10), Child = grid,
        };
        if (actions)
            Hover(box, grid, null, 1, tool);
        return box;
    }

    /// <summary>
    /// Load and Unload on a card, in place of its weight: while the mouse is on it, and while one of
    /// them waits for its second click or runs.
    /// </summary>
    private void Hover(Border card, Grid grid, FrameworkElement? weight, int column, int tool)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        foreach (bool load in new[] { true, false })
        {
            var (_, _, script, why) = Action(load, tool);
            var button = new Button
            {
                Style = (Style)FindResource("ActionButton"), Margin = new Thickness(load ? 0 : 4, 0, 0, 0),
                Content = _acting == (load, tool) ? (load ? "Loading…" : "Unloading…") : load ? "Load" : "Unload",
                Tag = _actionArmed == (load, tool) ? "armed" : null,
                IsEnabled = why == null && _acting == null,
                ToolTip = why ?? $"Runs {script}",
            };
            button.Click += (_, _) => OnActionClick(load, tool);
            strip.Children.Add(button);
        }
        Add(grid, strip, column, 0);
        bool pinned = _actionArmed?.Tool == tool || _acting?.Tool == tool;
        void Show(bool on)
        {
            strip.Visibility = on || pinned ? Visibility.Visible : Visibility.Hidden;
            if (weight != null)
                weight.Visibility = on || pinned ? Visibility.Hidden : Visibility.Visible;
        }
        Show(false);
        card.MouseEnter += (_, _) => Show(true);
        card.MouseLeave += (_, _) => Show(false);
        // Cards are drawn again every few seconds: the one under the mouse keeps its buttons.
        card.Loaded += (_, _) =>
        {
            var at = Mouse.GetPosition(card);
            if (at.X >= 0 && at.Y >= 0 && at.X < card.ActualWidth && at.Y < card.ActualHeight)
                Show(true);
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

    private void ShowList()
    {
        var state = _state;
        if (_spools == null || state == null)
            return;
        var loaded = state.LoadedIds.ToHashSet();
        var others = _spools.Where(s => !loaded.Contains(s.Id)).ToList();
        string filter = FilterBox.Text.Trim();
        var shown = filter.Length == 0 ? others : others.Where(s => Matches(s, filter)).ToList();
        ListNote.Text = others.Count == 0 ? "No other spools" : shown.Count == 0 ? "No matches" : "";
        ListNote.Visibility = ListNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The same list while nothing changes, so the row stays put under the mouse.
        IReadOnlyList<ToolPick> picks = _picking is int picking && state.MultiTool
            ? state.Tools.Tools.Select((t, i) => new ToolPick(picking, i, t.Name, _armed == (picking, i))).ToList()
            : NoPicks;
        if (!picks.SequenceEqual(_picks))
            _picks = picks;
        var fresh = shown.Select(Row).ToList();
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

    private SpoolRow Row(Spool s)
    {
        bool armed = _armed?.Spool == s.Id;
        bool picking = _picking == s.Id && _picks.Count > 0;
        // The tooltip: only what Spoolman has.
        var facts = new List<(string, string)>();
        if (s.Remaining is double left)
            facts.Add(("Left", s.Initial is > 0 and var start ? $"{Spools.Grams(left)} of {Spools.Grams(start)} ({Spools.Percent(left / start)})" : Spools.Grams(left)));
        if (s.Length is double mm)
            facts.Add(("Length", Spools.Metres(mm)));
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
                            s.Remaining < _levels.Low, s.Remaining < _levels.Empty, s.Fraction ?? double.NaN,
                            armed, picking, picking ? _picks : NoPicks,
                            s.Title, $"· #{s.Id}", string.Join("\n", facts.Select(f => f.Item1)), string.Join("\n", facts.Select(f => f.Item2)));
    }

    private string Money(double amount) =>
        _currency != null ? $"{amount.ToString("0.00", CultureInfo.CurrentCulture)} {_currency}" : amount.ToString("0.00", CultureInfo.CurrentCulture);

    // ---- switching -----------------------------------------------------------------------------

    private void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SpoolRow row } || _state is not { } state || _switching)
            return;
        // Several toolheads: a button for each, to pick which one gets the spool.
        if (state.MultiTool)
        {
            Disarm();
            _picking = row.Spool.Id;
            _disarm.Start();
            ShowList();
            return;
        }
        Pick(row.Spool.Id, SingleTool(state));
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToolPick pick })
            Pick(pick.SpoolId, pick.Index);
    }

    /// <summary>Puts the spool in the toolhead (-1: none); while printing, after a second click.</summary>
    private async void Pick(int spoolId, int tool)
    {
        if (_state is not { } state || _switching)
            return;
        // While printing, the rest of the print's usage goes to the new spool.
        if (_status.State is "printing" or "paused" && _armed != (spoolId, tool))
        {
            _armed = (spoolId, tool);
            _disarm.Stop();
            _disarm.Start();
            ShowList();
            return;
        }
        Disarm();
        _switching = true;
        try
        {
            if (tool >= 0 && tool < state.Tools.Tools.Count)
                await _source.Api.SetToolSpoolAsync(state.Tools.Tools[tool], spoolId,
                                                    tool == state.Tools.Active || !state.MultiTool, _lifetime);
            else
                await _source.Api.SetActiveSpoolAsync(spoolId, _lifetime);
            _wake = true;
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            ShowError("Couldn't switch: " + (ex is SpoolmanException ? ex.Message : _printer.DescribeError(ex)));
        }
        finally { _switching = false; }
        // Right away, not up to a refresh later (the tile too).
        await Task.Delay(300);
        _fresh = _wake = true;
    }

    // ---- load and unload -------------------------------------------------------------------------

    /// <summary>
    /// For a card's toolhead (its index; -1 on a printer without tool macros): the toolhead, whether
    /// it's the one in use, and the macro to run, or why there's none.
    /// </summary>
    private (Tool? Tool, bool InUse, string? Script, string? Why) Action(bool load, int index)
    {
        string name = FilamentMacros.Name(load);
        if (_state is not { } state)
            return (null, false, null, "Printer offline");
        var tool = index >= 0 && index < state.Tools.Tools.Count ? state.Tools.Tools[index] : null;
        bool inUse = tool == null || !state.MultiTool || index == state.Tools.Active;
        int number = tool != null && int.TryParse(tool.Name.AsSpan(1), out int n) ? n : -1;
        string chosen = load ? _loadMacro : _unloadMacro;
        bool own = !chosen.Equals(name, StringComparison.OrdinalIgnoreCase);
        string? script = tool?.Lane != null ? tool.LaneScript(load, inUse) : _macros.Script(load, number, inUse, chosen);
        string? why = tool?.Lane != null
            ? (load && inUse ? "Loaded" : !tool.Filament ? "No filament" : null)
            : script != null ? null
            : own || _macros.Macros.Contains(name) ? "Active toolhead only"
            : $"No {name} macro";
        // Paused is fine: that's when a print's filament is changed.
        if (_status.State == "printing")
            why = "Print in progress";
        return (tool, inUse, script, why);
    }

    private async void OnActionClick(bool load, int index)
    {
        if (_state is not { } state || _acting != null)
            return;
        // Both heat and move the printer: a second click.
        if (_actionArmed != (load, index))
        {
            Disarm();
            _actionArmed = (load, index);
            _disarm.Start();
            ShowCards();
            return;
        }
        Disarm();
        var (tool, inUse, script, why) = Action(load, index);
        if (script == null || why != null)
            return;
        string where = tool != null && state.MultiTool ? " " + tool.Name : "";
        _acting = (load, index);
        ActionNote.Visibility = Visibility.Collapsed;
        ShowCards();
        try
        {
            // Its own last word, e.g. "Filament free: pull it out".
            string said = await ((IMoonrakerApi)_printer).RunGcodeAsync(script, _lifetime) is { } message ? " " + Sentence(message) : "";
            // Spoolman follows: the toolhead's spool out after Unload, and in use after Load. A lane keeps
            // its spool (AFC forgets an ejected one itself); only the one Moonraker counts changes.
            try
            {
                if (!load && tool?.Lane != null)
                {
                    if (inUse)
                        await _source.Api.SetActiveSpoolAsync(null, _lifetime);
                }
                else if (!load && tool != null)
                    await _source.Api.SetToolSpoolAsync(tool.Name, null, inUse, _lifetime);
                else if (!load)
                    await _source.Api.SetActiveSpoolAsync(null, _lifetime);
                // The tool's spool is the one Moonraker counts, if it's the tool in use.
                else if (tool?.SpoolId is int id && (inUse || tool.Lane != null) && state.Status.ActiveSpoolId != id)
                    await _source.Api.SetActiveSpoolAsync(id, _lifetime);
                ShowNote($"{(load ? "Loaded" : "Unloaded")}{where}.{said}", warn: false);
            }
            catch (Exception ex) when (!_lifetime.IsCancellationRequested)
            {
                ShowNote($"{(load ? "Loaded" : "Unloaded")}{where}. Spool not {(load ? "set" : "cleared")}: "
                         + (ex is SpoolmanException ? ex.Message : _printer.DescribeError(ex)), warn: true);
            }
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            ShowNote($"{(load ? "Load" : "Unload")}{where} failed: "
                     + (ex is GcodeException ? ex.Message : _printer.DescribeError(ex)), warn: true);
        }
        finally
        {
            _acting = null;
            ShowCards();
        }
        await Task.Delay(300);
        _fresh = _wake = _listWake = true;
    }

    /// <summary>A macro's message as a sentence: "Filament free: pull it out" → "Filament free: pull it out.".</summary>
    internal static string Sentence(string message) =>
        message.Length > 0 && !".!?".Contains(message[^1]) ? message + "." : message;

    // Load and Unload's last word goes after a while (an error a little later).
    private DispatcherTimer? _noteFade;

    private void ShowNote(string text, bool warn)
    {
        ActionText.Text = text;
        ActionText.Foreground = warn ? WarnText : NoteText;
        ActionNote.Background = warn ? WarnBack : NoteBack;
        ActionNote.Visibility = Visibility.Visible;
        _noteFade?.Stop();
        _noteFade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(warn ? 15 : 8) };
        _noteFade.Tick += (_, _) =>
        {
            _noteFade?.Stop();
            ActionNote.Visibility = Visibility.Collapsed;
        };
        _noteFade.Start();
    }

    // Two columns: the panel is wide (under the webcam), so the other spools go beside the ones in the printer.
    private bool _twoColumns;

    private void Fit()
    {
        bool two = Layout.TwoColumns(this);
        if (two == _twoColumns)
            return;
        _twoColumns = two;
        if (two)
        {
            MainStack.Children.Remove(Others);
            OthersHost.Content = Others;
            OthersHead.Margin = new Thickness(8, 5, 10, 4);
        }
        else
        {
            OthersHost.Content = null;
            MainStack.Children.Add(Others);
            OthersHead.Margin = new Thickness(8, 10, 10, 4);
        }
        OthersColumn.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        OthersPane.Visibility = two ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The page scrolls only when the user scrolls it (or types in the filter), not when a click or a
    /// redrawn row asks to be brought into view.
    /// </summary>
    private void OnBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject != FilterBox)
            e.Handled = true;
    }

    private void Disarm()
    {
        _disarm.Stop();
        if (_actionArmed != null)
        {
            _actionArmed = null;
            ShowCards();
        }
        if (_armed == null && _picking == null)
            return;
        (_armed, _picking) = (null, null);
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
                                  EmptyBrush = Frozen(Color.FromRgb(0xFF, 0x7B, 0x72)),
                                  BlueBrush = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF)),
                                  OnBlueBrush = Frozen(Color.FromRgb(0x00, 0x20, 0x33)),
                                  LabelBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33)),
                                  CardBrush = Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F)),
                                  CardEdge = Frozen(Color.FromRgb(0x2E, 0x2E, 0x2E)),
                                  DashBrush = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                                  UsedBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33)),
                                  NoteBack = Frozen(Color.FromRgb(0x1F, 0x2A, 0x33)),
                                  NoteText = Frozen(Color.FromRgb(0xB9, 0xDC, 0xF2)),
                                  WarnBack = Frozen(Color.FromRgb(0x3A, 0x2F, 0x12)),
                                  WarnText = Frozen(Color.FromRgb(0xF0, 0xD5, 0x8A));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
