# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 5.4).
"""Solver-database registry, selection and swapping.

PushNav ships two prebuilt tetra3rs databases, a narrow one (7..10 deg) and
a wide one (18..32 deg). ``data/solver_dbs.json``, written by
``scripts/build_tetra3rs_catalog.py``, lists them with their FOV ranges, so
the ranges are not hardcoded here.

Rules:

* The active database is the one whose ``[min_fov, max_fov]`` contains the
  FOV. If two contain it, the narrower one wins. If none does, the closest
  range is used and the selection is flagged ``in_range=False``.
* Only one database is loaded at a time (RAM matters on a Pi). A swap is
  requested from any thread with :meth:`SolverDatabases.request` and carried
  out by :meth:`SolverDatabases.apply_pending` **on the solver thread**,
  between two solves: the old database is dropped once the new one loaded.
"""

from __future__ import annotations

import json
import logging
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

import tetra3rs

from evf.paths import solver_db_dir, tetra3rs_database_path

logger = logging.getLogger(__name__)

REGISTRY_FILENAME = "solver_dbs.json"

# Name and FOV range of the database that ships since the first release.
# Used when solver_dbs.json is missing or unreadable, so an install that
# only has tetra3rs_gaia.bin keeps working exactly as before.
_FALLBACK_NARROW_NAME = "narrow"
_FALLBACK_NARROW_RANGE = (7.0, 10.0)


@dataclass(frozen=True)
class DbInfo:
    """One entry of the solver-database registry."""

    name: str
    path: Path
    min_fov_deg: float
    max_fov_deg: float
    star_max_magnitude: float | None = None

    @property
    def width_deg(self) -> float:
        return self.max_fov_deg - self.min_fov_deg

    def distance_to(self, fov_deg: float) -> float:
        """0 inside the range, else degrees to the nearest end."""
        if fov_deg < self.min_fov_deg:
            return self.min_fov_deg - fov_deg
        if fov_deg > self.max_fov_deg:
            return fov_deg - self.max_fov_deg
        return 0.0


@dataclass(frozen=True)
class DbSelection:
    """Result of :func:`select_database`."""

    info: DbInfo
    in_range: bool  # False -> the UI shows the ``fov_out_of_range`` hint


def load_registry(db_dir: Path | None = None) -> list[DbInfo]:
    """Read ``solver_dbs.json`` from ``db_dir`` (default ``solver_db_dir()``).

    Entries whose file is missing are skipped (WARNING). If the registry is
    missing, unreadable or yields no usable entry, the narrow database at
    ``tetra3rs_database_path()`` is returned as the only entry.
    Result is sorted narrowest range first.
    """
    directory = Path(db_dir) if db_dir is not None else solver_db_dir()
    reg_path = directory / REGISTRY_FILENAME
    infos: list[DbInfo] = []
    try:
        with reg_path.open("r", encoding="utf-8") as f:
            raw = json.load(f)
        for entry in raw["databases"]:
            info = _parse_entry(directory, entry)
            if info is not None:
                infos.append(info)
    except FileNotFoundError:
        logger.warning("Solver registry %s not found; using the narrow database only", reg_path)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        logger.warning("Solver registry %s unusable (%s); using the narrow database only",
                       reg_path, exc)
        infos = []
    if not infos:
        return [_fallback_narrow()]
    return sorted(infos, key=lambda i: (i.width_deg, i.name))


def _parse_entry(directory: Path, entry: dict[str, Any]) -> DbInfo | None:
    name = str(entry["name"])
    filename = str(entry["file"])
    if Path(filename).name != filename:
        logger.warning("Solver registry entry %r: file %r must be a plain file name", name, filename)
        return None
    path = directory / filename
    if not path.is_file():
        logger.warning("Solver database %r is listed but %s is missing; skipping", name, path)
        return None
    lo, hi = float(entry["min_fov_deg"]), float(entry["max_fov_deg"])
    if not 0.0 < lo < hi:
        logger.warning("Solver registry entry %r: bad FOV range %s..%s; skipping", name, lo, hi)
        return None
    mag = entry.get("star_max_magnitude")
    return DbInfo(name, path, lo, hi, float(mag) if mag is not None else None)


def _fallback_narrow() -> DbInfo:
    lo, hi = _FALLBACK_NARROW_RANGE
    return DbInfo(_FALLBACK_NARROW_NAME, tetra3rs_database_path(), lo, hi)


def select_database(infos: list[DbInfo], fov_deg: float) -> DbSelection:
    """Pick the database for ``fov_deg`` (spec 5.4.3).

    Inside a range: that database (the narrower one when ranges overlap).
    Outside every range: the closest one, with ``in_range=False``.
    """
    if not infos:
        raise ValueError("no solver databases registered")
    containing = [i for i in infos if i.distance_to(fov_deg) == 0.0]
    if containing:
        return DbSelection(min(containing, key=lambda i: (i.width_deg, i.name)), True)
    closest = min(infos, key=lambda i: (i.distance_to(fov_deg), i.width_deg, i.name))
    return DbSelection(closest, False)


def _load_tetra3rs(path: Path) -> Any:
    return tetra3rs.SolverDatabase.load_from_file(str(path))


class SolverDatabases:
    """Holds the registry and the one loaded database.

    ``solver_db`` is read by whoever solves; the swap itself happens in
    :meth:`apply_pending`, which the solver thread calls between two solves.
    ``loader`` is injectable so tests need not load 50 MB files.
    """

    def __init__(
        self,
        registry: list[DbInfo],
        initial_fov_deg: float,
        loader: Callable[[Path], Any] = _load_tetra3rs,
    ) -> None:
        if not registry:
            raise ValueError("no solver databases registered")
        self._registry = sorted(registry, key=lambda i: (i.width_deg, i.name))
        self._loader = loader
        self._lock = threading.Lock()
        self._pending: DbInfo | None = None
        selection = select_database(self._registry, initial_fov_deg)
        self._selection = selection
        self._active = selection.info
        self._db = self._load(selection.info)

    # -- read side ------------------------------------------------------------

    @property
    def registry(self) -> list[DbInfo]:
        return list(self._registry)

    @property
    def db(self) -> Any:
        """The loaded tetra3rs SolverDatabase (grab once per solve)."""
        with self._lock:
            return self._db

    @property
    def active(self) -> DbInfo:
        with self._lock:
            return self._active

    @property
    def in_range(self) -> bool:
        """False while the active database does not contain the requested FOV."""
        with self._lock:
            return self._selection.in_range

    def select(self, fov_deg: float) -> DbSelection:
        return select_database(self._registry, fov_deg)

    # -- swap -----------------------------------------------------------------

    def request_for_fov(self, fov_deg: float) -> DbSelection:
        """Ask for the database that fits ``fov_deg``; any thread.

        Nothing is loaded here. If it differs from the active one, the swap
        happens in the next :meth:`apply_pending`. Returns the selection so
        the caller can show the out-of-range hint.
        """
        selection = self.select(fov_deg)
        with self._lock:
            self._selection = selection
            self._pending = selection.info if selection.info != self._active else None
        if not selection.in_range:
            logger.warning(
                "FOV %.2f deg is outside every solver database; using %r (%.1f..%.1f deg)",
                fov_deg, selection.info.name,
                selection.info.min_fov_deg, selection.info.max_fov_deg,
            )
        return selection

    def apply_pending(self) -> bool:
        """Load a requested database and drop the old one. Solver thread only.

        Returns True if the active database changed. A load failure keeps the
        old database (ERROR logged) and does not raise, so the solver thread
        survives it.
        """
        with self._lock:
            pending = self._pending
            self._pending = None
        if pending is None:
            return False
        try:
            new_db = self._load(pending)
        except Exception as exc:  # noqa: BLE001 - keep solving with the old db
            logger.error("Failed to load solver database %r from %s: %s",
                         pending.name, pending.path, exc)
            return False
        with self._lock:
            old = self._active
            self._db = new_db  # old handle is freed when the last solve drops it
            self._active = pending
        logger.info("Solver database swapped: %s -> %s", old.name, pending.name)
        return True

    # -- internals ------------------------------------------------------------

    def _load(self, info: DbInfo) -> Any:
        t0 = time.monotonic()
        db = self._loader(info.path)
        logger.info(
            "tetra3rs database loaded in %.2fs: %s [%s] (%s stars, %s patterns)",
            time.monotonic() - t0, info.path, info.name,
            getattr(db, "num_stars", "?"), getattr(db, "num_patterns", "?"),
        )
        for attr, expected in (("min_fov_deg", info.min_fov_deg), ("max_fov_deg", info.max_fov_deg)):
            actual = getattr(db, attr, None)
            if isinstance(actual, (int, float)) and abs(actual - expected) > 1e-3:
                logger.warning(
                    "Solver database %r: file says %s=%s, registry says %s",
                    info.name, attr, actual, expected,
                )
        return db
