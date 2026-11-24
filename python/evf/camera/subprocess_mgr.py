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

"""Camera subprocess manager — spawn, monitor, and recover the camera server.

Per specs/start/impl0.md §Phase 5.
"""

import logging
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

from evf.camera import server_select  # fork: camera-server-dotnet (see specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 8)
from evf.camera.client import CameraClient
from evf.camera.controls import restore_controls  # fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
from evf.camera.stream import v2_stall  # fork: camera-controls
from evf.config.manager import ConfigManager
from evf.engine.frame_buffer import LatestFrame
from evf.engine.state import EngineState, StateMachine

logger = logging.getLogger(__name__)


class SubprocessManager:
    """Manage the camera server subprocess lifecycle.

    Spawns the Swift camera_server binary, connects a CameraClient,
    monitors the connection, and performs crash recovery with backoff.
    """

    _BACKOFF = [1, 2, 4, 8, 15]
    _MONITOR_INTERVAL = 0.5
    _PORT_POLL_INTERVAL = 0.1
    _PORT_TIMEOUT = 15.0
    _FRAME_STALL_TIMEOUT = 2.0

    def __init__(
        self,
        frame_buffer: LatestFrame,
        state_machine: StateMachine,
        config: ConfigManager,
        binary_path: str | Path | None = None,
        host: str = "127.0.0.1",
        port: int = 8764,
    ) -> None:
        self._frame_buffer = frame_buffer
        self._state_machine = state_machine
        self._config = config
        # fork: camera-server-dotnet (see specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 8)
        # Windows only; an explicit binary_path (tests, dev) bypasses the selection.
        self._server_plan = (
            server_select.build_plan(config.get("camera", "windows_server"))
            if binary_path is None else None
        )
        self._binary_path: Path = (
            self._server_plan.current.path if self._server_plan
            else self._resolve_binary(binary_path)
        )
        # The server this attempt runs: the plan's current one, or the C fallback after exit code 3.
        self._fork_running = self._server_plan.current if self._server_plan else None
        self._host = host
        self._port = port
        self._process: subprocess.Popen | None = None
        self._client: CameraClient | None = None
        self._monitor_thread: threading.Thread | None = None
        self._recovery_thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._recovering = False
        # fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)
        # Called with the new HELLO after a successful recovery reconnect: the C server
        # fallback (exit code 3) can bring up a different camera than before.
        self.on_recovered = None

    # -- public API -----------------------------------------------------------

    def start(self) -> dict:
        """Spawn the camera server, connect, handshake, and start receiving.

        Returns the HELLO info dict from the camera server.
        Raises RuntimeError if the server fails to start or connect.
        """
        self._stop_event.clear()
        self._spawn_process()
        hello = self._connect_with_retry()
        self._fork_remember_connected()
        self._fork_log_server(hello)
        self._client.start_receiving()
        self._start_monitor()
        return hello

    def stop(self) -> None:
        """Stop the camera server and clean up all resources."""
        self._stop_event.set()
        # Wait for recovery thread to notice the stop event
        if self._recovery_thread is not None and self._recovery_thread.is_alive():
            self._recovery_thread.join(timeout=max(self._BACKOFF) + 5)
        # Wait for monitor thread
        if self._monitor_thread is not None and self._monitor_thread.is_alive():
            self._monitor_thread.join(timeout=2)
        self._monitor_thread = None
        self._recovery_thread = None
        self._cleanup_client()
        self._terminate_process()

    @property
    def client(self) -> CameraClient | None:
        return self._client

    @property
    def running(self) -> bool:
        return self._client is not None and self._client.connected

    @property
    def recovering(self) -> bool:
        """True while the backoff recovery loop owns the camera."""
        return self._recovering

    # -- binary resolution ----------------------------------------------------

    @staticmethod
    def _resolve_binary(binary_path: str | Path | None) -> Path:
        if binary_path is not None:
            return Path(binary_path)
        from evf.paths import camera_binary
        return camera_binary()

    # -- spawn / terminate ----------------------------------------------------

    def _kill_stale_server(self) -> None:
        """Kill any leftover camera_server process from a previous run.

        If PushNav was force-quit or crashed, the camera server may still
        be alive holding the camera device and TCP port.  This prevents
        the new instance from connecting.
        """
        name = Path(self._binary_path).name
        try:
            if sys.platform == "win32":
                # CREATE_NO_WINDOW: the released app is console-less
                # (--windows-console-mode=disable), so spawning the console app
                # taskkill.exe without it pops a brief black window on startup.
                # fork: camera-server-dotnet (spec section 8.4): kill both Windows servers
                for stale_name in server_select.stale_process_names(name):
                    subprocess.run(
                        ["taskkill", "/F", "/IM", stale_name],
                        capture_output=True, timeout=5,
                        creationflags=subprocess.CREATE_NO_WINDOW,
                    )
            else:
                subprocess.run(
                    ["pkill", "-f", name],
                    capture_output=True, timeout=5,
                )
        except (subprocess.TimeoutExpired, FileNotFoundError):
            pass
        # Brief pause to let the OS release the port
        time.sleep(0.5)

    def _build_env(self) -> dict | None:
        """Environment for the camera server.

        Injects PUSHNAV_CAMERA_IDS (the user-configured camera allowlist
        extras) so the native server recognizes cameras beyond its built-in
        list. Returns None to inherit the parent environment unchanged when
        there is nothing to add.
        """
        extras = self._config.extra_camera_ids
        if not extras:
            return None
        env = os.environ.copy()
        parts = []
        existing = env.get("PUSHNAV_CAMERA_IDS", "").strip()
        if existing:
            parts.append(existing)
        parts.extend(s for s in (str(x).strip() for x in extras) if s)
        if parts:
            env["PUSHNAV_CAMERA_IDS"] = ",".join(parts)
            logger.info(
                "Passing PUSHNAV_CAMERA_IDS=%s to camera server",
                env["PUSHNAV_CAMERA_IDS"],
            )
        return env

    def _spawn_process(self) -> None:
        self._kill_stale_server()
        path = str(self._binary_path)
        cmd = [sys.executable, path] if path.endswith(".py") else [path]
        cmd += self._fork_server_args()  # fork: camera-server-dotnet
        kwargs = {}
        if sys.platform == "win32":
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        self._process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=self._build_env(),
            **kwargs,
        )
        logger.info(
            "Spawned camera server (PID %d): %s",
            self._process.pid,
            self._binary_path,
        )

    def _connect_with_retry(self) -> dict:
        """Retry CameraClient.connect() until the server is ready.

        The camera server may take a few seconds to bind after spawning
        (AVFoundation + UVC init).  We must NOT probe the port with a
        bare TCP connect because the Swift server exits when any client
        disconnects.  Instead, retry the real CameraClient handshake.
        """
        self._frame_buffer.clear()  # reset so new client's frame_ids are accepted
        deadline = time.monotonic() + self._PORT_TIMEOUT
        last_exc: Exception | None = None
        while time.monotonic() < deadline:
            # Bail early if the server process already exited
            if self._process is not None and self._process.poll() is not None:
                rc = self._process.returncode
                # fork: camera-server-dotnet (spec section 8.2): exit code 3 -> start the C server in this attempt
                if self._fork_fall_back_to_c_server(rc):
                    deadline = time.monotonic() + self._PORT_TIMEOUT
                    continue
                stderr_text = ""
                try:
                    _, stderr_bytes = self._process.communicate(timeout=1)
                    if stderr_bytes:
                        stderr_text = stderr_bytes.decode(errors="replace").strip()
                except (subprocess.TimeoutExpired, ValueError):
                    pass
                self._process = None
                msg = (
                    f"Camera server exited immediately (rc={rc}). "
                    f"Stderr: {stderr_text or '(empty)'}"
                )
                logger.error(msg)
                raise RuntimeError(msg)
            try:
                self._client = CameraClient(
                    self._frame_buffer, self._host, self._port
                )
                return self._client.connect(timeout=5.0)
            except Exception as exc:
                last_exc = exc
                self._client = None
                time.sleep(self._PORT_POLL_INTERVAL)
        raise RuntimeError(
            f"Camera server not listening on {self._host}:{self._port} "
            f"within {self._PORT_TIMEOUT}s: {last_exc}"
        )

    def _cleanup_client(self) -> None:
        if self._client is not None:
            self._client.stop()
            self._client = None

    def _terminate_process(self) -> None:
        proc = self._process
        if proc is None:
            return
        proc.terminate()
        try:
            proc.wait(timeout=2)
        except subprocess.TimeoutExpired:
            logger.warning("Camera server did not exit after SIGTERM, sending SIGKILL")
            proc.kill()
        try:
            stdout, stderr = proc.communicate(timeout=1)
            if stdout:
                logger.debug("Camera stdout: %s", stdout.decode(errors="replace"))
            if stderr:
                logger.debug("Camera stderr: %s", stderr.decode(errors="replace"))
        except (subprocess.TimeoutExpired, ValueError):
            pass
        self._process = None

    # -- monitor --------------------------------------------------------------

    def _start_monitor(self) -> None:
        self._monitor_thread = threading.Thread(
            target=self._monitor_loop, name="camera-monitor", daemon=True
        )
        self._monitor_thread.start()

    def _monitor_loop(self) -> None:
        while not self._stop_event.is_set():
            if self._stop_event.wait(self._MONITOR_INTERVAL):
                return
            client = self._client
            if client is None or self._stop_event.is_set() or self._recovering:
                continue
            # Detect TCP disconnect
            if not client.connected:
                logger.warning("Camera disconnected — starting recovery")
                self._start_recovery()
                return
            # Detect frame stall (socket open but no frames arriving)
            if client.last_frame_time > 0:
                stall, timeout = self._fork_stall(client)  # fork: camera-controls
                if stall > timeout:
                    logger.warning(
                        "No frames for %.1fs (limit %.1fs) — treating as stall, starting recovery",
                        stall, timeout,
                    )
                    self._start_recovery()
                    return

    # -- crash recovery -------------------------------------------------------

    def _start_recovery(self) -> None:
        self._recovering = True
        self._state_machine.transition(EngineState.RECONNECTING)
        self._recovery_thread = threading.Thread(
            target=self._recovery_loop, name="camera-recovery", daemon=True
        )
        self._recovery_thread.start()

    def _recovery_loop(self) -> None:
        try:
            for i, delay in enumerate(self._BACKOFF):
                logger.info(
                    "Recovery attempt %d/%d in %ss",
                    i + 1,
                    len(self._BACKOFF),
                    delay,
                )
                if self._stop_event.wait(delay):
                    logger.info("Recovery cancelled by stop()")
                    return
                try:
                    self._cleanup_client()
                    self._terminate_process()
                    if self._stop_event.is_set():
                        return
                    self._fork_begin_attempt()
                    self._spawn_process()
                    hello = self._connect_with_retry()
                    self._fork_remember_connected()
                    self._fork_recovered(hello)  # fork: camera-controls
                    self._client.start_receiving()
                    # Restore camera settings from config
                    restore_controls(self._client, self._config)  # fork: camera-controls
                    self._state_machine.transition(EngineState.SETUP)
                    logger.info("Recovery succeeded on attempt %d", i + 1)
                    self._start_monitor()
                    return
                except Exception as exc:
                    logger.warning(
                        "Recovery attempt %d/%d failed: %s",
                        i + 1,
                        len(self._BACKOFF),
                        exc,
                    )
            logger.error(
                "Camera recovery failed after %d attempts", len(self._BACKOFF)
            )
            self._state_machine.transition(EngineState.ERROR)
        finally:
            self._recovering = False

    # -- fork: camera-server-dotnet (spec section 8) --------------------------
    # Selection of the Windows camera server (C# default, or C) lives in
    # evf.camera.server_select; these are the only places SubprocessManager
    # calls into it besides __init__ and _kill_stale_server.

    def _fork_server_args(self) -> list[str]:
        """Extra command line for the C# server (PUSHNAV_CAMERA_SERVER_ARGS, dev use)."""
        running = self._fork_running
        return server_select.extra_args(running) if running else []

    def _fork_fall_back_to_c_server(self, exit_code: int | None) -> bool:
        """The C# server found no supported camera (exit code 3): start the C server instead.

        Runs inside the same connect attempt, so it consumes no backoff step. The
        fallback is remembered only once it has connected (_fork_remember_connected); a
        fallback that fails too leaves the next attempt starting the C# server again.
        """
        plan = self._server_plan
        fallback = plan.fallback_after_exit(exit_code, self._fork_running) if plan else None
        if fallback is None:
            return False
        stderr_text = ""
        if self._process is not None:
            try:
                _, stderr_bytes = self._process.communicate(timeout=1)
                if stderr_bytes:
                    stderr_text = stderr_bytes.decode(errors="replace").strip()
            except (subprocess.TimeoutExpired, ValueError):
                pass
        logger.info(
            "C# camera server found no supported camera (exit code %s); starting the C server. Stderr: %s",
            exit_code, stderr_text or "(empty)",
        )
        self._process = None
        self._fork_running = fallback
        self._binary_path = fallback.path
        self._spawn_process()
        return True

    def _fork_begin_attempt(self) -> None:
        """A new (re)start attempt begins with the remembered server, not with a fallback that failed."""
        plan = self._server_plan
        if plan is not None:
            self._fork_running = plan.current
            self._binary_path = plan.current.path

    def _fork_remember_connected(self) -> None:
        """The server that connected is the one recovery attempts start with (spec 8.2)."""
        if self._server_plan is not None and self._fork_running is not None:
            self._server_plan.switch_to(self._fork_running)

    def _fork_stall(self, client: CameraClient) -> tuple[float, float]:
        """fork: camera-controls (spec 3.3). ``(seconds without frames, stall timeout)``.

        v1: the fixed 2.0 s since the last FRAME, as always. v2:
        ``max(2.0, 2 * frame interval + 1.0)`` from the latest STREAM_INFO, counted from
        the last message of any type, and suspended (for up to 5 s) while the camera
        says it is reconfiguring the stream.
        """
        now = time.monotonic()
        if client.session_version < 2:
            return now - client.last_frame_time, self._FRAME_STALL_TIMEOUT
        return v2_stall(now, client.last_activity_time, self._FRAME_STALL_TIMEOUT, client.stream_info)

    def _fork_recovered(self, hello: dict) -> None:
        """fork: camera-controls. Tell the engine which camera answered after a recovery."""
        callback = self.on_recovered
        if callback is None:
            return
        try:
            callback(hello)
        except Exception as exc:  # recovery must not fail because of a profile problem
            logger.error("on_recovered callback failed: %s", exc)

    def _fork_log_server(self, hello: dict) -> None:
        """After HELLO: which server answered, and what it drives."""
        if self._server_plan is None or self._fork_running is None:
            return
        logger.info(
            "Camera server %r (%s): backend=%s camera_model=%s",
            self._fork_running.name,
            Path(self._binary_path).name,
            hello.get("backend"),
            hello.get("camera_model"),
        )
