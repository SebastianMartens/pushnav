# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 3.2, 3.3 and 5.1).
"""Stream geometry: the size of the frames the camera sends, and how alive the stream is.

v1 sessions (spec 5.1): the size from HELLO ``default_width`` / ``default_height``,
corrected by the size of the first decoded frame.

v2 sessions (slice S3, ``SPEC_PROTOCOL_CAMERA.md`` section 11): STREAM_INFO
announces size, binning and a ``generation`` that grows on every stream
reconfiguration. This module parses STREAM_INFO and FRAME_RAW, decides the
stall timeout (spec 3.3) and lets the geometry follow the client's latest
STREAM_INFO. It has no socket code; ``CameraClient`` calls into it.
"""

from __future__ import annotations

import io
import json
import logging
import math
import struct
import threading
from dataclasses import dataclass
from typing import Callable

from PIL import Image

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class StreamGeometry:
    """Size in pixels of the frames currently arriving.

    ``binning`` and ``generation`` come from STREAM_INFO; a v1 session has the
    defaults (1x1, generation 0).
    """

    width: int
    height: int
    binning: int = 1
    generation: int = 0


# What the app assumed before geometry was dynamic, and what it still shows
# while no camera is connected (OV9281 at 1280x720).
DEFAULT_GEOMETRY = StreamGeometry(1280, 720)


def geometry_from_hello(hello: object) -> StreamGeometry | None:
    """``default_width`` x ``default_height`` of a camera HELLO, or None if unusable."""
    if not isinstance(hello, dict):
        return None
    w, h = hello.get("default_width"), hello.get("default_height")
    if (isinstance(w, int) and not isinstance(w, bool) and w > 0
            and isinstance(h, int) and not isinstance(h, bool) and h > 0):
        return StreamGeometry(w, h)
    return None


def jpeg_size(jpeg_bytes: bytes) -> StreamGeometry | None:
    """Frame size from the JPEG header (Pillow reads it without decoding the pixels)."""
    try:
        with Image.open(io.BytesIO(jpeg_bytes)) as img:
            width, height = img.size
    except Exception:  # noqa: BLE001 - a corrupt frame is not an error here
        return None
    return StreamGeometry(width, height) if width > 0 and height > 0 else None


# -- v2: STREAM_INFO (protocol section 11.5) -----------------------------------

STATE_STREAMING = "streaming"
STATE_RECONFIGURING = "reconfiguring"


@dataclass(frozen=True)
class StreamInfo:
    """One STREAM_INFO message: what the camera is streaming right now.

    ``received_at`` (``time.monotonic()``) is when the reconfiguration began for a
    ``"reconfiguring"`` info: stall detection waits for it only for a limited time.
    """

    generation: int
    state: str
    width: int
    height: int
    binning: int = 1
    frame_format: str = "JPEG"
    frame_interval_us: int | None = None
    exposure_us: int | None = None
    received_at: float = 0.0

    @property
    def geometry(self) -> StreamGeometry:
        return StreamGeometry(self.width, self.height, self.binning, self.generation)

    @property
    def reconfiguring(self) -> bool:
        return self.state == STATE_RECONFIGURING

    def to_dict(self) -> dict:
        """The ``camera.stream`` object for ``/ws`` (spec 6.1)."""
        return {
            "width": self.width, "height": self.height, "binning": self.binning,
            "state": self.state, "frame_interval_us": self.frame_interval_us,
        }


def _positive_int(value: object) -> int | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if not math.isfinite(value) or value <= 0 or value != int(value):
        return None
    return int(value)


def parse_stream_info(payload: bytes, previous: StreamInfo | None, now: float) -> StreamInfo | None:
    """Decode a STREAM_INFO payload; None if it is unusable (the caller logs and ignores it).

    ``generation`` and ``state`` are required. A field the message lacks (a
    ``"reconfiguring"`` message may be short) keeps the value of ``previous``;
    without one, ``width`` and ``height`` are required.
    """
    try:
        data = json.loads(payload.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None
    if not isinstance(data, dict):
        return None
    generation, state = data.get("generation"), data.get("state")
    if isinstance(generation, bool) or not isinstance(generation, int) or generation < 0:
        return None
    if not isinstance(state, str) or not state:
        return None

    def pick(name: str, fallback):
        value = _positive_int(data.get(name))
        return value if value is not None else fallback

    width = pick("width", previous.width if previous else None)
    height = pick("height", previous.height if previous else None)
    if width is None or height is None:
        return None
    interval = data.get("frame_interval_us")
    interval = _positive_int(interval) if "frame_interval_us" in data else (
        previous.frame_interval_us if previous else None)
    frame_format = data.get("frame_format")
    if not isinstance(frame_format, str) or not frame_format:
        frame_format = previous.frame_format if previous else "JPEG"
    exposure = _positive_int(data.get("exposure_us"))
    received_at = now
    if (previous is not None and previous.reconfiguring and state == STATE_RECONFIGURING
            and previous.generation == generation):
        received_at = previous.received_at  # the same reconfiguration, not a new one
    return StreamInfo(
        generation=generation,
        state=state,
        width=width,
        height=height,
        binning=pick("binning", previous.binning if previous else 1),
        frame_format=frame_format,
        frame_interval_us=interval,
        exposure_us=exposure,
        received_at=received_at,
    )


# -- v2: FRAME_RAW (protocol section 11.6) ---------------------------------------

_FRAME_RAW_HEADER = struct.Struct("<IHHIIQ")  # generation, width, height, stride, pixel_format, timestamp_us
FRAME_RAW_HEADER_SIZE = _FRAME_RAW_HEADER.size  # 24
PIXEL_FORMAT_GRAY8 = 1


@dataclass(frozen=True)
class RawFrameHeader:
    generation: int
    width: int
    height: int
    stride: int
    pixel_format: int
    timestamp_us: int


def parse_frame_raw(payload: bytes) -> RawFrameHeader | None:
    """Header of a FRAME_RAW message, None if it is malformed.

    The engine never requests GRAY8 yet (spec section 7), so the pixel rows are
    not copied or decoded: this only proves the message is well-formed.
    """
    if len(payload) < FRAME_RAW_HEADER_SIZE:
        return None
    generation, width, height, stride, pixel_format, timestamp_us = _FRAME_RAW_HEADER.unpack_from(payload)
    if width <= 0 or height <= 0 or stride < width:
        return None
    if len(payload) < FRAME_RAW_HEADER_SIZE + stride * height:
        return None
    return RawFrameHeader(generation, width, height, stride, pixel_format, timestamp_us)


# -- v2: liveness (spec 3.3, protocol section 11.7) ------------------------------

RECONFIGURE_GRACE_S = 5.0  # stall detection is suspended for at most this long while "reconfiguring"


def stall_timeout_s(floor_s: float, info: StreamInfo | None) -> float:
    """v2 frame-stall timeout: ``max(floor, 2 * frame_interval + 1.0 s)`` from the latest STREAM_INFO.

    ``floor_s`` is the v1 timeout (2.0 s), which stays the minimum. A 4 s
    exposure therefore gets 9 s instead of a restart every 2 s.
    """
    if info is None or info.frame_interval_us is None:
        return floor_s
    return max(floor_s, 2.0 * info.frame_interval_us / 1e6 + 1.0)


def v2_stall(now: float, last_activity: float, floor_s: float, info: StreamInfo | None) -> tuple[float, float]:
    """``(seconds without a sign of life, timeout)`` of a v2 session.

    Any received message counts as a sign of life, not only FRAME. While the
    latest STREAM_INFO says ``"reconfiguring"`` the stall is reported as 0 for
    up to ``RECONFIGURE_GRACE_S`` after it arrived.
    """
    timeout = stall_timeout_s(floor_s, info)
    if info is not None and info.reconfiguring and now - info.received_at <= RECONFIGURE_GRACE_S:
        return 0.0, timeout
    return now - last_activity, timeout


class GeometryTracker:
    """Engine-owned, lock-protected current stream geometry.

    v1 session: ``connect()`` takes the size from HELLO; the first frame decoded
    after it (``observe_frame``) corrects it, logging when the two differ.

    v2 session: ``connect(hello, stream_source)`` follows the client's latest
    STREAM_INFO (``stream_source()`` returns it, None before the first one), so
    the geometry changes the moment a binning change is announced. The HELLO
    size is only the fallback.

    ``reset()`` goes back to the no-camera default.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._geometry = DEFAULT_GEOMETRY
        self._awaiting_frame = False
        self._stream_source: Callable[[], StreamInfo | None] | None = None

    def get(self) -> StreamGeometry:
        with self._lock:
            geometry, source = self._geometry, self._stream_source
        info = source() if source is not None else None
        return info.geometry if info is not None else geometry

    def expected_frame_size(self) -> tuple[int, int] | None:
        """``(height, width)`` every frame must have, or None when it is not certain.

        Only STREAM_INFO is authoritative. A v1 server's HELLO size can be wrong
        until the first frame corrects it, so a v1 session has no expectation
        (and the solver thread never drops a result for its size).
        """
        with self._lock:
            source = self._stream_source
        info = source() if source is not None else None
        return (info.height, info.width) if info is not None else None

    @property
    def awaiting_frame(self) -> bool:
        """True from ``connect()`` until the first frame has been observed (v1 only)."""
        with self._lock:
            return self._awaiting_frame

    def connect(
        self,
        hello: object,
        stream_source: Callable[[], StreamInfo | None] | None = None,
    ) -> StreamGeometry:
        """A camera connected: take the HELLO size (the default if HELLO has none).

        With ``stream_source`` (a v2 session) STREAM_INFO takes over from the first message.
        """
        geometry = geometry_from_hello(hello)
        with self._lock:
            self._geometry = geometry or DEFAULT_GEOMETRY
            self._stream_source = stream_source
            self._awaiting_frame = stream_source is None
        if stream_source is not None:
            current = self.get()
            logger.info("Stream geometry from STREAM_INFO: %dx%d (binning %d, generation %d)",
                        current.width, current.height, current.binning, current.generation)
            return current
        with self._lock:
            result = self._geometry
        if geometry is None:
            logger.info("Camera HELLO has no usable frame size; assuming %dx%d until the first frame",
                        result.width, result.height)
        else:
            logger.info("Stream geometry from HELLO: %dx%d", result.width, result.height)
        return result

    def observe_frame(self, frame: StreamGeometry) -> StreamGeometry:
        """The first decoded frame after ``connect()``: it is the truth."""
        with self._lock:
            if not self._awaiting_frame:
                return self._geometry
            self._awaiting_frame = False
            previous, self._geometry = self._geometry, frame
        if previous != frame:
            logger.warning("First frame is %dx%d but HELLO said %dx%d; using the frame size",
                           frame.width, frame.height, previous.width, previous.height)
        else:
            logger.info("First frame confirms the stream geometry %dx%d", frame.width, frame.height)
        return frame

    def reset(self) -> None:
        """No camera: back to the default size."""
        with self._lock:
            self._geometry = DEFAULT_GEOMETRY
            self._awaiting_frame = False
            self._stream_source = None
