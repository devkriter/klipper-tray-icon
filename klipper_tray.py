#!/usr/bin/env python3
import sys
import time
import threading
import json
import webbrowser
import datetime
import io
import os
import subprocess
import ctypes
import queue
import tkinter as tk
import tkinter.font as tkfont

from PIL import Image, ImageDraw
import pystray

import autostart
import backends

__version__ = "1.6.0"

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
    "update_interval_seconds": 2,
    "notifications": True,
    "webcam_console": True,
}

STATE_LABELS = {
    "printing": "Printing",
    "paused": "Paused",
    "complete": "Print complete",
    "cancelled": "Print cancelled",
    "error": "Printer error",
    "standby": "Idle",
    "offline": "Offline",
}


def read_config():
    """Returns the saved config, or None if there isn't one yet."""
    try:
        with open(CONFIG_FILE, "r") as f:
            config = json.load(f)
    except FileNotFoundError:
        return None
    merged = DEFAULT_CONFIG.copy()
    merged.update(config)
    return merged


def write_config(config):
    with open(CONFIG_FILE, "w") as f:
        json.dump(config, f, indent=4)


def run_settings(first_run=False):
    """Shows the settings window (blocking) and saves the result. Returns
    True if the user saved."""
    import settings_window
    from tkinter import messagebox

    config = read_config() or DEFAULT_CONFIG.copy()
    new_config = settings_window.show(config, application_path, first_run=first_run,
                                      icon_image=create_tray_icon("complete", 1.0))
    if new_config is None:
        return False
    try:
        write_config(new_config)
    except OSError as e:
        root = tk.Tk()
        root.withdraw()
        messagebox.showerror("Klipper Tray Icon", f"Couldn't save settings to\n{CONFIG_FILE}\n\n{e}")
        root.destroy()
        return False
    return True


def create_tray_icon(state, progress, size=64):
    """Generate a circular progress icon."""
    width = height = size
    k = size / 64  # geometry below is laid out on a 64px grid
    image = Image.new('RGBA', (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    bg_color = (200, 200, 200, 50)

    if state == "printing":
        fg_color = (0, 255, 127, 255) # Spring Green
    elif state == "paused":
        fg_color = (255, 120, 0, 255) # Orange
    elif state == "complete":
        fg_color = (0, 191, 255, 255) # Deep Sky Blue
    elif state == "error":
        fg_color = (220, 20, 60, 255) # Crimson
    elif state == "offline":
        fg_color = (110, 110, 110, 255) # Dim gray, plus a red dot below
    else: # standby, cancelled or unknown
        fg_color = (169, 169, 169, 255) # Dark Gray

    # Geometry
    margin = 4 * k
    ring = max(1, round(6 * k))
    bbox = (margin, margin, width - margin, height - margin)

    # Draw background ring
    draw.ellipse(bbox, outline=bg_color, width=ring)

    # Clamp progress
    progress = max(0.0, min(1.0, progress))
    if state == "printing":
        start_angle = -90
        if progress > 0:
            end_angle = start_angle + (360 * progress)
            draw.arc(bbox, start=start_angle, end=end_angle, fill=fg_color, width=ring)
    else:
        # For non-printing states, fill the whole ring with the status color
        draw.ellipse(bbox, outline=fg_color, width=ring)

    if state == "offline":
        draw.ellipse((38 * k, 38 * k, 62 * k, 62 * k), fill=(220, 20, 60, 255))

    return image


def format_time_delta(seconds):
    if seconds is None or seconds < 0: return "?"
    m, s = divmod(int(seconds), 60)
    h, m = divmod(m, 60)
    if h > 0:
        return f"{h}h {m}m"
    return f"{m}m"


def describe_status(data):
    """Returns (headline, detail_lines) for the tooltip and menu."""
    label = STATE_LABELS.get(data.state, data.state.capitalize())
    if data.state in ("printing", "paused"):
        pct = int(data.progress * 100)
        headline = f"{label} · {pct}%"
        details = []
        if data.state == "printing" and data.time_left:
            eta = datetime.datetime.now() + datetime.timedelta(seconds=data.time_left)
            source = "slicer" if data.eta_source == "Slicer" else "est."
            details.append(f"{format_time_delta(data.time_left)} left · done {eta:%H:%M} ({source})")
        if data.filename:
            details.append(data.filename)
        return headline, details
    if data.state in ("complete", "cancelled") and data.filename:
        return label, [data.filename]
    if data.message:
        return label, [data.message]
    return label, []


def shorten(text, limit):
    return text if len(text) <= limit else "…" + text[-(limit - 1):]


def get_mouse_pos():
    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]
    pt = POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def self_command(*args):
    """Command line that re-launches this app with extra arguments."""
    if getattr(sys, 'frozen', False):
        return [sys.executable, *args]
    return [sys.executable, os.path.abspath(__file__), *args]


class TrayApp:
    def __init__(self, config):
        self.config = config
        self.backend = backends.create_backend(config, application_path)
        self.status = backends.PrinterStatus(state="standby", progress=0.0, message="Connecting...")
        self.shutdown_event = threading.Event()
        self.icon = None
        self._config_mtime = self._read_config_mtime()
        self._settings_proc = None
        self._last_drawn = None
        # Last status we actually heard from the printer. Notifications
        # compare against this so a brief network drop mid-print doesn't
        # swallow the "print finished" notification.
        self._last_online = self.status

    # ---- menu ---------------------------------------------------------

    def _status_lines(self):
        headline, details = describe_status(self.status)
        return [headline] + details

    def _menu_line(self, index):
        def text(item):
            lines = self._status_lines()
            return shorten(lines[index], 48) if index < len(lines) else ""
        return text

    def _menu_line_visible(self, index):
        return lambda item: index < len(self._status_lines())

    def build_menu(self):
        return pystray.Menu(
            pystray.MenuItem(self._menu_line(0), None, enabled=False),
            pystray.MenuItem(self._menu_line(1), None, enabled=False, visible=self._menu_line_visible(1)),
            pystray.MenuItem(self._menu_line(2), None, enabled=False, visible=self._menu_line_visible(2)),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Show webcam", self.on_show_webcam, default=True,
                             visible=lambda item: self.backend.supports_webcam),
            pystray.MenuItem("Open web interface", self.on_open_browser,
                             visible=lambda item: bool(self.backend.dashboard_url)),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Settings…", self.on_settings),
            pystray.MenuItem("Exit", self.on_exit),
        )

    # ---- actions ------------------------------------------------------

    def on_show_webcam(self, icon, item):
        mx, my = get_mouse_pos()
        subprocess.Popen(self_command("--webcam", str(mx), str(my)))

    def on_open_browser(self, icon, item):
        if self.backend.dashboard_url:
            webbrowser.open(self.backend.dashboard_url)

    def on_settings(self, icon, item):
        if self._settings_proc is not None and self._settings_proc.poll() is None:
            # Already open - bring it to the front instead of opening another.
            import settings_window
            hwnd = ctypes.windll.user32.FindWindowW(None, settings_window.WINDOW_TITLE)
            if hwnd:
                ctypes.windll.user32.SetForegroundWindow(hwnd)
            return
        # The window saves config.json itself; update_loop notices the change.
        self._settings_proc = subprocess.Popen(self_command("--settings"))

    def on_exit(self, icon, item):
        self.shutdown_event.set()
        icon.visible = False
        icon.stop()
        self.backend.stop()
        os._exit(0)

    # ---- background loop ----------------------------------------------

    def _read_config_mtime(self):
        try:
            return os.path.getmtime(CONFIG_FILE)
        except OSError:
            return None

    def _reload_config_if_changed(self):
        mtime = self._read_config_mtime()
        if mtime == self._config_mtime:
            return
        self._config_mtime = mtime
        try:
            config = read_config()
        except (OSError, ValueError):
            return  # half-written or hand-edited badly; keep the old settings
        if config is None:
            return
        old = self.backend
        self.config = config
        self.backend = backends.create_backend(config, application_path)
        self.backend.start()
        old.stop()
        self.status = backends.PrinterStatus(state="standby", progress=0.0, message="Connecting...")
        self._last_online = self.status
        self._last_drawn = None

    def _notify_transition(self, old, new):
        if not self.config.get("notifications", True) or old.state != "printing":
            return
        name = new.filename or old.filename or "Your print"
        messages = {
            "complete": ("Print finished", f"{name} is done."),
            "error": ("Print failed", f"{name} stopped with a printer error."),
            "cancelled": ("Print cancelled", f"{name} was cancelled."),
            "paused": ("Print paused", f"{name} is paused."),
        }
        if new.state in messages:
            title, body = messages[new.state]
            try:
                self.icon.notify(body, title)
            except Exception:
                pass

    def update_loop(self, icon):
        icon.visible = True
        while not self.shutdown_event.is_set():
            self._reload_config_if_changed()
            new = self.backend.get_status()
            self.status = new
            if new.state != "offline":
                self._notify_transition(self._last_online, new)
                self._last_online = new

            # Windows caps tray tooltips at 127 characters.
            icon.title = shorten("\n".join(self._status_lines()), 127)

            drawn = (new.state, round(new.progress, 2))
            if drawn != self._last_drawn:
                self._last_drawn = drawn
                icon.icon = create_tray_icon(new.state, new.progress)
            icon.update_menu()

            if self.shutdown_event.wait(timeout=self.config.get("update_interval_seconds", 2)):
                break

    def run(self):
        autostart.refresh_path()
        self.backend.start()
        self.icon = pystray.Icon("KlipperTray", create_tray_icon("standby", 0.0),
                                 "Connecting...", self.build_menu())
        self.icon.run(setup=self.update_loop)


class ConsolePanel:
    """Read-only Klipper console (Moonraker's gcode store) shown beside the
    webcam. A background thread polls Moonraker; the Tk side drains a queue
    so all widget updates stay on the Tk thread."""

    POLL_SECONDS = 1.0
    MAX_LINES = 500

    def __init__(self, parent, backend, stop_event, width):
        self.backend = backend
        self.stop_event = stop_event
        self.queue = queue.Queue()

        self.frame = tk.Frame(parent, bg="#141414", width=width)
        self.frame.pack_propagate(False)

        header = tk.Label(self.frame, text="CONSOLE", bg="#141414", fg="#8a8a8a",
                          font=("Segoe UI Semibold", 8), anchor="w", padx=10, pady=6)
        header.pack(fill=tk.X)

        # No scrollbar: Windows' native one can't be dark-themed and looks
        # out of place here. The mouse wheel scrolls the text.
        mono = tkfont.Font(family="Consolas", size=9)
        self.text = tk.Text(self.frame, bg="#141414", fg="#d6d6d6", font=mono, wrap=tk.WORD,
                            bd=0, highlightthickness=0, padx=10, pady=2, cursor="arrow",
                            spacing1=1, spacing3=2)
        self.text.pack(fill=tk.BOTH, expand=True)

        # Wrapped lines line up under the message, not under the timestamp.
        self.text.tag_configure("line", lmargin2=mono.measure("00:00:00  "))
        self.text.tag_configure("time", foreground="#5f5f5f")
        self.text.tag_configure("command", foreground="#6cb6ff")
        self.text.tag_configure("echo", foreground="#9a9a9a")
        self.text.tag_configure("error", foreground="#ff6b6b")
        self.text.tag_configure("status", foreground="#8a8a8a", font=("Segoe UI", 9, "italic"))
        self.text.configure(state=tk.DISABLED)
        self._append_status("Loading console…")
        self._has_lines = False

        threading.Thread(target=self._poll_loop, daemon=True).start()
        self.frame.after(200, self._drain)

    # -- background thread --

    def _poll_loop(self):
        last_time = 0.0
        last_error = None
        while not self.stop_event.is_set():
            try:
                entries = self.backend.get_console_messages()
                new = [e for e in entries if e.get("time", 0) > last_time]
                if new:
                    last_time = max(e.get("time", 0) for e in new)
                    self.queue.put(("lines", new))
                elif last_error is not None or last_time == 0.0:
                    self.queue.put(("lines", []))
                last_error = None
            except Exception as e:
                message = self.backend.describe_error(e)
                if message != last_error:
                    last_error = message
                    self.queue.put(("error", message))
            self.stop_event.wait(self.POLL_SECONDS)

    # -- Tk thread --

    def _drain(self):
        try:
            while True:
                kind, payload = self.queue.get_nowait()
                if kind == "lines":
                    self._show_lines(payload)
                else:
                    self._append_status(f"Console unavailable: {payload}")
        except queue.Empty:
            pass
        if not self.stop_event.is_set():
            self.frame.after(200, self._drain)

    def _at_bottom(self):
        return self.text.yview()[1] >= 0.999

    def _show_lines(self, entries):
        if not self._has_lines:
            # First successful fetch: drop the "Loading..." placeholder.
            self._clear()
            self._has_lines = True
            if not entries:
                self._append_status("No console messages yet.")
                return
        follow = self._at_bottom()
        self.text.configure(state=tk.NORMAL)
        for entry in entries:
            message = str(entry.get("message", "")).rstrip()
            stamp = datetime.datetime.fromtimestamp(entry.get("time", time.time())).strftime("%H:%M:%S")
            if entry.get("type") == "command":
                tag, message = "command", "> " + message
            elif message.startswith("!!"):
                tag = "error"
            elif message.startswith("//"):
                tag = "echo"
            else:
                tag = None
            self.text.insert(tk.END, stamp + "  ", ("line", "time"))
            self.text.insert(tk.END, message + "\n", ("line", tag) if tag else "line")
        self._trim()
        self.text.configure(state=tk.DISABLED)
        if follow:
            self.text.see(tk.END)

    def _append_status(self, message):
        follow = self._at_bottom()
        self.text.configure(state=tk.NORMAL)
        self.text.insert(tk.END, message + "\n", "status")
        self.text.configure(state=tk.DISABLED)
        if follow:
            self.text.see(tk.END)

    def _clear(self):
        self.text.configure(state=tk.NORMAL)
        self.text.delete("1.0", tk.END)
        self.text.configure(state=tk.DISABLED)

    def _trim(self):
        lines = int(self.text.index("end-1c").split(".")[0])
        if lines > self.MAX_LINES:
            self.text.delete("1.0", f"{lines - self.MAX_LINES}.0")


def run_webcam_window(backend, x=None, y=None, show_console=False):
    """Run the webcam viewer in a Tkinter window."""
    root = tk.Tk()
    root.title("Printer Webcam")

    # "Popup" style dimensions
    video_width = 640
    console_width = 340 if show_console and backend.supports_console else 0
    width = video_width + console_width
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
    
    video_frame = tk.Frame(content_frame, bg="black", width=video_width)
    video_frame.pack_propagate(False)
    video_frame.pack(side=tk.LEFT, fill=tk.Y)
    lbl_video = tk.Label(video_frame, bg="black")
    lbl_video.pack(fill=tk.BOTH, expand=True)

    # Threaded frame fetcher
    stop_event = threading.Event()

    if console_width:
        tk.Frame(content_frame, bg="#404040", width=1).pack(side=tk.LEFT, fill=tk.Y)
        console = ConsolePanel(content_frame, backend, stop_event, console_width - 1)
        console.frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

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
                    img.thumbnail((video_width, height))

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

def main():
    args = sys.argv[1:]

    if args[:1] == ["--settings"]:
        run_settings(first_run=read_config() is None)
        return

    config = read_config()
    if config is None:
        # First launch: set up before showing anything in the tray.
        if not run_settings(first_run=True):
            return
        config = read_config()

    if args[:1] == ["--webcam"]:
        backend = backends.create_backend(config, application_path)
        if not backend.supports_webcam:
            print("Webcam not supported for this printer type")
            sys.exit(1)
        x, y = None, None
        if len(args) >= 3:
            try:
                x, y = int(args[1]), int(args[2])
            except ValueError:
                pass
        run_webcam_window(backend, x, y, show_console=config.get("webcam_console", True))
        return

    TrayApp(config).run()


if __name__ == "__main__":
    main()
