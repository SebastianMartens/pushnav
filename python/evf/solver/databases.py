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
  requested from any thread with :meth:`SolverDatabases.request_for_fov` and carried
  out by :meth:`SolverDatabases.apply_pending`, normally on the solver thread
  between two solves; the engine also calls it when no solver thread is
  running (camera connect, sync solve). Callers are serialised by a load lock,
  so two of them never load at the same time: the old database is dropped
  once the new one loaded.
* Exception (spec 5.4.3): FOV search holds every database for its duration
  (:meth:`SolverDatabases.hold_all`, :meth:`SolverDatabases.release_extras`).
"""

from __future__ import annotations

import json
import logging
import math
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

import tetra3rs

from evf.paths import solver_db_dir, tetra3rs_database_path
from evf.solver.fov import validate_fov

logger = logging.getLogger(__name__)

REGISTRY_FILENAME = "solver_dbs.json"

# Name and FOV range of the database that ships since the first release.
# Used when solver_dbs.json is missing or unreadable, so an install that
# only has tetra3rs_gaia.bin keeps working exactly as before.
_FALLBACK_NARROW_NAME = "narrow"
_FALLBACK_NARROW_RANGE = (7.0, 10.0)


class DatabaseNotLoaded(RuntimeError):
    """A solve asked for a database that is not in memory (a FOV search ended meanwhile)."""


class NarrowDatabaseError(Exception):
    """The registry has no usable ``narrow`` database.

    The narrow database is the base every install has had since the first
    release, so a registry that cannot provide it fails loudly (like a missing
    tetra3rs_gaia.bin always did: the engine logs "Failed to load tetra3rs
    database" and runs without a solver) instead of quietly solving the legacy
    8.86 deg setup with the wide database, where every solve would fail.
    """


class NarrowDatabaseMissing(NarrowDatabaseError, FileNotFoundError):
    """The registry lists the narrow database but its file is missing."""


class NarrowDatabaseInvalid(NarrowDatabaseError, ValueError):
    """The registry's narrow entry is unusable (bad FOV range, bad file name),
    or the registry lists other databases but no narrow one."""


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

    Raises :class:`NarrowDatabaseMissing` if the entry named ``narrow`` is
    listed but its file is missing, and :class:`NarrowDatabaseInvalid` if that
    entry is unusable or the registry lists databases but no ``narrow`` one.
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
    except NarrowDatabaseError as exc:
        logger.error("%s", exc)
        raise
    except FileNotFoundError:
        logger.warning("Solver registry %s not found; using the narrow database only", reg_path)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        logger.warning("Solver registry %s unusable (%s); using the narrow database only",
                       reg_path, exc)
        infos = []
    if not infos:
        return [_fallback_narrow()]
    if not any(i.name == _FALLBACK_NARROW_NAME for i in infos):
        exc = NarrowDatabaseInvalid(
            f"Solver registry {reg_path} lists {', '.join(repr(i.name) for i in infos)} "
            f"but no usable {_FALLBACK_NARROW_NAME!r} database"
        )
        logger.error("%s", exc)
        raise exc
    return sorted(infos, key=lambda i: (i.width_deg, i.name))


def _parse_entry(directory: Path, entry: dict[str, Any]) -> DbInfo | None:
    name = str(entry["name"])
    filename = str(entry["file"])
    if Path(filename).name != filename:
        return _skip_entry(name, f"file {filename!r} must be a plain file name")
    path = directory / filename
    if not path.is_file():
        if name == _FALLBACK_NARROW_NAME:
            raise NarrowDatabaseMissing(f"Solver database {name!r} is listed but {path} is missing")
        logger.warning("Solver database %r is listed but %s is missing; skipping", name, path)
        return None
    lo, hi = float(entry["min_fov_deg"]), float(entry["max_fov_deg"])
    if not (math.isfinite(lo) and math.isfinite(hi) and 0.0 < lo < hi):
        return _skip_entry(name, f"bad FOV range {lo}..{hi}")
    mag = entry.get("star_max_magnitude")
    return DbInfo(name, path, lo, hi, float(mag) if mag is not None else None)


def _skip_entry(name: str, reason: str) -> None:
    """An unusable entry is skipped (WARNING); for ``narrow`` that is an error."""
    if name == _FALLBACK_NARROW_NAME:
        raise NarrowDatabaseInvalid(f"Solver database {name!r} is unusable: {reason}")
    logger.warning("Solver registry entry %r: %s; skipping", name, reason)
    return None


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
    fov_deg = validate_fov(fov_deg)  # NaN would count as "in range" (distance_to(nan) == 0)
    containing = [i for i in infos if i.distance_to(fov_deg) == 0.0]
    if containing:
        return DbSelection(min(containing, key=lambda i: (i.width_deg, i.name)), True)
    closest = min(infos, key=lambda i: (i.distance_to(fov_deg), i.width_deg, i.name))
    return DbSelection(closest, False)


def _load_tetra3rs(path: Path) -> Any:
    return tetra3rs.SolverDatabase.load_from_file(str(path))


class SolverDatabases:
    """Holds the registry and the one loaded database.

    ``db`` is read by whoever solves; the swap itself happens in
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
        # Held for a whole swap: two callers must not load the same file at once
        # (peak RAM). Never taken while holding _lock; _lock is only held briefly.
        self._load_lock = threading.Lock()
        selection = select_database(self._registry, initial_fov_deg)
        self._active = selection.info
        self._db = self._load(selection.info)
        # What the last request asked for. A swap is due whenever _wanted
        # differs from _active, so a request that arrives while a load is
        # running is never lost: the next apply_pending() sees it.
        self._wanted = selection.info
        self._fov_deg = initial_fov_deg
        self._failed: DbInfo | None = None  # target whose load failed (no retry loop)
        self._extras: dict[str, Any] = {}  # fork: FOV search: loaded besides the active one
        self._holding = False  # fork: a FOV search holds every database (hold_all .. release_extras)

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
        """False while the active database does not contain the requested FOV.

        Derived from the database actually loaded, so it stays truthful after
        a failed load or while a swap is still due.
        """
        with self._lock:
            return self._active.distance_to(self._fov_deg) == 0.0

    def select(self, fov_deg: float) -> DbSelection:
        return select_database(self._registry, fov_deg)

    def db_for(self, info: DbInfo) -> Any | None:
        """Handle of ``info``'s database if it is loaded (the active one, or held for FOV search)."""
        with self._lock:
            if info == self._active:
                return self._db
            return self._extras.get(info.name)

    @property
    def holding(self) -> bool:
        """True from ``hold_all()`` until ``release_extras()`` (a FOV search is running)."""
        with self._lock:
            return self._holding

    def loaded(self) -> list[DbInfo]:
        """The databases whose handle is in memory, narrowest range first."""
        with self._lock:
            return [i for i in self._registry if i == self._active or i.name in self._extras]

    # -- swap -----------------------------------------------------------------

    def request_for_fov(self, fov_deg: float) -> DbSelection:
        """Ask for the database that fits ``fov_deg``; any thread.

        Nothing is loaded here. If it differs from the active one, the swap
        happens in the next :meth:`apply_pending`. The last request always
        wins, also when it arrives during a load. A new request retries a
        target whose load failed. Returns the selection so the caller can
        show the out-of-range hint.
        """
        selection = self.select(fov_deg)
        with self._lock:
            self._wanted = selection.info
            self._fov_deg = fov_deg
            self._failed = None
            if not self._holding:  # fork: a copy kept for an earlier swap must not outlive its use
                self._drop_unwanted_extras_locked()
        if not selection.in_range:
            logger.warning(
                "FOV %.2f deg is outside every solver database; using %r (%.1f..%.1f deg)",
                fov_deg, selection.info.name,
                selection.info.min_fov_deg, selection.info.max_fov_deg,
            )
        return selection

    def apply_pending(self) -> bool:
        """Load a requested database and drop the old one.

        Called by the solver thread between two solves, and by the engine when
        no solver thread runs. Safe from several threads: callers queue on a
        load lock and re-check what is wanted once they hold it, so a second
        caller finds the swap done and loads nothing.

        Returns True if the active database changed. A load failure keeps the
        old database (ERROR logged) and does not raise, so the solver thread
        survives it; the failed target is not retried until the next request.
        """
        with self._load_lock:
            return self._apply_pending_locked()

    def _apply_pending_locked(self) -> bool:
        with self._lock:
            pending = self._wanted
            if pending == self._active or pending == self._failed:
                return False
            new_db = self._extras.get(pending.name)  # held for a FOV search: no second load
        try:
            if new_db is None:
                new_db = self._load(pending)
        except Exception as exc:  # noqa: BLE001 - keep solving with the old db
            logger.error("Failed to load solver database %r from %s: %s",
                         pending.name, pending.path, exc)
            with self._lock:
                if self._wanted == pending:  # not if a newer request arrived meanwhile
                    self._failed = pending
            return False
        with self._lock:
            old = self._active
            old_db = self._db
            self._extras.pop(pending.name, None)
            if self._holding:  # a FOV search still needs the old one
                self._extras[old.name] = old_db
            self._db = new_db  # old handle is freed when the last solve drops it
            self._active = pending
        logger.info("Solver database swapped: %s -> %s", old.name, pending.name)
        return True

    # -- FOV search (spec 5.4.3 / 5.4.4) ----------------------------------------

    def hold_all(self) -> bool:
        """Load every registered database that is not loaded yet and keep it.

        Only for the duration of a FOV search, which alternates solves between
        the databases. Callers queue on the load lock like a swap does; a
        database that fails to load is skipped (ERROR logged). While held, a
        swap moves the old active database into the extras instead of dropping
        it, and takes the new one from them instead of loading it again.

        Returns True if every database is loaded afterwards, so the caller can
        retry after a failure.
        """
        with self._lock:
            self._holding = True
        complete = True
        for info in self._registry:
            with self._load_lock:
                with self._lock:
                    if not self._holding:  # the search ended while we queued: load nothing more
                        return False
                    if info == self._active or info.name in self._extras:
                        continue
                try:
                    db = self._load(info)
                except Exception as exc:  # noqa: BLE001 - search with what is loaded
                    logger.error("Failed to load solver database %r from %s: %s", info.name, info.path, exc)
                    complete = False
                    continue
                with self._lock:
                    # ... or while we loaded: keep it only if a pending swap wants it
                    if not self._holding and info != self._wanted:
                        return False
                    self._extras[info.name] = db
                    if not self._holding:
                        return False
        return complete

    def release_extras(self) -> None:
        """Drop the databases ``hold_all`` loaded; the active one stays.

        A database that a pending swap wants stays until that swap adopts it, so
        the end of a FOV search does not load it a second time.
        """
        with self._lock:
            self._holding = False
            dropped = self._drop_unwanted_extras_locked()
        if dropped:
            logger.info("Released solver databases held for the FOV search: %s", ", ".join(dropped))

    def _drop_unwanted_extras_locked(self) -> list[str]:
        """Drop every extra except the one a pending swap wants. Caller holds ``_lock``."""
        keep = self._wanted.name if self._wanted != self._active else None
        dropped = [name for name in self._extras if name != keep]
        for name in dropped:
            del self._extras[name]
        return dropped

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
