# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 8).

"""Which Windows camera server to start: the C# one (default) or the C/DirectShow one.

Both ship side by side, permanently (spec section 8). ``camera.windows_server`` in the
config picks one (``"dotnet"`` default, or ``"c"``); the ``PUSHNAV_CAMERA_SERVER``
environment variable overrides it for debugging. On macOS and Linux none of this
applies: those platforms keep their single server and ``paths.camera_binary()``.

``SubprocessManager`` asks for a :class:`ServerPlan` and follows it:

* ``"dotnet"``: start the C# server; if it exits with code 3 (no supported camera, e.g.
  an OV9281 module only the C server handles) start the C server in the same attempt.
* ``"c"``: start only the C server, never the C# one.
* A missing exe falls back to the other server (both directions) with a warning; with
  neither present the plan raises ``FileNotFoundError`` naming both paths.
"""

from __future__ import annotations

import logging
import os
import shlex
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping

logger = logging.getLogger(__name__)

SERVER_DOTNET = "dotnet"
SERVER_C = "c"
DEFAULT_SERVER = SERVER_DOTNET

ENV_SERVER = "PUSHNAV_CAMERA_SERVER"
ENV_EXTRA_ARGS = "PUSHNAV_CAMERA_SERVER_ARGS"

#: Exit code the C# server uses for "no supported camera attached" (spec 4.3).
EXIT_NO_SUPPORTED_CAMERA = 3

DOTNET_EXE_NAME = "camera_server_dotnet.exe"
C_EXE_NAME = "camera_server.exe"

_BUILD_HINT = "camera\\windows-dotnet\\build.bat"


@dataclass(frozen=True)
class ServerChoice:
    """One camera server executable: ``name`` is ``"dotnet"`` or ``"c"``."""

    name: str
    path: Path


class ServerPlan:
    """The server to start now, and the one to fall back to when the C# server finds no camera."""

    def __init__(
        self,
        setting: str,
        current: ServerChoice,
        no_camera_fallback: ServerChoice | None = None,
    ) -> None:
        self.setting = setting
        self._current = current
        self._no_camera_fallback = no_camera_fallback

    @property
    def current(self) -> ServerChoice:
        """The server that is started next; after a fallback, the one that was switched to."""
        return self._current

    def fallback_after_exit(
        self, exit_code: int | None, running: ServerChoice | None = None
    ) -> ServerChoice | None:
        """The server to start instead when ``running`` (default: the current one) exited with ``exit_code``.

        Only the C# server's exit code 3 triggers it, and only with the ``"dotnet"`` setting
        (so ``"c"`` never reaches the C# server) and an existing C server exe. Any other
        failure follows the existing backoff unchanged. The fallback is not remembered until
        it has connected: call :meth:`switch_to` then.
        """
        running = running or self._current
        if (
            exit_code == EXIT_NO_SUPPORTED_CAMERA
            and running.name == SERVER_DOTNET
            and self._no_camera_fallback is not None
        ):
            return self._no_camera_fallback
        return None

    def switch_to(self, choice: ServerChoice) -> None:
        """Remember ``choice`` as the working server: recovery attempts start with it (spec 8.2)."""
        self._current = choice


def is_windows() -> bool:
    return sys.platform == "win32"


def default_paths() -> tuple[Path, Path]:
    """(C# exe, C exe) for this install; see ``evf.paths.windows_camera_server_paths``."""
    from evf import paths

    return paths.windows_camera_server_paths()


def stale_process_names(spawning_exe_name: str | None = None) -> list[str]:
    """Image names ``_kill_stale_server`` must kill on Windows: both servers, not only the one about to start."""
    names = [C_EXE_NAME, DOTNET_EXE_NAME]
    if spawning_exe_name and spawning_exe_name not in names:
        names.append(spawning_exe_name)
    return names


def configured_server(config_value: object, env: Mapping[str, str] | None = None) -> str:
    """The server setting: the env override if set, else the config value, else ``"dotnet"``.

    An unknown value logs a WARNING and counts as ``"dotnet"``.
    """
    env = os.environ if env is None else env
    source = "config camera.windows_server"
    value = config_value
    override = env.get(ENV_SERVER)
    if override is not None and override.strip():
        value = override
        source = f"environment variable {ENV_SERVER}"
    if value is None or (isinstance(value, str) and not value.strip()):
        return DEFAULT_SERVER
    normalized = str(value).strip().lower()
    if normalized in (SERVER_DOTNET, SERVER_C):
        return normalized
    logger.warning(
        "Unknown camera server %r in %s (expected %r or %r); using %r",
        value, source, SERVER_DOTNET, SERVER_C, DEFAULT_SERVER,
    )
    return DEFAULT_SERVER


def build_plan(
    config_value: object = None,
    *,
    env: Mapping[str, str] | None = None,
    windows: bool | None = None,
    dotnet_path: Path | None = None,
    c_path: Path | None = None,
) -> ServerPlan | None:
    """Plan which Windows camera server to start; None on macOS/Linux (the setting is ignored there).

    ``dotnet_path`` / ``c_path`` override the install layout (tests). Raises ``FileNotFoundError``
    when neither exe exists.
    """
    if not (is_windows() if windows is None else windows):
        return None

    if dotnet_path is None or c_path is None:
        default_dotnet, default_c = default_paths()
        dotnet_path = dotnet_path or default_dotnet
        c_path = c_path or default_c
    dotnet = ServerChoice(SERVER_DOTNET, Path(dotnet_path))
    c = ServerChoice(SERVER_C, Path(c_path))
    have_dotnet = dotnet.path.is_file()
    have_c = c.path.is_file()

    setting = configured_server(config_value, env)

    if setting == SERVER_DOTNET:
        if have_dotnet:
            plan = ServerPlan(setting, dotnet, c if have_c else None)
            logger.info("Camera server: C# (%s)%s", dotnet.path, "" if have_c else "; no C server to fall back to")
            return plan
        if have_c:
            logger.warning(
                "C# camera server not found at %s; run %s. Falling back to the C server.",
                dotnet.path, _BUILD_HINT,
            )
            return ServerPlan(setting, c)
    else:
        if have_c:
            logger.info("Camera server: C (%s)", c.path)
            return ServerPlan(setting, c)
        if have_dotnet:
            logger.warning(
                "C camera server not found at %s; run camera\\windows\\build.bat. Falling back to the C# server.",
                c.path,
            )
            return ServerPlan(setting, dotnet)

    raise FileNotFoundError(
        f"No camera server found: neither {dotnet.path} (C#, build with {_BUILD_HINT}) "
        f"nor {c.path} (C, build with camera\\windows\\build.bat) exists"
    )


def extra_args(choice: ServerChoice, env: Mapping[str, str] | None = None) -> list[str]:
    """Extra command line arguments for the C# server from ``PUSHNAV_CAMERA_SERVER_ARGS`` (dev use).

    For example ``--backend fake --fake-images tests/samples`` runs the engine against the C# server's
    fake camera, ``--log-level debug`` turns its logging up. Never passed to the C server.
    """
    env = os.environ if env is None else env
    if choice.name != SERVER_DOTNET:
        return []
    raw = env.get(ENV_EXTRA_ARGS, "").strip()
    if not raw:
        return []
    if is_windows():
        # posix=False keeps backslashes in paths literal but also keeps the quote characters.
        return [part.strip('"') for part in shlex.split(raw, posix=False)]
    return shlex.split(raw)
