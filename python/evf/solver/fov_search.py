# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.4.4).
"""FOV search for a camera profile that has no FOV yet.

Without a stored FOV and without a focal length the app would solve with the
legacy 8.86 deg and never find a field made by a wide lens. The search solves
alternate frames against each solver database, each time with that database's
centre FOV as the estimate and half its range as the tolerance. The first valid
solve decides: its ``fov_deg`` becomes the profile FOV (``"learned"``) and the
matching database stays.

This module only plans the steps. The caller loads the databases (both at
once for the duration of the search, spec 5.4.3) and stores the result.
"""

from __future__ import annotations

import threading
from dataclasses import dataclass
from typing import Sequence

from evf.solver.databases import DbInfo, select_database
from evf.solver.fov import SolverParams, fov_from_focal_length

# The sensor alone does not tell the FOV; the search starts with the database
# that fits a typical lens for it. 25 mm is the reference lens of spec 5.2.1
# (25.6 deg on the IDS sensor, 8.8 deg on the 3.8 mm wide OV9281).
GUESS_FOCAL_LENGTH_MM = 25.0


@dataclass(frozen=True)
class SearchStep:
    """One solve of the search: which database, with which FOV estimate and tolerance."""

    database: DbInfo
    params: SolverParams


def step_for(info: DbInfo) -> SearchStep:
    """Centre FOV of the database's range, half the range as tolerance."""
    centre = (info.min_fov_deg + info.max_fov_deg) / 2.0
    return SearchStep(info, SolverParams(centre, info.width_deg / 2.0))


def guess_fov_from_sensor(sensor_width_mm: float | None) -> float | None:
    """FOV a ``GUESS_FOCAL_LENGTH_MM`` lens gives on this sensor, None if the size is unknown."""
    if sensor_width_mm is None:
        return None
    try:
        return fov_from_focal_length(GUESS_FOCAL_LENGTH_MM, sensor_width_mm)
    except ValueError:
        return None


class FovSearch:
    """Round-robin over the databases, starting with the one that fits the sensor guess."""

    def __init__(self, registry: Sequence[DbInfo], guess_fov_deg: float | None = None) -> None:
        if not registry:
            raise ValueError("no solver databases registered")
        ordered = sorted(registry, key=lambda i: (i.width_deg, i.name))
        if guess_fov_deg is not None:
            first = select_database(ordered, guess_fov_deg).info
            ordered.remove(first)
            ordered.insert(0, first)
        self._order: list[DbInfo] = ordered
        self._lock = threading.Lock()
        self._turn = 0

    @property
    def order(self) -> list[DbInfo]:
        """The databases in the order the search tries them."""
        return list(self._order)

    def next_step(self, loaded: Sequence[DbInfo] | None = None) -> SearchStep:
        """The step for the next frame; alternates between the databases in ``loaded``.

        ``loaded`` limits the search to databases whose handle is in memory
        (default: all). Falls back to every database if none of them is.
        """
        candidates = [i for i in self._order if loaded is None or i in loaded] or self._order
        with self._lock:
            info = candidates[self._turn % len(candidates)]
            self._turn += 1
        return step_for(info)

    def steps(self, loaded: Sequence[DbInfo] | None = None) -> list[SearchStep]:
        """One step per database, in search order (the one-shot sync solve tries them all)."""
        candidates = [i for i in self._order if loaded is None or i in loaded] or self._order
        return [step_for(i) for i in candidates]
