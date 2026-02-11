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

__version__ = "1.1.0"

import os
import tkinter as tk
from tkinter import simpledialog, messagebox

# Configuration
if getattr(sys, 'frozen', False):
    # If run as an exe, config is next to the executable
    application_path = os.path.dirname(sys.executable)
else:
    # If run as a script, config is in the script directory
    application_path = os.path.dirname(os.path.abspath(__file__))

CONFIG_FILE = os.path.join(application_path, "config.json")
DEFAULT_CONFIG = {
    "moonraker_url": "http://mainsail.local",
    "update_interval_seconds": 2
}

def load_config():
    try:
        with open(CONFIG_FILE, "r") as f:
            return json.load(f)
    except FileNotFoundError:
        # Prompt user if config doesn't exist
        root = tk.Tk()
        root.withdraw() # Hide main window
        
        url = simpledialog.askstring(
            title="Configuration", 
            prompt="Enter your Moonraker/Mainsail URL:\n(e.g., http://mainsail.local or http://192.168.1.100)",
            initialvalue="http://mainsail.local"
        )
        
        if url:
            new_config = DEFAULT_CONFIG.copy()
            new_config["moonraker_url"] = url
            try:
                with open(CONFIG_FILE, "w") as f:
                    json.dump(new_config, f, indent=4)
                return new_config
            except Exception as e:
                 messagebox.showerror("Error", f"Failed to save config: {e}")
                 sys.exit(1)
        else:
            sys.exit(0) # User cancelled

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

shutdown_event = threading.Event()

def update_loop(icon):
    """Background thread logic to update icon."""
    icon.visible = True
    global current_state, current_progress
    
    import datetime

    while icon.visible and not shutdown_event.is_set():
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
        
        # Wait for interval or shutdown event
        if shutdown_event.wait(timeout=config.get("update_interval_seconds", 2)):
            break

def on_open_browser(icon, item):
    webbrowser.open(MOONRAKER_URL)

def on_exit(icon, item):
    shutdown_event.set()
    icon.visible = False
    icon.stop()
    os._exit(0)

import io
import subprocess

# ... (imports remain)

# Configuration handled above...

# MJPEG Streaming Logic
def get_webcam_url():
    """Discover webcam URL from Moonraker."""
    try:
        url = f"{MOONRAKER_URL}/server/webcams/list"
        response = requests.get(url, timeout=2)
        response.raise_for_status()
        data = response.json()
        webcams = data.get("result", {}).get("webcams", [])
        
        if not webcams:
            # Fallback default
            return f"{MOONRAKER_URL}/webcam/?action=stream"
            
        # Pick first enabled MJPEG stream
        for cam in webcams:
            if cam.get("enabled", True) and cam.get("service") in ["mjpegstreamer", "mjpegstreamer-adaptive", "ipstream"]:
                 stream_path = cam.get("stream_url", "")
                 if stream_path.startswith("http"):
                     return stream_path
                 elif stream_path.startswith("/"):
                     return f"{MOONRAKER_URL}{stream_path}"
                 return f"{MOONRAKER_URL}/{stream_path}"
        
        # Fallback if list exists but no suitable match found
        return f"{MOONRAKER_URL}/webcam/?action=stream"

    except Exception as e:
        print(f"Error fetching webcam list: {e}")
        return f"{MOONRAKER_URL}/webcam/?action=stream"

def run_webcam_window(x=None, y=None):
    """Run the webcam viewer in a Tkinter window."""
    root = tk.Tk()
    root.title("Klipper Webcam")
    
    # "Popup" style dimensions
    width = 640
    height = 480
    
    # Configure window style
    root.configure(bg="#202020") # Dark grey background
    root.overrideredirect(True)  # Remove OS title bar
    root.attributes("-topmost", True) # Keep on top
    
    # Calculate Position
    if x is not None and y is not None:
        # Default to bottom-right alignment with cursor (like system tray)
        # Mouse is at (x, y). Window goes above-center or above-left?
        # Windows tray icons are usually bottom-right of screen.
        # So we want the window's bottom-right (or center) to point to x,y.
        
        # Position: (x - width/2, y - height - 10) -> Centered above cursor
        # Ensure it fits on screen
        screen_w = root.winfo_screenwidth()
        screen_h = root.winfo_screenheight()
        
        pos_x = int(x - (width / 2))
        pos_y = int(y - height - 20) # 20px padding above cursor
        
        # Clamp to screen
        pos_x = max(0, min(screen_w - width, pos_x))
        pos_y = max(0, min(screen_h - height, pos_y))
        
        root.geometry(f"{width}x{height}+{pos_x}+{pos_y}")
    else:
        root.geometry(f"{width}x{height}")

    # Inner frame for border effect
    # Main window is #202020. We can add a lighter/darker border.
    # Actually, let's make the root the border color, and an inner frame the content.
    border_color = "#404040"
    root.configure(bg=border_color)
    
    content_frame = tk.Frame(root, bg="#000000")
    content_frame.pack(fill=tk.BOTH, expand=True, padx=1, pady=1) # 1px border
    
    lbl_video = tk.Label(content_frame, bg="black")
    lbl_video.pack(fill=tk.BOTH, expand=True)
    
    # Add a close button overlay or behavior
    close_btn = tk.Button(root, text="×", command=lambda: os._exit(0), 
                          bg="#202020", fg="white", bd=0, font=("Arial", 12),
                          activebackground="#cc0000", activeforeground="white")
    close_btn.place(x=width-30, y=1, width=28, height=24)

    stream_url = get_webcam_url()
    # print(f"Streaming from: {stream_url}")
    
    # Threaded frame fetcher
    stop_event = threading.Event()
    
    def stream_loop():
        try:
            with requests.get(stream_url, stream=True, timeout=5) as r:
                r.raise_for_status()
                bytes_buffer = b''
                for chunk in r.iter_content(chunk_size=4096):
                    if stop_event.is_set(): break
                    bytes_buffer += chunk
                    
                    while True:
                        a = bytes_buffer.find(b'\xff\xd8')
                        b = bytes_buffer.find(b'\xff\xd9')
                        if a != -1 and b != -1:
                            jpg = bytes_buffer[a:b+2]
                            bytes_buffer = bytes_buffer[b+2:]
                            try:
                                # Decode and resize
                                img = Image.open(io.BytesIO(jpg))
                                # Resize to fit window
                                # win_w = root.winfo_width() # May fail if not mapped?
                                # win_h = root.winfo_height()
                                
                                # Use fixed size for reliability or just img size?
                                # Let's scale to fit the frame
                                img.thumbnail((width, height))
                                
                                # Use standard PIL.ImageTk
                                from PIL import ImageTk
                                tk_img = ImageTk.PhotoImage(img)
                                
                                def update_ui(image_ref):
                                    try:
                                        if root.winfo_exists():
                                            lbl_video.config(image=image_ref)
                                            lbl_video.image = image_ref # Keep reference
                                    except: pass
                                
                                root.after(0, update_ui, tk_img)
                            except Exception as e:
                                pass # Frame error
                        else:
                            break # Wait for more data
        except Exception as e:
            pass
            # print(f"Stream error: {e}")
    
    t = threading.Thread(target=stream_loop, daemon=True)
    t.start()
    
    # Close on Escape or click outside?
    root.bind("<Escape>", lambda e: os._exit(0))
    
    # For click-away close, we need a focus out handler
    def on_focus_out(event):
        # Small delay to allow focus transfer to check
        # This can be annoying if it closes when you click the window itself (if focus handling is weird)
        # But for a popup, losing focus normally means close.
        if root.focus_displayof() is None:
             os._exit(0)
             
    root.bind("<FocusOut>", on_focus_out)
    root.focus_force() # Grab focus initially
    
    root.mainloop()

# ... (Existing Tray Logic) ...

import ctypes

def get_mouse_pos():
    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]
    pt = POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y

def on_show_webcam(icon, item):
    """Launch the webcam viewer as a subprocess."""
    mx, my = get_mouse_pos()
    
    if getattr(sys, 'frozen', False):
         cmd = [sys.executable, "--webcam", str(mx), str(my)]
    else:
         cmd = [sys.executable, __file__, "--webcam", str(mx), str(my)]
    
    subprocess.Popen(cmd)

def main():
    # Argument handling
    if len(sys.argv) > 1 and sys.argv[1] == "--webcam":
        x, y = None, None
        if len(sys.argv) >= 4:
            try:
                x = int(sys.argv[2])
                y = int(sys.argv[3])
            except ValueError: pass

        try:
            from PIL import ImageTk 
        except ImportError:
            print("PIL.ImageTk missing")
            sys.exit(1)
            
        run_webcam_window(x, y)
        return

    # Initial state
    initial_image = create_tray_icon("standby", 0.0)
    
    # Context Menu
    menu = pystray.Menu(
        pystray.MenuItem("Show Webcam", on_show_webcam, default=True),
        pystray.MenuItem("Open Mainsail", on_open_browser),
        pystray.MenuItem("Exit", on_exit)
    )
    
    icon = pystray.Icon("KlipperTray", initial_image, "Connecting...", menu)
    
    # Pass update_loop as the setup function
    icon.run(setup=update_loop)

if __name__ == "__main__":
    main()
