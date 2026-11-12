using System.Windows;

namespace KlipperTrayIcon.Modules;

/// <summary>How a module's page arranges itself in the space it has.</summary>
public static class Layout
{
    /// <summary>Wide enough for two lists side by side (each at least as wide as the side panel).</summary>
    public const double TwoColumnWidth = 720;

    /// <summary>
    /// Two columns rather than one: the page is wider than it is tall and has room for two side panels'
    /// width, as under the webcam in a tall popup.
    /// </summary>
    public static bool TwoColumns(FrameworkElement page) =>
        page.ActualWidth >= TwoColumnWidth && page.ActualWidth > page.ActualHeight;
}
