# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 3, 5.1 and 8).
"""Protocol v2 in the engine's camera client, against the mock camera server.

Slice S3: negotiation, STREAM_INFO / FRAME_RAW, float ``set_control``, the mock's
v2 mode, liveness, and that a v1 session still behaves as it did before.
The wire-level vectors of ``SPEC_PROTOCOL_CAMERA.md`` section 11.8 are in
``test_camera_conformance.py``.
"""

from __future__ import annotations

import copy
import io
import json
import struct
import threading
import time

import pytest
from PIL import Image

from evf.camera import stream
from evf.camera.client import MAX_PROTOCOL_VERSION, MIN_PROTOCOL_VERSION, CameraClient, ProtocolError
from evf.camera.protocol import (
    MSG_FRAME_RAW,
    MSG_SET_STREAM_CONFIG,
    MSG_STREAM_INFO,
    encode_json_message,
    type_name,
)
from evf.engine.frame_buffer import LatestFrame
from tests.mock_camera_server import MockCameraServer
from tests.mock_camera_v2 import V2_HELLO, V2Camera


def _wait(predicate, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False


def _stream_info_payload(generation=1, state="streaming", **fields):
    info = {"generation": generation, "state": state, "width": 640, "height": 360, "binning": 2,
            "frame_format": "JPEG", "frame_interval_us": 100000, "exposure_us": 10000}
    info.update(fields)
    return json.dumps(info).encode()


@pytest.fixture()
def v2_mock():
    server = MockCameraServer(port=0, fps=20, v2=True)
    server.start()
    yield server
    server.stop()


@pytest.fixture()
def v1_mock():
    server = MockCameraServer(port=0, fps=20)
    server.start()
    yield server
    server.stop()


@pytest.fixture()
def connected(v2_mock):
    """A receiving client on a v2 session, with its frame buffer."""
    buffer = LatestFrame()
    client = CameraClient(buffer, port=v2_mock.port)
    client.connect(timeout=5.0)
    client.start_receiving()
    yield client, buffer, v2_mock
    client.stop()


# -- task 1.1 -----------------------------------------------------------------


class TestMessageIds:
    def test_type_names_of_the_v2_messages(self):
        assert type_name(0x04) == "STREAM_INFO"
        assert type_name(0x05) == "FRAME_RAW"
        assert type_name(0x15) == "SET_STREAM_CONFIG"

    def test_v1_names_are_unchanged(self):
        assert [type_name(t) for t in (0x00, 0x01, 0x02, 0x03, 0x11, 0x12)] == [
            "HELLO", "FRAME", "CONTROL_INFO", "ERROR", "SET_CONTROL", "GET_CONTROLS"]

    def test_unassigned_ids_stay_unknown(self):
        assert "UNKNOWN" in type_name(0x13)
        assert "UNKNOWN" in type_name(0x06)


# -- task 1.2: negotiation ------------------------------------------------------


class TestNegotiation:
    def test_a_v2_server_gets_a_v2_session(self, v2_mock):
        client = CameraClient(LatestFrame(), port=v2_mock.port)
        try:
            hello = client.connect(timeout=5.0)
            assert hello["protocol_version"] == 1 and hello["max_protocol_version"] == 2
            assert client.session_version == 2
            assert v2_mock.client_hello_log[-1]["protocol_version"] == 2
        finally:
            client.stop()

    def test_a_v1_server_gets_a_v1_session(self, v1_mock):
        client = CameraClient(LatestFrame(), port=v1_mock.port)
        try:
            client.connect(timeout=5.0)
            assert client.session_version == 1
            assert v1_mock.client_hello_log[-1]["protocol_version"] == 1
            assert client.stream_info is None and client.stream_geometry is None
        finally:
            client.stop()

    def test_camera_key_and_capabilities_of_a_v2_server(self, v2_mock):
        client = CameraClient(LatestFrame(), port=v2_mock.port)
        try:
            client.connect(timeout=5.0)
            assert client.camera_key == "mock:v2"
            assert client.capabilities["sensor"]["pixel_size_um"] == 3.75
            assert client.capabilities["frame_formats"] == ["JPEG", "GRAY8"]
        finally:
            client.stop()

    def test_a_server_without_camera_key_and_capabilities(self, v1_mock):
        client = CameraClient(LatestFrame(), port=v1_mock.port)
        try:
            client.connect(timeout=5.0)
            assert client.camera_key == "mock-python:MOCK_CAMERA"  # "<backend>:<camera_model>"
            assert client.capabilities == {}
        finally:
            client.stop()

    @pytest.mark.parametrize("offered, expected", [
        (None, 1), (1, 1), (2, 2), (3, 2), (99, 2), (0, 1), (-4, 1), ("2", 1), (2.0, 1), (True, 1),
    ])
    def test_the_session_runs_at_the_lower_of_both_maxima(self, offered, expected):
        hello = copy.deepcopy(V2_HELLO)
        if offered is None:
            del hello["max_protocol_version"]
        else:
            hello["max_protocol_version"] = offered
        server = MockCameraServer(port=0, fps=20, v2=True, hello=hello)
        server.start()
        try:
            client = CameraClient(LatestFrame(), port=server.port)
            try:
                client.connect(timeout=5.0)
                assert client.session_version == expected
            finally:
                client.stop()
        finally:
            server.stop()

    def test_a_server_hello_with_another_base_version_is_still_rejected(self):
        server = MockCameraServer(port=0, hello={"protocol_version": 2, "max_protocol_version": 2})
        server.start()
        try:
            client = CameraClient(LatestFrame(), port=server.port)
            with pytest.raises(ProtocolError, match="version mismatch"):
                client.connect(timeout=5.0)
            client.stop()
        finally:
            server.stop()

    def test_the_version_constants(self):
        assert (MIN_PROTOCOL_VERSION, MAX_PROTOCOL_VERSION) == (1, 2)

    def test_the_v2_handshake_needs_a_stream_info(self):
        """A server that offers v2 but sends frames where STREAM_INFO belongs is a protocol error."""
        server = MockCameraServer(port=0, hello={"protocol_version": 1, "max_protocol_version": 2,
                                                 "backend": "x", "camera_model": "y"})
        server.start()  # the plain v1 mock sends no STREAM_INFO and its first message after CONTROL_INFO is a FRAME
        try:
            client = CameraClient(LatestFrame(), port=server.port)
            with pytest.raises(ProtocolError, match="Expected STREAM_INFO"):
                client.connect(timeout=5.0)
            client.stop()
        finally:
            server.stop()


# -- task 1.3: STREAM_INFO, FRAME_RAW, float set_control ---------------------------


class TestStreamInfoParsing:
    def test_a_complete_message(self):
        info = stream.parse_stream_info(_stream_info_payload(), None, now=12.0)
        assert info == stream.StreamInfo(1, "streaming", 640, 360, 2, "JPEG", 100000, 10000, 12.0)
        assert info.geometry == stream.StreamGeometry(640, 360, 2, 1)
        assert not info.reconfiguring

    def test_a_short_reconfiguring_message_keeps_the_previous_values(self):
        first = stream.parse_stream_info(_stream_info_payload(), None, now=1.0)
        short = json.dumps({"generation": 1, "state": "reconfiguring"}).encode()
        info = stream.parse_stream_info(short, first, now=2.0)
        assert info.reconfiguring and (info.width, info.height, info.binning) == (640, 360, 2)
        assert info.frame_interval_us == 100000

    def test_a_repeated_reconfiguring_message_keeps_its_start_time(self):
        first = stream.parse_stream_info(_stream_info_payload(state="reconfiguring"), None, now=1.0)
        again = stream.parse_stream_info(_stream_info_payload(state="reconfiguring"), first, now=4.0)
        assert again.received_at == 1.0
        other_generation = stream.parse_stream_info(_stream_info_payload(2, "reconfiguring"), first, now=4.0)
        assert other_generation.received_at == 4.0

    @pytest.mark.parametrize("payload", [
        b"", b"not json", b"[1]", b"{}", json.dumps({"generation": 1}).encode(),
        json.dumps({"generation": "1", "state": "streaming", "width": 1, "height": 1}).encode(),
        json.dumps({"generation": True, "state": "streaming", "width": 1, "height": 1}).encode(),
        json.dumps({"generation": -1, "state": "streaming", "width": 1, "height": 1}).encode(),
        json.dumps({"generation": 1, "state": "streaming"}).encode(),  # no size and nothing before it
        json.dumps({"generation": 1, "state": "streaming", "width": 0, "height": 10}).encode(),
        b"\xff\xfe",
    ])
    def test_unusable_messages_are_none(self, payload):
        assert stream.parse_stream_info(payload, None, now=1.0) is None

    def test_a_bad_optional_field_does_not_spoil_the_message(self):
        payload = _stream_info_payload(binning="two", frame_interval_us=-5, exposure_us="soon")
        info = stream.parse_stream_info(payload, None, now=1.0)
        assert info.binning == 1 and info.frame_interval_us is None and info.exposure_us is None

    def test_to_dict_is_the_ws_camera_stream_object(self):
        info = stream.parse_stream_info(_stream_info_payload(), None, now=1.0)
        assert info.to_dict() == {"width": 640, "height": 360, "binning": 2, "state": "streaming",
                                  "frame_interval_us": 100000}


def _raw_frame(width=4, height=3, stride=4, generation=2, fmt=1):
    return struct.pack("<IHHIIQ", generation, width, height, stride, fmt, 99) + bytes(stride * height)


class TestFrameRawParsing:
    def test_a_well_formed_frame(self):
        header = stream.parse_frame_raw(_raw_frame())
        assert header == stream.RawFrameHeader(2, 4, 3, 4, 1, 99)

    @pytest.mark.parametrize("payload", [
        b"", bytes(23), _raw_frame(stride=3), _raw_frame(width=0), _raw_frame()[:-1],
    ])
    def test_a_malformed_frame_is_none(self, payload):
        assert stream.parse_frame_raw(payload) is None


class TestStreamInfoHandling:
    def test_the_first_stream_info_is_part_of_the_handshake(self, v2_mock):
        client = CameraClient(LatestFrame(), port=v2_mock.port)
        try:
            client.connect(timeout=5.0)
            info = client.stream_info
            assert (info.generation, info.state, info.width, info.height) == (1, "streaming", 1280, 720)
            assert client.stream_geometry == stream.StreamGeometry(1280, 720, 1, 1)
        finally:
            client.stop()

    def test_a_new_generation_clears_the_frame_buffer_and_calls_back(self, connected):
        client, buffer, server = connected
        events = []
        client.on_stream_info(lambda info, new_generation: events.append((info.state, info.generation, new_generation)))
        assert _wait(lambda: buffer.get()[0] is not None)
        client.set_control("binning", 2)
        assert _wait(lambda: client.stream_info.generation == 2)
        assert events[0] == ("reconfiguring", 1, False)
        assert ("streaming", 2, True) in events
        # the buffer holds only frames of the new size now
        assert _wait(lambda: buffer.get()[0] is not None)
        jpeg, _, _ = buffer.get()
        assert Image.open(io.BytesIO(jpeg)).size == (640, 360)
        assert client.stream_geometry == stream.StreamGeometry(640, 360, 2, 2)

    def test_the_buffer_is_empty_between_the_new_generation_and_its_first_frame(self):
        buffer = LatestFrame()
        client = CameraClient(buffer, port=1)
        client._session_version = 2
        client._handle_message(MSG_STREAM_INFO, _stream_info_payload(1))
        buffer.set(b"old-size frame", 1.0, 7)
        client._handle_message(MSG_STREAM_INFO, _stream_info_payload(1, "reconfiguring"))
        assert buffer.get()[0] == b"old-size frame"  # a reconfiguration alone keeps it
        client._handle_message(MSG_STREAM_INFO, _stream_info_payload(2))
        assert buffer.get()[0] is None

    def test_the_first_stream_info_does_not_clear_the_buffer(self):
        buffer = LatestFrame()
        buffer.set(b"frame", 1.0, 3)
        client = CameraClient(buffer, port=1)
        client._session_version = 2
        client._handle_message(MSG_STREAM_INFO, _stream_info_payload(1))
        assert buffer.get()[0] == b"frame"

    def test_an_interval_change_keeps_the_generation(self, connected):
        client, buffer, server = connected
        events = []
        client.on_stream_info(lambda info, new_generation: events.append((info.frame_interval_us, new_generation)))
        client.set_control("fps", 4.0)
        assert _wait(lambda: any(i == 250000 for i, _ in events))
        assert all(new is False for _, new in events)
        assert client.stream_info.generation == 1

    def test_a_bad_stream_info_is_ignored(self, connected):
        client, buffer, server = connected
        before = client.stream_info
        client._handle_message(MSG_STREAM_INFO, b"{broken")
        assert client.stream_info == before

    def test_a_failing_callback_does_not_end_the_receiver(self, connected):
        client, buffer, server = connected

        def boom(_info, _new):
            raise RuntimeError("boom")

        client.on_stream_info(boom)
        client.set_control("fps", 5.0)
        assert _wait(lambda: client.stream_info.frame_interval_us == 200000)
        before = buffer.get()[2]
        assert _wait(lambda: buffer.get()[2] > before)  # frames keep arriving
        assert client.connected

    def test_stream_info_in_a_v1_session_is_ignored_like_any_unknown_type(self, v1_mock):
        client = CameraClient(LatestFrame(), port=v1_mock.port)
        try:
            client.connect(timeout=5.0)
            client._handle_message(MSG_STREAM_INFO, _stream_info_payload(5))
            assert client.stream_info is None
        finally:
            client.stop()

    def test_every_message_counts_as_activity(self):
        client = CameraClient(LatestFrame(), port=1)
        assert client.last_activity_time == 0.0
        client._handle_message(0x7F, b"unknown")
        assert client.last_activity_time > 0.0
        assert client.last_frame_time == 0.0  # only a FRAME sets this one


class TestFrameRawIsIgnored:
    def test_frame_raw_is_not_a_frame(self, connected):
        client, buffer, server = connected
        assert _wait(lambda: buffer.get()[0] is not None)
        raw_seen = []
        original = client._handle_v2_message

        def spy(msg_type, payload):
            if msg_type == MSG_FRAME_RAW:
                raw_seen.append(stream.parse_frame_raw(payload))
            original(msg_type, payload)

        client._handle_v2_message = spy
        client._sock.sendall(encode_json_message(MSG_SET_STREAM_CONFIG, {"frame_format": "GRAY8"}))
        assert _wait(lambda: len(raw_seen) >= 2)
        assert all(h is not None and h.generation == client.stream_info.generation for h in raw_seen)
        # the switch cleared the buffer and FRAME_RAW never refills it
        assert buffer.get()[0] is None, "FRAME_RAW must not reach the JPEG frame buffer"
        assert client.connected

    def test_a_malformed_frame_raw_does_not_hurt(self, connected):
        client, buffer, server = connected
        client._handle_message(MSG_FRAME_RAW, b"short")
        assert client.connected


class TestFloatSetControl:
    def test_a_float_value_goes_over_the_wire_as_a_float(self, connected):
        client, buffer, server = connected
        assert client.set_control("fps", 4.5) is True
        assert _wait(lambda: len(server.set_control_log) >= 1)
        assert server.set_control_log[-1] == {"id": "fps", "value": 4.5}
        assert _wait(lambda: next(c for c in client.controls if c["id"] == "fps")["cur"] == 4.5)

    def test_set_control_reports_whether_it_was_sent(self):
        client = CameraClient(LatestFrame(), port=1)
        assert client.set_control("gain", 3) is False  # not connected


# -- task 1.4: the mock's v2 mode -------------------------------------------------


class TestMockCamera:
    def test_exposure_longer_than_the_frame_interval_lowers_fps(self):
        camera = V2Camera(fps=10.0, exposure_us=10_000)
        camera.apply("exposure", 250_000, version=2)
        assert camera.fps == 4.0
        assert camera.frame_interval_us == 250_000
        by_id = {c["id"]: c for c in camera.descriptors()}
        assert by_id["fps"]["max"] == 4.0 and by_id["fps"]["cur"] == 4.0

    def test_fps_is_clamped_to_what_the_exposure_allows(self):
        camera = V2Camera(fps=2.0, exposure_us=250_000)
        camera.apply("fps", 9.0, version=2)
        assert camera.fps == 4.0 and camera.exposure_us == 250_000  # exposure is kept

    def test_binning_moves_the_fps_range_and_flags_a_stream_restart(self):
        camera = V2Camera(fps=10.0, exposure_us=10_000)
        assert camera.apply("binning", 2, version=2) == (True, True)
        assert camera.binning == 2
        assert {c["id"]: c for c in camera.descriptors()}["fps"]["max"] == 20.0

    def test_an_enum_value_that_is_not_an_option_is_rejected(self):
        camera = V2Camera(fps=10.0, exposure_us=10_000)
        assert camera.apply("binning", 3, version=2) == (False, False)
        assert camera.binning == 1

    def test_values_are_clamped(self):
        camera = V2Camera(fps=10.0, exposure_us=10_000)
        camera.apply("gain", 1000, version=2)
        camera.apply("exposure", 10**9, version=2)
        assert camera.gain == 100 and camera.exposure_us == 4_000_000 and camera.fps == 0.25

    def test_the_v1_view_has_two_controls_in_100us(self):
        camera = V2Camera(fps=10.0, exposure_us=250_000)
        v1 = {c["id"]: c for c in camera.descriptors_v1()}
        assert set(v1) == {"exposure", "gain"} and v1["exposure"]["unit"] == "100us"
        assert v1["exposure"]["cur"] == 2500
        camera.apply("exposure", 100, version=1)  # 100 x 100 us
        assert camera.exposure_us == 10_000
        assert camera.apply("fps", 1.0, version=1) == (False, False)  # fps does not exist in v1

    def test_unknown_and_bad_values_are_ignored(self):
        camera = V2Camera(fps=10.0, exposure_us=10_000)
        assert camera.apply("nope", 1, version=2) == (False, False)
        assert camera.apply("gain", "5", version=2) == (False, False)
        assert camera.apply("gain", float("nan"), version=2) == (False, False)
        assert camera.gain == 20

    def test_a_frame_every_four_seconds_is_possible(self):
        server = MockCameraServer(port=0, fps=0.25, v2=True)
        server.start()
        try:
            client = CameraClient(LatestFrame(), port=server.port)
            client.connect(timeout=5.0)
            assert client.stream_info.frame_interval_us == 4_000_000
            client.stop()
        finally:
            server.stop()

    def test_the_stream_loop_still_answers_commands_between_slow_frames(self):
        server = MockCameraServer(port=0, fps=0.25, v2=True)
        server.start()
        try:
            client = CameraClient(LatestFrame(), port=server.port)
            client.connect(timeout=5.0)
            client.start_receiving()
            time.sleep(0.2)  # the first frame is out; the next is 4 s away
            client.set_control("gain", 42)
            assert _wait(lambda: next(c for c in client.controls if c["id"] == "gain")["cur"] == 42, 2.0)
            client.stop()
        finally:
            server.stop()

    def test_the_default_mock_is_still_v1(self, v1_mock):
        client = CameraClient(LatestFrame(), port=v1_mock.port)
        try:
            hello = client.connect(timeout=5.0)
            assert "max_protocol_version" not in hello
            assert {c["id"] for c in client.controls} == {"exposure", "gain"}
        finally:
            client.stop()


# -- task 1.6 ----------------------------------------------------------------------


class TestVersionFile:
    def test_version_json_names_the_highest_protocol_version_this_release_speaks(self):
        from evf.paths import version_json

        with open(version_json(), encoding="utf-8") as f:
            data = json.load(f)
        assert data["protocol_version"] == MAX_PROTOCOL_VERSION == 2


# -- task 1.7: a v1 server and the v2 engine behave exactly like today ---------------------


class TestV1Regression:
    """The mock in its default (v1) mode stands for every real server today."""

    @pytest.fixture()
    def engine_on_v1(self, tmp_path, v1_mock):
        from evf.config.manager import ConfigManager
        from evf.engine.engine import Engine
        from tests.test_subprocess_mgr import _NoopSubprocessManager

        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        mgr = _NoopSubprocessManager(eng.frame_buffer, eng.state_machine, eng._config,
                                     binary_path="/dev/null", port=v1_mock.port)
        eng._subprocess_mgr = mgr
        eng._fork_camera_connected(mgr.start())
        yield eng, mgr, v1_mock
        mgr.stop()

    def test_two_controls_in_the_servers_native_unit_and_no_v2_traffic(self, engine_on_v1):
        eng, mgr, server = engine_on_v1
        assert mgr.client.session_version == 1
        controls = eng.camera_controls
        assert [c["id"] for c in controls] == ["exposure", "gain"]
        assert controls[0]["unit"] == "ms"  # whatever the server says, untouched
        assert mgr.client.stream_info is None
        time.sleep(0.5)
        assert server.stream_info_log == [] and server.stream_config_log == []

    def test_the_session_answers_with_protocol_version_1(self, engine_on_v1):
        _eng, _mgr, server = engine_on_v1
        assert server.client_hello_log[-1]["protocol_version"] == 1

    def test_startup_restore_sends_exactly_the_legacy_exposure_and_gain(self, tmp_path, v1_mock, monkeypatch):
        from evf.config.manager import ConfigManager
        from evf.engine.engine import Engine
        from tests.test_subprocess_mgr import _NoopSubprocessManager

        cfg = ConfigManager(config_dir=tmp_path)
        cfg.exposure, cfg.gain = 321, 99
        eng = Engine(config=cfg)
        monkeypatch.setattr("evf.engine.engine._usb_video_device_present", lambda: True)
        monkeypatch.setattr("evf.engine.engine.SubprocessManager",
                            lambda fb, sm, config: _NoopSubprocessManager(fb, sm, config, binary_path="/dev/null",
                                                                         port=v1_mock.port))
        eng.startup_camera()
        try:
            assert _wait(lambda: len(v1_mock.set_control_log) == 2)
            assert v1_mock.set_control_log == [{"id": "exposure", "value": 321}, {"id": "gain", "value": 99}]
        finally:
            eng._subprocess_mgr.stop()

    def test_first_run_stores_the_range_midpoints_in_the_legacy_keys(self, tmp_path, v1_mock, monkeypatch):
        from evf.config.manager import ConfigManager
        from evf.engine.engine import Engine
        from tests.test_subprocess_mgr import _NoopSubprocessManager

        cfg = ConfigManager(config_dir=tmp_path)
        eng = Engine(config=cfg)
        monkeypatch.setattr("evf.engine.engine._usb_video_device_present", lambda: True)
        monkeypatch.setattr("evf.engine.engine.SubprocessManager",
                            lambda fb, sm, config: _NoopSubprocessManager(fb, sm, config, binary_path="/dev/null",
                                                                         port=v1_mock.port))
        eng.startup_camera()
        try:
            assert _wait(lambda: len(v1_mock.set_control_log) == 2)
            # the mock advertises exposure 1..5000 and gain 0..255: the same (min + max) // 2 as before
            assert (cfg.exposure, cfg.gain) == (2500, 127)
            assert v1_mock.set_control_log == [{"id": "exposure", "value": 2500}, {"id": "gain", "value": 127}]
        finally:
            eng._subprocess_mgr.stop()

    def test_set_control_writes_the_legacy_keys_and_the_profile(self, engine_on_v1):
        eng, mgr, server = engine_on_v1
        eng.set_control("exposure", 777)
        eng.set_control("gain", 33)
        assert _wait(lambda: len(server.set_control_log) >= 2)
        assert (eng._config.exposure, eng._config.gain) == (777, 33)  # downgrade safety
        from evf.config import profiles
        assert profiles.get_saved_control(eng._config, mgr.client.camera_key, "exposure") == {
            "value": 777, "unit": "ms"}

    def test_the_stall_timeout_stays_two_seconds(self, engine_on_v1):
        from evf.camera.subprocess_mgr import SubprocessManager

        _eng, mgr, _server = engine_on_v1
        assert SubprocessManager._FRAME_STALL_TIMEOUT == 2.0
        stall, timeout = mgr._fork_stall(mgr.client)
        assert timeout == mgr._FRAME_STALL_TIMEOUT
        assert stall >= 0

    def test_the_geometry_is_still_hello_corrected_by_the_first_frame(self, engine_on_v1):
        eng, _mgr, _server = engine_on_v1
        profile = eng._camera_profile
        assert profile.expected_frame_size() is None
        assert _wait(lambda: eng.frame_buffer.get()[0] is not None)
        payload = profile.geometry_payload()  # reads the first frame
        assert (payload["image_w"], payload["image_h"]) == (1280, 720)
        assert profile.geometry.awaiting_frame is False

    def test_a_v1_server_that_sends_message_ids_4_and_5_is_not_confused(self, v1_mock):
        client = CameraClient(LatestFrame(), port=v1_mock.port)
        try:
            client.connect(timeout=5.0)
            client._handle_message(MSG_STREAM_INFO, _stream_info_payload(2))
            client._handle_message(MSG_FRAME_RAW, _raw_frame())
            assert client.stream_info is None and client.session_version == 1
        finally:
            client.stop()
