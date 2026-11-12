using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KlipperTrayIcon.UI;

/// <summary>
/// The console's lines as one read-only document, so text can be selected across lines and copied
/// (Ctrl+C, or right-click). One paragraph per line: the time, then the message, whose wrapped and
/// further lines stay under the message, never under the time.
/// </summary>
internal sealed class ConsoleText
{
    private static readonly Brush TimeBrush = Frozen(0x7A, 0x7A, 0x7A), LinkPrefixBrush = Frozen(0xA0, 0xA0, 0xA0),
                                  WarnBrush = Frozen(0xE3, 0xB3, 0x41);

    private readonly RichTextBox _box;
    private readonly FontFamily _timeFont;
    // The time and the gap after it: where messages start.
    private readonly double _indent;

    public ConsoleText(RichTextBox box, FontFamily timeFont)
    {
        _box = box;
        _timeFont = timeFont;
        _box.Document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 13 };
        var typeface = new Typeface(timeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _indent = new FormattedText(Time("00:00:00"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 12,
                                    TimeBrush, VisualTreeHelper.GetDpi(box).PixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    private static string Time(string time) => time + "  ";

    /// <summary>Shows these lines, and follows them as they change.</summary>
    public void Bind(ObservableCollection<ConsoleLine> lines)
    {
        Reset(lines);
        lines.CollectionChanged += (_, e) =>
        {
            var blocks = _box.Document.Blocks;
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems != null:
                    int at = e.NewStartingIndex;
                    foreach (ConsoleLine line in e.NewItems)
                    {
                        var paragraph = Make(line);
                        if (at < 0 || at >= blocks.Count)
                            blocks.Add(paragraph);
                        else
                            blocks.InsertBefore(blocks.ElementAt(at), paragraph);
                        at = at < 0 ? at : at + 1;
                    }
                    break;
                case NotifyCollectionChangedAction.Remove when e.OldItems != null && e.OldStartingIndex >= 0:
                    for (int i = 0; i < e.OldItems.Count && e.OldStartingIndex < blocks.Count; i++)
                        blocks.Remove(blocks.ElementAt(e.OldStartingIndex));
                    break;
                case NotifyCollectionChangedAction.Replace when e.NewItems is [ConsoleLine replacement] && e.NewStartingIndex < blocks.Count:
                    var old = blocks.ElementAt(e.NewStartingIndex);
                    blocks.InsertBefore(old, Make(replacement));
                    blocks.Remove(old);
                    break;
                default:
                    Reset(lines);
                    break;
            }
        };
    }

    private void Reset(ObservableCollection<ConsoleLine> lines)
    {
        _box.Document.Blocks.Clear();
        foreach (var line in lines)
            _box.Document.Blocks.Add(Make(line));
    }

    private Paragraph Make(ConsoleLine line)
    {
        // A hanging indent: the time sits in the margin, the message and its wrapped lines start after it.
        double indent = line.HasTime ? _indent : 0;
        var paragraph = new Paragraph
        {
            Margin = new Thickness(indent, 2, 0, 3), TextIndent = -indent,
            LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (line.HasTime)
            paragraph.Inlines.Add(new Run(Time(line.Time)) { FontFamily = _timeFont, FontSize = 12, Foreground = TimeBrush });
        var lines = line.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i])
            {
                Foreground = line.Brush, FontStyle = line.FontStyle, FontWeight = line.FontWeight, FontFamily = line.FontFamily,
            });
        }
        // A command being sent; or one that failed: a ⚠, why on hover.
        if (line.Pending)
            paragraph.Inlines.Add(new Run(" …") { Foreground = TimeBrush });
        if (line.Warning is { } warning)
            paragraph.Inlines.Add(new Run("  ⚠")
            {
                FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = WarnBrush, Cursor = System.Windows.Input.Cursors.Arrow,
                ToolTip = new ToolTip { Content = warning },
            });
        // Under a Klipper shutdown: "Click to restart.", with only the link part clickable.
        if (line.HasLink)
        {
            paragraph.Inlines.Add(new LineBreak());
            var ui = new FontFamily("Segoe UI");
            if (line.LinkPrefix.Length > 0)
                paragraph.Inlines.Add(new Run(line.LinkPrefix) { FontFamily = ui, Foreground = LinkPrefixBrush });
            if (line.Click is { } click)
            {
                var link = new Hyperlink(new Run(line.Link)) { FontFamily = ui, Foreground = line.LinkBrush, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
                link.Click += (_, _) => click();
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = null;
                paragraph.Inlines.Add(link);
            }
            else if (line.Link.Length > 0)
                paragraph.Inlines.Add(new Run(line.Link) { FontFamily = ui, Foreground = line.LinkBrush });
        }
        return paragraph;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
