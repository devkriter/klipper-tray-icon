using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Commands;

/// <summary>
/// The box under the console. Clicked while empty, a dropdown lists the pins, then the recent
/// commands; typing, it suggests the printer's commands. Enter or Send sends; while printing or
/// paused, only on a second Enter or click. What the printer answers shows in the console above.
/// </summary>
public partial class CommandBox : ModuleConsoleInput
{
    /// <summary>A row in the dropdown.</summary>
    private sealed record Item(string Kind, string Text, Pin? Pin = null, Suggestion? Suggestion = null);

    private const int MaxRecent = 6;
    // All Klipper takes once it has shut down.
    private static readonly string[] Restarts = ["FIRMWARE_RESTART", "RESTART"];

    private static readonly Brush Normal = Frozen(0xDA, 0xDA, 0xDA), Dim = Frozen(0x8A, 0x8A, 0x8A), Blue = Frozen(0x60, 0xCD, 0xFF),
                                  Current = Frozen(0x0F, 0x4F, 0x7A), CurrentDim = Frozen(0xC7, 0xE3, 0xF5),
                                  Line = Frozen(0x33, 0x33, 0x33), FrameLine = Frozen(0x3A, 0x3A, 0x3A);
    private static readonly FontFamily Ui = new("Segoe UI");

    private readonly IMoonrakerApi _printer;
    private readonly IPrinterBackend _backend;
    private readonly ModuleContext _context;
    private readonly CommandHistory _history;
    private List<Pin> _pins;
    private IReadOnlyDictionary<string, string> _help = new Dictionary<string, string>();
    private bool _helpAsked;

    private PrinterStatus _status = PrinterStatus.Connecting;
    // The dropdown before anything is typed: opened by clicking the empty box.
    private bool _openEmpty;
    private List<Item> _items = [];
    private List<Suggestion> _suggestions = [];
    private int _current = -1;
    // Up or Down moved through the dropdown: Enter takes the row rather than sending the text.
    private bool _moved;
    // Setting the box's text ourselves (history, a pick): not the user typing.
    private bool _setting;
    // The command Up or Down put in the box: while it's unchanged, they keep stepping.
    private string? _stepped;
    // The text sent on the next Enter or click while printing (null: not asked yet).
    private string? _armed;
    private string? _typo;
    private string? _error;

    public CommandBox(ModuleContext context, CommandHistory history)
    {
        InitializeComponent();
        _context = context;
        _backend = context.Printer;
        _printer = (IMoonrakerApi)context.Printer;
        _history = history;
        _pins = Pin.Parse(context.Text(CommandsModule.Pinned));

        Input.TextChanged += (_, _) => OnTyped();
        Input.SelectionChanged += (_, _) => ShowGhost();
        Input.PreviewKeyDown += OnKey;
        Input.PreviewMouseLeftButtonDown += (_, _) => OpenEmpty();
        Input.GotKeyboardFocus += (_, _) =>
        {
            Frame.BorderBrush = Blue;
            FetchHelp();
        };
        Input.LostKeyboardFocus += (_, e) =>
        {
            Frame.BorderBrush = FrameLine;
            ShowGhost();
            // To Pin or Unpin's menu: the dropdown stays.
            if (e.NewFocus is not System.Windows.Controls.ContextMenu and not MenuItem)
                Close();
        };
        // A click anywhere else in the popup closes the dropdown.
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window)
                window.PreviewMouseDown += (_, e) =>
                {
                    if (e.OriginalSource is DependencyObject d && !IsAncestorOf(d))
                        Close();
                };
        };
        SizeChanged += (_, _) => Dropdown.Width = Math.Max(0, ActualWidth - 20);
        Show();
    }

    // ---- the printer's state ------------------------------------------------------------

    public override void OnStatus(PrinterStatus status)
    {
        var old = _status;
        _status = status;
        // Klipper back after a restart or an outage: its commands may have changed.
        if ((old.Halted != null || old.State == "offline") && status.Halted == null && status.State != "offline")
        {
            _helpAsked = false;
            if (Input.IsKeyboardFocused)
                FetchHelp();
        }
        if (status.State == "offline")
            Close();
        Show();
    }

    private bool Offline => _status.State == "offline";
    private bool Halted => _status.Halted != null;
    // Every command asks for a second click while a print is running or paused.
    private bool Guarded => _status.State is "printing" or "paused";

    private async void FetchHelp()
    {
        if (_helpAsked || Offline)
            return;
        _helpAsked = true;
        try
        {
            using var doc = await _printer.GetJsonAsync("/printer/gcode/help", _context.Lifetime);
            _help = CommandText.ReadHelp(doc.RootElement.Prop("result"));
            Show();
        }
        catch (Exception) when (!_context.Lifetime.IsCancellationRequested) { _helpAsked = false; } // asked again on the next click
        catch (OperationCanceledException) { }
    }

    // ---- typing ------------------------------------------------------------------------

    private void OnTyped()
    {
        if (_setting)
            return;
        // Command words in capitals, as typed (same length, so the caret stays put).
        string capital = CommandText.Capitalize(Input.Text);
        if (capital != Input.Text)
        {
            int caret = Input.CaretIndex;
            Set(capital, caret, 0);
        }
        _history.StopStepping();
        (_armed, _error, _moved) = (null, null, false);
        if (Input.Text.Trim().Length > 0)
            _openEmpty = false;
        Show();
    }

    /// <summary>Puts text in the box, with length characters from start selected (none: the caret at start).</summary>
    private void Set(string text, int start, int length)
    {
        _setting = true;
        Input.Text = text;
        Input.Select(Math.Min(start, text.Length), length);
        _setting = false;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool dropped = Dropdown.Visibility == Visibility.Visible && _items.Any(i => i.Kind != "divider");
        switch (e.Key)
        {
            case Key.Down or Key.Up when dropped:
                Move(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Up or Key.Down when Input.Text.Length == 0 || Input.Text == _stepped:
                // Through history, as in a terminal: Up for older, Down back to newer (and the empty box).
                if ((e.Key == Key.Up ? _history.Older() : _history.Newer()) is { } step)
                {
                    _stepped = step;
                    Set(step, step.Length, 0);
                    (_armed, _error) = (null, null);
                    Show();
                }
                e.Handled = true;
                break;
            case Key.Tab:
                Complete();
                e.Handled = true;
                break;
            case Key.Enter:
                // A row picked with Up or Down, or the first one of the empty box's dropdown.
                if (dropped && _current >= 0 && (_moved || Input.Text.Trim().Length == 0))
                    Pick(_items[_current]);
                else
                    Send(Input.Text);
                e.Handled = true;
                break;
            case Key.Escape when dropped || _armed != null:
                _openEmpty = false;
                _armed = null;
                Close();
                Show();
                e.Handled = true;
                break;
        }
    }

    private void Move(int by)
    {
        int count = _items.Count;
        int next = _current;
        do
            next = (next + by + count) % count;
        while (_items[next].Kind == "divider");
        (_current, _moved) = (next, true);
        ShowDrop();
    }

    /// <summary>Tab: takes "did you mean", or fills in the row picked (the top suggestion at first).</summary>
    private void Complete()
    {
        if (_typo is { Length: > 0 } typo)
        {
            string text = CommandText.ReplaceWord(Input.Text, typo);
            Set(text, text.Length, 0);
            OnFilled();
        }
        else if (_current >= 0 && _current < _items.Count && Dropdown.Visibility == Visibility.Visible)
            Fill(_items[_current]);
    }

    private void Fill(Item item)
    {
        switch (item)
        {
            case { Suggestion: { } s }:
            {
                string text = CommandText.ReplaceWord(Input.Text, s.Name);
                // A space: the command's done, now its parameters.
                if (text.TrimEnd() == text)
                    text += " ";
                Set(text, text.Length, 0);
                break;
            }
            case { Pin: { } pin }:
            {
                var (text, start, length) = pin.Fill();
                Set(text, length > 0 ? start : text.Length, length);
                break;
            }
            default:
                Set(item.Text, item.Text.Length, 0);
                break;
        }
        _openEmpty = false;
        OnFilled();
    }

    private void OnFilled()
    {
        _history.StopStepping();
        (_armed, _error, _moved) = (null, null, false);
        Show();
    }

    /// <summary>A row clicked, or Enter on it: a pin without a value (or a restart) is sent, anything else fills the box.</summary>
    private void Pick(Item item)
    {
        if (item.Kind == "restart" || item.Pin is { HasValue: false })
        {
            string command = item.Pin?.Command ?? item.Text;
            Set(command, command.Length, 0);
            _openEmpty = false;
            OnFilled();
            Send(command);
        }
        else
            Fill(item);
        Input.Focus();
    }

    // ---- sending -----------------------------------------------------------------------

    private void OnSendClick(object sender, RoutedEventArgs e) => Send(Input.Text);

    private void Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || Offline)
            return;
        // While printing: the first time only asks.
        if (Guarded && _armed != text)
        {
            _armed = text;
            Close();
            Show();
            return;
        }
        (_armed, _error, _openEmpty) = (null, null, false);
        _history.Add(text);
        Set("", 0, 0);
        Close();
        Show();
        ShowLatest();
        _ = SendAsync(text);
    }

    private async Task SendAsync(string text)
    {
        try { await Task.Run(() => _printer.SendGcodeAsync(text, _context.Lifetime)); }
        // Klipper said no: its error is in the console.
        catch (MoonrakerHttpException e) when ((int)e.StatusCode == 400) { }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _error = "Couldn't send: " + _backend.DescribeError(e);
            Show();
        }
    }

    // ---- showing -----------------------------------------------------------------------

    /// <summary>Shows the box as things are now: its placeholder, the amber line, the button and the dropdown.</summary>
    private void Show()
    {
        string text = Input.Text;
        IsEnabled = !Offline;
        Frame.Opacity = Offline ? 0.55 : 1;
        Placeholder.Text = Offline ? "Printer offline" : Halted ? "Klipper shut down" : "";
        Placeholder.Visibility = text.Length == 0 && Placeholder.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var suggestions = Halted ? [] : CommandText.Suggest(_help, text);
        _typo = suggestions.Count == 0 && !Halted ? CommandText.DidYouMean(_help, text) : null;
        _suggestions = suggestions;
        ShowGhost();

        bool armed = _armed != null && Guarded;
        Say.Text = armed ? "Print in progress. Did you mean to send this?"
            : _error ?? (_typo == null ? "" : _typo.Length > 0 ? $"Command not recognized. Did you mean {_typo}?" : "Command not recognized.");
        Say.Visibility = Say.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Content = armed ? "Send anyway" : "Send";
        SendButton.Tag = armed ? "armed" : null;

        var items = new List<Item>();
        if (text.Trim().Length > 0)
            items.AddRange(suggestions.Select(s => new Item("suggestion", s.Name, Suggestion: s)));
        else if (_openEmpty && Halted)
            items.AddRange(Restarts.Select(r => new Item("restart", r)));
        else if (_openEmpty)
        {
            items.AddRange(_pins.Select(p => new Item("pin", p.Name, Pin: p)));
            var recent = _history.Recent(MaxRecent);
            if (items.Count > 0 && recent.Count > 0)
                items.Add(new Item("divider", ""));
            items.AddRange(recent.Select(r => new Item("recent", r)));
        }
        // The same rows: keep the one picked.
        bool same = items.Select(i => i.Text).SequenceEqual(_items.Select(i => i.Text));
        _items = items;
        if (!same)
            (_current, _moved) = (items.FindIndex(i => i.Kind != "divider"), false);
        ShowDrop();
    }

    /// <summary>The rest of the top suggestion, dim after the caret, while it's at the end.</summary>
    private void ShowGhost()
    {
        string text = Input.Text;
        GhostTyped.Text = text;
        GhostRest.Text = Input.IsKeyboardFocused && Input.CaretIndex == text.Length && Input.SelectionLength == 0
            ? CommandText.Ghost(text, _suggestions) : "";
    }

    private void ShowDrop()
    {
        DropList.Children.Clear();
        if (_items.Count == 0 || !IsEnabled)
        {
            Dropdown.Visibility = Visibility.Collapsed;
            return;
        }
        for (int i = 0; i < _items.Count; i++)
            DropList.Children.Add(Row(_items[i], i == _current));
        Dropdown.Visibility = Visibility.Visible;
    }

    private void Close()
    {
        _openEmpty = false;
        _items = [];
        _current = -1;
        Dropdown.Visibility = Visibility.Collapsed;
        DropList.Children.Clear();
    }

    private void OpenEmpty()
    {
        if (Input.Text.Trim().Length > 0 || Offline)
            return;
        _openEmpty = true;
        Show();
    }

    private UIElement Row(Item item, bool current)
    {
        if (item.Kind == "divider")
            return new Border { Height = 1, Background = Line, Margin = new Thickness(0, 4, 0, 4) };
        var grid = new Grid();
        var main = new TextBlock { FontSize = 12.5, Foreground = Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        var side = new TextBlock { FontSize = 11.5, Foreground = current ? CurrentDim : Dim, TextTrimming = TextTrimming.CharacterEllipsis,
                                   VerticalAlignment = VerticalAlignment.Center };
        switch (item)
        {
            case { Pin: { } pin }:
                // Its name, and what it sends dim at the right; a pin that asks for a value ends in "…".
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                main.FontFamily = Ui;
                main.Text = pin.HasValue ? pin.Name + "…" : pin.Name;
                side.Text = pin.Command;
                side.FontFamily = (FontFamily)FindResource("MonoFont");
                side.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(side, 1);
                break;
            case { Suggestion: { } s }:
                // The name, with what's typed in blue, then its description dim.
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                main.FontFamily = (FontFamily)FindResource("MonoFont");
                main.TextTrimming = TextTrimming.None;
                main.Inlines.Add(new Run(s.Name[..s.MatchStart]));
                main.Inlines.Add(new Run(s.Name.Substring(s.MatchStart, s.MatchLength)) { Foreground = Blue });
                main.Inlines.Add(new Run(s.Name[(s.MatchStart + s.MatchLength)..]));
                side.Text = s.Description;
                side.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(side, 1);
                break;
            default:
                main.FontFamily = (FontFamily)FindResource("MonoFont");
                main.Text = item.Text;
                break;
        }
        grid.Children.Add(main);
        if (side.Text.Length > 0)
            grid.Children.Add(side);
        var row = new Border { Child = grid, Padding = new Thickness(10, 4, 10, 4), Background = current ? Current : Brushes.Transparent,
                               Cursor = Cursors.Hand };
        row.MouseLeftButtonUp += (_, _) => Pick(item);
        if (item.Kind is "recent" or "pin")
            row.ContextMenu = Menu(item);
        return row;
    }

    /// <summary>Right-click: Pin a recent command (named after itself), or Unpin a pin.</summary>
    private ContextMenu Menu(Item item)
    {
        var entry = new MenuItem { Header = item.Pin != null ? "Unpin" : "Pin", Style = (Style)FindResource("MenuEntry") };
        entry.Click += (_, _) =>
        {
            if (item.Pin is { } pin)
                _pins.Remove(pin);
            else if (!_pins.Any(p => p.Command == item.Text))
                _pins.Add(Pin.From(item.Text));
            _context.SaveText(CommandsModule.Pinned, Pin.Format(_pins));
            _openEmpty = true;
            Input.Focus();
            Show();
        };
        var menu = new ContextMenu { Style = (Style)FindResource("Menu"), Items = { entry } };
        return menu;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
