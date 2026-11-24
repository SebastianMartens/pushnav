# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 9.2).

"""Camera protocol conformance suite (v1 vectors of SPEC_PROTOCOL_CAMERA.md section 8,
v2 vectors of section 11.8).

The same tests run against every camera server we can start:

* ``mock``: ``tests/mock_camera_server.py`` (always).
* ``exe``: the C# camera server (``camera_server_dotnet.exe``), started with
  ``--backend fake``, only when ``PUSHNAV_CONFORMANCE_SERVER=<path to the exe>`` is
  set. Without it that parametrization does not exist (nothing is skipped, so
  Linux/macOS runs stay green and quiet).

The v2 vectors (``TestV2Vectors``) run against the Python mock in v2 mode
(``MockCameraServer(v2=True)``). The C# exe joins them when it speaks v2
(``SPEC_CAMERA_CONTROLS.md`` slice S5); until then it is a v1 server and the
v1 vectors above are all it has to pass.

Tests in ``TestCSharpServerBehavior`` cover things only the C# server promises (exit
after disconnect, ERROR on a version mismatch, real stream size in HELLO, undrained
stderr pipes, ...). They need ``PUSHNAV_CONFORMANCE_SERVER`` and are *skipped* without
it, so a plain run only exercises the mock.

Run against the published exe::

    $env:PUSHNAV_CONFORMANCE_SERVER = "camera\\windows-dotnet\\publish\\camera_server_dotnet.exe"
    uv run pytest tests/test_camera_conformance.py -q

Manual hardware run (an IDS camera attached, spec task 5.4): the same suite against the real
camera. ``PUSHNAV_CONFORMANCE_BACKEND=ids`` starts the exe with ``--backend ids`` instead of
``--backend fake``; the tests that only make sense for the fake backend (sample-image size,
backend name) are skipped::

    $env:PUSHNAV_CONFORMANCE_SERVER = "camera\\windows-dotnet\\publish\\camera_server_dotnet.exe"
    $env:PUSHNAV_CONFORMANCE_BACKEND = "ids"
    uv run pytest tests/test_camera_conformance.py -q
"""

from __future__ import annotations

import io
import json
import os
import queue
import socket
import struct
import subprocess
import threading
import time
from dataclasses import dataclass
from pathlib import Path

import pytest
from PIL import Image

from evf.camera.client import CameraClient
from evf.camera.protocol import (
    MSG_CONTROL_INFO,
    MSG_ERROR,
    MSG_FRAME,
    MSG_FRAME_RAW,
    MSG_GET_CONTROLS,
    MSG_HELLO,
    MSG_SET_CONTROL,
    MSG_SET_STREAM_CONFIG,
    MSG_STREAM_INFO,
    DisconnectError,
    encode_json_message,
    encode_message,
    read_message,
)
from evf.engine.frame_buffer import LatestFrame
from tests.mock_camera_server import MockCameraServer

_SERVER_ENV = "PUSHNAV_CONFORMANCE_SERVER"
_BACKEND_ENV = "PUSHNAV_CONFORMANCE_BACKEND"
_SAMPLES = Path(__file__).parent / "samples"
_TIMEOUT = 10.0


def _exe_path() -> Path | None:
    value = os.environ.get(_SERVER_ENV, "").strip()
    return Path(value) if value else None


def _exe_backend() -> str:
    """Backend the exe is started with: ``fake`` (default, CI) or ``ids`` (real camera, manual)."""
    value = os.environ.get(_BACKEND_ENV, "fake").strip().lower()
    return value or "fake"


def _exe_args(port: int) -> list[str]:
    backend = _exe_backend()
    args = ["--backend", backend, "--port", str(port)]
    if backend == "fake":
        args += ["--fake-images", str(_SAMPLES)]
    return args


_SERVER_KINDS = ["mock"] + (["exe"] if _exe_path() else [])


@dataclass
class ServerHandle:
    kind: str  # "mock" | "exe"
    port: int
    process: subprocess.Popen | None = None

    def connect(self) -> socket.socket:
        sock = socket.create_connection(("127.0.0.1", self.port), timeout=_TIMEOUT)
        sock.settimeout(_TIMEOUT)
        return sock


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _start_exe(exe: Path) -> ServerHandle:
    if not exe.is_file():
        pytest.fail(f"{_SERVER_ENV} points at a missing file: {exe}")
    port = _free_port()
    proc = subprocess.Popen(
        [str(exe), *_exe_args(port)],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    lines: queue.Queue[str] = queue.Queue()

    # Drain both pipes for the whole life of the process (the engine does not, which is
    # why the server keeps its logging quiet; here a full pipe would only hide a bug).
    def pump(stream, tag):
        for raw in iter(stream.readline, b""):
            lines.put(f"{tag}: {raw.decode(errors='replace').rstrip()}")

    for stream, tag in ((proc.stdout, "out"), (proc.stderr, "err")):
        threading.Thread(target=pump, args=(stream, tag), daemon=True).start()

    seen: list[str] = []
    deadline = time.monotonic() + 20.0
    while time.monotonic() < deadline:
        try:
            line = lines.get(timeout=0.2)
        except queue.Empty:
            if proc.poll() is not None:
                break
            continue
        seen.append(line)
        if line == f"out: Listening on 127.0.0.1:{port}":
            return ServerHandle("exe", port, proc)
    proc.kill()
    pytest.fail(f"camera server did not start listening on {port}; output so far: {seen}")


def _stop_exe(handle: ServerHandle) -> None:
    proc = handle.process
    # The server exits by itself once its client is gone. Give it that moment before killing it: a real
    # camera must be closed cleanly, and a hard kill in the middle of the driver's teardown can take
    # a long time to complete.
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        proc.kill()
    proc.wait(timeout=30)


@pytest.fixture(params=_SERVER_KINDS)
def server(request):
    if request.param == "mock":
        mock = MockCameraServer(port=0, fps=20)
        mock.start()
        try:
            yield ServerHandle("mock", mock.port)
        finally:
            mock.stop()
    else:
        handle = _start_exe(_exe_path())
        try:
            yield handle
        finally:
            _stop_exe(handle)


@pytest.fixture(params=_SERVER_KINDS)
def v2_server(request):
    """A server that offers protocol v2: the mock in v2 mode, and the C# exe (it always offers v2)."""
    if request.param == "mock":
        mock = MockCameraServer(port=0, fps=20, v2=True)
        mock.start()
        try:
            yield ServerHandle("mock", mock.port)
        finally:
            mock.stop()
    else:
        handle = _start_exe(_exe_path())
        try:
            yield handle
        finally:
            _stop_exe(handle)


@pytest.fixture
def v1_only_server():
    """A server that speaks only v1 (the mock without v2 mode); the C# exe offers v2, so it is not one."""
    mock = MockCameraServer(port=0, fps=20)
    mock.start()
    try:
        yield ServerHandle("mock", mock.port)
    finally:
        mock.stop()


@pytest.fixture
def exe_server():
    """The C# server only: for behaviour the mock does not have (skipped without PUSHNAV_CONFORMANCE_SERVER)."""
    exe = _exe_path()
    if exe is None:
        pytest.skip(f"needs {_SERVER_ENV} (C# camera server behaviour)")
    handle = _start_exe(exe)
    try:
        yield handle
    finally:
        _stop_exe(handle)


@pytest.fixture
def fake_exe_server(exe_server):
    """The C# server on the fake backend: for tests that know the sample images (skipped for a real camera)."""
    if _exe_backend() != "fake":
        pytest.skip(f"needs {_BACKEND_ENV}=fake (knows the fake backend's sample images)")
    return exe_server


# -- helpers ----------------------------------------------------------------


def _handshake(sock, version=1):
    """HELLO exchange as the Python client does it; returns (hello, control_info)."""
    msg_type, payload = read_message(sock)
    assert msg_type == MSG_HELLO, "camera must speak first with HELLO"
    hello = json.loads(payload.decode("utf-8"))
    sock.sendall(encode_json_message(MSG_HELLO, {"protocol_version": version, "app": "conformance", "app_version": "0"}))
    msg_type, payload = read_message(sock)
    assert msg_type == MSG_CONTROL_INFO, "CONTROL_INFO must follow the handshake"
    return hello, json.loads(payload.decode("utf-8"))


def _read_until(sock, wanted, limit=500):
    """Read messages until one of type ``wanted``; returns (payload, other message types seen)."""
    others = []
    for _ in range(limit):
        msg_type, payload = read_message(sock)
        if msg_type == wanted:
            return payload, others
        others.append(msg_type)
    raise AssertionError(f"no message of type {wanted} within {limit} messages")


def _controls_by_id(control_info):
    return {c["id"]: c for c in control_info["controls"]}


def _is_jpeg(data: bytes) -> bool:
    return data[:2] == b"\xff\xd8" and data[-2:] == b"\xff\xd9"


# -- SPEC_PROTOCOL_CAMERA.md section 8 test vectors -------------------------


class TestV1Vectors:
    def test_receive_hello(self, server):
        with server.connect() as sock:
            msg_type, payload = read_message(sock)
            assert msg_type == MSG_HELLO
            hello = json.loads(payload.decode("utf-8"))
        assert hello["protocol_version"] == 1
        for key in ("backend", "backend_version", "camera_model", "stream_format"):
            assert isinstance(hello[key], str) and hello[key], key
        for key in ("default_width", "default_height", "default_fps"):
            assert isinstance(hello[key], int) and hello[key] > 0, key

    def test_control_info_follows_the_client_hello(self, server):
        with server.connect() as sock:
            _, info = _handshake(sock)
        controls = _controls_by_id(info)
        assert {"exposure", "gain"} <= set(controls)
        for control in (controls["exposure"], controls["gain"]):
            for key in ("id", "label", "type", "min", "max", "step", "cur", "unit"):
                assert key in control, f"{control['id']} lacks {key}"
            assert control["type"] == "int"
            assert control["min"] <= control["cur"] <= control["max"]
            assert control["step"] >= 1

    def test_frames_arrive_continuously(self, server):
        with server.connect() as sock:
            _handshake(sock)
            frames = []
            for _ in range(5):
                payload, _others = _read_until(sock, MSG_FRAME)
                frames.append(payload)
        assert all(len(f) > 100 for f in frames)
        if server.kind == "exe":
            assert all(_is_jpeg(f) for f in frames), "the C# server must send JPEG frames"

    def test_set_control_is_answered_with_updated_control_info(self, server):
        with server.connect() as sock:
            _, info = _handshake(sock)
            for control_id in ("gain", "exposure"):
                c = _controls_by_id(info)[control_id]
                target = c["min"] + (c["max"] - c["min"]) // 3
                if target == c["cur"]:
                    target += c["step"]
                sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": control_id, "value": target}))
                payload, _ = _read_until(sock, MSG_CONTROL_INFO)
                info = json.loads(payload.decode("utf-8"))
                applied = _controls_by_id(info)[control_id]["cur"]
                # The server may round to its step but must report what it applied, close to the request.
                assert abs(applied - target) <= max(c["step"], 1), f"{control_id}: asked {target}, got {applied}"

    def test_set_control_clamps_to_the_valid_range(self, server):
        with server.connect() as sock:
            _, info = _handshake(sock)
            gain = _controls_by_id(info)["gain"]
            sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": "gain", "value": gain["max"] + 100000}))
            payload, _ = _read_until(sock, MSG_CONTROL_INFO)
            assert _controls_by_id(json.loads(payload))["gain"]["cur"] == gain["max"]
            sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": "gain", "value": gain["min"] - 100000}))
            payload, _ = _read_until(sock, MSG_CONTROL_INFO)
            assert _controls_by_id(json.loads(payload))["gain"]["cur"] == gain["min"]

    def test_get_controls_is_answered_with_control_info(self, server):
        with server.connect() as sock:
            _, info = _handshake(sock)
            sock.sendall(encode_message(MSG_GET_CONTROLS))
            payload, _ = _read_until(sock, MSG_CONTROL_INFO)
        assert _controls_by_id(json.loads(payload)).keys() == _controls_by_id(info).keys()

    def test_unknown_message_types_are_ignored(self, server):
        with server.connect() as sock:
            _handshake(sock)
            sock.sendall(encode_message(0x7F, b"whatever"))  # never assigned
            sock.sendall(encode_message(0x13))                 # reserved START_STREAM
            sock.sendall(encode_message(MSG_GET_CONTROLS))
            _read_until(sock, MSG_CONTROL_INFO)                # still answering ...
            _read_until(sock, MSG_FRAME)                       # ... and still streaming


# -- SPEC_PROTOCOL_CAMERA.md section 11.8 test vectors (protocol v2) ------------


def _handshake_v2(sock):
    """v2 HELLO exchange; returns (hello, control_info, stream_info, messages seen after CONTROL_INFO)."""
    msg_type, payload = read_message(sock)
    assert msg_type == MSG_HELLO
    hello = json.loads(payload.decode("utf-8"))
    assert hello.get("max_protocol_version", 1) >= 2, "this server does not offer v2"
    sock.sendall(encode_json_message(MSG_HELLO, {"protocol_version": 2, "app": "conformance", "app_version": "0"}))
    order = []
    control_info = stream_info = None
    while stream_info is None:
        msg_type, payload = read_message(sock)
        order.append(msg_type)
        if msg_type == MSG_CONTROL_INFO and control_info is None:
            control_info = json.loads(payload.decode("utf-8"))
        elif msg_type == MSG_STREAM_INFO:
            stream_info = json.loads(payload.decode("utf-8"))
        else:
            raise AssertionError(f"unexpected message 0x{msg_type:02x} in the v2 handshake")
    return hello, control_info, stream_info, order


def _next_stream_info(sock, state, limit=500):
    """Read until a STREAM_INFO with ``state``; returns (info, frames seen before it)."""
    frames = []
    for _ in range(limit):
        msg_type, payload = read_message(sock)
        if msg_type == MSG_FRAME:
            frames.append(payload)
        elif msg_type == MSG_STREAM_INFO:
            info = json.loads(payload.decode("utf-8"))
            if info["state"] == state:
                return info, frames
    raise AssertionError(f"no STREAM_INFO {state!r} within {limit} messages")


def _jpeg_size(data: bytes):
    return Image.open(io.BytesIO(data)).size


class TestV2Vectors:
    def test_1_v2_is_negotiated_with_a_v2_server(self, v2_server):
        client = CameraClient(LatestFrame(), port=v2_server.port)
        try:
            hello = client.connect(timeout=_TIMEOUT)
            assert hello["protocol_version"] == 1 and hello["max_protocol_version"] == 2
            assert client.session_version == 2
        finally:
            client.stop()

    def test_1_v1_is_negotiated_with_a_v1_server(self, v1_only_server):
        client = CameraClient(LatestFrame(), port=v1_only_server.port)
        try:
            client.connect(timeout=_TIMEOUT)
            assert client.session_version == 1
            assert client.stream_info is None
        finally:
            client.stop()

    def test_1_a_v1_client_gets_the_v1_view_of_a_v2_server(self, v2_server):
        with v2_server.connect() as sock:
            hello, info = _handshake(sock, version=1)
            assert hello["max_protocol_version"] == 2
            controls = _controls_by_id(info)
            assert set(controls) == {"exposure", "gain"}
            assert controls["exposure"]["unit"] != "us"  # a v1 unit
            msg_types = {read_message(sock)[0] for _ in range(10)}
        assert msg_types <= {MSG_FRAME, MSG_CONTROL_INFO}, "a v1 session must not carry STREAM_INFO"

    def test_1_a_client_version_the_server_does_not_offer_is_refused(self, v2_server):
        with v2_server.connect() as sock:
            msg_type, _ = read_message(sock)
            assert msg_type == MSG_HELLO
            sock.sendall(encode_json_message(MSG_HELLO, {"protocol_version": 3}))
            msg_type, payload = read_message(sock)
            assert msg_type == MSG_ERROR and b"Protocol version mismatch" in payload

    def test_2_control_info_then_stream_info_before_the_first_frame(self, v2_server):
        with v2_server.connect() as sock:
            _hello, controls, info, order = _handshake_v2(sock)
            assert order == [MSG_CONTROL_INFO, MSG_STREAM_INFO]
            msg_type, _ = read_message(sock)
            assert msg_type == MSG_FRAME
        assert info["generation"] == 1 and info["state"] == "streaming"
        assert info["width"] > 0 and info["height"] > 0 and info["frame_interval_us"] > 0
        by_id = _controls_by_id(controls)
        assert {"exposure", "gain"} <= set(by_id)
        assert by_id["exposure"]["unit"] == "us"
        for control in by_id.values():
            assert {"id", "label", "type", "cur"} <= set(control)

    def test_3_exposure_longer_than_the_frame_interval_lowers_fps(self, v2_server):
        with v2_server.connect() as sock:
            _, controls, _info, _ = _handshake_v2(sock)
            by_id = _controls_by_id(controls)
            fps_before = by_id["fps"]["cur"]
            step = by_id["exposure"]["step"]
            # two frame intervals, on the exposure's step grid and inside its range
            exposure = round(2 * 1e6 / fps_before / step) * step
            exposure = max(by_id["exposure"]["min"], min(by_id["exposure"]["max"], exposure))
            assert exposure * 1e-6 > 1 / fps_before, "the exposure must be longer than the frame interval"
            sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": "exposure", "value": exposure}))
            payload, _ = _read_until(sock, MSG_CONTROL_INFO)
        after = _controls_by_id(json.loads(payload))
        assert abs(after["exposure"]["cur"] - exposure) <= step
        assert after["fps"]["cur"] < fps_before  # exposure priority: fps is lowered ...
        assert 1e6 / after["fps"]["cur"] >= after["exposure"]["cur"]  # ... until the interval covers the exposure

    def test_3_the_next_stream_info_carries_the_new_frame_interval(self, v2_server):
        with v2_server.connect() as sock:
            _, _controls, info, _ = _handshake_v2(sock)
            exposure = int(2 * info["frame_interval_us"])
            sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": "exposure", "value": exposure}))
            new_info, _ = _next_stream_info(sock, "streaming")
        assert new_info["generation"] == info["generation"]
        assert new_info["frame_interval_us"] >= exposure

    def test_4_binning_reconfigures_the_stream_before_the_new_size_arrives(self, v2_server):
        with v2_server.connect() as sock:
            _, controls, info, _ = _handshake_v2(sock)
            assert _controls_by_id(controls)["binning"]["restarts_stream"] is True
            sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": "binning", "value": 2}))
            reconfiguring, frames_before = _next_stream_info(sock, "reconfiguring")
            assert reconfiguring["generation"] == info["generation"]
            streaming, frames_during = _next_stream_info(sock, "streaming")
            assert streaming["generation"] == info["generation"] + 1
            assert (streaming["width"], streaming["height"]) == (info["width"] // 2, info["height"] // 2)
            assert streaming["binning"] == 2
            payload, _ = _read_until(sock, MSG_FRAME)
        # no frame of the new size came before STREAM_INFO announced it
        for frame in frames_before + frames_during:
            assert _jpeg_size(frame) == (info["width"], info["height"])
        assert _jpeg_size(payload) == (streaming["width"], streaming["height"])

    def test_5_an_unknown_message_type_keeps_the_stream_going(self, v2_server):
        with v2_server.connect() as sock:
            _handshake_v2(sock)
            sock.sendall(encode_message(0x7F, b"whatever"))
            sock.sendall(encode_message(MSG_GET_CONTROLS))
            _read_until(sock, MSG_CONTROL_INFO)
            _read_until(sock, MSG_FRAME)

    def test_6_gray8_arrives_as_frame_raw_with_a_matching_generation(self, v2_server):
        with v2_server.connect() as sock:
            hello, _, _info, _ = _handshake_v2(sock)
            if "GRAY8" not in hello.get("capabilities", {}).get("frame_formats", []):
                pytest.skip("GRAY8 is not advertised")
            sock.sendall(encode_json_message(MSG_SET_STREAM_CONFIG, {"frame_format": "GRAY8"}))
            streaming, _ = _next_stream_info(sock, "streaming")
            assert streaming["frame_format"] == "GRAY8"
            payload, _ = _read_until(sock, MSG_FRAME_RAW)
        generation, width, height, stride, pixel_format, _timestamp = struct.unpack_from("<IHHIIQ", payload)
        assert generation == streaming["generation"]
        assert (width, height) == (streaming["width"], streaming["height"])
        assert pixel_format == 1 and stride >= width
        assert len(payload) == 24 + stride * height

    def test_6_a_format_that_is_not_advertised_is_ignored(self, v2_server):
        with v2_server.connect() as sock:
            _handshake_v2(sock)
            sock.sendall(encode_json_message(MSG_SET_STREAM_CONFIG, {"frame_format": "BAYER16"}))
            sock.sendall(encode_message(MSG_GET_CONTROLS))
            _read_until(sock, MSG_CONTROL_INFO)
            payload, _ = _read_until(sock, MSG_FRAME)  # still JPEG frames
        assert payload[:2] == b"\xff\xd8"


# -- the real Python client against each server ------------------------------


class TestPythonClient:
    def test_camera_client_connects_and_receives_decodable_frames(self, server):
        buffer = LatestFrame()
        client = CameraClient(buffer, port=server.port)
        controls_seen = threading.Event()
        client.on_controls_update(lambda _controls: controls_seen.set())
        try:
            hello = client.connect(timeout=_TIMEOUT)
            assert hello["protocol_version"] == 1
            assert {"exposure", "gain"} <= {c["id"] for c in client.controls}
            client.start_receiving()

            deadline = time.monotonic() + _TIMEOUT
            while buffer.get()[0] is None and time.monotonic() < deadline:
                time.sleep(0.02)
            jpeg, _ts, frame_id = buffer.get()
            assert jpeg is not None, "no frame reached the LatestFrame buffer"
            assert frame_id >= 1

            # This is exactly what the solver does with a frame.
            image = Image.open(io.BytesIO(jpeg)).convert("L")
            assert image.size[0] > 100 and image.size[1] > 100
            if server.kind == "exe":
                assert (image.width, image.height) == (hello["default_width"], hello["default_height"])

            controls_seen.clear()
            gain = next(c for c in client.controls if c["id"] == "gain")
            client.set_control("gain", gain["min"] + (gain["max"] - gain["min"]) // 2)
            assert controls_seen.wait(_TIMEOUT), "no CONTROL_INFO after SET_CONTROL"
            applied = next(c for c in client.controls if c["id"] == "gain")["cur"]
            assert applied == gain["min"] + (gain["max"] - gain["min"]) // 2
        finally:
            client.stop()

    def test_frame_ids_increase_and_the_buffer_holds_only_the_newest(self, server):
        buffer = LatestFrame()
        client = CameraClient(buffer, port=server.port)
        try:
            client.connect(timeout=_TIMEOUT)
            client.start_receiving()
            ids = []
            deadline = time.monotonic() + _TIMEOUT
            while len(set(ids)) < 5 and time.monotonic() < deadline:
                ids.append(buffer.get()[2])
                time.sleep(0.05)
            assert ids == sorted(ids)
            assert len(set(ids)) >= 5, f"too few distinct frames: {ids}"
        finally:
            client.stop()


# -- C# server specifics ---------------------------------------------------


class TestCSharpServerBehavior:
    def test_hello_names_the_backend_and_the_real_stream_size(self, fake_exe_server):
        with fake_exe_server.connect() as sock:
            hello, _ = _handshake(sock)
            payload, _ = _read_until(sock, MSG_FRAME)
        assert hello["backend"] == "windows-dotnet-fake"
        assert hello["backend_version"].count(".") == 2
        image = Image.open(io.BytesIO(payload))
        assert (image.width, image.height) == (hello["default_width"], hello["default_height"])
        assert image.size == (1280, 720)  # the sample images, not a hardcoded size

    def test_hello_carries_camera_key_sensor_and_the_v2_offer(self, exe_server):
        """Tasks 7.0 and 7.1: the engine's profile key, the sensor size, and the v2 offer (the base version stays 1)."""
        with exe_server.connect() as sock:
            hello, _ = _handshake(sock)
        assert hello["protocol_version"] == 1
        assert hello["max_protocol_version"] == 2
        assert hello["capabilities"]["frame_formats"] == ["JPEG"]
        assert hello["capabilities"]["stream_info"] is True
        key = hello["camera_key"]
        assert isinstance(key, str) and key.count(":") >= 1 and key.split(":")[0] == _exe_backend()
        sensor = hello["capabilities"]["sensor"]
        assert sensor["width"] > 0 and sensor["height"] > 0
        assert isinstance(sensor["mono"], bool)
        assert sensor.get("pixel_size_um", 1.0) > 0

    def test_exposure_uses_the_legacy_100us_unit_and_only_v1_controls(self, exe_server):
        with exe_server.connect() as sock:
            _, info = _handshake(sock)
        controls = _controls_by_id(info)
        assert set(controls) == {"exposure", "gain"}
        assert controls["exposure"]["unit"] == "100us"
        assert controls["gain"]["unit"] == "raw"

    def test_server_exits_with_code_zero_after_the_client_disconnects(self, exe_server):
        with exe_server.connect() as sock:
            _handshake(sock)
            _read_until(sock, MSG_FRAME)
        assert exe_server.process.wait(timeout=10) == 0

    def test_protocol_version_mismatch_gets_an_error_message(self, exe_server):
        with exe_server.connect() as sock:
            msg_type, _ = read_message(sock)
            assert msg_type == MSG_HELLO
            sock.sendall(encode_json_message(MSG_HELLO, {"protocol_version": 3}))
            msg_type, payload = read_message(sock)
            assert msg_type == MSG_ERROR
            assert b"Protocol version mismatch" in payload
            with pytest.raises(DisconnectError):
                read_message(sock)
        assert exe_server.process.wait(timeout=10) == 1

    def test_oversize_message_is_rejected(self, exe_server):
        with exe_server.connect() as sock:
            _handshake(sock)
            sock.sendall(struct.pack("<II", MSG_SET_CONTROL, 16 * 1024 * 1024 + 1))
            payload, _ = _read_until(sock, MSG_ERROR)
            assert b"limit" in payload
        assert exe_server.process.wait(timeout=10) == 1

    def test_malformed_set_control_does_not_break_the_session(self, exe_server):
        with exe_server.connect() as sock:
            _handshake(sock)
            sock.sendall(encode_message(MSG_SET_CONTROL, b"{not json"))
            sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": "gain", "value": 7}))
            payload, _ = _read_until(sock, MSG_CONTROL_INFO)
        assert _controls_by_id(json.loads(payload))["gain"]["cur"] == 7

    def test_undrained_stdout_stderr_pipes_never_block_the_session(self):
        """Regression: the engine spawns the server with PIPEs and never reads them.

        A Windows pipe holds only a few KB. The server used to log every SET_CONTROL
        synchronously, so after ~170 of them the session thread blocked on stderr and
        the server neither answered nor exited. Run with ``--log-level debug`` (every
        control logs) and nothing reading the pipes, like ``SubprocessManager``.
        """
        exe = _exe_path()
        if exe is None:
            pytest.skip(f"needs {_SERVER_ENV} (C# camera server behaviour)")
        port = _free_port()
        proc = subprocess.Popen(
            [str(exe), *_exe_args(port), "--log-level", "debug"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        try:
            # Nothing may read proc.stdout / proc.stderr. Wait for the port by retrying the real
            # connect (a bare probe connection would make the single-client server exit).
            sock = None
            deadline = time.monotonic() + 20.0
            while sock is None:
                try:
                    sock = socket.create_connection(("127.0.0.1", port), timeout=_TIMEOUT)
                except OSError:
                    assert proc.poll() is None, "server exited before listening"
                    assert time.monotonic() < deadline, "server never listened"
                    time.sleep(0.1)
            sock.settimeout(_TIMEOUT)
            with sock:
                _handshake(sock)
                counts = {"control_info": 0, "frames": 0}

                def read_all():
                    try:
                        while True:
                            msg_type, _ = read_message(sock)
                            if msg_type == MSG_CONTROL_INFO:
                                counts["control_info"] += 1
                            elif msg_type == MSG_FRAME:
                                counts["frames"] += 1
                    except (DisconnectError, OSError):
                        pass

                reader = threading.Thread(target=read_all, daemon=True)
                reader.start()
                n = 1000
                for i in range(n):
                    control_id = "gain" if i % 7 else "no_such_control"  # also the unknown-control path
                    sock.sendall(encode_json_message(MSG_SET_CONTROL, {"id": control_id, "value": i % 100}))
                    time.sleep(0.002)
                deadline = time.monotonic() + 10.0
                while counts["control_info"] < n and time.monotonic() < deadline:
                    time.sleep(0.05)
                assert counts["control_info"] == n, f"only {counts['control_info']} of {n} CONTROL_INFO answers"
                frames_before = counts["frames"]
                time.sleep(1.0)
                assert counts["frames"] > frames_before, "frames stopped flowing"
            # The client is gone: the server must exit by itself (not hang on a blocked log write).
            assert proc.wait(timeout=10) == 0
        finally:
            if proc.poll() is None:
                proc.kill()
            proc.wait(timeout=5)
