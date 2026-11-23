# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.2 / 5.3).
"""Solver FOV parameters.

* ``SolverParams`` / ``max_error_for_fov``: what ``PlateSolver.solve_frame``
  gets (spec 5.3).
* ``resolve_fov``: the FOV of the active camera profile (spec 5.2). Slice S1
  implements steps 1 and 4 only: the profile value, else the legacy 8.86 deg.
* ``SolverParamsHolder``: the engine-owned, lock-protected current value that
  the solver thread reads at the start of every solve.

* ``sensor_width_mm`` / ``fov_from_focal_length`` / ``focal_length_from_fov``:
  the lens data path of spec 5.2 step 2.

Learning (5.2.2) and FOV search (5.4.4) are in ``fov_learning.py`` and
``fov_search.py``: both need the active database's range, and
``databases.py`` imports this module.
"""

from __future__ import annotations

import logging
import math
import numbers
import threading
from dataclasses import dataclass

logger = logging.getLogger(__name__)

# The OV9281 + stock lens setup the app was tuned on (SPEC_ARCHITECTURE.md 8.1).
LEGACY_FOV_DEG = 8.86
_MIN_FOV_MAX_ERROR_DEG = 1.5
_FOV_MAX_ERROR_FRACTION = 0.10
_MAX_FOV_DEG = 180.0  # exclusive upper bound of a horizontal FOV


def validate_fov(fov_deg: float) -> float:
    """Return ``fov_deg`` as a float, or raise ``ValueError`` if it cannot be a FOV.

    Rejects NaN, infinity, zero, negative values and 180 deg or more (and
    non-numbers): a horizontal field of view is below a half sphere. NaN in
    particular must never reach database selection: every comparison with NaN
    is false, so ``DbInfo.distance_to(nan)`` would report 0.0 ("in range").
    """
    if isinstance(fov_deg, bool) or not isinstance(fov_deg, numbers.Real):
        raise ValueError(f"FOV must be a number, got {fov_deg!r}")
    value = float(fov_deg)
    if not math.isfinite(value) or not 0.0 < value < _MAX_FOV_DEG:
        raise ValueError(f"FOV must be a finite number > 0 and < {_MAX_FOV_DEG:g} degrees, got {fov_deg!r}")
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


# -- FOV resolution (spec 5.2, steps 1 and 4) -----------------------------------

FOV_SOURCE_USER = "user"
FOV_SOURCE_LEARNED = "learned"
# Not stored in a profile: reported when no profile FOV applies (step 4).
FOV_SOURCE_DEFAULT = "default"
_STORED_SOURCES = (FOV_SOURCE_USER, FOV_SOURCE_LEARNED)


@dataclass(frozen=True)
class ResolvedFov:
    """The FOV the solver works with and where it came from."""

    fov_h_deg: float
    source: str  # "user" | "learned" | "default"

    @property
    def params(self) -> SolverParams:
        return SolverParams.for_fov(self.fov_h_deg)


DEFAULT_RESOLVED_FOV = ResolvedFov(LEGACY_FOV_DEG, FOV_SOURCE_DEFAULT)


def resolve_fov(profile_fov_deg: object, profile_source: object = None) -> ResolvedFov:
    """FOV for a profile's stored ``fov_h_deg`` / ``fov_source``.

    1. A valid profile value wins. A value with no (or an unknown)
       ``fov_source``, e.g. one typed into config.json by hand, counts as
       ``"user"``.
    4. Otherwise (nothing stored, or a value that is not a finite number
       in (0, 180) deg, logged as a WARNING): 8.86 deg, today's behaviour.
    """
    if profile_fov_deg is None:
        return DEFAULT_RESOLVED_FOV
    try:
        fov = validate_fov(profile_fov_deg)  # type: ignore[arg-type]
    except ValueError as exc:
        logger.warning("Ignoring the profile FOV (%s); using the default %.2f deg",
                       exc, LEGACY_FOV_DEG)
        return DEFAULT_RESOLVED_FOV
    source = profile_source if profile_source in _STORED_SOURCES else FOV_SOURCE_USER
    return ResolvedFov(fov, source)  # type: ignore[arg-type]


class SolverParamsHolder:
    """Lock-protected current FOV for the solver (spec 5.3).

    Written by the engine (camera connect, later FOV edits and learning), read
    by the solver thread and the sync solve. ``get()`` returns an immutable
    snapshot, so a change takes effect on the next frame.
    """

    def __init__(self, resolved: ResolvedFov = DEFAULT_RESOLVED_FOV) -> None:
        self._lock = threading.Lock()
        self._resolved = resolved
        self._params = resolved.params

    def get(self) -> SolverParams:
        """Snapshot for one solve."""
        with self._lock:
            return self._params

    @property
    def resolved(self) -> ResolvedFov:
        with self._lock:
            return self._resolved

    def set(self, resolved: ResolvedFov) -> SolverParams:
        params = resolved.params  # validates before anything changes
        with self._lock:
            self._resolved = resolved
            self._params = params
        return params


# -- Lens data (spec 5.2 step 2) ------------------------------------------------


def sensor_width_mm(hello: object) -> float | None:
    """Physical sensor width from HELLO ``capabilities.sensor``, or None if unknown.

    ``width`` (pixels) x ``pixel_size_um`` / 1000. None when the camera server
    does not send a sensor (every server but the C# one) or no pixel pitch (UVC).
    """
    if not isinstance(hello, dict):
        return None
    capabilities = hello.get("capabilities")
    sensor = capabilities.get("sensor") if isinstance(capabilities, dict) else None
    if not isinstance(sensor, dict):
        return None
    width, pitch = sensor.get("width"), sensor.get("pixel_size_um")
    if any(isinstance(v, bool) or not isinstance(v, numbers.Real) for v in (width, pitch)):
        return None
    result = float(width) * float(pitch) / 1000.0  # type: ignore[arg-type]
    return result if math.isfinite(result) and result > 0.0 else None


def fov_from_focal_length(focal_length_mm: object, sensor_width_mm_: float) -> float:
    """Horizontal FOV in degrees: ``2 * atan(sensor_width / (2 * f))``.

    Raises ``ValueError`` for a focal length that is not a finite positive number
    or that gives a FOV of 180 deg or more.
    """
    if isinstance(focal_length_mm, bool) or not isinstance(focal_length_mm, numbers.Real):
        raise ValueError(f"focal length must be a number, got {focal_length_mm!r}")
    f = float(focal_length_mm)
    if not math.isfinite(f) or f <= 0.0:
        raise ValueError(f"focal length must be a finite number > 0 mm, got {focal_length_mm!r}")
    return validate_fov(math.degrees(2.0 * math.atan(sensor_width_mm_ / (2.0 * f))))


def focal_length_from_fov(fov_h_deg: float, sensor_width_mm_: float) -> float:
    """Inverse of :func:`fov_from_focal_length`, for showing the lens in Settings."""
    return sensor_width_mm_ / (2.0 * math.tan(math.radians(validate_fov(fov_h_deg)) / 2.0))
