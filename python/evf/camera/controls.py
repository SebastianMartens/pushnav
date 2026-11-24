# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 3.4 and 4.2).
"""Saved camera controls: one restore path, one save path.

``restore_controls()`` is what the engine calls after a camera connects and what
the subprocess manager calls after a recovery reconnect (spec 3.4). It sends
the saved value of every control the camera advertises, in the order
``binning``, ``fps``, ``exposure``, ``gain``, then the others. A value
saved with another unit than the camera advertises now is dropped (the same
camera key can speak ``"100us"`` in a v1 session and ``"us"`` in v2). A control
with no saved value gets, in this order (spec 4.2):

1. in a v1 session, the legacy ``camera.exposure`` / ``camera.gain`` (once, then stored in the profile),
2. the descriptor's ``default``,
3. the midpoint of ``[min, max]`` (geometric mean for ``scale: "log"``, the first option of an enum).

``remember_control()`` is the save side, called by ``Engine.set_control()``.

A v1 session restores only ``exposure`` and ``gain`` and keeps writing the legacy
``camera.exposure`` / ``camera.gain``: it behaves exactly as before profiles existed.
"""

from __future__ import annotations

import logging
import math
from typing import Any

from evf.config import profiles
from evf.config.manager import ConfigManager

logger = logging.getLogger(__name__)

# Protocol section 11.4.3: restoring in this order lets fps and exposure find their ranges
# already moved by binning, and exposure find the frame interval fps has set.
RESTORE_ORDER = ("binning", "fps", "exposure", "gain")
# What a v1 session has, and the only controls with a legacy config key.
V1_CONTROLS = ("exposure", "gain")

_SOURCE_PROFILE = "profile"
_SOURCE_LEGACY = "legacy"
_SOURCE_FIRST_RUN = "first run"


def restore_controls(client, config: ConfigManager) -> list[tuple[str, Any]]:
    """Send the saved (or first-run) value of every advertised control. Returns what was sent.

    A problem with one control is logged and does not stop the others, so a bad
    entry in ``config.json`` can never take the camera down.
    """
    if client is None:
        return []
    version = client.session_version
    key = client.camera_key
    sent: list[tuple[str, Any]] = []
    for descriptor in restore_order(client.controls, version):
        control_id = descriptor["id"]
        try:
            chosen = choose_value(config, key, descriptor, version)
            if chosen is None:
                continue
            value, source = chosen
            if not client.set_control(control_id, value):
                continue
            client.update_cached_control(control_id, value)
            sent.append((control_id, value))
            if source != _SOURCE_PROFILE:
                _remember_first_run(config, key, descriptor, value, source, version)
        except Exception as exc:  # noqa: BLE001 - one control must not stop the rest
            logger.error("Could not restore control %r: %s", control_id, exc)
    return sent


def restore_order(descriptors: list[dict], session_version: int) -> list[dict]:
    """The advertised controls to restore, in restore order.

    The known ids come first in ``RESTORE_ORDER``, then every other advertised id in
    the order the camera lists them. A v1 session has only ``exposure`` and ``gain``.
    """
    usable = [d for d in descriptors if isinstance(d, dict) and isinstance(d.get("id"), str)]
    if session_version < 2:
        usable = [d for d in usable if d["id"] in V1_CONTROLS]
    rank = {control_id: i for i, control_id in enumerate(RESTORE_ORDER)}
    # sorted() is stable: ids outside RESTORE_ORDER keep the camera's order, after the known ones
    return sorted(usable, key=lambda d: rank.get(d["id"], len(RESTORE_ORDER)))


def choose_value(config: ConfigManager, key: str | None, descriptor: dict, session_version: int):
    """``(value, source)`` to send for one control, or None when there is nothing sensible to send."""
    control_id = descriptor["id"]
    unit = descriptor.get("unit")
    saved = profiles.get_saved_control(config, key, control_id) if key else None
    if saved is not None:
        saved_unit = saved.get("unit")
        if saved_unit != unit:
            logger.info("Saved %s %r (%s) does not match the camera's unit %r: dropped",
                        control_id, saved.get("value"), saved_unit, unit)
        else:
            value = coerce(descriptor, saved.get("value"))
            if value is not None:
                return value, _SOURCE_PROFILE
            logger.info("Saved %s %r is not valid for this camera: dropped", control_id, saved.get("value"))
    if session_version < 2 and control_id in V1_CONTROLS:
        legacy = config.get("camera", control_id)
        if legacy is not None:
            return legacy, _SOURCE_LEGACY  # sent as it always was
    value = first_run_value(descriptor)
    return (value, _SOURCE_FIRST_RUN) if value is not None else None


def first_run_value(descriptor: dict) -> Any | None:
    """The descriptor's ``default``, else the midpoint of its range, else the first option."""
    default = coerce(descriptor, descriptor.get("default"))
    if default is not None:
        return default
    if descriptor.get("type") == "enum":
        options = descriptor.get("options")
        if isinstance(options, list) and options and isinstance(options[0], dict):
            return options[0].get("value")
        return None
    low, high = descriptor.get("min", 0), descriptor.get("max", 0)
    if not (_is_number(low) and _is_number(high)):
        return None
    is_float = descriptor.get("type") == "float"
    if descriptor.get("scale") == "log" and low > 0 and high > 0:
        middle = math.sqrt(low * high)
        return middle if is_float else int(round(middle))
    return (low + high) / 2 if is_float else int((low + high) // 2)


def coerce(descriptor: dict, value: object) -> Any | None:
    """``value`` as the descriptor's type, or None when it cannot be one of its values."""
    if not _is_number(value):
        return None
    kind = descriptor.get("type")
    if kind == "enum":
        for option in descriptor.get("options") or []:
            if isinstance(option, dict) and _is_number(option.get("value")) and option["value"] == value:
                return option["value"]
        return None
    if kind == "float":
        return float(value)
    return int(round(value))


def remember_control(config: ConfigManager, client, control_id: str, value: Any, sent: bool = True) -> None:
    """A control the user changed (``Engine.set_control``): keep it for the next start.

    A v1 session (or no camera) keeps writing the legacy ``camera.exposure`` /
    ``camera.gain``. A control the connected camera advertises is also saved in its
    profile with the unit of the descriptor (spec 4.2), if the value was sent.
    """
    session_version = client.session_version if client is not None else 1
    if session_version < 2 and control_id in V1_CONTROLS:
        config.set("camera", control_id, value)
    if client is None or not sent:
        return
    key = client.camera_key
    descriptor = next((d for d in client.controls if isinstance(d, dict) and d.get("id") == control_id), None)
    if key is None or descriptor is None:
        return
    profiles.save_control(config, key, control_id, value, descriptor.get("unit"))


def _remember_first_run(config, key, descriptor, value, source, session_version) -> None:
    control_id = descriptor["id"]
    if source == _SOURCE_FIRST_RUN:
        logger.info("First-run %s = %s", control_id, value)
        if session_version < 2 and control_id in V1_CONTROLS:
            config.set("camera", control_id, value)  # as before profiles existed
    elif source == _SOURCE_LEGACY:
        logger.info("Saved %s = %s from before profiles moved into the camera profile", control_id, value)
    if key is None:
        return
    if profiles.get_saved_control(config, key, control_id) is not None:
        return  # a value in another unit stays: it belongs to the other kind of session
    profiles.save_control(config, key, control_id, value, descriptor.get("unit"))


def _is_number(value: object) -> bool:
    return (not isinstance(value, bool) and isinstance(value, (int, float)) and math.isfinite(value))
