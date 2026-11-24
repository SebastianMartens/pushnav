# PushNav (EVF) — Plate-Solving Push-To System

## Project Overview

A cross-platform desktop app that plate-solves live camera frames to determine telescope pointing,
enabling push-to navigation for manual telescopes.

Python (React UI in a pywebview window + tetra3rs Rust plate-solver) talks to a native camera subprocess over TCP.
Camera servers are platform-specific: Swift (macOS), C/V4L2 (Linux), C/DirectShow (Windows).

External integrations run in-process on three TCP servers: a **Stellarium** binary-protocol
server on `localhost:10001`, an **LX200 Classic** ASCII server on `0.0.0.0:4030` for
SkySafari / Stellarium Mobile / INDI / ASCOM clients, and an **aiohttp HTTP + WebSocket**
server on `0.0.0.0:8765` for the phone-scannable mobile web interface.

The UI has two tabs:
- **Navigation** — live camera frame, plate-solve overlay, sync wizard, push-to arrow.
- **What to See** — filterable catalog of 161 deep-sky objects (vendored from
  the Stargazing-Buddy site) with client-side visibility math. Click a row →
  detail panel; "Set as target" promotes the object to the current GOTO target
  and switches back to Navigation. The right column also hosts a manual
  Location panel; observer location feeds visibility math and falls back to
  Stellarium's reported location when a Stellarium client is connected.

## Build & Run

```bash
uv sync                          # install Python deps from lockfile
(cd web && npm install)          # install Node deps

# Production-style — launches engine + pywebview window with built React UI
(cd web && npm run build)
uv run python -m evf.main

# Dev mode (HMR) — Vite serves UI on :5173 with hot reload
uv run python -m evf.main --dev --no-window     # terminal 1: engine only
(cd web && npm run dev)                          # terminal 2: Vite dev server
# Open http://localhost:5173 in your browser
```

### Platform-specific camera build

```bash
scripts/build_camera_mac.sh         # macOS — Swift camera server
make -C camera/linux                # Linux — C/V4L2 camera server
```

```cmd
camera\windows\build.bat            :: Windows — C/DirectShow camera server (run from VS Developer Command Prompt)
camera\windows-dotnet\build.bat     :: Windows — C#/.NET 10 Native AOT camera server (default; IDS cameras). Needs the .NET 10 SDK + MSVC linker
```

On Windows the engine starts the C# server by default, and the C server when the C# one
finds no supported camera (exit code 3) or when `camera.windows_server` / the
`PUSHNAV_CAMERA_SERVER` environment variable says `"c"` (`evf/camera/server_select.py`; spec
`specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md`). IDS cameras need the IDS Software
Suite installed separately; PushNav never bundles it. Dev only: `PUSHNAV_CAMERA_SERVER_ARGS`
passes extra arguments to the C# server, e.g. `--backend fake --fake-images tests/samples`.
Windows dev shortcut (fork): `run-windows.bat` in the repo root rebuilds whatever is out of date
(web UI, C# and C camera servers) and starts the app; `-Check` only reports, `-Fake` uses the
fake camera, `-Rebuild` forces a full rebuild.

### Platform-specific app builds

```bash
scripts/build_mac.sh                # macOS — Nuitka → .app → .dmg
scripts/build_linux.sh              # Linux — Nuitka standalone
scripts/build_windows.bat           # Windows — Nuitka → Inno Setup installer
scripts/build_headless_deb.sh       # Raspberry Pi — pushnav-headless .deb (arm64, bookworm container)
scripts/smoke_test_headless_deb.sh  # install + run the .deb in clean bookworm / trixie containers
```

The Raspberry Pi build is **always headless** and named `pushnav-headless` to
keep it distinct from the Linux PC/laptop build. It bundles a standalone
Python under `/usr/lib/pushnav-headless` without the `desktop` dependency
group (no pywebview / PyQt6), runs as the `pushnav-headless` systemd service
(user `pushnav`, state in `/var/lib/pushnav-headless`), and the launcher sets
`PUSHNAV_ROOT` so `evf/paths.py` uses the Linux release layout. With
`--no-window` the engine runs a camera watchdog that keeps retrying the
camera in the background, and shows its status on the Pi's ACT/PWR LEDs
(`evf/engine/status_led.py`), plus a brief green flash when a client
connects or sends a target (the servers' `on_client_connected` callbacks
and `GotoTarget` changes). The LEDs are only writable because the
unit's `ExecStartPre=+`/`ExecStopPost=+` run `evf.engine.led_helper
take`/`restore` as root; `led.enabled` in config.json turns it off.
A jumper from GPIO21 (pin 40) to GND (pin 39) turns on debug mode on the Pi
without environment variables: another root `ExecStartPre=+` step,
`evf.engine.debug_pin`, reads the pin and creates `/run/pushnav-headless/debug`
(in the unit's `RuntimeDirectory=`), which `main.py` treats like `PUSHNAV_DEBUG=1`.

### Running tests

```bash
uv run pytest tests/
```

## Project Layout

```
pyproject.toml                  # project config (hatchling build, uv sources)
python/evf/                     # main Python package
  main.py                       # entry point — engine + pywebview
  paths.py                      # centralized path resolution (dev vs release bundles)
  network.py                    # shared LAN-IP probe used by webserver + engine
  ui_notify.py                  # native error dialog for fatal start-up errors (windowed mode only)
  engine/                       # core engine, state machine, threading
    engine.py                   # main engine (owns all subsystems)
    state.py                    # state machine
    frame_buffer.py             # latest-frame buffer (no queues)
    pointing.py                 # telescope pointing state
    navigation.py               # goto-target navigation logic
    goto_target.py              # thread-safe goto target container
    camera_profile.py           # fork: active camera profile, solver FOV, stream geometry for the web server
    sample_injector.py          # dev-mode sample-image injector
    audio.py                    # audio feedback (lock/lost sounds)
    epoch.py                    # J2000 <-> JNow precession (pyerfa)
  camera/                       # TCP camera client
    client.py                   # camera TCP client
    protocol.py                 # camera protocol (de)serialization
    subprocess_mgr.py           # camera subprocess lifecycle manager
    stream.py                   # fork: stream geometry (HELLO size, or STREAM_INFO in a v2 session), STREAM_INFO / FRAME_RAW parsing, stall timeout
    controls.py                 # fork: restore_controls() / remember_control(): saved control values per camera profile
  solver/                       # tetra3rs (Rust) plate-solving
    solver.py                   # solver wrapper
    thread.py                   # solver worker thread
    databases.py                # fork: solver-database registry, selection, swap (narrow/wide)
    fov.py                      # fork: solver FOV parameters (SolverParams), FOV resolution, SolverParamsHolder, lens maths
    fov_learning.py             # fork: FOV learning (median of 5 solves, range / step guards)
    fov_search.py               # fork: FOV search for a profile without a FOV (alternates the databases)
    sync.py                     # multi-star sync / calibration
  stellarium/                   # Stellarium telescope protocol server
    server.py                   # TCP server for Stellarium connection
    protocol.py                 # Stellarium binary protocol
  lx200/                        # LX200 Classic TCP protocol server
    server.py                   # TCP server (select-based, multi-client)
    protocol.py                 # LX200 ASCII parsing + dispatch
  webserver/                    # aiohttp HTTP + WebSocket server
    server.py                   # serves React build, MJPEG, /ws, /api/*
  config/                       # configuration
    manager.py                  # JSON config read/write
    profiles.py                 # fork: per-camera profiles (camera key, FOV) in config.json
    logging_setup.py            # logging configuration
web/                            # React + Vite + TS + Tailwind + shadcn/ui front-end
  package.json
  vite.config.ts
  src/
    App.tsx
    main.tsx
    components/                 # LiveView, Wizard, Settings, DebugPanel, ...
      catalog/                  # WhatToSee tab — Buddy/Advanced sub-tabs, table, filters, detail, time, LocationPanel
        advanced/               # AdvancedTab: search input + results + ManualEntry RA/Dec panel
        buddy/                  # BuddyTab (curated 161 catalog)
      live-view/                # LiveView, NavOverlay, AxesOverlay, StarOverlay, ...
        SkyDome/                # always-visible 3D dome (R3F) — pointing/target markers, telescope cylinder
    hooks/                      # useEngineState (WebSocket subscription), useView
    lib/                        # api client, types, astronomy (altAz/rise/set), catalogTypes, advancedSearch
    data/
      objects.json              # vendored buddy catalog (161 curated entries)
      openngc.json              # OpenNGC trimmed to ~12.5k NGC objects (CC-BY-SA 4.0)
      hyg-bright.json           # HYG v3 trimmed to ~8.8k bright stars (CC-BY-SA 4.0)
  public/                       # logo, inapp-title, favicons
  dist/                         # build output (gitignored)
camera/
  mac/                          # Swift camera server (macOS)
  linux/                        # C/V4L2 camera server (Linux)
  windows/                      # C/DirectShow camera server (Windows)
  windows-dotnet/               # C#/.NET 10 Native AOT camera server (Windows, default; IDS cameras)
data/
  tetra3rs_gaia.bin             # tetra3rs Gaia star catalog, narrow FOV 7-10 deg (~50 MB)
  tetra3rs_gaia_wide.bin        # fork: wide-field catalog, FOV 18-32 deg, mag 7 (~10 MB)
  solver_dbs.json               # fork: registry of the two databases (FOV ranges, counts, hashes)
  VERSION.json                  # app + protocol + db version metadata
  sounds/                       # audio feedback WAVs (lock, lost, goto_ack)
  web_dist/                     # React build output (release-only — copied here by build scripts)
docs/                           # GitHub Pages documentation
  index.md                      # GitHub Pages landing page
  hardware.md                   # camera, lens, DIY, shopping list
  design.md                     # KISS philosophy, design decisions
  _config.yml                   # Jekyll config for GitHub Pages
  assets/                       # documentation images
hardware/                       # 3D-printable mechanical designs (OpenSCAD)
  3d_models/                    # OpenSCAD source and pre-built STLs (README lists every part)
    housing_v3.scad             # recommended camera housing, hood, cap and low (laptop) mount
    housing_v4.scad             # optional Pi 3A+ mount and box for headless use (YAPP_Box)
    housing_v2.scad, housing.scad  # deprecated v2 (threaded) and v1 (bolted) housings
    laser_pointer_holder.scad   # 23 mm laser pointer cradle on a finder-shoe saddle
    camera_base.scad            # spare for the camera module's white base
    vendor/                     # threads.scad (CC0), YAPP_Box (MIT)
    stls/                       # pre-built STL files ready for printing
marketing/                      # app branding assets (logo, in-app title)
linux/                          # Linux desktop integration (pushnav.desktop)
packaging/headless/             # pushnav-headless .deb: launcher, systemd unit, debian/ maintainer scripts,
                                #   and user-data (first-boot config for the ready-made SD card, attached to releases)
build/                          # build output (gitignored)
scripts/                        # build and dev scripts
  build_mac.sh                  # macOS Nuitka build → .app/.dmg
  build_linux.sh                # Linux Nuitka build
  build_windows.bat             # Windows Nuitka + Inno Setup build
  build_headless_deb.sh         # Raspberry Pi pushnav-headless .deb build
  smoke_test_headless_deb.sh    # container install/run test for the .deb
  build_camera_mac.sh           # compile Swift camera server
  run_dev.sh                    # dev launch (macOS)
  run_dev_linux.sh              # dev launch (Linux)
  run_dev_windows.bat           # dev launch (Windows)
  run_mock_camera.sh            # launch mock camera for testing
  pushnav.iss                   # Inno Setup installer script (Windows)
  sync_catalog.py               # vendor objects.json from ~/Devel/Github/stargazing-buddy-site
  make_pi_label.py              # 50 x 20 mm SVG + PNG label for a ready-made Pi (QR + hotspot details)
  fonts/                        # Atkinson Hyperlegible (OFL), used only by make_pi_label.py
  render_3d_models.py           # render docs/assets/3d_model_*.png from the STLs (uv run, PyVista)
  test_stellarium_live.py       # manual Stellarium integration test
tests/                          # test suite (pytest)
  samples/                      # plate-solve test images (a–d.png + targets/)
specs/start/                    # design specifications
```

## Key Dependencies

- **tetra3rs** — Rust (pyo3) plate-solver, installed from a prebuilt wheel (`tetra3rs==0.7.1`); no vendoring or Rust toolchain needed
- **pywebview** — wraps the OS webview (WebKit/WebView2/GTK) for the desktop window; lives in the `desktop` dependency group (default for `uv sync`, left out of the headless Pi package)
- **React + Vite + TypeScript + Tailwind + shadcn/ui** — front-end stack (under `web/`)
- **three** + **@react-three/fiber** + **@react-three/drei** — WebGL rendering for the always-visible Sky View 3D dome (`web/src/components/live-view/SkyDome/`). Labels use drei `<Html>` to stay offline; `<Text>` is intentionally avoided so troika-three-text's CDN font-resolver is never loaded
- **numpy**, **Pillow** — image/array handling (solver wrapper) and the sync rotation maths
- **playsound3** — audio feedback for solve lock/lost events
- **aiohttp** — HTTP + WebSocket server (serves the React build, /ws state, /frame.mjpg, /api/*)
- **qrcode.react** (npm) — QR-code rendering for the LAN URL in the Settings panel
- **pyerfa** — IAU 2006 precession (J2000 ↔ JNow) for the LX200 protocol server
- **pyyaml** (dev group) — used by `scripts/sync_catalog.py` to parse the
  buddy-site markdown frontmatter when refreshing `web/src/data/objects.json`,
  by `tests/test_headless_user_data.py` to check `packaging/headless/user-data`,
  and by `scripts/make_pi_label.py` to read its passwords and address

Dev dependencies: **nuitka** (builds), **pytest** (tests), and **segno**,
**fonttools**, **uharfbuzz** and **freetype-py** for `scripts/make_pi_label.py`
(QR code, outlined and kerned text, and the PNG). The Pi package is built
with `--no-dev`, so none of these reach it.
Docs dependency group (`uv sync --group docs`): **mkdocs-material** for the
GitHub Pages site under `docs/`. fork: the fork has no Pages site; `docs.yml` only checks
that `mkdocs build --strict` passes and does not deploy.

The runtime app does **not** depend on `~/Devel/Github/stargazing-buddy-site`.
That repo is only consulted by `scripts/sync_catalog.py` when the maintainer
runs the script to refresh the vendored JSON.

## Important: Loading the tetra3rs Catalog

The plate solver is **tetra3rs** (a Rust/pyo3 port of ESA's tetra3). The star catalog is a
prebuilt `SolverDatabase` at `data/tetra3rs_gaia.bin` (~50 MB, Gaia DR3, mag 8), loaded once
at startup:

```python
import tetra3rs
from evf.paths import tetra3rs_database_path

# load_from_file takes a plain string path — no Path-vs-string gotcha
db = tetra3rs.SolverDatabase.load_from_file(str(tetra3rs_database_path()))
```

In application code, use `evf.paths.tetra3rs_database_path()` (handles dev vs release path
resolution). The `.bin` is regenerated by `scripts/build_tetra3rs_catalog.py` from the
`gaia-catalog` package.

Fork (`specs/camera-server/SPEC_CAMERA_CONTROLS.md` section 5.4): a second, wide-field database
`data/tetra3rs_gaia_wide.bin` (18-32 deg, mag 7, for the IDS camera with a 25 mm lens) ships
next to the narrow one, and `data/solver_dbs.json` lists both with their FOV ranges.
`scripts/build_tetra3rs_catalog.py --db narrow|wide|all [--force]` builds them and rewrites the
registry and the `solver_db_version*` keys in `VERSION.json`. Builds are not byte-reproducible
(same sizes and counts, different bytes), so do not regenerate the narrow file without a reason.
`evf.solver.databases` reads the registry (`evf.paths.solver_db_dir()`), picks the database whose
range contains the FOV, and keeps only one loaded; a swap runs on the solver thread
(`PlateSolver.apply_pending_database()`). The FOV comes from the connected camera's profile
(`camera.profiles[<camera key>].fov_h_deg`, key = HELLO `camera_key` or `<backend>:<camera_model>`, see
`evf.config.profiles` and `evf.engine.camera_profile`); the engine loads the database for
`camera.last_camera_key` at startup and requests the right one on camera connect, and passes
`fov` / `max(1.5, 0.10 * fov)` to `PlateSolver.solve_frame(image, fov_estimate_deg, fov_max_error_deg)`
through a lock-protected `SolverParams` holder. With no profile FOV it is today's behaviour: the narrow
database, 8.86 deg / 1.5 deg, 1280x720. The Settings panel shows the camera, the FOV with its source
(user / learned / searching / default), the loaded database and a focal-length field (`POST /api/camera/fov`,
`CameraProfile.set_user_fov()`). A camera whose HELLO carries a sensor size (the C# server) and whose profile has
no FOV runs the FOV search (`evf.solver.fov_search`, both databases held while it runs) and then learns the FOV
(`evf.solver.fov_learning`); other cameras keep the legacy FOV and learn nothing. `docs/hardware.md` also
describes entering the FOV by hand.

## Architecture Rules

- UI must NOT own core logic; core engine runs without UI
- Solver thread never blocks UI thread
- No frame queues — always use most recent frame only
- All shared state protected by `threading.Lock`
- Camera subprocess is a separate OS process, communicating over TCP
- The camera_server binary should `exit(1)` on device disconnect / fatal
  capture error; the engine's `CameraSubprocessMgr` watches the TCP socket
  and runs a 5-attempt backoff recovery loop (1, 2, 4, 8, 15 s)
- Path resolution goes through `evf/paths.py` (handles dev repo, macOS .app bundle, Linux/Windows release)
- All fonts in the React UI come from the OS — the app ships no web fonts and
  applies no `font-family` overrides; Tailwind v4 preflight uses its built-in
  `font-sans` / `font-mono` stacks (system-ui / ui-monospace)

## Specs

Detailed design docs live in `specs/start/`:
- `SPEC_ARCHITECTURE.md` — threading model, state machine, data structures
- `SPEC_PRODUCT.md` — product requirements
- `SPEC_PROTOCOL_CAMERA.md` — camera TCP protocol
- `SPEC_PROTOCOL_STELLARIUM.md` — Stellarium telescope binary protocol (J2000, port 10001)
- `SPEC_PROTOCOL_LX200.md` — LX200 Classic ASCII protocol for SkySafari / Stellarium Mobile / INDI / ASCOM (JNow, port 4030)
- `SPEC_BUILD_RELEASE.md` — build and release process
- `ACCEPTANCE_TESTS.md` — acceptance criteria
