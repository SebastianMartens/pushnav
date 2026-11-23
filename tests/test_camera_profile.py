# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 4, 5.1-5.3, 5.6 and 8).
"""Profile FOV -> solver parameters, database request and stream geometry.

Covers slice S1: FOV resolution (spec 5.2 steps 1 and 4), the engine-owned
SolverParams holder, the engine asking the registry for a database at startup
and on camera connect, the ``fov_out_of_range`` flag, and the v1 stream
geometry (HELLO size corrected by the first frame).
"""

from __future__ import annotations

import io
import json
import logging
import threading
import time
import types
from pathlib import Path

import pytest
from PIL import Image

from evf.camera.stream import (
    DEFAULT_GEOMETRY,
    GeometryTracker,
    StreamGeometry,
    geometry_from_hello,
    jpeg_size,
)
from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.camera_profile import CameraProfile
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.goto_target import GotoTarget
from evf.engine.pointing import PointingState
from evf.engine.state import EngineState, StateMachine
from evf.solver.databases import DbInfo, SolverDatabases
from evf.solver.fov import (
    DEFAULT_RESOLVED_FOV,
    LEGACY_SOLVER_PARAMS,
    ResolvedFov,
    SolverParams,
    SolverParamsHolder,
    resolve_fov,
)
from evf.solver.solver import PlateSolver
from evf.solver.thread import SolverThread
from evf.webserver.server import WebServer

IDS_KEY = "ids:1409:3060"
IDS_HELLO = {
    "protocol_version": 1, "backend": "windows-dotnet-ids", "camera_model": "UI306xCP-M",
    "camera_key": IDS_KEY, "default_width": 1936, "default_height": 1216, "default_fps": 10,
}
OV_HELLO = {  # a server that sends no camera_key (Swift / Linux C / Windows C)
    "protocol_version": 1, "backend": "linux-v4l2", "camera_model": "OV9281",
    "default_width": 1280, "default_height": 720, "default_fps": 30,
}

NARROW = DbInfo("narrow", Path("narrow.bin"), 7.0, 10.0)
WIDE = DbInfo("wide", Path("wide.bin"), 18.0, 32.0)


class _FakeDb:
    def __init__(self, path):
        self.path = path


class _FakeSolver:
    """Stands in for PlateSolver: a real registry with a fake loader."""

    def __init__(self, initial_fov=8.86):
        self.databases = SolverDatabases([NARROW, WIDE], initial_fov, loader=_FakeDb)

    def apply_pending_database(self):
        return self.databases.apply_pending()


def _jpeg(w, h) -> bytes:
    buf = io.BytesIO()
    Image.new("L", (w, h), 10).save(buf, "JPEG")
    return buf.getvalue()


@pytest.fixture
def cfg(tmp_path):
    return ConfigManager(config_dir=tmp_path / "cfg")


def _profile(cfg, solver=None):
    box = {"solver": solver}
    return CameraProfile(cfg, LatestFrame(), lambda: box["solver"]), box


# ---------------------------------------------------------------------------
# resolve_fov (spec 5.2 steps 1 and 4) and the holder
# ---------------------------------------------------------------------------


class TestResolveFov:
    def test_no_profile_value_is_todays_default(self):
        r = resolve_fov(None, None)
        assert r == DEFAULT_RESOLVED_FOV == ResolvedFov(8.86, "default")
        assert r.params == LEGACY_SOLVER_PARAMS == SolverParams(8.86, 1.5)

    @pytest.mark.parametrize("source", ["user", "learned"])
    def test_profile_value_wins(self, source):
        r = resolve_fov(25.6, source)
        assert (r.fov_h_deg, r.source) == (25.6, source)
        assert r.params.fov_max_error_deg == pytest.approx(2.56)

    @pytest.mark.parametrize("source", [None, "", "bogus", 3])
    def test_a_hand_entered_value_counts_as_user(self, source):
        assert resolve_fov(25.6, source).source == "user"

    @pytest.mark.parametrize("bad", [float("nan"), float("inf"), 0, 0.0, -8.86, "25.6", True, [25.6]])
    def test_invalid_profile_value_falls_back_to_the_default(self, bad, caplog):
        with caplog.at_level(logging.WARNING, logger="evf.solver.fov"):
            r = resolve_fov(bad, "user")
        assert r == DEFAULT_RESOLVED_FOV
        assert "Ignoring the profile FOV" in caplog.text

    def test_integer_fov_is_accepted(self):
        assert resolve_fov(26, "user").fov_h_deg == 26.0


class TestSolverParamsHolder:
    def test_starts_with_the_legacy_values(self):
        h = SolverParamsHolder()
        assert h.get() == LEGACY_SOLVER_PARAMS
        assert h.resolved == DEFAULT_RESOLVED_FOV

    def test_set_replaces_the_snapshot(self):
        h = SolverParamsHolder()
        before = h.get()
        h.set(ResolvedFov(25.6, "user"))
        assert before == LEGACY_SOLVER_PARAMS  # a snapshot never changes
        assert h.get() == SolverParams(25.6, pytest.approx(2.56))
        assert h.resolved == ResolvedFov(25.6, "user")

    def test_an_invalid_value_changes_nothing(self):
        h = SolverParamsHolder()
        with pytest.raises(ValueError):
            h.set(ResolvedFov(float("nan"), "user"))
        assert h.get() == LEGACY_SOLVER_PARAMS

    def test_readers_never_see_a_torn_value(self):
        h = SolverParamsHolder()
        stop = threading.Event()
        seen = set()

        def writer():
            flip = False
            while not stop.is_set():
                h.set(ResolvedFov(25.6 if flip else 8.86, "user"))
                flip = not flip

        t = threading.Thread(target=writer)
        t.start()
        try:
            for _ in range(20000):
                p = h.get()
                seen.add((p.fov_estimate_deg, round(p.fov_max_error_deg, 3)))
        finally:
            stop.set()
            t.join()
        assert seen <= {(8.86, 1.5), (25.6, 2.56)}


class _RecordingSolver:
    def __init__(self):
        self.calls = []
        self.called = threading.Event()

    def apply_pending_database(self):
        return False

    def solve_frame(self, image_bytes, fov_estimate_deg, fov_max_error_deg):
        self.calls.append((fov_estimate_deg, fov_max_error_deg))
        self.called.set()
        return {"RA": None, "Dec": None, "Roll": None, "Matches": 0, "Prob": None,
                "all_centroids": [], "matched_centroids": [], "matched_stars": [],
                "image_size": (720, 1280)}

    is_valid = staticmethod(PlateSolver.is_valid)


class TestSolverThreadReadsTheHolder:
    def _thread(self, tmp_path, holder):
        stub = _RecordingSolver()
        fb, sm = LatestFrame(), StateMachine()
        st = SolverThread(stub, fb, PointingState(), sm, ConfigManager(config_dir=tmp_path),
                          solver_params=holder)
        sm.transition(EngineState.SYNC)
        sm.transition(EngineState.SYNC_CONFIRM)
        return stub, fb, st

    def test_a_change_takes_effect_on_the_next_frame(self, tmp_path):
        holder = SolverParamsHolder()
        stub, fb, st = self._thread(tmp_path, holder)
        fb.set(b"frame", time.monotonic(), 1)
        st.start()
        try:
            assert stub.called.wait(5)
            holder.set(ResolvedFov(25.6, "user"))
            fb.set(b"frame", time.monotonic(), 2)
            deadline = time.monotonic() + 5
            while len(stub.calls) < 2 and time.monotonic() < deadline:
                time.sleep(0.01)
        finally:
            st.stop()
        assert stub.calls[0] == (8.86, 1.5)
        assert stub.calls[1] == (25.6, pytest.approx(2.56))

    def test_without_a_holder_it_solves_the_legacy_fov(self, tmp_path):
        stub, fb, st = self._thread(tmp_path, None)
        fb.set(b"frame", time.monotonic(), 1)
        st.start()
        try:
            assert stub.called.wait(5)
        finally:
            st.stop()
        assert stub.calls[0] == (8.86, 1.5)


# ---------------------------------------------------------------------------
# Stream geometry (spec 5.1, v1 sessions)
# ---------------------------------------------------------------------------


class TestGeometryFromHello:
    def test_reads_the_default_size(self):
        assert geometry_from_hello(IDS_HELLO) == StreamGeometry(1936, 1216)

    @pytest.mark.parametrize("hello", [
        None, {}, {"default_width": 1280}, {"default_width": 0, "default_height": 720},
        {"default_width": "1280", "default_height": 720}, {"default_width": True, "default_height": 1},
        {"default_width": -1, "default_height": 720},
    ])
    def test_unusable_hello_gives_none(self, hello):
        assert geometry_from_hello(hello) is None

    def test_jpeg_size_reads_the_header(self):
        assert jpeg_size(_jpeg(968, 608)) == StreamGeometry(968, 608)
        assert jpeg_size(b"not a jpeg") is None


class TestGeometryTracker:
    def test_no_camera_is_the_legacy_size(self):
        t = GeometryTracker()
        assert t.get() == DEFAULT_GEOMETRY == StreamGeometry(1280, 720)
        assert t.awaiting_frame is False

    def test_connect_takes_the_hello_size_and_waits_for_a_frame(self):
        t = GeometryTracker()
        assert t.connect(IDS_HELLO) == StreamGeometry(1936, 1216)
        assert t.get() == StreamGeometry(1936, 1216)
        assert t.awaiting_frame is True

    def test_first_frame_corrects_the_hello_size_once(self, caplog):
        t = GeometryTracker()
        t.connect({"default_width": 1280, "default_height": 720})
        with caplog.at_level(logging.WARNING, logger="evf.camera.stream"):
            t.observe_frame(StreamGeometry(1936, 1216))
        assert t.get() == StreamGeometry(1936, 1216)
        assert "HELLO said 1280x720" in caplog.text
        assert t.awaiting_frame is False
        t.observe_frame(StreamGeometry(100, 100))  # later frames do not change it
        assert t.get() == StreamGeometry(1936, 1216)

    def test_a_frame_matching_hello_changes_nothing(self):
        t = GeometryTracker()
        t.connect(IDS_HELLO)
        t.observe_frame(StreamGeometry(1936, 1216))
        assert t.get() == StreamGeometry(1936, 1216) and not t.awaiting_frame

    def test_hello_without_size_assumes_the_default_until_the_frame(self):
        t = GeometryTracker()
        t.connect({"backend": "x"})
        assert t.get() == DEFAULT_GEOMETRY
        t.observe_frame(StreamGeometry(640, 480))
        assert t.get() == StreamGeometry(640, 480)

    def test_frames_are_ignored_while_no_camera_is_connected(self):
        t = GeometryTracker()
        t.observe_frame(StreamGeometry(640, 480))
        assert t.get() == DEFAULT_GEOMETRY

    def test_reset_goes_back_to_the_default(self):
        t = GeometryTracker()
        t.connect(IDS_HELLO)
        t.reset()
        assert t.get() == DEFAULT_GEOMETRY and not t.awaiting_frame


# ---------------------------------------------------------------------------
# CameraProfile: startup, camera connect, payload
# ---------------------------------------------------------------------------


class TestStartup:
    def test_no_last_camera_is_todays_behaviour(self, cfg):
        cp, _ = _profile(cfg)
        assert cp.restore_last() == 8.86
        assert cp.params.get() == LEGACY_SOLVER_PARAMS
        assert cp.params.resolved.source == "default"

    def test_last_camera_profile_fov_is_the_initial_fov(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        profiles.set_last_camera_key(cfg, IDS_KEY)
        cp, _ = _profile(cfg)
        assert cp.restore_last() == 25.6
        assert cp.params.get().fov_estimate_deg == 25.6

    def test_last_camera_without_a_profile_fov_is_the_default(self, cfg):
        profiles.set_last_camera_key(cfg, IDS_KEY)
        cp, _ = _profile(cfg)
        assert cp.restore_last() == 8.86

    def test_request_database_asks_the_registry(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        profiles.set_last_camera_key(cfg, IDS_KEY)
        solver = _FakeSolver(initial_fov=25.6)
        cp, _ = _profile(cfg, solver)
        cp.restore_last()
        cp.request_database()
        assert solver.databases.active.name == "wide"
        assert solver.apply_pending_database() is False  # already the right one

    def test_request_database_without_a_solver_is_harmless(self, cfg):
        cp, _ = _profile(cfg, None)
        cp.request_database()


class TestCameraConnect:
    def test_profile_fov_selects_the_wide_database(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        solver = _FakeSolver()  # narrow loaded, as after a first start
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected(IDS_HELLO)

        assert cp.active_key == IDS_KEY
        assert cp.params.get() == SolverParams(25.6, pytest.approx(2.56))
        assert solver.databases.active.name == "narrow"  # swap is for the solver thread
        assert solver.apply_pending_database() is True
        assert solver.databases.active.name == "wide"
        assert profiles.last_camera_key(cfg) == IDS_KEY

    def test_a_new_camera_gets_an_empty_profile_and_the_default_fov(self, cfg):
        solver = _FakeSolver()
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected(IDS_HELLO)
        assert cp.params.get() == LEGACY_SOLVER_PARAMS  # 8.86 / 1.5, narrow: today's behaviour
        assert cp.params.resolved.source == "default"
        assert profiles.get_profile(cfg, IDS_KEY) == {"controls": {}, "fov_h_deg": None, "fov_source": None}
        assert solver.apply_pending_database() is False
        assert solver.databases.active.name == "narrow"
        assert profiles.last_camera_key(cfg) == IDS_KEY

    def test_hello_without_camera_key_uses_backend_and_model(self, cfg):
        cp, _ = _profile(cfg, _FakeSolver())
        cp.on_camera_connected(OV_HELLO)
        assert cp.active_key == "linux-v4l2:OV9281"
        assert profiles.get_profile(cfg, "linux-v4l2:OV9281") is not None
        assert profiles.last_camera_key(cfg) == "linux-v4l2:OV9281"

    def test_switching_back_to_a_camera_without_fov_requests_the_narrow_database(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        solver = _FakeSolver()
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected(IDS_HELLO)
        solver.apply_pending_database()
        assert solver.databases.active.name == "wide"

        cp.on_camera_connected(OV_HELLO)  # another camera: its own profile, no FOV
        assert cp.params.get() == LEGACY_SOLVER_PARAMS
        assert solver.apply_pending_database() is True
        assert solver.databases.active.name == "narrow"

    def test_unidentifiable_camera_gets_the_default_and_no_profile(self, cfg, caplog):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        profiles.set_last_camera_key(cfg, IDS_KEY)
        cp, _ = _profile(cfg, _FakeSolver())
        cp.restore_last()
        with caplog.at_level(logging.WARNING, logger="evf.engine.camera_profile"):
            cp.on_camera_connected({"protocol_version": 1})
        assert cp.active_key is None
        assert cp.params.get() == LEGACY_SOLVER_PARAMS
        assert profiles.last_camera_key(cfg) == IDS_KEY  # unchanged
        assert "no camera_key" in caplog.text

    def test_a_bad_fov_in_the_config_is_ignored(self, cfg):
        raw = json.loads(cfg.path.read_text(encoding="utf-8"))
        raw["camera"]["profiles"] = {IDS_KEY: {"controls": {}, "fov_h_deg": float("nan"), "fov_source": "user"}}
        cfg.path.write_text(json.dumps(raw), encoding="utf-8")  # json writes NaN, json.load reads it
        cfg2 = ConfigManager(config_dir=cfg.path.parent)
        cp, _ = _profile(cfg2, _FakeSolver())
        cp.on_camera_connected(IDS_HELLO)
        assert cp.params.get() == LEGACY_SOLVER_PARAMS

    def test_the_connect_sets_the_geometry_from_hello(self, cfg):
        cp, _ = _profile(cfg)
        cp.on_camera_connected(IDS_HELLO)
        assert cp.geometry.get() == StreamGeometry(1936, 1216)

    def test_camera_gone_resets_geometry_but_keeps_the_fov(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        cp, _ = _profile(cfg, _FakeSolver())
        cp.on_camera_connected(IDS_HELLO)
        cp.on_camera_gone()
        assert cp.active_key is None
        assert cp.geometry.get() == DEFAULT_GEOMETRY
        assert cp.params.get().fov_estimate_deg == 25.6


class TestPayload:
    def test_no_camera_is_the_legacy_payload(self, cfg):
        cp, _ = _profile(cfg, _FakeSolver())
        cp.restore_last()
        assert cp.geometry_payload() == {
            "image_w": 1280, "image_h": 720, "fov_h_deg": 8.86, "fov_source": "default",
            "solver_db": "narrow", "fov_out_of_range": False,
        }

    def test_without_a_solver_there_is_no_database_name(self, cfg):
        cp, _ = _profile(cfg, None)
        payload = cp.geometry_payload()
        assert payload["solver_db"] is None and payload["fov_out_of_range"] is False

    def test_connected_ids_camera(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        solver = _FakeSolver()
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected(IDS_HELLO)
        solver.apply_pending_database()
        assert cp.geometry_payload() == {
            "image_w": 1936, "image_h": 1216, "fov_h_deg": 25.6, "fov_source": "user",
            "solver_db": "wide", "fov_out_of_range": False,
        }

    def test_fov_outside_every_database_sets_the_flag(self, cfg):
        profiles.set_fov(cfg, IDS_KEY, 60.0, "user")
        solver = _FakeSolver()
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected(IDS_HELLO)
        solver.apply_pending_database()
        payload = cp.geometry_payload()
        assert payload["fov_out_of_range"] is True
        assert payload["solver_db"] == "wide"  # the closest one

    def test_the_flag_follows_the_database_actually_loaded(self, cfg):
        """FOV 25.6 requested, but the narrow database is still the loaded one:
        the hint stays on until the swap has happened."""
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        solver = _FakeSolver()
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected(IDS_HELLO)
        assert cp.geometry_payload()["fov_out_of_range"] is True
        solver.apply_pending_database()
        assert cp.geometry_payload()["fov_out_of_range"] is False

    def test_first_frame_corrects_the_hello_size_in_the_payload(self, cfg):
        solver = _FakeSolver()
        box_fb = LatestFrame()
        cp = CameraProfile(cfg, box_fb, lambda: solver)
        cp.on_camera_connected({**OV_HELLO, "default_width": 1280, "default_height": 720})
        assert cp.geometry_payload()["image_w"] == 1280
        box_fb.set(_jpeg(1936, 1216), time.monotonic(), 1)
        payload = cp.geometry_payload()
        assert (payload["image_w"], payload["image_h"]) == (1936, 1216)
        box_fb.set(_jpeg(640, 480), time.monotonic(), 2)  # only the first frame counts
        assert cp.geometry_payload()["image_w"] == 1936

    def test_a_frame_without_a_camera_does_not_change_the_geometry(self, cfg):
        fb = LatestFrame()
        cp = CameraProfile(cfg, fb, lambda: None)
        fb.set(_jpeg(640, 480), time.monotonic(), 1)  # e.g. the dev sample injector
        assert cp.geometry_payload()["image_w"] == 1280

    @pytest.mark.parametrize("w,h,fov,db", [
        (1280, 720, None, "narrow"),
        (1936, 1216, 25.6, "wide"),
        (968, 608, 25.6, "wide"),
    ])
    def test_web_server_payload_follows_the_engine_objects(self, cfg, w, h, fov, db):
        """CameraProfile -> WebServer, no engine: the three geometries of task 4.9."""
        if fov is not None:
            profiles.set_fov(cfg, IDS_KEY, fov, "user")
        solver = _FakeSolver()
        cp, _ = _profile(cfg, solver)
        cp.on_camera_connected({**IDS_HELLO, "default_width": w, "default_height": h})
        solver.apply_pending_database()
        ws = WebServer(PointingState(), StateMachine(), GotoTarget(), cfg,
                       frame_buffer=LatestFrame(), camera_geometry=cp.geometry_payload)
        payload = ws._build_payload()
        assert (payload["image_w"], payload["image_h"]) == (w, h)
        assert payload["fov_h_deg"] == (fov if fov is not None else 8.86)
        assert payload["fov_source"] == ("user" if fov is not None else "default")
        assert payload["solver_db"] == db
        assert payload["fov_out_of_range"] is False


# ---------------------------------------------------------------------------
# Engine hooks (real Engine, real PlateSolver)
# ---------------------------------------------------------------------------


class _EngineSolver(_FakeSolver):
    """Records the solve call of the engine's sync step."""

    def __init__(self):
        super().__init__()
        self.calls = []

    def solve_frame(self, image_bytes, fov_estimate_deg, fov_max_error_deg):
        self.calls.append((self.databases.active.name, fov_estimate_deg, fov_max_error_deg))
        return {"RA": None, "Dec": None, "Roll": None, "Matches": 0, "Prob": None,
                "all_centroids": [], "matched_centroids": [], "matched_stars": [],
                "image_size": (1216, 1936)}


class TestEngineHooks:
    def test_startup_solver_without_last_camera_loads_the_narrow_database(self, tmp_path):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        eng.startup_solver()
        assert eng._solver.databases.active.name == "narrow"
        assert eng._camera_profile.params.get() == LEGACY_SOLVER_PARAMS

    def test_startup_solver_loads_the_database_of_the_last_camera(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        profiles.set_last_camera_key(cfg, IDS_KEY)
        eng = Engine(config=cfg)
        eng.startup_solver()
        assert eng._solver.databases.active.name == "wide"
        assert eng._solver.databases.in_range is True
        assert eng._camera_profile.params.get().fov_estimate_deg == 25.6
        assert eng._camera_profile.geometry_payload()["solver_db"] == "wide"

    def test_camera_connect_hook_loads_the_database_when_no_solver_thread_runs(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        eng = Engine(config=cfg)
        eng.startup_solver()
        assert eng._solver.databases.active.name == "narrow"  # no last camera yet
        eng._fork_camera_connected(IDS_HELLO)
        # No solver thread: the engine swaps at once, so the UI never shows the
        # "no database covers this FOV" hint for a database that is merely not loaded yet.
        assert eng._solver.databases.active.name == "wide"
        assert eng._camera_profile.geometry_payload()["fov_out_of_range"] is False

    def test_camera_connect_hook_leaves_the_swap_to_a_running_solver_thread(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        eng = Engine(config=cfg)
        eng._solver = _FakeSolver()
        eng._solver_thread = types.SimpleNamespace(running=True)
        eng._fork_camera_connected(IDS_HELLO)
        assert eng._solver.databases.active.name == "narrow"  # the solver thread's apply_pending does it
        assert eng._solver.apply_pending_database() is True
        assert eng._solver.databases.active.name == "wide"

    def test_a_failing_profile_hook_does_not_break_the_camera(self, tmp_path, caplog):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))

        def boom(_hello):
            raise RuntimeError("boom")

        eng._camera_profile.on_camera_connected = boom
        with caplog.at_level(logging.ERROR, logger="evf.engine.engine"):
            eng._fork_camera_connected(IDS_HELLO)  # must not raise
        assert "Could not activate the camera profile" in caplog.text

    def test_sync_solve_applies_the_swap_and_uses_the_profile_fov(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        eng = Engine(config=cfg)
        eng._solver = _EngineSolver()
        eng._solver_thread = types.SimpleNamespace(running=True)  # so the connect hook leaves the swap pending
        eng._fork_camera_connected(IDS_HELLO)
        assert eng._solver.databases.active.name == "narrow"
        eng._frame_buffer.set(b"frame", time.monotonic(), 1)
        eng._sync_lock.acquire()  # _perform_sync_solve releases it
        eng._perform_sync_solve()
        # The wide database was loaded before the solve, and the FOV is the profile's.
        assert eng._solver.calls == [("wide", 25.6, pytest.approx(2.56))]

    def test_solver_thread_gets_the_engines_holder(self, tmp_path):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        eng._solver = _EngineSolver()
        eng.startup_solver_thread()
        assert eng._solver_thread._solver_params is eng._camera_profile.params

    def test_failed_camera_start_resets_the_geometry(self, tmp_path, monkeypatch):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        eng._camera_profile.geometry.connect(IDS_HELLO)
        monkeypatch.setattr("evf.engine.engine._usb_video_device_present", lambda: True)

        class _Boom:
            def __init__(self, *a, **k):
                pass

            def start(self):
                raise RuntimeError("no camera")

        monkeypatch.setattr("evf.engine.engine.SubprocessManager", _Boom)
        eng.startup_camera()
        assert eng._camera_profile.geometry.get() == DEFAULT_GEOMETRY


class TestSolverThreadRunning:
    def test_running_follows_the_thread(self, tmp_path):
        stub = _RecordingSolver()
        fb, sm = LatestFrame(), StateMachine()
        st = SolverThread(stub, fb, PointingState(), sm, ConfigManager(config_dir=tmp_path))
        assert st.running is False
        fb.set(b"frame", time.monotonic(), 1)
        sm.transition(EngineState.SYNC)
        sm.transition(EngineState.SYNC_CONFIRM)
        st.start()
        try:
            assert stub.called.wait(5)
            assert st.running is True
        finally:
            st.stop()
        assert st.running is False
