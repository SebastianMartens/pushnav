# Copyright (C) 2026 Arun Venkataswamy
#
# This file is part of PushNav.
#
# PushNav is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PushNav is distributed in the hope that it will be useful, but
# WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
# General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PushNav. If not, see <https://www.gnu.org/licenses/>.

"""Camera TCP client — connects to the camera server subprocess.

Per SPEC_PROTOCOL_CAMERA.md §4–5 and SPEC_ARCHITECTURE.md §4.1/§7.
"""

import json
import logging
import socket
import threading
import time

from evf.camera.protocol import (
    MSG_CONTROL_INFO,
    MSG_ERROR,
    MSG_FRAME,
    MSG_FRAME_RAW,  # fork: camera-controls
    MSG_GET_CONTROLS,
    MSG_HELLO,
    MSG_SET_CONTROL,
    MSG_STREAM_INFO,  # fork: camera-controls
    DisconnectError,
    encode_json_message,
    encode_message,
    read_message,
    type_name,
)
from evf.camera import stream  # fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
from evf.config.profiles import camera_key_from_hello  # fork: camera-controls
from evf.engine.frame_buffer import LatestFrame

logger = logging.getLogger(__name__)

# fork: camera-controls (spec 3.1): v2 is negotiated on top of v1. The server HELLO
# keeps ``protocol_version: 1`` and adds ``max_protocol_version``; the session runs
# at the lower of that and MAX_PROTOCOL_VERSION.
MIN_PROTOCOL_VERSION = 1
MAX_PROTOCOL_VERSION = 2
_DEFAULT_HOST = "127.0.0.1"
_DEFAULT_PORT = 8764


class ProtocolError(Exception):
    """Raised on handshake or protocol version mismatch."""


class CameraClient:
    """TCP client that speaks the camera binary protocol.

    Connects, performs the HELLO handshake, receives CONTROL_INFO,
    then continuously receives FRAME messages into a LatestFrame buffer.
    """

    def __init__(
        self,
        frame_buffer: LatestFrame,
        host: str = _DEFAULT_HOST,
        port: int = _DEFAULT_PORT,
    ) -> None:
        self._frame_buffer = frame_buffer
        self._host = host
        self._port = port
        self._sock: socket.socket | None = None
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._frame_id = 0
        self._controls: list[dict] = []
        self._controls_lock = threading.Lock()
        self._hello_info: dict = {}
        self._on_controls_cb: callable | None = None
        self._on_error_cb: callable | None = None
        self._last_frame_time: float = 0.0
        self._connected = False
        # fork: camera-controls (spec 3.1, 3.2, 3.3)
        self._session_version = MIN_PROTOCOL_VERSION
        self._stream_lock = threading.Lock()
        self._stream_info: stream.StreamInfo | None = None
        self._on_stream_info_cb: callable | None = None
        self._last_message_time: float = 0.0
        self._frame_raw_logged = False

    # -- public API -----------------------------------------------------------

    def connect(self, timeout: float = 5.0) -> dict:
        """Connect and perform handshake. Returns the camera HELLO info dict.

        Raises:
            ProtocolError: On version mismatch or handshake failure.
            ConnectionRefusedError: If the server is not running.
            DisconnectError: If the connection drops during handshake.
        """
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._sock.settimeout(timeout)
        self._sock.connect((self._host, self._port))
        logger.info("Connected to camera server at %s:%d", self._host, self._port)

        # Receive camera HELLO
        msg_type, payload = read_message(self._sock)
        if msg_type != MSG_HELLO:
            raise ProtocolError(f"Expected HELLO, got {type_name(msg_type)}")
        self._hello_info = json.loads(payload.decode("utf-8"))
        logger.info("Camera HELLO: %s", self._hello_info)

        # Validate protocol version
        cam_version = self._hello_info.get("protocol_version")
        if cam_version != MIN_PROTOCOL_VERSION:
            raise ProtocolError(
                f"Protocol version mismatch: camera={cam_version}, expected={MIN_PROTOCOL_VERSION}"
            )
        self._session_version = _negotiate_version(self._hello_info)  # fork: camera-controls

        # Send our HELLO response
        our_hello = {"protocol_version": self._session_version, "app": "evf", "app_version": "0.2.1"}
        self._sock.sendall(encode_json_message(MSG_HELLO, our_hello))
        logger.debug("Sent HELLO response")

        # Receive CONTROL_INFO
        msg_type, payload = read_message(self._sock)
        if msg_type != MSG_CONTROL_INFO:
            raise ProtocolError(f"Expected CONTROL_INFO, got {type_name(msg_type)}")
        self._update_controls(payload)
        if self._session_version >= 2:
            self._receive_first_stream_info()  # fork: camera-controls

        self._connected = True
        return self._hello_info

    def start_receiving(self) -> None:
        """Start the background frame-receiving thread."""
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._recv_loop, name="camera-recv", daemon=True)
        self._thread.start()

    def stop(self, timeout: float = 2.0) -> None:
        """Stop receiving and close the connection."""
        self._stop_event.set()
        self._connected = False
        if self._thread is not None:
            self._thread.join(timeout=timeout)
            self._thread = None
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None
        logger.info("Camera client stopped")

    def set_control(self, control_id: str, value: int | float) -> bool:
        """Send a SET_CONTROL message (v2 controls can be float). True if it was sent."""
        if self._sock is None:
            return False
        msg = encode_json_message(MSG_SET_CONTROL, {"id": control_id, "value": value})
        try:
            self._sock.sendall(msg)
        except OSError as exc:
            logger.error("Failed to send SET_CONTROL: %s", exc)
            return False
        return True

    def update_cached_control(self, control_id: str, value: int | float) -> None:
        """Optimistically update the cached current value for a control.

        Avoids UI displaying stale values before the server's CONTROL_INFO
        reply arrives.
        """
        with self._controls_lock:
            for ctrl in self._controls:
                if ctrl.get("id") == control_id:
                    ctrl["cur"] = value
                    break

    def get_controls(self) -> None:
        """Send a GET_CONTROLS request."""
        if self._sock is None:
            return
        try:
            self._sock.sendall(encode_message(MSG_GET_CONTROLS))
            logger.debug("Sent GET_CONTROLS")
        except OSError as exc:
            logger.error("Failed to send GET_CONTROLS: %s", exc)

    @property
    def controls(self) -> list[dict]:
        with self._controls_lock:
            return list(self._controls)

    @property
    def connected(self) -> bool:
        return self._connected

    @property
    def last_frame_time(self) -> float:
        return self._last_frame_time

    def on_controls_update(self, callback: callable) -> None:
        """Register a callback for CONTROL_INFO updates."""
        self._on_controls_cb = callback

    def on_error(self, callback: callable) -> None:
        """Register a callback for ERROR messages."""
        self._on_error_cb = callback

    # -- fork: camera-controls (spec 3.1 to 3.3) -------------------------------

    @property
    def session_version(self) -> int:
        """Protocol version of this session (1 or 2), as negotiated in ``connect()``."""
        return self._session_version

    @property
    def camera_key(self) -> str | None:
        """Profile key of the camera: HELLO ``camera_key``, else ``"<backend>:<camera_model>"``."""
        return camera_key_from_hello(self._hello_info)

    @property
    def capabilities(self) -> dict:
        """HELLO ``capabilities`` (empty for servers that send none)."""
        caps = self._hello_info.get("capabilities")
        return caps if isinstance(caps, dict) else {}

    @property
    def stream_info(self) -> "stream.StreamInfo | None":
        """The latest STREAM_INFO (v2 sessions; None in a v1 session)."""
        with self._stream_lock:
            return self._stream_info

    @property
    def stream_geometry(self) -> "stream.StreamGeometry | None":
        info = self.stream_info
        return info.geometry if info is not None else None

    @property
    def last_activity_time(self) -> float:
        """``time.monotonic()`` of the last message of any type (0.0 before the first)."""
        return self._last_message_time

    def on_stream_info(self, callback: callable) -> None:
        """Register ``callback(info, new_generation)`` for STREAM_INFO messages (receiver thread)."""
        self._on_stream_info_cb = callback

    # -- internal -------------------------------------------------------------

    def _recv_loop(self) -> None:
        assert self._sock is not None
        try:
            while not self._stop_event.is_set():
                try:
                    msg_type, payload = read_message(self._sock)
                except socket.timeout:
                    continue
                self._handle_message(msg_type, payload)
        except (DisconnectError, ConnectionResetError, BrokenPipeError, OSError) as exc:
            if not self._stop_event.is_set():
                logger.warning("Camera connection lost: %s", exc)
                self._connected = False

    def _handle_message(self, msg_type: int, payload: bytes) -> None:
        self._last_message_time = time.monotonic()  # fork: camera-controls: any message is a sign of life
        if self._session_version >= 2 and msg_type in (MSG_STREAM_INFO, MSG_FRAME_RAW):
            self._handle_v2_message(msg_type, payload)  # fork: camera-controls
        elif msg_type == MSG_FRAME:
            self._frame_id += 1
            self._last_frame_time = time.monotonic()
            self._frame_buffer.set(payload, self._last_frame_time, self._frame_id)
        elif msg_type == MSG_CONTROL_INFO:
            self._update_controls(payload)
        elif msg_type == MSG_ERROR:
            error_msg = payload.decode("utf-8", errors="replace")
            logger.error("Camera error: %s", error_msg)
            if self._on_error_cb:
                self._on_error_cb(error_msg)
        else:
            logger.debug("Ignoring unknown message type: %s", type_name(msg_type))

    def _update_controls(self, payload: bytes) -> None:
        try:
            data = json.loads(payload.decode("utf-8"))
            with self._controls_lock:
                self._controls = data.get("controls", [])
            logger.debug("CONTROL_INFO: %s", self._controls)
            if self._on_controls_cb:
                self._on_controls_cb(self._controls)
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            logger.error("Bad CONTROL_INFO payload: %s", exc)

    # -- fork: camera-controls (protocol v2, SPEC_PROTOCOL_CAMERA.md section 11) ----

    def _receive_first_stream_info(self) -> None:
        """v2 handshake: STREAM_INFO follows CONTROL_INFO (generation 1)."""
        assert self._sock is not None
        msg_type, payload = read_message(self._sock)
        self._last_message_time = time.monotonic()
        if msg_type == MSG_ERROR:
            raise ProtocolError(f"Camera error during handshake: {payload.decode('utf-8', errors='replace')}")
        if msg_type != MSG_STREAM_INFO:
            raise ProtocolError(f"Expected STREAM_INFO, got {type_name(msg_type)}")
        if self._store_stream_info(payload) is None:
            raise ProtocolError("Bad STREAM_INFO payload in handshake")

    def _handle_v2_message(self, msg_type: int, payload: bytes) -> None:
        if msg_type == MSG_STREAM_INFO:
            stored = self._store_stream_info(payload)
            if stored is None:
                return
            info, new_generation = stored
            if new_generation:
                # Frames of the old size must not be solved after the switch (spec 3.2).
                self._frame_buffer.clear()
            logger.info("STREAM_INFO: %s %dx%d binning %d, generation %d, interval %s us",
                        info.state, info.width, info.height, info.binning, info.generation,
                        info.frame_interval_us)
            if self._on_stream_info_cb:
                try:
                    self._on_stream_info_cb(info, new_generation)
                except Exception as exc:  # noqa: BLE001 - a callback must not end the receiver
                    logger.error("on_stream_info callback failed: %s", exc)
        else:  # FRAME_RAW: parsed and ignored, the engine never requests GRAY8 yet (spec 3.2, 7)
            header = stream.parse_frame_raw(payload)
            if header is None:
                logger.warning("Ignoring a malformed FRAME_RAW (%d bytes)", len(payload))
            elif not self._frame_raw_logged:
                self._frame_raw_logged = True
                logger.debug("Ignoring FRAME_RAW (generation %d, %dx%d); the engine does not use GRAY8",
                             header.generation, header.width, header.height)

    def _store_stream_info(self, payload: bytes):
        """Parse and keep a STREAM_INFO. Returns ``(info, new_generation)`` or None if unusable."""
        with self._stream_lock:
            previous = self._stream_info
            info = stream.parse_stream_info(payload, previous, time.monotonic())
            if info is None:
                logger.error("Bad STREAM_INFO payload: %r", payload[:200])
                return None
            self._stream_info = info
        return info, previous is not None and info.generation != previous.generation


def _negotiate_version(hello: dict) -> int:
    """Session version = min(MAX_PROTOCOL_VERSION, HELLO ``max_protocol_version`` or 1)."""
    offered = hello.get("max_protocol_version", MIN_PROTOCOL_VERSION)
    if isinstance(offered, bool) or not isinstance(offered, int):
        offered = MIN_PROTOCOL_VERSION
    return max(MIN_PROTOCOL_VERSION, min(MAX_PROTOCOL_VERSION, offered))
