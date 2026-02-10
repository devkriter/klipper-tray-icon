# Klipper Tray Icon

![Klipper](https://raw.githubusercontent.com/Klipper3d/klipper/master/docs/img/klipper-logo.png)

A lightweight Windows system tray application that visualizes the status of your 3D print from a Klipper/Moonraker/Mainsail setup.
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
- **Zero Configuration**: Prompts for your printer URL on first run.
- **Portable**: Run as a single `.exe` file without installation.

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
Wait for the prompt on first run, or create the file manually:

```json
{
    "moonraker_url": "http://your-printer-ip",
    "update_interval_seconds": 2
}
```

## License
MIT
