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
- **Notifications** when a print finishes, fails, is cancelled or pauses.
- **Settings window**: switch printers, test the connection, change the refresh rate, and turn
  notifications and *Start with Windows* on or off. Changes apply immediately.
- **Two Printer Backends**:
  - **Klipper / Moonraker** (Mainsail, Fluidd) over HTTP.
  - **Bambu Lab** printers over local MQTT (works with the printer in normal/cloud mode — full
    "LAN Only Mode" is not required).
- **Guided setup**: a welcome window asks for your printer details on first run, with a
  *Test connection* button.
- **Portable**: Unzip and run - no installation.

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
  camera as an encrypted RTSPS stream; this app bundles [go2rtc](https://github.com/AlexxIT/go2rtc)
  (MIT licensed) plus a static [ffmpeg](https://ffmpeg.org) build (LGPL) to decode it locally and
  re-display it as a live feed, using the same IP and Access Code entered during setup — no extra
  configuration needed. Both are only started while the "Show Webcam" popup is open, since the
  printer only allows **one** camera client at a time — close Bambu Studio/Bambu Handy first if
  the feed shows "Camera unavailable".
- **P1P / P1S / A1 / A1 mini**: not yet supported (these models use a different local camera
  protocol). "Show Webcam" will show "Camera unavailable" on these printers.
- When run from source, the first "Show Webcam" click downloads and caches `go2rtc.exe` (~19MB)
  and `ffmpeg.exe` (~110MB) into a `go2rtc_cache` folder next to `config.json`. The release
  zip already ships both next to `KlipperTrayIcon.exe`, so no download happens for end users.

## Installation

### Method 1: Download Executable (Recommended)
1. Go to the [Releases](../../releases) page.
2. Download the latest `KlipperTrayIcon_vX.X.X.zip`.
3. Extract it anywhere and run `KlipperTrayIcon.exe` from the extracted folder (keep the other
   files and the `_internal` folder next to it).
4. On first launch, pick your printer type, enter its details and click **Start**.

### Method 2: Run from Source
If you prefer to run the Python script directly:

1. Clone the repository.
2. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```
3. Run the application:
   ```bash
   python klipper_tray.py
   ```

## Building form Source
To build the `.exe` yourself:

1. Install requirements.
2. Run the build script:
   ```bash
   build.bat
   ```
3. The app folder is `dist/KlipperTrayIcon/` and the release zip is `dist/KlipperTrayIcon_vX.X.X.zip`.

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

`moonraker_api_key` is optional. Moonraker lets devices on your home network in without one
(via `trusted_clients` in `moonraker.conf`), so you only need it if this PC reaches the printer
from elsewhere (Tailscale, VPN, internet) or Moonraker has been set to require logins. Get it by
running `~/moonraker/scripts/fetch-apikey.sh` on the printer.

## License
MIT

This project bundles [go2rtc](https://github.com/AlexxIT/go2rtc) (MIT licensed) and a static
[ffmpeg](https://ffmpeg.org) build (LGPL 2.1+, via [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds))
to support Bambu Lab RTSPS camera streaming. Neither is linked into this project's own code —
both are invoked as separate local subprocesses.

## Disclaimer
This software was written with the assistance of an AI coding assistant.
