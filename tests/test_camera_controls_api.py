# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 6.1 and 8).
"""``POST /api/control`` validation, the exposure cap and the stream fields of ``/ws`` (slice S4)."""

from __future__ import annotations

import math
import threading
import time

import pytest
from aiohttp import ClientSession

from evf.camera import controls, stream
from evf.camera.client import CameraClient
from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.goto_target import GotoTarget
from evf.engine.pointing import PointingState
from evf.engine.state import StateMachine
from evf.webserver.server import WebServer
from tests.mock_camera_server import MockCameraServer
from tests.test_camera_controls import FakeClient, _all_v2, _exposure_v2, _fps, _gain, _v1_exposure
from tests.test_camera_v2 import _stream_info_payload, _wait


# -- validate_control_request ------------------------------------------------------------------


class TestValidateControlRequest:
    def test_an_int_control_rounds_to_an_int(self):
        assert controls.validate_control_request(_all_v2(), "gain", 41.6) == 42
        assert isinstance(controls.validate_control_request(_all_v2(), "gain", 41.6), int)

    def test_a_float_control_takes_an_int_or_a_float(self):
        assert controls.validate_control_request(_all_v2(), "fps", 4) == 4.0
        assert isinstance(controls.validate_control_request(_all_v2(), "fps", 4), float)
        assert controls.validate_control_request(_all_v2(), "fps", 2.5) == 2.5

    def test_the_bounds_themselves_are_kept(self):
        assert controls.validate_control_request(_all_v2(), "fps", 1.0) == 1.0
        assert controls.validate_control_request(_all_v2(), "fps", 10.0) == 10.0
        assert controls.validate_control_request(_all_v2(), "exposure", 4_000_000) == 4_000_000

    @pytest.mark.parametrize("control_id, value, clamped", [
        ("fps", 10.5, 10.0), ("fps", 0.5, 1.0), ("exposure", 4_000_001, 4_000_000),
        ("exposure", 50, 100), ("gain", -1, 0), ("gain", 101, 100), ("gain", 1e9, 100),
    ])
    def test_a_number_outside_the_range_is_clamped_like_the_camera_does(self, control_id, value, clamped):
        assert controls.validate_control_request(_all_v2(), control_id, value) == clamped

    def test_a_clamped_int_is_still_an_int_and_a_clamped_float_a_float(self):
        assert isinstance(controls.validate_control_request(_all_v2(), "gain", 500), int)
        assert isinstance(controls.validate_control_request(_all_v2(), "fps", 500), float)

    def test_the_type_of_the_bound_does_not_leak_into_the_clamped_value(self):
        int_exposure_float_cap = controls.capped_controls([_exposure_v2()], 1_000_000.0)
        clamped = controls.validate_control_request(int_exposure_float_cap, "exposure", 3_000_000)
        assert clamped == 1_000_000 and isinstance(clamped, int)
        float_fps_int_max = [_fps(max=10)]
        clamped = controls.validate_control_request(float_fps_int_max, "fps", 50)
        assert clamped == 10.0 and isinstance(clamped, float)
        clamped = controls.validate_control_request([_fps(min=1)], "fps", 0.1)
        assert clamped == 1.0 and isinstance(clamped, float)

    def test_the_cap_in_the_descriptors_is_what_is_clamped_to(self):
        capped = controls.capped_controls(_all_v2(), 1_000_000)
        assert controls.validate_control_request(capped, "exposure", 3_000_000) == 1_000_000

    def test_an_enum_value_must_be_one_of_its_options(self):
        assert controls.validate_control_request(_all_v2(), "binning", 2) == 2
        assert controls.validate_control_request(_all_v2(), "binning", 2.0) == 2
        with pytest.raises(controls.ControlRequestError, match="not one of"):
            controls.validate_control_request(_all_v2(), "binning", 3)
        with pytest.raises(controls.ControlRequestError, match="not one of"):
            controls.validate_control_request(_all_v2(), "binning", 1.5)

    def test_an_id_the_camera_does_not_advertise_is_rejected(self):
        with pytest.raises(controls.ControlRequestError, match="Unknown control"):
            controls.validate_control_request([_gain()], "fps", 4.0)
        with pytest.raises(controls.ControlRequestError, match="Unknown control"):
            controls.validate_control_request([], "gain", 4)

    @pytest.mark.parametrize("value", [None, "5", True, float("nan"), float("inf"), [1]])
    def test_a_value_that_is_not_a_number_is_rejected(self, value):
        with pytest.raises(controls.ControlRequestError, match="not a number"):
            controls.validate_control_request(_all_v2(), "gain", value)

    def test_a_v1_exposure_is_checked_in_the_servers_own_unit(self):
        v1 = [_v1_exposure("log2s"), _gain()]
        assert controls.validate_control_request(v1, "exposure", 2) == 2
        assert controls.validate_control_request(v1, "exposure", 16000) == 15000  # clamped to its max


# -- capped_controls ---------------------------------------------------------------------------


class TestCappedControls:
    def test_the_exposure_max_is_limited_to_the_cap(self):
        capped = controls.capped_controls(_all_v2(), 2_000_000)
        maxima = {c["id"]: c.get("max") for c in capped}
        assert maxima["exposure"] == 2_000_000
        assert maxima["gain"] == 100  # the others stay

    def test_the_cameras_own_list_is_not_modified(self):
        infos = _all_v2()
        controls.capped_controls(infos, 2_000_000)
        assert next(c for c in infos if c["id"] == "exposure")["max"] == 4_000_000

    def test_a_cap_above_the_max_changes_nothing(self):
        infos = _all_v2()
        assert controls.capped_controls(infos, 8_000_000) == infos

    def test_a_v1_exposure_is_not_capped(self):
        v1 = [_v1_exposure("100us")]
        assert controls.capped_controls(v1, 5000) == v1

    @pytest.mark.parametrize("cap", [None, 0, -5, "4000000", True, math.nan])
    def test_a_cap_that_is_not_a_positive_number_changes_nothing(self, cap):
        infos = _all_v2()
        assert controls.capped_controls(infos, cap) == infos

    def test_a_cap_below_the_minimum_changes_nothing(self):
        infos = _all_v2()
        assert controls.capped_controls(infos, 50) == infos  # min is 100


# -- Engine: validation and the cap ---------------------------------------------------------


class _Manager:
    def __init__(self, client):
        self.client = client


@pytest.fixture()
def engine(tmp_path):
    eng = Engine(config=ConfigManager(config_dir=tmp_path))
    eng._subprocess_mgr = _Manager(FakeClient(_all_v2()))
    return eng


class TestEngineSetControl:
    def test_a_valid_request_is_sent_in_the_controls_type(self, engine):
        engine.set_control("fps", 4)
        engine.set_control("gain", 40.2)
        engine.set_control("binning", 2.0)
        assert engine._subprocess_mgr.client.sent == [("fps", 4.0), ("gain", 40), ("binning", 2)]

    def test_a_bad_request_sends_and_saves_nothing(self, engine):
        client = engine._subprocess_mgr.client
        for control_id, value in [("binning", 3), ("contrast", 1), ("gain", None)]:
            with pytest.raises(controls.ControlRequestError):
                engine.set_control(control_id, value)
        assert client.sent == []
        assert engine._config.get("camera", "profiles") == {}

    def test_an_out_of_range_number_is_clamped_sent_and_saved_clamped(self, engine):
        client = engine._subprocess_mgr.client
        engine.set_control("fps", 99)
        assert client.sent == [("fps", 10.0)]
        assert profiles.get_saved_control(engine._config, client.camera_key, "fps") == {"value": 10.0, "unit": "fps"}

    def test_the_exposure_cap_is_in_the_descriptors_and_is_enforced(self, engine):
        client = engine._subprocess_mgr.client
        engine._config.set("camera", "max_exposure_us", 1_000_000)
        assert next(c for c in engine.camera_controls if c["id"] == "exposure")["max"] == 1_000_000
        engine.set_control("exposure", 1_000_000)
        engine.set_control("exposure", 3_000_000)
        assert client.sent == [("exposure", 1_000_000), ("exposure", 1_000_000)]
        assert profiles.get_saved_control(engine._config, client.camera_key, "exposure")["value"] == 1_000_000

    def test_without_a_camera_nothing_is_validated_and_the_legacy_keys_are_written(self, tmp_path):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        assert eng.camera_controls is None
        eng.set_control("gain", 31)
        assert eng._config.gain == 31

    def test_a_client_with_no_descriptors_yet_counts_as_no_camera(self, tmp_path):
        """During a reconnect the client has no CONTROL_INFO yet: not an 'Unknown control'."""
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        client = FakeClient([], version=1)
        eng._subprocess_mgr = _Manager(client)
        eng.set_control("exposure", 7)
        assert eng._config.exposure == 7  # the legacy key, as with no camera
        assert client.sent == [("exposure", 7)]


# -- POST /api/control ------------------------------------------------------------------------


@pytest.fixture()
def api(tmp_path, engine):
    cfg = engine._config
    cfg._data["webserver"]["port"] = 0
    ws = WebServer(PointingState(), StateMachine(), GotoTarget(), cfg,
                   frame_buffer=LatestFrame(), actions=engine)
    ws.start()
    for _ in range(40):
        if ws._port is not None:
            break
        threading.Event().wait(0.05)
    yield f"http://127.0.0.1:{ws._port}/api/control", engine._subprocess_mgr.client
    ws.stop()


@pytest.mark.asyncio
class TestControlApi:
    async def test_a_float_is_accepted_and_reaches_the_camera(self, api):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, json={"name": "fps", "value": 2.5}) as resp:
                assert resp.status == 204
        assert client.sent == [("fps", 2.5)]

    async def test_an_int_is_accepted(self, api):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, json={"name": "exposure", "value": 250_000}) as resp:
                assert resp.status == 204
        assert client.sent == [("exposure", 250_000)]

    async def test_a_number_above_the_range_is_clamped_not_rejected(self, api):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, json={"name": "fps", "value": 11}) as resp:
                assert resp.status == 204
        assert client.sent == [("fps", 10.0)]

    async def test_an_enum_option_is_accepted(self, api):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, json={"name": "binning", "value": 2}) as resp:
                assert resp.status == 204
        assert client.sent == [("binning", 2)]

    @pytest.mark.parametrize("body", [
        {"name": "binning", "value": 3},       # not an option
        {"name": "contrast", "value": 1},      # not advertised
        {"name": "gain", "value": "high"},     # not a number
        {"name": "gain", "value": True},       # a boolean is not a number
        {"name": "gain"},                      # no value
        {"value": 5},                          # no name
        {"name": 7, "value": 5},               # name is not a string
        ["gain", 5],                           # not an object
    ])
    async def test_a_request_the_camera_cannot_take_is_400(self, api, body):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, json=body) as resp:
                assert resp.status == 400
        assert client.sent == []

    async def test_a_body_that_is_not_json_is_400(self, api):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, data=b"{nope", headers={"Content-Type": "application/json"}) as resp:
                assert resp.status == 400
        assert client.sent == []


# -- /ws: controls and camera.stream -----------------------------------------------------------


def _server(tmp_path, **kwargs):
    cfg = ConfigManager(config_dir=tmp_path)
    return WebServer(PointingState(), StateMachine(), GotoTarget(), cfg, frame_buffer=LatestFrame(), **kwargs)


class TestWsCameraBlock:
    def test_without_a_provider_the_new_fields_are_empty(self, tmp_path):
        camera = _server(tmp_path)._build_payload()["camera"]
        assert camera["protocol_version"] is None and camera["stream"] is None
        assert camera["connected"] is False

    def test_the_provider_fills_protocol_version_and_stream(self, tmp_path):
        info = {"width": 968, "height": 608, "binning": 2, "state": "streaming", "frame_interval_us": 250000}
        server = _server(tmp_path, camera_stream=lambda: {
            "connected": False, "protocol_version": 2, "stream": info})
        camera = server._build_payload()["camera"]
        assert (camera["protocol_version"], camera["stream"]) == (2, info)

    def test_connected_comes_from_the_provider_when_it_gives_one(self, tmp_path):
        server = _server(tmp_path, camera_stream=lambda: {"connected": True, "protocol_version": 2, "stream": None})
        assert server._build_payload()["camera"]["connected"] is True  # its frame buffer is empty

    def test_without_connected_from_the_provider_the_frame_buffer_decides_as_before(self, tmp_path):
        server = _server(tmp_path, camera_stream=lambda: {"protocol_version": 1, "stream": None})
        assert server._build_payload()["camera"]["connected"] is False
        server._frame_buffer.set(b"jpeg", 1.0, 1)
        assert server._build_payload()["camera"]["connected"] is True

    def test_a_v1_session_has_a_version_and_no_stream(self, tmp_path):
        server = _server(tmp_path, camera_stream=lambda: {"protocol_version": 1, "stream": None})
        camera = server._build_payload()["camera"]
        assert (camera["protocol_version"], camera["stream"]) == (1, None)

    def test_the_payload_is_json(self, tmp_path):
        import json
        info = {"width": 968, "height": 608, "binning": 2, "state": "streaming", "frame_interval_us": None}
        server = _server(tmp_path, camera_stream=lambda: {"protocol_version": 2, "stream": info},
                         camera_controls=lambda: _all_v2())
        assert json.dumps(server._build_payload())

    def test_the_controls_are_passed_on_as_received(self, tmp_path):
        server = _server(tmp_path, camera_controls=lambda: _all_v2())
        sent = server._build_payload()["controls"]
        assert [c["id"] for c in sent] == ["gain", "exposure", "binning", "fps"]
        assert sent[2]["options"] == [{"value": 1, "label": "1x1"}, {"value": 2, "label": "2x2"}]
        assert sent[2]["restarts_stream"] is True
        assert sent[1]["unit"] == "us"


# -- StreamStatus ---------------------------------------------------------------------------


class _Info:
    def __init__(self, generation, state="streaming", since=None):
        self.generation, self.state, self.generation_since = generation, state, since

    def to_dict(self):
        return {"width": 968, "height": 608, "binning": 2, "state": self.state, "frame_interval_us": 100000}


class _Client:
    def __init__(self, version=2, info=None, connected=True):
        self.session_version, self.stream_info, self.connected = version, info, connected


def _status():
    buffer = LatestFrame()
    return stream.StreamStatus(buffer), buffer


def _state(out):
    return out["stream"]["state"]


class TestStreamStatus:
    def test_no_client_or_a_dead_client_has_no_stream(self):
        status, buffer = _status()
        assert status.poll(None, 0.0) == {"connected": False, "protocol_version": None, "stream": None}
        buffer.set(b"frame", 1.0, 1)
        dead = _Client(info=_Info(1), connected=False)
        assert status.poll(dead, 0.0) == {"connected": True, "protocol_version": None, "stream": None}

    def test_a_v1_client_has_a_version_and_no_stream(self):
        status, buffer = _status()
        assert status.poll(_Client(version=1), 0.0) == {"connected": False, "protocol_version": 1, "stream": None}
        buffer.set(b"frame", 1.0, 1)
        assert status.poll(_Client(version=1), 0.0)["connected"] is True

    def test_connected_follows_the_frame_buffer_while_streaming(self):
        status, buffer = _status()
        client = _Client(info=_Info(1, since=1.0))
        out = status.poll(client, 2.0)
        assert (out["connected"], out["protocol_version"], _state(out)) == (False, 2, "streaming")
        buffer.set(b"frame", 1.5, 1)
        assert status.poll(client, 2.0)["connected"] is True

    def test_the_announced_reconfiguring_state_is_a_connected_camera(self):
        status, _ = _status()
        out = status.poll(_Client(info=_Info(1, "reconfiguring", since=1.0)), 2.0)
        assert _state(out) == "reconfiguring" and out["connected"] is True

    def test_before_the_first_frame_of_a_session_there_is_no_gap(self):
        """Generation 1 of a new session is not a reconfiguration: no frame yet is not 'reconfiguring'."""
        status, _ = _status()
        out = status.poll(_Client(info=_Info(1, since=1.0)), 1.1)
        assert _state(out) == "streaming" and out["connected"] is False

    def test_between_a_new_generation_and_its_first_frame_the_state_is_reconfiguring(self):
        status, buffer = _status()
        client = _Client(info=_Info(1, since=100.0))
        buffer.set(b"old-size frame", 100.5, 1)
        assert _state(status.poll(client, 100.6)) == "streaming"

        client.stream_info = _Info(2, since=200.0)  # binning change announced; buffer cleared
        buffer.clear()
        out = status.poll(client, 200.1)
        assert _state(out) == "reconfiguring" and out["connected"] is True

        buffer.set(b"new-size frame", 200.4, 1)  # the first frame of generation 2 ends the wait
        out = status.poll(client, 200.5)
        assert _state(out) == "streaming" and out["connected"] is True

    def test_a_frame_in_the_same_clock_tick_as_the_announcement_ends_the_wait(self):
        """time.monotonic() ticks every 15.6 ms on Windows: the first new frame can share the announcement's time."""
        status, buffer = _status()
        client = _Client(info=_Info(2, since=200.0))
        buffer.set(b"new", 200.0, 1)
        out = status.poll(client, 200.01)
        assert _state(out) == "streaming" and out["connected"] is True

    def test_a_stale_frame_does_not_end_the_wait(self):
        """The race: a poll that sees the new STREAM_INFO while the buffer still holds the old frame.

        The receiver thread updates the info, clears the buffer, then sets the first new frame; a
        poll can land in between. An old frame is older than the new generation's announcement.
        """
        status, buffer = _status()
        client = _Client(info=_Info(1, since=100.0))
        buffer.set(b"old", 150.0, 1)
        client.stream_info = _Info(2, since=200.0)  # announced, the buffer not cleared yet
        for now in (200.01, 200.02):
            out = status.poll(client, now)
            assert _state(out) == "reconfiguring" and out["connected"] is True
        buffer.clear()
        assert _state(status.poll(client, 200.03)) == "reconfiguring"  # still waiting
        buffer.set(b"new", 200.5, 1)
        assert _state(status.poll(client, 200.6)) == "streaming"

    def test_the_receiver_running_between_the_two_reads_is_never_a_lost_camera(self):
        """The receiver thread updates the info, then clears the buffer: a poll must read the buffer first.

        Here the receiver runs just as the buffer is read: it comes back empty and the client's
        info is already the new generation. Reading the info first, the poll would pair the old
        info (streaming) with the empty buffer and report a lost camera for one tick.
        """
        class ReceiverRunsOnRead(LatestFrame):
            armed = False

            def get(self):
                if self.armed:
                    self.armed = False
                    client.stream_info = _Info(2, since=200.0)
                    self.clear()
                return super().get()

        buffer = ReceiverRunsOnRead()
        status = stream.StreamStatus(buffer)
        client = _Client(info=_Info(1, since=100.0))
        buffer.set(b"old", 150.0, 1)
        assert _state(status.poll(client, 150.1)) == "streaming"
        buffer.armed = True
        out = status.poll(client, 200.01)
        assert out["connected"] is True and _state(out) == "reconfiguring"

    def test_a_gap_that_lasts_too_long_is_not_hidden(self):
        status, buffer = _status()
        client = _Client(info=_Info(1, since=100.0))
        buffer.set(b"old", 150.0, 1)
        client.stream_info = _Info(2, since=200.0)
        buffer.clear()
        late = 200.0 + stream.RECONFIGURE_GRACE_S + 0.1
        out = status.poll(client, late)
        assert _state(out) == "streaming" and out["connected"] is False

    def test_an_interval_change_with_no_new_generation_is_not_a_gap(self):
        status, buffer = _status()
        client = _Client(info=_Info(1, since=100.0))
        buffer.set(b"frame", 150.0, 1)
        status.poll(client, 150.1)
        client.stream_info = _Info(1, since=100.0)  # same generation, re-sent
        buffer.clear()
        assert _state(status.poll(client, 160.0)) == "streaming"

    def test_a_new_session_does_not_inherit_the_wait_of_the_old_one(self):
        status, buffer = _status()
        old = _Client(info=_Info(1, since=100.0))
        buffer.set(b"frame", 150.0, 1)
        status.poll(old, 150.1)
        new = _Client(info=_Info(1, since=300.0))  # a recovery: a new client, generation 1 again
        buffer.clear()
        out = status.poll(new, 300.1)
        assert _state(out) == "streaming" and out["connected"] is False

    def test_an_info_without_a_generation_time_never_makes_a_gap(self):
        status, buffer = _status()
        client = _Client(info=_Info(1))
        buffer.set(b"frame", 1.0, 1)
        status.poll(client, 1.1)
        client.stream_info = _Info(2)
        buffer.clear()
        assert _state(status.poll(client, 1.2)) == "streaming"


class TestGenerationSince:
    def test_a_new_generation_is_stamped_and_the_same_one_keeps_its_time(self):
        first = stream.parse_stream_info(_stream_info_payload(1), None, now=10.0)
        assert first.generation_since == 10.0
        same = stream.parse_stream_info(_stream_info_payload(1, "reconfiguring"), first, now=20.0)
        assert same.generation_since == 10.0
        new = stream.parse_stream_info(_stream_info_payload(2), same, now=30.0)
        assert new.generation_since == 30.0


# -- the engine and a real v2 session ---------------------------------------------------------


class TestEngineStreamAgainstTheMock:
    @pytest.fixture()
    def session(self, tmp_path):
        server = MockCameraServer(port=0, fps=20, v2=True, v2_options={"reconfigure_s": 0.6})
        server.start()
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        client = CameraClient(eng.frame_buffer, port=server.port)
        client.connect(timeout=5.0)
        client.start_receiving()
        eng._subprocess_mgr = _Manager(client)
        yield eng, client
        client.stop()
        server.stop()

    def test_a_binning_change_is_reconfiguring_and_never_a_lost_camera(self, session):
        eng, client = session
        server = WebServer(PointingState(), StateMachine(), GotoTarget(), eng._config,
                           frame_buffer=eng.frame_buffer, camera_stream=eng.camera_stream)
        assert _wait(lambda: eng.frame_buffer.get()[0] is not None)
        camera = server._build_payload()["camera"]
        assert (camera["protocol_version"], camera["stream"]["state"], camera["connected"]) == (2, "streaming", True)

        eng.set_control("binning", 2)
        seen = []
        deadline = time.monotonic() + 5.0
        while time.monotonic() < deadline:
            camera = server._build_payload()["camera"]
            seen.append((camera["stream"]["state"], camera["stream"]["width"], camera["connected"]))
            if seen[-1] == ("streaming", 640, True):
                break
            time.sleep(0.005)
        assert seen[-1] == ("streaming", 640, True)
        assert ("reconfiguring", 1280, True) in seen  # the announced reconfiguration
        assert all(connected for _, _, connected in seen), f"the camera must never read as lost: {[x for x in seen if not x[2]]} in {seen[:60]}"
        first_gap = next(i for i, sample in enumerate(seen) if sample[0] == "reconfiguring")
        assert not any(state == "streaming" and width == 1280 for state, width, _ in seen[first_gap:])
