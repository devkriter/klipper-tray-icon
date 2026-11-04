using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KlipperTrayIcon.Modules;
using KlipperTrayIcon.Modules.Commands;
using KlipperTrayIcon.Printers;
using Xunit;

namespace KlipperTrayIcon.Tests;

/// <summary>The Console commands module: suggestions, "did you mean", pins and history.</summary>
public class CommandsTests
{
    // As /printer/gcode/help lists them (a macro's name can come in lower case).
    private static readonly Dictionary<string, string> Help = CommandText.ReadHelp(JsonDocument.Parse("""
        {"G28":"Move to origin (home)","M104":"Set extruder temperature","M140":"Set bed temperature","M84":"Disable steppers",
         "BED_MESH_CALIBRATE":"Perform Mesh Bed Leveling","BED_MESH_CLEAR":"Clear the Mesh so no z-adjustment is made",
         "BED_MESH_PROFILE":"Bed Mesh Persistent Storage management","BED_MESH_OUTPUT":"Retrieve interpolated grid of probed points",
         "BED_MESH_MAP":"Serialize mesh and output to terminal","QUAD_GANTRY_LEVEL":"Conform a moving, twistable gantry to the shape of a stationary bed",
         "FIRMWARE_RESTART":"Restart firmware, host, and reload config","RESTART":"Reload config file and restart host software",
         "print_start":"G-Code macro"}
        """).RootElement);

    [Fact]
    public void ReadsThePrintersCommands()
    {
        Assert.Equal(13, Help.Count);
        Assert.Equal("G-Code macro", Help["PRINT_START"]);
        Assert.Equal("Move to origin (home)", Help["g28"]); // found however it's typed
        Assert.Empty(CommandText.ReadHelp(default));
    }

    [Fact]
    public void CapitalisesOnlyTheCommandWord()
    {
        Assert.Equal("SET_LED LED=caselight", CommandText.Capitalize("set_led LED=caselight"));
        Assert.Equal("M117 hello there", CommandText.Capitalize("m117 hello there"));
        Assert.Equal("  G28 x", CommandText.Capitalize("  g28 x"));
        Assert.Equal("", CommandText.Capitalize(""));
        Assert.Equal("BED_M", CommandText.Capitalize("bed_m"));
    }

    [Fact]
    public void SuggestsCommandsWithTheWordAnywhere()
    {
        var typed = CommandText.Suggest(Help, "BED_M");
        Assert.Equal(["BED_MESH_MAP", "BED_MESH_CLEAR", "BED_MESH_OUTPUT", "BED_MESH_PROFILE", "BED_MESH_CALIBRATE"],
                     typed.Select(s => s.Name));
        Assert.Equal(new Suggestion("BED_MESH_MAP", "Serialize mesh and output to terminal", 0, 5), typed[0]);
        Assert.Equal("ESH_MAP", CommandText.Ghost("BED_M", typed));

        // In the middle too, after those it starts: the match is where it's found, for highlighting.
        var mesh = CommandText.Suggest(Help, "mesh");
        Assert.Equal(5, mesh.Count);
        Assert.All(mesh, s => Assert.Equal((4, 4), (s.MatchStart, s.MatchLength)));
        Assert.Equal("", CommandText.Ghost("mesh", mesh)); // only for a suggestion it starts
        var restart = CommandText.Suggest(Help, "RESTART");
        Assert.Equal(["FIRMWARE_RESTART"], restart.Select(s => s.Name)); // not the one typed

        // Done once a space is typed, and nothing for an empty box.
        Assert.Empty(CommandText.Suggest(Help, "M104 S"));
        Assert.Empty(CommandText.Suggest(Help, ""));
        Assert.Equal(2, CommandText.Suggest(Help, "BED_MESH", max: 2).Count);
    }

    [Fact]
    public void AsksAboutATypo()
    {
        Assert.Equal("G28", CommandText.DidYouMean(Help, "G2B"));
        Assert.Equal("M104", CommandText.DidYouMean(Help, "M1o4 S240"));
        Assert.Equal("QUAD_GANTRY_LEVEL", CommandText.DidYouMean(Help, "QUAD_GANTY_LEVL"));
        // Nothing close: just that it isn't a command.
        Assert.Equal("", CommandText.DidYouMean(Help, "HELLO_THERE"));
        // Fine: a command, or part of one, or nothing typed; or the list isn't there yet.
        Assert.Null(CommandText.DidYouMean(Help, "g28"));
        Assert.Null(CommandText.DidYouMean(Help, "BED_M"));
        Assert.Null(CommandText.DidYouMean(Help, " "));
        Assert.Null(CommandText.DidYouMean(new Dictionary<string, string>(), "G2B"));

        Assert.Equal("G28 X Y", CommandText.ReplaceWord("G2B X Y", "G28"));
        Assert.Equal("BED_MESH_CALIBRATE", CommandText.ReplaceWord("BED_M", "BED_MESH_CALIBRATE"));
        Assert.Equal(1, CommandText.Distance("G2B", "G28"));
        Assert.Equal(3, CommandText.Distance("", "M84"));
        Assert.Equal(0, CommandText.Distance("M84", "M84"));
    }

    [Fact]
    public void PinsAreReadFromTheSetting()
    {
        var pins = Pin.Parse(CommandsModule.Pinned.Text!);
        Assert.Equal(6, pins.Count);
        Assert.Equal(new Pin("Home", "G28"), pins[0]);
        Assert.Equal(new Pin("Nozzle", "M104 S{240}"), pins[4]);
        Assert.False(pins[0].HasValue);
        Assert.True(pins[4].HasValue);

        // Blank lines skipped; a line without a name is named after itself; Windows line ends too.
        Assert.Equal([new Pin("STATUS", "STATUS"), new Pin("Lights", "SET_LED LED=x WHITE=1")],
                     Pin.Parse("\r\n  STATUS \r\n\r\nLights:SET_LED LED=x WHITE=1\r\n"));
        Assert.Equal("Home: G28\nNozzle: M104 S{240}", Pin.Format([pins[0], pins[4]]));
        Assert.Equal(pins, Pin.Parse(Pin.Format(pins)));
        // Pinned from a recent command: named after it (a colon would split it).
        Assert.Equal(new Pin("M117 Hi 1230", "M117 Hi 12:30"), Pin.From("M117 Hi 12:30"));
        Assert.Equal([Pin.From("M117 Hi 12:30")], Pin.Parse(Pin.Format([Pin.From("M117 Hi 12:30")])));
    }

    [Fact]
    public void APinsValueIsFilledInAndSelected()
    {
        Assert.Equal(("M104 S240", 6, 3), new Pin("Nozzle", "M104 S{240}").Fill());
        Assert.Equal(("M140 S60 T0", 6, 2), new Pin("Bed", "M140 S{60} T{0}").Fill());
        Assert.Equal(("M104 S", 6, 0), new Pin("Nozzle", "M104 S{}").Fill());
        Assert.Equal(("G28", 3, 0), new Pin("Home", "G28").Fill());
    }

    [Fact]
    public void TheLastHundredCommandsAreKeptPerPrinter()
    {
        string path = Path.Combine(Path.GetTempPath(), $"command_history_{Guid.NewGuid():N}.json");
        try
        {
            var voron = new CommandHistory(path, "http://voron");
            Assert.Empty(voron.Recent());
            Assert.Null(voron.Older());
            foreach (string command in new[] { "G28", "QUAD_GANTRY_LEVEL", "G28", "PROBE_ACCURACY SAMPLES=10", " ", "G28" })
                voron.Add(command);
            Assert.Equal(["G28", "PROBE_ACCURACY SAMPLES=10", "QUAD_GANTRY_LEVEL"], voron.Recent());
            Assert.Equal(["G28"], voron.Recent(1));

            // Up and Down, as in a terminal: Down past the newest is the empty box again.
            Assert.Null(voron.Newer());
            Assert.Equal("G28", voron.Older());
            Assert.Equal("PROBE_ACCURACY SAMPLES=10", voron.Older());
            Assert.Equal("QUAD_GANTRY_LEVEL", voron.Older());
            Assert.Null(voron.Older());
            Assert.Equal("PROBE_ACCURACY SAMPLES=10", voron.Newer());
            Assert.Equal("G28", voron.Newer());
            Assert.Equal("", voron.Newer());
            voron.Older();
            voron.StopStepping();
            Assert.Equal("G28", voron.Older());

            // Each printer its own, in the one file, read back.
            var other = new CommandHistory(path, "http://ender");
            other.Add("M84");
            Assert.Equal(["G28", "PROBE_ACCURACY SAMPLES=10", "QUAD_GANTRY_LEVEL"], new CommandHistory(path, "http://voron").Recent());
            Assert.Equal(["M84"], new CommandHistory(path, "http://ender").Recent());

            for (int i = 0; i < 150; i++)
                voron.Add($"M117 {i}");
            var again = new CommandHistory(path, "http://voron");
            Assert.Equal(CommandHistory.Max, again.All.Count);
            Assert.Equal("M117 149", again.Recent()[0]);
            Assert.Equal("M117 50", again.All[0]);

            File.WriteAllText(path, "not json");
            Assert.Empty(new CommandHistory(path, "http://voron").Recent()); // unreadable: starts over
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AListSettingCanBeEmptied()
    {
        var pinned = CommandsModule.Pinned;
        Assert.True(pinned.IsMultiLine);
        Assert.Equal(6, pinned.Lines);
        Assert.False(ModuleSetting.ForText("macro", "Macro", "X").IsMultiLine);
        Assert.Equal(pinned.Text, pinned.TextIn(null));
        Assert.Equal("", pinned.TextIn(new Dictionary<string, string> { ["pinned"] = "" }));
        Assert.Equal("Home: G28\nQGL: QUAD_GANTRY_LEVEL", pinned.TextIn(new Dictionary<string, string> { ["pinned"] = "Home: G28\nQGL: QUAD_GANTRY_LEVEL\n" }));

        // Saved from the popup through the context.
        (ModuleSetting, string)? saved = null;
        var context = new ModuleContext(new MoonrakerBackend("http://p"), default) { TextSaver = (s, v) => saved = (s, v) };
        context.SaveText(pinned, "Home: G28");
        Assert.Equal((pinned, "Home: G28"), saved);
        new ModuleContext(new MoonrakerBackend("http://p"), default).SaveText(pinned, "x"); // nowhere to save: nothing happens
    }

    [Fact]
    public void TheModuleIsABoxUnderTheConsole()
    {
        var module = (IModule)ModuleRegistry.All.Single(m => m.Id == "commands");
        Assert.Equal("Console commands", module.Name);
        Assert.Empty(module.Tiles);
        Assert.Null(module.CreatePanel(new ModuleContext(new MoonrakerBackend("http://p"), default)));
        using var klipper = new MoonrakerBackend("http://p");
        using var bambu = new BambuBackend("1.2.3.4", "S", "C");
        Assert.True(module.Supports(klipper));
        Assert.False(module.Supports(bambu));
    }

    /// <summary>A fake Moonraker that records the scripts it's sent, and says no to one.</summary>
    private sealed class FakeMoonraker : HttpMessageHandler
    {
        public readonly List<string> Scripts = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            const string script = "/printer/gcode/script?script=";
            if (request.Method == HttpMethod.Post && path.StartsWith(script))
                Scripts.Add(path[script.Length..]);
            var status = path.EndsWith("G2B") ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"result":"ok"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task SendsWhatWasTyped()
    {
        var printer = new FakeMoonraker();
        using var backend = new MoonrakerBackend("http://p", http: new HttpClient(printer));
        await backend.SendGcodeAsync("SET_LED LED=x WHITE=0.5 # half", default);
        Assert.Equal(["SET_LED LED=x WHITE=0.5 # half"], printer.Scripts);
        var refused = await Assert.ThrowsAsync<MoonrakerHttpException>(() => backend.SendGcodeAsync("G2B", default));
        Assert.Equal(400, (int)refused.StatusCode);
    }
}
