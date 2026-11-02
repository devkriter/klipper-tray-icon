using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules;

/// <summary>
/// An optional feature with a panel in the webcam popup. Every module is off until the user turns
/// it on in Settings; once on, it gets a button in the popup's title bar, beside the console's.
/// Each lives in its own folder under Modules/ and is listed in <see cref="ModuleRegistry.All"/>.
/// See CONTRIBUTING.md.
/// </summary>
public interface IModule
{
    /// <summary>Saved in config.json ("modules": [...]). Never change it once released.</summary>
    string Id { get; }
    /// <summary>Its name in Settings and on its title bar button's tooltip, e.g. "Print queue".</summary>
    string Name { get; }
    /// <summary>One sentence: what it adds. Shown in Settings' list of modules.</summary>
    string Description { get; }
    /// <summary>Its title bar button: a Segoe Fluent Icons glyph, e.g. "" (a list).</summary>
    string Glyph { get; }

    /// <summary>Everything it does and how to use it, a paragraph or two. Shown when it's picked in Settings.</summary>
    string Details => Description;
    /// <summary>What the printer needs for it, e.g. which Moonraker component to turn on. Empty: nothing special.</summary>
    string Needs => "";
    /// <summary>A page with more about it (its setup, or the feature it uses), or null.</summary>
    string? HelpUrl => null;
    /// <summary>Who made it: a name or GitHub user.</summary>
    string Author => "";

    /// <summary>
    /// Whether it can work with this kind of printer at all, e.g. <c>printer is IMoonrakerApi</c> for
    /// Klipper only. No network calls: Settings greys the module out when this is false.
    /// </summary>
    bool Supports(IPrinterBackend printer);

    /// <summary>
    /// Asks the printer whether it has what the module needs (Settings calls it once it reaches the
    /// printer). Null if it does, otherwise why not, e.g. which Moonraker component to turn on.
    /// </summary>
    Task<string?> CheckAsync(IPrinterBackend printer, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Its panel, made once for each popup (when the popup is made, hidden).</summary>
    ModulePanel CreatePanel(ModuleContext context);

    /// <summary>
    /// Tiles it offers for the stats strip, while it's on. The user adds them to the strip in Settings,
    /// like the built-in ones. None by default.
    /// </summary>
    IReadOnlyList<TileInfo> Tiles => [];

    /// <summary>Makes one of its <see cref="Tiles"/> (by its Id) for a popup's stats strip, once the strip shows it.</summary>
    ModuleTile CreateTile(string id, ModuleContext context) => throw new NotSupportedException($"No tile {id}");
}

/// <summary>A stats strip tile a module offers.</summary>
/// <param name="Id">Unique within the module, e.g. "queue". Saved in config.json as "module_id:tile_id", so never change it once released.</param>
/// <param name="Name">Its name in Settings' tile menu, e.g. "Queued jobs".</param>
/// <param name="Label">Its label in the strip, in capitals and short (tiles are narrow), e.g. "QUEUE".</param>
public sealed record TileInfo(string Id, string Name, string Label);

/// <summary>
/// A module's tile in the stats strip: a big value with a smaller line under it. Its
/// <see cref="Show"/> is called with each status update while the popup is open (about every
/// second). For data of its own, fetch it in the background and call <see cref="Refresh"/> when it
/// changes; stop when the context's Lifetime is cancelled.
/// </summary>
public abstract class ModuleTile
{
    /// <summary>What it shows now, e.g. new("3", "jobs"). "—" for nothing to show. UI thread; no waiting here.</summary>
    public abstract StatusText.Tile Show(PrinterStatus status);

    /// <summary>Asks the strip to call <see cref="Show"/> again (any thread).</summary>
    protected void Refresh() => Changed?.Invoke();

    internal event Action? Changed;
}

/// <summary>What a module's panel gets to work with.</summary>
/// <param name="Printer">The printer. Klipper printers are also an <see cref="IMoonrakerApi"/>.</param>
/// <param name="Lifetime">Cancelled when the popup goes away (settings changed, app exiting): stop any loops then.</param>
public sealed record ModuleContext(IPrinterBackend Printer, CancellationToken Lifetime);

/// <summary>
/// A module's view: the popup's side panel under the stats strip, in place of the console while its
/// button is selected. Derive a UserControl from it (in XAML: &lt;modules:ModulePanel x:Class=...&gt;).
/// <see cref="System.Windows.UIElement.IsVisible"/> says whether it's on screen right now.
/// All of these are called on the UI thread.
/// </summary>
public class ModulePanel : UserControl
{
    /// <summary>Each status update from the tray (every couple of seconds; every second while the popup is open).</summary>
    public virtual void OnStatus(PrinterStatus status) { }
    /// <summary>The popup was opened (whether or not this panel is the one showing).</summary>
    public virtual void OnPopupShown() { }
    /// <summary>Its button was selected (true), or another one was (false).</summary>
    public virtual void OnSelected(bool selected) { }
}
