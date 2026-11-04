using System.Collections.Generic;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.Commands;

/// <summary>A box under the console to send commands, with the printer's own commands as suggestions.</summary>
public sealed class CommandsModule : IModule
{
    public string Id => "commands";
    public string Name => "Console commands";
    public string Description => "A box under the console for sending commands, with suggestions from the printer.";
    public string Glyph => "";
    // A prompt: › and a cursor.
    public string? Icon => "M3,4.5 L6.5,8 L3,11.5 M8,11.5 H13";
    public string Author => "Klipper Tray Icon";

    public bool Supports(IPrinterBackend printer) => printer is IMoonrakerApi;

    public ModuleConsoleInput CreateConsoleInput(ModuleContext context) =>
        new CommandBox(context, new CommandHistory(CommandHistory.DefaultPath, ((IMoonrakerApi)context.Printer).BaseUrl));

    internal static readonly ModuleSetting Pinned = ModuleSetting.ForText("pinned", "Pinned",
        "Home: G28\nQGL: QUAD_GANTRY_LEVEL\nBed mesh: BED_MESH_CALIBRATE\nMotors off: M84\nNozzle: M104 S{240}\nBed: M140 S{60}",
        "Name: command, one per line. {240} asks for a value.", lines: 6);

    public IReadOnlyList<ModuleSetting> Settings { get; } = [Pinned];
}
