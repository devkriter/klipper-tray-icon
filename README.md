# Klipper Tray Icon

A lightweight Windows system tray application that visualizes the status of your 3D print. Supports
Klipper/Moonraker/Mainsail setups, and Bambu Lab printers over the local network (LAN Only Mode).
The icon updates dynamically to show a circular progress bar corresponding to the print progress.

## Features

- **Dynamic Tray Icon**: visualizes print progress as a circular ring.
  - 🟢 **Green Ring**: Printing
  - 🟠 **Orange Ring**: Paused
  - 🔵 **Blue Ring**: Complete
  - 🔴 **Red Ring**: Error/Disconnected
- **Rich Tooltip**: Hover to see detailed status:
  - Percentage Complete
  - Time Remaining & ETA
  - Filename being printed
- **Two Printer Backends**:
  - **Klipper / Moonraker** (Mainsail, Fluidd) over HTTP.
  - **Bambu Lab** printers over local MQTT (requires **LAN Only Mode**).
- **Zero Configuration**: Prompts for your printer type and connection details on first run.
- **Portable**: Run as a single `.exe` file without installation.

## Bambu Lab Support

Bambu printers are supported via their local, LAN-only MQTT interface — no Bambu Cloud account is
used or required. To connect:

1. On the printer, enable **Settings > WLAN > LAN Only Mode**.
2. From the same screen, note the printer's **IP address**, **Serial Number**, and **Access Code**.
3. On first run, choose "Bambu Lab" and enter these three values when prompted.

Limitations:
- Only LAN Only Mode is supported — there is no Bambu Cloud/remote connection option.
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
  and `ffmpeg.exe` (~110MB) into a `go2rtc_cache` folder next to `config.json`. The packaged
  `.exe` build already has both bundled in, so no download happens for end users running the
  release build.

## Installation

### Method 1: Download Executable (Recommended)
1. Go to the [Releases](../../releases) page.
2. Download the latest `KlipperTrayIcon_vX.X.X.exe`.
3. Run the file.
4. On first launch, enter your printer's URL (e.g., `http://mainsail.local`).

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
3. The executable will be in the `dist/` folder.

## Configuration
The application stores its configuration in a `config.json` file in the same directory as the executable.
Wait for the prompt on first run, or create the file manually.

For Klipper/Moonraker (see `config.example.json`):
```json
{
    "printer_type": "klipper",
    "moonraker_url": "http://your-printer-ip",
    "update_interval_seconds": 2
}
```

For Bambu Lab, LAN Only Mode (see `config.example.bambu.json`):
```json
{
    "printer_type": "bambu",
    "bambu_ip": "192.168.1.50",
    "bambu_serial": "your-printer-serial",
    "bambu_access_code": "your-lan-access-code",
    "update_interval_seconds": 2
}
```

## License
MIT

This project bundles [go2rtc](https://github.com/AlexxIT/go2rtc) (MIT licensed) and a static
[ffmpeg](https://ffmpeg.org) build (LGPL 2.1+, via [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds))
to support Bambu Lab RTSPS camera streaming. Neither is linked into this project's own code —
both are invoked as separate local subprocesses.

## Disclaimer
This software was written with the assistance of an AI coding assistant.
