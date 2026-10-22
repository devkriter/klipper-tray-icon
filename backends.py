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


def _iter_http_mjpeg_frames(url, stop_event, headers=None):
    """Consume a plain HTTP MJPEG stream, yielding each JPEG frame as bytes."""
    try:
        with requests.get(url, stream=True, timeout=5, headers=headers) as r:
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
    # state is one of: printing, paused, complete, cancelled, error (printer
    # reports a fault), standby, or offline (we can't talk to the printer -
    # message says why).
    state: str
    progress: float
    filename: str = ""
    time_left: float = 0.0
    eta_source: str = "Calc"
    message: str = ""


def normalize_moonraker_url(url):
    """Accepts what people actually type ("mainsail.local", "192.168.1.5/")
    and returns a usable base URL."""
    url = (url or "").strip().rstrip("/")
    if url and "://" not in url:
        url = "http://" + url
    return url


def _describe_request_error(e, url, api_key=""):
    if isinstance(e, requests.exceptions.Timeout):
        return f"{url} didn't respond in time"
    if isinstance(e, requests.exceptions.ConnectionError):
        return f"Can't reach {url}"
    if isinstance(e, requests.exceptions.HTTPError) and e.response is not None:
        if e.response.status_code in (401, 403):
            if api_key:
                return "Moonraker rejected the API key"
            return "Moonraker refused access - enter an API key in Settings"
        return f"{url} returned HTTP {e.response.status_code}"
    return f"Unexpected response from {url}"


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

    def __init__(self, moonraker_url, api_key=""):
        self.moonraker_url = normalize_moonraker_url(moonraker_url)
        self.api_key = (api_key or "").strip()
        # Only needed when Moonraker doesn't already trust this PC's address
        # (trusted_clients in moonraker.conf), e.g. over Tailscale/VPN.
        self._headers = {"X-Api-Key": self.api_key} if self.api_key else {}
        self.dashboard_url = self.moonraker_url
        self._last_filename = None
        self._cached_estimated_time = None

    def get_status(self) -> PrinterStatus:
        """Fetch status from Moonraker API."""
        try:
            # Query print_stats, display_status, virtual_sdcard
            url = f"{self.moonraker_url}/printer/objects/query?print_stats&display_status&virtual_sdcard"
            response = requests.get(url, timeout=2, headers=self._headers)
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
                        meta_resp = requests.get(meta_url, timeout=2, headers=self._headers)
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
        except Exception as e:
            return PrinterStatus(state="offline", progress=0.0,
                                 message=_describe_request_error(e, self.moonraker_url, self.api_key))

    def test_connection(self):
        """Returns (ok, message) for the settings window's Test button."""
        try:
            r = requests.get(f"{self.moonraker_url}/printer/info", timeout=4, headers=self._headers)
            r.raise_for_status()
            info = r.json().get("result", {})
        except Exception as e:
            return False, _describe_request_error(e, self.moonraker_url, self.api_key)
        host = info.get("hostname") or self.moonraker_url
        klippy = info.get("state", "unknown")
        if klippy != "ready":
            return True, f"Connected to {host}, but Klipper is '{klippy}'"
        return True, f"Connected to {host}"

    def get_webcam_url(self):
        """Discover webcam URL from Moonraker."""
        try:
            url = f"{self.moonraker_url}/server/webcams/list"
            response = requests.get(url, timeout=2, headers=self._headers)
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
        url = self.get_webcam_url()
        # Don't hand the key to a camera hosted somewhere else.
        headers = self._headers if url.startswith(self.moonraker_url) else None
        yield from _iter_http_mjpeg_frames(url, stop_event, headers)


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
        self._connect_rc = None
        self._latest_report = None
        self._last_message_time = None
        self._camera_bridge = None

    def _on_connect(self, client, userdata, flags, rc):
        with self._lock:
            self._connected = (rc == 0)
            self._connected_at = time.monotonic()
            self._connect_rc = rc
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

    def _offline_reason(self, first_report_timeout):
        """None if things look healthy (or it's too early to tell)."""
        with self._lock:
            connected = self._connected
            connected_at = self._connected_at
            rc = self._connect_rc
            has_report = self._latest_report is not None

        if rc in (4, 5):  # MQTT "bad username/password" / "not authorized"
            return "Printer rejected the access code"
        if not connected:
            return f"Can't reach printer at {self.ip}"
        if not has_report and connected_at is not None                 and (time.monotonic() - connected_at) > first_report_timeout:
            return "Connected, but no data from printer - check the serial number"
        return None

    def get_status(self) -> PrinterStatus:
        reason = self._offline_reason(_FIRST_REPORT_TIMEOUT)
        if reason:
            return PrinterStatus(state="offline", progress=0.0, message=reason)

        with self._lock:
            report = self._latest_report
        if report is None:
            return PrinterStatus(state="standby", progress=0.0, message="Waiting for printer...")
        return parse_bambu_report(report)

    def test_connection(self, timeout=12):
        """Returns (ok, message). Blocks up to `timeout` seconds; must be
        called on a backend that was start()ed just for the test."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            with self._lock:
                rc = self._connect_rc
                report = self._latest_report
            if report is not None:
                status = parse_bambu_report(report)
                return True, f"Connected - printer is {status.state}"
            if rc in (4, 5):
                return False, "Printer rejected the access code"
            time.sleep(0.2)
        reason = self._offline_reason(first_report_timeout=0)
        return False, reason or f"No response from printer at {self.ip}"

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


def test_connection(config: dict, app_dir: str):
    """Spins up a throwaway backend for `config` and reports (ok, message)."""
    backend = create_backend(config, app_dir)
    backend.start()
    try:
        return backend.test_connection()
    finally:
        backend.stop()


def create_backend(config: dict, app_dir: str) -> PrinterBackend:
    printer_type = config.get("printer_type", "klipper")
    if printer_type == "bambu":
        return BambuBackend(
            ip=config.get("bambu_ip", ""),
            serial=config.get("bambu_serial", ""),
            access_code=config.get("bambu_access_code", ""),
            app_dir=app_dir,
        )
    return MoonrakerBackend(config.get("moonraker_url", "http://mainsail.local"),
                            api_key=config.get("moonraker_api_key", ""))
