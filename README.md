# Klipper Tray Icon

A Windows system tray application that displays the status of your 3D print from a Klipper/Moonraker/Mainsail setup.
The icon updates dynamically to show a circular progress bar corresponding to the print progress.

## Tech Stack

- **Language:** Python 3
- **Libraries:**
  - `pystray`: System tray icon management.
  - `Pillow`: Image generation for the dynamic circular progress icon.
  - `requests`: HTTP client to poll Moonraker API for status.

## Features

- **Dynamic Tray Icon:** visualizes print progress as a circular ring.
- **Tooltip:** Hover to see detailed status (State, % Complete, ETA).
- **Context Menu:**
  - Open Mainsail/Fluidd interface in browser.
  - Exit.

## Configuration

The application requires the IP address or hostname of your Moonraker instance.
This is configured in `config.json`.
