using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KlipperTrayIcon.Printers;

namespace KlipperTrayIcon.Modules.PrintQueue;

/// <summary>Moonraker's print queue: queued jobs, recent uploads, and starting prints.</summary>
public sealed class PrintQueueModule : IModule
{
    public string Id => "print_queue";
    public string Name => "Print queue";
    public string Description => "Queued jobs (drag to reorder), recent uploads to add, and buttons that start prints.";
    public string Glyph => ""; // a bulleted list

    public string Details =>
        "The printer's print queue, as in Mainsail: each job with its thumbnail (hover over it for a larger " +
        "preview) and the slicer's time estimate. Drag jobs to reorder them, or ✕ to remove one.\n\n" +
        "Below the queue are recent uploads that aren't queued yet (Load more for older ones): ＋ adds one at " +
        "the end, or drag it into the queue where you want it. ▶ on any row prints that file now. It and " +
        "Start next ask for a second click, so a stray click never starts a print.";
    public string Needs => "Klipper with Moonraker's job queue: add a [job_queue] section to moonraker.conf and restart Moonraker.";
    public string? HelpUrl => "https://moonraker.readthedocs.io/en/latest/configuration/#job_queue";
    public string Author => "Klipper Tray Icon";

    public bool Supports(IPrinterBackend printer) => printer is IMoonrakerApi;

    public async Task<string?> CheckAsync(IPrinterBackend printer, CancellationToken ct) =>
        await new PrintQueueApi((IMoonrakerApi)printer).HasJobQueueAsync(ct)
            ? null
            : "This printer has no print queue: add [job_queue] to moonraker.conf";

    public ModulePanel CreatePanel(ModuleContext context) =>
        new PrintQueuePanel(new PrintQueueApi((IMoonrakerApi)context.Printer), context);

    public IReadOnlyList<TileInfo> Tiles { get; } = [new("queue", "Queued jobs", "QUEUE")];

    public ModuleTile CreateTile(string id, ModuleContext context) =>
        new QueueTile(new PrintQueueApi((IMoonrakerApi)context.Printer), context.Lifetime);
}
