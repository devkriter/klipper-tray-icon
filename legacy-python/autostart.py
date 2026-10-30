"""'Start with Windows' via the per-user Run registry key (no admin needed)."""
import os
import sys

try:
    import winreg
except ImportError:  # not on Windows
    winreg = None

_RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
_VALUE_NAME = "KlipperTrayIcon"


def is_supported():
    return winreg is not None


def _launch_command():
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}"'
    # Running from source: use pythonw so no console window pops up at login.
    exe = sys.executable
    pythonw = os.path.join(os.path.dirname(exe), "pythonw.exe")
    if os.path.exists(pythonw):
        exe = pythonw
    script = os.path.abspath(sys.modules["__main__"].__file__)
    return f'"{exe}" "{script}"'


def _read_value():
    if not is_supported():
        return None
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _RUN_KEY) as key:
            return winreg.QueryValueEx(key, _VALUE_NAME)[0]
    except OSError:
        return None


def is_enabled():
    return _read_value() is not None


def set_enabled(enabled):
    if not is_supported():
        return
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
        if enabled:
            winreg.SetValueEx(key, _VALUE_NAME, 0, winreg.REG_SZ, _launch_command())
        else:
            try:
                winreg.DeleteValue(key, _VALUE_NAME)
            except FileNotFoundError:
                pass


def refresh_path():
    """If autostart is on but points at an old location (e.g. the user
    unzipped a new version somewhere else), repoint it at this copy."""
    current = _read_value()
    if current is not None and current != _launch_command():
        try:
            set_enabled(True)
        except OSError:
            pass
