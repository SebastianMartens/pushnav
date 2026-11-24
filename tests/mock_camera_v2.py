# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 8 and
# specs/start/SPEC_PROTOCOL_CAMERA.md section 11).
"""Protocol v2 session of the mock camera server (``MockCameraServer(v2=True)``).

Behaves like the C# server will: an IDS-like set of controls (``exposure`` in
microseconds, ``fps``, ``gain``, ``binning``) with coupling between them,
STREAM_INFO with generations, binning that serves downscaled sample frames,
and GRAY8 / FRAME_RAW on request. A client that answers ``protocol_version: 1``
gets the v1 view of the same camera (``exposure`` in ``"100us"`` and ``gain``,
JPEG frames, no STREAM_INFO), as protocol section 11.2 demands.

Kept out of ``mock_camera_server.py`` so the v1 mock stays as upstream has it.
"""

from __future__ import annotations

import copy
import io
import json
import logging
import math
import select
import socket
import struct
import time
from pathlib import Path

from PIL import Image

from evf.camera.protocol import (
    MSG_CONTROL_INFO,
    MSG_ERROR,
    MSG_FRAME,
    MSG_FRAME_RAW,
    MSG_GET_CONTROLS,
    MSG_SET_CONTROL,
    MSG_SET_STREAM_CONFIG,
    MSG_STREAM_INFO,
    DisconnectError,
    encode_json_message,
    encode_message,
    read_message,
)

logger = logging.getLogger(__name__)

EXPOSURE_MIN_US = 92
EXPOSURE_MAX_US = 4_000_000
FPS_MIN = 0.25
FPS_MAX_BY_BINNING = {1: 10.0, 2: 20.0}
BINNING_OPTIONS = (1, 2)
GAIN_MAX = 100

V2_HELLO = {
    "protocol_version": 1,
    "max_protocol_version": 2,
    "backend": "mock-python-v2",
    "backend_version": "0.1.0",
    "camera_model": "MOCK_V2",
    "camera_key": "mock:v2",
    "stream_format": "MJPEG",
    "default_width": 1280,
    "default_height": 720,
    "default_fps": 10,
    "capabilities": {
        "frame_formats": ["JPEG", "GRAY8"],
        "stream_info": True,
        "sensor": {"width": 1280, "height": 720, "pixel_size_um": 3.75, "mono": True},
    },
}


class V2Camera:
    """Control state with the coupling of protocol section 11.4.3."""

    def __init__(self, fps: float, exposure_us: int, gain: int = 20) -> None:
        self.binning = 1
        self.exposure_us = int(exposure_us)
        self.gain = gain
        self.fps = _floor2(max(FPS_MIN, min(fps, self.fps_max)))
        self._couple_fps_to_exposure()

    @property
    def fps_max(self) -> float:
        """Hardware limit of the binning, lowered so the frame interval still covers the exposure."""
        return min(FPS_MAX_BY_BINNING[self.binning], _floor2(1e6 / self.exposure_us))

    @property
    def frame_interval_us(self) -> int:
        return int(round(1e6 / self.fps))

    def descriptors(self) -> list[dict]:
        return [
            {"id": "exposure", "label": "Exposure", "type": "int", "unit": "us",
             "min": EXPOSURE_MIN_US, "max": EXPOSURE_MAX_US, "step": 1, "cur": self.exposure_us,
             "scale": "log", "default": 100_000},
            {"id": "fps", "label": "Frame rate", "type": "float", "unit": "fps",
             "min": FPS_MIN, "max": self.fps_max, "step": 0.01, "cur": self.fps},
            {"id": "gain", "label": "Gain", "type": "int", "unit": "raw",
             "min": 0, "max": GAIN_MAX, "step": 1, "cur": self.gain},
            {"id": "binning", "label": "Binning", "type": "enum", "cur": self.binning,
             "options": [{"value": n, "label": f"{n}x{n}"} for n in BINNING_OPTIONS],
             "restarts_stream": True},
        ]

    def descriptors_v1(self) -> list[dict]:
        """What a v1 session sees: exposure in 100 us units (capped at 1.5 s) and gain."""
        return [
            {"id": "exposure", "label": "Exposure", "type": "int", "unit": "100us",
             "min": 1, "max": 15000, "step": 1, "cur": max(1, min(15000, self.exposure_us // 100))},
            {"id": "gain", "label": "Gain", "type": "int", "unit": "raw",
             "min": 0, "max": GAIN_MAX, "step": 1, "cur": self.gain},
        ]

    def apply(self, control_id: str, value, version: int) -> tuple[bool, bool]:
        """Apply SET_CONTROL. Returns ``(stream restarts, frame interval changed)``."""
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            return False, False
        interval_before = self.frame_interval_us
        if control_id == "exposure":
            microseconds = value * 100 if version == 1 else value
            self.exposure_us = int(round(max(EXPOSURE_MIN_US, min(EXPOSURE_MAX_US, microseconds))))
            self._couple_fps_to_exposure()
        elif control_id == "gain":
            self.gain = int(round(max(0, min(GAIN_MAX, value))))
        elif version >= 2 and control_id == "fps":
            self.fps = _floor2(max(FPS_MIN, min(self.fps_max, value)))
        elif version >= 2 and control_id == "binning":
            if value not in BINNING_OPTIONS:
                return False, False  # an enum value that is not an option is rejected
            if int(value) != self.binning:
                self.binning = int(value)
                self.fps = _floor2(max(FPS_MIN, min(self.fps, self.fps_max)))
                return True, True
        return False, self.frame_interval_us != interval_before

    def _couple_fps_to_exposure(self) -> None:
        """Exposure priority: an exposure longer than the frame interval lowers fps."""
        if self.exposure_us > 1e6 / self.fps:
            self.fps = max(FPS_MIN, _floor2(1e6 / self.exposure_us))


def _floor2(value: float) -> float:
    return math.floor(value * 100 + 1e-9) / 100


class FrameSource:
    """Sample images at the current binning, as JPEG or GRAY8."""

    def __init__(self, sample_dir: Path, frame_size: tuple[int, int] | None) -> None:
        paths = sorted(sample_dir.glob("*.png"))
        images = [Image.open(p).convert("RGB") for p in paths]
        if not images:
            images = [Image.linear_gradient("L").resize((1280, 720)).convert("RGB")]
        if frame_size is not None:
            images = [img.resize(frame_size, Image.LANCZOS) for img in images]
        self._images = images
        self._cache: dict[tuple[int, int, str], bytes] = {}

    @property
    def size(self) -> tuple[int, int]:
        return self._images[0].size

    def size_at(self, binning: int) -> tuple[int, int]:
        width, height = self.size
        return width // binning, height // binning

    def jpeg(self, index: int, binning: int) -> bytes:
        return self._get(index, binning, "JPEG")

    def gray8(self, index: int, binning: int) -> bytes:
        return self._get(index, binning, "GRAY8")

    def _get(self, index: int, binning: int, kind: str) -> bytes:
        slot = index % len(self._images)
        key = (slot, binning, kind)
        if key not in self._cache:
            image = self._images[slot]
            if binning > 1:
                image = image.resize(self.size_at(binning), Image.LANCZOS)
            if kind == "JPEG":
                buf = io.BytesIO()
                image.convert("L").save(buf, "JPEG", quality=85)
                self._cache[key] = buf.getvalue()
            else:
                self._cache[key] = image.convert("L").tobytes()
        return self._cache[key]


class V2Session:
    """One client connection of a ``MockCameraServer(v2=True)``."""

    def __init__(self, server, client, camera: V2Camera, frames: FrameSource, reconfigure_s: float) -> None:
        self._server = server
        self._client = client
        self._camera = camera
        self._frames = frames
        self._reconfigure_s = reconfigure_s
        self.version = 1
        self.generation = 1
        self.frame_format = "JPEG"

    def run(self, client_hello: dict) -> None:
        requested = client_hello.get("protocol_version")
        max_version = self._server.hello.get("max_protocol_version", 1)
        if isinstance(max_version, bool) or not isinstance(max_version, int):
            max_version = 1
        if not isinstance(requested, int) or not 1 <= requested <= max(1, max_version):
            self._client.sendall(encode_message(MSG_ERROR, b"Protocol version mismatch"))
            return
        self.version = requested
        self._send_controls()
        if self.version >= 2:
            self._send_stream_info("streaming")
        self._stream_loop()

    # -- streaming ------------------------------------------------------------

    def _stream_loop(self) -> None:
        index = 0
        next_frame = time.monotonic()
        while not self._server.stopping:
            now = time.monotonic()
            if now >= next_frame:
                self._send_frame(index)
                index += 1
                next_frame = time.monotonic() + self._camera.frame_interval_us / 1e6
            wait = max(0.0, min(next_frame - time.monotonic(), 0.05))
            readable, _, _ = select.select([self._client], [], [], wait)
            if readable:
                restarted = self._handle_incoming()
                if restarted:
                    next_frame = time.monotonic()  # the first frame of the new stream comes at once
                else:
                    next_frame = min(next_frame, time.monotonic() + self._camera.frame_interval_us / 1e6)

    def _send_frame(self, index: int) -> None:
        binning = self._camera.binning if self.version >= 2 else 1
        try:
            if self.frame_format == "GRAY8":
                width, height = self._frames.size_at(binning)
                header = struct.pack("<IHHIIQ", self.generation, width, height, width, 1, int(time.monotonic() * 1e6))
                self._client.sendall(encode_message(MSG_FRAME_RAW, header + self._frames.gray8(index, binning)))
            else:
                self._client.sendall(encode_message(MSG_FRAME, self._frames.jpeg(index, binning)))
        except OSError as exc:
            raise DisconnectError("Send failed") from exc

    def _handle_incoming(self) -> bool:
        """One app -> camera message. True if it restarted the stream."""
        try:
            msg_type, payload = read_message(self._client)
        except socket.timeout:
            return False
        if msg_type == MSG_SET_CONTROL:
            return self._on_set_control(payload)
        if msg_type == MSG_GET_CONTROLS:
            self._send_controls()
        elif msg_type == MSG_SET_STREAM_CONFIG and self.version >= 2:
            return self._on_set_stream_config(payload)
        else:
            logger.debug("Mock camera v2: ignoring message type 0x%02x", msg_type)
        return False

    def _on_set_control(self, payload: bytes) -> bool:
        try:
            cmd = json.loads(payload.decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as exc:
            logger.error("Bad SET_CONTROL payload: %s", exc)
            return False
        old_binning = self._camera.binning
        restarts, interval_changed = self._camera.apply(cmd.get("id"), cmd.get("value"), self.version)
        self._server.record_set_control(cmd)
        self._send_controls()
        if self.version < 2:
            return False
        if restarts:
            self._reconfigure(old_binning=old_binning)
            return True
        if interval_changed:
            self._send_stream_info("streaming")
        return False

    def _on_set_stream_config(self, payload: bytes) -> bool:
        try:
            cmd = json.loads(payload.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return False
        self._server.record_stream_config(cmd)
        wanted = cmd.get("frame_format")
        advertised = self._server.hello.get("capabilities", {}).get("frame_formats", ["JPEG"])
        if wanted not in advertised:
            return False  # ignored, as the protocol says
        if wanted == self.frame_format:
            self._send_stream_info("streaming")
            return False
        self._reconfigure(frame_format=wanted)
        return True

    def _reconfigure(self, frame_format: str | None = None, old_binning: int | None = None) -> None:
        """reconfiguring (same generation, still the old size), restart in-process, then
        streaming with generation + 1 and the new size."""
        self._send_stream_info("reconfiguring", binning=old_binning)
        time.sleep(self._reconfigure_s)
        self.generation += 1
        if frame_format is not None:
            self.frame_format = frame_format
        self._send_stream_info("streaming")

    # -- messages -------------------------------------------------------------

    def _send_controls(self) -> None:
        controls = self._camera.descriptors() if self.version >= 2 else self._camera.descriptors_v1()
        self._client.sendall(encode_json_message(MSG_CONTROL_INFO, {"controls": copy.deepcopy(controls)}))

    def _send_stream_info(self, state: str, binning: int | None = None) -> None:
        binning = binning or self._camera.binning
        width, height = self._frames.size_at(binning)
        info = {
            "generation": self.generation,
            "state": state,
            "width": width,
            "height": height,
            "binning": binning,
            "frame_format": self.frame_format,
            "frame_interval_us": self._camera.frame_interval_us,
            "exposure_us": self._camera.exposure_us,
        }
        self._client.sendall(encode_json_message(MSG_STREAM_INFO, info))
        self._server.record_stream_info(info)
