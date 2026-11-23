"""Tests for /ws JSON payload schema."""

import json
import math

import pytest

from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.engine.goto_target import GotoTarget
from evf.engine.pointing import PointingState
from evf.engine.state import StateMachine
from evf.webserver.server import WebServer


def test_payload_contains_new_fields(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path))
    ws = WebServer(
        PointingState(), StateMachine(), GotoTarget(), ConfigManager(config_dir=tmp_path / "evf-config"),
        frame_buffer=LatestFrame(),
        camera_controls=lambda: [
            {"id": "exposure", "label": "Exposure", "min": 0, "max": 100, "step": 1, "value": 50, "unit": "ms"},
        ],
        sync_state=lambda: {
            "in_progress": False, "candidates": [], "selected_idx": None, "error": None,
        },
        activity=lambda: {
            "stellarium": {"active": False, "address": "localhost:10001"},
            "lx200":      {"active": False, "address": "0.0.0.0:4030"},
            "webserver":  {"url": "http://192.168.1.42:8765"},
            "audio_enabled": True,
        },
    )
    payload = ws._build_payload()
    # Existing fields still present
    assert "state" in payload
    assert "pointing" in payload
    # New fields
    assert "controls" in payload and isinstance(payload["controls"], list)
    assert "sync" in payload and "in_progress" in payload["sync"]
    assert "stellarium" in payload and "active" in payload["stellarium"]
    assert "lx200" in payload
    assert "webserver" in payload
    assert "audio_enabled" in payload
    assert "camera" in payload  # centroid arrays
    # Roundtrip JSON-serializable
    assert json.dumps(payload)


def test_payload_contains_location_field(tmp_path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path))
    cfg = ConfigManager(config_dir=tmp_path / "evf-config")
    ws = WebServer(
        PointingState(), StateMachine(), GotoTarget(), cfg,
        frame_buffer=LatestFrame(),
        location=lambda: {
            "latitude": 13.0878,
            "longitude": 80.2785,
            "source": "manual",
        },
    )
    payload = ws._build_payload()
    assert "location" in payload
    assert payload["location"]["source"] == "manual"
    assert payload["location"]["latitude"] == 13.0878


# ---------------------------------------------------------------------------
# Geometry and FOV from the engine (spec 5.6, task 4.9)
# fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
# ---------------------------------------------------------------------------

# (image_w, image_h, fov_h_deg): legacy OV9281, IDS 25 mm at 1x1, IDS 25 mm at 2x2
_GEOMETRIES = [
    pytest.param(1280, 720, 8.86, id="1280x720-8.86"),
    pytest.param(1936, 1216, 25.6, id="1936x1216-25.6"),
    pytest.param(968, 608, 25.6, id="968x608-25.6"),
]


def _geometry(w, h, fov, **extra):
    return {"image_w": w, "image_h": h, "fov_h_deg": fov, "fov_source": "user",
            "solver_db": "wide" if fov > 15 else "narrow", "fov_out_of_range": False, **extra}


def _server(tmp_path, geometry=None, *, d_body=None, pointing=True, target=None):
    cfg = ConfigManager(config_dir=tmp_path / "evf-config")
    if d_body is not None:
        cfg.sync_d_body = d_body
    ps = PointingState()
    if pointing:
        ps.update(100.0, 0.0, 0.0, 12, 0.001)
    gt = GotoTarget()
    if target is not None:
        gt.set(*target)
    kwargs = {"camera_geometry": (lambda: geometry)} if geometry is not None else {}
    return WebServer(ps, StateMachine(), gt, cfg, frame_buffer=LatestFrame(), **kwargs)


def _scale(w, fov):
    return w / (2.0 * math.tan(math.radians(fov / 2.0)))


class TestPayloadGeometry:
    def test_without_a_provider_it_is_todays_legacy_geometry(self, tmp_path):
        payload = _server(tmp_path)._build_payload()
        assert (payload["image_w"], payload["image_h"], payload["fov_h_deg"]) == (1280, 720, 8.86)
        assert payload["fov_source"] == "default"
        assert payload["solver_db"] is None
        assert payload["fov_out_of_range"] is False
        assert (payload["origin_x"], payload["origin_y"]) == (640.0, 360.0)

    @pytest.mark.parametrize("w,h,fov", _GEOMETRIES)
    def test_image_size_and_fov_follow_the_engine(self, tmp_path, w, h, fov):
        payload = _server(tmp_path, _geometry(w, h, fov))._build_payload()
        assert (payload["image_w"], payload["image_h"], payload["fov_h_deg"]) == (w, h, fov)
        assert payload["fov_source"] == "user"
        assert payload["solver_db"] == ("wide" if fov > 15 else "narrow")
        assert payload["fov_out_of_range"] is False
        assert (payload["origin_x"], payload["origin_y"]) == (w / 2.0, h / 2.0)  # no sync yet
        assert json.dumps(payload)

    def test_out_of_range_flag_is_passed_on(self, tmp_path):
        payload = _server(tmp_path, _geometry(1936, 1216, 60.0, fov_out_of_range=True))._build_payload()
        assert payload["fov_out_of_range"] is True

    def test_a_provider_without_the_extra_keys_still_works(self, tmp_path):
        geo = {"image_w": 640, "image_h": 480, "fov_h_deg": 10.0}
        payload = _server(tmp_path, geo)._build_payload()
        assert (payload["image_w"], payload["image_h"], payload["fov_h_deg"]) == (640, 480, 10.0)
        assert payload["fov_source"] == "default" and payload["solver_db"] is None

    @pytest.mark.parametrize("w,h,fov", _GEOMETRIES)
    def test_sync_origin_uses_the_geometry(self, tmp_path, w, h, fov):
        d_body = [0.02, -0.01, 0.9997]
        payload = _server(tmp_path, _geometry(w, h, fov), d_body=d_body)._build_payload()
        scale = _scale(w, fov)
        assert payload["origin_x"] == pytest.approx(w / 2.0 + (-d_body[0] / d_body[2]) * scale)
        assert payload["origin_y"] == pytest.approx(h / 2.0 + (-d_body[1] / d_body[2]) * scale)

    @pytest.mark.parametrize("w,h,fov", _GEOMETRIES)
    def test_target_projection_uses_the_geometry(self, tmp_path, w, h, fov):
        # Target 3 deg north of the pointing: straight up from the image centre.
        ws = _server(tmp_path, _geometry(w, h, fov), target=(100.0, 3.0))
        nav = ws._build_payload()["nav"]
        expected_dy = _scale(w, fov) * math.tan(math.radians(3.0))
        assert nav["pixel_x"] == pytest.approx(w / 2.0, abs=1e-6)
        assert nav["pixel_y"] == pytest.approx(h / 2.0 - expected_dy, abs=1e-6)
        assert nav["in_fov"] is (0 <= h / 2.0 - expected_dy < h)

    def test_the_same_target_is_in_the_wide_field_and_outside_the_narrow_one(self, tmp_path):
        target = (100.0, 6.0)
        narrow = _server(tmp_path / "n", _geometry(1280, 720, 8.86), target=target)._build_payload()["nav"]
        wide = _server(tmp_path / "w", _geometry(1936, 1216, 25.6), target=target)._build_payload()["nav"]
        assert narrow["in_fov"] is False and narrow["edge_x"] is not None
        assert wide["in_fov"] is True and wide["edge_x"] is None

    @pytest.mark.parametrize("w,h,fov", _GEOMETRIES)
    def test_edge_arrow_stays_inside_the_image(self, tmp_path, w, h, fov):
        ws = _server(tmp_path, _geometry(w, h, fov), target=(100.0, 40.0))
        nav = ws._build_payload()["nav"]
        assert nav["in_fov"] is False
        assert 0 <= nav["edge_x"] <= w and 0 <= nav["edge_y"] <= h

    @pytest.mark.parametrize("w,h,fov", _GEOMETRIES)
    def test_target_behind_the_camera_still_gets_an_edge_arrow(self, tmp_path, w, h, fov):
        ws = _server(tmp_path, _geometry(w, h, fov), target=(280.0, 0.0))
        nav = ws._build_payload()["nav"]
        assert nav["pixel_x"] is None and nav["in_fov"] is False
        assert 0 <= nav["edge_x"] <= w and 0 <= nav["edge_y"] <= h
