# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 4, 5.2, 5.3, 5.6).
"""What the engine knows about the connected camera's optics.

Owns three things and keeps them consistent:

* the active camera profile (key from HELLO, FOV from ``camera.profiles``),
* the solver FOV (``SolverParamsHolder``, read by the solver thread), and the
  request to the database registry for the database that fits it,
* the stream geometry (``GeometryTracker``), which the web server needs for the
  sync origin and the navigation projection.

``Engine`` only calls into this class from small hooks, so the upstream file
stays close to upstream.
"""

from __future__ import annotations

import logging
from typing import Any, Callable

from evf.camera.stream import GeometryTracker, jpeg_size
from evf.config import profiles
from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.solver.fov import (
    DEFAULT_RESOLVED_FOV,
    ResolvedFov,
    SolverParams,
    SolverParamsHolder,
    resolve_fov,
)

logger = logging.getLogger(__name__)


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
        self._active_key: str | None = None

    # -- read side -------------------------------------------------------------

    @property
    def params(self) -> SolverParamsHolder:
        """Current solver FOV; the solver thread and the sync solve read it."""
        return self._params

    @property
    def geometry(self) -> GeometryTracker:
        return self._geometry

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

    def on_camera_connected(self, hello: dict) -> None:
        """A camera answered HELLO: activate its profile, FOV and geometry."""
        self._geometry.connect(hello)
        key = profiles.camera_key_from_hello(hello)
        self._active_key = key
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
        logger.info("Camera profile %r: FOV %.2f deg (%s)", key, resolved.fov_h_deg, resolved.source)

    def on_camera_gone(self) -> None:
        """The camera could not be started: back to the no-camera geometry.

        The FOV stays: it describes the last used camera, which is also what the
        solver database matches.
        """
        self._active_key = None
        self._geometry.reset()

    def _apply(self, resolved: ResolvedFov) -> SolverParams:
        params = self._params.set(resolved)
        self.request_database()
        return params

    # -- web server ------------------------------------------------------------

    def geometry_payload(self) -> dict:
        """Geometry, FOV and database state for ``/ws`` (spec 5.6). Any thread."""
        self._correct_geometry_from_first_frame()
        geometry = self._geometry.get()
        resolved = self._params.resolved
        databases = getattr(self._get_solver(), "databases", None)
        return {
            "image_w": geometry.width,
            "image_h": geometry.height,
            "fov_h_deg": resolved.fov_h_deg,
            "fov_source": resolved.source,
            "solver_db": databases.active.name if databases is not None else None,
            "fov_out_of_range": (not databases.in_range) if databases is not None else False,
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
