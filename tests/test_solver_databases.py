# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.4 and 8).
"""Solver databases: registry, selection, one-at-a-time loading, swapping.

Selection and swap logic runs against a fake loader (no 50 MB loads). A few
integration tests use the real data/ files: registry matches the shipped
.bin files, a synthetic 25.6 deg frame solves with the wide database, and a
swap on the real solver thread.
"""

from __future__ import annotations

import gc
import hashlib
import json
import math
import threading
import time
import weakref
from pathlib import Path

import numpy as np
import pytest

from evf import paths
from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.engine.pointing import PointingState
from evf.engine.state import EngineState, StateMachine
from evf.solver.databases import (
    DbInfo,
    SolverDatabases,
    load_registry,
    select_database,
)
from evf.solver.fov import LEGACY_SOLVER_PARAMS, SolverParams, max_error_for_fov
from evf.solver.solver import PlateSolver
from evf.solver.thread import SolverThread

_SAMPLES_DIR = Path(__file__).parent / "samples"


def _info(name, lo, hi, tmp_path=None):
    return DbInfo(name, Path(f"{name}.bin"), lo, hi)


NARROW = _info("narrow", 7.0, 10.0)
WIDE = _info("wide", 18.0, 32.0)


# ---------------------------------------------------------------------------
# select_database (spec 5.4.3)
# ---------------------------------------------------------------------------


class TestSelectDatabase:
    @pytest.mark.parametrize(
        "fov,expected",
        [
            (8.86, "narrow"),   # legacy setup
            (7.0, "narrow"),    # range ends are inside
            (10.0, "narrow"),
            (18.0, "wide"),
            (25.6, "wide"),     # IDS + 25 mm reference
            (32.0, "wide"),
        ],
    )
    def test_inside_a_range(self, fov, expected):
        sel = select_database([NARROW, WIDE], fov)
        assert sel.info.name == expected
        assert sel.in_range is True

    def test_overlap_narrower_wins(self):
        broad = _info("broad", 5.0, 40.0)
        for order in ([broad, NARROW], [NARROW, broad]):
            sel = select_database(order, 8.86)
            assert sel.info.name == "narrow"
            assert sel.in_range is True
        # outside the narrow range the broad one is the only match
        assert select_database([NARROW, broad], 20.0).info.name == "broad"

    @pytest.mark.parametrize(
        "fov,expected",
        [
            (3.0, "narrow"),    # below everything
            (12.0, "narrow"),   # gap: 2 from narrow, 6 from wide
            (16.0, "wide"),     # gap: 6 from narrow, 2 from wide
            (50.0, "wide"),     # above everything
            (14.0, "narrow"),   # equidistant (4 / 4): narrower range wins
        ],
    )
    def test_out_of_range_uses_closest(self, fov, expected):
        sel = select_database([NARROW, WIDE], fov)
        assert sel.info.name == expected
        assert sel.in_range is False

    def test_empty_registry_raises(self):
        with pytest.raises(ValueError):
            select_database([], 8.86)


# ---------------------------------------------------------------------------
# solver parameters
# ---------------------------------------------------------------------------


class TestSolverParams:
    def test_legacy_is_todays_values(self):
        assert LEGACY_SOLVER_PARAMS == SolverParams(8.86, 1.5)

    def test_max_error_rule(self):
        assert max_error_for_fov(8.86) == 1.5
        assert max_error_for_fov(15.0) == 1.5
        assert max_error_for_fov(25.6) == pytest.approx(2.56)


# ---------------------------------------------------------------------------
# load_registry
# ---------------------------------------------------------------------------


def _write_registry(directory: Path, entries: list[dict], touch: bool = True) -> None:
    for e in entries:
        if touch:
            (directory / e["file"]).write_bytes(b"x")
    (directory / "solver_dbs.json").write_text(
        json.dumps({"version": 1, "databases": entries}), encoding="utf-8"
    )


def _entry(name, filename, lo, hi, mag=8.0):
    return {"name": name, "file": filename, "min_fov_deg": lo, "max_fov_deg": hi,
            "star_max_magnitude": mag}


class TestLoadRegistry:
    def test_reads_entries_narrowest_first(self, tmp_path):
        _write_registry(tmp_path, [
            _entry("wide", "w.bin", 18, 32, 7.0),
            _entry("narrow", "n.bin", 7, 10, 8.0),
        ])
        infos = load_registry(tmp_path)
        assert [i.name for i in infos] == ["narrow", "wide"]
        assert infos[0].path == tmp_path / "n.bin"
        assert (infos[1].min_fov_deg, infos[1].max_fov_deg) == (18.0, 32.0)
        assert infos[1].star_max_magnitude == 7.0

    def test_missing_registry_falls_back_to_narrow(self, tmp_path):
        infos = load_registry(tmp_path)
        assert [i.name for i in infos] == ["narrow"]
        assert infos[0].path == paths.tetra3rs_database_path()
        assert (infos[0].min_fov_deg, infos[0].max_fov_deg) == (7.0, 10.0)

    def test_corrupt_registry_falls_back_to_narrow(self, tmp_path):
        (tmp_path / "solver_dbs.json").write_text("{not json", encoding="utf-8")
        assert [i.name for i in load_registry(tmp_path)] == ["narrow"]

    def test_wrong_shape_falls_back_to_narrow(self, tmp_path):
        (tmp_path / "solver_dbs.json").write_text('{"databases": [{"name": "x"}]}', encoding="utf-8")
        assert [i.name for i in load_registry(tmp_path)] == ["narrow"]

    def test_entry_with_missing_file_is_skipped(self, tmp_path):
        (tmp_path / "n.bin").write_bytes(b"x")
        _write_registry(tmp_path, [
            _entry("narrow", "n.bin", 7, 10),
            _entry("wide", "w.bin", 18, 32),  # no such file
        ], touch=False)
        assert [i.name for i in load_registry(tmp_path)] == ["narrow"]

    def test_file_with_path_separator_is_skipped(self, tmp_path):
        sub = tmp_path / "sub"
        sub.mkdir()
        (sub / "evil.bin").write_bytes(b"x")
        (tmp_path / "n.bin").write_bytes(b"x")
        _write_registry(tmp_path, [
            _entry("narrow", "n.bin", 7, 10),
            _entry("evil", "sub/evil.bin", 18, 32),
        ], touch=False)
        assert [i.name for i in load_registry(tmp_path)] == ["narrow"]

    def test_bad_range_is_skipped(self, tmp_path):
        _write_registry(tmp_path, [
            _entry("narrow", "n.bin", 7, 10),
            _entry("bad", "b.bin", 30, 20),
        ])
        assert [i.name for i in load_registry(tmp_path)] == ["narrow"]


class TestShippedRegistry:
    """data/solver_dbs.json must describe the .bin files that are committed."""

    def test_lists_narrow_and_wide(self):
        infos = load_registry(paths.solver_db_dir())
        by_name = {i.name: i for i in infos}
        assert set(by_name) == {"narrow", "wide"}
        assert (by_name["narrow"].min_fov_deg, by_name["narrow"].max_fov_deg) == (7.0, 10.0)
        assert (by_name["wide"].min_fov_deg, by_name["wide"].max_fov_deg) == (18.0, 32.0)
        assert by_name["narrow"].path == paths.tetra3rs_database_path()
        assert by_name["wide"].path.name == "tetra3rs_gaia_wide.bin"

    def test_hashes_sizes_and_version_json_match_the_files(self):
        raw = json.loads((paths.solver_db_dir() / "solver_dbs.json").read_text(encoding="utf-8"))
        version = json.loads(paths.version_json().read_text(encoding="utf-8"))
        keys = {"narrow": "solver_db_version", "wide": "solver_db_version_wide"}
        for entry in raw["databases"]:
            path = paths.solver_db_dir() / entry["file"]
            assert path.stat().st_size == entry["size_bytes"], entry["name"]
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            assert digest == entry["sha256"], f"{entry['name']}: registry hash is stale"
            tag = f"_{digest[:12]}_mag{entry['star_max_magnitude']:g}"
            assert version[keys[entry["name"]]].endswith(tag), entry["name"]

    def test_ranges_match_the_database_files(self):
        import tetra3rs

        for info in load_registry(paths.solver_db_dir()):
            db = tetra3rs.SolverDatabase.load_from_file(str(info.path))
            assert db.min_fov_deg == pytest.approx(info.min_fov_deg), info.name
            assert db.max_fov_deg == pytest.approx(info.max_fov_deg), info.name


# ---------------------------------------------------------------------------
# SolverDatabases: one at a time, swap on the caller of apply_pending()
# ---------------------------------------------------------------------------


class FakeDb:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.num_stars = 1
        self.num_patterns = 1


class RecordingLoader:
    def __init__(self) -> None:
        self.calls: list[tuple[str, str]] = []  # (db name, thread name)
        self.live: list[weakref.ref] = []
        self.fail_for: set[str] = set()

    def __call__(self, path: Path):
        self.calls.append((path.stem, threading.current_thread().name))
        if path.stem in self.fail_for:
            raise OSError("boom")
        db = FakeDb(path)
        self.live.append(weakref.ref(db))
        return db

    def live_count(self) -> int:
        gc.collect()
        return sum(1 for r in self.live if r() is not None)


class TestSolverDatabases:
    def test_initial_load_follows_fov(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        assert dbs.active.name == "narrow"
        assert dbs.db.path == NARROW.path
        assert [c[0] for c in loader.calls] == ["narrow"]
        assert dbs.in_range is True

        loader2 = RecordingLoader()
        wide_first = SolverDatabases([NARROW, WIDE], 25.6, loader=loader2)
        assert wide_first.active.name == "wide"
        assert [c[0] for c in loader2.calls] == ["wide"]  # the other one is never loaded

    def test_request_loads_nothing_until_apply(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        sel = dbs.request_for_fov(25.6)
        assert sel.info.name == "wide" and sel.in_range
        assert dbs.active.name == "narrow"
        assert len(loader.calls) == 1

    def test_apply_swaps_and_drops_the_old_database(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        dbs.request_for_fov(25.6)
        assert dbs.apply_pending() is True
        assert dbs.active.name == "wide"
        assert dbs.db.path == WIDE.path
        assert loader.live_count() == 1  # only one database in memory
        assert dbs.apply_pending() is False  # nothing pending any more

    def test_request_for_active_database_is_a_noop(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        dbs.request_for_fov(9.0)
        assert dbs.apply_pending() is False
        assert len(loader.calls) == 1

    def test_last_request_wins(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        dbs.request_for_fov(25.6)
        dbs.request_for_fov(8.5)  # changed its mind before the solver thread got there
        assert dbs.apply_pending() is False
        assert dbs.active.name == "narrow"
        assert len(loader.calls) == 1

    def test_out_of_range_flag(self):
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=RecordingLoader())
        sel = dbs.request_for_fov(14.0)
        assert sel.in_range is False
        assert dbs.in_range is False
        dbs.request_for_fov(9.0)
        assert dbs.in_range is True

    def test_failed_swap_keeps_the_old_database(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        loader.fail_for.add("wide")
        dbs.request_for_fov(25.6)
        assert dbs.apply_pending() is False  # does not raise
        assert dbs.active.name == "narrow"
        assert dbs.db.path == NARROW.path
        assert dbs.apply_pending() is False  # request was consumed, no retry loop

    def test_swap_runs_on_the_thread_calling_apply_pending(self):
        loader = RecordingLoader()
        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        dbs.request_for_fov(25.6)  # from the test thread
        t = threading.Thread(target=dbs.apply_pending, name="solver-test")
        t.start()
        t.join()
        assert loader.calls[-1] == ("wide", "solver-test")

    def test_empty_registry_raises(self):
        with pytest.raises(ValueError):
            SolverDatabases([], 8.86, loader=RecordingLoader())


# ---------------------------------------------------------------------------
# paths.solver_db_dir() layouts (platform independent via module patching;
# the real PUSHNAV_ROOT case is in test_paths_headless.py)
# ---------------------------------------------------------------------------


class TestSolverDbDirLayouts:
    def _patch(self, monkeypatch, **values):
        base = dict(_BUNDLE_MODE=False, _LINUX_RELEASE=False, _WINDOWS_RELEASE=False)
        base.update(values)
        for k, v in base.items():
            monkeypatch.setattr(paths, k, v, raising=False)

    def test_dev_layout(self, monkeypatch, tmp_path):
        self._patch(monkeypatch, _REPO_ROOT=tmp_path)
        assert paths.solver_db_dir() == tmp_path / "data"

    def test_macos_bundle_layout(self, monkeypatch, tmp_path):
        self._patch(monkeypatch, _BUNDLE_MODE=True, _RESOURCES=tmp_path / "Resources")
        assert paths.solver_db_dir() == tmp_path / "Resources"

    @pytest.mark.parametrize("flag", ["_LINUX_RELEASE", "_WINDOWS_RELEASE"])
    def test_release_layouts(self, monkeypatch, tmp_path, flag):
        self._patch(monkeypatch, _RELEASE_ROOT=tmp_path, **{flag: True})
        assert paths.solver_db_dir() == tmp_path / "data"

    def test_sits_next_to_the_narrow_database(self):
        assert paths.solver_db_dir() == paths.tetra3rs_database_path().parent
        assert (paths.solver_db_dir() / "solver_dbs.json").is_file()


# ---------------------------------------------------------------------------
# Real databases
# ---------------------------------------------------------------------------


def _synthetic_wide_centroids(db, ra0, dec0, fov_deg=25.6, w=1936, h=1216):
    """Project catalog stars through a pinhole camera: the 30 brightest
    centroids of a 25.6 x 16.2 deg IDS-like frame (image-centre origin)."""
    f = (w / 2) / math.tan(math.radians(fov_deg) / 2)
    a0, d0 = math.radians(ra0), math.radians(dec0)
    out = []
    for s in db.cone_search(ra0, dec0, 20.0):  # sorted by brightness
        a, d = math.radians(s.ra_deg), math.radians(s.dec_deg)
        cosc = math.sin(d0) * math.sin(d) + math.cos(d0) * math.cos(d) * math.cos(a - a0)
        xi = math.cos(d) * math.sin(a - a0) / cosc
        eta = (math.cos(d0) * math.sin(d) - math.sin(d0) * math.cos(d) * math.cos(a - a0)) / cosc
        x, y = -f * xi, -f * eta  # north up, east left
        if abs(x) < w / 2 and abs(y) < h / 2:
            out.append((x, y, 10 ** (-0.4 * s.magnitude)))
    return np.array(out[:30])


class TestRealDatabases:
    @pytest.mark.parametrize("ra,dec", [(83.8, -5.4), (200.0, 60.0), (10.0, -70.0)])
    def test_wide_database_solves_a_synthetic_ids_frame(self, ra, dec):
        """Task 4.4 sanity check: the committed wide database can solve a
        25.6 deg / 1936x1216 field (rendered from its own catalog)."""
        import tetra3rs

        info = {i.name: i for i in load_registry(paths.solver_db_dir())}["wide"]
        db = tetra3rs.SolverDatabase.load_from_file(str(info.path))
        centroids = _synthetic_wide_centroids(db, ra, dec)
        assert len(centroids) == 30
        result = db.solve_from_centroids(
            centroids, fov_estimate_deg=25.6, fov_max_error_deg=max_error_for_fov(25.6),
            image_shape=(1216, 1936),
        )
        assert result is not None and result.status == "match_found"
        assert result.ra_deg == pytest.approx(ra, abs=0.05)
        assert result.dec_deg == pytest.approx(dec, abs=0.05)
        assert result.fov_deg == pytest.approx(25.6, abs=0.1)

    def test_default_plate_solver_loads_the_narrow_database(self):
        solver = PlateSolver()
        assert solver.databases.active.name == "narrow"
        assert [i.name for i in solver.databases.registry] == ["narrow", "wide"]

    def test_swap_on_the_solver_thread_keeps_it_running(self, tmp_path):
        """Request the wide database while the real solver thread runs: the
        swap happens on that thread, the thread survives, and solving
        works again after swapping back (frames keep arriving throughout)."""
        calls: list[tuple[str, str]] = []

        def loader(path):
            import tetra3rs

            calls.append((path.stem, threading.current_thread().name))
            return tetra3rs.SolverDatabase.load_from_file(str(path))

        dbs = SolverDatabases(load_registry(paths.solver_db_dir()), 8.86, loader=loader)
        solver = PlateSolver(databases=dbs)
        results: list[dict] = []
        real_solve = solver.solve_frame

        def recording_solve(*args, **kwargs):
            results.append(real_solve(*args, **kwargs))
            return results[-1]

        solver.solve_frame = recording_solve
        fb, ps, sm = LatestFrame(), PointingState(), StateMachine()
        cfg = ConfigManager(config_dir=tmp_path)
        st = SolverThread(solver, fb, ps, sm, cfg)

        image = (_SAMPLES_DIR / "b.png").read_bytes()
        frame_id = 1
        fb.set(image, time.monotonic(), frame_id)
        sm.transition(EngineState.SYNC)
        sm.transition(EngineState.SYNC_CONFIRM)
        st.start()

        def wait_for(cond, timeout=15.0):
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                if cond():
                    return True
                time.sleep(0.05)
            return False

        try:
            assert wait_for(lambda: sm.state == EngineState.TRACKING)
            assert len(calls) == 1  # initial load (narrow), on the constructing thread
            assert calls[0] == ("tetra3rs_gaia", threading.current_thread().name)

            dbs.request_for_fov(25.6)
            assert wait_for(lambda: dbs.active.name == "wide")
            assert calls[-1] == ("tetra3rs_gaia_wide", "solver")  # swapped on the solver thread
            assert st._thread.is_alive()

            dbs.request_for_fov(8.86)
            assert wait_for(lambda: dbs.active.name == "narrow")
            assert calls[-1][1] == "solver"

            # a fresh frame still solves after the round trip
            n_before = len(results)
            frame_id += 1
            fb.set(image, time.monotonic(), frame_id)
            assert wait_for(lambda: len(results) > n_before)
            assert results[-1]["RA"] is not None
            assert st._thread.is_alive()
        finally:
            st.stop()
