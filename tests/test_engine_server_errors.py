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
import sys
import time

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


@pytest.mark.skipif(sys.platform != "win32", reason="Windows SO_REUSEADDR semantics")
def test_windows_loopback_occupant_blocks_wildcard_bind(eng, monkeypatch):
    """With SO_REUSEADDR, a 0.0.0.0 bind silently succeeds next to an app
    holding 127.0.0.1:<port>. SO_EXCLUSIVEADDRUSE must make it fail."""
    occupant = socket.socket()
    occupant.bind(("127.0.0.1", 0))
    occupant.listen(5)
    port = occupant.getsockname()[1]
    real = engine_mod.Lx200Server
    monkeypatch.setattr(
        engine_mod, "Lx200Server",
        lambda *a, **kw: real(*a, **{**kw, "host": "0.0.0.0", "port": port}),
    )
    try:
        eng.startup_lx200()
    finally:
        occupant.close()

    (err,) = eng.server_errors
    assert err["server"] == "lx200" and err["port"] == port
    assert "in use" in err["message"]
    assert eng._lx200 is None


@pytest.mark.parametrize("name", ["Lx200Server", "StellariumServer"])
def test_server_can_restart_on_same_port_after_stop_with_client(name):
    """EXCLUSIVEADDRUSE must not break stop() followed by an immediate rebind,
    even after a client connection left TIME_WAIT behind."""
    from evf.engine.goto_target import GotoTarget
    from evf.engine.pointing import PointingState

    cls = getattr(engine_mod, name)
    srv = cls(PointingState(), host="127.0.0.1", port=0, goto_target=GotoTarget())
    srv.start()
    port = srv.port
    c = socket.create_connection(("127.0.0.1", port), timeout=2)
    time.sleep(0.3)  # let the server accept it
    c.close()
    time.sleep(0.3)
    srv.stop()

    again = cls(PointingState(), host="127.0.0.1", port=port, goto_target=GotoTarget())
    again.start()  # must not raise
    again.stop()
