# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 5.2.2, 5.4.4, 6.1).
"""Fakes for the FOV learning, search and API tests: no 50 MB databases, no tetra3rs."""

from __future__ import annotations

import time
from pathlib import Path

from evf.config.manager import ConfigManager
from evf.engine.camera_profile import CameraProfile
from evf.engine.frame_buffer import LatestFrame
from evf.solver.databases import DbInfo, SolverDatabases
from evf.solver.solver import PlateSolver

NARROW = DbInfo("narrow", Path("narrow.bin"), 7.0, 10.0)
WIDE = DbInfo("wide", Path("wide.bin"), 18.0, 32.0)

IDS_KEY = "ids:1409:3060"
# What the C# server sends: camera_key and the sensor (IMX174, 1936 px x 5.86 um = 11.35 mm wide).
IDS_SENSOR_HELLO = {
    "protocol_version": 1, "backend": "windows-dotnet-ids", "camera_model": "UI306xCP-M",
    "camera_key": IDS_KEY, "default_width": 1936, "default_height": 1216, "default_fps": 10,
    "capabilities": {"sensor": {"width": 1936, "height": 1216, "pixel_size_um": 5.86, "mono": True}},
}
# The same camera as before S1b / without a sensor in HELLO: no search, no focal length.
IDS_PLAIN_HELLO = {k: v for k, v in IDS_SENSOR_HELLO.items() if k != "capabilities"}
OV_HELLO = {
    "protocol_version": 1, "backend": "linux-v4l2", "camera_model": "OV9281",
    "default_width": 1280, "default_height": 720, "default_fps": 30,
}


class FakeDb:
    def __init__(self, path):
        self.path = path


class FakeSolver:
    """Stands in for PlateSolver: a real registry with a fake loader.

    ``solve_frame`` solves only against ``solves_on`` (a database name) and reports
    ``fov_deg``; every other database fails. Records each call.
    """

    is_valid = staticmethod(PlateSolver.is_valid)

    def __init__(self, initial_fov=8.86, solves_on: str | None = None, solved_fov: float = 25.6):
        self.databases = SolverDatabases([NARROW, WIDE], initial_fov, loader=FakeDb)
        self.solves_on = solves_on
        self.solved_fov = solved_fov
        self.calls: list[tuple[str, float, float]] = []

    def apply_pending_database(self):
        return self.databases.apply_pending()

    def solve_frame(self, image_bytes, fov_estimate_deg, fov_max_error_deg, database=None):
        info = database if database is not None else self.databases.active
        assert self.databases.db_for(info) is not None, f"{info.name} is not loaded"
        self.calls.append((info.name, fov_estimate_deg, fov_max_error_deg))
        out = {"RA": None, "Dec": None, "Roll": None, "Matches": 0, "Prob": None, "fov_deg": None,
               "all_centroids": [], "matched_centroids": [], "matched_stars": [],
               "image_size": (1216, 1936)}
        if self.solves_on == info.name:
            out.update({"RA": 10.0, "Dec": 20.0, "Roll": 30.0, "Matches": 12, "Prob": 1e-9,
                        "fov_deg": self.solved_fov})
        return out


def valid_result(fov_deg: float | None) -> dict:
    """A result that passes ``PlateSolver.is_valid`` and carries ``fov_deg``."""
    return {"RA": 10.0, "Dec": 20.0, "Roll": 30.0, "Matches": 12, "Prob": 1e-9, "fov_deg": fov_deg,
            "image_size": (1216, 1936)}


def make_profile(tmp_path, solver=None, name="cfg"):
    """A CameraProfile on a fresh config; returns ``(config, profile, solver_box)``."""
    cfg = ConfigManager(config_dir=tmp_path / name)
    box = {"solver": solver}
    return cfg, CameraProfile(cfg, LatestFrame(), lambda: box["solver"]), box


def wait_for(predicate, timeout=5.0):
    deadline = time.monotonic() + timeout
    while not predicate() and time.monotonic() < deadline:
        time.sleep(0.01)
    return predicate()
