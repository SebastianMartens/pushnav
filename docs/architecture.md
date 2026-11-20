# Architecture diagrams

High-level diagrams of how PushNav is put together. Each one is a standalone
HTML page with an inline SVG, a light and a dark theme (toggle at the top),
zoom and pan, and export. They were drawn from the code in `python/evf/`,
`camera/`, `web/`, `packaging/` and `scripts/`, and cross-checked with the
specs in `specs/start/` and `specs/camera-server/`.

**State reflected:** commit `e921d73` (2026-11-19). Every diagram says so in
its subtitle. Where code and spec disagree, the diagrams follow the code.

| Diagram | What it shows |
|---|---|
| [System overview](architecture/system-overview.html) | The engine process, its three servers (Stellarium `:10001`, LX200 `:4030`, web `:8765`), the solver thread, the camera client and the camera server subprocess, and who connects from outside. |
| [Camera servers per platform](architecture/camera-servers.html) | The macOS, Linux and Windows camera servers, how the Windows server is chosen (`camera.windows_server`, `PUSHNAV_CAMERA_SERVER`, exit code 3 and missing-exe fallbacks), and which parts are upstream and which are fork-only. |
| [Camera protocol](architecture/camera-protocol.html) | HELLO handshake, CONTROL_INFO, the FRAME stream, SET_CONTROL / GET_CONTROLS, ERROR, disconnect and stall recovery (5 attempts, 1, 2, 4, 8, 15 s). The v2 negotiation (#3) is shown as planned. |
| [Engine states and sync wizard](architecture/engine-states.html) | The states from `evf/engine/state.py` and the transitions the code takes between them. |
| [Deployment variants](architecture/deployment-variants.html) | Nuitka desktop builds for macOS, Linux and Windows next to the headless Raspberry Pi `.deb` (systemd, status LEDs, hotspot, debug jumper). |

The diagrams are generated with the archify skill and checked for layout
problems. They are snapshots: if the code moves on, redraw them and update
the commit in the subtitle.
