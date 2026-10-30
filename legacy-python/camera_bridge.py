#!/usr/bin/env python3
"""Manages a local go2rtc helper process that turns a Bambu Lab printer's
RTSPS camera stream (X1/X2D/P2S/H2D/H2C/H2S) into plain HTTP MJPEG we can
already consume with the app's existing MJPEG frame parsing.

go2rtc (https://github.com/AlexxIT/go2rtc, MIT licensed) is a small
single-binary media gateway. We bundle/download it rather than writing our
own RTSP+H.264 client.
"""
import ctypes
import io
import os
import socket
import subprocess
import sys
import tempfile
import time
import zipfile

import requests

GO2RTC_VERSION = "v1.9.14"
GO2RTC_WIN64_URL = f"https://github.com/AlexxIT/go2rtc/releases/download/{GO2RTC_VERSION}/go2rtc_win64.zip"

# go2rtc has no native RTSPS decoder - for "rtsps://" sources it always
# shells out to an external ffmpeg binary. Pinned to the n8.1 static
# (no separate DLLs) Windows build from BtbN's GitHub-hosted releases.
FFMPEG_ASSET = "ffmpeg-n8.1-latest-win64-lgpl-8.1.zip"
FFMPEG_URL = f"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/{FFMPEG_ASSET}"


def build_rtsps_url(ip, access_code, stream="streaming/live/1"):
    return f"rtsps://bblp:{access_code}@{ip}:322/{stream}"


def _short_path(path):
    """Best-effort Windows 8.3 short path (no spaces) for a path that
    already exists on disk. go2rtc naively whitespace-splits its
    ffmpeg.bin config value before exec'ing it, so any path containing a
    space (this app's own folder, or a Windows username with a space in
    it under AppData) breaks it outright. Falls back to the original path
    if short-name generation isn't available for that volume (rare)."""
    try:
        buf = ctypes.create_unicode_buffer(260)
        if ctypes.windll.kernel32.GetShortPathNameW(path, buf, 260):
            return buf.value
    except Exception:
        pass
    return path


def build_go2rtc_config(port, rtsp_port, stream_name, rtsps_url, ffmpeg_bin=None):
    """Minimal go2rtc YAML config. ffmpeg_bin, if given, should already be a
    space-free path (see _short_path) - it's placed in a single-quoted YAML
    scalar so backslashes in a Windows path aren't treated as escapes.

    The stream needs two producer entries: the raw RTSPS source (H.264),
    plus a self-referencing "ffmpeg:<name>#video=mjpeg" entry that
    transcodes it to MJPEG - go2rtc's /api/stream.mjpeg endpoint requires
    an MJPEG-codec producer and will not transcode H.264 on its own. That
    second entry works by having the spawned ffmpeg process pull the
    stream back from go2rtc's own internal RTSP server - so unlike WebRTC,
    the RTSP listener can't be disabled even though we never consume it
    directly ourselves; it's given its own dedicated local port instead."""
    lines = [
        f'api:\n  listen: "127.0.0.1:{port}"',
        f'rtsp:\n  listen: "127.0.0.1:{rtsp_port}"',
        'webrtc:\n  listen: ""',
    ]
    if ffmpeg_bin:
        lines.append(f"ffmpeg:\n  bin: '{ffmpeg_bin}'")
    lines.append(
        f'streams:\n'
        f'  {stream_name}:\n'
        f'    - "{rtsps_url}"\n'
        f'    - "ffmpeg:{stream_name}#video=mjpeg"'
    )
    return "\n".join(lines) + "\n"


def _bundled_helper_path(name):
    """Packaged builds ship go2rtc/ffmpeg as plain files next to the main exe
    (not packed inside it - self-extracting helper binaries trip antivirus
    heuristics). Also checks PyInstaller's _MEIPASS for older-style builds."""
    if not getattr(sys, "frozen", False):
        return None
    dirs = [os.path.dirname(sys.executable)]
    if hasattr(sys, "_MEIPASS"):
        dirs.append(sys._MEIPASS)
    for d in dirs:
        candidate = os.path.join(d, name)
        if os.path.exists(candidate):
            return candidate
    return None


class Go2RtcCameraBridge:
    def __init__(self, ip, access_code, app_dir, stream_name="bambu"):
        self.ip = ip
        self.access_code = access_code
        self.app_dir = app_dir
        self.stream_name = stream_name
        self._proc = None
        self._config_path = None
        self._port = None

    def _bundled_binary_path(self):
        return _bundled_helper_path("go2rtc.exe")

    def _cached_binary_path(self):
        return os.path.join(self.app_dir, "go2rtc_cache", f"go2rtc_{GO2RTC_VERSION}.exe")

    def _ensure_binary(self):
        bundled = self._bundled_binary_path()
        if bundled:
            return bundled

        cached = self._cached_binary_path()
        if os.path.exists(cached):
            return cached

        os.makedirs(os.path.dirname(cached), exist_ok=True)
        resp = requests.get(GO2RTC_WIN64_URL, timeout=30)
        resp.raise_for_status()
        with zipfile.ZipFile(io.BytesIO(resp.content)) as zf:
            exe_names = [n for n in zf.namelist() if n.lower().endswith(".exe")]
            if not exe_names:
                raise RuntimeError("go2rtc executable not found in downloaded archive")
            data = zf.read(exe_names[0])

        tmp_path = cached + ".tmp"
        with open(tmp_path, "wb") as f:
            f.write(data)
        os.replace(tmp_path, cached)
        return cached

    def _bundled_ffmpeg_path(self):
        return _bundled_helper_path("ffmpeg.exe")

    def _cached_ffmpeg_path(self):
        return os.path.join(self.app_dir, "go2rtc_cache", "ffmpeg.exe")

    def _ensure_ffmpeg(self):
        bundled = self._bundled_ffmpeg_path()
        if bundled:
            return bundled

        cached = self._cached_ffmpeg_path()
        if os.path.exists(cached):
            return cached

        os.makedirs(os.path.dirname(cached), exist_ok=True)
        resp = requests.get(FFMPEG_URL, timeout=120)
        resp.raise_for_status()
        with zipfile.ZipFile(io.BytesIO(resp.content)) as zf:
            # The build ships as <top-level-folder>/bin/{ffmpeg,ffplay,ffprobe}.exe -
            # only pull the one we need, not the whole ~130MB archive contents.
            names = [n for n in zf.namelist() if n.lower().endswith("/bin/ffmpeg.exe")]
            if not names:
                raise RuntimeError("ffmpeg executable not found in downloaded archive")
            data = zf.read(names[0])

        tmp_path = cached + ".tmp"
        with open(tmp_path, "wb") as f:
            f.write(data)
        os.replace(tmp_path, cached)
        return cached

    @staticmethod
    def _free_port():
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind(("127.0.0.1", 0))
            return s.getsockname()[1]

    def start(self, timeout=10):
        """Downloads/locates go2rtc, spawns it pointed at this printer's
        RTSPS camera, and waits for its API to come up. Returns the local
        MJPEG URL to consume, or None if go2rtc couldn't be started or the
        upstream camera never became reachable within `timeout` seconds."""
        try:
            binary_path = self._ensure_binary()
            ffmpeg_path = self._ensure_ffmpeg()
        except Exception:
            return None

        self._port = self._free_port()
        rtsp_port = self._free_port()
        rtsps_url = build_rtsps_url(self.ip, self.access_code)
        config_text = build_go2rtc_config(
            self._port, rtsp_port, self.stream_name, rtsps_url, ffmpeg_bin=_short_path(ffmpeg_path)
        )

        fd, self._config_path = tempfile.mkstemp(suffix=".yaml", prefix="go2rtc_")
        with os.fdopen(fd, "w") as f:
            f.write(config_text)

        creationflags = subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0
        try:
            self._proc = subprocess.Popen(
                [binary_path, "-config", self._config_path],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=creationflags,
            )
        except Exception:
            return None

        base_url = f"http://127.0.0.1:{self._port}"
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self._proc.poll() is not None:
                return None  # go2rtc exited early
            try:
                r = requests.get(f"{base_url}/api/streams", timeout=1)
                if r.ok:
                    return f"{base_url}/api/stream.mjpeg?src={self.stream_name}"
            except Exception:
                pass
            time.sleep(0.3)
        return None  # timed out waiting for the upstream camera

    def stop(self):
        if self._proc is not None:
            try:
                self._proc.terminate()
                self._proc.wait(timeout=3)
            except Exception:
                try:
                    self._proc.kill()
                except Exception:
                    pass
        if self._config_path and os.path.exists(self._config_path):
            try:
                os.remove(self._config_path)
            except Exception:
                pass
