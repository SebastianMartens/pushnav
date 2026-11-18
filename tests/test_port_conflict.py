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

"""Tests for the fatal web-port conflict in evf.main (message + dialog vs stderr)."""

import socket
from pathlib import Path

import pytest

from evf import main as evf_main

CONFIG = Path("C:/Users/x/AppData/Roaming/ElectronicViewfinder/config.json")


def test_other_app_message_names_port_config_path_and_key(monkeypatch):
    monkeypatch.delenv("PUSHNAV_ROOT", raising=False)
    msg = evf_main._port_conflict_message(8765, "other", CONFIG)
    assert "8765" in msg
    assert "another application" in msg
    assert str(CONFIG) in msg
    assert "webserver.port" in msg


def test_pushnav_message_says_already_running(monkeypatch):
    monkeypatch.delenv("PUSHNAV_ROOT", raising=False)
    msg = evf_main._port_conflict_message(8765, "pushnav", CONFIG)
    assert "PushNav is already running on port 8765" in msg
    assert "another application" not in msg


def test_headless_service_message_names_systemctl(monkeypatch):
    monkeypatch.setenv("PUSHNAV_ROOT", "/usr/lib/pushnav-headless")
    msg = evf_main._port_conflict_message(8765, "pushnav", CONFIG)
    assert "systemctl stop pushnav-headless" in msg


@pytest.fixture
def busy_port():
    """A real listening socket that is not PushNav (never answers HTTP)."""
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    s.listen(5)
    yield s.getsockname()[1]
    s.close()


def test_free_port_does_not_exit(monkeypatch):
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    shown = []
    monkeypatch.setattr(evf_main.ui_notify, "show_error", lambda *a: shown.append(a))

    evf_main._check_single_instance(port, CONFIG)  # must return normally

    assert shown == []


def test_windowed_conflict_shows_dialog_and_exits_1(monkeypatch, busy_port, capsys):
    monkeypatch.delenv("PUSHNAV_ROOT", raising=False)
    shown = []
    monkeypatch.setattr(
        evf_main.ui_notify, "show_error", lambda title, msg: shown.append((title, msg))
    )

    with pytest.raises(SystemExit) as exc:
        evf_main._check_single_instance(busy_port, CONFIG, no_window=False)

    assert exc.value.code == 1
    assert len(shown) == 1
    assert str(busy_port) in shown[0][1]
    assert str(CONFIG) in shown[0][1]
    assert capsys.readouterr().err == ""  # the dialog backend owns the fallback


def test_headless_conflict_prints_stderr_and_never_opens_dialog(
    monkeypatch, busy_port, capsys
):
    monkeypatch.delenv("PUSHNAV_ROOT", raising=False)
    shown = []
    monkeypatch.setattr(evf_main.ui_notify, "show_error", lambda *a: shown.append(a))

    with pytest.raises(SystemExit) as exc:
        evf_main._check_single_instance(busy_port, CONFIG, no_window=True)

    assert exc.value.code == 1
    assert shown == []
    assert str(CONFIG) in capsys.readouterr().err


def test_conflict_is_logged(monkeypatch, busy_port, caplog):
    monkeypatch.delenv("PUSHNAV_ROOT", raising=False)
    monkeypatch.setattr(evf_main.ui_notify, "show_error", lambda *a: None)

    with pytest.raises(SystemExit):
        evf_main._check_single_instance(busy_port, CONFIG)

    assert any(str(busy_port) in r.getMessage() for r in caplog.records)
