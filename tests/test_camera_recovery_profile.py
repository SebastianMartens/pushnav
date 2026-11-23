# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 4 and 5).
"""The engine's camera-connect hook runs on startup and on a recovery reconnect."""

from __future__ import annotations

import logging
import time

import pytest

from evf.camera.stream import StreamGeometry
from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.state import EngineState, StateMachine
from evf.solver.fov import LEGACY_SOLVER_PARAMS
from tests.mock_camera_server import MockCameraServer
from tests.test_camera_profile import IDS_HELLO, IDS_KEY, OV_HELLO, _FakeSolver
from tests.test_subprocess_mgr import _NoopSubprocessManager


class _StartedManager:
    """Stands in for SubprocessManager: start() answers with a fixed HELLO."""

    hello: dict = {}

    def __init__(self, *args, **kwargs):
        self.client = None
        self.on_recovered = None

    def start(self):
        return self.hello


class TestStartupCameraRunsTheHook:
    def test_a_successful_start_activates_the_profile(self, tmp_path, monkeypatch):
        cfg = ConfigManager(config_dir=tmp_path)
        eng = Engine(config=cfg)
        monkeypatch.setattr("evf.engine.engine._usb_video_device_present", lambda: True)
        _StartedManager.hello = IDS_HELLO
        monkeypatch.setattr("evf.engine.engine.SubprocessManager", _StartedManager)

        eng.startup_camera()

        assert profiles.last_camera_key(cfg) == IDS_KEY
        assert eng._camera_profile.active_key == IDS_KEY
        assert eng._camera_profile.geometry.get() == StreamGeometry(1936, 1216)
        assert profiles.get_profile(cfg, IDS_KEY) is not None
        # a recovery reconnect re-runs the same hook
        assert eng._subprocess_mgr.on_recovered == eng._fork_camera_connected


def _wait_for(sm, state, timeout):
    deadline = time.monotonic() + timeout
    while sm.state != state and time.monotonic() < deadline:
        time.sleep(0.05)
    return sm.state == state


class TestRecoveryRunsTheHook:
    def test_reconnecting_to_another_camera_re_resolves_key_fov_database_and_geometry(self, tmp_path):
        """IDS (25.6 deg, 1936x1216, wide database) goes away and an OV9281
        (1280x720, no profile FOV) answers after recovery, as when the C# server
        exits with code 3 and the C server starts: everything follows the new HELLO."""
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        eng = Engine(config=cfg)
        eng._solver = _FakeSolver(initial_fov=25.6)
        server = MockCameraServer(port=0, fps=20, hello=dict(IDS_HELLO))
        server.start()
        port = server.port
        mgr = _NoopSubprocessManager(eng.frame_buffer, eng.state_machine, cfg,
                                     binary_path="/dev/null", port=port)
        mgr.on_recovered = eng._fork_camera_connected
        server2 = None
        try:
            eng._fork_camera_connected(mgr.start())
            profile = eng._camera_profile
            assert profile.active_key == IDS_KEY
            assert profile.geometry.get() == StreamGeometry(1936, 1216)
            assert profile.params.get().fov_estimate_deg == 25.6
            assert eng._solver.databases.active.name == "wide"

            server.stop()
            assert _wait_for(eng.state_machine, EngineState.RECONNECTING, 5.0)

            server2 = MockCameraServer(port=port, fps=20, hello=dict(OV_HELLO))
            server2.start()
            assert _wait_for(eng.state_machine, EngineState.SETUP, 10.0)

            assert profile.active_key == "linux-v4l2:OV9281"
            assert profiles.last_camera_key(cfg) == "linux-v4l2:OV9281"
            assert profile.geometry.get() == StreamGeometry(1280, 720)
            assert profile.params.get() == LEGACY_SOLVER_PARAMS
            assert profile.params.resolved.source == "default"
            assert eng._solver.databases.active.name == "narrow"  # no solver thread: swapped at once
            payload = profile.geometry_payload()
            assert (payload["image_w"], payload["image_h"], payload["fov_h_deg"]) == (1280, 720, 8.86)
            assert payload["solver_db"] == "narrow" and payload["fov_out_of_range"] is False
        finally:
            mgr.stop()
            server.stop()
            if server2 is not None:
                server2.stop()

    def test_a_failing_callback_does_not_fail_the_recovery(self, tmp_path, caplog):
        cfg = ConfigManager(config_dir=tmp_path)
        sm = StateMachine()
        server = MockCameraServer(port=0, fps=20)
        server.start()
        port = server.port
        mgr = _NoopSubprocessManager(LatestFrame(), sm, cfg, binary_path="/dev/null", port=port)

        def boom(_hello):
            raise RuntimeError("boom")

        mgr.on_recovered = boom
        server2 = None
        try:
            mgr.start()
            server.stop()
            assert _wait_for(sm, EngineState.RECONNECTING, 5.0)
            server2 = MockCameraServer(port=port, fps=20)
            server2.start()
            with caplog.at_level(logging.ERROR, logger="evf.camera.subprocess_mgr"):
                assert _wait_for(sm, EngineState.SETUP, 10.0)
            assert mgr.running
            assert "on_recovered callback failed" in caplog.text
        finally:
            mgr.stop()
            server.stop()
            if server2 is not None:
                server2.stop()

    def test_without_a_callback_recovery_is_unchanged(self, tmp_path):
        sm = StateMachine()
        server = MockCameraServer(port=0, fps=20)
        server.start()
        port = server.port
        mgr = _NoopSubprocessManager(LatestFrame(), sm, ConfigManager(config_dir=tmp_path),
                                     binary_path="/dev/null", port=port)
        assert mgr.on_recovered is None
        server2 = None
        try:
            mgr.start()
            server.stop()
            assert _wait_for(sm, EngineState.RECONNECTING, 5.0)
            server2 = MockCameraServer(port=port, fps=20)
            server2.start()
            assert _wait_for(sm, EngineState.SETUP, 10.0)
        finally:
            mgr.stop()
            server.stop()
            if server2 is not None:
                server2.stop()
