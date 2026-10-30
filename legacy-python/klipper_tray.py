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
import re
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
        self._webcam = None  # WebcamPopupHelper, started in run()
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

    def _start_webcam_helper(self):
        if self.backend.supports_webcam:
            self._webcam = WebcamPopupHelper(self.config)
            self._webcam.start()

    def on_show_webcam(self, icon, item):
        mx, my = get_mouse_pos()
        if self._webcam is None:
            self._start_webcam_helper()
        if self._webcam is not None:
            self._webcam.show(mx, my)

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
        if self._webcam is not None:
            self._webcam.stop()
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
        # The popup helper has the old settings baked in; replace it.
        if self._webcam is not None:
            self._webcam.stop()
            self._webcam = None
        self._start_webcam_helper()
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
        # Start the webcam popup now, hidden, so clicks only have to show it.
        self._start_webcam_helper()
        self.icon = pystray.Icon("KlipperTray", create_tray_icon("standby", 0.0),
                                 "Connecting...", self.build_menu())
        self.icon.run(setup=self.update_loop)


# e.g. "B:109.3 /110.0 T0:230.6 /250.0" (also T:, T1:, C: chamber, ...)
TEMP_REPORT_RE = re.compile(r"^(?:ok\s+)?(?:[A-Z]\d*:\s*-?\d+(?:\.\d+)?\s*/\s*-?\d+(?:\.\d+)?\s*)+$")


class ConsolePanel:
    """Read-only Klipper console (Moonraker's gcode store) shown beside the
    webcam. A background thread polls Moonraker while the popup is visible
    (plus once at startup, so history is ready before the first click); the
    Tk side drains a queue so all widget updates stay on the Tk thread."""

    POLL_SECONDS = 1.0
    MAX_LINES = 500

    def __init__(self, parent, backend, width):
        self.backend = backend
        self.queue = queue.Queue()
        self._active = threading.Event()  # set while the popup is visible
        self._wake = threading.Event()    # poll right now instead of in a second

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
        self.text.tag_configure("temp", foreground="#e3b341")
        self.text.tag_configure("status", foreground="#8a8a8a", font=("Segoe UI", 9, "italic"))
        self.text.configure(state=tk.DISABLED)
        self._temp_line_open = False  # is the last line a temperature report?
        self._append_status("Loading console…")
        self._has_lines = False

        threading.Thread(target=self._poll_loop, daemon=True).start()
        self.frame.after(200, self._drain)

    # -- background thread --

    def resume(self):
        self._active.set()
        self._wake.set()

    def pause(self):
        self._active.clear()

    def _poll_loop(self):
        last_time = 0.0
        last_error = None
        first = True
        while True:
            if not first:
                self._active.wait()
            first = False
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
            self._wake.wait(self.POLL_SECONDS)
            self._wake.clear()

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
        self.frame.after(100, self._drain)

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
            elif TEMP_REPORT_RE.match(message):
                tag = "temp"
            else:
                tag = None

            # Klipper prints a temperature report every second while it waits
            # to heat (M109/M190). Keep just one line and update it in place.
            if tag == "temp":
                if self._temp_line_open:
                    self.text.delete("temp_start", "end-1c")
                else:
                    self.text.mark_set("temp_start", "end-1c")
                    self.text.mark_gravity("temp_start", tk.LEFT)
            self._temp_line_open = tag == "temp"

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
        self._temp_line_open = False
        if follow:
            self.text.see(tk.END)

    def _clear(self):
        self.text.configure(state=tk.NORMAL)
        self.text.delete("1.0", tk.END)
        self.text.configure(state=tk.DISABLED)
        self._temp_line_open = False

    def _trim(self):
        lines = int(self.text.index("end-1c").split(".")[0])
        if lines > self.MAX_LINES:
            self.text.delete("1.0", f"{lines - self.MAX_LINES}.0")


class WebcamPopup:
    """The webcam popup. Normally lives in a background helper process that
    the tray starts once and keeps hidden, so a click only has to un-hide a
    window that already exists (no process start, no imports, no window
    creation). While hidden it keeps a recent snapshot ready, which is shown
    the instant the popup opens and replaced as soon as live video arrives.

    Commands arrive on stdin, one per line: "show X Y", "hide", "quit".
    """

    VIDEO_WIDTH = 640
    HEIGHT = 480
    CONSOLE_WIDTH = 340
    SNAPSHOT_REFRESH_SECONDS = 20
    # A tray click while the popup is open first takes focus away (hiding
    # it), then sends "show". Treat that pair as "toggle closed".
    TOGGLE_WINDOW_SECONDS = 0.4

    def __init__(self, backend, show_console=False, one_shot=False):
        self.backend = backend
        self.one_shot = one_shot  # legacy --webcam: exit instead of hiding
        self.visible = False
        self._session = None      # threading.Event, set to end the current stream
        self._session_id = 0
        self._got_live = False
        self._cached = None       # (PIL image, datetime) - last frame we've seen
        self._frame_slot = None   # newest (session_id, kind, PIL image) for the Tk thread
        self._slot_lock = threading.Lock()
        self._commands = queue.Queue()
        self._hidden_at = 0.0

        self.root = root = tk.Tk()
        root.withdraw()
        root.title("Printer Webcam")
        root.overrideredirect(True)  # no OS title bar
        root.attributes("-topmost", True)

        console_width = self.CONSOLE_WIDTH if show_console and backend.supports_console else 0
        self.width = self.VIDEO_WIDTH + console_width

        border_color = "#404040"
        root.configure(bg=border_color)
        content = tk.Frame(root, bg="#000000")
        content.pack(fill=tk.BOTH, expand=True, padx=1, pady=1)  # 1px border

        video_frame = tk.Frame(content, bg="black", width=self.VIDEO_WIDTH)
        video_frame.pack_propagate(False)
        video_frame.pack(side=tk.LEFT, fill=tk.Y)
        self.lbl_video = tk.Label(video_frame, bg="black", fg="white", text="Connecting...")
        self.lbl_video.pack(fill=tk.BOTH, expand=True)
        # Shown over a snapshot until live video takes over.
        self.badge = tk.Label(video_frame, bg="#202020", fg="#d0d0d0", font=("Segoe UI", 8), padx=8, pady=3)

        self.console = None
        if console_width:
            tk.Frame(content, bg=border_color, width=1).pack(side=tk.LEFT, fill=tk.Y)
            self.console = ConsolePanel(content, backend, console_width - 1)
            self.console.frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        close_btn = tk.Button(root, text="×", command=self.hide,
                              bg="#202020", fg="white", bd=0, font=("Arial", 12),
                              activebackground="#cc0000", activeforeground="white")
        close_btn.place(x=self.width - 30, y=1, width=28, height=24)

        root.bind("<Escape>", lambda e: self.hide())
        root.bind("<FocusOut>", self._on_focus_out)

        threading.Thread(target=self._snapshot_loop, daemon=True).start()
        root.after(15, self._tick)

    # ---- commands (Tk thread) -------------------------------------------

    def listen_stdin(self):
        def read():
            for line in sys.stdin:
                self._commands.put(line.split())
            self._commands.put(["quit"])  # tray went away
        threading.Thread(target=read, daemon=True).start()

    def _tick(self):
        try:
            while True:
                self._handle(self._commands.get_nowait())
        except queue.Empty:
            pass
        with self._slot_lock:
            slot, self._frame_slot = self._frame_slot, None
        if slot is not None:
            self._show_frame(*slot)
        self.root.after(15, self._tick)

    def _handle(self, cmd):
        if not cmd:
            return
        if cmd[0] == "show":
            x = y = None
            if len(cmd) >= 3:
                try:
                    x, y = int(cmd[1]), int(cmd[2])
                except ValueError:
                    pass
            if time.monotonic() - self._hidden_at < self.TOGGLE_WINDOW_SECONDS:
                return  # this click is what just closed it
            self.show(x, y)
        elif cmd[0] == "hide":
            self.hide()
        elif cmd[0] == "quit":
            self.quit()

    # ---- show / hide ----------------------------------------------------

    def show(self, x=None, y=None):
        root = self.root
        if x is not None and y is not None:
            # Centered above the cursor (the tray is usually bottom-right),
            # clamped to the screen.
            pos_x = int(x - self.width / 2)
            pos_y = int(y - self.HEIGHT - 20)
            pos_x = max(0, min(root.winfo_screenwidth() - self.width, pos_x))
            pos_y = max(0, min(root.winfo_screenheight() - self.HEIGHT, pos_y))
            root.geometry(f"{self.width}x{self.HEIGHT}+{pos_x}+{pos_y}")
        else:
            root.geometry(f"{self.width}x{self.HEIGHT}")

        # Paint the cached frame before the window appears, so the very first
        # thing on screen is a picture rather than "Connecting...".
        if self._cached is not None:
            image, taken = self._cached
            self._set_image(image)
            self._set_badge(f"Snapshot from {taken:%H:%M:%S} · connecting…")
        else:
            self._set_badge(None)
            self.lbl_video.config(image="", text="Connecting...")
            self.lbl_video.image = None

        self.visible = True
        root.deiconify()
        root.lift()
        root.focus_force()
        try:
            ctypes.windll.user32.SetForegroundWindow(ctypes.windll.user32.GetParent(root.winfo_id()))
        except Exception:
            pass

        if self.console is not None:
            self.console.resume()
        self._start_session()

    def hide(self):
        if self.one_shot:
            self.quit()
        if not self.visible:
            return
        self.visible = False
        self._hidden_at = time.monotonic()
        self.root.withdraw()
        if self.console is not None:
            self.console.pause()
        self._end_session()

    def quit(self):
        self._end_session()
        try:
            self.backend.stop()
        except Exception:
            pass
        os._exit(0)

    def _on_focus_out(self, event):
        if self.visible and self.root.focus_displayof() is None:
            self.hide()

    # ---- video ------------------------------------------------------------

    def _start_session(self):
        self._end_session()
        self._session_id += 1
        session_id = self._session_id
        stop = self._session = threading.Event()
        self._got_live = False
        threading.Thread(target=self._stream_loop, args=(session_id, stop), daemon=True).start()
        # A single snapshot usually arrives well before the stream's first
        # frame; show it in the meantime.
        threading.Thread(target=self._quick_snapshot, args=(session_id,), daemon=True).start()

    def _end_session(self):
        if self._session is not None:
            self._session.set()
            # The stream thread notices within a frame and its cleanup releases
            # the camera (Bambu: stops go2rtc, as the printer only allows one
            # viewer at a time).
            self._session = None

    def _decode(self, jpg):
        img = Image.open(io.BytesIO(jpg))
        img.thumbnail((self.VIDEO_WIDTH, self.HEIGHT))
        img.load()
        return img

    def _post(self, session_id, kind, image):
        with self._slot_lock:
            current = self._frame_slot
            # Never let a snapshot replace a live frame that's waiting.
            if kind == "snapshot" and current is not None and current[1] == "live":
                return
            self._frame_slot = (session_id, kind, image)

    def _stream_loop(self, session_id, stop):
        got_frame = False
        try:
            for jpg in self.backend.iter_webcam_frames(stop):
                if stop.is_set():
                    break
                try:
                    image = self._decode(jpg)
                except Exception:
                    continue
                got_frame = True
                self._post(session_id, "live", image)
        except Exception:
            pass
        if not got_frame and not stop.is_set():
            self._post(session_id, "unavailable", None)

    def _quick_snapshot(self, session_id):
        jpg = self.backend.get_webcam_snapshot(timeout=2)
        if jpg:
            try:
                self._post(session_id, "snapshot", self._decode(jpg))
            except Exception:
                pass

    def _snapshot_loop(self):
        """While hidden, keep the cached picture reasonably fresh (Klipper
        only - for Bambu a snapshot means starting go2rtc, so the cache just
        holds the last frame seen)."""
        while True:
            if not self.visible:
                jpg = self.backend.get_webcam_snapshot(timeout=3)
                if jpg:
                    try:
                        self._post(None, "cache", self._decode(jpg))
                    except Exception:
                        pass
            time.sleep(self.SNAPSHOT_REFRESH_SECONDS)

    def _show_frame(self, session_id, kind, image):
        if kind == "cache":
            self._cached = (image, datetime.datetime.now())
            return
        if session_id != self._session_id or not self.visible:
            return  # from a stream that has since been closed
        if kind == "live":
            self._got_live = True
            self._cached = (image, datetime.datetime.now())
            self._set_image(image)
            self._set_badge(None)
        elif kind == "snapshot":
            if self._got_live:
                return
            self._cached = (image, datetime.datetime.now())
            self._set_image(image)
            self._set_badge("Snapshot · connecting to live video…")
        elif kind == "unavailable":
            if self._cached is not None:
                self._set_badge("Live video unavailable · showing last snapshot")
            else:
                self.lbl_video.config(image="", text="Camera unavailable")
                self.lbl_video.image = None

    def _set_image(self, image):
        from PIL import ImageTk
        photo = ImageTk.PhotoImage(image)
        self.lbl_video.config(image=photo, text="")
        self.lbl_video.image = photo  # keep a reference

    def _set_badge(self, text):
        if text:
            self.badge.config(text=text)
            self.badge.place(x=8, rely=1.0, y=-8, anchor="sw")
        else:
            self.badge.place_forget()

    def run(self):
        self.root.mainloop()


class WebcamPopupHelper:
    """Tray-side handle on the background webcam popup process."""

    def __init__(self, config):
        self.config = config
        self.proc = None

    def start(self):
        self.proc = subprocess.Popen(self_command("--webcam-helper"), stdin=subprocess.PIPE)

    def _send(self, line):
        if self.proc is None or self.proc.poll() is not None:
            self.start()
        try:
            self.proc.stdin.write((line + "\n").encode())
            self.proc.stdin.flush()
        except OSError:
            # Helper died between the check and the write - start a fresh one.
            self.start()
            self.proc.stdin.write((line + "\n").encode())
            self.proc.stdin.flush()

    def show(self, x, y):
        if self.proc is not None and self.proc.poll() is None:
            # Windows only lets the app the user just clicked bring a window
            # to the front; pass that permission on to the helper.
            try:
                ctypes.windll.user32.AllowSetForegroundWindow(self.proc.pid)
            except Exception:
                pass
        self._send(f"show {x} {y}")

    def hide(self):
        if self.proc is not None and self.proc.poll() is None:
            self._send("hide")

    def stop(self):
        proc, self.proc = self.proc, None
        if proc is None or proc.poll() is not None:
            return
        try:
            proc.stdin.write(b"quit\n")
            proc.stdin.close()
            proc.wait(timeout=2)
        except Exception:
            proc.kill()


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

    if args[:1] in (["--webcam"], ["--webcam-helper"]):
        backend = backends.create_backend(config, application_path)
        if not backend.supports_webcam:
            print("Webcam not supported for this printer type")
            sys.exit(1)
        one_shot = args[0] == "--webcam"
        popup = WebcamPopup(backend, show_console=config.get("webcam_console", True), one_shot=one_shot)
        if one_shot:
            # Standalone popup (handy for testing): show now, exit on close.
            x = y = None
            if len(args) >= 3:
                try:
                    x, y = int(args[1]), int(args[2])
                except ValueError:
                    pass
            popup.root.after(0, popup.show, x, y)
        else:
            # Started hidden by the tray, which sends "show" on each click.
            popup.listen_stdin()
        popup.run()
        return

    TrayApp(config).run()


if __name__ == "__main__":
    main()
