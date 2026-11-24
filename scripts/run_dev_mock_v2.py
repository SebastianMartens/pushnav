# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork addition; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md section 10, task 5.4).
"""Dev only: run PushNav against the Python mock camera server in protocol v2 mode.

No camera server speaks protocol v2 yet (the C# server joins in slice S5), so this starts
``tests/mock_camera_server.py`` inside this process and makes the engine connect to it
instead of spawning a camera server binary. Production server selection
(``evf/camera/server_select.py``, ``evf/paths.py``) is not touched: the script only swaps
the engine's ``SubprocessManager`` for one that does not spawn anything.

    uv run python scripts/run_dev_mock_v2.py                 # window
    uv run python scripts/run_dev_mock_v2.py --no-window     # engine + web server only
    uv run python scripts/run_dev_mock_v2.py --v1 --no-window  # the same mock as a v1 camera

Extra arguments (``--no-window``, ``--dev``) go to ``evf.main``. The mock serves
the sample frames from ``tests/samples``. A binning change shows "Reconfiguring camera"
for ``--reconfigure-s`` seconds (default 0.8).

Use a throw-away config (this is a dev tool, it writes camera profiles): set ``APPDATA``
(Windows) or ``XDG_CONFIG_HOME`` (Linux) to a temp directory first, with a ``config.json``
that has its own ``webserver.port`` and ``audio.enabled: false`` if you do not want sound.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "python"))

MOCK_PORT = 8764  # what SubprocessManager connects to by default


def _attached_manager_class():
    from evf.camera.subprocess_mgr import SubprocessManager

    class MockAttachedManager(SubprocessManager):
        """The normal manager (connect, monitor, recovery), minus spawning a process."""

        def __init__(self, frame_buffer, state_machine, config):
            super().__init__(frame_buffer, state_machine, config, binary_path="in-process-mock",
                             port=MOCK_PORT)

        def _kill_stale_server(self) -> None:
            pass

        def _spawn_process(self) -> None:
            pass

    return MockAttachedManager


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
                                     allow_abbrev=False)
    parser.add_argument("--v1", action="store_true", help="serve the mock as a protocol v1 camera")
    parser.add_argument("--fps", type=float, default=10.0, help="frames per second of the mock (v1 mode)")
    parser.add_argument("--reconfigure-s", type=float, default=0.8,
                        help="how long a binning change stays 'reconfiguring' (v2 mode)")
    args, rest = parser.parse_known_args()

    from tests.mock_camera_server import MockCameraServer

    server = MockCameraServer(
        port=MOCK_PORT, fps=args.fps, v2=not args.v1,
        v2_options={"reconfigure_s": args.reconfigure_s},
    )
    server.start()
    print(f"Mock camera ({'v1' if args.v1 else 'v2'}) on 127.0.0.1:{server.port}", flush=True)

    import evf.engine.engine as engine_module
    engine_module.SubprocessManager = _attached_manager_class()
    engine_module._usb_video_device_present = lambda: True  # Linux checks sysfs for a USB camera

    sys.argv = [sys.argv[0], *rest]
    from evf import main as evf_main
    try:
        evf_main.main()
    finally:
        server.stop()


if __name__ == "__main__":
    main()
