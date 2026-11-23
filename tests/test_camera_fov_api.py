# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 6.1, 6.3, task 5.5).
"""``POST /api/camera/fov``: focal length, direct FOV, reset."""

from __future__ import annotations

import json
import threading

import pytest
from aiohttp import ClientSession

from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.goto_target import GotoTarget
from evf.engine.pointing import PointingState
from evf.engine.state import StateMachine
from evf.solver.fov import fov_from_focal_length
from evf.webserver.server import WebServer
from tests.fov_fakes import IDS_KEY, IDS_PLAIN_HELLO, IDS_SENSOR_HELLO, OV_HELLO, FakeSolver

IDS_SENSOR_MM = 1936 * 5.86 / 1000.0
FOV_25MM = fov_from_focal_length(25, IDS_SENSOR_MM)  # 25.6 deg


@pytest.fixture
def api(tmp_path):
    """A real Engine (fake solver, no camera process) behind a real WebServer."""
    cfg = ConfigManager(config_dir=tmp_path / "cfg")
    cfg._data["webserver"]["port"] = 0
    eng = Engine(config=cfg)
    eng._solver = FakeSolver()
    ws = WebServer(
        PointingState(), StateMachine(), GotoTarget(), cfg,
        frame_buffer=LatestFrame(), actions=eng,
        camera_geometry=eng._camera_profile.geometry_payload,
    )
    ws.start()
    for _ in range(40):
        if ws._port is not None:
            break
        threading.Event().wait(0.05)
    yield eng, ws, cfg
    ws.stop()


async def _post(ws, body, raw=None):
    async with ClientSession() as s:
        kwargs = {"data": raw, "headers": {"Content-Type": "application/json"}} if raw is not None else {"json": body}
        async with s.post(f"http://127.0.0.1:{ws._port}/api/camera/fov", **kwargs) as resp:
            text = await resp.text()
            return resp.status, text


class TestNoCamera:
    async def test_no_camera_is_409(self, api):
        _eng, ws, _cfg = api
        status, text = await _post(ws, {"focal_length_mm": 25})
        assert status == 409
        assert "no camera" in text

    async def test_no_camera_is_409_for_reset_too(self, api):
        status, _ = await _post(api[1], {"reset": True})
        assert status == 409

    async def test_an_unidentifiable_camera_has_no_profile_either(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected({"protocol_version": 1})
        status, _ = await _post(ws, {"fov_h_deg": 25.6})
        assert status == 409

    async def test_a_failed_camera_start_is_409_again(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        eng._camera_profile.on_camera_gone()
        status, _ = await _post(ws, {"fov_h_deg": 25.6})
        assert status == 409


class TestFocalLength:
    async def test_focal_length_sets_the_fov_and_swaps_the_database(self, api):
        eng, ws, cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        assert eng._solver.databases.active.name == "narrow"

        status, text = await _post(ws, {"focal_length_mm": 25})

        assert status == 200
        body = json.loads(text)
        assert body["fov_h_deg"] == pytest.approx(FOV_25MM)
        assert body["fov_h_deg"] == pytest.approx(25.6, abs=0.05)
        assert body["fov_source"] == "user"
        assert body["solver_db"] == "wide"           # swapped: no solver thread runs
        assert body["fov_out_of_range"] is False
        assert eng._solver.databases.active.name == "wide"
        assert profiles.get_fov(cfg, IDS_KEY) == (pytest.approx(FOV_25MM), "user")
        assert eng._camera_profile.params.get().fov_estimate_deg == pytest.approx(FOV_25MM)

    async def test_the_swap_is_left_to_a_running_solver_thread(self, api):
        import types
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        eng._solver_thread = types.SimpleNamespace(running=True)
        status, text = await _post(ws, {"focal_length_mm": 25})
        assert status == 200
        assert json.loads(text)["solver_db"] == "narrow"      # still loaded; the thread swaps it
        assert eng._solver.apply_pending_database() is True    # what the thread does between solves
        assert eng._solver.databases.active.name == "wide"

    async def test_a_long_lens_swaps_back_to_the_narrow_database(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        await _post(ws, {"focal_length_mm": 25})
        assert eng._solver.databases.active.name == "wide"
        status, text = await _post(ws, {"focal_length_mm": 75})  # 8.7 deg on this sensor
        assert status == 200
        body = json.loads(text)
        assert body["fov_h_deg"] == pytest.approx(8.66, abs=0.05)
        assert body["solver_db"] == "narrow"

    async def test_a_fov_between_the_databases_sets_the_out_of_range_flag(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, text = await _post(ws, {"focal_length_mm": 45})  # 14 deg: neither range
        assert status == 200
        assert json.loads(text)["fov_out_of_range"] is True

    async def test_unknown_sensor_size_is_400(self, api):
        eng, ws, cfg = api
        eng._fork_camera_connected(IDS_PLAIN_HELLO)  # HELLO without capabilities.sensor
        status, text = await _post(ws, {"focal_length_mm": 25})
        assert status == 400
        assert "sensor size" in text
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)

    async def test_the_upstream_camera_has_no_sensor_size_either(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(OV_HELLO)
        status, _ = await _post(ws, {"focal_length_mm": 25})
        assert status == 400

    @pytest.mark.parametrize("bad", [0, -5, "25", True, float("inf")])
    async def test_a_bad_focal_length_is_400(self, api, bad):
        eng, ws, cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, _ = await _post(ws, None, raw=json.dumps({"focal_length_mm": bad}))
        assert status == 400
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)


class TestDirectFov:
    async def test_direct_fov_works_without_a_sensor_size(self, api):
        eng, ws, cfg = api
        eng._fork_camera_connected(OV_HELLO)
        status, text = await _post(ws, {"fov_h_deg": 9.2})
        assert status == 200
        assert json.loads(text)["fov_source"] == "user"
        assert profiles.get_fov(cfg, "linux-v4l2:OV9281") == (9.2, "user")

    @pytest.mark.parametrize("bad", [0, -1, 180, 400, "wide", True, None])
    async def test_a_bad_fov_is_400(self, api, bad):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, _ = await _post(ws, {"fov_h_deg": bad})
        assert status == 400

    async def test_nan_is_400(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, _ = await _post(ws, None, raw='{"fov_h_deg": NaN}')  # Python's json accepts NaN
        assert status == 400


class TestReset:
    async def test_reset_clears_a_user_value_and_the_search_takes_over(self, api):
        eng, ws, cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        await _post(ws, {"focal_length_mm": 25})
        assert eng._solver.databases.active.name == "wide"

        status, text = await _post(ws, {"reset": True})

        assert status == 200
        body = json.loads(text)
        assert body["fov_source"] == "searching"
        assert body["fov_h_deg"] == 8.86
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)
        assert eng._solver.databases.active.name == "narrow"  # back to the default database

    async def test_reset_without_search_goes_back_to_the_default(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(OV_HELLO)
        await _post(ws, {"fov_h_deg": 9.2})
        status, text = await _post(ws, {"reset": True})
        assert status == 200
        body = json.loads(text)
        assert (body["fov_source"], body["fov_h_deg"]) == ("default", 8.86)

    async def test_reset_leaves_a_learned_value_alone(self, api):
        eng, ws, cfg = api
        profiles.set_fov(cfg, IDS_KEY, 25.6, "learned")
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, text = await _post(ws, {"reset": True})
        assert status == 200
        assert json.loads(text)["fov_source"] == "learned"
        assert profiles.get_fov(cfg, IDS_KEY) == (25.6, "learned")


class TestBadRequests:
    @pytest.mark.parametrize("body", [
        {}, {"focal_length_mm": 25, "fov_h_deg": 25.6}, {"reset": True, "fov_h_deg": 25.6},
        {"focal_length_mm": 25, "reset": True}, {"reset": False},
    ])
    async def test_exactly_one_field_is_required(self, api, body):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, _ = await _post(ws, body)
        assert status == 400

    async def test_unknown_fields_are_400(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, text = await _post(ws, {"focal_length": 25})
        assert status == 400 and "focal_length" in text

    @pytest.mark.parametrize("raw", ["not json", "[1, 2]", "25", ""])
    async def test_a_body_that_is_not_an_object_is_400(self, api, raw):
        status, _ = await _post(api[1], None, raw=raw)
        assert status == 400

    async def test_reset_must_be_a_boolean(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        status, _ = await _post(ws, {"reset": "yes"})
        assert status == 400

    async def test_no_actions_wired_is_503(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path / "cfg")
        cfg._data["webserver"]["port"] = 0
        ws = WebServer(PointingState(), StateMachine(), GotoTarget(), cfg, frame_buffer=LatestFrame())
        ws.start()
        try:
            for _ in range(40):
                if ws._port is not None:
                    break
                threading.Event().wait(0.05)
            status, _ = await _post(ws, {"fov_h_deg": 25.6})
            assert status == 503
        finally:
            ws.stop()


class TestWsPayload:
    """What Settings reads from /ws after the API call (spec 5.6, 6.3)."""

    async def test_payload_carries_camera_info_and_the_new_fov(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        await _post(ws, {"focal_length_mm": 25})
        payload = ws._build_payload()
        assert payload["fov_source"] == "user"
        assert payload["fov_h_deg"] == pytest.approx(25.6, abs=0.05)
        assert payload["solver_db"] == "wide"
        cam = payload["camera"]
        assert cam["camera_model"] == "UI306xCP-M"
        assert cam["backend"] == "windows-dotnet-ids"
        assert cam["camera_key"] == IDS_KEY
        assert cam["sensor_width_mm"] == pytest.approx(11.35, abs=0.01)
        assert cam["focal_length_mm"] == 25.0

    async def test_searching_state_in_the_payload(self, api):
        eng, ws, _cfg = api
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        payload = ws._build_payload()
        assert payload["fov_source"] == "searching"
        assert payload["camera"]["focal_length_mm"] is None

    async def test_no_camera_payload(self, api):
        _eng, ws, _cfg = api
        cam = ws._build_payload()["camera"]
        assert cam["camera_model"] is None and cam["sensor_width_mm"] is None and cam["focal_length_mm"] is None
