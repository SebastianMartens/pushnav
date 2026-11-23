# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 4.2, task 3.3).
"""``calibration.camera_key`` and ``has_calibration`` across cameras."""

from __future__ import annotations

import json

import pytest

from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.camera_profile import CameraProfile
from evf.engine.frame_buffer import LatestFrame
from evf.webserver.server import _compute_origin

IDS_HELLO = {"protocol_version": 1, "backend": "windows-dotnet-ids", "camera_model": "UI306xCP-M",
             "camera_key": "ids:1409:3060", "default_width": 1936, "default_height": 1216}
OV_HELLO = {"protocol_version": 1, "backend": "linux-v4l2", "camera_model": "OV9281",
            "default_width": 1280, "default_height": 720}
D_BODY = [0.1, 0.2, 0.97]


@pytest.fixture
def cfg(tmp_path):
    return ConfigManager(config_dir=tmp_path / "cfg")


def _profile(cfg) -> CameraProfile:
    return CameraProfile(cfg, LatestFrame(), lambda: None)


def _write_old_file(tmp_path, calibration) -> ConfigManager:
    d = tmp_path / "old"
    d.mkdir()
    (d / "config.json").write_text(json.dumps({"version": 1, "calibration": calibration}))
    return ConfigManager(config_dir=d)


class TestDefaults:
    def test_new_key_is_in_the_defaults(self, cfg):
        assert cfg.get("calibration", "camera_key") is None
        assert cfg.has_calibration is False

    def test_old_file_without_the_key_loads_and_keeps_its_calibration(self, tmp_path):
        old = _write_old_file(tmp_path, {"finder_rotation": 12.5, "sync_d_body": D_BODY})
        assert old.finder_rotation == 12.5
        assert old.sync_d_body == D_BODY
        assert old.get("calibration", "camera_key") is None
        assert old.has_calibration is True  # no camera connected yet, nothing to compare


class TestHasCalibration:
    def test_sync_stamps_the_connected_camera(self, cfg):
        _profile(cfg).on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        assert cfg.get("calibration", "camera_key") == "ids:1409:3060"
        assert cfg.has_calibration is True

    def test_another_camera_does_not_have_the_calibration(self, cfg):
        profile = _profile(cfg)
        profile.on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        profile.on_camera_connected(OV_HELLO)
        assert cfg.has_calibration is False

    def test_the_old_calibration_is_kept_not_deleted(self, cfg):
        profile = _profile(cfg)
        profile.on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        cfg.finder_rotation = 42.0
        profile.on_camera_connected(OV_HELLO)
        assert cfg.sync_d_body == D_BODY
        assert cfg.finder_rotation == 42.0
        assert cfg.get("calibration", "camera_key") == "ids:1409:3060"

    def test_reconnecting_the_original_camera_brings_it_back(self, cfg):
        profile = _profile(cfg)
        profile.on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        profile.on_camera_connected(OV_HELLO)
        assert cfg.has_calibration is False
        profile.on_camera_connected(IDS_HELLO)
        assert cfg.has_calibration is True

    def test_a_new_sync_with_the_other_camera_takes_over(self, cfg):
        profile = _profile(cfg)
        profile.on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        profile.on_camera_connected(OV_HELLO)
        cfg.sync_d_body = [0.0, 0.0, 1.0]
        assert cfg.get("calibration", "camera_key") == "linux-v4l2:OV9281"
        assert cfg.has_calibration is True

    def test_the_key_is_saved_to_the_file(self, cfg):
        _profile(cfg).on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        again = ConfigManager(config_dir=cfg.path.parent)
        assert again.get("calibration", "camera_key") == "ids:1409:3060"
        _profile(again).on_camera_connected(OV_HELLO)
        assert again.has_calibration is False

    def test_no_calibration_means_none_whatever_the_key(self, cfg):
        _profile(cfg).on_camera_connected(IDS_HELLO)
        assert cfg.has_calibration is False


class TestCalibrationFromBeforeProfiles:
    def test_the_first_camera_to_connect_claims_it(self, tmp_path):
        old = _write_old_file(tmp_path, {"finder_rotation": 12.5, "sync_d_body": D_BODY})
        profile = _profile(old)
        profile.on_camera_connected(OV_HELLO)
        assert old.get("calibration", "camera_key") == "linux-v4l2:OV9281"
        assert old.has_calibration is True
        profile.on_camera_connected(IDS_HELLO)  # the user plugs in the IDS camera later
        assert old.has_calibration is False

    def test_nothing_to_claim_without_a_calibration(self, cfg):
        _profile(cfg).on_camera_connected(OV_HELLO)
        assert cfg.get("calibration", "camera_key") is None

    def test_an_existing_key_is_not_overwritten(self, cfg):
        cfg.set("calibration", "sync_d_body", D_BODY)
        cfg.set("calibration", "camera_key", "ids:1409:3060")
        assert profiles.claim_legacy_calibration(cfg, "other") is False
        assert cfg.get("calibration", "camera_key") == "ids:1409:3060"

    def test_a_camera_without_identity_matches_any_calibration(self, cfg):
        _profile(cfg).on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = D_BODY
        _profile(cfg).on_camera_connected({"protocol_version": 1})  # no key, backend or model
        assert cfg.active_camera_key is None
        assert cfg.has_calibration is True


class TestSyncOrigin:
    """The nav projection must not use another camera's sync offset."""

    def test_origin_ignores_a_foreign_calibration(self, cfg):
        profile = _profile(cfg)
        profile.on_camera_connected(IDS_HELLO)
        cfg.sync_d_body = [0.05, 0.02, 0.99]
        own = _compute_origin(cfg, 25.6, 1936, 1216)
        assert own != (968.0, 608.0)
        profile.on_camera_connected(OV_HELLO)
        assert _compute_origin(cfg, 8.86, 1280, 720) == (640.0, 360.0)
        profile.on_camera_connected(IDS_HELLO)
        assert _compute_origin(cfg, 25.6, 1936, 1216) == own
