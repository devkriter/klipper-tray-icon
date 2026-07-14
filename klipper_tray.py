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

import backends

__version__ = "1.4.1"

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
    "printer_type": "klipper",
    "moonraker_url": "http://mainsail.local",
    "update_interval_seconds": 2
}

def _prompt_klipper_config():
    url = simpledialog.askstring(
        title="Configuration",
        prompt="Enter your Moonraker/Mainsail URL:\n(e.g., http://mainsail.local or http://192.168.1.100)",
        initialvalue="http://mainsail.local"
    )
    if not url:
        sys.exit(0)  # User cancelled

    new_config = DEFAULT_CONFIG.copy()
    new_config["printer_type"] = "klipper"
    new_config["moonraker_url"] = url
    return new_config

def _prompt_bambu_config():
    ip = simpledialog.askstring(
        title="Bambu Printer IP",
        prompt="Enter your Bambu printer's local IP address:\n(Settings > WLAN on the printer)"
    )
    if not ip:
        sys.exit(0)

    serial = simpledialog.askstring(
        title="Bambu Serial Number",
        prompt="Enter your printer's Serial Number:\n(Settings > WLAN, or printed on the printer)"
    )
    if not serial:
        sys.exit(0)

    access_code = simpledialog.askstring(
        title="Bambu Access Code",
        prompt="Enter your printer's Access Code:\n(Settings > WLAN, requires 'LAN Only Mode' enabled)"
    )
    if not access_code:
        sys.exit(0)

    new_config = DEFAULT_CONFIG.copy()
    new_config["printer_type"] = "bambu"
    new_config["bambu_ip"] = ip
    new_config["bambu_serial"] = serial
    new_config["bambu_access_code"] = access_code
    return new_config

def load_config():
    try:
        with open(CONFIG_FILE, "r") as f:
            return json.load(f)
    except FileNotFoundError:
        # Prompt user if config doesn't exist
        root = tk.Tk()
        root.withdraw() # Hide main window

        is_klipper = messagebox.askyesno(
            "Printer Type",
            "What type of printer are you connecting to?\n\n"
            "Yes = Klipper / Moonraker (Mainsail/Fluidd)\n"
            "No = Bambu Lab (LAN Only Mode)"
        )

        new_config = _prompt_klipper_config() if is_klipper else _prompt_bambu_config()

        try:
            with open(CONFIG_FILE, "w") as f:
                json.dump(new_config, f, indent=4)
            return new_config
        except Exception as e:
            messagebox.showerror("Error", f"Failed to save config: {e}")
            sys.exit(1)

config = load_config()
backend = backends.create_backend(config, application_path)

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

# Global state
current_state = "unknown"
current_progress = 0.0
shutdown_event = threading.Event()

def update_loop(icon, backend):
    """Background thread logic to update icon."""
    icon.visible = True
    global current_state, current_progress

    import datetime

    while icon.visible and not shutdown_event.is_set():
        data = backend.get_status()
        state = data.state
        progress = data.progress

        # Determine tooltip text
        if state == "printing":
            pct = int(progress * 100)
            filename = data.filename or "Unknown"
            time_left = data.time_left
            eta_source = data.eta_source

            # ETA
            eta_dt = datetime.datetime.now() + datetime.timedelta(seconds=time_left)
            eta_str = eta_dt.strftime("%H:%M")
            left_str = format_time_delta(time_left)
            
            # Shorten filename
            if len(filename) > 25:
                filename = "..." + filename[-22:]
            
            source_label = "Slicer" if eta_source == "Slicer" else "Calc"
            tooltip = f"Printing: {pct}%\nLeft: {left_str} ({source_label})\nETA: {eta_str}\n{filename}"
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
    if backend.dashboard_url:
        webbrowser.open(backend.dashboard_url)

def on_exit(icon, item):
    shutdown_event.set()
    icon.visible = False
    icon.stop()
    backend.stop()
    os._exit(0)

import io
import subprocess

# ... (imports remain)

# Configuration handled above...

# MJPEG Streaming Logic (Klipper/Moonraker only)
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
    
    # Threaded frame fetcher
    stop_event = threading.Event()

    def close_window():
        # stop_event lets the stream thread's own cleanup run first if it's
        # quick, but backend.stop() is the real guarantee - it force-stops
        # any camera bridge process (e.g. go2rtc) this window started, since
        # os._exit() below won't run the stream thread's "finally" cleanup.
        stop_event.set()
        try:
            backend.stop()
        except Exception:
            pass
        os._exit(0)

    # Add a close button overlay or behavior
    close_btn = tk.Button(root, text="×", command=close_window,
                          bg="#202020", fg="white", bd=0, font=("Arial", 12),
                          activebackground="#cc0000", activeforeground="white")
    close_btn.place(x=width-30, y=1, width=28, height=24)

    # Show something immediately - first Bambu use may include a one-time
    # go2rtc download before any frame arrives.
    lbl_video.config(text="Connecting...", fg="white")

    def stream_loop():
        got_frame = False
        try:
            for jpg in backend.iter_webcam_frames(stop_event):
                if stop_event.is_set():
                    break
                got_frame = True
                try:
                    # Decode and resize
                    img = Image.open(io.BytesIO(jpg))
                    # Scale to fit the frame
                    img.thumbnail((width, height))

                    # Use standard PIL.ImageTk
                    from PIL import ImageTk
                    tk_img = ImageTk.PhotoImage(img)

                    def update_ui(image_ref):
                        try:
                            if root.winfo_exists():
                                lbl_video.config(image=image_ref, text="")
                                lbl_video.image = image_ref # Keep reference
                        except: pass

                    root.after(0, update_ui, tk_img)
                except Exception as e:
                    pass # Frame error
        except Exception as e:
            pass
            # print(f"Stream error: {e}")

        if not got_frame and not stop_event.is_set():
            def show_unavailable():
                try:
                    if root.winfo_exists():
                        lbl_video.config(text="Camera unavailable", fg="white")
                except Exception:
                    pass
            root.after(0, show_unavailable)

    t = threading.Thread(target=stream_loop, daemon=True)
    t.start()
    
    # Close on Escape or click outside?
    root.bind("<Escape>", lambda e: close_window())

    # For click-away close, we need a focus out handler
    def on_focus_out(event):
        # Small delay to allow focus transfer to check
        # This can be annoying if it closes when you click the window itself (if focus handling is weird)
        # But for a popup, losing focus normally means close.
        if root.focus_displayof() is None:
             close_window()
             
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
        if not backend.supports_webcam:
            print("Webcam not supported for this printer type")
            sys.exit(1)

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

    backend.start()

    # Initial state
    initial_image = create_tray_icon("standby", 0.0)

    # Context Menu (built based on what the active backend supports)
    items = []
    if backend.supports_webcam:
        items.append(pystray.MenuItem("Show Webcam", on_show_webcam, default=True))
    if backend.dashboard_url:
        items.append(pystray.MenuItem("Open Mainsail", on_open_browser))
    items.append(pystray.MenuItem("Exit", on_exit))
    menu = pystray.Menu(*items)

    icon = pystray.Icon("KlipperTray", initial_image, "Connecting...", menu)

    # Pass update_loop as the setup function
    icon.run(setup=lambda ic: update_loop(ic, backend))

if __name__ == "__main__":
    main()
