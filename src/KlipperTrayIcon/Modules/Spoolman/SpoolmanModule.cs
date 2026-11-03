using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Spoolman;

/// <summary>Spoolman, through Moonraker: the spool in the printer, what's left on it, and the other spools to switch to.</summary>
public sealed class SpoolmanModule : IModule
{
    public string Id => "spoolman";
    public string Name => "Spoolman";
    public string Description => "Your filament spools from Spoolman: the one in the printer, how much is left, and whether it's enough for the print.";
    public string Glyph => "";
    // A spool: its rim, its hub, and the filament wound between them.
    public string? Icon => "M14.3,8 A6.3,6.3 0 1 1 1.7,8 A6.3,6.3 0 1 1 14.3,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 Z M8,1.7 V6 M8,10 V14.3";

    public string Details =>
        "Shows the spool Moonraker is tracking, with what's left in grams and metres, and checks it against the " +
        "file's slicer data while printing: whether there's enough left, and whether it's the material the file " +
        "was sliced for. On a printer with several toolheads (T0, T1, … macros with a spool_id, as Mainsail and " +
        "Fluidd use them), each toolhead gets a card; click one to pick which tool Use puts a spool in.\n\n" +
        "The other spools are listed below it, most recently used first, to switch to; hover over one for its " +
        "details. While printing, Use asks for a second click. Adds a SPOOL tile for the stats strip.";
    public string Needs => "Klipper with Spoolman connected to Moonraker: a [spoolman] section in moonraker.conf with Spoolman's address.";
    public string? HelpUrl => "https://moonraker.readthedocs.io/en/latest/configuration/#spoolman";
    public string Author => "Klipper Tray Icon";

    public bool Supports(IPrinterBackend printer) => printer is IMoonrakerApi;

    public async Task<string?> CheckAsync(IPrinterBackend printer, CancellationToken ct) =>
        await new SpoolmanApi((IMoonrakerApi)printer).HasSpoolmanAsync(ct)
            ? null
            : "This printer isn't connected to Spoolman: add [spoolman] to moonraker.conf";

    public ModulePanel CreatePanel(ModuleContext context) =>
        new SpoolmanPanel(SpoolmanSource.For((IMoonrakerApi)context.Printer), context);

    public IReadOnlyList<TileInfo> Tiles { get; } = [new("spool", "Spool in use", "SPOOL")];

    public ModuleTile CreateTile(string id, ModuleContext context) =>
        new SpoolTile(SpoolmanSource.For((IMoonrakerApi)context.Printer), context.Lifetime, Levels.From(context));

    internal static readonly ModuleSetting Low = new("low", "Low below", 100, "g",
        "The grams left turn amber, as they do when a spool won't last the print.");
    internal static readonly ModuleSetting Empty = new("empty", "Nearly empty below", 50, "g", "The grams left turn red.");

    public IReadOnlyList<ModuleSetting> Settings { get; } = [Low, Empty];
}
