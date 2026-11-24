# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 4, 5.2, 5.3, 5.4.4, 5.6, 6.1).
"""What the engine knows about the connected camera's optics.

Owns these things and keeps them consistent:

* the active camera profile (key from HELLO, FOV from ``camera.profiles``),
* the solver FOV (``SolverParamsHolder``, read by the solver thread), and the
  request to the database registry for the database that fits it,
* the stream geometry (``GeometryTracker``), which the web server needs for the
  sync origin and the navigation projection,
* how the FOV changes (slice S2): set by the user (focal length or degrees),
  learned from the solver's own ``fov_deg`` (spec 5.2.2) or found by the FOV
  search (spec 5.4.4).

``Engine`` only calls into this class from small hooks, so the upstream file
stays close to upstream.
"""

from __future__ import annotations

import logging
import threading
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Callable

from evf.camera.stream import FrameSizeGuard, GeometryTracker, jpeg_size
from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.solver.databases import DatabaseNotLoaded, DbInfo
from evf.solver.fov import (
    DEFAULT_RESOLVED_FOV,
    FOV_SOURCE_DEFAULT,
    FOV_SOURCE_LEARNED,
    FOV_SOURCE_USER,
    ResolvedFov,
    SolverParams,
    SolverParamsHolder,
    focal_length_from_fov,
    fov_from_focal_length,
    resolve_fov,
    sensor_width_mm,
    validate_fov,
)
from evf.solver.fov_learning import FovLearner
from evf.solver.fov_search import FovSearch, guess_fov_from_sensor
from evf.solver.solver import PlateSolver

if TYPE_CHECKING:
    from evf.camera.client import CameraClient

logger = logging.getLogger(__name__)

# ``/ws`` ``fov_source`` while the FOV search runs (spec 5.4.4, UI "Finding field of view...").
FOV_SOURCE_SEARCHING = "searching"


class FovRequestError(ValueError):
    """A ``POST /api/camera/fov`` request that cannot be applied (HTTP 400)."""


class NoCameraConnected(RuntimeError):
    """The FOV belongs to a connected camera's profile and there is none (HTTP 409)."""


@dataclass(frozen=True)
class SolvePlan:
    """What the solver thread solves next: FOV estimate and tolerance, and the database.

    ``database`` is None for the active database; only the FOV search names one.
    """

    params: SolverParams
    database: DbInfo | None = None


class CameraProfile:
    """Active profile, solver FOV and stream geometry of the engine.

    ``get_solver`` returns the engine's ``PlateSolver`` (None while it is not
    loaded or failed to load); it is a callable because the solver is created
    after this object.
    """

    def __init__(
        self,
        config: ConfigManager,
        frame_buffer: LatestFrame,
        get_solver: Callable[[], Any],
    ) -> None:
        self._config = config
        self._frame_buffer = frame_buffer
        self._get_solver = get_solver
        self._params = SolverParamsHolder()
        self._geometry = GeometryTracker()
        self._frame_size_guard = FrameSizeGuard(self._geometry.authoritative)
        self._active_key: str | None = None
        # Slice S2. The solver thread (learning, search), the web server (user FOV)
        # and the engine (camera connect) all change the FOV: serialise them.
        self._lock = threading.RLock()
        self._learner = FovLearner()
        self._search: FovSearch | None = None
        self._hello: dict | None = None
        self._sensor_mm: float | None = None

    # -- read side -------------------------------------------------------------

    @property
    def params(self) -> SolverParamsHolder:
        """Current solver FOV; the solver thread and the sync solve read it."""
        return self._params

    @property
    def geometry(self) -> GeometryTracker:
        return self._geometry

    @property
    def frame_size_guard(self) -> FrameSizeGuard:
        """Tells the solver thread which results to drop: frames of another size than announced (spec 5.1)."""
        return self._frame_size_guard

    @property
    def active_key(self) -> str | None:
        """Profile key of the connected camera (None: none connected, or unidentifiable)."""
        return self._active_key

    # -- startup (spec 5.4.3) --------------------------------------------------

    def restore_last(self) -> float:
        """Before the solver loads: FOV of the last used profile, as the initial FOV.

        With no ``camera.last_camera_key`` (first start, or a config from before
        profiles) this is the legacy 8.86 deg, so the narrow database is loaded,
        exactly as before.
        """
        key = profiles.last_camera_key(self._config)
        resolved = DEFAULT_RESOLVED_FOV
        if key is not None:
            resolved = resolve_fov(*profiles.get_fov(self._config, key))
            logger.info("Last camera %r: FOV %.2f deg (%s)", key, resolved.fov_h_deg, resolved.source)
        self._params.set(resolved)
        return resolved.fov_h_deg

    def request_database(self) -> None:
        """Tell the registry which database fits the current FOV (swap runs on the solver thread)."""
        solver = self._get_solver()
        databases = getattr(solver, "databases", None)
        if databases is not None:
            databases.request_for_fov(self._params.resolved.fov_h_deg)

    # -- camera connect --------------------------------------------------------

    def on_camera_connected(self, hello: dict, client: CameraClient | None = None) -> None:
        """A camera answered HELLO: activate its profile, FOV and geometry.

        ``client`` (the connected ``CameraClient``) matters in a v2 session only: its
        STREAM_INFO is the geometry (spec 5.1). A v1 session takes HELLO's size.
        """
        with self._lock:
            v2 = client is not None and client.session_version >= 2
            self._geometry.connect(hello, (lambda: client.stream_info) if v2 else None)
            key = profiles.camera_key_from_hello(hello)
            self._active_key = key
            self._hello = dict(hello) if isinstance(hello, dict) else None
            self._sensor_mm = sensor_width_mm(hello)
            self._config.active_camera_key = key  # has_calibration compares with calibration.camera_key
            if key is None:
                logger.warning("Camera HELLO has no camera_key, backend or camera_model; using the default FOV")
                resolved = DEFAULT_RESOLVED_FOV
            else:
                profiles.ensure_profile(self._config, key)
                profiles.set_last_camera_key(self._config, key)
                profiles.claim_legacy_calibration(self._config, key)
                resolved = resolve_fov(*profiles.get_fov(self._config, key))
            self._apply(resolved)
            self._restart_learning_or_search(resolved)
            logger.info("Camera profile %r: FOV %.2f deg (%s)", key, resolved.fov_h_deg,
                        FOV_SOURCE_SEARCHING if self._search is not None else resolved.source)

    def on_camera_gone(self) -> None:
        """The camera could not be started: back to the no-camera geometry.

        The FOV stays: it describes the last used camera, which is also what the
        solver database matches.
        """
        with self._lock:
            self._active_key = None
            self._hello = None
            self._sensor_mm = None
            self._geometry.reset()
            self._stop_search()
            self._learner.reset()

    def _apply(self, resolved: ResolvedFov) -> SolverParams:
        params = self._params.set(resolved)
        self.request_database()
        return params

    # -- FOV: search, learning, user (spec 5.2, 5.2.2, 5.4.4, 6.1) ---------------

    @property
    def searching(self) -> bool:
        """True while the FOV search runs (shown as "Finding field of view...")."""
        return self._search is not None

    def _databases(self):
        return getattr(self._get_solver(), "databases", None)

    def _restart_learning_or_search(self, resolved: ResolvedFov) -> None:
        """New camera or changed FOV: start from scratch.

        The search runs only for a profile with no FOV at all and a known sensor
        size (the C# camera server sends it). Every other camera keeps today's
        behaviour: no FOV means the legacy 8.86 deg and the narrow database.
        """
        self._stop_search()
        self._learner.reset()
        if self._active_key is None or resolved.source != FOV_SOURCE_DEFAULT or self._sensor_mm is None:
            return
        databases = self._databases()
        if databases is None or len(databases.registry) < 2:
            return
        self._search = FovSearch(databases.registry, guess_fov_from_sensor(self._sensor_mm))
        logger.info("Profile %r has no FOV: searching with %s", self._active_key,
                    " and ".join(i.name for i in self._search.order))

    def _stop_search(self) -> None:
        if self._search is None:
            return
        self._search = None
        databases = self._databases()
        if databases is not None:
            databases.release_extras()

    def _ensure_search_databases(self, databases, search: FovSearch) -> None:
        """The search alternates between the databases, so it holds them all (spec 5.4.3).

        Called without ``self._lock``: a load takes up to 0.15 s and ``/ws`` reads
        ``camera_info()`` under that lock. Checked on every call (cheap), so a failed
        load is retried and a new search after an old one re-arms the holding.
        """
        if databases.holding and len(databases.loaded()) >= len(databases.registry):
            return
        databases.hold_all()
        with self._lock:
            if self._search is None:  # no search any more: do not keep what it loaded
                databases.release_extras()

    def plan(self) -> SolvePlan:
        """What the solver thread solves next. Called once per frame on that thread."""
        with self._lock:
            search = self._search
        databases = self._databases()
        if search is not None and databases is not None:
            self._ensure_search_databases(databases, search)
            step = search.next_step(databases.loaded())
            return SolvePlan(step.params, step.database)
        return SolvePlan(self._params.get())

    def solve_succeeded(self, result: dict, plan: SolvePlan | None = None) -> None:
        """A valid solve: it ends a FOV search, or counts towards learning (spec 5.2.2)."""
        with self._lock:
            key = self._active_key
            if key is None:
                return
            if self._search is not None:
                self._finish_search(key, result, plan)
                return
            resolved = self._params.resolved
            databases = self._databases()
            if resolved.source != FOV_SOURCE_LEARNED or databases is None:
                return  # nothing stored yet (default), or set by the user: leave it alone
            active = databases.active
            new_fov = self._learner.add(
                result.get("fov_deg"),
                current_fov_deg=resolved.fov_h_deg,
                source=resolved.source,
                db_min_fov_deg=active.min_fov_deg,
                db_max_fov_deg=active.max_fov_deg,
            )
            if new_fov is not None:
                logger.info("Learned FOV %.2f deg (was %.2f deg)", new_fov, resolved.fov_h_deg)
                self._store(key, new_fov, FOV_SOURCE_LEARNED)

    def solve_failed(self) -> None:
        """An invalid or failed solve breaks the run of consecutive valid solves."""
        with self._lock:
            self._learner.miss()

    def _finish_search(self, key: str, result: dict, plan: SolvePlan | None) -> None:
        try:
            fov = validate_fov(result.get("fov_deg"))
        except ValueError:
            return  # a solve without a usable fov_deg decides nothing
        database = plan.database.name if plan is not None and plan.database is not None else "active"
        logger.info("FOV search done: solved with the %s database, FOV %.2f deg", database, fov)
        self._store(key, fov, FOV_SOURCE_LEARNED)  # requests the fitting database first, so ...
        self._stop_search()  # ... the release keeps it for the swap instead of loading it again

    def _store(self, key: str, fov_h_deg: float, source: str) -> None:
        """Persist the profile FOV, hand it to the solver and ask for the fitting database."""
        profiles.set_fov(self._config, key, fov_h_deg, source)
        self._apply(ResolvedFov(fov_h_deg, source))

    def solve_for_sync(self, solver: Any, frame: bytes) -> dict:
        """The engine's one-shot sync solve (state SYNC, no solver thread).

        Normally one solve with the profile FOV. During a FOV search it tries each
        database in turn until one solves, so the wizard works on a fresh profile.
        """
        with self._lock:
            search = self._search
        databases = self._databases()
        if search is None or databases is None:
            params = self._params.get()
            return solver.solve_frame(frame, params.fov_estimate_deg, params.fov_max_error_deg)
        self._ensure_search_databases(databases, search)
        result: dict = {}
        for step in search.steps(databases.loaded()):
            try:
                result = solver.solve_frame(
                    frame, step.params.fov_estimate_deg, step.params.fov_max_error_deg,
                    database=step.database,
                )
            except DatabaseNotLoaded:
                continue  # the search ended (user FOV, camera gone) while we solved: skip it quietly
            if PlateSolver.is_valid(result, self._config.min_matches, self._config.max_prob):
                self.solve_succeeded(result, SolvePlan(step.params, step.database))
                break
        return result

    def set_user_fov(
        self,
        *,
        focal_length_mm: object = None,
        fov_h_deg: object = None,
        reset: bool = False,
    ) -> dict:
        """``POST /api/camera/fov`` (spec 6.1): set or clear the connected camera's FOV.

        Exactly one of ``focal_length_mm`` (converted with the sensor size), ``fov_h_deg``
        (direct) or ``reset``. A value sets ``fov_source = "user"``, which learning never
        overwrites, and asks for the fitting database. ``reset`` clears a user value;
        search or learning takes over (a learned FOV is left as it is).

        Raises ``NoCameraConnected`` (HTTP 409) and ``FovRequestError`` (HTTP 400).
        """
        given = [focal_length_mm is not None, fov_h_deg is not None, bool(reset)]
        if sum(given) != 1:
            raise FovRequestError('give exactly one of "focal_length_mm", "fov_h_deg" or "reset"')
        with self._lock:
            key = self._active_key
            if key is None:
                raise NoCameraConnected("no camera is connected")
            if reset:
                if self._params.resolved.source == FOV_SOURCE_USER:
                    profiles.set_fov(self._config, key, None, None)
                    self._apply(DEFAULT_RESOLVED_FOV)
                    self._restart_learning_or_search(DEFAULT_RESOLVED_FOV)
                    logger.info("Profile %r: user FOV cleared", key)
                return self.fov_payload()
            if focal_length_mm is not None:
                if self._sensor_mm is None:
                    raise FovRequestError("the sensor size of this camera is unknown; enter the FOV in degrees")
                try:
                    fov = fov_from_focal_length(focal_length_mm, self._sensor_mm)
                except ValueError as exc:
                    raise FovRequestError(str(exc)) from exc
            else:
                try:
                    fov = validate_fov(fov_h_deg)  # type: ignore[arg-type]
                except ValueError as exc:
                    raise FovRequestError(str(exc)) from exc
            profiles.set_fov(self._config, key, fov, FOV_SOURCE_USER)
            self._apply(ResolvedFov(fov, FOV_SOURCE_USER))
            self._restart_learning_or_search(self._params.resolved)
            logger.info("Profile %r: user FOV %.2f deg", key, fov)
            return self.fov_payload()

    def fov_payload(self) -> dict:
        """FOV, its source and the database, for the API answer and ``/ws``."""
        resolved = self._params.resolved
        databases = self._databases()
        return {
            "fov_h_deg": resolved.fov_h_deg,
            "fov_source": FOV_SOURCE_SEARCHING if self._search is not None else resolved.source,
            "solver_db": databases.active.name if databases is not None else None,
            "fov_out_of_range": (not databases.in_range) if databases is not None else False,
        }

    def camera_info(self) -> dict | None:
        """The connected camera for the Settings panel, None when there is none."""
        with self._lock:
            hello, key, sensor_mm = self._hello, self._active_key, self._sensor_mm
        if hello is None:
            return None
        resolved = self._params.resolved
        focal = None
        if sensor_mm is not None and resolved.source in (FOV_SOURCE_USER, FOV_SOURCE_LEARNED):
            focal = round(focal_length_from_fov(resolved.fov_h_deg, sensor_mm), 1)
        return {
            "camera_model": hello.get("camera_model"),
            "backend": hello.get("backend"),
            "camera_key": key,
            "sensor_width_mm": sensor_mm,
            "focal_length_mm": focal,
        }

    # -- web server ------------------------------------------------------------

    def geometry_payload(self) -> dict:
        """Geometry, FOV and database state for ``/ws`` (spec 5.6). Any thread."""
        self._correct_geometry_from_first_frame()
        geometry = self._geometry.get()
        return {
            "image_w": geometry.width,
            "image_h": geometry.height,
            **self.fov_payload(),
            "camera_info": self.camera_info(),
        }

    def _correct_geometry_from_first_frame(self) -> None:
        """v1 sessions: the first frame after connect overrides the HELLO size (spec 5.1)."""
        if not self._geometry.awaiting_frame:
            return
        jpeg, _timestamp, _frame_id = self._frame_buffer.get()
        if jpeg is None:
            return
        size = jpeg_size(jpeg)
        if size is not None:
            self._geometry.observe_frame(size)
