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
from evf.config.manager import ConfigManager
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.goto_target import GotoTarget
from evf.engine.pointing import PointingState
from evf.engine.state import StateMachine
from evf.webserver.server import WebServer
from tests.mock_camera_server import MockCameraServer
from tests.test_camera_controls import FakeClient, _all_v2, _gain, _v1_exposure
from tests.test_camera_v2 import _wait


# -- validate_control_request ------------------------------------------------------------------


class TestValidateControlRequest:
    def test_an_int_control_rounds_to_an_int(self):
        assert controls.validate_control_request(_all_v2(), "gain", 41.6) == 42
        assert isinstance(controls.validate_control_request(_all_v2(), "gain", 41.6), int)

    def test_a_float_control_takes_an_int_or_a_float(self):
        assert controls.validate_control_request(_all_v2(), "fps", 4) == 4.0
        assert isinstance(controls.validate_control_request(_all_v2(), "fps", 4), float)
        assert controls.validate_control_request(_all_v2(), "fps", 2.5) == 2.5

    def test_the_bounds_themselves_are_in_range(self):
        assert controls.validate_control_request(_all_v2(), "fps", 1.0) == 1.0
        assert controls.validate_control_request(_all_v2(), "fps", 10.0) == 10.0
        assert controls.validate_control_request(_all_v2(), "exposure", 4_000_000) == 4_000_000

    def test_float_noise_at_the_bound_is_not_an_error(self):
        assert controls.validate_control_request(_all_v2(), "fps", 10.0 + 1e-12) == pytest.approx(10.0)

    @pytest.mark.parametrize("control_id, value", [
        ("fps", 10.5), ("fps", 0.5), ("exposure", 4_000_001), ("exposure", 50), ("gain", -1), ("gain", 101),
    ])
    def test_a_number_outside_the_range_is_rejected(self, control_id, value):
        with pytest.raises(controls.ControlRequestError, match="outside"):
            controls.validate_control_request(_all_v2(), control_id, value)

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
        with pytest.raises(controls.ControlRequestError):
            controls.validate_control_request(v1, "exposure", 16000)


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
        for control_id, value in [("fps", 99), ("binning", 3), ("contrast", 1), ("gain", None)]:
            with pytest.raises(controls.ControlRequestError):
                engine.set_control(control_id, value)
        assert client.sent == []
        assert engine._config.get("camera", "profiles") == {}

    def test_the_exposure_cap_is_in_the_descriptors_and_in_the_validation(self, engine):
        engine._config.set("camera", "max_exposure_us", 1_000_000)
        assert next(c for c in engine.camera_controls if c["id"] == "exposure")["max"] == 1_000_000
        engine.set_control("exposure", 1_000_000)
        with pytest.raises(controls.ControlRequestError, match="outside"):
            engine.set_control("exposure", 1_000_001)

    def test_without_a_camera_nothing_is_validated_and_the_legacy_keys_are_written(self, tmp_path):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        assert eng.camera_controls is None
        eng.set_control("gain", 31)
        assert eng._config.gain == 31


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

    async def test_an_enum_option_is_accepted(self, api):
        url, client = api
        async with ClientSession() as s:
            async with s.post(url, json={"name": "binning", "value": 2}) as resp:
                assert resp.status == 204
        assert client.sent == [("binning", 2)]

    @pytest.mark.parametrize("body", [
        {"name": "binning", "value": 3},       # not an option
        {"name": "fps", "value": 11},          # above max
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
        server = _server(tmp_path, camera_stream=lambda: {"protocol_version": 2, "stream": info})
        camera = server._build_payload()["camera"]
        assert (camera["protocol_version"], camera["stream"]) == (2, info)
        assert camera["connected"] is False  # no frame yet, and the camera is not reconfiguring

    def test_a_reconfiguring_stream_is_not_a_lost_camera(self, tmp_path):
        info = {"width": 968, "height": 608, "binning": 2, "state": "reconfiguring", "frame_interval_us": 250000}
        server = _server(tmp_path, camera_stream=lambda: {"protocol_version": 2, "stream": info})
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
    def __init__(self, generation, state="streaming"):
        self.generation, self.state = generation, state

    def to_dict(self):
        return {"width": 968, "height": 608, "binning": 2, "state": self.state, "frame_interval_us": 100000}


class _Client:
    def __init__(self, version=2, info=None, connected=True):
        self.session_version, self.stream_info, self.connected = version, info, connected


class TestStreamStatus:
    def test_no_client_or_a_dead_client_has_no_stream(self):
        status = stream.StreamStatus()
        assert status.payload(None, False, 0.0) == {"protocol_version": None, "stream": None}
        dead = _Client(info=_Info(1), connected=False)
        assert status.payload(dead, True, 0.0) == {"protocol_version": None, "stream": None}

    def test_a_v1_client_has_a_version_and_no_stream(self):
        assert stream.StreamStatus().payload(_Client(version=1), True, 0.0) == {
            "protocol_version": 1, "stream": None}

    def test_the_announced_state_is_passed_on(self):
        out = stream.StreamStatus().payload(_Client(info=_Info(1)), True, 0.0)
        assert out["protocol_version"] == 2 and out["stream"]["state"] == "streaming"
        out = stream.StreamStatus().payload(_Client(info=_Info(1, "reconfiguring")), True, 0.0)
        assert out["stream"]["state"] == "reconfiguring"

    def test_between_a_new_generation_and_its_first_frame_the_state_is_reconfiguring(self):
        status, info = stream.StreamStatus(), _Info(2)
        status.stream_info_arrived(info, True, now=100.0)
        client = _Client(info=info)
        assert status.payload(client, False, 101.0)["stream"]["state"] == "reconfiguring"
        assert status.payload(client, True, 101.5)["stream"]["state"] == "streaming"  # the first frame came
        assert status.payload(client, False, 102.0)["stream"]["state"] == "streaming"  # and it stays over

    def test_a_gap_that_lasts_too_long_is_not_hidden(self):
        status, info = stream.StreamStatus(), _Info(2)
        status.stream_info_arrived(info, True, now=100.0)
        late = 100.0 + stream.RECONFIGURE_GRACE_S + 0.1
        assert status.payload(_Client(info=info), False, late)["stream"]["state"] == "streaming"

    def test_an_interval_change_with_no_new_generation_is_not_a_gap(self):
        status, info = stream.StreamStatus(), _Info(1)
        status.stream_info_arrived(info, False, now=100.0)
        assert status.payload(_Client(info=info), False, 100.1)["stream"]["state"] == "streaming"

    def test_reset_forgets_the_wait_of_the_previous_session(self):
        status, info = stream.StreamStatus(), _Info(2)
        status.stream_info_arrived(info, True, now=100.0)
        status.reset()
        assert status.payload(_Client(info=info), False, 100.1)["stream"]["state"] == "streaming"


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
        eng._fork_watch_stream(client)
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
            if seen[-1] == ("streaming", 640, True) and eng.frame_buffer.get()[0] is not None:
                break
            time.sleep(0.01)
        assert seen[-1] == ("streaming", 640, True)
        assert ("reconfiguring", 1280, True) in seen  # the announced reconfiguration
        assert all(connected for _, _, connected in seen), "the camera must never read as lost"
        first_gap = next(i for i, sample in enumerate(seen) if sample[0] == "reconfiguring")
        assert not any(state == "streaming" and width == 1280 for state, width, _ in seen[first_gap:])
