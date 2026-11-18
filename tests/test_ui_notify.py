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

"""Tests for evf.ui_notify: backend selection and the never-raise contract.

No real dialogs are ever shown: ctypes, subprocess and PyQt6 are mocked.
"""

import ctypes
import subprocess
import sys
import types
from unittest.mock import MagicMock

import pytest

from evf import ui_notify


def _completed(returncode=0, stderr=""):
    return subprocess.CompletedProcess([], returncode, stdout="", stderr=stderr)


@pytest.fixture
def linux_with_display(monkeypatch):
    monkeypatch.setattr(sys, "platform", "linux")
    monkeypatch.setenv("DISPLAY", ":0")
    monkeypatch.delenv("WAYLAND_DISPLAY", raising=False)


def test_windows_uses_messageboxw(monkeypatch):
    monkeypatch.setattr(sys, "platform", "win32")
    msgbox = MagicMock(return_value=1)
    fake = types.SimpleNamespace(user32=types.SimpleNamespace(MessageBoxW=msgbox))
    monkeypatch.setattr(ctypes, "windll", fake, raising=False)

    assert ui_notify.show_error("PushNav", "Port 8765 is busy") is True

    args = msgbox.call_args.args
    assert args[1:3] == ("Port 8765 is busy", "PushNav")
    assert args[3] & 0x10  # MB_ICONERROR
    assert args[3] & 0x10000  # MB_SETFOREGROUND


def test_macos_uses_osascript(monkeypatch):
    monkeypatch.setattr(sys, "platform", "darwin")
    run = MagicMock(return_value=_completed(0))
    monkeypatch.setattr(subprocess, "run", run)

    assert ui_notify.show_error("PushNav", "it's \"busy\"") is True

    cmd = run.call_args.args[0]
    assert cmd[0] == "osascript"
    # title/message travel as argv, not interpolated into the script
    assert cmd[-2:] == ["PushNav", "it's \"busy\""]


def test_linux_prefers_zenity(monkeypatch, linux_with_display):
    monkeypatch.setattr(
        ui_notify.shutil, "which", lambda n: f"/usr/bin/{n}" if n == "zenity" else None
    )
    run = MagicMock(return_value=_completed(0))
    monkeypatch.setattr(subprocess, "run", run)

    assert ui_notify.show_error("PushNav", "busy") is True
    assert run.call_args.args[0][0] == "zenity"


def test_linux_uses_kdialog_without_zenity(monkeypatch, linux_with_display):
    monkeypatch.setattr(
        ui_notify.shutil, "which", lambda n: f"/usr/bin/{n}" if n == "kdialog" else None
    )
    run = MagicMock(return_value=_completed(0))
    monkeypatch.setattr(subprocess, "run", run)

    assert ui_notify.show_error("PushNav", "busy") is True
    assert run.call_args.args[0][0] == "kdialog"


def test_linux_falls_back_to_qt_without_cli_tools(monkeypatch, linux_with_display):
    monkeypatch.setattr(ui_notify.shutil, "which", lambda n: None)
    box = MagicMock()
    widgets = types.SimpleNamespace(
        QApplication=MagicMock(instance=MagicMock(return_value=object())),
        QMessageBox=MagicMock(return_value=box),
    )
    monkeypatch.setitem(sys.modules, "PyQt6", types.ModuleType("PyQt6"))
    monkeypatch.setitem(sys.modules, "PyQt6.QtWidgets", widgets)

    assert ui_notify.show_error("PushNav", "busy") is True
    box.exec.assert_called_once()


def test_linux_without_display_skips_dialogs_and_uses_stderr(monkeypatch, capsys):
    monkeypatch.setattr(sys, "platform", "linux")
    monkeypatch.delenv("DISPLAY", raising=False)
    monkeypatch.delenv("WAYLAND_DISPLAY", raising=False)
    run = MagicMock()
    monkeypatch.setattr(subprocess, "run", run)

    assert ui_notify.show_error("PushNav", "busy") is False
    run.assert_not_called()
    assert "busy" in capsys.readouterr().err


def test_failing_backend_falls_through_to_next_and_stderr(monkeypatch, capsys):
    monkeypatch.setattr(sys, "platform", "linux")
    monkeypatch.setenv("DISPLAY", ":0")
    monkeypatch.setattr(ui_notify.shutil, "which", lambda n: "/usr/bin/zenity")
    # zenity can't open the display; Qt import fails too
    monkeypatch.setattr(
        subprocess, "run", MagicMock(return_value=_completed(1, "cannot open display"))
    )
    monkeypatch.setitem(sys.modules, "PyQt6", None)

    assert ui_notify.show_error("PushNav", "busy") is False
    assert "busy" in capsys.readouterr().err


def test_zenity_closed_with_esc_counts_as_shown(monkeypatch, linux_with_display):
    monkeypatch.setattr(ui_notify.shutil, "which", lambda n: "/usr/bin/zenity")
    monkeypatch.setattr(subprocess, "run", MagicMock(return_value=_completed(1, "")))

    assert ui_notify.show_error("PushNav", "busy") is True


def test_never_raises_when_backend_and_stderr_blow_up(monkeypatch):
    monkeypatch.setattr(sys, "platform", "win32")
    fake = types.SimpleNamespace(
        user32=types.SimpleNamespace(MessageBoxW=MagicMock(side_effect=OSError("boom")))
    )
    monkeypatch.setattr(ctypes, "windll", fake, raising=False)
    monkeypatch.setattr(sys, "stderr", None)

    assert ui_notify.show_error("PushNav", "busy") is False


def test_unknown_platform_falls_back_to_stderr(monkeypatch, capsys):
    monkeypatch.setattr(sys, "platform", "plan9")

    assert ui_notify.show_error("PushNav", "busy") is False
    assert "busy" in capsys.readouterr().err
