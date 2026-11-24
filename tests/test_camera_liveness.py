# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 3.3).
"""Frame-stall timeout per session: v1 fixed, v2 from the frame interval, paused while reconfiguring."""

from __future__ import annotations

import time

import pytest

from evf.camera import stream
from evf.camera.client import CameraClient
from evf.camera.subprocess_mgr import SubprocessManager
from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.engine.state import EngineState, StateMachine
from tests.mock_camera_server import MockCameraServer
from tests.test_subprocess_mgr import _NoopSubprocessManager


def _info(interval_us=None, state="streaming", reconfiguring_since=None):
    return stream.StreamInfo(1, state, 640, 360, 1, "JPEG", interval_us, None, reconfiguring_since)


def _wait_state(sm, state, timeout):
    deadline = time.monotonic() + timeout
    while sm.state != state and time.monotonic() < deadline:
        time.sleep(0.05)
    return sm.state == state


class TestTimeoutFormula:
    @pytest.mark.parametrize("interval_us, expected", [
        (None, 2.0),          # no STREAM_INFO interval: the v1 value
        (100_000, 2.0),       # 10 fps: 2 * 0.1 + 1 = 1.2, the floor wins
        (500_000, 2.0),       # 2 * 0.5 + 1 = 2.0
        (1_000_000, 3.0),
        (4_000_000, 9.0),     # a 4 s exposure
    ])
    def test_max_of_the_floor_and_two_intervals_plus_one_second(self, interval_us, expected):
        assert stream.stall_timeout_s(2.0, _info(interval_us)) == expected

    def test_without_stream_info_it_is_the_floor(self):
        assert stream.stall_timeout_s(2.0, None) == 2.0

    def test_the_floor_is_the_managers_v1_timeout(self):
        assert stream.stall_timeout_s(0.5, _info(100_000)) == 1.2
        assert SubprocessManager._FRAME_STALL_TIMEOUT == 2.0


class TestV2Stall:
    def test_any_message_counts_not_only_frames(self):
        stall, timeout = stream.stall_since_last_activity(now=100.0, last_activity=99.5, floor_s=2.0, info=_info(100_000))
        assert (stall, timeout) == (0.5, 2.0)

    def test_the_stall_grows_without_messages(self):
        stall, timeout = stream.stall_since_last_activity(now=100.0, last_activity=90.0, floor_s=2.0, info=_info(100_000))
        assert stall == 10.0 and stall > timeout

    def test_a_long_interval_raises_the_limit(self):
        stall, timeout = stream.stall_since_last_activity(now=100.0, last_activity=93.0, floor_s=2.0, info=_info(4_000_000))
        assert stall == 7.0 and timeout == 9.0 and stall < timeout

    def test_reconfiguring_suspends_stall_detection(self):
        info = _info(100_000, "reconfiguring", reconfiguring_since=96.0)
        stall, timeout = stream.stall_since_last_activity(now=100.0, last_activity=80.0, floor_s=2.0, info=info)
        assert stall == 0.0 and timeout == 2.0

    def test_the_suspension_ends_after_five_seconds(self):
        info = _info(100_000, "reconfiguring", reconfiguring_since=94.0)
        stall, timeout = stream.stall_since_last_activity(now=100.0, last_activity=80.0, floor_s=2.0, info=info)
        assert stall == 20.0 and stall > timeout
        assert stream.RECONFIGURE_GRACE_S == 5.0


class TestManagerPicksThePolicyPerSession:
    def test_v1_counts_frames_with_the_fixed_timeout(self, tmp_path):
        server = MockCameraServer(port=0, fps=20)
        server.start()
        client = CameraClient(LatestFrame(), port=server.port)
        try:
            client.connect(timeout=5.0)
            mgr = SubprocessManager(LatestFrame(), StateMachine(), ConfigManager(config_dir=tmp_path),
                                    binary_path="/dev/null")
            client._last_frame_time = time.monotonic() - 10.0
            client._last_message_time = time.monotonic()  # a recent message does not help a v1 session
            stall, timeout = mgr._fork_stall(client)
            assert timeout == 2.0 and stall > 9.0
        finally:
            client.stop()
            server.stop()

    def test_v2_counts_any_message(self, tmp_path):
        server = MockCameraServer(port=0, fps=20, v2=True)
        server.start()
        client = CameraClient(LatestFrame(), port=server.port)
        try:
            client.connect(timeout=5.0)
            mgr = SubprocessManager(LatestFrame(), StateMachine(), ConfigManager(config_dir=tmp_path),
                                    binary_path="/dev/null")
            client._last_frame_time = time.monotonic() - 10.0
            client._last_message_time = time.monotonic() - 0.1
            stall, timeout = mgr._fork_stall(client)
            assert stall < 0.5 and timeout == 2.0
        finally:
            client.stop()
            server.stop()


@pytest.fixture()
def state_machine():
    return StateMachine()


def _manager(state_machine, port, tmp_path):
    return _NoopSubprocessManager(LatestFrame(), state_machine, ConfigManager(config_dir=tmp_path),
                                  binary_path="/dev/null", port=port)


class TestAgainstSlowFrames:
    def test_four_second_frames_do_not_restart_a_v2_session(self, state_machine, tmp_path):
        """The mock sends a frame every 4 s (STREAM_INFO says so). The test manager's floor is 0.5 s,
        which would restart a v1 session after half a second; v2 allows 2 * 4 + 1 = 9 s."""
        server = MockCameraServer(port=0, fps=0.25, v2=True)
        server.start()
        mgr = _manager(state_machine, server.port, tmp_path)
        try:
            mgr.start()
            assert mgr.client.session_version == 2
            # frame 1 at once, frame 2 after 4 s: a v1 timeout would have fired 7 times by then
            time.sleep(5.0)
            assert state_machine.state != EngineState.RECONNECTING
            assert mgr.running
            assert mgr.client.last_frame_time > 0
        finally:
            mgr.stop()
            server.stop()

    def test_a_v1_session_with_slow_frames_still_restarts_at_the_fixed_timeout(self, state_machine, tmp_path):
        """Task 1.7: v1 keeps the fixed timeout, whatever the frame rate."""
        server = MockCameraServer(port=0, fps=0.5)  # a frame every 2 s
        server.start()
        mgr = _manager(state_machine, server.port, tmp_path)
        try:
            mgr.start()
            assert mgr.client.session_version == 1
            assert _wait_state(state_machine, EngineState.RECONNECTING, 4.0)
        finally:
            mgr.stop()
            server.stop()

    def test_a_reconfiguration_that_takes_a_while_does_not_restart_the_session(self, state_machine, tmp_path):
        server = MockCameraServer(port=0, fps=20, v2=True, v2_options={"reconfigure_s": 2.5})
        server.start()
        mgr = _manager(state_machine, server.port, tmp_path)
        try:
            mgr.start()
            time.sleep(0.3)
            mgr.client.set_control("binning", 2)
            # no frames for 2.5 s, more than the 1.2 s timeout (2 * 0.1 + 1.0, the mock caps fps at 10), but "reconfiguring"
            time.sleep(1.6)
            assert mgr.client.stream_info.reconfiguring
            time.sleep(2.0)
            assert state_machine.state != EngineState.RECONNECTING
            assert mgr.client.stream_info.generation == 2
            before = mgr.client.last_frame_time
            time.sleep(0.4)
            assert mgr.client.last_frame_time > before  # frames of the new size flow again
        finally:
            mgr.stop()
            server.stop()

    def test_a_reconfiguration_that_never_ends_is_a_stall_after_the_grace_time(
            self, state_machine, tmp_path, monkeypatch):
        monkeypatch.setattr(stream, "RECONFIGURE_GRACE_S", 1.0)
        server = MockCameraServer(port=0, fps=20, v2=True, v2_options={"reconfigure_s": 30.0})
        server.start()
        mgr = _manager(state_machine, server.port, tmp_path)
        try:
            mgr.start()
            time.sleep(0.3)
            mgr.client.set_control("binning", 2)
            assert _wait_state(state_machine, EngineState.RECONNECTING, 6.0)
        finally:
            mgr.stop()
            server.stop(timeout=0.5)


class TestStallLog:
    def test_a_v1_session_logs_the_upstream_text(self, caplog):
        class V1:
            session_version = 1

        with caplog.at_level("WARNING", logger="evf.camera.subprocess_mgr"):
            SubprocessManager._fork_log_stall(V1(), 2.5, 2.0)
        assert "No frames for 2.5s — treating as stall, starting recovery" in caplog.text

    def test_a_v2_session_says_messages_and_the_limit(self, caplog):
        class V2:
            session_version = 2

        with caplog.at_level("WARNING", logger="evf.camera.subprocess_mgr"):
            SubprocessManager._fork_log_stall(V2(), 10.0, 9.0)
        assert "No message from the camera for 10.0s (limit 9.0s)" in caplog.text
