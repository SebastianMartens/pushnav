# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.2.2).
"""Learning the profile FOV from the solver's own ``fov_deg``.

Pure decision logic, no I/O: ``FovLearner`` collects the solved FOV of
consecutive valid solves and, once it has five, decides whether the median is
worth storing. The caller (``evf.engine.camera_profile``) owns the stored value
and passes in what the decision needs: the current FOV, where it came from and
the FOV range of the loaded database. It lives in its own module, not in
``fov.py``, so it can stay free of ``solver/databases.py`` (which imports
``fov.py``).

Guards against drift (spec 5.2.2):

* a ``"user"`` value is never overwritten;
* the learned value must lie inside the database's FOV range, shrunk by 5 % of
  the range width at each end, else it is not stored (WARNING once);
* a change of at most 2 % is not worth storing;
* a change of up to 10 % is stored at once; a bigger jump only after two
  consecutive rounds agree within 2 %.
"""

from __future__ import annotations

import logging
import statistics
from collections import deque

from evf.solver.fov import FOV_SOURCE_USER, validate_fov

logger = logging.getLogger(__name__)

WINDOW = 5            # consecutive valid solves per learning round
MIN_CHANGE = 0.02     # at most this relative change: leave the stored value alone
MAX_STEP = 0.10       # at most this relative change per update ...
AGREE_TOLERANCE = 0.02  # ... unless two consecutive rounds agree within this
RANGE_GUARD = 0.05    # shrink the database range by this fraction of its width at each end


def guarded_range(db_min_fov_deg: float, db_max_fov_deg: float) -> tuple[float, float]:
    """The FOV range a learned value may lie in: the database range minus 5 % at each end."""
    margin = RANGE_GUARD * (db_max_fov_deg - db_min_fov_deg)
    return db_min_fov_deg + margin, db_max_fov_deg - margin


class FovLearner:
    """Median-of-five FOV learning with the spec 5.2.2 guards."""

    def __init__(self, window: int = WINDOW) -> None:
        self._window = window
        self._samples: deque[float] = deque(maxlen=window)
        self._pending_jump: float | None = None  # a >10 % candidate waiting for a second round
        self._warned_range = False

    def reset(self) -> None:
        """Forget everything: a new camera, or a FOV set by hand."""
        self._samples.clear()
        self._pending_jump = None
        self._warned_range = False

    def miss(self) -> None:
        """A solve failed: the run of consecutive valid solves is broken."""
        self._samples.clear()

    def add(
        self,
        fov_deg: object,
        *,
        current_fov_deg: float,
        source: str,
        db_min_fov_deg: float,
        db_max_fov_deg: float,
    ) -> float | None:
        """Feed the ``fov_deg`` of one valid solve.

        Returns the FOV to store (as ``"learned"``) when this solve completes a
        round and the guards pass, else None. A solve without a usable
        ``fov_deg`` breaks the run, like a failed one.
        """
        try:
            sample = validate_fov(fov_deg)  # type: ignore[arg-type]
        except ValueError:
            self.miss()
            return None
        if source == FOV_SOURCE_USER:
            self._samples.clear()
            return None
        self._samples.append(sample)
        if len(self._samples) < self._window:
            return None
        median = statistics.median(self._samples)
        self._samples.clear()
        return self._decide(median, current_fov_deg, db_min_fov_deg, db_max_fov_deg)

    def _decide(self, median: float, current: float, db_min: float, db_max: float) -> float | None:
        low, high = guarded_range(db_min, db_max)
        if not low <= median <= high:
            if not self._warned_range:
                self._warned_range = True
                logger.warning(
                    "Learned FOV %.2f deg is outside the solver database range %.1f..%.1f deg "
                    "(minus a 5 %% guard band); not stored", median, db_min, db_max)
            self._pending_jump = None
            return None
        change = abs(median - current) / current
        if change <= MIN_CHANGE:
            self._pending_jump = None
            return None
        if change <= MAX_STEP:
            self._pending_jump = None
            return median
        # A jump of more than 10 %: only when a second round in a row agrees.
        pending = self._pending_jump
        if pending is not None and abs(median - pending) / pending <= AGREE_TOLERANCE:
            self._pending_jump = None
            return median
        self._pending_jump = median
        logger.info("FOV round gave %.2f deg, %.0f %% away from %.2f deg; waiting for a second round to agree",
                    median, change * 100.0, current)
        return None
