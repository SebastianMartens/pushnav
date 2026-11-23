# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 4 and 8).
"""Camera profiles in config.json: defaults, old files, profile key rule."""

from __future__ import annotations

import json

import pytest

from evf.config import profiles
from evf.config.manager import CONFIG_VERSION, DEFAULT_CONFIG, ConfigManager

# A config.json as written before camera profiles existed (every key that
# DEFAULT_CONFIG had then, with non-default values).
_OLD_CONFIG = {
    "version": 1,
    "solver": {"min_matches": 10, "max_prob": 0.05},
    "camera": {"exposure": 1234, "gain": 77, "extra_camera_ids": ["1409:3060"], "windows_server": "c"},
    "calibration": {"finder_rotation": 123.5, "sync_d_body": [0.1, 0.2, 0.97]},
    "logging": {"verbose": True},
    "audio": {"enabled": False},
    "led": {"enabled": False},
    "lx200": {"epoch": "j2000"},
    "webserver": {"port": 9000},
    "location": {"latitude": 52.5, "longitude": 13.4},
}


def _write(tmp_path, data) -> None:
    tmp_path.mkdir(parents=True, exist_ok=True)
    (tmp_path / "config.json").write_text(json.dumps(data), encoding="utf-8")


class TestDefaults:
    def test_config_version_is_unchanged(self):
        assert CONFIG_VERSION == 1
        assert DEFAULT_CONFIG["version"] == 1

    def test_new_camera_keys(self):
        cam = DEFAULT_CONFIG["camera"]
        assert cam["max_exposure_us"] == 4000000
        assert cam["last_camera_key"] is None
        assert cam["profiles"] == {}
        # untouched keys
        assert cam["windows_server"] == "dotnet"
        assert cam["extra_camera_ids"] == []

    def test_fresh_config_has_them(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        assert cfg.get("camera", "max_exposure_us") == 4000000
        assert cfg.get("camera", "last_camera_key") is None
        assert cfg.get("camera", "profiles") == {}

    def test_defaults_are_not_shared_between_configs(self, tmp_path):
        a = ConfigManager(config_dir=tmp_path / "a")
        profiles.ensure_profile(a, "ids:1409:3060")
        b = ConfigManager(config_dir=tmp_path / "b")
        assert b.get("camera", "profiles") == {}
        assert DEFAULT_CONFIG["camera"]["profiles"] == {}


class TestOldConfigFile:
    def test_old_file_keeps_every_value_and_gains_the_new_keys(self, tmp_path):
        _write(tmp_path, _OLD_CONFIG)
        cfg = ConfigManager(config_dir=tmp_path)
        data = cfg.data
        assert data["version"] == 1
        # every old value survives (no reset to defaults)
        for section, values in _OLD_CONFIG.items():
            if section == "version":
                continue
            for key, value in values.items():
                assert data[section][key] == value, (section, key)
        assert cfg.has_calibration is True
        assert cfg.exposure == 1234 and cfg.gain == 77
        # new keys with their defaults
        assert data["camera"]["max_exposure_us"] == 4000000
        assert data["camera"]["last_camera_key"] is None
        assert data["camera"]["profiles"] == {}

    def test_existing_profiles_and_user_values_are_kept(self, tmp_path):
        data = json.loads(json.dumps(_OLD_CONFIG))
        data["camera"].update({
            "max_exposure_us": 2000000,
            "last_camera_key": "ids:1409:3060",
            "profiles": {"ids:1409:3060": {"controls": {}, "fov_h_deg": 25.6, "fov_source": "user"}},
        })
        _write(tmp_path, data)
        cfg = ConfigManager(config_dir=tmp_path)
        assert cfg.get("camera", "max_exposure_us") == 2000000
        assert profiles.last_camera_key(cfg) == "ids:1409:3060"
        assert profiles.get_fov(cfg, "ids:1409:3060") == (25.6, "user")

    def test_a_version_mismatch_still_resets(self, tmp_path):
        """Why CONFIG_VERSION must stay 1: any other version wipes the whole file."""
        _write(tmp_path, {**_OLD_CONFIG, "version": 2})
        assert ConfigManager(config_dir=tmp_path).exposure is None


class TestCameraKey:
    def test_hello_camera_key_wins(self):
        hello = {"backend": "windows-dotnet-ids", "camera_model": "UI306xCP-M", "camera_key": "ids:1409:3060"}
        assert profiles.camera_key_from_hello(hello) == "ids:1409:3060"

    def test_camera_key_is_used_in_a_v1_hello_too(self):
        hello = {"protocol_version": 1, "backend": "windows-dotnet-ids",
                 "camera_model": "UI306xCP-M", "camera_key": "ids:1409:3060"}
        assert profiles.camera_key_from_hello(hello) == "ids:1409:3060"

    @pytest.mark.parametrize("hello,expected", [
        ({"backend": "linux-v4l2", "camera_model": "OV9281"}, "linux-v4l2:OV9281"),
        ({"backend": "windows-directshow", "camera_model": "Arducam OV9281 UVC", "camera_key": None},
         "windows-directshow:Arducam OV9281 UVC"),
        ({"backend": "swift", "camera_model": "OV9281", "camera_key": ""}, "swift:OV9281"),
        ({"backend": "swift", "camera_model": "OV9281", "camera_key": "   "}, "swift:OV9281"),
        ({"backend": "swift", "camera_model": "OV9281", "camera_key": 42}, "swift:OV9281"),
    ])
    def test_without_camera_key_it_is_backend_colon_model(self, hello, expected):
        assert profiles.camera_key_from_hello(hello) == expected

    @pytest.mark.parametrize("hello", [None, {}, {"backend": "x"}, {"camera_model": "y"},
                                       {"backend": "", "camera_model": "y"}, "nope", []])
    def test_unidentifiable_camera_has_no_key(self, hello):
        assert profiles.camera_key_from_hello(hello) is None


class TestProfileHelpers:
    KEY = "ids:1409:3060"

    def test_first_connect_creates_an_empty_profile_and_saves_it(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        assert profiles.get_profile(cfg, self.KEY) is None
        profile = profiles.ensure_profile(cfg, self.KEY)
        assert profile == {"controls": {}, "fov_h_deg": None, "fov_source": None}
        on_disk = json.loads((tmp_path / "config.json").read_text(encoding="utf-8"))
        assert on_disk["camera"]["profiles"][self.KEY] == profile

    def test_ensure_is_idempotent_and_keeps_values(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, self.KEY, 25.6, "user")
        again = profiles.ensure_profile(cfg, self.KEY)
        assert again["fov_h_deg"] == 25.6 and again["fov_source"] == "user"

    def test_ensure_fills_the_gaps_of_a_hand_written_profile(self, tmp_path):
        data = json.loads(json.dumps(_OLD_CONFIG))
        data["camera"]["profiles"] = {self.KEY: {"fov_h_deg": 25.6}}
        _write(tmp_path, data)
        cfg = ConfigManager(config_dir=tmp_path)
        profile = profiles.ensure_profile(cfg, self.KEY)
        assert profile == {"controls": {}, "fov_h_deg": 25.6, "fov_source": None}

    def test_returned_profile_is_a_copy(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profile = profiles.ensure_profile(cfg, self.KEY)
        profile["fov_h_deg"] = 99.0
        profile["controls"]["x"] = 1
        assert profiles.get_fov(cfg, self.KEY) == (None, None)
        assert cfg.get("camera", "profiles")[self.KEY]["controls"] == {}

    def test_set_and_clear_fov(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, self.KEY, 25.6, "user")
        assert profiles.get_fov(cfg, self.KEY) == (25.6, "user")
        reloaded = ConfigManager(config_dir=tmp_path)
        assert profiles.get_fov(reloaded, self.KEY) == (25.6, "user")
        profiles.set_fov(cfg, self.KEY, None, "user")
        assert profiles.get_fov(cfg, self.KEY) == (None, None)

    def test_profiles_are_separate_per_key(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, self.KEY, 25.6, "user")
        assert profiles.get_fov(cfg, "linux-v4l2:OV9281") == (None, None)

    def test_last_camera_key(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        assert profiles.last_camera_key(cfg) is None
        profiles.set_last_camera_key(cfg, self.KEY)
        assert profiles.last_camera_key(ConfigManager(config_dir=tmp_path)) == self.KEY

    def test_a_non_dict_profiles_value_is_survivable(self, tmp_path):
        data = json.loads(json.dumps(_OLD_CONFIG))
        data["camera"]["profiles"] = ["not", "a", "dict"]
        data["camera"]["last_camera_key"] = 17
        _write(tmp_path, data)
        cfg = ConfigManager(config_dir=tmp_path)
        assert profiles.get_fov(cfg, self.KEY) == (None, None)
        assert profiles.last_camera_key(cfg) is None
        profiles.set_fov(cfg, self.KEY, 25.6, "user")
        assert profiles.get_fov(cfg, self.KEY) == (25.6, "user")


class TestNoNeedlessSaves:
    KEY = "ids:1409:3060"

    @staticmethod
    def _count_saves(cfg, monkeypatch):
        calls = []
        real = cfg.save

        def counting():
            calls.append(1)
            real()

        monkeypatch.setattr(cfg, "save", counting)
        return calls

    def test_unchanged_last_camera_key_is_not_saved_again(self, tmp_path, monkeypatch):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_last_camera_key(cfg, self.KEY)
        calls = self._count_saves(cfg, monkeypatch)
        profiles.set_last_camera_key(cfg, self.KEY)
        assert calls == []
        profiles.set_last_camera_key(cfg, "other:key")
        assert len(calls) == 1

    def test_ensure_profile_and_unchanged_fov_are_not_saved_again(self, tmp_path, monkeypatch):
        cfg = ConfigManager(config_dir=tmp_path)
        profiles.set_fov(cfg, self.KEY, 25.6, "user")
        calls = self._count_saves(cfg, monkeypatch)
        profiles.ensure_profile(cfg, self.KEY)
        profiles.set_fov(cfg, self.KEY, 25.6, "user")
        assert calls == []
        profiles.set_fov(cfg, self.KEY, 26.0, "user")
        assert len(calls) == 1
