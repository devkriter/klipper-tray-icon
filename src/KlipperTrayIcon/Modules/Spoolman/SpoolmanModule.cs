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
    public string Description => "Spools from Spoolman: what's loaded, what's left, and if it's enough.";
    public string Glyph => "";
    // A spool: its rim, its hub, and the filament wound between them.
    public string? Icon => "M14.3,8 A6.3,6.3 0 1 1 1.7,8 A6.3,6.3 0 1 1 14.3,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 Z M8,1.7 V6 M8,10 V14.3";

    public string Details =>
        "The spool in each toolhead, what's left, and a check against the print's file. Switch spools with Use; " +
        "Load and Unload run the printer's macros. Adds a SPOOL tile.";
    public string Needs => "Spoolman in moonraker.conf ([spoolman])";
    public string? HelpUrl => "https://moonraker.readthedocs.io/en/latest/configuration/#spoolman";
    public string Author => "Klipper Tray Icon";

    public bool Supports(IPrinterBackend printer) => printer is IMoonrakerApi;

    public async Task<string?> CheckAsync(IPrinterBackend printer, CancellationToken ct) =>
        await new SpoolmanApi((IMoonrakerApi)printer).HasSpoolmanAsync(ct)
            ? null
            : "No [spoolman] in moonraker.conf";

    // The macro settings: a dropdown of the printer's macros.
    public async Task<IReadOnlyList<string>> ChoicesAsync(ModuleSetting setting, IPrinterBackend printer, CancellationToken ct) =>
        setting == LoadMacro || setting == UnloadMacro ? await new SpoolmanApi((IMoonrakerApi)printer).GetMacrosAsync(ct) : [];

    public ModulePanel CreatePanel(ModuleContext context) =>
        new SpoolmanPanel(SpoolmanSource.For((IMoonrakerApi)context.Printer), context);

    public IReadOnlyList<TileInfo> Tiles { get; } = [new("spool", "Spool in use", "SPOOL")];

    public ModuleTile CreateTile(string id, ModuleContext context) =>
        new SpoolTile(SpoolmanSource.For((IMoonrakerApi)context.Printer), context.Lifetime, Levels.From(context));

    internal static readonly ModuleSetting Low = new("low", "Low below", 100, "g",
        "Shown in amber");
    internal static readonly ModuleSetting Empty = new("empty", "Nearly empty below", 50, "g", "Shown in red");

    internal static readonly ModuleSetting LoadMacro = ModuleSetting.ForText("load_macro", "Load runs", "LOAD_FILAMENT",
        "{tool} = toolhead number");
    internal static readonly ModuleSetting UnloadMacro = ModuleSetting.ForText("unload_macro", "Unload runs", "UNLOAD_FILAMENT",
        "{tool} = toolhead number");

    public IReadOnlyList<ModuleSetting> Settings { get; } = [Low, Empty, LoadMacro, UnloadMacro];
}
