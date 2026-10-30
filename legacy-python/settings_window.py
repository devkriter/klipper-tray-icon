"""Settings window - used for first-run setup and for editing settings later.

Runs its own Tk mainloop, so the tray process launches it as a subprocess
(see klipper_tray.on_settings) rather than mixing Tk with pystray's loop.
"""
import threading
import tkinter as tk
from tkinter import ttk, messagebox

from PIL import ImageTk

import autostart
import backends

WINDOW_TITLE = "Klipper Tray Icon Settings"

PRINTER_TYPES = [
    ("klipper", "Klipper (Mainsail / Fluidd)"),
    ("bambu", "Bambu Lab"),
]


def _system_uses_dark_mode():
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as key:
            return winreg.QueryValueEx(key, "AppsUseLightTheme")[0] == 0
    except Exception:
        return False


def _enable_dpi_awareness():
    """Without this Windows bitmap-stretches the window on scaled displays
    (125%/150%), which makes all the text blurry."""
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass


def _apply_theme(root):
    dark = _system_uses_dark_mode()
    try:
        import sv_ttk
        sv_ttk.set_theme("dark" if dark else "light")
    except Exception:
        # Theme package missing - fall back to the stock Windows look.
        style = ttk.Style(root)
        if "vista" in style.theme_names():
            style.theme_use("vista")
    if dark:
        _dark_title_bar(root)
    return dark


def _dark_title_bar(root):
    """Win10 20H1+/Win11: match the title bar to dark mode."""
    try:
        import ctypes
        root.update_idletasks()
        hwnd = ctypes.windll.user32.GetParent(root.winfo_id())
        value = ctypes.c_int(1)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(value), ctypes.sizeof(value))
    except Exception:
        pass


API_KEY_HELP = (
    "Usually not needed. Moonraker lets devices on your home network in without a key "
    "(its trusted_clients setting covers 192.168.x.x, 10.x.x.x and 172.16–31.x.x "
    "addresses by default).\n\n"
    "You'll need it if:\n"
    "•  this PC reaches the printer over Tailscale, another VPN or the internet\n"
    "•  trusted_clients in moonraker.conf has been narrowed\n"
    "•  Mainsail/Fluidd is set to require a login\n\n"
    "If Test connection says Moonraker refused access, add the key here. To get it, run "
    "~/moonraker/scripts/fetch-apikey.sh on the printer."
)


class Tooltip:
    """Small hover popup. Tk has no built-in tooltip widget."""

    def __init__(self, widget, text, dark=False, delay_ms=250):
        self.widget = widget
        self.text = text
        self.dark = dark
        self.delay_ms = delay_ms
        self._after = None
        self._tip = None
        widget.bind("<Enter>", self._schedule, add="+")
        widget.bind("<Leave>", self._hide, add="+")
        widget.bind("<Button-1>", self._show, add="+")  # click works too (touch screens)

    def _schedule(self, event=None):
        self._cancel()
        self._after = self.widget.after(self.delay_ms, self._show)

    def _cancel(self):
        if self._after is not None:
            self.widget.after_cancel(self._after)
            self._after = None

    def _show(self, event=None):
        self._cancel()
        if self._tip is not None:
            return
        bg, fg, border = ("#2b2b2b", "#f0f0f0", "#4a4a4a") if self.dark else ("#ffffff", "#1a1a1a", "#c8c8c8")
        tip = tk.Toplevel(self.widget)
        tip.wm_overrideredirect(True)
        tip.attributes("-topmost", True)
        tip.configure(bg=border)
        tk.Label(tip, text=self.text, justify="left", wraplength=460, bg=bg, fg=fg,
                 font=("Segoe UI", 9), padx=10, pady=8).pack(padx=1, pady=1)
        tip.update_idletasks()
        x = self.widget.winfo_rootx() + self.widget.winfo_width() + 6
        y = self.widget.winfo_rooty() - 4
        # Keep it on screen.
        x = min(x, tip.winfo_screenwidth() - tip.winfo_reqwidth() - 8)
        y = min(y, tip.winfo_screenheight() - tip.winfo_reqheight() - 48)
        tip.geometry(f"+{x}+{y}")
        self._tip = tip

    def _hide(self, event=None):
        self._cancel()
        if self._tip is not None:
            self._tip.destroy()
            self._tip = None


class SettingsWindow:
    def __init__(self, config, app_dir, first_run=False, icon_image=None):
        self.app_dir = app_dir
        self.first_run = first_run
        self.result = None  # the saved config dict, or None if cancelled
        self._test_thread = None
        self._test_result = None

        _enable_dpi_awareness()
        self.root = tk.Tk()
        self.root.title("Set up Klipper Tray Icon" if first_run else WINDOW_TITLE)
        self.root.resizable(False, False)
        if icon_image is not None:
            self._icon = ImageTk.PhotoImage(icon_image)
            self.root.iconphoto(True, self._icon)
        self._dark = _apply_theme(self.root)

        self.var_type = tk.StringVar(value=config.get("printer_type", "klipper"))
        self.var_url = tk.StringVar(value=config.get("moonraker_url", "http://mainsail.local"))
        self.var_api_key = tk.StringVar(value=config.get("moonraker_api_key", ""))
        self.var_show_key = tk.BooleanVar(value=False)
        self.var_console = tk.BooleanVar(value=config.get("webcam_console", True))
        self.var_ip = tk.StringVar(value=config.get("bambu_ip", ""))
        self.var_serial = tk.StringVar(value=config.get("bambu_serial", ""))
        self.var_code = tk.StringVar(value=config.get("bambu_access_code", ""))
        self.var_show_code = tk.BooleanVar(value=False)
        self.var_interval = tk.IntVar(value=int(config.get("update_interval_seconds", 2)))
        self.var_notify = tk.BooleanVar(value=config.get("notifications", True))
        self.var_autostart = tk.BooleanVar(value=autostart.is_enabled() if not first_run else True)
        self._original = dict(config)

        self._hints = []
        self._build()
        self._lock_width()
        self._on_type_changed()
        self.root.protocol("WM_DELETE_WINDOW", self._cancel)
        self.root.bind("<Escape>", lambda e: self._cancel())
        self.root.bind("<Return>", lambda e: self._save())
        self._center()

    # ---- layout -------------------------------------------------------

    def _build(self):
        outer = ttk.Frame(self.root, padding=(24, 20, 24, 20))
        outer.pack(fill=tk.BOTH, expand=True)
        outer.columnconfigure(0, weight=1)

        row = 0
        if self.first_run:
            ttk.Label(outer, text="Welcome!", font=("Segoe UI Semibold", 16)).grid(row=row, column=0, sticky="w")
            row += 1
            intro = ttk.Label(outer, text="Tell the tray icon which printer to watch. You can change this "
                                          "later from the tray menu under Settings.", justify="left")
            intro.grid(row=row, column=0, sticky="w", pady=(4, 16))
            self._hints.append(intro)  # so it wraps to the window width too
            row += 1

        # Printer section
        ttk.Label(outer, text="Printer", font=("Segoe UI Semibold", 12)).grid(row=row, column=0, sticky="w")
        row += 1
        type_row = ttk.Frame(outer)
        type_row.grid(row=row, column=0, sticky="w", pady=(8, 12))
        for value, label in PRINTER_TYPES:
            ttk.Radiobutton(type_row, text=label, value=value, variable=self.var_type,
                            command=self._on_type_changed).pack(side=tk.LEFT, padx=(0, 18))
        row += 1

        self.klipper_frame = ttk.Frame(outer)
        self.klipper_frame.columnconfigure(1, weight=1)
        self._field(self.klipper_frame, 0, "Address", self.var_url)
        self._hint(self.klipper_frame, 1, "The address you open Mainsail or Fluidd at, "
                                          "e.g. mainsail.local or 192.168.1.50")
        key_label = ttk.Frame(self.klipper_frame)
        key_label.grid(row=2, column=0, sticky="w", padx=(0, 12), pady=4)
        ttk.Label(key_label, text="API key").pack(side=tk.LEFT)
        info = ttk.Label(key_label, text="ⓘ", font=("Segoe UI Symbol", 11), cursor="hand2",
                         foreground="#4cc2ff" if self._dark else "#005fb8")
        info.pack(side=tk.LEFT, padx=(4, 0))
        self.api_key_tooltip = Tooltip(info, API_KEY_HELP, dark=self._dark)
        self.key_entry = ttk.Entry(self.klipper_frame, textvariable=self.var_api_key, width=36, show="•")
        self.key_entry.grid(row=2, column=1, sticky="we", pady=4)
        ttk.Checkbutton(self.klipper_frame, text="Show", variable=self.var_show_key,
                        command=self._toggle_key).grid(row=2, column=2, padx=(8, 0))
        self._hint(self.klipper_frame, 3, "Optional — leave blank unless Moonraker asks for one.")
        ttk.Checkbutton(self.klipper_frame, text="Show the Klipper console next to the webcam",
                        variable=self.var_console).grid(row=4, column=0, columnspan=3, sticky="w", pady=(0, 8))

        self.bambu_frame = ttk.Frame(outer)
        self.bambu_frame.columnconfigure(1, weight=1)
        self._field(self.bambu_frame, 0, "IP address", self.var_ip)
        self._field(self.bambu_frame, 1, "Serial number", self.var_serial)
        self.code_entry = self._field(self.bambu_frame, 2, "Access code", self.var_code, show="•")
        ttk.Checkbutton(self.bambu_frame, text="Show", variable=self.var_show_code,
                        command=self._toggle_code).grid(row=2, column=2, padx=(8, 0))
        self._hint(self.bambu_frame, 3, "All three are on the printer's screen under Settings → "
                                        "WLAN / Network. For the camera, turn on “LAN Mode Live View”. "
                                        "LAN Only Mode is not required.")

        self.fields_row = row
        row += 1

        test_row = ttk.Frame(outer)
        test_row.grid(row=row, column=0, sticky="we", pady=(4, 0))
        self.test_btn = ttk.Button(test_row, text="Test connection", command=self._start_test)
        self.test_btn.pack(side=tk.LEFT)
        self.test_label = ttk.Label(test_row, text="", wraplength=280, justify="left")
        self.test_label.pack(side=tk.LEFT, padx=(12, 0))
        row += 1

        ttk.Separator(outer).grid(row=row, column=0, sticky="we", pady=18)
        row += 1

        # General section
        ttk.Label(outer, text="General", font=("Segoe UI Semibold", 12)).grid(row=row, column=0, sticky="w")
        row += 1
        interval_row = ttk.Frame(outer)
        interval_row.grid(row=row, column=0, sticky="w", pady=(8, 4))
        ttk.Label(interval_row, text="Check printer every").pack(side=tk.LEFT)
        ttk.Spinbox(interval_row, from_=1, to=60, width=4, textvariable=self.var_interval).pack(side=tk.LEFT, padx=8)
        ttk.Label(interval_row, text="seconds").pack(side=tk.LEFT)
        row += 1
        ttk.Checkbutton(outer, text="Notify me when a print finishes, fails or pauses",
                        variable=self.var_notify).grid(row=row, column=0, sticky="w", pady=4)
        row += 1
        cb = ttk.Checkbutton(outer, text="Start with Windows", variable=self.var_autostart)
        cb.grid(row=row, column=0, sticky="w", pady=4)
        if not autostart.is_supported():
            cb.state(["disabled"])
        row += 1

        buttons = ttk.Frame(outer)
        buttons.grid(row=row, column=0, sticky="e", pady=(20, 0))
        ttk.Button(buttons, text="Quit" if self.first_run else "Cancel",
                   command=self._cancel).pack(side=tk.RIGHT)
        save = ttk.Button(buttons, text="Start" if self.first_run else "Save", command=self._save)
        try:
            save.configure(style="Accent.TButton")  # sv_ttk's highlighted button
        except tk.TclError:
            pass
        save.pack(side=tk.RIGHT, padx=(0, 8))

        self._outer = outer

    def _field(self, parent, row, label, var, show=None):
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=(0, 12), pady=4)
        entry = ttk.Entry(parent, textvariable=var, width=36)
        if show:
            entry.configure(show=show)
        entry.grid(row=row, column=1, sticky="we", pady=4)
        return entry

    def _hint(self, parent, row, text):
        hint = ttk.Label(parent, text=text, wraplength=400, justify="left", foreground="#8a8a8a")
        hint.grid(row=row, column=0, columnspan=3, sticky="w", pady=(2, 8))
        self._hints.append(hint)

    def _lock_width(self):
        """Size the window for the wider of the two printer forms so it
        doesn't jump around when switching type, and wrap hints to fit."""
        self.root.update_idletasks()
        width = max(self.klipper_frame.winfo_reqwidth(), self.bambu_frame.winfo_reqwidth())
        self._outer.columnconfigure(0, minsize=width)
        for hint in self._hints:
            hint.configure(wraplength=width)

    def _center(self):
        self.root.update_idletasks()
        w, h = self.root.winfo_reqwidth(), self.root.winfo_reqheight()
        x = (self.root.winfo_screenwidth() - w) // 2
        y = (self.root.winfo_screenheight() - h) // 3
        self.root.geometry(f"+{x}+{y}")

    # ---- behaviour ----------------------------------------------------

    def _on_type_changed(self):
        is_bambu = self.var_type.get() == "bambu"
        self.klipper_frame.grid_forget()
        self.bambu_frame.grid_forget()
        frame = self.bambu_frame if is_bambu else self.klipper_frame
        frame.grid(row=self.fields_row, column=0, sticky="we")
        self.test_label.configure(text="")

    def _toggle_key(self):
        self.key_entry.configure(show="" if self.var_show_key.get() else "•")

    def _toggle_code(self):
        self.code_entry.configure(show="" if self.var_show_code.get() else "•")

    def _collect(self):
        """Builds a config dict from the form, or shows an error and returns None."""
        config = dict(self._original)
        printer_type = self.var_type.get()
        config["printer_type"] = printer_type
        if printer_type == "klipper":
            url = backends.normalize_moonraker_url(self.var_url.get())
            if not url:
                messagebox.showwarning("Missing address", "Enter your printer's address.", parent=self.root)
                return None
            config["moonraker_url"] = url
            config["moonraker_api_key"] = self.var_api_key.get().strip()
            config["webcam_console"] = bool(self.var_console.get())
        else:
            ip = self.var_ip.get().strip()
            serial = self.var_serial.get().strip().upper()
            code = self.var_code.get().strip()
            missing = [name for name, v in (("IP address", ip), ("serial number", serial),
                                            ("access code", code)) if not v]
            if missing:
                messagebox.showwarning("Missing details", "Enter the printer's " + ", ".join(missing) + ".",
                                       parent=self.root)
                return None
            config.update(bambu_ip=ip, bambu_serial=serial, bambu_access_code=code)

        try:
            interval = int(self.var_interval.get())
        except (tk.TclError, ValueError):
            interval = 2
        config["update_interval_seconds"] = max(1, min(60, interval))
        config["notifications"] = bool(self.var_notify.get())
        return config

    def _start_test(self):
        if self._test_thread is not None:
            return
        config = self._collect()
        if config is None:
            return
        self.test_btn.state(["disabled"])
        self.test_label.configure(text="Testing…", foreground="")

        def run():
            try:
                self._test_result = backends.test_connection(config, self.app_dir)
            except Exception as e:
                self._test_result = (False, f"Test failed: {e}")

        self._test_thread = threading.Thread(target=run, daemon=True)
        self._test_thread.start()
        self.root.after(150, self._poll_test)

    def _poll_test(self):
        if self._test_thread.is_alive():
            self.root.after(150, self._poll_test)
            return
        self._test_thread = None
        ok, message = self._test_result
        self.test_btn.state(["!disabled"])
        self.test_label.configure(text=("✔ " if ok else "✖ ") + message,
                                  foreground="#2e9e4f" if ok else "#d13438")

    def _save(self):
        config = self._collect()
        if config is None:
            return
        try:
            autostart.set_enabled(bool(self.var_autostart.get()))
        except OSError as e:
            messagebox.showwarning("Start with Windows",
                                   f"Couldn't update the startup setting:\n{e}", parent=self.root)
        self.result = config
        self.root.destroy()

    def _cancel(self):
        self.result = None
        self.root.destroy()

    def run(self):
        self.root.lift()
        self.root.focus_force()
        self.root.mainloop()
        return self.result


def show(config, app_dir, first_run=False, icon_image=None):
    """Opens the window and blocks until it closes. Returns the new config
    dict if the user saved, else None."""
    return SettingsWindow(config, app_dir, first_run=first_run, icon_image=icon_image).run()
