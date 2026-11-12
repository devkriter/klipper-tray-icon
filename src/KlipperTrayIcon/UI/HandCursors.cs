using System;
using System.Windows;
using System.Windows.Input;

namespace KlipperTrayIcon.UI;

/// <summary>
/// Open and closed hand cursors for dragging the print queue's rows (Windows has no "grab"
/// cursor; its hand is the link pointer). They're XCursor Pro's Light "Pan" and "Grabbing"
/// (https://github.com/ful1e5/XCursor-pro, GPL-3.0): white, like Windows' default arrow, at
/// 32 to 128 px, so Windows picks the size that matches the screen's scale.
/// </summary>
internal static class HandCursors
{
    private static readonly Lazy<Cursor> OpenHand = new(() => Load("grab.cur"));
    private static readonly Lazy<Cursor> ClosedHand = new(() => Load("grabbing.cur"));

    /// <summary>Over something that can be picked up.</summary>
    public static Cursor Open => OpenHand.Value;

    /// <summary>While dragging it.</summary>
    public static Cursor Closed => ClosedHand.Value;

    private static Cursor Load(string file)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/KlipperTrayIcon;component/UI/Cursors/{file}"))!;
        using var stream = resource.Stream;
        return new Cursor(stream, scaleWithDpi: true);
    }
}
