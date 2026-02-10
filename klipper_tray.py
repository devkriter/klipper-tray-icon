#!/usr/bin/env python3
import sys
import time
import threading
import json
import webbrowser
from PIL import Image, ImageDraw
import pystray
import requests
import datetime

# Configuration
CONFIG_FILE = "config.json"
DEFAULT_CONFIG = {
    "moonraker_url": "http://mainsail.local",
    "update_interval_seconds": 2
}

def load_config():
    try:
        with open(CONFIG_FILE, "r") as f:
            return json.load(f)
    except FileNotFoundError:
        return DEFAULT_CONFIG

config = load_config()
MOONRAKER_URL = config.get("moonraker_url", "http://mainsail.local").rstrip("/")

# Global state
current_state = "unknown"
current_progress = 0.0

def get_printer_status():
    """Fetch status from Moonraker API."""
    try:
        # Query print_stats, display_status, virtual_sdcard
        url = f"{MOONRAKER_URL}/printer/objects/query?print_stats&display_status&virtual_sdcard"
        response = requests.get(url, timeout=2)
        response.raise_for_status()
        data = response.json()
        
        status = data.get("result", {}).get("status", {})
        print_stats = status.get("print_stats", {})
        display_status = status.get("display_status", {})
        virtual_sdcard = status.get("virtual_sdcard", {})

        state = print_stats.get("state", "standby")
        # Prefer virtual_sdcard progress as it is often more accurate
        progress = virtual_sdcard.get("progress", 0.0)
        if progress == 0:
            progress = display_status.get("progress", 0.0)
            
        filename = print_stats.get("filename", "")
        print_duration = print_stats.get("print_duration", 0.0)
        
        # Calculate time left
        time_left = 0
        if state == "printing" and progress > 0.01:
             total_duration = print_duration / progress
             time_left = total_duration - print_duration

        return {
            "state": state,
            "progress": progress,
            "filename": filename,
            "time_left": time_left
        }
    except Exception as e:
        # print(f"Error fetching status: {e}")
        return {"state": "error", "progress": 0.0, "filename": "", "time_left": 0}

def create_tray_icon(state, progress):
    """Generate a circular progress icon."""
    width = 64
    height = 64
    image = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    
    # Colors
    bg_color = (130, 222, 230, 100) # Light circular background (Mainsail-ish blue/cyan tint) or gray
    bg_color = (200, 200, 200, 50)
    
    if state == "printing":
        fg_color = (0, 255, 127, 255) # Spring Green
    elif state == "paused":
        fg_color = (255, 120, 0, 255) # Orange
    elif state == "complete":
        fg_color = (0, 191, 255, 255) # Deep Sky Blue
    elif state == "error":
        fg_color = (220, 20, 60, 255) # Crimson
    else: # standby or unknown
        fg_color = (169, 169, 169, 255) # Dark Gray

    # Geometry
    margin = 4
    bbox = (margin, margin, width - margin, height - margin)
    
    # Draw background ring
    draw.ellipse(bbox, outline=bg_color, width=6)
    
    # Clamp progress
    progress = max(0.0, min(1.0, progress))
    if state == "printing":
        start_angle = -90
        # Progress 0.0 to 1.0. If 0, draw nothing or small dot?
        # If > 0, draw arc.
        if progress > 0:
            end_angle = start_angle + (360 * progress)
            draw.arc(bbox, start=start_angle, end=end_angle, fill=fg_color, width=6)
    else:
         # For non-printing states, fill the whole ring with the status color
        draw.ellipse(bbox, outline=fg_color, width=6)

    return image

def format_time_delta(seconds):
    if seconds is None or seconds < 0: return "?"
    m, s = divmod(int(seconds), 60)
    h, m = divmod(m, 60)
    if h > 0:
        return f"{h}h {m}m"
    return f"{m}m"

def update_loop(icon):
    """Background thread logic to update icon."""
    icon.visible = True
    global current_state, current_progress
    
    import datetime

    while icon.visible:
        data = get_printer_status()
        state = data.get("state", "unknown")
        progress = data.get("progress", 0.0)
        
        # Determine tooltip text
        if state == "printing":
            pct = int(progress * 100)
            filename = data.get("filename", "Unknown")
            time_left = data.get("time_left", 0)
            
            # ETA
            eta_dt = datetime.datetime.now() + datetime.timedelta(seconds=time_left)
            eta_str = eta_dt.strftime("%H:%M")
            left_str = format_time_delta(time_left)
            
            # Shorten filename
            if len(filename) > 25:
                filename = "..." + filename[-22:]
            
            tooltip = f"Printing: {pct}%\nLeft: {left_str} (ETA: {eta_str})\n{filename}"
        elif state == "error":
             tooltip = "Connection Error / Printer Error"
        else:
            tooltip = f"Status: {state.capitalize()}"
        
        # Check change
        if state != current_state or abs(progress - current_progress) > 0.005:
            current_state = state
            current_progress = progress
            
            icon.title = tooltip
            icon.icon = create_tray_icon(state, progress)
        
        time.sleep(config.get("update_interval_seconds", 2))

def on_open_browser(icon, item):
    webbrowser.open(MOONRAKER_URL)

def on_exit(icon, item):
    icon.stop()

def main():
    # Initial state
    initial_image = create_tray_icon("standby", 0.0)
    
    # Context Menu
    menu = pystray.Menu(
        pystray.MenuItem("Open Mainsail", on_open_browser, default=True),
        pystray.MenuItem("Exit", on_exit)
    )
    
    icon = pystray.Icon("KlipperTray", initial_image, "Connecting...", menu)
    
    # Pass update_loop as the setup function
    icon.run(setup=update_loop)

if __name__ == "__main__":
    main()
