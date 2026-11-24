# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.1).
"""A binning change: geometry follows STREAM_INFO, results of the old frame size are dropped."""

from __future__ import annotations

import time
from unittest.mock import MagicMock

import pytest

from evf.camera.stream import FrameSizeGuard, StreamGeometry
from evf.config.manager import ConfigManager
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.pointing import PointingState
from evf.engine.state import EngineState, StateMachine
from evf.solver.solver import PlateSolver
from evf.solver.thread import SolverThread
from tests.fov_fakes import wait_for
from tests.mock_camera_server import MockCameraServer
from tests.test_subprocess_mgr import _NoopSubprocessManager

OLD_SIZE = (720, 1280)  # (height, width) as in a solve result
NEW_SIZE = (360, 640)
NEW_GEOMETRY = StreamGeometry(640, 360, 2, 2)  # what STREAM_INFO announced


class _SizedSolver:
    """Solves every frame; the frame bytes say which size the 'image' had."""

    is_valid = staticmethod(PlateSolver.is_valid)

    def __init__(self):
        self.calls = 0

    def apply_pending_database(self):
        pass

    def solve_frame(self, image_bytes, fov_estimate_deg, fov_max_error_deg, database=None):
        self.calls += 1
        size = OLD_SIZE if image_bytes == b"old" else NEW_SIZE
        return {"RA": 10.0, "Dec": 20.0, "Roll": 30.0, "Matches": 12, "Prob": 1e-9, "fov_deg": 8.86,
                "all_centroids": [[1, 2]], "matched_centroids": [[1, 2]], "matched_stars": [],
                "image_size": size}


def _thread(tmp_path, announced, audio=None):
    fb, pointing, sm = LatestFrame(), PointingState(), StateMachine()
    sm.transition(EngineState.SYNC)
    sm.transition(EngineState.SYNC_CONFIRM)
    thread = SolverThread(_SizedSolver(), fb, pointing, sm, ConfigManager(config_dir=tmp_path),
                          audio=audio, frame_size_guard=FrameSizeGuard(announced) if announced else None)
    return thread, fb, pointing, sm


def _feed(fb, payload, first_id, count=3):
    for i in range(count):
        fb.set(payload, time.monotonic(), first_id + i)
        time.sleep(0.03)


class TestSolverThreadDropsMismatchedResults:
    def test_a_result_of_the_old_size_is_dropped_without_a_failure(self, tmp_path):
        audio = MagicMock()
        thread, fb, pointing, sm = _thread(tmp_path, lambda: NEW_GEOMETRY, audio)
        thread.start()
        try:
            _feed(fb, b"old", 1)
            assert wait_for(lambda: thread._solver.calls >= 1)
            time.sleep(0.2)
            assert thread.consecutive_failures == 0
            audio.on_failure_count_changed.assert_not_called()
            assert pointing.read().valid is False, "a result of the wrong size must not update the pointing"
            assert sm.state == EngineState.WARMING_UP
        finally:
            thread.stop()

    def test_after_the_switch_frames_of_the_new_size_are_solved(self, tmp_path):
        thread, fb, pointing, sm = _thread(tmp_path, lambda: NEW_GEOMETRY)
        thread.start()
        try:
            _feed(fb, b"old", 1)
            _feed(fb, b"new", 10)
            assert wait_for(lambda: sm.state == EngineState.TRACKING)
            assert pointing.read().image_size == NEW_SIZE
            assert thread.consecutive_failures == 0
        finally:
            thread.stop()

    def test_a_dropped_result_is_not_a_failure_even_after_many_of_them(self, tmp_path):
        audio = MagicMock()
        thread, fb, _pointing, _sm = _thread(tmp_path, lambda: NEW_GEOMETRY, audio)
        thread.start()
        try:
            _feed(fb, b"old", 1, count=10)
            assert wait_for(lambda: thread._solver.calls >= 8)
            assert thread.consecutive_failures == 0
            audio.on_failure_count_changed.assert_not_called()
        finally:
            thread.stop()

    def test_a_v1_session_has_no_expected_size_and_nothing_is_dropped(self, tmp_path):
        thread, fb, _pointing, sm = _thread(tmp_path, lambda: None)
        thread.start()
        try:
            _feed(fb, b"old", 1)
            assert wait_for(lambda: sm.state == EngineState.TRACKING)
        finally:
            thread.stop()

    def test_a_standalone_thread_without_the_hook_is_unchanged(self, tmp_path):
        thread, fb, _pointing, sm = _thread(tmp_path, None)
        thread.start()
        try:
            _feed(fb, b"old", 1)
            assert wait_for(lambda: sm.state == EngineState.TRACKING)
        finally:
            thread.stop()

    def test_a_failing_geometry_source_does_not_stop_the_solver(self, tmp_path):
        def boom():
            raise RuntimeError("boom")

        thread, fb, _pointing, sm = _thread(tmp_path, boom)
        thread.start()
        try:
            _feed(fb, b"old", 1)
            assert wait_for(lambda: sm.state == EngineState.TRACKING)
        finally:
            thread.stop()


class TestFrameSizeGuard:
    def test_a_result_of_the_announced_size_is_accepted(self):
        guard = FrameSizeGuard(lambda: NEW_GEOMETRY)
        assert guard.accepts({"image_size": NEW_SIZE}) is True

    def test_a_result_of_another_size_is_rejected(self):
        guard = FrameSizeGuard(lambda: NEW_GEOMETRY)
        assert guard.accepts({"image_size": OLD_SIZE}) is False

    def test_without_an_announced_geometry_everything_is_accepted(self):
        guard = FrameSizeGuard(lambda: None)
        assert guard.accepts({"image_size": OLD_SIZE}) is True
        assert guard.accepts({}) is True

    def test_a_result_without_a_size_is_accepted(self):
        assert FrameSizeGuard(lambda: NEW_GEOMETRY).accepts({"RA": 1.0}) is True

    def test_a_persistent_mismatch_is_logged_once_at_error(self, caplog):
        guard = FrameSizeGuard(lambda: NEW_GEOMETRY)
        with caplog.at_level("DEBUG", logger="evf.camera.stream"):
            for _ in range(12):
                assert guard.accepts({"image_size": OLD_SIZE}) is False
        errors = [r for r in caplog.records if r.levelname == "ERROR"]
        assert len(errors) == 1 and "5 results in a row dropped" in errors[0].getMessage()
        assert "1280x720" in errors[0].getMessage() and "640x360" in errors[0].getMessage()

    def test_a_few_dropped_results_around_a_switch_are_not_an_error(self, caplog):
        guard = FrameSizeGuard(lambda: NEW_GEOMETRY)
        with caplog.at_level("DEBUG", logger="evf.camera.stream"):
            for _ in range(4):
                guard.accepts({"image_size": OLD_SIZE})
            guard.accepts({"image_size": NEW_SIZE})
            for _ in range(4):
                guard.accepts({"image_size": OLD_SIZE})
        assert not [r for r in caplog.records if r.levelname == "ERROR"]

    def test_each_streak_reports_again(self, caplog):
        guard = FrameSizeGuard(lambda: NEW_GEOMETRY)
        with caplog.at_level("ERROR", logger="evf.camera.stream"):
            for _ in range(5):
                guard.accepts({"image_size": OLD_SIZE})
            guard.accepts({"image_size": NEW_SIZE})
            for _ in range(5):
                guard.accepts({"image_size": OLD_SIZE})
        assert len([r for r in caplog.records if r.levelname == "ERROR"]) == 2


class TestEngineFollowsStreamInfo:
    @pytest.fixture()
    def session(self, tmp_path):
        server = MockCameraServer(port=0, fps=20, v2=True)
        server.start()
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        mgr = _NoopSubprocessManager(eng.frame_buffer, eng.state_machine, eng._config,
                                     binary_path="/dev/null", port=server.port)
        eng._subprocess_mgr = mgr
        hello = mgr.start()
        eng._fork_camera_connected(hello)
        yield eng, mgr, server
        mgr.stop()
        server.stop()

    def test_the_geometry_is_the_stream_info_of_a_v2_session(self, session):
        eng, mgr, _server = session
        profile = eng._camera_profile
        assert profile.geometry.get() == StreamGeometry(1280, 720, 1, 1)
        assert profile.geometry.authoritative() == StreamGeometry(1280, 720, 1, 1)
        assert profile.geometry.awaiting_frame is False  # no "first frame corrects HELLO" in v2

    def test_binning_changes_the_geometry_the_web_server_reports(self, session):
        eng, mgr, _server = session
        mgr.client.set_control("binning", 2)
        assert wait_for(lambda: eng._camera_profile.geometry.get().generation == 2)
        payload = eng._camera_profile.geometry_payload()
        assert (payload["image_w"], payload["image_h"]) == (640, 360)
        assert eng._camera_profile.geometry.authoritative() == NEW_GEOMETRY

    def test_a_new_generation_clears_the_centroids_but_not_the_pointing(self, session):
        eng, mgr, _server = session
        eng._pointing_state.update(10.0, 20.0, 30.0, 12, 1e-9, all_centroids=[[1, 2]],
                                   matched_centroids=[[1, 2]], image_size=(720, 1280))
        mgr.client.set_control("binning", 2)
        assert wait_for(lambda: eng._pointing_state.read().all_centroids is None)
        snapshot = eng._pointing_state.read()
        assert snapshot.valid is True and (snapshot.ra_j2000, snapshot.dec_j2000) == (10.0, 20.0)
        assert snapshot.matched_centroids is None and snapshot.image_size is None

    def test_an_interval_change_leaves_the_centroids_alone(self, session):
        eng, mgr, _server = session
        eng._pointing_state.update(10.0, 20.0, 30.0, 12, 1e-9, all_centroids=[[1, 2]],
                                   image_size=(720, 1280))
        mgr.client.set_control("fps", 5.0)
        assert wait_for(lambda: mgr.client.stream_info.frame_interval_us == 200000)
        assert eng._pointing_state.read().all_centroids == [[1, 2]]

    def test_a_v1_session_has_no_expected_size_and_keeps_the_hello_geometry(self, tmp_path):
        server = MockCameraServer(port=0, fps=20)
        server.start()
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        mgr = _NoopSubprocessManager(eng.frame_buffer, eng.state_machine, eng._config,
                                     binary_path="/dev/null", port=server.port)
        eng._subprocess_mgr = mgr
        try:
            eng._fork_camera_connected(mgr.start())
            profile = eng._camera_profile
            assert profile.geometry.authoritative() is None
            assert profile.geometry.get() == StreamGeometry(1280, 720)
            assert profile.geometry.awaiting_frame is True  # still corrected by the first decoded frame
        finally:
            mgr.stop()
            server.stop()

    def test_losing_the_camera_forgets_the_stream(self, session):
        eng, _mgr, _server = session
        eng._camera_profile.on_camera_gone()
        assert eng._camera_profile.geometry.authoritative() is None
        assert eng._camera_profile.geometry.get() == StreamGeometry(1280, 720)

    def test_recovery_re_registers_the_stream_callback_on_the_new_client(self, tmp_path):
        server = MockCameraServer(port=0, fps=20, v2=True)
        server.start()
        port = server.port
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        mgr = _NoopSubprocessManager(eng.frame_buffer, eng.state_machine, eng._config,
                                     binary_path="/dev/null", port=port)
        mgr.on_recovered = eng._fork_camera_connected
        eng._subprocess_mgr = mgr
        server2 = None
        try:
            eng._fork_camera_connected(mgr.start())
            first_client = mgr.client
            server.stop()
            assert wait_for(lambda: eng.state_machine.state == EngineState.RECONNECTING, 5.0)
            server2 = MockCameraServer(port=port, fps=20, v2=True)
            server2.start()
            assert wait_for(lambda: eng.state_machine.state == EngineState.SETUP, 10.0)
            assert mgr.client is not first_client
            eng._pointing_state.update(1.0, 2.0, 3.0, 12, 1e-9, all_centroids=[[1, 2]])
            mgr.client.set_control("binning", 2)
            assert wait_for(lambda: eng._pointing_state.read().all_centroids is None)
            assert eng._camera_profile.geometry.get() == StreamGeometry(640, 360, 2, 2)
        finally:
            mgr.stop()
            server.stop()
            if server2 is not None:
                server2.stop()
