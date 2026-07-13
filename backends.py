#!/usr/bin/env python3
"""Printer backend abstraction: Moonraker (Klipper) and Bambu Lab (LAN MQTT)."""
import json
import ssl
import threading
import time
from dataclasses import dataclass

import requests
import paho.mqtt.client as mqtt

import camera_bridge


def _iter_http_mjpeg_frames(url, stop_event):
    """Consume a plain HTTP MJPEG stream, yielding each JPEG frame as bytes."""
    try:
        with requests.get(url, stream=True, timeout=5) as r:
            r.raise_for_status()
            bytes_buffer = b''
            for chunk in r.iter_content(chunk_size=4096):
                if stop_event.is_set():
                    return
                bytes_buffer += chunk
                while True:
                    a = bytes_buffer.find(b'\xff\xd8')
                    b_idx = bytes_buffer.find(b'\xff\xd9')
                    if a != -1 and b_idx != -1:
                        yield bytes_buffer[a:b_idx + 2]
                        bytes_buffer = bytes_buffer[b_idx + 2:]
                    else:
                        break
    except Exception:
        return


@dataclass
class PrinterStatus:
    state: str
    progress: float
    filename: str = ""
    time_left: float = 0.0
    eta_source: str = "Calc"


class PrinterBackend:
    """Base class. Constructors must not perform network I/O - only start() may."""
    supports_webcam = False
    dashboard_url = None

    def start(self):
        pass

    def stop(self):
        pass

    def get_status(self) -> PrinterStatus:
        raise NotImplementedError

    def iter_webcam_frames(self, stop_event):
        raise NotImplementedError


class MoonrakerBackend(PrinterBackend):
    supports_webcam = True

    def __init__(self, moonraker_url):
        self.moonraker_url = moonraker_url.rstrip("/")
        self.dashboard_url = self.moonraker_url
        self._last_filename = None
        self._cached_estimated_time = None

    def get_status(self) -> PrinterStatus:
        """Fetch status from Moonraker API."""
        try:
            # Query print_stats, display_status, virtual_sdcard
            url = f"{self.moonraker_url}/printer/objects/query?print_stats&display_status&virtual_sdcard"
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

            # Metadata handling for accurate ETA
            if filename != self._last_filename:
                self._last_filename = filename
                self._cached_estimated_time = None
                if filename:
                    try:
                        # Fetch metadata
                        meta_url = f"{self.moonraker_url}/server/files/metadata?filename={filename}"
                        meta_resp = requests.get(meta_url, timeout=2)
                        if meta_resp.ok:
                            meta_data = meta_resp.json()
                            self._cached_estimated_time = meta_data.get("result", {}).get("estimated_time")
                    except Exception as e:
                        print(f"Metadata error: {e}")

            # Calculate time left
            time_left = 0
            eta_source = "Calc"

            if state == "printing":
                # Strategy 1: Slicer Estimate (Best)
                if self._cached_estimated_time and self._cached_estimated_time > 0:
                    time_left = max(0, self._cached_estimated_time - print_duration)
                    eta_source = "Slicer"

                # Strategy 2: Simple Calculation (Fallback)
                if (time_left == 0 or eta_source == "Calc") and progress > 0.01:
                    total_duration = print_duration / progress
                    time_left = total_duration - print_duration
                    eta_source = "Calc"

            return PrinterStatus(
                state=state,
                progress=progress,
                filename=filename,
                time_left=time_left,
                eta_source=eta_source,
            )
        except Exception:
            return PrinterStatus(state="error", progress=0.0, filename="", time_left=0)

    def get_webcam_url(self):
        """Discover webcam URL from Moonraker."""
        try:
            url = f"{self.moonraker_url}/server/webcams/list"
            response = requests.get(url, timeout=2)
            response.raise_for_status()
            data = response.json()
            webcams = data.get("result", {}).get("webcams", [])

            if not webcams:
                # Fallback default
                return f"{self.moonraker_url}/webcam/?action=stream"

            # Pick first enabled MJPEG stream
            for cam in webcams:
                if cam.get("enabled", True) and cam.get("service") in ["mjpegstreamer", "mjpegstreamer-adaptive", "ipstream"]:
                    stream_path = cam.get("stream_url", "")
                    if stream_path.startswith("http"):
                        return stream_path
                    elif stream_path.startswith("/"):
                        return f"{self.moonraker_url}{stream_path}"
                    return f"{self.moonraker_url}/{stream_path}"

            # Fallback if list exists but no suitable match found
            return f"{self.moonraker_url}/webcam/?action=stream"

        except Exception as e:
            print(f"Error fetching webcam list: {e}")
            return f"{self.moonraker_url}/webcam/?action=stream"

    def iter_webcam_frames(self, stop_event):
        yield from _iter_http_mjpeg_frames(self.get_webcam_url(), stop_event)


# Bambu gcode_state -> this app's common state vocabulary
_BAMBU_STATE_MAP = {
    "RUNNING": "printing",
    "PREPARE": "printing",
    "SLICING": "printing",
    "PAUSE": "paused",
    "FINISH": "complete",
    "FAILED": "error",
    "IDLE": "standby",
}

# How long to wait after a fresh connection for the first report before
# treating it as a config error (wrong serial/access code).
_FIRST_REPORT_TIMEOUT = 25


def parse_bambu_report(payload: dict) -> PrinterStatus:
    """Map a Bambu MQTT 'print' report object to a PrinterStatus.

    This mirrors the community-reverse-engineered Bambu local MQTT schema
    (no official API docs exist) - field names are best-effort and may vary
    across firmware versions.
    """
    print_obj = payload.get("print", payload)

    gcode_state = print_obj.get("gcode_state", "")
    state = _BAMBU_STATE_MAP.get(gcode_state, "standby")

    mc_percent = print_obj.get("mc_percent", 0) or 0
    progress = mc_percent / 100.0

    mc_remaining_time = print_obj.get("mc_remaining_time", 0) or 0
    time_left = mc_remaining_time * 60

    filename = print_obj.get("subtask_name") or print_obj.get("gcode_file") or ""

    return PrinterStatus(
        state=state,
        progress=progress,
        filename=filename,
        time_left=time_left,
        eta_source="Slicer",
    )


class BambuBackend(PrinterBackend):
    supports_webcam = True
    dashboard_url = None

    def __init__(self, ip, serial, access_code, app_dir):
        self.ip = ip
        self.serial = serial
        self.access_code = access_code
        self.app_dir = app_dir

        self._lock = threading.Lock()
        self._client = None
        self._connected = False
        self._connected_at = None
        self._latest_report = None
        self._last_message_time = None
        self._camera_bridge = None

    def _on_connect(self, client, userdata, flags, rc):
        with self._lock:
            self._connected = (rc == 0)
            self._connected_at = time.monotonic()
        if rc == 0:
            client.subscribe(f"device/{self.serial}/report")
            client.publish(
                f"device/{self.serial}/request",
                json.dumps({"pushing": {"sequence_id": "0", "command": "pushall"}}),
            )

    def _on_disconnect(self, client, userdata, rc):
        with self._lock:
            self._connected = False

    def _on_message(self, client, userdata, msg):
        try:
            payload = json.loads(msg.payload.decode("utf-8"))
        except Exception:
            return
        if "print" not in payload:
            return
        with self._lock:
            self._latest_report = payload
            self._last_message_time = time.monotonic()

    def start(self):
        client = mqtt.Client()
        client.username_pw_set("bblp", self.access_code)
        client.tls_set(cert_reqs=ssl.CERT_NONE)
        client.tls_insecure_set(True)
        client.on_connect = self._on_connect
        client.on_disconnect = self._on_disconnect
        client.on_message = self._on_message

        self._client = client
        client.connect_async(self.ip, 8883, keepalive=15)
        client.loop_start()

    def stop(self):
        if self._client is not None:
            try:
                self._client.loop_stop()
                self._client.disconnect()
            except Exception:
                pass

        with self._lock:
            bridge = self._camera_bridge
            self._camera_bridge = None
        if bridge is not None:
            bridge.stop()

    def get_status(self) -> PrinterStatus:
        with self._lock:
            connected = self._connected
            connected_at = self._connected_at
            report = self._latest_report

        if not connected:
            return PrinterStatus(state="error", progress=0.0, filename="", time_left=0)

        if report is None:
            if connected_at is not None and (time.monotonic() - connected_at) > _FIRST_REPORT_TIMEOUT:
                return PrinterStatus(state="error", progress=0.0, filename="", time_left=0)
            return PrinterStatus(state="standby", progress=0.0, filename="", time_left=0)

        return parse_bambu_report(report)

    def iter_webcam_frames(self, stop_event):
        bridge = camera_bridge.Go2RtcCameraBridge(self.ip, self.access_code, self.app_dir)
        with self._lock:
            self._camera_bridge = bridge
        try:
            mjpeg_url = bridge.start()
            if mjpeg_url is None or stop_event.is_set():
                return
            yield from _iter_http_mjpeg_frames(mjpeg_url, stop_event)
        finally:
            bridge.stop()
            with self._lock:
                if self._camera_bridge is bridge:
                    self._camera_bridge = None


def create_backend(config: dict, app_dir: str) -> PrinterBackend:
    printer_type = config.get("printer_type", "klipper")
    if printer_type == "bambu":
        return BambuBackend(
            ip=config.get("bambu_ip", ""),
            serial=config.get("bambu_serial", ""),
            access_code=config.get("bambu_access_code", ""),
            app_dir=app_dir,
        )
    return MoonrakerBackend(config.get("moonraker_url", "http://mainsail.local"))
