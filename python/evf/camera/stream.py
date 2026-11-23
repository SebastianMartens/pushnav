# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.1).
"""Stream geometry: the size of the frames the camera sends.

Slice S1 only needs width and height for v1 sessions (spec 5.1): the size
from HELLO ``default_width`` / ``default_height``, corrected by the size of
the first decoded frame. STREAM_INFO, binning and generations (protocol v2)
extend this module in slice S3.
"""

from __future__ import annotations

import io
import logging
import threading
from dataclasses import dataclass

from PIL import Image

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class StreamGeometry:
    """Size in pixels of the frames currently arriving."""

    width: int
    height: int


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


class GeometryTracker:
    """Engine-owned, lock-protected current stream geometry (v1 sessions).

    ``connect()`` takes the size from HELLO; the first frame decoded after it
    (``observe_frame``) corrects it, logging when the two differ. ``reset()``
    goes back to the no-camera default.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._geometry = DEFAULT_GEOMETRY
        self._awaiting_frame = False

    def get(self) -> StreamGeometry:
        with self._lock:
            return self._geometry

    @property
    def awaiting_frame(self) -> bool:
        """True from ``connect()`` until the first frame has been observed."""
        with self._lock:
            return self._awaiting_frame

    def connect(self, hello: object) -> StreamGeometry:
        """A camera connected: take the HELLO size (the default if HELLO has none)."""
        geometry = geometry_from_hello(hello)
        with self._lock:
            self._geometry = geometry or DEFAULT_GEOMETRY
            self._awaiting_frame = True
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
