# Contributing

Thanks for helping! Klipper Tray Icon is meant to stay small: a tray icon, and a webcam popup with
the console. Anything beyond that is a **module**: an optional feature that is off until the user
turns it on in Settings. New features almost always belong in a module, and a module lives in its
own folder, so it can be added without touching the core of the app.

Bug fixes and small improvements to the core are welcome as pull requests too. For bigger changes
to the core, please open an issue first so we can agree on the approach.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build src/KlipperTrayIcon
dotnet test tests/KlipperTrayIcon.Tests
```

`build.bat` runs the tests and makes the release zip.

## Adding a module

The print queue (`src/KlipperTrayIcon/Modules/PrintQueue/`) is a complete module: copy its layout.

1. **Make a folder** `src/KlipperTrayIcon/Modules/YourModule/`, with the namespace
   `KlipperTrayIcon.Modules.YourModule`.
2. **Describe the module** with a class that implements `IModule` (`Modules/IModule.cs`):
   - `Id`: a lowercase key such as `"filament_tracker"`. It's saved in `config.json`, so never
     change it once it has been released.
   - `Name`, `Description`: its name, and one sentence on what it adds.
   - Optional, for the *Modules* window in Settings, where picking a module shows its details:
     `Details` (a paragraph or two on what it does and how to use it), `Needs` (what the printer
     must have, e.g. a Moonraker component), `HelpUrl` (a page with more) and `Author` (you).
   - `Glyph`: its icon (title bar button and Settings), a [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font)
     character such as `""`. If Segoe has nothing that fits, leave it empty and draw one
     instead with `Icon`: WPF path markup in a 16×16 box, drawn in lines (the Spoolman module's spool).
   - `Supports(printer)`: whether it works with this kind of printer at all. For Klipper only, use
     `printer is IMoonrakerApi`. No network calls here.
   - `CheckAsync(printer, ct)` (optional): asks the printer whether it has what the module needs.
     Return `null` if it does, or a short reason (shown in the Modules window) if not.
   - `CreatePanel(context)`: returns the module's panel.
3. **Build the panel**: a UserControl that derives from `ModulePanel`. In XAML:
   ```xml
   <modules:ModulePanel x:Class="KlipperTrayIcon.Modules.YourModule.YourPanel"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:modules="clr-namespace:KlipperTrayIcon.Modules">
   ```
   It fills the popup's side panel under the stats strip, in place of the console, while its title
   bar button is selected. Override what you need:
   - `OnStatus(status)`: each printer status update from the tray.
   - `OnPopupShown()`: the popup was opened.
   - `OnSelected(selected)`: its button was selected, or another one was.

   `IsVisible` tells you whether the panel is on screen. Poll the printer only while it is, and stop
   when `context.Lifetime` is cancelled (settings changed, or the app is exiting). Keep the popup's
   dark look: the queue's XAML has the colours and the small button styles.
4. **Talk to the printer** through `context.Printer`. For Klipper, cast it to `IMoonrakerApi`
   (`Printers/IMoonrakerApi.cs`), which calls [Moonraker's API](https://moonraker.readthedocs.io/en/latest/web_api/)
   with the address and API key already applied. Put your API calls in your folder (like
   `PrintQueueApi.cs`) rather than in `MoonrakerBackend`. To show an error, use
   `context.Printer.DescribeError(exception)`.
   **A tile for the stats strip** (optional): list it in `Tiles`, e.g.
   `[new("queue", "Queued jobs", "QUEUE")]` (id, its name in Settings' tile menu, its short label in
   capitals), and make it in `CreateTile(id, context)`: a class deriving from `ModuleTile` whose
   `Show(status)` returns the value and the line under it, e.g. `new("3", "next Benchy")`. While
   the module is on, the user can add the tile to the strip in Settings like the built-in ones.
   `Show` is called about every second while the popup is open, on the UI thread, so don't wait in
   it: fetch your data in the background (not more than every few seconds) and call `Refresh()`
   when it changes. The print queue's `QueueTile.cs` is the example.
5. **Register it**: add one line to `ModuleRegistry.All` (`Modules/ModuleRegistry.cs`). The order
   there is the order of the Modules window's list. In the title bar the console's button stays on
   the right, next to the pin, and each module's goes to the left of the ones before it. This is
   the only change needed outside your folder.
6. **Test it**: add `tests/KlipperTrayIcon.Tests/YourModuleTests.cs`. `PrintQueueTests.cs` shows
   how to test API calls against a fake Moonraker.
7. **Document it**: a line in the README's feature list and in CHANGELOG.md under *Unreleased*.

You don't need to touch Settings, the popup or `config.json` handling: the module's row and details
in the Modules window, its title bar button and saving whether it's on all come from the registry.

### Ground rules for modules

- **Off by default, and quiet.** A module is never turned on for the user, and it shouldn't pop up
  notifications or windows of its own.
- **Ask before acting.** Anything that moves, heats or starts the printer needs a confirming second
  click, like the queue's *Start next* and ▶.
- **No new dependencies** (NuGet packages) without discussing it in an issue first. The app ships
  as one self-contained zip, and every package is in it.
- **Stay in your folder.** If a module needs something from the core (a new hook, a new kind of
  printer data), open an issue or keep that change small and separate in the PR, so it can be
  reviewed on its own.
- **Fail softly.** If the printer doesn't answer, show a short message in the panel. Don't throw
  out of event handlers. A module whose `CreatePanel` throws is left out, and the error goes to
  `error.log`.

## Pull requests

- Keep each PR to one change: one module, or one fix.
- `dotnet test` must pass, and new behaviour should have tests.
- Match the code around you: comment density, naming, and the tone of the UI text (short, plain,
  sentence case).
- By contributing you agree that your work is licensed under the GPL-3.0, like the rest of the app.
