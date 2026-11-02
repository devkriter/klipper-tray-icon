using System.Text.Json;
using KlipperTrayIcon.Modules;
using KlipperTrayIcon.Modules.PrintQueue;
using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The module list, and turning modules on and off in config.json.</summary>
public class ModuleTests
{
    [Fact]
    public void ModulesHaveDistinctIdsAndWhatSettingsShows()
    {
        var ids = ModuleRegistry.All.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var module in ModuleRegistry.All)
        {
            Assert.Matches("^[a-z0-9_]+$", module.Id); // config.json keys' style
            Assert.False(string.IsNullOrWhiteSpace(module.Name));
            Assert.False(string.IsNullOrWhiteSpace(module.Description));
            Assert.False(string.IsNullOrEmpty(module.Icon ?? module.Glyph)); // a drawn icon, or a glyph
            if (module.Icon != null)
                System.Windows.Media.Geometry.Parse(module.Icon); // throws if it isn't path markup
            // The Modules window's details: optional, but a link must open in the browser.
            Assert.NotNull(module.Details);
            Assert.NotNull(module.Needs);
            Assert.NotNull(module.Author);
            if (module.HelpUrl != null)
                Assert.StartsWith("https://", module.HelpUrl);
        }
    }

    [Fact]
    public void ModuleTilesAreFoundByTheirSavedKey()
    {
        var keys = ModuleRegistry.All.SelectMany(m => m.Tiles.Select(t => ModuleRegistry.TileKey(m, t))).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var module in ModuleRegistry.All)
            foreach (var tile in module.Tiles)
            {
                string key = ModuleRegistry.TileKey(module, tile);
                Assert.Matches("^[a-z0-9_]+:[a-z0-9_]+$", key);
                Assert.True(StatusText.IsModuleTile(key));
                Assert.False(StatusText.IsSensorTile(key));
                Assert.Equal((module, tile), ModuleRegistry.FindTile(key));
                Assert.Equal(tile.Label, StatusText.TileLabel(key));
                Assert.Equal(tile.Label.ToUpperInvariant(), tile.Label);
                Assert.True(tile.Label.Length <= 12, $"{key}'s label is too long for a tile");
                Assert.NotNull(module.CreateTile(tile.Id, new ModuleContext(new MoonrakerBackend("http://p"), default)));
            }
        Assert.Equal("print_queue:queue", Assert.Single(keys));
        Assert.Null(ModuleRegistry.FindTile("print_queue:nope"));
        Assert.Null(ModuleRegistry.FindTile("from_a_newer_version:tile"));
        Assert.Null(ModuleRegistry.FindTile(StatusText.Nozzle));
        Assert.False(StatusText.IsModuleTile("temperature_fan chamber"));
    }

    [Fact]
    public void TheQueueTile()
    {
        Assert.Equal(new StatusText.Tile("—", ""), QueueTile.Describe(null));
        Assert.Equal(new StatusText.Tile("0", "empty"), QueueTile.Describe(new QueueStatus("ready", [])));
        Assert.Equal(new StatusText.Tile("2", "next Benchy"),
            QueueTile.Describe(new QueueStatus("paused", [new("1", "parts/Benchy.gcode"), new("2", "b.gcode")])));
    }

    [Fact]
    public void AHiddenStripAsksForNoTiles()
    {
        var config = new AppConfig { StatsTiles = ["nozzle", "temperature_fan chamber"] };
        Assert.True(config.ShowStats); // shown at first
        Assert.Equal(config.StatsTiles, config.ShownStatsTiles);
        config.ShowStats = false;
        Assert.Empty(config.ShownStatsTiles!);
        Assert.Equal(2, config.StatsTiles.Count); // kept for turning it back on
        Assert.Null(new AppConfig().ShownStatsTiles); // the default tiles
    }

    [Fact]
    public void ModulesAreOffAtFirst() => Assert.Empty(new AppConfig().Modules);

    // As ConfigStore.Load reads config.json.
    private static AppConfig Load(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, ConfigStore.JsonOptions)!.Upgrade();

    [Fact]
    public void TheOldQueueSettingBecomesTheModule()
    {
        var config = Load("""{"print_queue": true}""");
        Assert.Equal(["print_queue"], config.Modules);
        // Saved without the old key.
        Assert.DoesNotContain("\"print_queue\":", JsonSerializer.Serialize(config, ConfigStore.JsonOptions).Replace(" ", ""));
        Assert.Empty(Load("""{"print_queue": false}""").Modules);
        Assert.Equal(["print_queue"], Load("""{"print_queue": true, "modules": ["print_queue"]}""").Modules);
    }

    [Fact]
    public void OnlyModulesThatSuitThePrinterAreEnabled()
    {
        var config = new AppConfig { Modules = ["print_queue", "from_a_newer_version"] };
        using var klipper = new MoonrakerBackend("http://p");
        using var bambu = new BambuBackend("1.2.3.4", "S", "C");
        Assert.Equal(["print_queue"], ModuleRegistry.Enabled(config, klipper).Select(m => m.Id));
        Assert.Empty(ModuleRegistry.Enabled(config, bambu)); // the queue is Moonraker's
        Assert.Empty(ModuleRegistry.Enabled(new AppConfig(), klipper));
    }
}
