# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.2 / 5.3).
"""Solver FOV parameters.

Only the pieces the solver needs today. FOV resolution from a camera profile,
learning and FOV search (spec 5.2, 5.4.4) are added here later.
"""

from __future__ import annotations

import math
import numbers
from dataclasses import dataclass

# The OV9281 + stock lens setup the app was tuned on (SPEC_ARCHITECTURE.md 8.1).
LEGACY_FOV_DEG = 8.86
_MIN_FOV_MAX_ERROR_DEG = 1.5
_FOV_MAX_ERROR_FRACTION = 0.10


def validate_fov(fov_deg: float) -> float:
    """Return ``fov_deg`` as a float, or raise ``ValueError`` if it cannot be a FOV.

    Rejects NaN, infinity, zero and negative values (and non-numbers). NaN in
    particular must never reach database selection: every comparison with NaN
    is false, so ``DbInfo.distance_to(nan)`` would report 0.0 ("in range").
    """
    if isinstance(fov_deg, bool) or not isinstance(fov_deg, numbers.Real):
        raise ValueError(f"FOV must be a number, got {fov_deg!r}")
    value = float(fov_deg)
    if not math.isfinite(value) or value <= 0.0:
        raise ValueError(f"FOV must be a finite number > 0 degrees, got {fov_deg!r}")
    return value


def max_error_for_fov(fov_deg: float) -> float:
    """tetra3rs ``fov_max_error_deg`` for a profile FOV (spec 5.3).

    ``max(1.5, 0.10 * fov)``: 1.5 deg at the legacy 8.86 deg, 2.56 deg at the
    25.6 deg IDS reference.
    """
    return max(_MIN_FOV_MAX_ERROR_DEG, _FOV_MAX_ERROR_FRACTION * validate_fov(fov_deg))


@dataclass(frozen=True)
class SolverParams:
    """FOV estimate and tolerance handed to ``PlateSolver.solve_frame``."""

    fov_estimate_deg: float
    fov_max_error_deg: float

    @classmethod
    def for_fov(cls, fov_deg: float) -> "SolverParams":
        return cls(validate_fov(fov_deg), max_error_for_fov(fov_deg))


# Today's behaviour: 8.86 deg +- 1.5 deg, narrow database.
LEGACY_SOLVER_PARAMS = SolverParams.for_fov(LEGACY_FOV_DEG)
