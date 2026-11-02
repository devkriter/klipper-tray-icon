# Changelog

## [2.12.0] - 2026-11-02
### Added
- Stats strip: a *Time elapsed* tile to pick in Settings, showing how long the print has been
  running (preparing and pauses included) and since when, or its total once it's done.
- Stats strip: a checkbox in Settings hides it (the tiles stay as chosen, for turning it back on).
- Modules can add tiles to the stats strip, picked in Settings like the built-in ones while the
  module is on. The print queue's *QUEUE* tile shows how many jobs are queued and which is next.

### Changed
- Modules: optional features are now modules, all off until turned on in Settings under *Manage
  modules…*: a window listing them, each with its on/off, and on the right the picked one's details
  (what it does, what the printer needs, or why it can't be used with this printer). Each
  one gets a button in the popup's title bar beside a new console button, side by side, replacing
  the old console/queue toggle; with only the console, the title bar is as before. The print queue
  is the first module; if it was on, it stays on.
- CONTRIBUTING.md explains how to add a module: a folder under `Modules/` and one line to register
  it, without touching the rest of the app.

## [2.11.0] - 2026-11-01
### Added
- Updates: once a day (20 seconds after start, then daily) the app asks GitHub for the latest
  release. When there's a newer one, *Update to vX* shows in the tray menu and Settings shows
  *Update now* and *What's new*; nothing pops up. One click downloads the zip, checks it against
  GitHub's SHA-256, swaps the app's files (keeping `config.json`) and restarts into the new
  version. If anything fails the old files stay in place, with an offer to open the release page.
- Settings: the app's version, *Check for updates* (on by default) and *Check now*.

## [2.10.0] - 2026-11-01
### Added
- Print queue: a small ▶ on each queued job and recent upload prints that file now. Like Start
  next it asks first: the first click turns it into an amber *Confirm*. A queued job printed this
  way leaves the queue (and doesn't set it to start the next one by itself). While the printer is
  printing, paused, starting a job or offline, ▶ is there but greyed out.
- Console: when Klipper shuts down (or can't start), the reason shows in red with *Click to restart*
  under it, which does a firmware restart. The line goes once Klipper is running again; if it's
  still down after 20 seconds, it says *Failed to restart* with *Click to try again*.
- The title bar and tray tooltip say *Shut down* (or *Couldn't start*) while Klipper needs a
  restart, with a red dot in the popup.

### Changed
- Print queue: the row buttons (▶, ✕ and now ＋) only show when the mouse is over the row.
- Tray icon: a thicker ring with a solid track, so it stands out at 16px on any taskbar, including
  one tinted by the wallpaper. A print that's only just under way shows a short stub of progress
  rather than a sliver.
## [2.9.2] - 2026-11-01
### Fixed
- Console: when the printer can't be reached, one line says so and counts down to the next try
  ("Retrying in 5 s"), trying again after 3, 5, 10 and then every 30 seconds (at once when the
  popup opens). It goes away as soon as the console is back, instead of staying in the log.
- Print queue: adding a recent upload (＋ or by dragging) happens in one step: the file goes to the
  queue and the next upload moves up into the list at once, instead of rows jumping around while
  the printer confirms. Removing a job is shown at once the same way, and the lists only change
  the rows that actually did on each refresh.
## [2.9.1] - 2026-10-31
### Changed
- Settings: the API key (and Bambu access code) has an eye inside its field to show or hide it,
  and *Test connection* sits right beside it; the test's result replaces the hint underneath.
  One row fewer.
- Stats strip: a chamber controlled by a `temperature_fan` shows its target ("of 51°") like the
  nozzle and bed, highlighted while it's still well below it.

## [2.9.0] - 2026-10-31
### Changed
- Settings › Stats strip is drawn as the strip itself: six slots, left to right, with the labels
  the popup shows. Click one to pick what goes in it (temperatures, progress, layer, time left or
  none); picking a tile shown in another slot swaps the two, which is how you reorder.
- Settings: *Test connection* sits right under the address and API key (or the Bambu details),
  before the camera and popup options.
- Settings, U1 Extended: the touchscreen is a choice in the *Second view* list (with a line saying
  what it is), instead of a checkbox with a long explanation. It's only listed when the printer
  serves its screen (Remote Screen on), and picked by default then; a U1 can also have a second
  camera as its second view now.
- Settings: less space between the printer section and General.
- Print queue: drag a queued job by any part of its row to reorder it, and drag a recent upload
  into the queue to put it exactly there (＋ still adds it at the end). The queue opens a gap where
  the row will land. The grip is gone: an open hand cursor over a row shows it can be picked up,
  and a closed one while it's held (XCursor Pro's Light "Pan" and "Grabbing": white, like
  Windows' default arrow, and 32 to 128 px, so they're sharp at any display scale).
- The webcam popup moves only when dragged by its title bar, so dragging in the print queue or the
  recent uploads doesn't take the window with it.

### License
- The app is now GPL-3.0 (was MIT), as the hand cursors from XCursor Pro are GPL-3.0. The release
  includes the license as LICENSE.txt.

## [2.8.0] - 2026-10-31
### Added
- Preparing to print: from the start of a print until its first extrusion (start macro, heating,
  heat soak) the tray icon is a gold ring and the status reads "Preparing to print". Klipper
  doesn't start its print clock until the first extrusion, which is how it's detected; Bambu
  reports it as PREPARE.
- While printing, the progress fills a faint green track, so the icon doesn't vanish at 0%.
- Chamber temperature in the popup's stats strip, on printers with a chamber sensor (Klipper's
  `heater_generic`, `temperature_fan` or `temperature_sensor` named "chamber", or with "chamber"
  in its name). A heated chamber shows its target like the bed; a sensor or fan just the
  temperature.
- Settings › Stats strip: choose up to 6 tiles for the strip: nozzle, bed, progress, layer, time
  left and any of the printer's temperature sensors (MCU, host, chamber…), shown in that order.
  Untouched, it's nozzle, bed, chamber (if there is one), progress, layer and time left; with
  none chosen, there's no strip. Config key: `stats_tiles`.
- The webcam popup's title bar names the file being printed, after the progress
  ("mikron · Printing · 42% · Benchy"). It's dimmer, and trimmed first when the window is narrow.

### Changed
- Stats strip: a value too wide for its tile (six tiles, "1h 34m") shrinks to fit rather than
  being cut off; the layer shows "—" while a print is preparing.

## [2.7.2] - 2026-10-31
### Changed
- Print queue: recent uploads leave out files that are already queued. Adding one moves it up into
  the queue at once; removing it from the queue brings it back.

### Added
- Print queue: *Load more* under the recent uploads shows five more at a time (back to five when
  the popup reopens).

## [2.7.1] - 2026-10-31
### Added
- Print queue: hover over a thumbnail for a larger preview (the slicer's biggest thumbnail), after
  a short delay like a tooltip. Moving to another thumbnail while it's open switches straight away.

## [2.7.0] - 2026-10-31
### Added
- **Print queue** in the webcam popup (Klipper and U1 Extended; off by default, turn it on in
  Settings). A title-bar button swaps the console for the printer's print queue (Moonraker's job
  queue, the one Mainsail shows):
  - queued jobs with the slicer's thumbnail and time estimate; drag the handle to reorder, ✕ to
    remove;
  - the five most recent uploads, each with ＋ to add it to the queue;
  - *Start next* in the header, which needs a second click ("Confirm start") and only
    shows when the printer can start a print.

  The Settings option is greyed out for printers whose Moonraker has no job queue. Config key:
  `print_queue`.

## [2.6.0] - 2026-10-31
### Changed
- The **stream dropdowns** in Settings list every protocol the app supports (RTSP, H.264, MJPEG,
  Snapshots); the ones a camera doesn't offer are greyed out and can't be picked. H.264's
  description now says why RTSP is the better choice when both are offered.
- The **console shows the same messages as Mainsail**. It reads Mainsail's console settings from
  the printer (*Hide temperatures*, *Hide timelapse*, custom filters, and *clear console*) and
  applies them exactly as Mainsail v2.19 does. Changes made in Mainsail apply the next time the
  popup opens. Lines are also shown as Mainsail shows them (no `echo:` prefix; `action:` and
  `debug:` lines dimmed), and the history goes back as far as Mainsail's (500 lines).

### Removed
- The *Hide the commands timelapse sends on every frame* option: Mainsail's *Hide timelapse*
  setting now decides that. Consecutive temperature reports are no longer merged into one line
  (Mainsail hides them by default).

## [2.5.0] - 2026-10-31
### Added
- **U1 Extended** printer type, for the Snapmaker U1 with paxx12's extended firmware (it's
  required). It's Klipper plus one clear option, *Show the printer's touchscreen in the webcam
  popup, and let me use it*, in place of the second camera. The touchscreen is found at its known
  address, not guessed from the webcam list. If it doesn't show, the popup says to turn on Remote
  Screen. Config: `"printer_type": "u1"`, `u1_touchscreen`.

### Changed
- With the **Klipper** type, a second view is always just another camera: clicks never go to the
  printer. Settings suggests U1 Extended when the printer looks like a U1. *Already using a U1
  with the Klipper type and `gui` as the second view? Switch to U1 Extended to keep using its
  touchscreen.*

## [2.4.0] - 2026-10-31
### Added
- **RTSP** in the stream picker, for camera-streamer with RTSP turned on (in Crowsnest:
  `enable_rtsp: true` and `rtsp_port: 8554` in `crowsnest.conf`). The app finds it through
  camera-streamer's status page, and *Automatic* tries it first: H.264 like `video.h264`, through
  camera-streamer's separate RTSP server. If it keeps dropping, *Automatic* switches to MJPEG.
  Config value: `rtsp`.
- After switching from RTSP or H.264 to MJPEG, *Automatic* tries the better stream again after
  5 minutes, so a passing outage (like a camera service restarting) doesn't cost it for good.
- **Full-resolution H.264** when the popup is big enough to show it (pinned, snapped, maximized).
  At the default size the picture is still converted at half size, since that's all the screen
  shows; the switch happens as you resize, without reconnecting.
- H.264 pictures are converted to screen colours with SIMD: about 8× faster (a 1080p frame in
  ~2 ms instead of ~19 ms), so full resolution costs less than half did before.
- Each stream in the Settings dropdowns has a short description of what it is and what it costs.

### Fixed
- RTSP cameras that send a packet before answering PLAY (LIVE555, which camera-streamer uses) were
  rejected; the app now keeps any video that arrives early and skips the rest.
- **Cancel** now closes the settings window when it was opened from the tray menu.
- **Printers with several extruders** (toolchangers like the Snapmaker U1, IDEX): the nozzle tile
  shows the extruder in use, labelled e.g. "NOZZLE T2", instead of always the first one.

## [2.3.0] - 2026-10-31
### Added
- **Stream picker** next to each camera in Settings (Klipper). The app finds out what each camera
  offers and lists only that:
  - **H.264**: camera-streamer's hardware-encoded video (`video.h264`), decoded by Windows. Far
    less network traffic than MJPEG (about 4 instead of 80 Mbit/s for a 1080p camera).
  - **MJPEG**, as before.
  - **Snapshots**, refreshed about ten times a second.

  *Automatic* uses the first of these the camera has. If H.264 keeps dropping (3 times in 30 s),
  it switches to MJPEG until the settings change or the app restarts. Config keys:
  `webcam_stream`, `second_view_stream` (`h264`, `mjpeg`, `snapshots`; empty: automatic).
- *Automatic* in the camera list now names the camera it picks, e.g. "Automatic (Chamber)".

## [2.2.0] - 2026-10-31
### Added
- **Camera picker** (Settings, Klipper): choose which of the printer's webcams the popup shows.
  The list is read from Moonraker once the printer has been reached (on opening Settings, and
  after *Test connection*). Config key: `webcam_name` (empty: the first camera, as before).
- **Second view** (Settings, Klipper): a second webcam the popup can switch to with a button
  in the title bar, e.g. the printer's touchscreen. Config key: `second_view_name`.
- **Clickable remote touchscreens**: on a view that takes touches (the remote screen of paxx12's
  extended firmware for the Snapmaker U1), clicks and drags in the popup are sent to the printer.
  **Touch starts locked** (view only): unlock it with the lock button in the title bar. While
  unlocked, the picture has an amber outline. It locks again when the popup closes or the view
  changes, and the click that brings the window forward is never sent to the printer.
- **Title bar** on the webcam popup with the printer's name and state, and all its buttons (switch
  view, touch lock, pin, close). Drag it to move the window; double-click to maximize. The stats
  strip gets its full width back.
- Cameras that aren't plain MJPEG (WebRTC, HLS, iframe) now work when the server also serves MJPEG
  next to them (camera-streamer, paxx12's firmware), and otherwise fall back to polling their
  snapshot about ten times a second. Before, only MJPEG webcams were found.

### Fixed
- A camera that hangs up every few seconds (the Snapmaker U1's full-rate MJPEG) is reconnected
  straight away, without flashing the "reconnecting" badge.

## [2.1.1] - 2026-10-31
### Fixed
- Live video no longer freezes when the popup stays open for a long time. A camera stream that
  goes quiet for 10 seconds or ends is reconnected automatically (after 1 s, backing off to 10 s),
  with a "reconnecting" badge over the last frame meanwhile. Applies to Klipper and Bambu.

## [2.1.0] - 2026-10-31
### Added
- **Stats strip** above the Klipper console: nozzle and bed temperature (current and target;
  amber while still heating), progress with a bar, layer (from Klipper, else worked out from Z and
  the file's layer heights, else Z in mm) and time left with the finish time. While the popup is
  open the printer is checked every second.
- **Pin button** next to the close button: a pinned popup stays open when you click elsewhere,
  isn't always on top, gets a taskbar button and keeps its position and size. A tray click brings
  it forward if it's behind other windows.
- The popup can be **dragged from anywhere** and **resized**, and works with Windows snapping
  (screen edges, Win+arrows, Snap Layouts). Double-click maximizes.
- In tall windows (e.g. snapped to half a screen) the console **moves below the video**,
  whichever arrangement leaves the video bigger.

## [2.0.1] - 2026-10-30
### Added
- Console option **"Hide the commands timelapse sends on every frame"** (Settings, under the
  console option; on by default). Hides the repeated `TIMELAPSE_TAKE_FRAME` /
  `SET_GCODE_VARIABLE MACRO=TIMELAPSE_...` (and hyperlapse) commands. Klipper's responses are
  never hidden, so timelapse errors still show. Config key: `console_hide_timelapse`.

## [2.0.0] - 2026-10-30
### Changed
- **Rewritten in C# / .NET 10 (WPF)**, replacing the Python app. Same features and the same
  `config.json` format; *Start with Windows* carries over from 1.x.
  - Native Windows 11 look for the settings window (Fluent theme, follows light/dark mode) and a
    dark-mode tray menu.
  - Starts in about 0.6 s (was about 1 s).
  - One process instead of two: the webcam popup is a hidden window inside the app, shown in
    about 10 ms.
  - Release is a folder with one `KlipperTrayIcon.exe` and WPF's few native DLLs. The .NET
    runtime is included, so there's nothing else to install.
  - **Bambu camera without go2rtc and ffmpeg**: the app now talks RTSPS to the printer itself
    and decodes the H.264 video with Windows' built-in decoder. The release is ~150 MB smaller
    unpacked, and live video starts sooner (no helper processes to launch). On Windows "N"
    editions this needs Microsoft's Media Feature Pack.
  - The release zip is 66 MB (1.6.0 was 92 MB).
- Console readability: Klipper's responses (macro output, warnings, `//` lines) are now the
  brightest text instead of faded gray; the `//` and `!!` markers are dropped; errors are
  semi-bold red; commands are a softer blue; larger text and a wider panel. Multi-line messages
  stay aligned under the first line.
### Added
- Launching the exe again while it's running opens the webcam popup.
- `--settings` command-line option opens Settings on startup.
- Instant webcam popup with a snapshot shown while live video connects, the Klipper console
  history loaded in advance, temperature reports collapsed into one updating line, and
  click-the-tray-icon-again to close (these were built for the Python version first, see below).
- Bambu: partial status updates are merged, so printers that only send changed fields after the
  first report keep showing the file name and state.
- Errors are logged to `error.log` next to the exe instead of crashing the app.

### Changed (Python version, never released; carried into 2.0.0)
- Webcam popup opens almost instantly: about 20 ms instead of about 1 s. The tray now
  keeps the popup ready in a hidden background process and only shows it on click,
  instead of starting a new process every time.
- The popup shows a recent snapshot immediately (marked "Snapshot · connecting to live
  video…") while the live stream connects, so a picture appears within about 60 ms instead
  of 0.7 to 1.5 s. For Klipper, the snapshot is refreshed every 20 s while the popup is
  hidden; for Bambu, it's the last frame you saw.
- The Klipper console history is loaded in advance, so it's there the moment the popup
  opens.
- The webcam address is looked up once and reused instead of on every click.
- Clicking the tray icon while the popup is open now closes it.
- Console: the once-a-second temperature reports Klipper prints while heating
  (e.g. `B:109.3 /110.0 T0:230.6 /250.0`) are collapsed into a single amber line that updates
  in place, instead of filling the panel.

## [1.6.0] - 2026-10-25
### Added
- Klipper console beside the webcam: the webcam window shows Moonraker's console history
  (the same lines as Mainsail's console) and follows new lines live. Commands, errors (`!!`)
  and `//` messages are color-coded. Read-only. Can be turned off in Settings. Klipper only.

## [1.5.0] - 2026-10-22
### Added
- Settings window (right-click → Settings…) replacing hand-editing `config.json`: printer type,
  connection details with a **Test connection** button, refresh interval, notifications and
  Start with Windows. Matches the Windows light/dark theme. Changes apply live, no restart.
- First-run welcome window replacing the chain of Yes/No and text prompts.
- Notifications when a print finishes, fails, is cancelled or pauses.
- Status lines (progress, time left, ETA, file name) at the top of the tray menu.
- Distinct "offline" state (gray ring with red dot) with a specific reason, e.g. "Printer
  rejected the access code" or "Can't reach http://mainsail.local".
- Optional Moonraker API key in Settings (with an info tooltip on when it's needed), for
  setups where Moonraker doesn't already trust the PC, e.g. over Tailscale or a VPN.
- Proper app icon and version info on the exe.

### Changed
- Release is now a zip containing an app folder instead of a single self-extracting `.exe`,
  which Windows Defender was flagging as a virus. go2rtc and ffmpeg now sit next to the exe.
- Klipper addresses without `http://` (e.g. `mainsail.local`) are accepted.

### Fixed
- ffmpeg download for the Bambu camera 404'd (upstream dropped the 7.1 build); now uses 8.1.
- `build.bat` now installs `requirements.txt`, so builds can't silently miss `paho-mqtt`.

## [1.4.2] - 2026-07-18
### Fixed / Clarified
- Corrected docs and the first-run prompt: Bambu Lab support does **not** require the printer's
  full "LAN Only Mode". Local MQTT status and the RTSPS camera both work with the printer in its
  normal cloud-connected mode — the only camera-specific requirement is enabling **LAN Mode Live
  View**. Verified end-to-end against a real Bambu H2S (LAN Only Mode off, Live View on): both
  print progress and the live camera feed work.

## [1.4.1] - 2026-07-14
### Fixed
- Bambu RTSPS camera support (added in 1.4.0) didn't actually work: go2rtc requires an external
  `ffmpeg` binary to decode `rtsps://` sources, which wasn't bundled. Now bundles a static
  ffmpeg build alongside go2rtc.
- go2rtc's `ffmpeg.bin` config value is naively whitespace-split before being run, so any path
  containing a space (e.g. this app's own install folder, or a Windows username with a space in
  it) silently broke ffmpeg invocation. Fixed by resolving ffmpeg's Windows 8.3 short (no-space)
  path before writing it into go2rtc's config.
- The generated go2rtc config now includes the required MJPEG-transcode producer entry
  (`ffmpeg:<name>#video=mjpeg`) and a dedicated local RTSP loopback port - go2rtc's MJPEG output
  needs an explicit transcode step from the camera's native H.264 and, via ffmpeg, pulls that
  through go2rtc's own internal RTSP server, which can't be fully disabled as previously done.
- Verified end-to-end against a real Bambu H2S.

## [1.4.0] - 2026-07-13
### Added
- Bambu Lab camera support for X1/X1C/X1E/X2D/P2S/H2D/H2C/H2S via a bundled
  [go2rtc](https://github.com/AlexxIT/go2rtc) helper, which decodes the printer's RTSPS camera
  stream locally and re-exposes it as MJPEG for the existing webcam viewer. Uses the same IP and
  Access Code already configured for status polling.
### Notes
- P1P/P1S/A1/A1 mini use a different local camera protocol and are not yet supported (shows
  "Camera unavailable").

## [1.3.0] - 2026-07-13
### Added
- Bambu Lab printer support via local LAN Only Mode MQTT (no cloud account required). Select
  printer type on first run and enter the printer's IP, serial number, and access code.
- Introduced a printer backend abstraction (`backends.py`) so Klipper/Moonraker and Bambu Lab
  share the same tray icon, tooltip, and status logic.
### Notes
- Webcam support remains Klipper/Moonraker only for this release.

## [1.2.0] - 2026-02-12
### Improved
- ETA Calculation: Now fetches file metadata from Moonraker to use the **Slicer Estimated Time** if available, providing much more accurate time remaining estimates than simple linear projection.
- Tooltip: Added source indicator for ETA (e.g., "Slicer" vs "Calc").

## [1.1.0] - 2026-02-10
### Added
- Webcam Streaming: Left-click (or 'Show Webcam' in menu) to view a live feed.
- Borderless Popup Window: Webcam window mimics a native system flyout (borderless, stays on top, auto-close on focus loss).
- Smart Shutdown: Application now exits instantly without waiting for the update loop.

## [1.0.0] - 2026-02-10
### Added
- Initial Release.
- Tray icon showing circular progress for Klipper prints.
- Tooltip with status: Percentage, Time, ETA, Filename.
- Single EXE build support.
- Configurable Moonraker/Mainsail URL with auto-prompt on first run.
