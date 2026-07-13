# Changelog

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
