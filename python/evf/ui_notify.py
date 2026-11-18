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

"""Native error dialog for failures before the UI exists.

The windowed (Nuitka) builds have no console, so a fatal start-up error
printed to stderr is invisible to the user. `show_error` pops up a native
message box instead, with no dependency beyond what each OS already ships:

- Windows: ``MessageBoxW`` via ctypes
- macOS:   ``osascript`` (``display alert``)
- Linux:   ``zenity`` or ``kdialog`` if on PATH, else a PyQt6 ``QMessageBox``

Never call this in headless mode (``--no-window``): there is nobody to
click the dialog away. It blocks until the user dismisses the dialog.
"""

import logging
import os
import shutil
import subprocess
import sys
from typing import Callable

logger = logging.getLogger(__name__)

# MessageBoxW flags
_MB_OK = 0x00000000
_MB_ICONERROR = 0x00000010
_MB_SETFOREGROUND = 0x00010000


def _show_windows(title: str, message: str) -> bool:
    import ctypes

    ctypes.windll.user32.MessageBoxW(
        None, message, title, _MB_OK | _MB_ICONERROR | _MB_SETFOREGROUND
    )
    return True


def _run_dialog(cmd: list[str]) -> bool:
    """Run a dialog helper to completion. True if it displayed a dialog.

    A non-zero exit is still a shown dialog when the tool printed nothing
    to stderr (zenity exits 1 when the user closes the window with Esc);
    a tool that can't reach a display complains on stderr instead.
    """
    proc = subprocess.run(cmd, capture_output=True, text=True, check=False)
    return proc.returncode == 0 or not (proc.stderr or "").strip()


def _show_macos(title: str, message: str) -> bool:
    # Pass title/message as argv so no AppleScript string escaping is needed.
    return _run_dialog([
        "osascript",
        "-e", "on run {t, m}",
        "-e", "display alert t message m as critical",
        "-e", "end run",
        title, message,
    ])


def _has_display() -> bool:
    return bool(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"))


def _show_linux_cli(title: str, message: str) -> bool:
    if not _has_display():
        return False
    if shutil.which("zenity"):
        return _run_dialog([
            "zenity", "--error", "--no-markup", "--title", title, "--text", message,
        ])
    if shutil.which("kdialog"):
        return _run_dialog(["kdialog", "--title", title, "--error", message])
    return False


def _show_linux_qt(title: str, message: str) -> bool:
    # Qt aborts the whole process when it can't connect to a display.
    if not _has_display():
        return False
    from PyQt6.QtWidgets import QApplication, QMessageBox

    _app = QApplication.instance() or QApplication([])
    box = QMessageBox(QMessageBox.Icon.Critical, title, message)
    box.exec()
    return True


def _backends() -> list[Callable[[str, str], bool]]:
    if sys.platform == "win32":
        return [_show_windows]
    if sys.platform == "darwin":
        return [_show_macos]
    if sys.platform.startswith("linux"):
        return [_show_linux_cli, _show_linux_qt]
    return []


def show_error(title: str, message: str) -> bool:
    """Show `message` in a blocking native error dialog. Never raises.

    Returns True if a dialog was shown. If no dialog backend works, the
    message goes to stderr instead and False is returned.
    """
    for backend in _backends():
        try:
            if backend(title, message):
                return True
        except Exception as exc:  # dialogs are best-effort, never fatal
            logger.debug("Error dialog backend %s failed: %s", backend.__name__, exc)
    try:
        print(f"{title}: {message}", file=sys.stderr)
    except Exception:
        pass
    return False
