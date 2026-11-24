# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 3.4 and 4.2).
"""Saved camera controls: one restore path, one save path.

``restore_controls()`` is what the engine calls after a camera connects and what
the subprocess manager calls after a recovery reconnect (spec 3.4). It sends
a value for every control the camera advertises, in the order
``binning``, ``fps``, ``exposure``, ``gain``, then the others. A control gets its
value from the first of these that applies (spec 4.2):

1. **v1 session only**: the legacy ``camera.exposure`` / ``camera.gain``. In a v1
   session they stay authoritative, exactly as before profiles existed: the user may
   change a control while the camera is away (``remember_control`` with no client), and
   only these keys see that. The profile is kept in step with them, not the other way round.
2. the camera profile's saved value, if its unit equals the advertised unit (the same
   camera key can speak ``"100us"`` in a v1 session and ``"us"`` in v2),
3. the descriptor's ``default``,
4. the midpoint of ``[min, max]`` (geometric mean for ``scale: "log"``, the first option of an enum).

``remember_control()`` is the save side, called by ``Engine.set_control()``.

A v1 session restores only ``exposure`` and ``gain``: it behaves as it did before the profiles.
``ControlDescriptor`` is the CONTROL_INFO entry parsed once, at the edge, so the rules
(type coercion, valid enum options, first-run value) live in one place.
"""

from __future__ import annotations

import enum
import logging
import math
from dataclasses import dataclass
from typing import Any

from evf.config import profiles
from evf.config.manager import ConfigManager

logger = logging.getLogger(__name__)

# Protocol section 11.4.3: restoring in this order lets fps and exposure find their ranges
# already moved by binning, and exposure find the frame interval fps has set.
RESTORE_ORDER = ("binning", "fps", "exposure", "gain")
# What a v1 session has, and the only controls with a legacy config key.
V1_CONTROLS = ("exposure", "gain")


class ValueSource(enum.Enum):
    LEGACY = "legacy key"
    PROFILE = "profile"
    DEFAULT = "descriptor default"
    MIDPOINT = "midpoint"


@dataclass(frozen=True)
class ChosenValue:
    value: Any
    source: ValueSource


@dataclass(frozen=True)
class ControlDescriptor:
    """One CONTROL_INFO entry, as far as saving and restoring care about it.

    The client's ``controls`` list stays as the camera sent it; this is built from an entry
    where it is used. ``options`` holds the numeric values of an enum's options.
    """

    id: str
    type: str = "int"
    unit: str | None = None
    min: Any = 0
    max: Any = 0
    default: Any = None
    scale: str | None = None
    options: tuple = ()

    @classmethod
    def from_info(cls, info: object) -> ControlDescriptor | None:
        """The descriptor of a CONTROL_INFO entry, None if it has no usable id."""
        if not isinstance(info, dict) or not isinstance(info.get("id"), str) or not info["id"]:
            return None
        raw_options = info.get("options")
        options = tuple(
            option["value"] for option in raw_options
            if isinstance(option, dict) and _is_number(option.get("value"))
        ) if isinstance(raw_options, list) else ()
        kind = info.get("type")
        return cls(
            id=info["id"],
            type=kind if isinstance(kind, str) else "int",
            unit=info.get("unit"),
            min=info.get("min", 0),
            max=info.get("max", 0),
            default=info.get("default"),
            scale=info.get("scale"),
            options=options,
        )

    def coerce(self, value: object) -> Any | None:
        """``value`` as this control's type, or None when it cannot be one of its values.

        An enum value must be one of the options; ``int`` rounds, ``float`` converts. The range
        is not checked: the camera clamps.
        """
        if not _is_number(value):
            return None
        if self.type == "enum":
            return next((option for option in self.options if option == value), None)
        if self.type == "float":
            return float(value)
        return int(round(value))

    def default_value(self) -> Any | None:
        """The descriptor's own ``default`` if it is a valid value, else None."""
        return self.coerce(self.default)

    def midpoint(self) -> Any | None:
        """Middle of the range (geometric for a log scale), or the first option of an enum."""
        if self.type == "enum":
            return self.options[0] if self.options else None
        if not (_is_number(self.min) and _is_number(self.max)):
            return None
        is_float = self.type == "float"
        if self.scale == "log" and self.min > 0 and self.max > 0:
            middle = math.sqrt(self.min * self.max)
            return middle if is_float else int(round(middle))
        return (self.min + self.max) / 2 if is_float else int((self.min + self.max) // 2)

    def first_run_value(self) -> Any | None:
        """What a control gets when nothing is saved: ``default``, else the midpoint."""
        default = self.default_value()
        return default if default is not None else self.midpoint()


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
    for descriptor in restore_order(_descriptors(client.controls), version):
        try:
            chosen = choose_value(config, key, descriptor, version)
            if chosen is None:
                continue
            if not client.set_control(descriptor.id, chosen.value):
                continue
            client.update_cached_control(descriptor.id, chosen.value)
            sent.append((descriptor.id, chosen.value))
            _keep_in_step(config, key, descriptor, version, chosen)
        except Exception as exc:  # noqa: BLE001 - one control must not stop the rest
            logger.error("Could not restore control %r: %s", descriptor.id, exc)
    return sent


def restore_order(descriptors: list[ControlDescriptor], session_version: int) -> list[ControlDescriptor]:
    """The advertised controls to restore, in restore order.

    The known ids come first in ``RESTORE_ORDER``, then every other advertised id in
    the order the camera lists them. A v1 session has only ``exposure`` and ``gain``.
    """
    usable = descriptors if session_version >= 2 else [d for d in descriptors if d.id in V1_CONTROLS]
    rank = {control_id: i for i, control_id in enumerate(RESTORE_ORDER)}
    # sorted() is stable: ids outside RESTORE_ORDER keep the camera's order, after the known ones
    return sorted(usable, key=lambda d: rank.get(d.id, len(RESTORE_ORDER)))


def choose_value(
    config: ConfigManager, key: str | None, descriptor: ControlDescriptor, session_version: int
) -> ChosenValue | None:
    """The value to send for one control and where it came from, None if there is nothing sensible to send."""
    if _uses_legacy_key(session_version, descriptor.id):
        legacy = config.get("camera", descriptor.id)
        if legacy is not None:
            return ChosenValue(legacy, ValueSource.LEGACY)  # sent as it always was
    saved = profiles.get_saved_control(config, key, descriptor.id) if key else None
    if saved is not None:
        if saved.get("unit") != descriptor.unit:
            logger.info("Saved %s %r (%s) does not match the camera's unit %r: dropped",
                        descriptor.id, saved.get("value"), saved.get("unit"), descriptor.unit)
        else:
            value = descriptor.coerce(saved.get("value"))
            if value is not None:
                return ChosenValue(value, ValueSource.PROFILE)
            logger.info("Saved %s %r is not valid for this camera: dropped", descriptor.id, saved.get("value"))
    default = descriptor.default_value()
    if default is not None:
        return ChosenValue(default, ValueSource.DEFAULT)
    midpoint = descriptor.midpoint()
    return ChosenValue(midpoint, ValueSource.MIDPOINT) if midpoint is not None else None


def remember_control(config: ConfigManager, client, control_id: str, value: Any, sent: bool) -> None:
    """A control the user changed (``Engine.set_control``): keep it for the next start.

    A v1 session (or no camera) writes the legacy ``camera.exposure`` / ``camera.gain``,
    sent or not: they are authoritative there. A control the connected camera advertises
    is also saved in its profile with the unit of the descriptor (spec 4.2), if ``sent``.
    """
    session_version = client.session_version if client is not None else 1
    if _uses_legacy_key(session_version, control_id):
        config.set("camera", control_id, value)
    if client is None or not sent:
        return
    key = client.camera_key
    descriptor = next((d for d in _descriptors(client.controls) if d.id == control_id), None)
    if key is None or descriptor is None:
        return
    profiles.save_control(config, key, control_id, value, descriptor.unit)


def _keep_in_step(config, key, descriptor: ControlDescriptor, session_version: int, chosen: ChosenValue) -> None:
    """After a restore: first-run values are stored, and the profile follows a legacy value."""
    if chosen.source is ValueSource.PROFILE:
        return
    if chosen.source is not ValueSource.LEGACY:
        logger.info("First-run %s = %s (%s)", descriptor.id, chosen.value, chosen.source.value)
        if _uses_legacy_key(session_version, descriptor.id):
            config.set("camera", descriptor.id, chosen.value)  # as before profiles existed
    if key is None:
        return
    existing = profiles.get_saved_control(config, key, descriptor.id)
    if existing is not None and existing.get("unit") != descriptor.unit:
        return  # a value in another unit stays: it belongs to the other kind of session
    profiles.save_control(config, key, descriptor.id, chosen.value, descriptor.unit)


def _uses_legacy_key(session_version: int, control_id: str) -> bool:
    """True where ``camera.exposure`` / ``camera.gain`` apply: a v1 session's exposure and gain."""
    return session_version < 2 and control_id in V1_CONTROLS


def _descriptors(infos: list) -> list[ControlDescriptor]:
    return [d for d in (ControlDescriptor.from_info(info) for info in infos) if d is not None]


def _is_number(value: object) -> bool:
    return not isinstance(value, bool) and isinstance(value, (int, float)) and math.isfinite(value)
