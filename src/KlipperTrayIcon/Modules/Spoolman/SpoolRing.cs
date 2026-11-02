using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace KlipperTrayIcon.Modules.Spoolman;

/// <summary>
/// A spool seen from the side: a ring in the filament's colour(s) for what's left, grey for what's used,
/// around a hub. Without a starting weight (Fraction NaN) the whole ring is the colour.
/// </summary>
public sealed class SpoolRing : FrameworkElement
{
    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(
        nameof(Colors), typeof(IReadOnlyList<string>), typeof(SpoolRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(SpoolRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HubProperty = DependencyProperty.Register(
        nameof(Hub), typeof(Brush), typeof(SpoolRing),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The filament's hex colours ("C8312B"), several for a multi-colour one.</summary>
    public IReadOnlyList<string>? Colors { get => (IReadOnlyList<string>?)GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }
    /// <summary>What's left, 0 to 1; NaN when it isn't known.</summary>
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    /// <summary>The hub's colour: the background it sits on.</summary>
    public Brush Hub { get => (Brush)GetValue(HubProperty); set => SetValue(HubProperty, value); }

    private static readonly Brush Used = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)), NoColor = Frozen(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly Pen HubEdge = FrozenPen(Color.FromArgb(0x55, 0, 0, 0));

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2;
        var brushes = (Colors ?? []).Select(ToBrush).OfType<Brush>().ToList();
        if (brushes.Count == 0)
            brushes.Add(NoColor);
        double left = double.IsNaN(Fraction) ? 1 : Math.Clamp(Fraction, 0, 1);
        // Clockwise from the top: each colour an equal share of what's left, then the used part.
        double start = 0;
        foreach (var brush in brushes)
        {
            double sweep = 360 * left / brushes.Count;
            Slice(dc, brush, center, radius, start, sweep);
            start += sweep;
        }
        Slice(dc, Used, center, radius, start, 360 - start);
        dc.DrawEllipse(Hub, HubEdge, center, radius * 0.4, radius * 0.4);
    }

    private static void Slice(DrawingContext dc, Brush brush, Point center, double radius, double from, double sweep)
    {
        if (sweep <= 0.01)
            return;
        if (sweep >= 359.99)
        {
            dc.DrawEllipse(brush, null, center, radius, radius);
            return;
        }
        Point At(double degrees) => new(center.X + radius * Math.Sin(degrees * Math.PI / 180),
                                        center.Y - radius * Math.Cos(degrees * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(center, true, true);
            g.LineTo(At(from), false, false);
            g.ArcTo(At(from + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>"C8312B" (or with alpha after it, "C8312BFF") as a brush; null if it isn't one.</summary>
    internal static Brush? ToBrush(string hex)
    {
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return null;
        if (hex.Length == 6)
            v = (v << 8) | 0xFF;
        return Frozen(Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8)));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(Frozen(color), 1);
        pen.Freeze();
        return pen;
    }
}
