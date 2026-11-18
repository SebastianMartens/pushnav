# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 8 and task 6.2).

"""SubprocessManager and the Windows camera server selection.

Two fake "servers" (Python scripts, which SubprocessManager can start like an exe): the
"C#" one exits with a configurable code (3 = no supported camera), the "C" one serves the
mock camera. Each records its start in ``starts.log``. ``server_select`` is told it runs
on Windows with those scripts as the two exes, so the tests run on every OS. The existing
``tests/test_subprocess_mgr.py`` is not touched by this.
"""

import logging
import socket
import subprocess
import sys
from pathlib import Path

import pytest

from evf.camera import server_select
from evf.camera import subprocess_mgr as mgr_module
from evf.camera.subprocess_mgr import SubprocessManager
from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.engine.state import StateMachine

_REPO_ROOT = Path(__file__).resolve().parent.parent

_FAKE_DOTNET = """
import os, sys
from pathlib import Path
with open(Path(os.environ["FAKE_DIR"]) / "starts.log", "a") as f:
    f.write("dotnet " + " ".join(sys.argv[1:]) + "\\n")
sys.exit(int(os.environ.get("FAKE_DOTNET_EXIT", "3")))
"""

_FAKE_C = """
import os, sys, time
from pathlib import Path
with open(Path(os.environ["FAKE_DIR"]) / "starts.log", "a") as f:
    f.write("c " + " ".join(sys.argv[1:]) + "\\n")
from tests.mock_camera_server import MockCameraServer
server = MockCameraServer(port=int(os.environ["FAKE_CAMERA_PORT"]), fps=20)
server.start()
while True:
    time.sleep(1)
"""


class _Mgr(SubprocessManager):
    _BACKOFF = [0.1, 0.2]
    _MONITOR_INTERVAL = 0.1
    _PORT_TIMEOUT = 10.0
    _PORT_POLL_INTERVAL = 0.05

    def _kill_stale_server(self):
        pass  # never taskkill the developer's real servers from a test


@pytest.fixture
def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@pytest.fixture
def fakes(tmp_path, monkeypatch, free_port):
    """Fake servers wired in as the two Windows exes; returns the directory with ``starts.log``."""
    dotnet = tmp_path / "fake_dotnet.py"
    c = tmp_path / "fake_c.py"
    dotnet.write_text(_FAKE_DOTNET)
    c.write_text(_FAKE_C)
    monkeypatch.setenv("FAKE_DIR", str(tmp_path))
    monkeypatch.setenv("FAKE_CAMERA_PORT", str(free_port))
    monkeypatch.setenv("PYTHONPATH", str(_REPO_ROOT))  # lets fake_c import tests.mock_camera_server
    monkeypatch.delenv(server_select.ENV_SERVER, raising=False)
    monkeypatch.delenv(server_select.ENV_EXTRA_ARGS, raising=False)
    monkeypatch.setattr(server_select, "is_windows", lambda: True)
    monkeypatch.setattr(server_select, "default_paths", lambda: (dotnet, c))
    return tmp_path


@pytest.fixture
def config(tmp_path_factory):
    return ConfigManager(config_dir=tmp_path_factory.mktemp("config"))


def _starts(fakes_dir: Path) -> list[str]:
    log = fakes_dir / "starts.log"
    return log.read_text().splitlines() if log.exists() else []


def _make(config, port):
    return _Mgr(LatestFrame(), StateMachine(), config, port=port)


def test_csharp_exit_code_3_starts_the_c_server_in_the_same_attempt(fakes, config, free_port, caplog):
    mgr = _make(config, free_port)
    with caplog.at_level(logging.INFO):
        hello = mgr.start()
    try:
        assert hello["backend"] == "mock-python"
        assert _starts(fakes) == ["dotnet ", "c "]
        assert mgr._server_plan.current.name == "c"
        assert mgr._binary_path.name == "fake_c.py"
        assert not mgr.recovering  # no backoff step was used
        messages = [r.message for r in caplog.records]
        assert any("no supported camera" in m and "starting the C server" in m for m in messages)
        assert any("Camera server 'c'" in m and "backend=mock-python" in m and "camera_model=MOCK_CAMERA" in m for m in messages)
    finally:
        mgr.stop()


def test_the_c_setting_never_starts_the_csharp_server(fakes, config, free_port):
    config.set("camera", "windows_server", "c")
    mgr = _make(config, free_port)
    mgr.start()
    try:
        assert _starts(fakes) == ["c "]
    finally:
        mgr.stop()


def test_the_env_override_beats_the_config(fakes, config, free_port, monkeypatch):
    config.set("camera", "windows_server", "dotnet")
    monkeypatch.setenv(server_select.ENV_SERVER, "c")
    mgr = _make(config, free_port)
    mgr.start()
    try:
        assert _starts(fakes) == ["c "]
    finally:
        mgr.stop()


def test_any_other_csharp_failure_is_not_a_fallback(fakes, config, free_port, monkeypatch):
    monkeypatch.setenv("FAKE_DOTNET_EXIT", "1")
    mgr = _make(config, free_port)
    with pytest.raises(RuntimeError, match="rc=1"):
        mgr.start()
    assert _starts(fakes) == ["dotnet "]  # the C server was never started


def test_a_missing_csharp_exe_falls_back_to_the_c_server(fakes, config, free_port, monkeypatch, caplog):
    missing = fakes / "never_built.py"
    monkeypatch.setattr(server_select, "default_paths", lambda: (missing, fakes / "fake_c.py"))
    mgr = _make(config, free_port)
    with caplog.at_level(logging.WARNING):
        mgr.start()
    try:
        assert _starts(fakes) == ["c "]
        assert any(str(missing) in r.message and "build.bat" in r.message for r in caplog.records)
    finally:
        mgr.stop()


def test_neither_exe_present_is_a_file_not_found_error(fakes, config, free_port, monkeypatch):
    a, b = fakes / "no_a.py", fakes / "no_b.py"
    monkeypatch.setattr(server_select, "default_paths", lambda: (a, b))
    with pytest.raises(FileNotFoundError) as info:
        _make(config, free_port)
    assert str(a) in str(info.value) and str(b) in str(info.value)


def test_recovery_attempts_start_with_the_server_that_worked(fakes, config, free_port):
    mgr = _make(config, free_port)
    mgr.start()
    mgr._stop_event.set()  # keep the monitor from starting a recovery of its own while this test drives the steps
    mgr._monitor_thread.join(timeout=2)
    try:
        # What _recovery_loop does for each attempt: drop the client, stop the process, spawn again.
        mgr._cleanup_client()
        mgr._terminate_process()
        mgr._spawn_process()
        mgr._connect_with_retry()
        assert _starts(fakes) == ["dotnet ", "c ", "c "]  # the C# server is not asked a second time
    finally:
        mgr.stop()


def test_extra_args_reach_the_csharp_server_only(fakes, config, free_port, monkeypatch):
    monkeypatch.setenv(server_select.ENV_EXTRA_ARGS, "--backend fake --log-level debug")
    mgr = _make(config, free_port)
    mgr.start()
    try:
        assert _starts(fakes) == ["dotnet --backend fake --log-level debug", "c "]
    finally:
        mgr.stop()


def test_an_explicit_binary_path_bypasses_the_selection(fakes, config, tmp_path):
    script = tmp_path / "explicit.py"
    script.write_text("")
    mgr = _Mgr(LatestFrame(), StateMachine(), config, binary_path=script)
    assert mgr._server_plan is None
    assert mgr._binary_path == script
    assert mgr._fork_fall_back_to_c_server(3) is False


def test_non_windows_keeps_the_single_server_and_ignores_the_setting(fakes, config, monkeypatch, tmp_path):
    monkeypatch.setattr(server_select, "is_windows", lambda: False)
    monkeypatch.setenv(server_select.ENV_SERVER, "c")
    single = tmp_path / "camera_server"
    single.write_text("")
    monkeypatch.setattr(SubprocessManager, "_resolve_binary", staticmethod(lambda _p: single))
    mgr = _Mgr(LatestFrame(), StateMachine(), config)
    assert mgr._server_plan is None
    assert mgr._binary_path == single


def test_kill_stale_server_kills_both_windows_exe_names(fakes, config, monkeypatch):
    calls = []
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setattr(subprocess, "CREATE_NO_WINDOW", 0x08000000, raising=False)
    monkeypatch.setattr(mgr_module.subprocess, "run", lambda cmd, **kw: calls.append(cmd))
    monkeypatch.setattr(mgr_module.time, "sleep", lambda _s: None)
    mgr = SubprocessManager(
        LatestFrame(), StateMachine(), config, binary_path=Path("C:/pushnav/camera_server_dotnet.exe")
    )
    mgr._kill_stale_server()
    killed = {cmd[3] for cmd in calls}
    assert killed == {"camera_server.exe", "camera_server_dotnet.exe"}
    assert all(cmd[:3] == ["taskkill", "/F", "/IM"] for cmd in calls)
