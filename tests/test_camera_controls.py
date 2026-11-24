# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 3.4 and 4.2).
"""Saved camera controls: restore order, unit filter, first-run values, legacy migration, saving."""

from __future__ import annotations

import time

import pytest

from evf.camera import controls
from evf.camera.client import CameraClient
from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from tests.mock_camera_server import MockCameraServer

KEY = "ids:1409:3060"


def _exposure_v2(**extra):
    return {"id": "exposure", "label": "Exposure", "type": "int", "unit": "us",
            "min": 100, "max": 4_000_000, "step": 1, "cur": 100, **extra}


def _fps(**extra):
    return {"id": "fps", "label": "Frame rate", "type": "float", "unit": "fps",
            "min": 1.0, "max": 10.0, "step": 0.01, "cur": 5.0, **extra}


def _gain(**extra):
    return {"id": "gain", "label": "Gain", "type": "int", "unit": "raw",
            "min": 0, "max": 100, "step": 1, "cur": 0, **extra}


def _binning(**extra):
    return {"id": "binning", "label": "Binning", "type": "enum", "cur": 1,
            "options": [{"value": 1, "label": "1x1"}, {"value": 2, "label": "2x2"}],
            "restarts_stream": True, **extra}


def _v1_exposure(unit="100us"):
    return {"id": "exposure", "label": "Exposure", "type": "int", "unit": unit,
            "min": 1, "max": 15000, "step": 1, "cur": 1}


def _first_run(info):
    return controls.ControlDescriptor.from_info(info).first_run_value()


class FakeClient:
    """What ``restore_controls`` and ``remember_control`` need from a CameraClient."""

    def __init__(self, descriptors, version=2, key=KEY, accept=True):
        self.controls = descriptors
        self.session_version = version
        self.camera_key = key
        self.sent: list[tuple[str, object]] = []
        self.cached: list[tuple[str, object]] = []
        self._accept = accept
        self.stream_info = None
        self.on_stream_info = lambda callback: None

    def set_control(self, control_id, value):
        if self._accept:
            self.sent.append((control_id, value))
        return self._accept

    def update_cached_control(self, control_id, value):
        self.cached.append((control_id, value))


@pytest.fixture()
def config(tmp_path):
    return ConfigManager(config_dir=tmp_path)


def _all_v2():
    return [_gain(), _exposure_v2(), _binning(), _fps()]  # deliberately not in restore order


# -- restore order (task 2.2) -------------------------------------------------------


class TestRestoreOrder:
    def test_binning_fps_exposure_gain(self, config):
        client = FakeClient(_all_v2())
        controls.restore_controls(client, config)
        assert [c for c, _ in client.sent] == ["binning", "fps", "exposure", "gain"]

    def test_other_ids_come_after_the_known_ones_in_the_cameras_order(self, config):
        extra_a = {"id": "contrast", "label": "C", "type": "int", "min": 0, "max": 10, "cur": 1}
        extra_b = {"id": "gamma", "label": "G", "type": "int", "min": 0, "max": 4, "cur": 1}
        client = FakeClient([extra_a, _gain(), extra_b, _binning()])
        controls.restore_controls(client, config)
        assert [c for c, _ in client.sent] == ["binning", "gain", "contrast", "gamma"]

    def test_only_advertised_ids_are_sent(self, config):
        client = FakeClient([_gain(), _exposure_v2()])
        profiles.save_control(config, KEY, "fps", 4.0, "fps")  # saved, but this camera has no fps
        profiles.save_control(config, KEY, "binning", 2)
        controls.restore_controls(client, config)
        assert {c for c, _ in client.sent} == {"gain", "exposure"}

    def test_a_v1_session_restores_only_exposure_and_gain(self, config):
        other = {"id": "contrast", "label": "C", "type": "int", "min": 0, "max": 10, "cur": 1}
        client = FakeClient([other, _gain(), _v1_exposure()], version=1)
        controls.restore_controls(client, config)
        assert [c for c, _ in client.sent] == ["exposure", "gain"]

    def test_the_cache_is_updated_with_what_was_sent(self, config):
        client = FakeClient([_gain()])
        controls.restore_controls(client, config)
        assert client.cached == client.sent == [("gain", 50)]

    def test_no_client_and_no_controls_are_fine(self, config):
        assert controls.restore_controls(None, config) == []
        assert controls.restore_controls(FakeClient([]), config) == []

    def test_a_control_that_could_not_be_sent_is_not_remembered(self, config):
        client = FakeClient([_gain()], accept=False)
        assert controls.restore_controls(client, config) == []
        assert profiles.get_saved_control(config, KEY, "gain") is None

    def test_a_failing_control_does_not_stop_the_others(self, config, caplog):
        client = FakeClient([{"id": "fps", "type": "float", "min": "x"}, _gain()])
        sent = controls.restore_controls(client, config)
        assert [c for c, _ in sent] == ["gain"]


# -- the unit filter (task 2.2, spec 4.2) ----------------------------------------------


class TestUnitFilter:
    def test_a_saved_value_with_the_advertised_unit_is_restored(self, config):
        profiles.save_control(config, KEY, "exposure", 250_000, "us")
        profiles.save_control(config, KEY, "fps", 4.0, "fps")
        profiles.save_control(config, KEY, "gain", 20, "raw")
        profiles.save_control(config, KEY, "binning", 2)
        client = FakeClient(_all_v2())
        controls.restore_controls(client, config)
        assert client.sent == [("binning", 2), ("fps", 4.0), ("exposure", 250_000), ("gain", 20)]

    def test_a_saved_value_in_another_unit_is_dropped_and_logged(self, config, caplog):
        profiles.save_control(config, KEY, "exposure", 2500, "100us")  # from a v1 session of the same camera
        client = FakeClient([_exposure_v2(default=100_000)])
        with caplog.at_level("INFO", logger="evf.camera.controls"):
            controls.restore_controls(client, config)
        assert client.sent == [("exposure", 100_000)]  # the default, not 2500 us
        assert "does not match the camera's unit" in caplog.text

    def test_the_other_units_entry_stays_in_the_profile(self, config):
        profiles.save_control(config, KEY, "exposure", 2500, "100us")
        controls.restore_controls(FakeClient([_exposure_v2(default=100_000)]), config)
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 2500, "unit": "100us"}

    def test_the_unit_filter_also_separates_c_server_log2s_from_100us(self, config):
        profiles.save_control(config, "windows-c:CAM", "exposure", -6, "log2s")
        client = FakeClient([_v1_exposure("100us")], version=1, key="windows-c:CAM")
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 7500)]  # midpoint of [1, 15000]

    def test_a_control_without_a_unit_matches_a_saved_entry_without_one(self, config):
        profiles.save_control(config, KEY, "binning", 2)
        client = FakeClient([_binning()])
        controls.restore_controls(client, config)
        assert client.sent == [("binning", 2)]

    def test_a_saved_enum_value_that_is_not_an_option_is_dropped(self, config):
        profiles.save_control(config, KEY, "binning", 4)
        client = FakeClient([_binning()])
        controls.restore_controls(client, config)
        assert client.sent == [("binning", 1)]  # first option

    def test_a_hand_edited_entry_that_is_not_a_number_is_dropped(self, config):
        config.set("camera", "profiles", {KEY: {"controls": {"gain": {"value": "loud", "unit": "raw"}},
                                               "fov_h_deg": None, "fov_source": None}})
        client = FakeClient([_gain()])
        controls.restore_controls(client, config)
        assert client.sent == [("gain", 50)]

    def test_saved_values_are_coerced_to_the_descriptors_type(self, config):
        profiles.save_control(config, KEY, "gain", 20.0, "raw")
        profiles.save_control(config, KEY, "fps", 4, "fps")
        client = FakeClient([_gain(), _fps()])
        controls.restore_controls(client, config)
        sent = dict(client.sent)
        assert sent["gain"] == 20 and isinstance(sent["gain"], int)
        assert sent["fps"] == 4.0 and isinstance(sent["fps"], float)


# -- first run (spec 4.2) ---------------------------------------------------------------


class TestFirstRun:
    def test_linear_midpoint_of_an_int_control(self):
        assert _first_run(_gain()) == 50
        assert _first_run(_gain(min=1, max=4)) == 2  # floor, as the engine always did

    def test_linear_midpoint_of_a_float_control(self):
        assert _first_run(_fps()) == 5.5

    def test_geometric_mean_of_a_log_control(self):
        assert _first_run(_exposure_v2(scale="log", min=100, max=10_000)) == 1000
        value = _first_run(_fps(scale="log", min=1.0, max=100.0))
        assert value == pytest.approx(10.0) and isinstance(value, float)

    def test_a_log_control_with_a_non_positive_bound_falls_back_to_linear(self):
        assert _first_run(_gain(scale="log", min=0, max=100)) == 50

    def test_first_option_of_an_enum(self):
        assert _first_run(_binning()) == 1
        assert _first_run(_binning(options=[{"value": 4, "label": "4"}, {"value": 2}])) == 4
        assert _first_run(_binning(options=[])) is None

    def test_the_descriptors_default_wins_over_the_midpoint(self):
        assert _first_run(_exposure_v2(default=250_000, scale="log")) == 250_000
        assert _first_run(_binning(default=2)) == 2

    def test_a_default_that_is_not_valid_is_ignored(self):
        assert _first_run(_binning(default=3)) == 1
        assert _first_run(_gain(default="high")) == 50

    def test_a_descriptor_without_a_usable_range_gets_nothing(self):
        assert _first_run({"id": "x", "type": "int", "min": None, "max": 5}) is None

    def test_first_run_values_are_sent_and_saved_in_the_profile(self, config):
        client = FakeClient(_all_v2())
        controls.restore_controls(client, config)
        assert profiles.get_saved_control(config, KEY, "gain") == {"value": 50, "unit": "raw"}
        assert profiles.get_saved_control(config, KEY, "binning") == {"value": 1}
        assert profiles.get_saved_control(config, KEY, "fps") == {"value": 5.5, "unit": "fps"}
        # the second start restores them, it does not choose again
        again = FakeClient([_gain(min=0, max=10)])
        controls.restore_controls(again, config)
        assert again.sent == [("gain", 50)]

    def test_a_v2_session_leaves_the_legacy_keys_alone(self, config):
        controls.restore_controls(FakeClient(_all_v2()), config)
        assert config.exposure is None and config.gain is None

    def test_a_camera_without_a_key_gets_first_run_values_but_no_profile(self, config):
        client = FakeClient([_gain()], key=None)
        controls.restore_controls(client, config)
        assert client.sent == [("gain", 50)]
        assert config.get("camera", "profiles") == {}


# -- legacy migration (spec 4.2 first run, case 1) --------------------------------------


class TestLegacyMigration:
    def test_a_v1_session_uses_the_legacy_values_once_then_stores_them_in_the_profile(self, config):
        config.exposure, config.gain = 4200, 55
        client = FakeClient([_v1_exposure(), _gain()], version=1)
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 4200), ("gain", 55)]
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 4200, "unit": "100us"}
        assert profiles.get_saved_control(config, KEY, "gain") == {"value": 55, "unit": "raw"}

    def test_a_v2_session_does_not_use_a_legacy_value(self, config):
        config.exposure = 4200  # in 100 us, a v1 number
        client = FakeClient([_exposure_v2(default=100_000)], version=2)
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 100_000)]

    def test_in_a_v1_session_the_legacy_value_wins_over_the_profile(self, config):
        config.exposure = 1
        profiles.save_control(config, KEY, "exposure", 900, "100us")
        client = FakeClient([_v1_exposure()], version=1)
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 1)]
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 1, "unit": "100us"}  # kept in step

    def test_the_profile_value_is_used_when_there_is_no_legacy_value(self, config):
        profiles.save_control(config, KEY, "exposure", 900, "100us")
        client = FakeClient([_v1_exposure()], version=1)
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 900)]

    def test_a_profile_value_in_another_unit_stays_while_the_legacy_value_is_used(self, config):
        config.exposure = 700
        profiles.save_control(config, KEY, "exposure", 250_000, "us")  # saved in a v2 session
        client = FakeClient([_v1_exposure()], version=1)
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 700)]
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 250_000, "unit": "us"}

    def test_a_control_changed_while_the_camera_is_away_is_what_the_next_restore_sends(self, config):
        """The repro of the review: set while connected, set during a recovery (no client), then restore."""
        client = FakeClient([_v1_exposure(), _gain()], version=1)
        controls.restore_controls(client, config)  # first run: midpoints
        controls.remember_control(config, client, "exposure", 300, True)  # the user sets 300 while connected
        controls.remember_control(config, None, "exposure", 900, False)  # and 900 while the camera is away
        assert config.exposure == 900
        again = FakeClient([_v1_exposure(), _gain()], version=1)
        controls.restore_controls(again, config)
        assert dict(again.sent)["exposure"] == 900
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 900, "unit": "100us"}

    def test_a_change_whose_send_failed_is_what_the_next_restore_sends(self, config):
        client = FakeClient([_v1_exposure()], version=1, accept=False)
        controls.remember_control(config, client, "exposure", 800, False)
        again = FakeClient([_v1_exposure()], version=1)
        controls.restore_controls(again, config)
        assert again.sent == [("exposure", 800)]

    def test_a_v2_session_has_no_legacy_authority(self, config):
        config.exposure = 5
        profiles.save_control(config, KEY, "exposure", 200_000, "us")
        client = FakeClient([_exposure_v2()], version=2)
        controls.restore_controls(client, config)
        assert client.sent == [("exposure", 200_000)]

    def test_a_v1_first_run_stores_the_midpoint_in_the_legacy_keys_as_before(self, config):
        client = FakeClient([_v1_exposure(), _gain()], version=1)
        controls.restore_controls(client, config)
        assert (config.exposure, config.gain) == (7500, 50)

    def test_a_v1_session_without_a_key_still_uses_and_keeps_the_legacy_keys(self, config):
        config.gain = 12
        client = FakeClient([_gain()], version=1, key=None)
        controls.restore_controls(client, config)
        assert client.sent == [("gain", 12)]


# -- saving (task 3.2) ------------------------------------------------------------------


class TestRememberControl:
    def test_a_v2_session_saves_the_value_with_its_unit_in_the_profile_only(self, config):
        client = FakeClient(_all_v2())
        controls.remember_control(config, client, "exposure", 300_000, True)
        controls.remember_control(config, client, "fps", 3.5, True)
        controls.remember_control(config, client, "binning", 2, True)
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 300_000, "unit": "us"}
        assert profiles.get_saved_control(config, KEY, "fps") == {"value": 3.5, "unit": "fps"}
        assert profiles.get_saved_control(config, KEY, "binning") == {"value": 2}
        assert config.exposure is None  # legacy keys are for v1 sessions (downgrade safety)

    def test_a_v1_session_also_writes_the_legacy_keys(self, config):
        client = FakeClient([_v1_exposure(), _gain()], version=1)
        controls.remember_control(config, client, "exposure", 333, True)
        controls.remember_control(config, client, "gain", 44, True)
        assert (config.exposure, config.gain) == (333, 44)
        assert profiles.get_saved_control(config, KEY, "exposure") == {"value": 333, "unit": "100us"}

    def test_without_a_camera_the_legacy_keys_are_written_as_before(self, config):
        controls.remember_control(config, None, "gain", 9, True)
        controls.remember_control(config, None, "exposure", 8, True)
        assert (config.gain, config.exposure) == (9, 8)
        assert config.get("camera", "profiles") == {}

    def test_a_v2_session_that_could_not_send_saves_nothing(self, config):
        controls.remember_control(config, FakeClient([_gain()]), "gain", 5, False)
        assert profiles.get_saved_control(config, KEY, "gain") is None
        assert config.gain is None

    def test_a_value_that_was_not_sent_is_not_saved_in_the_profile(self, config):
        controls.remember_control(config, FakeClient([_gain()], version=1), "gain", 5, False)
        assert profiles.get_saved_control(config, KEY, "gain") is None
        assert config.gain == 5  # the legacy key keeps today's behaviour

    def test_an_id_the_camera_does_not_advertise_is_not_saved_in_the_profile(self, config):
        controls.remember_control(config, FakeClient([_gain()]), "fps", 2.0, True)
        assert profiles.get_saved_control(config, KEY, "fps") is None

    def test_the_file_is_written_only_when_the_entry_changes(self, config, monkeypatch):
        client = FakeClient([_gain()])
        controls.remember_control(config, client, "gain", 5, True)
        saves = []
        monkeypatch.setattr(config, "save", lambda: saves.append(1))
        controls.remember_control(config, client, "gain", 5, True)
        assert saves == []
        controls.remember_control(config, client, "gain", 6, True)
        assert len(saves) == 1

    def test_a_saved_value_is_what_the_next_restore_sends(self, config):
        controls.remember_control(config, FakeClient(_all_v2()), "exposure", 123_456, True)
        client = FakeClient(_all_v2())
        controls.restore_controls(client, config)
        assert ("exposure", 123_456) in client.sent


class TestProfileHelpers:
    def test_save_and_get_round_trip(self, config):
        profiles.save_control(config, KEY, "gain", 5, "raw")
        assert profiles.get_saved_control(config, KEY, "gain") == {"value": 5, "unit": "raw"}
        assert profiles.get_saved_control(config, KEY, "fps") is None
        assert profiles.get_saved_control(config, "other:key", "gain") is None

    def test_saving_creates_the_profile_and_keeps_the_fov(self, config):
        profiles.set_fov(config, KEY, 25.6, "user")
        profiles.save_control(config, KEY, "gain", 5, "raw")
        assert profiles.get_fov(config, KEY) == (25.6, "user")

    def test_saved_controls_survive_a_reload(self, tmp_path):
        first = ConfigManager(config_dir=tmp_path)
        profiles.save_control(first, KEY, "exposure", 200_000, "us")
        again = ConfigManager(config_dir=tmp_path)
        assert profiles.get_saved_control(again, KEY, "exposure") == {"value": 200_000, "unit": "us"}

    def test_an_entry_that_is_not_an_object_is_not_saved_data(self, config):
        config.set("camera", "profiles", {KEY: {"controls": {"gain": 5, "fps": {"unit": "fps"}}}})
        assert profiles.get_saved_control(config, KEY, "gain") is None
        assert profiles.get_saved_control(config, KEY, "fps") is None


# -- with the real client and the mock ---------------------------------------------------


def _wait(predicate, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False


class TestAgainstTheMock:
    def test_a_v2_camera_gets_binning_fps_exposure_gain_in_that_order(self, config):
        server = MockCameraServer(port=0, fps=20, v2=True)
        server.start()
        client = CameraClient(LatestFrame(), port=server.port)
        try:
            client.connect(timeout=5.0)
            client.start_receiving()
            controls.restore_controls(client, config)
            assert _wait(lambda: len(server.set_control_log) == 4)
            assert [c["id"] for c in server.set_control_log] == ["binning", "fps", "exposure", "gain"]
            assert server.set_control_log[2]["value"] == 100_000  # the descriptor's default
            key = client.camera_key
            assert profiles.get_saved_control(config, key, "exposure") == {"value": 100_000, "unit": "us"}
        finally:
            client.stop()
            server.stop()

    def test_a_saved_binning_comes_back_with_its_frame_size(self, config):
        server = MockCameraServer(port=0, fps=20, v2=True)
        server.start()
        profiles.save_control(config, "mock:v2", "binning", 2)
        client = CameraClient(LatestFrame(), port=server.port)
        try:
            client.connect(timeout=5.0)
            client.start_receiving()
            controls.restore_controls(client, config)
            assert _wait(lambda: client.stream_info.binning == 2 and client.stream_info.generation == 2)
            assert (client.stream_info.width, client.stream_info.height) == (640, 360)
        finally:
            client.stop()
            server.stop()

    def test_a_v1_server_keeps_getting_exactly_exposure_and_gain(self, config):
        server = MockCameraServer(port=0, fps=20)
        server.start()
        config.exposure, config.gain = 200, 50
        client = CameraClient(LatestFrame(), port=server.port)
        try:
            client.connect(timeout=5.0)
            client.start_receiving()
            controls.restore_controls(client, config)
            assert _wait(lambda: len(server.set_control_log) == 2)
            assert server.set_control_log == [{"id": "exposure", "value": 200}, {"id": "gain", "value": 50}]
        finally:
            client.stop()
            server.stop()


# -- Engine.set_control and startup_camera --------------------------------------------------


class _Manager:
    def __init__(self, client):
        self.client = client


class TestEngineHooks:
    def test_set_control_saves_through_the_profile(self, tmp_path):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        client = FakeClient(_all_v2())
        eng._subprocess_mgr = _Manager(client)
        eng.set_control("exposure", 250_000)
        eng.set_control("fps", 4.0)
        assert client.sent == [("exposure", 250_000), ("fps", 4.0)]
        assert profiles.get_saved_control(eng._config, KEY, "fps") == {"value": 4.0, "unit": "fps"}
        assert eng._config.exposure is None

    def test_set_control_without_a_camera_keeps_todays_legacy_behaviour(self, tmp_path):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        eng.set_control("gain", 31)
        eng.set_control("exposure", 2)
        assert (eng._config.gain, eng._config.exposure) == (31, 2)

    def test_startup_restores_through_restore_controls(self, tmp_path, monkeypatch):
        eng = Engine(config=ConfigManager(config_dir=tmp_path))
        client = FakeClient(_all_v2())
        monkeypatch.setattr("evf.engine.engine._usb_video_device_present", lambda: True)

        class Started(_Manager):
            def __init__(self, *args, **kwargs):
                super().__init__(client)
                self.on_recovered = None

            def start(self):
                return {"protocol_version": 1, "camera_key": KEY}

        monkeypatch.setattr("evf.engine.engine.SubprocessManager", Started)
        eng.startup_camera()
        assert [c for c, _ in client.sent] == ["binning", "fps", "exposure", "gain"]


# -- recovery uses the same restore path -----------------------------------------------------


class TestRecoveryRestores:
    def test_a_v2_camera_gets_its_saved_controls_again_in_order_after_a_recovery(self, tmp_path):
        from evf.engine.state import EngineState, StateMachine
        from tests.test_subprocess_mgr import _NoopSubprocessManager

        server = MockCameraServer(port=0, fps=20, v2=True)
        server.start()
        port = server.port
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.save_control(cfg, "mock:v2", "binning", 2)
        profiles.save_control(cfg, "mock:v2", "fps", 8.0, "fps")
        profiles.save_control(cfg, "mock:v2", "exposure", 20_000, "us")
        profiles.save_control(cfg, "mock:v2", "gain", 33, "raw")
        sm = StateMachine()
        mgr = _NoopSubprocessManager(LatestFrame(), sm, cfg, binary_path="/dev/null", port=port)
        server2 = None
        try:
            mgr.start()
            time.sleep(0.3)
            server.stop()
            assert _wait(lambda: sm.state == EngineState.RECONNECTING, 5.0)
            server2 = MockCameraServer(port=port, fps=20, v2=True)
            server2.start()
            assert _wait(lambda: sm.state == EngineState.SETUP, 10.0)
            assert _wait(lambda: len(server2.set_control_log) == 4)
            assert server2.set_control_log == [
                {"id": "binning", "value": 2}, {"id": "fps", "value": 8.0},
                {"id": "exposure", "value": 20_000}, {"id": "gain", "value": 33}]
        finally:
            mgr.stop()
            server.stop()
            if server2 is not None:
                server2.stop()


# -- the descriptor ----------------------------------------------------------------------------


class TestControlDescriptor:
    def test_built_from_a_control_info_entry(self):
        d = controls.ControlDescriptor.from_info(_exposure_v2(scale="log", default=5))
        assert (d.id, d.type, d.unit, d.min, d.max, d.default, d.scale) == (
            "exposure", "int", "us", 100, 4_000_000, 5, "log")

    def test_an_enum_keeps_the_values_of_its_options(self):
        d = controls.ControlDescriptor.from_info(_binning())
        assert d.options == (1, 2)

    @pytest.mark.parametrize("info", [None, "x", {}, {"id": ""}, {"id": 5}, []])
    def test_an_entry_without_an_id_is_not_a_descriptor(self, info):
        assert controls.ControlDescriptor.from_info(info) is None

    def test_a_missing_type_is_int_as_before(self):
        d = controls.ControlDescriptor.from_info({"id": "gain", "min": 0, "max": 9})
        assert d.type == "int" and d.first_run_value() == 4

    def test_it_is_frozen(self):
        d = controls.ControlDescriptor.from_info(_gain())
        with pytest.raises(Exception):
            d.id = "x"

    def test_coerce(self):
        gain, fps, binning = (controls.ControlDescriptor.from_info(x) for x in (_gain(), _fps(), _binning()))
        assert gain.coerce(2.6) == 3 and isinstance(gain.coerce(2.0), int)
        assert fps.coerce(2) == 2.0 and isinstance(fps.coerce(2), float)
        assert binning.coerce(2.0) == 2 and binning.coerce(3) is None
        assert gain.coerce("2") is None and gain.coerce(True) is None and gain.coerce(float("nan")) is None

    def test_the_value_source_is_named(self, config):
        chosen = controls.choose_value(config, KEY, controls.ControlDescriptor.from_info(_gain()), 2)
        assert chosen == controls.ChosenValue(50, controls.ValueSource.MIDPOINT)
        chosen = controls.choose_value(config, KEY, controls.ControlDescriptor.from_info(_gain(default=7)), 2)
        assert chosen.source is controls.ValueSource.DEFAULT


# -- concurrent writers of camera.profiles ---------------------------------------------------------


def _widen_the_race_window(cfg):
    """Make ``config.set`` slow: a writer that read the profiles before another one wrote them
    then stores its stale copy a moment later, which is what a lost update needs."""
    original = cfg.set

    def slow_set(section, key, value):
        time.sleep(0.0005)
        original(section, key, value)

    cfg.set = slow_set


class TestProfilesAreWrittenUnderOneLock:
    def test_saving_controls_cannot_wipe_a_learned_fov(self, tmp_path):
        """The solver thread stores a learned FOV while the web server saves controls."""
        import threading

        cfg = ConfigManager(config_dir=tmp_path)
        profiles.ensure_profile(cfg, KEY)
        errors: list[BaseException] = []
        _widen_the_race_window(cfg)

        def guard(fn):
            def run():
                try:
                    fn()
                except BaseException as exc:  # noqa: BLE001
                    errors.append(exc)
            return run

        def save_controls():
            for i in range(150):
                profiles.save_control(cfg, KEY, "gain", i, "raw")
                profiles.save_control(cfg, KEY, f"extra{i % 5}", i, "raw")

        def learn_fovs():
            for i in range(150):
                profiles.set_fov(cfg, KEY, 20.0 + i / 100, "learned")

        threads = [threading.Thread(target=guard(save_controls)) for _ in range(2)]
        threads += [threading.Thread(target=guard(learn_fovs)) for _ in range(2)]
        for t in threads:
            t.start()
        for t in threads:
            t.join(timeout=60)
        assert not errors
        fov, source = profiles.get_fov(cfg, KEY)
        assert (fov, source) == (20.0 + 149 / 100, "learned"), "a control save wiped the FOV"
        assert profiles.get_saved_control(cfg, KEY, "gain") == {"value": 149, "unit": "raw"}
        for n in range(5):
            assert profiles.get_saved_control(cfg, KEY, f"extra{n}") is not None

    def test_a_slow_control_save_does_not_undo_a_fov_stored_meanwhile(self, tmp_path):
        import threading

        cfg = ConfigManager(config_dir=tmp_path)
        profiles.ensure_profile(cfg, KEY)
        original, first = cfg.set, threading.Event()

        def slow_first_set(section, key, value):
            if not first.is_set():
                first.set()
                time.sleep(0.4)  # the control save has read the profile and is about to write it back
            original(section, key, value)

        cfg.set = slow_first_set
        saver = threading.Thread(target=profiles.save_control, args=(cfg, KEY, "gain", 5, "raw"))
        saver.start()
        assert first.wait(5.0)
        profiles.set_fov(cfg, KEY, 25.6, "learned")  # the solver thread, in the middle of that save
        saver.join(timeout=10)
        assert profiles.get_fov(cfg, KEY) == (25.6, "learned")
        assert profiles.get_saved_control(cfg, KEY, "gain") == {"value": 5, "unit": "raw"}

    def test_two_cameras_saving_at_once_keep_both_profiles(self, tmp_path):
        import threading

        cfg = ConfigManager(config_dir=tmp_path)
        keys = [f"cam:{n}" for n in range(6)]
        _widen_the_race_window(cfg)

        def work(key):
            for i in range(60):
                profiles.save_control(cfg, key, "gain", i, "raw")

        threads = [threading.Thread(target=work, args=(k,)) for k in keys]
        for t in threads:
            t.start()
        for t in threads:
            t.join(timeout=60)
        for key in keys:
            assert profiles.get_saved_control(cfg, key, "gain") == {"value": 59, "unit": "raw"}
