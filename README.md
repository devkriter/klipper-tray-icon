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
- The webcam feature is currently **Klipper/Moonraker only**.
- Bambu has no official local API; this app uses the community-documented MQTT status schema
  (the same one used by tools like Home Assistant's Bambu Lab integration). Field names may drift
  across firmware versions — please report any mismatched status if you hit one.

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

## Disclaimer
This software was written with the assistance of an AI coding assistant.
