# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 4).
"""Per-camera profiles in ``config.json`` (``camera.profiles``).

A profile belongs to one camera model and carries what depends on camera and
lens: the saved control values (``controls``: ``{"value": v, "unit": u}`` per
control id, spec 4.2) and the horizontal FOV (``fov_h_deg`` / ``fov_source``).
``camera.last_camera_key`` names the last connected camera.

Profile key (spec 4.2): HELLO ``camera_key`` when the camera server sends it,
in v1 and v2 sessions alike; else ``"<backend>:<camera_model>"`` from HELLO.

``ConfigManager.get`` hands out its internal dicts, so these helpers copy
before they change anything and write back through ``ConfigManager.set``
(which saves).
"""

from __future__ import annotations

import copy
import logging
from typing import Any

from evf.config.manager import ConfigManager

logger = logging.getLogger(__name__)

_SECTION = "camera"


def camera_key_from_hello(hello: dict[str, Any] | None) -> str | None:
    """Profile key for the camera that answered with ``hello``, or None.

    ``camera_key`` from HELLO wins. Servers that do not send it (macOS Swift,
    Linux C, Windows C) get ``"<backend>:<camera_model>"``. None when HELLO
    has neither, so no profile is created for an unidentifiable camera.
    """
    if not isinstance(hello, dict):
        return None
    key = hello.get("camera_key")
    if isinstance(key, str) and key.strip():
        return key.strip()
    backend, model = hello.get("backend"), hello.get("camera_model")
    if isinstance(backend, str) and backend.strip() and isinstance(model, str) and model.strip():
        return f"{backend.strip()}:{model.strip()}"
    return None


def new_profile() -> dict[str, Any]:
    """An empty profile (spec 4.1): no saved controls, no FOV."""
    return {"controls": {}, "fov_h_deg": None, "fov_source": None}


def _profiles(config: ConfigManager) -> dict[str, Any]:
    raw = config.get(_SECTION, "profiles")
    return raw if isinstance(raw, dict) else {}


def get_profile(config: ConfigManager, key: str) -> dict[str, Any] | None:
    """A copy of the profile for ``key``, or None if there is none."""
    profile = _profiles(config).get(key)
    return copy.deepcopy(profile) if isinstance(profile, dict) else None


def ensure_profile(config: ConfigManager, key: str) -> dict[str, Any]:
    """The profile for ``key`` (a copy); created and saved on first use.

    Missing parts of an existing profile (a hand-written one with only
    ``fov_h_deg``, say) are filled in so readers can rely on the three keys.
    """
    profiles = copy.deepcopy(_profiles(config))
    existing = profiles.get(key)
    profile = existing if isinstance(existing, dict) else {}
    completed = {**new_profile(), **profile}
    if not isinstance(completed["controls"], dict):
        completed["controls"] = {}
    if profiles.get(key) != completed:
        profiles[key] = completed
        config.set(_SECTION, "profiles", profiles)
        if existing is None:
            logger.info("Created camera profile %r", key)
    return copy.deepcopy(completed)


def get_fov(config: ConfigManager, key: str) -> tuple[Any, Any]:
    """Raw ``(fov_h_deg, fov_source)`` of the profile, ``(None, None)`` if unset.

    Not validated here: ``evf.solver.fov.resolve_fov`` decides what a bad
    value in a hand-edited file means.
    """
    profile = _profiles(config).get(key)
    if not isinstance(profile, dict):
        return None, None
    return profile.get("fov_h_deg"), profile.get("fov_source")


def set_fov(config: ConfigManager, key: str, fov_h_deg: float | None, source: str | None) -> None:
    """Store the profile FOV (None clears it). Creates the profile if needed."""
    profile = ensure_profile(config, key)
    profile["fov_h_deg"] = fov_h_deg
    profile["fov_source"] = source if fov_h_deg is not None else None
    profiles = copy.deepcopy(_profiles(config))
    profiles[key] = profile
    config.set(_SECTION, "profiles", profiles)


def get_saved_control(config: ConfigManager, key: str, control_id: str) -> dict[str, Any] | None:
    """The saved ``{"value": ..., "unit": ...}`` of a control (a copy), None if there is none.

    ``unit`` is absent for controls without one (binning). An entry that is not
    an object with a ``value`` (a hand-edited file) counts as not saved.
    """
    profile = _profiles(config).get(key)
    controls = profile.get("controls") if isinstance(profile, dict) else None
    entry = controls.get(control_id) if isinstance(controls, dict) else None
    if not isinstance(entry, dict) or "value" not in entry:
        return None
    return dict(entry)


def save_control(
    config: ConfigManager, key: str, control_id: str, value: Any, unit: str | None = None
) -> None:
    """Save a control value in the profile (spec 4.2): ``{"value": v, "unit": u}``, no ``unit`` if None.

    Creates the profile if needed and writes the file only when the entry changes.
    """
    profile = ensure_profile(config, key)
    entry: dict[str, Any] = {"value": value}
    if unit is not None:
        entry["unit"] = unit
    if profile["controls"].get(control_id) == entry:
        return
    profile["controls"][control_id] = entry
    profiles = copy.deepcopy(_profiles(config))
    profiles[key] = profile
    config.set(_SECTION, "profiles", profiles)


def last_camera_key(config: ConfigManager) -> str | None:
    """Key of the last connected camera; picks the solver database at startup."""
    key = config.get(_SECTION, "last_camera_key")
    return key if isinstance(key, str) and key else None


def set_last_camera_key(config: ConfigManager, key: str | None) -> None:
    config.set(_SECTION, "last_camera_key", key)


def claim_legacy_calibration(config: ConfigManager, key: str) -> bool:
    """Tie a calibration made before profiles existed to the camera that connects.

    ``calibration.camera_key`` is None for a calibration saved by an earlier
    version. The camera the user has been using is the one connecting now, so it
    gets the calibration; a camera that connects later does not (spec 4.2).
    Returns True if the key was written.
    """
    if config.get("calibration", "sync_d_body") is None:
        return False
    if config.get("calibration", "camera_key") is not None:
        return False
    config.set("calibration", "camera_key", key)
    logger.info("Existing calibration belongs to camera %r", key)
    return True
