try:
    import pystray
    import PIL
    import requests
    import paho.mqtt.client
    import backends
    import camera_bridge
    print("Imports successful.")
except ImportError as e:
    print(f"Import failed: {e}")
    exit(1)

# Sanity-check the Bambu MQTT report parsing against a canned "pushall"-style
# payload (community-documented schema - no official Bambu API docs exist).
sample_report = {
    "print": {
        "gcode_state": "RUNNING",
        "mc_percent": 42,
        "mc_remaining_time": 63,
        "subtask_name": "Benchy",
        "gcode_file": "Benchy.3mf",
        "layer_num": 12,
        "total_layer_num": 100,
    }
}

status = backends.parse_bambu_report(sample_report)
assert status.state == "printing", status.state
assert status.progress == 0.42, status.progress
assert status.time_left == 63 * 60, status.time_left
assert status.filename == "Benchy", status.filename
assert status.eta_source == "Slicer", status.eta_source

print("Bambu report parsing OK.")

# Sanity-check the go2rtc camera bridge's pure helper functions (no network,
# no spawning go2rtc - just string building).
rtsps_url = camera_bridge.build_rtsps_url("192.168.1.50", "12345678")
assert rtsps_url == "rtsps://bblp:12345678@192.168.1.50:322/streaming/live/1", rtsps_url

config_text = camera_bridge.build_go2rtc_config(23456, 23457, "bambu", rtsps_url, ffmpeg_bin=r"C:\short\ffmpeg.exe")
assert '"127.0.0.1:23456"' in config_text, config_text
assert '"127.0.0.1:23457"' in config_text, config_text
assert rtsps_url in config_text, config_text
assert "streams:" in config_text and "bambu:" in config_text, config_text
assert "ffmpeg:bambu#video=mjpeg" in config_text, config_text
assert r"C:\short\ffmpeg.exe" in config_text, config_text

print("go2rtc camera bridge helpers OK.")

# Settings / tray helpers (no network, no windows).
assert backends.normalize_moonraker_url(" mainsail.local/ ") == "http://mainsail.local"
assert backends.normalize_moonraker_url("https://printer:7125") == "https://printer:7125"
assert backends.normalize_moonraker_url("") == ""

keyed = backends.create_backend({"moonraker_url": "x", "moonraker_api_key": " k "}, ".")
assert keyed._headers == {"X-Api-Key": "k"}, keyed._headers
assert backends.create_backend({"moonraker_url": "x"}, ".")._headers == {}

assert backends.create_backend({"moonraker_url": "x"}, ".").supports_console
assert not backends.create_backend({"printer_type": "bambu"}, ".").supports_console

import klipper_tray
printing = backends.PrinterStatus(state="printing", progress=0.456, filename="Benchy.gcode",
                                  time_left=3900, eta_source="Slicer")
headline, details = klipper_tray.describe_status(printing)
assert headline == "Printing · 45%", headline
assert details[0].startswith("1h 5m left"), details
assert details[1] == "Benchy.gcode", details

offline = backends.PrinterStatus(state="offline", progress=0.0, message="Can't reach http://x")
assert klipper_tray.describe_status(offline) == ("Offline", ["Can't reach http://x"])
assert klipper_tray.shorten("abcdef", 4) == "…def"

for state in ("printing", "paused", "complete", "error", "offline", "standby"):
    for size in (64, 256):
        img = klipper_tray.create_tray_icon(state, 0.5, size=size)
        assert img.size == (size, size)

assert klipper_tray.TEMP_REPORT_RE.match("B:109.3 /110.0 T0:230.6 /250.0")
assert klipper_tray.TEMP_REPORT_RE.match("T:215.0 /215.0")
assert not klipper_tray.TEMP_REPORT_RE.match("// Klipper state: Ready")
assert not klipper_tray.TEMP_REPORT_RE.match("M109 S250")

print("Tray / settings helpers OK.")
print("Test complete.")
exit(0)
