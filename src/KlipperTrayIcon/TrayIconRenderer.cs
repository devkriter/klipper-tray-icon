using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace KlipperTrayIcon;

/// <summary>
/// Draws the tray icon: a ring that fills up with print progress, colored by state. While a print
/// is preparing (start macro, heating) the ring is whole and gold; once it prints, the green fills
/// a faint green track, so it doesn't vanish at 0%.
/// </summary>
public static class TrayIconRenderer
{
    private static Color StateColor(string state) => state switch
    {
        "printing" => Color.FromArgb(0, 255, 127),   // spring green
        "paused" => Color.FromArgb(255, 120, 0),     // orange
        "complete" => Color.FromArgb(0, 191, 255),   // deep sky blue
        "error" => Color.FromArgb(220, 20, 60),      // crimson
        "offline" => Color.FromArgb(110, 110, 110),  // dim gray, plus a red dot
        _ => Color.FromArgb(169, 169, 169),          // standby, cancelled, unknown
    };

    private static readonly Color Preparing = Color.FromArgb(255, 200, 40); // gold

    /// <summary>Geometry is laid out on a 64px grid and scaled to size.</summary>
    public static Bitmap Draw(string state, double progress, int size = 64, bool preparing = false)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float k = size / 64f;
        float ring = Math.Max(1f, 6 * k);
        // The pen is centred on the path, so inset by half its width to keep
        // the ring's outer edge 4px from the border.
        float inset = 4 * k + ring / 2;
        var rect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);

        bool filling = state == "printing" && !preparing;
        Color color = state == "printing" && preparing ? Preparing : StateColor(state);
        // The track the progress fills: faint green while printing, faint grey otherwise.
        using (var background = new Pen(filling ? Color.FromArgb(80, color) : Color.FromArgb(50, 200, 200, 200), ring))
            g.DrawEllipse(background, rect);

        using var pen = new Pen(color, ring);
        progress = Math.Clamp(progress, 0, 1);
        if (filling)
        {
            if (progress > 0)
                g.DrawArc(pen, rect, -90, (float)(360 * progress));
        }
        else
        {
            g.DrawEllipse(pen, rect);
        }

        if (state == "offline")
        {
            using var dot = new SolidBrush(Color.FromArgb(220, 20, 60));
            g.FillEllipse(dot, 38 * k, 38 * k, 24 * k, 24 * k);
        }
        return bitmap;
    }

    /// <summary>
    /// A tray-sized Icon. The caller owns it and must Dispose it; the
    /// underlying HICON is destroyed then too.
    /// </summary>
    public static Icon DrawIcon(string state, double progress, int size, bool preparing = false)
    {
        using var bitmap = Draw(state, progress, size, preparing);
        IntPtr handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone(); // owns its own copy of the handle
        Native.DestroyIcon(handle);
        return icon;
    }
}
