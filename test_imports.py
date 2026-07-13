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

config_text = camera_bridge.build_go2rtc_config(23456, "bambu", rtsps_url)
assert '"127.0.0.1:23456"' in config_text, config_text
assert rtsps_url in config_text, config_text
assert "streams:" in config_text and "bambu:" in config_text, config_text

print("go2rtc camera bridge helpers OK.")
print("Test complete.")
exit(0)
