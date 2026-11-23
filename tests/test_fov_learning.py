# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.2.2, task 4.3).
"""FOV learning: median of five, the guards, and how the profile applies them."""

from __future__ import annotations

import logging

import pytest

from evf.config import profiles
from evf.solver.fov import LEGACY_SOLVER_PARAMS
from evf.solver.fov_learning import FovLearner, guarded_range
from tests.fov_fakes import (
    IDS_KEY, IDS_PLAIN_HELLO, OV_HELLO, FakeSolver, make_profile, valid_result,
)

NARROW_RANGE = (7.0, 10.0)
WIDE_RANGE = (18.0, 32.0)


def _feed(learner, values, current=25.0, source="learned", rng=WIDE_RANGE):
    out = None
    for v in values:
        out = learner.add(v, current_fov_deg=current, source=source,
                          db_min_fov_deg=rng[0], db_max_fov_deg=rng[1])
    return out


class TestGuardedRange:
    def test_five_percent_of_the_width_at_each_end(self):
        assert guarded_range(18.0, 32.0) == pytest.approx((18.7, 31.3))
        assert guarded_range(7.0, 10.0) == pytest.approx((7.15, 9.85))


class TestMedianOfFive:
    def test_nothing_before_the_fifth_solve(self):
        learner = FovLearner()
        for _ in range(4):
            assert _feed(learner, [26.0]) is None

    def test_the_fifth_solve_returns_the_median(self):
        assert _feed(FovLearner(), [26.0, 26.1, 25.9, 26.2, 26.0]) == 26.0

    def test_outliers_do_not_move_the_median(self):
        assert _feed(FovLearner(), [26.0, 31.0, 26.1, 19.0, 26.2]) == 26.1

    def test_a_round_starts_again_after_it_completes(self):
        learner = FovLearner()
        assert _feed(learner, [26.0] * 5) == 26.0
        assert _feed(learner, [26.0] * 4) is None  # a new round needs five more

    def test_a_failed_solve_breaks_the_run(self):
        learner = FovLearner()
        _feed(learner, [26.0] * 4)
        learner.miss()
        assert _feed(learner, [26.0]) is None  # one sample only, not five
        assert _feed(learner, [26.0] * 4) == 26.0

    @pytest.mark.parametrize("bad", [None, float("nan"), 0.0, -3.0, 200.0, "26"])
    def test_a_result_without_a_usable_fov_breaks_the_run(self, bad):
        learner = FovLearner()
        _feed(learner, [26.0] * 4)
        assert _feed(learner, [bad]) is None
        assert _feed(learner, [26.0]) is None  # the four earlier samples are gone


class TestGuards:
    def test_a_user_value_is_never_overwritten(self):
        assert _feed(FovLearner(), [26.5] * 10, current=25.0, source="user") is None

    def test_a_change_of_two_percent_or_less_is_not_stored(self):
        assert _feed(FovLearner(), [25.5] * 5, current=25.0) is None  # exactly 2 %
        assert _feed(FovLearner(), [25.0] * 5, current=25.0) is None

    def test_a_change_above_two_percent_is_stored(self):
        assert _feed(FovLearner(), [25.6] * 5, current=25.0) == 25.6  # 2.4 %

    def test_the_learned_value_must_stay_inside_the_range_minus_five_percent(self, caplog):
        learner = FovLearner()
        with caplog.at_level(logging.WARNING, logger="evf.solver.fov_learning"):
            assert _feed(learner, [31.5] * 5, current=30.0) is None   # > 31.3
            assert _feed(learner, [18.5] * 5, current=19.0) is None   # < 18.7
        assert caplog.text.count("outside the solver database range") == 1  # WARNING once

    def test_values_just_inside_the_guard_band_are_stored(self):
        assert _feed(FovLearner(), [31.2] * 5, current=30.0) == 31.2
        assert _feed(FovLearner(), [18.8] * 5, current=19.5) == 18.8

    def test_the_range_is_the_active_databases(self):
        assert _feed(FovLearner(), [9.9] * 5, current=9.0, rng=NARROW_RANGE) is None  # 9.85 limit
        assert _feed(FovLearner(), [9.5] * 5, current=9.0, rng=NARROW_RANGE) == 9.5

    def test_a_change_up_to_ten_percent_is_stored_at_once(self):
        assert _feed(FovLearner(), [27.5] * 5, current=25.0) == 27.5  # exactly 10 %

    def test_a_bigger_jump_needs_two_agreeing_rounds(self):
        learner = FovLearner()
        assert _feed(learner, [30.0] * 5, current=25.0) is None   # 20 %: first round waits
        assert _feed(learner, [30.1] * 5, current=25.0) == 30.1   # agrees within 2 %: stored

    def test_a_bigger_jump_with_disagreeing_rounds_is_not_stored(self):
        learner = FovLearner()
        assert _feed(learner, [30.0] * 5, current=25.0) is None
        assert _feed(learner, [28.5] * 5, current=25.0) is None   # 14 % away, not within 2 % of 30.0
        assert _feed(learner, [28.6] * 5, current=25.0) == 28.6   # but it agrees with the last round

    def test_a_round_back_near_the_current_value_forgets_the_jump(self):
        learner = FovLearner()
        assert _feed(learner, [30.0] * 5, current=25.0) is None
        assert _feed(learner, [25.0] * 5, current=25.0) is None
        assert _feed(learner, [30.0] * 5, current=25.0) is None   # first of a new pair again

    def test_reset_forgets_samples_and_pending_jump(self):
        learner = FovLearner()
        _feed(learner, [30.0] * 5, current=25.0)
        _feed(learner, [26.0] * 3, current=25.0)
        learner.reset()
        assert _feed(learner, [30.0] * 5, current=25.0) is None  # no second round to agree with


# -- through CameraProfile ------------------------------------------------------


def _solve_n(profile, fov_deg, n=5):
    for _ in range(n):
        plan = profile.plan()
        profile.solve_succeeded(valid_result(fov_deg), plan)


def _learned_profile(tmp_path, fov=25.0, solver=None):
    cfg, profile, box = make_profile(tmp_path)
    profiles.set_fov(cfg, IDS_KEY, fov, "learned")
    box["solver"] = solver or FakeSolver(initial_fov=fov)
    profile.on_camera_connected(IDS_PLAIN_HELLO)
    return cfg, profile, box["solver"]


class TestProfileLearning:
    def test_five_valid_solves_store_the_median_as_learned(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path)
        _solve_n(profile, 26.0)
        assert profiles.get_fov(cfg, IDS_KEY) == (26.0, "learned")
        assert profile.params.get().fov_estimate_deg == 26.0
        assert profile.params.resolved.source == "learned"

    def test_the_learned_value_is_in_the_file(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path)
        _solve_n(profile, 26.0)
        from evf.config.manager import ConfigManager
        again = ConfigManager(config_dir=cfg.path.parent)
        assert profiles.get_fov(again, IDS_KEY) == (26.0, "learned")

    def test_a_user_fov_is_never_overwritten(self, tmp_path):
        cfg, profile, box = make_profile(tmp_path, FakeSolver(initial_fov=25.0))
        profiles.set_fov(cfg, IDS_KEY, 25.0, "user")
        profile.on_camera_connected(IDS_PLAIN_HELLO)
        _solve_n(profile, 26.5, n=20)
        assert profiles.get_fov(cfg, IDS_KEY) == (25.0, "user")
        assert profile.params.get().fov_estimate_deg == 25.0

    def test_a_failed_solve_in_between_restarts_the_count(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path)
        _solve_n(profile, 26.0, n=4)
        profile.solve_failed()
        _solve_n(profile, 26.0, n=1)
        assert profiles.get_fov(cfg, IDS_KEY) == (25.0, "learned")
        _solve_n(profile, 26.0, n=4)
        assert profiles.get_fov(cfg, IDS_KEY) == (26.0, "learned")

    def test_outliers_among_the_five_are_ignored(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path)
        for fov in (26.0, 31.0, 26.1, 19.0, 26.2):
            profile.solve_succeeded(valid_result(fov), profile.plan())
        assert profiles.get_fov(cfg, IDS_KEY) == (26.1, "learned")

    def test_a_value_outside_the_database_range_is_not_stored(self, tmp_path, caplog):
        cfg, profile, _solver = _learned_profile(tmp_path, fov=31.0)
        with caplog.at_level(logging.WARNING, logger="evf.solver.fov_learning"):
            _solve_n(profile, 31.9)  # inside 18..32, outside the 5 % guard band
        assert profiles.get_fov(cfg, IDS_KEY) == (31.0, "learned")
        assert "not stored" in caplog.text

    def test_a_ten_percent_step_needs_two_rounds_in_the_profile_too(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path, fov=22.0)
        _solve_n(profile, 26.0)
        assert profiles.get_fov(cfg, IDS_KEY) == (22.0, "learned")
        _solve_n(profile, 26.1)
        assert profiles.get_fov(cfg, IDS_KEY) == (26.1, "learned")

    def test_a_result_without_fov_deg_learns_nothing(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path)
        _solve_n(profile, None, n=10)
        assert profiles.get_fov(cfg, IDS_KEY) == (25.0, "learned")

    def test_a_user_fov_set_during_a_round_wins(self, tmp_path):
        cfg, profile, _solver = _learned_profile(tmp_path)
        _solve_n(profile, 26.0, n=4)
        profile.set_user_fov(fov_h_deg=24.0)
        _solve_n(profile, 26.0, n=10)
        assert profiles.get_fov(cfg, IDS_KEY) == (24.0, "user")

    def test_no_stored_fov_and_no_search_learns_nothing(self, tmp_path):
        """The upstream camera: no profile FOV, no sensor in HELLO -> nothing changes."""
        cfg, profile, box = make_profile(tmp_path, FakeSolver())
        profile.on_camera_connected(OV_HELLO)
        _solve_n(profile, 8.4, n=20)
        assert profiles.get_fov(cfg, "linux-v4l2:OV9281") == (None, None)
        assert profile.params.get() == LEGACY_SOLVER_PARAMS
        assert profile.plan().database is None

    def test_no_camera_learns_nothing(self, tmp_path):
        cfg, profile, _box = make_profile(tmp_path, FakeSolver())
        profile.solve_succeeded(valid_result(8.4), None)  # must not raise


class TestSolverThreadFeedsLearning:
    def test_the_thread_learns_from_its_solves(self, tmp_path):
        import time
        from evf.config.manager import ConfigManager
        from evf.engine.frame_buffer import LatestFrame
        from evf.engine.pointing import PointingState
        from evf.engine.state import EngineState, StateMachine
        from evf.solver.thread import SolverThread
        from tests.fov_fakes import wait_for

        cfg, profile, solver = _learned_profile(tmp_path)
        solver.solves_on = "wide"
        solver.solved_fov = 26.0
        fb, sm = LatestFrame(), StateMachine()
        st = SolverThread(solver, fb, PointingState(), sm, ConfigManager(config_dir=tmp_path / "tcfg"),
                          solver_params=profile.params, fov_tracker=profile)
        sm.transition(EngineState.SYNC)
        sm.transition(EngineState.SYNC_CONFIRM)
        st.start()
        try:
            for i in range(1, 40):
                fb.set(b"frame", time.monotonic(), i)
                if wait_for(lambda: profiles.get_fov(cfg, IDS_KEY) == (26.0, "learned"), timeout=0.05):
                    break
            assert profiles.get_fov(cfg, IDS_KEY) == (26.0, "learned")
        finally:
            st.stop()
        # The thread solved with the FOV it read for each frame, 25.0 then 26.0, on the active database.
        assert solver.calls[0][:2] == ("wide", 25.0)
