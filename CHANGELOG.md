# Changelog

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
