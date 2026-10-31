# Klipper Tray Icon

A lightweight Windows system tray application that visualizes the status of your 3D print. Supports
Klipper/Moonraker/Mainsail setups, and Bambu Lab printers over the local network (no Bambu Cloud
account required, and the printer's full "LAN Only Mode" is *not* required — see below).
The icon updates dynamically to show a circular progress bar corresponding to the print progress.

## Features

- **Dynamic Tray Icon**: visualizes print progress as a circular ring.
  - 🟢 **Green Ring**: Printing
  - 🟠 **Orange Ring**: Paused
  - 🔵 **Blue Ring**: Complete
  - 🔴 **Red Ring**: Printer error
  - ⚪ **Gray Ring with red dot**: Offline (can't reach the printer — hover or right-click for why)
- **Status at a glance**: hover or right-click the icon to see progress, time left, ETA and the
  file being printed.
- **Choose the camera, and a second view** (Klipper): pick any of the printer's webcams in
  Settings, plus a second one to switch to from the popup. Remote touchscreens (e.g. the
  [Snapmaker U1's](#snapmaker-u1-support)) can be used right in the popup, once you unlock touch
  with the lock button (it starts locked, so a stray click can't press anything).
- **Webcam with console** (Klipper): the webcam popup shows the live Klipper console alongside the
  video, under a strip of key stats (nozzle and bed temperature, progress, layer, time left). It
  can be turned off in Settings.
- **Notifications** when a print finishes, fails, is cancelled or pauses.
- **Settings window**: switch printers, test the connection, change the refresh rate, and turn
  notifications and *Start with Windows* on or off. Changes apply immediately.
- **Two Printer Backends**:
  - **Klipper / Moonraker** (Mainsail, Fluidd) over HTTP.
  - **Bambu Lab** printers over local MQTT (works with the printer in normal/cloud mode — full
    "LAN Only Mode" is not required).
- **Guided setup**: a welcome window asks for your printer details on first run, with a
  *Test connection* button.
- **Instant webcam popup**: left-click the tray icon. A recent snapshot appears immediately while
  the live stream connects.
- **Pin it as a window**: the pin button keeps the popup open like a normal window. Drag it from
  anywhere, resize it, or snap it to the side of the screen; in tall windows the console moves
  below the video.
- **Native Windows app**: built with C# / .NET and the Windows 11 look, follows light/dark mode.
- **Portable**: Unzip and run - no installation, nothing else to install (the .NET runtime is
  included).

## Bambu Lab Support

Bambu printers are supported via their local MQTT interface — no Bambu Cloud account is used or
required, and the printer can stay in its normal (cloud-connected) mode. To connect:

1. On the printer, go to **Settings > WLAN** and note the **IP address**, **Serial Number**, and
   **Access Code**. Enabling full **LAN Only Mode** is *not* required — the local MQTT status and
   the camera both work with the printer in normal mode.
2. For the live camera feed, enable **LAN Mode Live View** in the printer's camera settings
   (this is the only camera-specific toggle needed; it is separate from full LAN Only Mode).
3. On first run, choose "Bambu Lab" and enter the three values (IP / Serial / Access Code).

Verified end-to-end against a Bambu **H2S** in normal cloud-connected mode (LAN Only Mode off,
LAN Mode Live View on): both status polling and the live camera feed work.

Limitations:
- This is a local-network connection only — there is no Bambu Cloud/remote connection option.
- Bambu has no official local API; this app uses the community-documented MQTT status schema
  (the same one used by tools like Home Assistant's Bambu Lab integration). Field names may drift
  across firmware versions — please report any mismatched status if you hit one.

### Webcam / Camera

- **X1 / X1C / X1E / X2D / P2S / H2D / H2C / H2S**: supported. These printers expose their
  camera as an encrypted RTSPS stream (H.264 video); the app receives it itself and decodes it
  with the H.264 decoder built into Windows, using the same IP and Access Code entered during
  setup — no extra tools or configuration needed. The camera is only connected while the
  "Show Webcam" popup is open, since the printer only allows **one** camera client at a time —
  close Bambu Studio/Bambu Handy first if the feed shows "Camera unavailable". (Windows "N"
  editions need Microsoft's free Media Feature Pack for the decoder.)
- **P1P / P1S / A1 / A1 mini**: not yet supported (these models use a different local camera
  protocol). "Show Webcam" will show "Camera unavailable" on these printers.

## Snapmaker U1 Support

The Snapmaker U1 works as a Klipper printer when it runs
[paxx12's extended firmware](https://github.com/paxx12-snapmaker-u1/SnapmakerU1-Extended-Firmware),
which provides Moonraker. Status, notifications, the console and the stats strip work as on any
Klipper printer, and the popup can also show the printer's **touchscreen**, which you can use from
your PC.

1. Install paxx12's extended firmware on the U1. To see the touchscreen, turn on **Remote Screen**
   in the firmware's settings page (`http://<printer>/firmware-config`).
2. On first run, choose "Klipper" and enter the printer's address.
3. In **Settings**, set **Camera** to `case` (or leave it on Automatic) and **Second view** to `gui`.
4. In the webcam popup, the view button in the title bar switches between the camera and the
   touchscreen. Touch starts **locked** (view only): click the lock to use the screen, and the
   picture gets an amber outline while your clicks go to the printer. It locks again when the
   popup closes or you switch views.

Under the hood: the U1 lists its camera as WebRTC, which the app can't show, so it uses the MJPEG
stream the firmware serves next to it (`/webcam/stream.mjpg`, about 12 fps). The touchscreen
is shown by refreshing its snapshot about ten times a second (unchanged frames cost almost nothing),
and taps go to the firmware's remote-screen touch endpoint. The U1's camera hangs up every few
seconds at full frame rate; the app reconnects at once without interrupting the picture.

Verified against a real U1: the camera and the touchscreen both show and switch. Taps are covered
by tests but haven't been tried on a printer yet.

**Credits:** Snapmaker U1 support, the camera picker and the remote touchscreen were contributed by
[@abdukir](https://github.com/abdukir) in
[#1](https://github.com/devkriter/klipper-tray-icon/pull/1). Thanks!

## Installation

### Method 1: Download Executable (Recommended)
1. Go to the [Releases](../../releases) page.
2. Download the latest `KlipperTrayIcon_vX.X.X.zip`.
3. Extract it anywhere and run `KlipperTrayIcon.exe` from the extracted folder (keep the other
   files next to it).
4. On first launch, pick your printer type, enter its details and click **Start**.

**Upgrading from 1.x:** copy your old `config.json` next to the new `KlipperTrayIcon.exe` — the
settings format hasn't changed. *Start with Windows* is carried over automatically.

Tips:
- Running `KlipperTrayIcon.exe` again while it's already running opens the webcam popup.
- `KlipperTrayIcon.exe --settings` opens the Settings window on startup.

### Method 2: Run from Source
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

1. Clone the repository.
2. Run the application:
   ```bash
   dotnet run --project src/KlipperTrayIcon
   ```
3. Run the tests:
   ```bash
   dotnet test
   ```

## Building from Source
Run the build script (requires the .NET 10 SDK):
```bash
build.bat
```
It runs the tests and produces the app folder
`publish/KlipperTrayIcon/` and the release zip `publish/KlipperTrayIcon_vX.X.X.zip`.

The project lives in `src/KlipperTrayIcon` (open `KlipperTrayIcon.slnx` in Visual Studio or
Rider). The original Python version (1.x) is in `legacy-python/` for reference.

## Configuration
Right-click the tray icon and choose **Settings…** to change anything. Settings are stored in a
`config.json` file next to the executable; you can also edit it by hand, and the app picks up the
change within a few seconds.

For Klipper/Moonraker (see `config.example.json`):
```json
{
    "printer_type": "klipper",
    "moonraker_url": "http://your-printer-ip",
    "moonraker_api_key": "",
    "webcam_console": true,
    "console_hide_timelapse": true,
    "webcam_name": "",
    "webcam_stream": "",
    "second_view_name": "",
    "second_view_stream": "",
    "update_interval_seconds": 2,
    "notifications": true
}
```

For Bambu Lab (see `config.example.bambu.json`):
```json
{
    "printer_type": "bambu",
    "bambu_ip": "192.168.1.50",
    "bambu_serial": "your-printer-serial",
    "bambu_access_code": "your-lan-access-code",
    "update_interval_seconds": 2,
    "notifications": true
}
```

`webcam_name` picks which of Moonraker's webcams to show (empty: the first one), and
`second_view_name` adds a second one the popup can switch to — both are easiest to set from the
lists in Settings. `webcam_stream` and `second_view_stream` pick how each is fetched: `h264`
(camera-streamer's `video.h264`, far less bandwidth than MJPEG), `mjpeg` or `snapshots`; empty
uses the best one the camera offers, and switches from H.264 to MJPEG if H.264 keeps dropping. Webcams that use WebRTC or an iframe work too if the printer also serves MJPEG
or a snapshot for them. For the Snapmaker U1, see [Snapmaker U1 Support](#snapmaker-u1-support).

`console_hide_timelapse` hides the commands moonraker-timelapse sends on every frame from the
console (Klipper's responses always show).

`moonraker_api_key` is optional. Moonraker lets devices on your home network in without one
(via `trusted_clients` in `moonraker.conf`), so you only need it if this PC reaches the printer
from elsewhere (Tailscale, VPN, internet) or Moonraker has been set to require logins. Get it by
running `~/moonraker/scripts/fetch-apikey.sh` on the printer.

## License
MIT

## Disclaimer
This software was written with the assistance of an AI coding assistant.
