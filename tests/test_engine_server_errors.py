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

"""A busy Stellarium/LX200 port must surface in the engine's server_errors
(and thus the /ws payload) instead of only a log line."""

import errno
import json
import socket

import pytest

from evf.config.manager import ConfigManager
from evf.engine import engine as engine_mod
from evf.engine.engine import Engine, server_error_message
from evf.webserver.server import WebServer


@pytest.fixture
def busy_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    s.listen(5)
    yield s.getsockname()[1]
    s.close()


@pytest.fixture
def eng(tmp_path):
    return Engine(config=ConfigManager(config_dir=tmp_path))


def _on_port(monkeypatch, name, port):
    """Make the engine's `name` server class bind `port` instead of its default."""
    real = getattr(engine_mod, name)
    monkeypatch.setattr(
        engine_mod, name,
        lambda *a, **kw: real(*a, **{**kw, "host": "127.0.0.1", "port": port}),
    )


def test_no_errors_by_default(eng):
    assert eng.server_errors == []


def test_busy_lx200_port_is_reported(eng, monkeypatch, busy_port):
    _on_port(monkeypatch, "Lx200Server", busy_port)

    eng.startup_lx200()

    (err,) = eng.server_errors
    assert err["server"] == "lx200"
    assert err["port"] == busy_port
    assert str(busy_port) in err["message"]
    assert "in use" in err["message"]
    assert "LX200" in err["message"]


def test_busy_stellarium_port_is_reported(eng, monkeypatch, busy_port):
    _on_port(monkeypatch, "StellariumServer", busy_port)

    eng.startup_stellarium()

    (err,) = eng.server_errors
    assert err["server"] == "stellarium"
    assert err["port"] == busy_port
    assert "Stellarium" in err["message"]
    assert "in use" in err["message"]


def test_free_ports_report_nothing(eng, monkeypatch):
    _on_port(monkeypatch, "Lx200Server", 0)
    _on_port(monkeypatch, "StellariumServer", 0)

    eng.startup_lx200()
    eng.startup_stellarium()
    try:
        assert eng.server_errors == []
    finally:
        eng._lx200.stop()
        eng._stellarium.stop()


def test_server_errors_reach_the_ws_payload(eng, monkeypatch, busy_port):
    _on_port(monkeypatch, "Lx200Server", busy_port)
    eng.startup_lx200()
    ws = WebServer(
        eng.pointing_state, eng.state_machine, eng.goto_target, eng.config,
        server_errors=lambda: eng.server_errors,
    )

    payload = json.loads(json.dumps(ws._build_payload()))

    assert payload["server_errors"] == eng.server_errors
    assert payload["server_errors"][0]["server"] == "lx200"


def test_payload_server_errors_defaults_to_empty_list(eng):
    ws = WebServer(
        eng.pointing_state, eng.state_machine, eng.goto_target, eng.config
    )
    assert ws._build_payload()["server_errors"] == []


def test_message_for_addr_in_use():
    exc = OSError(errno.EADDRINUSE, "Address already in use")
    msg = server_error_message("Stellarium", 10001, exc)
    assert msg == "Stellarium can't connect: port 10001 is in use by another application."


def test_message_for_windows_access_denied():
    # Windows reports WSAEACCES when a SO_REUSEADDR bind hits a held port
    exc = OSError(10013, "forbidden")
    assert "in use" in server_error_message("LX200", 4030, exc)


def test_message_for_other_errors_includes_reason():
    msg = server_error_message("LX200", 4030, OSError(errno.EACCES, "Permission denied"))
    assert "port 4030" in msg
    assert "Permission denied" in msg
    assert "in use" not in msg
