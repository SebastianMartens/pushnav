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

"""EVF — Electronic Viewfinder main entry point.

Per impl0.md §8.6.
"""

import json
import logging
import os
import signal
import socket
import sys
import threading
import urllib.error
import urllib.request
from pathlib import Path

from evf import ui_notify
from evf.config.logging_setup import setup_logging
from evf.config.manager import ConfigManager
from evf.engine.debug_pin import debug_flag_set
from evf.engine.engine import Engine

logger = logging.getLogger(__name__)

# Default window dimensions, sized to the React UI's `max-w-5xl` (1024px) layout
# plus minimal padding.
# fork: the window is resizable (upstream fixed it at this size, so the page
# scrolled whenever content was taller than the window). The layout is
# responsive (it is also served to phones), so `_VP_MIN_*` only keeps it above
# the Tailwind `md` breakpoint (768px): the three-column navigation layout
# stays instead of collapsing into the stacked phone layout.
# Each platform's webview (WKWebView / WebKit2GTK / WebView2) handles HiDPI
# scaling natively against its OS's DPI/scale settings — no app-side multiplier.
_VP_WIDTH = 1060
_VP_HEIGHT = 820
_VP_MIN_WIDTH = 800
_VP_MIN_HEIGHT = 600


def _vite_running(port: int = 5173) -> bool:
    """True if **PushNav's** Vite dev server is reachable on localhost:`port`.

    A bare TCP probe isn't enough (the port can be squatted on — macOS uses
    5000 for AirPlay Receiver, for instance) and a generic Vite marker
    like `/@vite/client` would also succeed against an *unrelated* Vite
    running someone else's project. Probe a PushNav-specific public asset
    instead: `web/public/inapp-title.png` is served at
    `/static/inapp-title.png` because of `vite.config.ts`'s
    `base: "/static/"`. A foreign Vite (default base `"/"`) 404s the path
    so we correctly fall through to the prod URL on :8765.
    """
    # Use `localhost` (not 127.0.0.1) so the OS resolver tries both ::1 and
    # 127.0.0.1 — Vite on macOS binds IPv6-only by default, while other
    # platforms or `--host` bind IPv4. urllib walks the address list until
    # one succeeds, so a single probe covers both families.
    try:
        with urllib.request.urlopen(
            f"http://localhost:{port}/static/inapp-title.png", timeout=0.3
        ) as r:
            return r.status == 200
    except (urllib.error.URLError, OSError, TimeoutError):
        return False


def _identify_port_occupant(port: int) -> str | None:
    """Return None if `port` is free, else "pushnav" or "other".

    Distinguishes "another PushNav" from "another app on the port" by
    hitting our /api/version identifier endpoint.
    """
    # Fast TCP probe first — if the port is free, the slow HTTP probe
    # would just time out. Use 127.0.0.1 explicitly: webserver binds
    # 0.0.0.0 so this always reaches our listener if any.
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.3):
            pass  # something is bound
    except OSError:
        return None  # port free, fine to start

    # Port is busy. Identify the occupant.
    try:
        with urllib.request.urlopen(
            f"http://127.0.0.1:{port}/api/version", timeout=0.5
        ) as r:
            if r.status == 200:
                payload = json.loads(r.read(512))
                if payload.get("app") == "pushnav":
                    return "pushnav"
    except (urllib.error.URLError, OSError, TimeoutError, json.JSONDecodeError):
        pass
    return "other"


def _port_conflict_message(port: int, occupant: str, config_path: Path | str) -> str:
    """Build the user-facing text for a busy web port.

    `occupant` is "pushnav" or "other" (see `_identify_port_occupant`).
    """
    if occupant == "pushnav" and os.environ.get("PUSHNAV_ROOT"):
        # pushnav-headless .deb: the other instance is almost always the service.
        return (
            f"PushNav is already running on port {port} "
            f"(probably the pushnav-headless service).\n"
            f"Stop it first with: sudo systemctl stop pushnav-headless"
        )
    if occupant == "pushnav":
        return (
            f"PushNav is already running on port {port}.\n"
            f"Close the existing window before launching another instance."
        )
    return (
        f"Port {port} is in use by another application.\n"
        f"Close that application, or change webserver.port (the \"port\" key "
        f"in the \"webserver\" section) in the PushNav config file:\n"
        f"{config_path}"
    )


def _check_single_instance(
    port: int, config_path: Path | str, *, no_window: bool = False
) -> None:
    """Exit with status 1 if another PushNav (or anything) is already on `port`.

    Runs before any engine threads, port binds, or pywebview windows
    open. The conflict is logged; in windowed mode it is also shown in a
    native error dialog (the windowed builds have no console), while
    headless mode (--no-window: Raspberry Pi service) only prints to
    stderr — nobody could dismiss a dialog there.
    """
    occupant = _identify_port_occupant(port)
    if occupant is None:
        return

    msg = _port_conflict_message(port, occupant, config_path)
    logger.error("Cannot start: %s", msg.replace("\n", " "))
    if no_window:
        print(msg, file=sys.stderr)
    else:
        ui_notify.show_error("PushNav", msg)  # falls back to stderr itself
    sys.exit(1)


def main() -> None:
    # PUSHNAV_DEBUG=1 / --dev toggles in-app dev features only (sample
    # injection, frame capture, DebugPanel). On the headless Pi a jumper
    # from GPIO21 to GND does the same (evf.engine.debug_pin). Opening the
    # webview's inspector is a separate opt-in via WEBVIEW_DEBUG=1: on
    # Linux Qt, pywebview's debug=True starts QtWebEngine's remote-debugging
    # server which truncates aiohttp static-file responses to ~440 bytes
    # and leaves the React bundle unparseable, so we keep the inspector off
    # by default even in dev_mode.
    dev_mode = (
        "--dev" in sys.argv
        or os.environ.get("PUSHNAV_DEBUG") == "1"
        or debug_flag_set()
    )
    webview_debug = os.environ.get("WEBVIEW_DEBUG") == "1"
    no_window = "--no-window" in sys.argv
    # --react is now the default; accept the flag as a no-op for back-compat
    _ = "--react" in sys.argv

    # Single-instance guard. Reads config first (we need web_port for the
    # probe) and reuses the same ConfigManager for the engine, so it isn't
    # loaded twice from disk. Logging goes up before the check so a port
    # conflict lands in evf.log (engine.startup_logging() below re-runs the
    # same setup and logs the version banner). Headless gets no console
    # handler yet: the conflict is printed to stderr exactly once below.
    config = ConfigManager()
    setup_logging(verbose=config.verbose, console=not no_window)
    _check_single_instance(config.web_port, config.path, no_window=no_window)

    engine = Engine(dev_mode=dev_mode, config=config)

    # Engine + servers come up either way
    engine.startup_logging()
    engine.startup_solver()
    engine.startup_stellarium()
    engine.startup_lx200()
    engine.startup_webserver()

    def _start_camera() -> None:
        """Spawn + connect the camera, then start the solver thread if it came up.

        This is the slow part of startup: the native camera server retries for
        ~8 s when no camera is attached, and DirectShow/AVFoundation init takes a
        few seconds even when one is. In windowed mode this runs in a background
        thread AFTER the window is shown — the React UI already renders a
        'connecting' state for camera_connected=False — so the window appears
        immediately instead of blocking on the camera.
        """
        try:
            engine.startup_camera()
            if engine.camera_connected:
                engine.startup_solver_thread()
        except Exception:
            logger.exception("Camera startup failed")

    if no_window:
        _start_camera()  # headless: no window to unblock, so start it inline
        # No "Retry camera" click to rely on: keep retrying in the background
        # so a camera plugged in after boot (or after recovery gave up) comes up.
        engine.start_camera_watchdog()
        engine.start_status_led()
        logger.info("Running headless (--no-window). Press Ctrl-C to exit.")
        stop = threading.Event()
        signal.signal(signal.SIGINT, lambda *_: stop.set())
        signal.signal(signal.SIGTERM, lambda *_: stop.set())
        stop.wait()
        engine.shutdown()
        return

    # Default: open pywebview window pointing at the React UI
    import webview

    # Use Vite's HMR server when it's actually running; otherwise serve the
    # prebuilt bundle through the in-process aiohttp server. The prod URL
    # is built from engine.config.web_port (single source of truth) so a
    # user-customized port keeps the navigation URL and the bound webserver
    # in sync — and old config files carried over from earlier installs
    # don't desync the two sides.
    web_port = engine.config.web_port
    # Append a per-launch cache-buster to the entry URL. WKWebView keeps a
    # persistent HTTP cache under ~/Library/Caches/com.pushnav.evf and
    # aiohttp's FileResponse sends no Cache-Control, so heuristic freshness
    # can keep serving a stale index.html past Cmd-Q + restart and the
    # webview keeps booting an old JS bundle. A unique query per launch
    # makes the URL miss any prior cache entry.
    import time as _time
    cache_buster = f"?v={int(_time.time())}"
    target_url = (
        f"http://localhost:5173{cache_buster}" if _vite_running()
        else f"http://localhost:{web_port}/{cache_buster}"
    )
    title = f"PushNav {engine.app_version}"

    # background_color paints the native window dark *before* the page renders,
    # so the WebView2 / WKWebView frame doesn't flash white on startup while the
    # JS bundle parses. The React UI is always dark (see web/index.html); this
    # matches the .dark --background (oklch(0.10 0.06 22)).
    window = webview.create_window(
        title,
        target_url,
        width=_VP_WIDTH,
        height=_VP_HEIGHT,
        resizable=True,  # fork: was False
        min_size=(_VP_MIN_WIDTH, _VP_MIN_HEIGHT),
        background_color="#0D0A0B",
    )
    # Linux/QtWebEngine flashes white on startup despite background_color above:
    # QtWebEngine renders the page on its own compositor surface whose default
    # background is white, and pywebview only paints that surface dark for
    # *transparent* windows (platforms/qt.py WebPage). background_color only
    # reaches the QMainWindow palette, which the page surface covers — so the
    # page stays white until our HTML background paints. WebView2 (Windows) and
    # WKWebView (macOS) don't expose this surface, which is why they don't flash.
    # Set the QWebEnginePage background explicitly, before the window is shown.
    if sys.platform.startswith("linux"):

        def _paint_qt_page_dark() -> None:
            try:
                from qtpy.QtGui import QColor

                window.native.webview.page().setBackgroundColor(QColor("#0D0A0B"))
            except Exception as exc:  # backend internals are best-effort
                logger.debug("Could not set QtWebEngine page background: %s", exc)

        window.events.before_show += _paint_qt_page_dark
    # Connect the camera in the background so the window appears immediately
    # instead of blocking on the ~8 s camera-server retry (worst when no camera
    # is attached). daemon=True so it never holds up shutdown on window close.
    threading.Thread(
        target=_start_camera, name="camera-startup", daemon=True
    ).start()
    # On Linux, force pywebview's Qt backend (QtPy + PyQt6 + PyQt6-WebEngine,
    # pulled in by the pywebview[qt] extra in pyproject.toml). Without this,
    # pywebview probes GTK first and only falls through to Qt on ImportError —
    # works either way, but `gui='qt'` skips the noisy GTK traceback in logs
    # when PyGObject isn't installed (the supported state on Linux now).
    # macOS uses Cocoa/WKWebView and Windows uses WebView2 (Edge Chromium);
    # `gui=None` lets pywebview pick the platform-native backend there.
    gui = "qt" if sys.platform.startswith("linux") else None
    # private_mode=False is harmless on Cocoa/WebView2/QtWebEngine and was
    # historically needed to keep localStorage alive on WebKit2GTK.
    webview.start(gui=gui, debug=webview_debug, private_mode=False)

    engine.shutdown()


if __name__ == "__main__":
    main()
