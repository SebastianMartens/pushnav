# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 5.4.3, 5.4.4, task 4.6).
"""FOV search for a profile without a FOV: which database solves decides."""

from __future__ import annotations

import time

import pytest

from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.camera_profile import SolvePlan
from evf.engine.engine import Engine
from evf.engine.frame_buffer import LatestFrame
from evf.engine.pointing import PointingState
from evf.engine.state import EngineState, StateMachine
from evf.solver.databases import SolverDatabases
from evf.solver.fov import LEGACY_SOLVER_PARAMS, SolverParams, fov_from_focal_length
from evf.solver.fov_search import GUESS_FOCAL_LENGTH_MM, FovSearch, guess_fov_from_sensor, step_for
from evf.solver.thread import SolverThread
from tests.fov_fakes import (
    IDS_KEY, IDS_PLAIN_HELLO, IDS_SENSOR_HELLO, NARROW, OV_HELLO, WIDE, FakeDb, FakeSolver,
    make_profile, valid_result, wait_for,
)

IDS_SENSOR_MM = 1936 * 5.86 / 1000.0  # 11.35


class TestFovSearchPlanning:
    def test_a_step_uses_the_centre_and_half_the_range(self):
        assert step_for(NARROW).params == SolverParams(8.5, 1.5)
        assert step_for(WIDE).params == SolverParams(25.0, 7.0)

    def test_the_guess_is_the_fov_of_a_25_mm_lens(self):
        assert GUESS_FOCAL_LENGTH_MM == 25.0
        assert guess_fov_from_sensor(IDS_SENSOR_MM) == pytest.approx(25.6, abs=0.1)
        assert guess_fov_from_sensor(1280 * 3.0 / 1000) == pytest.approx(8.76, abs=0.1)  # OV9281
        assert guess_fov_from_sensor(None) is None

    def test_it_starts_with_the_database_that_fits_the_guess(self):
        assert FovSearch([NARROW, WIDE], 25.6).order == [WIDE, NARROW]
        assert FovSearch([NARROW, WIDE], 8.8).order == [NARROW, WIDE]
        assert FovSearch([WIDE, NARROW], None).order == [NARROW, WIDE]  # no guess: narrowest first

    def test_it_alternates_between_the_databases(self):
        search = FovSearch([NARROW, WIDE], 25.6)
        names = [search.next_step().database.name for _ in range(5)]
        assert names == ["wide", "narrow", "wide", "narrow", "wide"]

    def test_it_only_uses_databases_that_are_loaded(self):
        search = FovSearch([NARROW, WIDE], 25.6)
        assert {search.next_step([NARROW]).database.name for _ in range(3)} == {"narrow"}
        assert [s.database.name for s in search.steps([WIDE, NARROW])] == ["wide", "narrow"]
        assert search.next_step([]).database.name in ("wide", "narrow")  # nothing loaded: try all

    def test_an_empty_registry_is_refused(self):
        with pytest.raises(ValueError):
            FovSearch([])


class TestHeldDatabases:
    def _dbs(self):
        loaded = []

        def loader(path):
            loaded.append(path.name)
            return FakeDb(path)

        return SolverDatabases([NARROW, WIDE], 8.86, loader=loader), loaded

    def test_hold_all_loads_the_others_and_release_drops_them(self):
        dbs, loaded = self._dbs()
        assert [i.name for i in dbs.loaded()] == ["narrow"]
        dbs.hold_all()
        assert [i.name for i in dbs.loaded()] == ["narrow", "wide"]
        assert dbs.db_for(WIDE) is not None and dbs.db_for(NARROW) is dbs.db
        assert loaded == ["narrow.bin", "wide.bin"]
        dbs.hold_all()  # idempotent
        assert loaded == ["narrow.bin", "wide.bin"]
        dbs.release_extras()
        assert [i.name for i in dbs.loaded()] == ["narrow"]
        assert dbs.db_for(WIDE) is None
        assert dbs.active.name == "narrow"

    def test_a_failing_load_is_skipped(self, caplog):
        def loader(path):
            if path.name == "wide.bin":
                raise OSError("disk")
            return FakeDb(path)

        dbs = SolverDatabases([NARROW, WIDE], 8.86, loader=loader)
        dbs.hold_all()
        assert [i.name for i in dbs.loaded()] == ["narrow"]
        assert "Failed to load solver database 'wide'" in caplog.text

    def test_a_swap_still_works_while_holding(self):
        dbs, _ = self._dbs()
        dbs.hold_all()
        dbs.request_for_fov(25.6)
        assert dbs.apply_pending() is True
        assert dbs.active.name == "wide"
        assert dbs.db_for(WIDE) is dbs.db


class TestSearchInTheProfile:
    def _connect(self, tmp_path, solves_on=None, solved_fov=25.6, hello=IDS_SENSOR_HELLO):
        solver = FakeSolver(solves_on=solves_on, solved_fov=solved_fov)
        cfg, profile, _box = make_profile(tmp_path, solver)
        profile.on_camera_connected(hello)
        return cfg, profile, solver

    def test_a_fresh_profile_with_a_known_sensor_starts_the_search(self, tmp_path):
        cfg, profile, _solver = self._connect(tmp_path)
        assert profile.searching is True
        payload = profile.geometry_payload()
        assert payload["fov_source"] == "searching"
        assert payload["fov_h_deg"] == 8.86  # nothing is known yet
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)

    def test_the_plans_alternate_and_name_the_database(self, tmp_path):
        _cfg, profile, solver = self._connect(tmp_path)
        plans = [profile.plan() for _ in range(4)]
        assert [p.database.name for p in plans] == ["wide", "narrow", "wide", "narrow"]
        assert plans[0].params == SolverParams(25.0, 7.0)
        assert plans[1].params == SolverParams(8.5, 1.5)
        # The search holds both databases for its duration.
        assert [i.name for i in solver.databases.loaded()] == ["narrow", "wide"]

    def test_the_first_valid_solve_fixes_the_database_and_stores_the_fov(self, tmp_path):
        cfg, profile, solver = self._connect(tmp_path, solved_fov=25.7)
        profile.solve_failed()
        plan = profile.plan()
        profile.solve_succeeded(valid_result(25.7), plan)
        assert profile.searching is False
        assert profiles.get_fov(cfg, IDS_KEY) == (25.7, "learned")
        assert profile.params.get() == SolverParams(25.7, pytest.approx(2.57))
        assert profile.params.resolved.source == "learned"
        assert profile.geometry_payload()["fov_source"] == "learned"
        # the wide database is requested, the extra one is released
        assert solver.apply_pending_database() is True
        assert solver.databases.active.name == "wide"
        assert [i.name for i in solver.databases.loaded()] == ["wide"]
        assert profile.plan() == SolvePlan(profile.params.get(), None)

    def test_a_narrow_field_is_found_with_the_narrow_database(self, tmp_path):
        cfg, profile, solver = self._connect(tmp_path)
        profile.solve_succeeded(valid_result(8.7), SolvePlan(SolverParams(8.5, 1.5), NARROW))
        assert profiles.get_fov(cfg, IDS_KEY) == (8.7, "learned")
        assert solver.apply_pending_database() is False  # narrow stays
        assert solver.databases.active.name == "narrow"
        assert [i.name for i in solver.databases.loaded()] == ["narrow"]

    def test_a_valid_solve_without_fov_deg_does_not_end_the_search(self, tmp_path):
        cfg, profile, _solver = self._connect(tmp_path)
        profile.solve_succeeded(valid_result(None), profile.plan())
        assert profile.searching is True
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)

    def test_the_stored_fov_is_used_at_the_next_start_without_search(self, tmp_path):
        cfg, profile, _solver = self._connect(tmp_path)
        profile.solve_succeeded(valid_result(25.7), profile.plan())
        solver2 = FakeSolver()
        cfg2 = ConfigManager(config_dir=cfg.path.parent)
        profile2 = make_profile(tmp_path, solver2, name="cfg")[1]
        profile2.on_camera_connected(IDS_SENSOR_HELLO)
        assert profile2.searching is False
        assert profile2.params.resolved.fov_h_deg == 25.7
        assert cfg2.get("camera", "profiles")[IDS_KEY]["fov_source"] == "learned"

    def test_learning_takes_over_after_the_search(self, tmp_path):
        cfg, profile, solver = self._connect(tmp_path)
        profile.solve_succeeded(valid_result(25.0), profile.plan())
        solver.apply_pending_database()  # the solver thread swaps to the wide database between solves
        for _ in range(5):
            profile.solve_succeeded(valid_result(26.0), profile.plan())
        assert profiles.get_fov(cfg, IDS_KEY) == (26.0, "learned")

    # -- the upstream camera keeps today's behaviour --------------------------------

    @pytest.mark.parametrize("hello", [OV_HELLO, IDS_PLAIN_HELLO], ids=["upstream", "no-sensor"])
    def test_no_sensor_in_hello_means_no_search(self, tmp_path, hello):
        _cfg, profile, solver = self._connect(tmp_path, hello=hello)
        assert profile.searching is False
        assert profile.params.get() == LEGACY_SOLVER_PARAMS
        assert profile.plan() == SolvePlan(LEGACY_SOLVER_PARAMS, None)
        assert profile.geometry_payload()["fov_source"] == "default"
        assert [i.name for i in solver.databases.loaded()] == ["narrow"]  # nothing extra loaded

    def test_a_stored_fov_means_no_search(self, tmp_path):
        solver = FakeSolver()
        cfg, profile, _box = make_profile(tmp_path, solver)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        assert profile.searching is False
        assert profile.plan().database is None

    def test_without_a_solver_there_is_no_search(self, tmp_path):
        _cfg, profile, _box = make_profile(tmp_path, None)
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        assert profile.searching is False

    def test_a_single_database_registry_has_nothing_to_search(self, tmp_path):
        solver = FakeSolver()
        solver.databases = SolverDatabases([NARROW], 8.86, loader=FakeDb)
        _cfg, profile, _box = make_profile(tmp_path, solver)
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        assert profile.searching is False

    # -- ending the search other ways --------------------------------------------------

    def test_entering_a_focal_length_ends_the_search(self, tmp_path):
        cfg, profile, solver = self._connect(tmp_path)
        profile.plan()  # holds both
        profile.set_user_fov(focal_length_mm=25)
        assert profile.searching is False
        assert profiles.get_fov(cfg, IDS_KEY) == (pytest.approx(fov_from_focal_length(25, IDS_SENSOR_MM)), "user")
        assert [i.name for i in solver.databases.loaded()] == ["narrow"]  # extra released; swap pending
        assert solver.apply_pending_database() is True
        assert solver.databases.active.name == "wide"

    def test_reset_starts_the_search_again(self, tmp_path):
        cfg, profile, _solver = self._connect(tmp_path)
        profile.set_user_fov(focal_length_mm=25)
        assert profile.searching is False
        profile.set_user_fov(reset=True)
        assert profile.searching is True
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)
        assert profile.geometry_payload()["fov_source"] == "searching"

    def test_the_camera_going_away_stops_the_search(self, tmp_path):
        _cfg, profile, solver = self._connect(tmp_path)
        profile.plan()
        profile.on_camera_gone()
        assert profile.searching is False
        assert [i.name for i in solver.databases.loaded()] == ["narrow"]

    def test_another_camera_connecting_restarts_from_its_own_profile(self, tmp_path):
        cfg, profile, _solver = self._connect(tmp_path)
        profile.on_camera_connected(OV_HELLO)
        assert profile.searching is False
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        assert profile.searching is True


class TestSyncSolveDuringSearch:
    def test_the_sync_solve_tries_each_database_until_one_solves(self, tmp_path):
        solver = FakeSolver(solves_on="narrow", solved_fov=8.7)
        cfg, profile, _box = make_profile(tmp_path, solver)
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        result = profile.solve_for_sync(solver, b"frame")
        assert [c[0] for c in solver.calls] == ["wide", "narrow"]  # guess first, then the other
        assert result["fov_deg"] == 8.7
        assert profiles.get_fov(cfg, IDS_KEY) == (8.7, "learned")
        assert profile.searching is False

    def test_a_failing_sync_solve_keeps_searching(self, tmp_path):
        solver = FakeSolver(solves_on=None)
        cfg, profile, _box = make_profile(tmp_path, solver)
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        result = profile.solve_for_sync(solver, b"frame")
        assert result["RA"] is None
        assert len(solver.calls) == 2
        assert profile.searching is True

    def test_without_a_search_it_is_one_solve_with_the_profile_fov(self, tmp_path):
        solver = FakeSolver()
        cfg, profile, _box = make_profile(tmp_path, solver)
        profiles.set_fov(cfg, IDS_KEY, 25.6, "user")
        profile.on_camera_connected(IDS_PLAIN_HELLO)
        solver.apply_pending_database()
        profile.solve_for_sync(solver, b"frame")
        assert solver.calls == [("wide", 25.6, pytest.approx(2.56))]

    def test_the_engine_sync_step_uses_the_search(self, tmp_path):
        cfg = ConfigManager(config_dir=tmp_path)
        eng = Engine(config=cfg)
        eng._solver = FakeSolver(solves_on="wide", solved_fov=25.5)
        eng._fork_camera_connected(IDS_SENSOR_HELLO)
        eng._frame_buffer.set(b"frame", time.monotonic(), 1)
        eng._sync_lock.acquire()
        eng._perform_sync_solve()  # no candidates in the fake result: fails after the solve
        assert profiles.get_fov(cfg, IDS_KEY) == (25.5, "learned")
        assert eng._solver.databases.active.name == "narrow"  # the swap is the next apply


class TestSolverThreadSearch:
    def test_the_thread_searches_and_ends_on_the_database_that_solves(self, tmp_path):
        solver = FakeSolver(solves_on="wide", solved_fov=25.6)
        cfg, profile, _box = make_profile(tmp_path, solver)
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        fb, sm = LatestFrame(), StateMachine()
        st = SolverThread(solver, fb, PointingState(), sm, ConfigManager(config_dir=tmp_path / "tcfg"),
                          solver_params=profile.params, fov_tracker=profile)
        sm.transition(EngineState.SYNC)
        sm.transition(EngineState.SYNC_CONFIRM)
        st.start()
        try:
            for i in range(1, 30):
                fb.set(b"frame", time.monotonic(), i)
                if wait_for(lambda: not profile.searching, timeout=0.1):
                    break
            assert profile.searching is False
            assert profiles.get_fov(cfg, IDS_KEY) == (25.6, "learned")
            assert wait_for(lambda: solver.databases.active.name == "wide")  # the thread swapped
            assert wait_for(lambda: sm.state == EngineState.TRACKING)
        finally:
            st.stop()
        # It tried the wide database first (the sensor guess), solved on it at once.
        assert solver.calls[0] == ("wide", 25.0, 7.0)

    def test_a_search_that_never_solves_keeps_alternating_and_changes_nothing(self, tmp_path):
        solver = FakeSolver(solves_on=None)
        cfg, profile, _box = make_profile(tmp_path, solver)
        profile.on_camera_connected(IDS_SENSOR_HELLO)
        fb, sm = LatestFrame(), StateMachine()
        st = SolverThread(solver, fb, PointingState(), sm, ConfigManager(config_dir=tmp_path / "tcfg"),
                          solver_params=profile.params, fov_tracker=profile)
        sm.transition(EngineState.SYNC)
        sm.transition(EngineState.SYNC_CONFIRM)
        st.start()
        try:
            for i in range(1, 7):
                fb.set(b"frame", time.monotonic(), i)
                wait_for(lambda i=i: len(solver.calls) >= i, timeout=2)
        finally:
            st.stop()
        assert [c[0] for c in solver.calls[:6]] == ["wide", "narrow"] * 3
        assert profile.searching is True
        assert profiles.get_fov(cfg, IDS_KEY) == (None, None)
