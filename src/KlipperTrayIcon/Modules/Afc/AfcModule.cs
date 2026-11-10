using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Afc;

/// <summary>Armored Turtle's AFC filament changer: its lanes, the path to the toolhead and the sensors along it.</summary>
public sealed class AfcModule : IModule
{
    public string Id => "afc";
    public string Name => "AFC";
    public string Description => "Your filament changer's lanes, the path to the toolhead and its sensors.";
    public string Glyph => "";
    // Two lanes meeting at the hub, and the one path on to the toolhead.
    public string? Icon => "M4,1 V5 Q4,8.5 8,9.5 M12,1 V5 Q12,8.5 8,9.5 M8,9.5 V15";

    public string Details =>
        "A drawing of the unit: each lane's spool, the path through the hub and buffer to the toolhead in the " +
        "colour of the filament in it, and every sensor, green when it sees filament. Load, Eject and Unload on " +
        "hover, each after a second click; Resume after an AFC error. Adds a LANE tile.";
    public string Needs => "Klipper with AFC (Box Turtle, NightOwl and other AFC units).";
    public string? HelpUrl => "https://github.com/AFCProject/AFC-Klipper-Add-On";
    public string Author => "Klipper Tray Icon";

    public bool Supports(IPrinterBackend printer) => printer is IMoonrakerApi;

    public async Task<string?> CheckAsync(IPrinterBackend printer, CancellationToken ct) =>
        await new AfcApi((IMoonrakerApi)printer).HasAfcAsync(ct) ? null : "No AFC in Klipper";

    public ModulePanel CreatePanel(ModuleContext context) => new AfcPanel(AfcSource.For((IMoonrakerApi)context.Printer), context);

    public IReadOnlyList<TileInfo> Tiles { get; } = [new("lane", "Lane in use", "LANE")];

    public ModuleTile CreateTile(string id, ModuleContext context) =>
        new LaneTile(AfcSource.For((IMoonrakerApi)context.Printer), context.Lifetime);
}
