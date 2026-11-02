using System.Collections.Generic;
using System.Linq;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules;

public static class ModuleRegistry
{
    /// <summary>
    /// Every module, in the order of their title bar buttons and their place in Settings.
    /// A new module is added here, and nowhere else outside its own folder.
    /// </summary>
    public static IReadOnlyList<IModule> All { get; } =
    [
        new PrintQueue.PrintQueueModule(),
        new Spoolman.SpoolmanModule(),
    ];

    /// <summary>The modules the user turned on that work with this printer, in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<IModule> Enabled(AppConfig config, IPrinterBackend printer) =>
        All.Where(m => config.Modules.Contains(m.Id) && m.Supports(printer)).ToList();

    /// <summary>How a module's tile is saved in stats_tiles: "print_queue:queue".</summary>
    public static string TileKey(IModule module, TileInfo tile) => $"{module.Id}:{tile.Id}";

    /// <summary>The module tile saved as this key, or null (not a module tile, or from a module this version doesn't have).</summary>
    public static (IModule Module, TileInfo Tile)? FindTile(string key)
    {
        int colon = key.IndexOf(':');
        if (colon < 0)
            return null;
        var module = All.FirstOrDefault(m => m.Id == key[..colon]);
        var tile = module?.Tiles.FirstOrDefault(t => t.Id == key[(colon + 1)..]);
        return tile == null ? null : (module!, tile);
    }
}
