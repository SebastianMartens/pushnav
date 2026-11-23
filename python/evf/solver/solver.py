# Copyright (C) 2026 Arun Venkataswamy
#
# This file is part of PushNav.
#
# PushNav is free software: you can redistribute it and/or modify it
# under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PushNav is distributed in the hope that it will be useful, but
# WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
# General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PushNav. If not, see <https://www.gnu.org/licenses/>.

"""Plate solver — tetra3rs wrapper for single-frame plate solving.

Internals use the Rust-backed tetra3rs (MIT, GPL-3 compatible). The
external API and result-dict shape match the legacy tetra3 wrapper so
the rest of the engine is unchanged. See
docs/superpowers/specs/2026-05-18-tetra3rs-bench-findings.md for the
parameter rationale and tetra3rs API quirks.
"""

from __future__ import annotations

import io
import logging
import time
from pathlib import Path

import numpy as np
import tetra3rs
from PIL import Image

# fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
from evf.solver.databases import DatabaseNotLoaded, DbInfo, SolverDatabases, load_registry
from evf.solver.fov import LEGACY_FOV_DEG

logger = logging.getLogger(__name__)

# Centroid extraction parameters — see the bench-findings doc for why
# matched_filter_sigma=2.0 is load-bearing (without it, M42 nebulosity
# crowds out real stars in the brightest-N list and orion.png fails).
_CENTROID_PARAMS = dict(
    sigma_threshold=5.0,
    max_pixels=2000,
    max_centroids=30,
    matched_filter_sigma=2.0,
)


# fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
class PlateSolver:
    """Load a tetra3rs database and solve frames on demand.

    Which database is loaded comes from the registry in ``solver_dbs.json``
    (``evf.solver.databases``). Only one is loaded at a time: the one for
    ``initial_fov_deg`` (default the legacy 8.86 deg, narrow). A swap is
    requested through ``databases`` and done by ``apply_pending_database()``
    (the solver thread between solves; the engine when none runs).
    """

    def __init__(
        self,
        database_path: Path | None = None,
        databases: SolverDatabases | None = None,
        initial_fov_deg: float = LEGACY_FOV_DEG,  # fork: camera-controls
    ) -> None:
        if databases is not None:
            self._databases = databases
        elif database_path is not None:
            # Explicit file (tests, tools): a registry of one, valid for any FOV.
            info = DbInfo(Path(database_path).stem, Path(database_path), 0.1, 180.0)
            self._databases = SolverDatabases([info], initial_fov_deg)
        else:
            # initial_fov_deg: the FOV of the last used camera profile, so the
            # database for it is the one loaded at startup (spec 5.4.3).
            self._databases = SolverDatabases(load_registry(), initial_fov_deg)

    @property
    def databases(self) -> SolverDatabases:
        return self._databases

    def apply_pending_database(self) -> bool:
        """Swap to a requested database. Call on the solver thread between solves."""
        return self._databases.apply_pending()

    def solve_frame(
        self,
        image_bytes: bytes,
        fov_estimate_deg: float,
        fov_max_error_deg: float,
        database: DbInfo | None = None,  # fork: camera-controls (FOV search)
    ) -> dict:
        """Solve a single image frame. Returns a tetra3-compatible result dict.

        ``fov_estimate_deg`` / ``fov_max_error_deg`` are the horizontal FOV
        guess and its tolerance (legacy setup: 8.86 / 1.5, see
        ``evf.solver.fov``). ``database`` solves against that registry entry
        instead of the active one (FOV search holds both, spec 5.4.4).

        Keys returned (preserving the legacy contract):
          RA, Dec, Roll          — degrees, J2000
          Matches                — number of matched stars (int)
          Prob                   — false-positive probability (float)
          T_extract              — centroid extraction time, ms
          T_solve                — solver internal time, ms
          all_centroids          — list[[y, x], ...] top-left-origin pixels
          matched_centroids      — list[[y, x], ...] top-left-origin pixels
          matched_stars          — list[[ra_deg, dec_deg, mag], ...]
          image_size             — (height, width)
          fov_deg                — solved horizontal FOV in degrees, None if not solved

        On unsolvable frames, RA/Dec/Roll are None, Matches=0, Prob=None,
        and the matched_* lists are empty. all_centroids is populated
        when extraction succeeded regardless of whether the solve did.
        """
        img = Image.open(io.BytesIO(image_bytes)).convert("L")
        arr = np.asarray(img, dtype=np.float64)
        h, w = arr.shape

        t0 = time.monotonic()
        extraction = tetra3rs.extract_centroids(arr, **_CENTROID_PARAMS)
        t_extract_ms = (time.monotonic() - t0) * 1000

        all_centroids = self._centroids_list_to_yx(extraction.centroids, h, w)

        db = self._databases.db  # one handle for the whole solve, even across a swap
        if database is not None:  # fork: camera-controls
            db = self._databases.db_for(database)
            if db is None:
                raise DatabaseNotLoaded(f"solver database {database.name!r} is not loaded")
        t0 = time.monotonic()
        result = db.solve_from_centroids(
            extraction.centroids,
            fov_estimate_deg=fov_estimate_deg,
            fov_max_error_deg=fov_max_error_deg,
            image_shape=(h, w),
        )
        t_solve_ms = (time.monotonic() - t0) * 1000

        out: dict = {
            "RA": None,
            "Dec": None,
            "Roll": None,
            "Matches": 0,
            "Prob": None,
            "T_extract": t_extract_ms,
            "T_solve": t_solve_ms,
            "all_centroids": all_centroids,
            "matched_centroids": [],
            "matched_stars": [],
            "image_size": (h, w),
            "fov_deg": None,  # fork: camera-controls
        }

        # result.status is a plain str from the pyo3 binding (not a Python
        # Enum); tetra3rs exposes no SolveStatus type at module level.
        if result is None or result.status != "match_found":
            return out

        # Roll convention: tetra3rs roll_deg is ~180° flipped from the
        # legacy evf result (which negated tetra3's Roll as `360 - r`).
        # Empirically verified across the five test samples.
        roll = (result.roll_deg + 180.0) % 360.0

        # result.matched_centroids is a numpy array of indices into
        # extraction.centroids (not Centroid objects).
        matched_centroids = self._indices_to_yx(
            result.matched_centroids, extraction.centroids, h, w
        )
        matched_stars = self._catalog_ids_to_radec_mag(db, result.matched_catalog_ids)

        out.update({
            "RA": result.ra_deg,
            "Dec": result.dec_deg,
            "Roll": roll,
            "Matches": result.num_matches or 0,
            "Prob": result.probability,
            "fov_deg": result.fov_deg,  # fork: camera-controls (FOV learning)
            "matched_centroids": matched_centroids,
            "matched_stars": matched_stars,
        })
        return out

    def _centroids_list_to_yx(self, centroids, h: int, w: int) -> list:
        """Convert a list of tetra3rs Centroid objects (image-center origin,
        +X right, +Y down) to legacy [y, x] top-left-origin pixel coords."""
        cx, cy = w / 2.0, h / 2.0
        return [
            [float(c.y + cy), float(c.x + cx)]
            for c in centroids
        ]

    def _indices_to_yx(self, indices, centroids, h: int, w: int) -> list:
        """Look up matched centroid indices in the extraction list and
        return legacy [y, x] top-left-origin pixel coords."""
        cx, cy = w / 2.0, h / 2.0
        out = []
        for idx in indices:
            c = centroids[int(idx)]
            out.append([float(c.y + cy), float(c.x + cx)])
        return out

    def _catalog_ids_to_radec_mag(self, db, ids) -> list:
        """Build the legacy [[ra_deg, dec_deg, mag], ...] list from
        tetra3rs Gaia source IDs via db.get_star_by_id().

        Note: CatalogStar exposes .magnitude (not .mag); we normalise to
        'mag' in the returned tuple to match the legacy contract.
        """
        out = []
        for sid in ids:
            star = db.get_star_by_id(sid)
            out.append([float(star.ra_deg), float(star.dec_deg), float(star.magnitude)])
        return out

    @staticmethod
    def is_valid(
        result: dict, min_matches: int = 8, max_prob: float = 0.2
    ) -> bool:
        """Check if a solve result meets quality thresholds."""
        if result.get("RA") is None:
            return False
        if result.get("Matches", 0) < min_matches:
            return False
        if result.get("Prob", 1.0) > max_prob:
            return False
        return True
