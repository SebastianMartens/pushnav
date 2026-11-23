# SPEC_CAMERA_CONTROLS.md — PushNav

Version: 1.3 (2026-11-18: checked against `main` d665e17 after PRs #9, #10, #14; camera key from HELLO in every session; FOV API; slice plan in §10.8. 1.2: C server stays v1 side by side, upstream minimal-diff rules; 1.1: 25 mm reference lens, wide-field solver database)
Status: Approved (PR #8); slice S1a (wide database, registry, shipping) implemented on `feat/s1a-wide-solver-db`, the rest not yet
Date: 2026-11-18
Target: all platforms (engine, config, web UI); camera side on the Windows C# server first
Issue: SebastianMartens/pushnav#3 (camera controls: exposure time, fps, gain, binning)
Depends on: `specs/start/SPEC_PROTOCOL_CAMERA.md` §11 (protocol v2) and
`SPEC_CAMERA_SERVER_WINDOWS.md` (#1). The C# server with the IDS backend
is merged and speaks v1 (PRs #9, #10, #14); its v2 part is that spec's
task 7, scheduled in the slices of §10.8.
Baseline: `specs/start/SPEC_ARCHITECTURE.md` v2.0 (§5, §7, §8, §13)

---

## 1. Overview

Let the user set **exposure time** (in real time units), **frame rate**,
**gain** and **binning** from the PushNav UI (desktop window and the mobile
web view, which share the same React app), on whichever camera server is
running. Each camera server advertises only the controls its camera
supports, with real ranges (protocol v2 CONTROL_INFO). The UI shows only
advertised controls. Settings persist **per camera model**.

The protocol part is in `SPEC_PROTOCOL_CAMERA.md` §11. This spec covers the
Python engine, the config file, the web server and the React UI, and how a
**frame-size change** (binning) reaches the solver and the overlays.

### 1.1 Terms

| Term | Meaning |
|---|---|
| **Control** | A camera setting with an id (`exposure`, `gain`, `fps`, `binning`), a type and a range, as sent in CONTROL_INFO. |
| **Session version** | The protocol version negotiated for the current camera connection (1 or 2). |
| **Camera key** | Stable id of a camera model from HELLO (`camera_key`, e.g. `ids:1409:3060`). The server HELLO is sent before negotiation, so the C# server includes it in v1 sessions too. For servers that don't send it (Swift, Linux C, Windows C) the engine builds `"<backend>:<camera_model>"`. |
| **Camera profile** | The per-camera-key block in config: saved control values and the FOV. |
| **Stream geometry** | Width, height, binning and generation of the frames currently arriving (from STREAM_INFO, or from HELLO / the decoded frame in v1). |
| **Generation** | Counter from STREAM_INFO that increases on every stream reconfiguration. |
| **FOV** | Horizontal field of view in degrees of the whole sensor width with the user's lens. Independent of binning. |

### 1.2 Upstream compatibility

The fork merges upstream (`meridianfield/pushnav`) changes, so this work
follows the same minimal-diff rules as `SPEC_CAMERA_SERVER_WINDOWS.md` §8.5.

**Camera servers we don't touch.** The Windows C/DirectShow server
(`camera/windows/`), the Linux C server and the macOS Swift server are not
modified. They keep speaking protocol **v1**, permanently. That keeps
working because v2 is negotiated on top of v1
(`SPEC_PROTOCOL_CAMERA.md` §11.2): a v2 engine talking to any of them
answers `protocol_version: 1` and runs exactly today's v1 session (only
`exposure` and `gain`, their native units, fixed-size JPEG frames, 2.0 s
stall timeout). The UI then shows only those two controls. Only the C#
server speaks v2.

**Scope of the new features (Sebastian, 2026-11-18).** Exposure in real
units, fps, binning, the per-camera FOV and the wide database target the
C# server only. With the upstream servers (still maintained in the parent
repo) these features are simply not available, and they are not tested
there: Sebastian owns only the IDS camera. The engine must still run an
upstream v1 server exactly as today. That is covered by pytest against
the mock in v1 mode, not by hardware tests.

**New code in new modules** where practical:

| New module | Holds |
|---|---|
| `python/evf/camera/stream.py` | `StreamGeometry`, STREAM_INFO / FRAME_RAW parsing |
| `python/evf/camera/controls.py` | control descriptors, validation, `restore_controls()` (§3.4) |
| `python/evf/config/profiles.py` | camera-profile helpers on top of `ConfigManager` (§4) |
| `python/evf/solver/databases.py` | solver-database registry, selection and swapping (§5.4.3) |
| `python/evf/solver/fov.py` | FOV resolution, learning and FOV search (§5.2, §5.4.4) |
| `tests/test_camera_v2.py`, `tests/test_profiles.py`, `tests/test_solver_databases.py` | new tests; existing test files get only updated expectations |

`tests/test_camera_conformance.py` already exists (fork file from #1, v1
vectors, also runs against the C# exe); this work adds the v2 vectors to
it. `camera/windows-dotnet/**` is fork-owned too, so the C# v2 work
(`SPEC_CAMERA_SERVER_WINDOWS.md` task 7) needs no hook markers.

**Upstream files touched** (small hooks, each marked
`# fork: camera-controls (see specs/camera-server/SPEC_CAMERA_CONTROLS.md)`):

| File | Hook | Why |
|---|---|---|
| `python/evf/camera/protocol.py` | 3 message ids + names | v2 messages |
| `python/evf/camera/client.py` | version negotiation; dispatch of the new message types into `stream.py`; float `set_control` | v2 |
| `python/evf/camera/subprocess_mgr.py` | stall timeout from `stream.py`; recovery calls `restore_controls()` | liveness, restore |
| `python/evf/engine/engine.py` | `startup_camera()` / `set_control()` delegate to `controls.py` / `profiles.py`; owns `SolverParams` | restore, persistence |
| `python/evf/solver/solver.py` | `solve_frame()` takes FOV + max error; database loaded via `databases.py` | per-profile FOV |
| `python/evf/solver/thread.py` | reads `SolverParams`; drops mismatched-size results; feeds FOV learning | frame-size change |
| `python/evf/webserver/server.py` | geometry/FOV from the engine instead of `_FOV_H`/`_IMG_W`/`_IMG_H`; float/enum in `/api/control`; route for `/api/camera/fov` (§6.1) | dynamic geometry |
| `python/evf/config/manager.py` | new default keys only | config |
| `python/evf/paths.py` | `solver_db_dir()` | databases |
| `tests/mock_camera_server.py` | opt-in `v2=True` mode; v1 default behaviour unchanged | tests |
| `data/VERSION.json` | `protocol_version: 2`, `solver_db_version_wide` | versions |
| `scripts/build_tetra3rs_catalog.py` | parameterised (`--db`) | wide database |
| `scripts/build_mac.sh`, `build_linux.sh`, `build_windows.bat`, `build_headless_deb.sh`, `smoke_test_headless_deb.sh`, `.github/workflows/build.yml` | one extra copy/check line for the wide database | shipping |
| `web/src/lib/types.ts`, `web/src/components/controls/CameraControls.tsx`, `web/src/components/settings/Settings.tsx`, `web/src/components/live-view/{NavOverlay,SyncCandidatesOverlay,StarOverlay}.tsx` | control types, settings fields, marker scaling | UI |
| `specs/start/SPEC_PROTOCOL_CAMERA.md` | §11 (v2) and §12 (changelog) appended; v1 text unchanged | protocol |

Merge-risk notes: `engine.py`, `client.py`, `webserver/server.py` and
`CameraControls.tsx` are the likeliest conflict points. Keep hooks at the
edges of functions and push logic into the new modules.
`subprocess_mgr.py`, `config/manager.py`, `paths.py` and
`build_windows.bat` already carry `# fork: camera-server-dotnet` hooks
from #1 (`server_select`, the code-3 fallback, `windows_server`,
`windows_camera_server_paths()`, Phase 0/1a). Put the new hooks next to
them, not inside them.

---

## 2. Current Behaviour (baseline)

- `CameraClient` accepts only `protocol_version == 1`; controls are
  whatever CONTROL_INFO lists; SET_CONTROL values are ints
  (`python/evf/camera/client.py`).
- Engine restores only `exposure` and `gain` from the **global**
  `camera.exposure` / `camera.gain` keys, seeding the range midpoint on
  first run (`engine.py` `startup_camera()`); `SubprocessManager` repeats
  the same restore after recovery.
- Units differ per server: `"100us"` (Linux/macOS, and the C# server's
  v1 view), `"log2s"` (Windows C). A saved value from one server is
  meaningless on another.
- Windows: the C# server (`camera_server_dotnet.exe`) is the default and
  falls back to the C server on exit code 3 (`evf/camera/server_select.py`,
  `SubprocessManager` hooks). It speaks v1 only: HELLO has
  `protocol_version: 1`, the real stream size (1936x1216 for the IDS
  camera), and no `max_protocol_version`, `camera_key` or `capabilities`.
  Its v1 view (`V1ControlAdapter`) advertises only `exposure` (`"100us"`,
  capped at 1.5 s because of the 2.0 s stall timeout) and `gain` (0–100);
  `fps` exists in the backend but is hidden. The IDS backend caps exposure
  at 4 s itself (`IdsBackend.MaxExposureUs`) and has no binning yet. The
  Fake backend (`--backend fake`) already simulates binning.
- With the IDS camera the web server's fixed 1280x720 / 8.86° geometry
  is wrong today: frames are 1936x1216 and the field is 25.6°.
- `POST /api/control` casts `value` to `int` (`webserver/server.py`).
- Geometry constants: `webserver/server.py` hardcodes `_FOV_H = 8.86`,
  `_IMG_W = 1280`, `_IMG_H = 720` (used for the sync origin and the
  navigation projection). `solver/solver.py` hardcodes `_FOV_DEG = 8.86`,
  `_FOV_MAX_ERROR_DEG = 1.5`. The solver already takes the image shape from
  the decoded frame.
- The React overlays already read `image_w`, `image_h`, `fov_h_deg` from
  the `/ws` state (`LiveView.tsx`, `NavOverlay.tsx`, `TrackingStep.tsx`), so
  they follow whatever the server sends.
- The tetra3rs database (`data/tetra3rs_gaia.bin`) is built for
  **7°–10°** FOV (`scripts/build_tetra3rs_catalog.py` `MIN_FOV_DEG`/`MAX_FOV_DEG`).
- Frame-stall timeout is a fixed 2.0 s (`SubprocessManager._FRAME_STALL_TIMEOUT`).
- Config: `CONFIG_VERSION = 1`. A version mismatch **resets the whole config
  to defaults**; missing keys are merged from `DEFAULT_CONFIG`.

---

## 3. Camera Client and Subprocess Manager

### 3.1 Negotiation

- `MIN_PROTOCOL_VERSION = 1`, `MAX_PROTOCOL_VERSION = 2` in `client.py`.
- Accept server HELLO `protocol_version == 1`; session version =
  `min(2, hello.get("max_protocol_version", 1))`; reply with it.
- Keep rejecting anything else (unchanged error path).
- Expose `client.session_version`, `client.camera_key`,
  `client.capabilities`.

### 3.2 New messages

- `MSG_STREAM_INFO = 0x04`, `MSG_FRAME_RAW = 0x05`,
  `MSG_SET_STREAM_CONFIG = 0x15` in `protocol.py` (+ `_TYPE_NAMES`).
- On STREAM_INFO: update a `StreamGeometry` under a lock and call an
  `on_stream_info` callback. On a new `generation`: clear `LatestFrame`
  (so no frame of the old size is solved after the switch).
- FRAME_RAW: parse and ignore in this spec (logged once at DEBUG); the
  engine never requests GRAY8 yet (§7).
- `set_control(id, value: int | float)`.

### 3.3 Liveness

`SubprocessManager` computes the stall timeout per session:

- v1: 2.0 s (unchanged).
- v2: `max(2.0, 2 * frame_interval_s + 1.0)` from the latest STREAM_INFO;
  suspended for up to 5 s while STREAM_INFO `state == "reconfiguring"`.
- Any received message (not only FRAME) resets the stall timer.

### 3.4 One restore path

Move "restore saved controls" from `Engine.startup_camera()` and
`SubprocessManager._recovery_loop()` into one function
`restore_controls(client, config)` (in `evf/camera/`), called by both.
Order: `binning`, `fps`, `exposure`, `gain`, then any other advertised id.
It sends only ids the camera advertises and only values whose saved unit
matches the advertised unit (§4.2).

---

## 4. Config: Per-Camera Profiles

### 4.1 Schema (additive; `CONFIG_VERSION` stays 1)

Bumping `CONFIG_VERSION` would wipe every user's config (§2), so the new
keys are **added** and picked up by `_merge_defaults`:

```json
"camera": {
  "exposure": null,
  "gain": null,
  "extra_camera_ids": [],
  "windows_server": "dotnet",
  "dotnet_uvc": false,
  "max_exposure_us": 4000000,
  "last_camera_key": null,
  "profiles": {
    "ids:1409:3060": {
      "controls": {
        "exposure": { "value": 200000, "unit": "us" },
        "fps":      { "value": 4.0,    "unit": "fps" },
        "gain":     { "value": 20,     "unit": "raw" },
        "binning":  { "value": 1 }
      },
      "fov_h_deg": null,
      "fov_source": null
    }
  }
}
```

- `windows_server` (`"dotnet"` default, or `"c"`) and `dotnet_uvc` are defined in
  `SPEC_CAMERA_SERVER_WINDOWS.md` §6 and §8.1. Windows only; ignored elsewhere.
  `windows_server` is in `DEFAULT_CONFIG` already (#1); `dotnet_uvc` is
  added only with the optional UVC backend (that spec's task 8), not by
  this work.
- `max_exposure_us`: UI/engine cap for the exposure slider (default 4 s,
  §9 Q2). The engine clamps the advertised `max` to it before sending
  controls to the UI.
- `fov_source`: `"user"` | `"learned"` | `null` (§5.2).
- `last_camera_key`: the camera key of the last connected camera; picks
  the solver database to load at startup (§5.4.3).

### 4.2 Rules

- Profile key = HELLO `camera_key` when present, in v1 and v2 sessions
  alike, else `"<backend>:<camera_model>"`. The C# server sends
  `camera_key` from slice S1 on (§10.8), before it speaks v2, so the IDS
  profile keeps the key `ids:1409:3060` when the session moves to v2.
  Profiles are created on first connect.
- Saving: every successful `set_control` from the UI writes
  `profiles[key].controls[id] = {value, unit}` (unit copied from the
  descriptor). The legacy `camera.exposure` / `camera.gain` are still
  written in v1 sessions, for downgrade safety.
- Restoring: a saved value is applied only if its `unit` equals the
  advertised unit; otherwise it is dropped (logged at INFO). This is what
  fixes the "100us vs log2s" problem when switching servers or cameras.
- First run for a key (no saved value):
  1. v1 session and legacy `camera.exposure`/`camera.gain` set: use them
     once (today's behaviour), then store them in the profile.
  2. else the descriptor's `default` if present,
  3. else the midpoint of `[min, max]` for linear controls, the geometric
     mean for `scale: "log"` controls, `options[0]` for enums.
- Calibration (`calibration.finder_rotation`, `calibration.sync_d_body`)
  depends on the optical setup. Add `calibration.camera_key`; when the
  connected camera key differs, `has_calibration` is false (the user
  re-syncs) and the old calibration is kept, not deleted. Per-profile
  calibration storage is out of scope (§9 Q4).

---

## 5. Frame Size and FOV in the Engine

### 5.1 Frame-size change path

```
UI binning 2x2 -> POST /api/control {name: "binning", value: 2}
  -> Engine.set_control -> client.set_control("binning", 2)
  <- CONTROL_INFO (all ranges updated)
  <- STREAM_INFO {state: "reconfiguring"}      -> stall detection paused, UI shows "Reconfiguring camera..."
  <- STREAM_INFO {state: "streaming", generation: g+1, width: 968, height: 608}
       -> StreamGeometry updated, LatestFrame cleared
  <- FRAME (968x608)
       -> SolverThread solves with image_shape from the decoded frame (already the case)
       -> PointingSnapshot.image_size = (608, 968)
       -> /ws state: image_w/image_h from StreamGeometry, overlays rescale
```

Rules:
- No subprocess restart and no TCP reconnect for a binning change.
- `SolverThread` drops a solve result whose `image_size` does not match
  the current `StreamGeometry` (a frame decoded just before the switch);
  it does not count as a failure for the lost/lock sounds.
- On a new generation the engine clears `all_centroids` /
  `matched_centroids` in the next snapshot; RA/Dec pointing stays valid
  (it does not depend on pixel size).
- Sync calibration stays valid across binning changes: `sync_d_body` is a
  body-frame unit vector and the FOV does not change with binning; the
  sync origin pixel is recomputed from the current width (§5.6).
- v1 sessions: geometry = HELLO `default_width/height`, corrected by the
  size of the first decoded frame (logged if they differ).

### 5.2 FOV per camera profile

Binning changes pixels, not the sky area, so the solver's FOV estimate is
a property of **camera + lens**, stored per profile (`fov_h_deg`,
horizontal, whole sensor width):

1. `profiles[key].fov_h_deg` if set (`fov_source` `"user"` or `"learned"`).
2. Else from lens data, if the user entered a focal length in Settings:
   `fov = 2 * atan(sensor_width_mm / (2 * f_mm))` with
   `sensor_width_mm = capabilities.sensor.width * pixel_size_um / 1000`.
3. Else, for a profile with no FOV yet: **FOV search** (§5.4.4).
4. v1 servers / unknown sensors with no FOV and no search result: the
   legacy default **8.86°** (the OV9281 + stock lens setup).

#### 5.2.1 Reference configurations

| Setup | Sensor | Pixels | Pixel | Sensor size | Lens | FOV (h x v, diag) | Plate scale 1x1 / 2x2 |
|---|---|---|---|---|---|---|---|
| Legacy (upstream default) | OV9281 | 1280x720 (as streamed) | — | — | stock | 8.86° x 4.98° (`SPEC_ARCHITECTURE.md` §8.1) | 24.9"/px / — |
| **IDS reference (Sebastian)** | IMX174 (UI-306xCP-M) | 1936x1216 | 5.86 µm | 11.35 x 7.13 mm | **25 mm** Basler | **25.6° x 16.2°**, ~30° diag | **48.3"/px** / **96.7"/px** |

Plate scale = `206.265 * pixel_um / f_mm` arcsec/px; 2x2 binning doubles
it. Binning does not change the FOV.

The shipped database (7°–10°) cannot solve the IDS reference setup. §5.4
adds a second, wide-field database.

#### 5.2.2 Learning the FOV

After 5 consecutive valid solves, take the median of the solver's
`fov_deg` (tetra3rs `SolveResult.fov_deg`, solved horizontal FOV) and
store it as `fov_source = "learned"` if it differs from the current value
by more than 2 %. Guards against drift:

- A `"user"` value is never overwritten.
- The learned value must lie inside the **active database's** FOV range
  shrunk by 5 % at each end; otherwise it is not stored (WARNING once).
  This keeps a few bad solves from walking the FOV out of the range the
  loaded database can solve, which would make every later solve fail.
- A single update may change the FOV by at most 10 %; bigger jumps need
  two consecutive learning rounds that agree within 2 %.
- Learned values are per profile, so another camera never inherits them.

### 5.3 Solver parameters from the profile

`solver/solver.py` today hardcodes `_FOV_DEG = 8.86` and
`_FOV_MAX_ERROR_DEG = 1.5` (lines 54–55). Both move to arguments:

```python
solver.solve_frame(image_bytes, fov_estimate_deg=fov, fov_max_error_deg=err)
```

- `fov` = the profile FOV (§5.2).
- `err` = `max(1.5, 0.10 * fov)`: 1.5° for the legacy 8.86° setup
  (unchanged), 2.6° for the IDS reference. During FOV search (§5.4.4) the
  error is the half-width of the active database's range.
- The solver thread reads `fov`/`err` from an engine-owned, lock-protected
  `SolverParams` snapshot at the start of each solve, so a profile or
  database change takes effect on the next frame.
- `_CENTROID_PARAMS` are unchanged in this spec; they were tuned on
  1280x720 frames at 24.9"/px. At 48"/px (1x1) and 97"/px (2x2) stars
  cover fewer pixels; verify (task 6.3, §9 Q6). If they need different
  values, they become per-database parameters next to the FOV range.

### 5.4 Solver databases (narrow and wide)

#### 5.4.1 Proposed wide-field database

| Parameter | Narrow (shipped today) | **Wide (new, proposed)** |
|---|---|---|
| File | `data/tetra3rs_gaia.bin` | `data/tetra3rs_gaia_wide.bin` |
| `min_fov_deg` / `max_fov_deg` | 7.0 / 10.0 | **18.0 / 32.0** |
| `star_max_magnitude` | 8.0 | **7.0** |
| `pattern_max_error` | 0.005 | 0.005 |
| `verification_stars_per_fov` | 30 | 30 |
| `epoch_proper_motion_year` | 2026.0 | 2026.0 |
| Stars / patterns | 62 793 / 1 914 984 | 21 399 / 348 742 |
| File size | 52.8 MB | **9.7 MB** (5.5 MB zipped) |
| Load time (dev laptop) | 0.13 s | 0.03 s |

Star, pattern, size and load numbers were **measured** on 2026-11-18 by
building the candidate in a scratch directory with the installed
tetra3rs 0.7.1. Nothing was committed.

Why these starting values:

- **FOV 18°–32°**: centred on the 25.6° reference with about ±30 % room.
  That covers focal lengths from about 20 mm (31.7°) to 35 mm (18.4°) on
  the IMX174, as well as the learned FOV moving within the 5 % guard band
  (§5.2.2). A 20°–30° variant measured 8.2 MB / 291k patterns, so the
  wider range costs only ~1.5 MB.
- **Magnitude 7.0**: a 25.6° x 16.2° field (415 deg²) holds ~215 stars
  ≤ mag 7 on average, and still ~70 near the galactic poles. That is
  well above the 30 verification stars, while the solver only uses the
  30 brightest centroids (`max_centroids=30`, typically mag ≤ 5.5–6 in
  such a field). Magnitude 6.5 measured 7.8 MB but leaves only ~40 stars
  per field near the poles. Magnitude 8 (the narrow value) adds no
  pattern benefit at this scale and makes the file bigger.
- Other parameters are copied from the narrow database so the two behave
  the same way.

The final values are **tuned on the real camera** (task 6.3): solve
rate, solve time and false-positive probability on sky frames from the
IDS reference setup at 1x1 and 2x2. The numbers above are starting points.

#### 5.4.2 Building both

`scripts/build_tetra3rs_catalog.py` becomes parameterised:

- A table of database specs in the script (`name`, output file, FOV range,
  magnitude, other parameters). Today's values become the `narrow` entry.
- `--db narrow|wide|all` (default `all`). An existing output file is still
  skipped unless `--force` is given, as today.
- The script writes a sidecar entry for each built file (FOV range,
  magnitude, star/pattern counts, a content hash) into
  `data/solver_dbs.json`. The engine reads this file (§5.4.3), so FOV
  ranges are not hardcoded twice.
- `data/VERSION.json` keeps `solver_db_version` (narrow) as a string and
  gains `solver_db_version_wide` (`"tetra3rs_gaia_wide_<hash>_mag7"`).
  It stays a flat object because the release workflow rewrites the file
  with PowerShell `ConvertTo-Json` / Python `json.dump`. Only the engine's
  startup log reads it; `/api/version` reports only app and protocol
  versions.
- `scripts/bench_solvers.py` gets `--db` to benchmark either file.

Shipping (both files always shipped):

| Package | Change | Size impact |
|---|---|---|
| macOS `.dmg` (`build_mac.sh`) | copy both into `Resources/` | +9.7 MB raw, ~+5.5 MB compressed |
| Linux tar.gz / AppImage (`build_linux.sh`) | copy both into `data/` | same |
| Windows zip / installer (`build_windows.bat`, `pushnav.iss` picks up the directory) | copy both into `data\` | same |
| Pi `.deb` (`build_headless_deb.sh`) | copy both into `$APP/data/` | +9.7 MB installed, ~+5.5 MB `.deb` |
| CI (`build.yml` "Verify required files" steps, 4 jobs) | add `data/tetra3rs_gaia_wide.bin` | — |

The wide file is committed to git like the narrow one (it is a quarter of
its size). The Pi `.deb` ships it too: the Pi can use a wide-field lens,
and 9.7 MB on an SD card is negligible.

`evf/paths.py`: `tetra3rs_database_path()` stays (narrow, for existing
callers and tests). A new `solver_db_dir()` returns the directory holding
`solver_dbs.json` and both files, in all modes (dev, macOS bundle,
Linux/Windows release, headless `PUSHNAV_ROOT`).
`tests/test_paths_headless.py` covers it.

#### 5.4.3 Choosing the database

- `PlateSolver` gets a small registry built from `solver_dbs.json`:
  `[(name, path, min_fov, max_fov)]`.
- Active database = the one whose `[min_fov, max_fov]` contains the
  profile FOV. If two contain it, the narrower one wins. If none does, the
  closest range is used, a WARNING is logged and the state flag
  `fov_out_of_range` is set (the Settings panel shows a hint).
- **Only one database is loaded at a time.** At startup the engine loads
  the one for the last-used profile (`camera.last_camera_key`, new config
  key). With no last profile it loads the narrow one, which is today's
  behaviour.
- On camera connect (HELLO gives the camera key), and whenever the profile
  FOV moves into another range (user edit or learning), the engine
  loads the new database on the solver thread between two solves, then
  drops the old one. Loading takes ≤ 0.15 s on the dev laptop; the solver
  just skips frames during it.
- Memory: the narrow database is ~53 MB on disk and roughly the same in
  RAM; the wide one is ~10 MB. Holding both would cost ~63 MB, which
  matters on the Pi 3A+ (512 MB RAM). Hence one at a time. Exception:
  FOV search (§5.4.4) may hold both for its duration. To be measured on
  the Pi (task 4.8).

#### 5.4.4 FOV search (profile without a FOV)

A new camera with no FOV (no user value, no focal length) would otherwise
fall back to 8.86° and never solve with a wide lens. Instead:

1. The solver alternates frames between the databases, starting with the
   one whose range contains the sensor-based guess if the sensor size is
   known. Each solve uses `fov_estimate` = the database's centre FOV and
   `fov_max_error` = half its range.
2. The first valid solve (normal `min_matches` / `max_prob` checks) fixes
   that database and stores `fov_h_deg` = the solved `fov_deg` with
   `fov_source = "learned"`. From then on the normal learning rules apply
   (§5.2.2).
3. Search is only active in states that already run the solver
   (`SPEC_ARCHITECTURE.md` §4.2) and is shown in the UI ("Finding field of
   view…").

For the IDS reference setup the user can skip the search by entering
25 mm in Settings (§6.3).

### 5.5 FOV and pixel assumptions elsewhere (audit)

Code that assumes the ~8.86° field or 1280x720, as of `main` 2271bc4
(line numbers re-checked on d665e17, unchanged):

| Location | Assumption | Change |
|---|---|---|
| `python/evf/solver/solver.py:54–55` | `_FOV_DEG = 8.86`, `_FOV_MAX_ERROR_DEG = 1.5` | from the profile (§5.3) |
| `python/evf/webserver/server.py:53–55` | `_FOV_H = 8.86`, `_IMG_W = 1280`, `_IMG_H = 720`; used by `_compute_origin()` and the navigation projection, and sent to the UI as `fov_h_deg` / `image_w` / `image_h` | from the engine (§5.6) |
| `python/evf/solver/solver.py` `_CENTROID_PARAMS` | tuned at 24.9"/px | verify at 48 / 97"/px (task 6.3) |
| `python/evf/solver/thread.py:131–133` | calibration: `_CALIBRATE_MIN_SEP = 0.5°`, `_CALIBRATE_STABLE_TOL = 0.05°` | in degrees, so they stay valid. 0.05° is ~3.7 px at 1x1 and ~1.9 px at 2x2 on the IDS setup, still above solve jitter. Verify with the camera (task 6.2); no change planned |
| `web/src/components/live-view/NavOverlay.tsx:8` | `LOCKED_THRESHOLD_DEG = 0.15` | in degrees, OK (~11 px at 48"/px); no change |
| `web/src/components/live-view/NavOverlay.tsx`, `SyncCandidatesOverlay.tsx`, `StarOverlay.tsx` | marker radii, stroke widths and font sizes in **image pixels** (viewBox = `image_w x image_h`), sized for 1280 px wide | scale by `image_w / 1280` (one helper), so markers keep their on-screen size at 1936 (else ~34 % smaller) and 968 (else ~32 % bigger) |
| `web/src/components/live-view/NavOverlay.tsx:149`, `TrackingStep.tsx:36`, `AxesOverlay.tsx:25`, `LiveView.tsx` | use `state.fov_h_deg`, `image_w`, `image_h` | already dynamic; no change |
| `web/src/components/live-view/SkyDome/*` (`SkyDomeCanvas.tsx` `SCOPE_LENGTH = RADIUS * 0.3`, `SCOPE_RADIUS = 0.1`; `HemisphereGrid`, `Marker`) | telescope cylinder and markers are fixed decorative sizes on a unit dome; nothing is derived from the camera FOV (there is no FOV cone) | no change. A FOV footprint on the dome would be a separate feature |
| `web/src/components/wizard/Wizard.test.tsx` fixtures | 1280x720 / 8.86 | tests only; add one 1936x1216 / 25.6° case |
| `specs/start/SPEC_ARCHITECTURE.md` §8.1, §8.4 | "Camera Optical Geometry" 8.86° / 24.9"/px | document per-profile FOV and both databases when this lands (task 7.1) |

### 5.6 Web server geometry

`webserver/server.py`: replace the module constants with values from the
engine:

- `image_w`, `image_h` from the current `StreamGeometry` (fallback
  1280x720 when no camera is connected, as today).
- `fov_h_deg` from §5.2, plus `fov_source`, `solver_db` (active database name) and `fov_out_of_range` for the UI.
- `_compute_origin()` and the navigation projection take
  `(fov_h, img_w, img_h)` as arguments.

The UI needs no geometry change: it already scales overlays from these
state fields.

---

## 6. Web API and UI

### 6.1 API

- `POST /api/control` accepts `value` as a JSON number (int or float).
  The engine validates against the current descriptor: unknown id ->
  HTTP 400; `enum` value not in `options` -> HTTP 400; numbers are passed
  through (the camera clamps). Response unchanged.
- `POST /api/camera/fov` sets the connected camera's profile FOV (§6.3):
  `{"focal_length_mm": 25}` (converted with the sensor size, §5.2 step 2;
  HTTP 400 when the sensor size is unknown), `{"fov_h_deg": 25.6}`
  (direct), or `{"reset": true}` (clears a user value; learning or search
  takes over). Sets `fov_source = "user"` and may swap the database
  (§5.4.3). HTTP 409 when no camera is connected.
- `/ws` state additions:
  - `controls`: the v2 descriptors as received (with the engine's
    `max_exposure_us` cap applied); v1 descriptors unchanged.
  - `camera.stream`: `{width, height, binning, state, frame_interval_us}`
    or `null`.
  - `camera.protocol_version`, `camera.camera_key`, `camera.backend`.
  - `fov_h_deg`, `fov_source`, `solver_db`, `fov_out_of_range` (§5.6).

### 6.2 `CameraControls.tsx`

Render each advertised control by `type`:

| type | widget |
|---|---|
| `int` / `float` | slider (`min`/`max`/`step`); if `scale == "log"`, the slider runs on `log10(value)` and snaps to `step` on commit |
| `enum` | segmented buttons (2–4 options) or a select (more) |

Value labels by `unit`:
- `"us"`: `< 1000` → `"850 µs"`, `< 1 s` → `"12.5 ms"` / `"250 ms"`, else `"1.50 s"`.
- `"fps"`: `"4.00 fps"`; the label also shows the frame interval.
- `"100us"` and `"log2s"`: unchanged (v1 servers).
- anything else: the raw value plus the unit.

Behaviour:
- Commit on release (as today). After commit, the slider follows the
  next CONTROL_INFO (ranges may move: changing fps moves the exposure max).
- While `camera.stream.state == "reconfiguring"`, disable the controls and
  show "Reconfiguring camera…".
- Order: exposure, fps, gain, binning, then others by label.
- No change to `App.tsx` placement; the same component serves the desktop
  window and the mobile view.

`types.ts` `ControlDescriptor` gains `type`, `options`, `scale`,
`restarts_stream`, `default`.

### 6.3 Settings panel

Under Camera, add:
- the connected camera (`camera_model`, backend);
- the FOV with its source (user / learned / searching / default) and the
  active solver database (narrow / wide);
- an optional **focal length (mm)** field that sets `fov_h_deg`
  (`fov_source = "user"`). For the IDS reference setup the user enters
  25 mm and gets 25.6° with the wide database, with no FOV search;
- a "Reset to learned" action that clears a user value;
- a hint when `fov_out_of_range` is set ("No star database covers this
  field of view").

---

## 7. Out of Scope

- Engine use of FRAME_RAW / GRAY8 (needs JPEG encoding in the engine for
  `/frame.mjpg`; do after the JPEG-vs-raw solve-rate measurement,
  `SPEC_CAMERA_SERVER_WINDOWS.md` §11 Q4).
- v2 in the macOS Swift, Linux C and Windows C servers. They stay v1 and
  unmodified; their controls keep working through §3.1 / §1.2.
- ROI/cropping, gain boost, pixel clock in the UI.
- Databases beyond the two in §5.4 (e.g. very wide all-sky lenses, or
  long focal lengths below 7°).
- A FOV footprint on the Sky View dome.

---

## 8. Testing

- pytest, client: negotiation against the mock in v1 mode and v2 mode;
  STREAM_INFO handling clears the frame buffer on a new generation; float
  SET_CONTROL. Existing `tests/test_camera.py` v1 cases must stay green.
- pytest, mock: extend `tests/mock_camera_server.py` with a `v2=True`
  mode (IDS-like controls, coupling, binning that serves downscaled
  sample frames and STREAM_INFO).
- pytest, conformance: `tests/test_camera_conformance.py` v2 vectors
  (`SPEC_PROTOCOL_CAMERA.md` §11.8) against the mock (shared with #1).
- pytest, config: profile creation, unit-mismatch drop, first-run
  defaults (linear midpoint, log geometric mean, enum first option),
  legacy migration, `CONFIG_VERSION` unchanged and old configs load.
- pytest, subprocess manager: stall timeout from frame interval;
  paused while reconfiguring; `restore_controls` order.
- pytest, solver: `solve_frame(..., fov_estimate_deg=8.86, fov_max_error_deg=1.5)`
  matches today on `tests/samples`; downscaled 2x samples still solve;
  FOV learning median with the range guard and the 10 % step limit;
  database registry selection (inside, overlap, out of range); FOV
  search picks the database that solves.
- pytest, wide database: no wide-field sky sample exists in
  `tests/samples` yet. Add one IDS frame (or a synthetic frame rendered
  from the Gaia catalog at 25.6° / 1936x1216) once available (task 6.3),
  then assert it solves with the wide database. Whether the narrow database
  also solves it is not asserted: tetra3rs matches sub-patterns, so the narrow
  database can solve a wide field too. The comparison is a Q7 measurement on
  real frames.
- pytest, paths: `solver_db_dir()` in dev, release and headless layouts.
- pytest, web server: `image_w/h` and `fov_h_deg` follow the engine;
  `/api/control` float and enum validation (`tests/test_webserver_api.py`,
  `test_webserver_payload.py`).
- vitest: `CameraControls` renders int/float/enum, log slider mapping,
  unit labels (incl. v1 units); overlay marker scaling at 968/1280/1936 px.
- Hardware: see tasks marked **[HW]**.

---

## 9. Open Questions

Resolved: the IDS camera uses a **25 mm Basler lens** (answered by
Sebastian on #3). This is the reference configuration in §5.2.1 and the
reason for the wide database in §5.4. Q2–Q5 are decided (below). Q6–Q9
are measurements on hardware, scheduled in the slices (§10.8). Q10–Q12
(added 2026-11-18 with the slice plan) need Sebastian; each has a
proposed answer that the slices assume until he says otherwise.

| # | Question | Needs | Blocks approval? |
|---|---|---|---|
| Q2 | ~~Exposure cap for the UI: 4 s proposed (sensor allows 30 s).~~ **Resolved 2026-11-18: 4 s, as proposed.** | Sebastian | — |
| Q3 | ~~Gain shown as `raw` 0–100 for IDS, OK? (protocol §11.9)~~ **Resolved 2026-11-18: yes, a plain 0–100 number (effectively %).** | Sebastian | — |
| Q4 | ~~Calibration per camera: proposed "ignore calibration from another camera" (§4.2) instead of storing calibration per profile. OK?~~ **Resolved 2026-11-18: as proposed.** | Sebastian | — |
| Q5 | ~~Should a binning change be blocked while TRACKING, or allowed (pointing survives, overlays rescale)? Proposed: allowed.~~ **Resolved 2026-11-18: allowed, as proposed.** | Sebastian | — |
| Q6 | **[HW]** Centroid parameters at ~48"/px (1x1) and ~97"/px (2x2) on IMX174 frames: do `sigma_threshold`, `max_pixels`, `matched_filter_sigma` need re-tuning (possibly per database)? Measurement, not a decision: night test after slice S1 (1x1) and S5 (2x2). | Sebastian + camera + sky | No |
| Q7 | **[HW]** Wide database: solve rate, solve time and false-positive probability with the proposed 18°–32° / mag 7.0 parameters at 1x1 and 2x2; tune FOV range and magnitude from the results. Measurement: same night tests as Q6. | Sebastian + camera + sky | No |
| Q8 | **[HW]** Does tetra3rs's `max_fov_deg` refer to the horizontal FOV (as `fov_estimate` does) or the pattern diameter? The IDS setup has a ~30° diagonal at the top of the proposed range. If solves fail near the corners, raise `max_fov_deg`. Measurement: S1 night test. | camera + sky | No |
| Q9 | ~~Memory with one database loaded vs two (FOV search) on the Pi 3A+ (512 MB).~~ **Resolved 2026-11-18: no Pi available (Q11).** Measured on the laptop only (task 4.11); the one-database-at-a-time rule stays. | — | — |
| Q10 | **Resolved 2026-11-18: yes, the FOV is entered by hand in `config.json` for the night test between S1 and S2.** Interim FOV entry in slice S1: S1 lets the engine use a per-camera FOV and the wide database, but the Settings field and `/api/camera/fov` come in S2. Is setting `camera.profiles["ids:1409:3060"].fov_h_deg = 25.6` by hand in `config.json` (with `fov_source: "user"`) acceptable for a night test between S1 and S2? **Proposed: yes**; otherwise S1 grows by the API + one Settings field. | Sebastian | No (affects S1 size only) |
| Q11 | ~~Is an OV9281 module (and a Pi 3A+) available for the OV9281 and Pi checks?~~ **Resolved 2026-11-18: no. The IDS camera is the only one.** The OV9281 hardware checks are dropped (tasks 1.7, 6.5; Windows Q10, 6.2a, task 8). The v1 path is covered by pytest against the mock in v1 mode and by the upstream servers staying unmodified. Memory is measured on the laptop only. See also the scope note in §1.2. | — | — |
| Q12 | Long-exposure mode in **v2** sessions (`SPEC_CAMERA_SERVER_WINDOWS.md` §5.6): the IDS backend never leaves it today because switching off gaps the stream ~2.5 s, more than the v1 stall timeout; in long mode `fps` is fixed. **Proposed:** in v2 sessions, leave long mode when the exposure drops back into the normal range, wrapped in STREAM_INFO `reconfiguring` → `streaming` (the engine pauses stall detection for up to 5 s, §3.3), so `fps` becomes settable again. v1 sessions keep the sticky behaviour. | Sebastian | No (S5 only) |

---

## 10. Implementation Tasks

Dependency: tasks 1–5 can be built and tested against the Python mock
(v2 mode). The C# server from #1 is merged and speaks v1 with the IDS
backend, so the FOV and database work (task 4) can be tested on the IDS
camera before any v2 code exists. Tasks marked **[HW]** need Sebastian +
the IDS camera (25 mm lens); those that need all four controls also need
`SPEC_CAMERA_SERVER_WINDOWS.md` task 7 (v2 in the C# server). The order
of work is the slice plan in §10.8.

### 1. Protocol plumbing (Python)
- [ ] 1.1 `protocol.py`: new message ids and names. Test: pytest `type_name`.
- [ ] 1.2 `client.py`: v1/v2 negotiation, `session_version`, `camera_key`, `capabilities`. Test: pytest against mock v1 and v2; existing tests green.
- [ ] 1.3 `client.py`: STREAM_INFO -> `StreamGeometry`, callback, frame buffer clear on new generation; FRAME_RAW parsed and ignored; float `set_control`. Test: pytest.
- [ ] 1.4 `mock_camera_server.py` v2 mode (IDS-like controls, coupling, binning, STREAM_INFO). Test: pytest.
- [ ] 1.5 `tests/test_camera_conformance.py` v2 vectors (§11.8 1–5; 6 only where GRAY8 is advertised), added to the existing v1 suite from #1 task 3.2. Test: green against the mock; the C# exe joins in S5.
- [ ] 1.6 `data/VERSION.json` `protocol_version: 2` + update `tests/test_camera.py`/`test_subprocess_mgr.py` expectations. Test: pytest.
- [ ] 1.7 v1 regression: the v2 engine against the mock in v1 mode behaves exactly like today (two controls, native units, 2.0 s stall). Test: pytest. No hardware test with an upstream server (Q11).

### 2. Liveness and restore
- [ ] 2.1 Per-session stall timeout, paused while reconfiguring, reset on any message. Test: pytest with a mock that sends 4 s frames.
- [ ] 2.2 `restore_controls()` used by engine startup and recovery. Test: pytest order and unit filtering.

### 3. Config profiles
- [ ] 3.1 `DEFAULT_CONFIG` additions (incl. `camera.last_camera_key`, `camera.max_exposure_us`, `camera.profiles`), profile helpers in `evf/config/profiles.py`, profile key from HELLO `camera_key` else `"<backend>:<camera_model>"` (§4.2), `CONFIG_VERSION` unchanged. Test: pytest load of an old config file keeps all values; key from a HELLO with and without `camera_key`.
- [ ] 3.2 Saving every successful `set_control` into the profile with its unit, first-run defaults and legacy migration (§4.2). Test: pytest.
- [ ] 3.3 `calibration.camera_key` and `has_calibration` check. Test: pytest.

### 4. Geometry, FOV and solver databases
- [ ] 4.1 `PlateSolver.solve_frame(fov_estimate_deg, fov_max_error_deg)`; remove `_FOV_DEG`/`_FOV_MAX_ERROR_DEG`; `SolverParams` snapshot from the active profile (§5.3). Test: pytest on `tests/samples`, unchanged results at 8.86° / 1.5°. *(S1a: the signature part is done. `solve_frame(image, fov_estimate_deg, fov_max_error_deg)` takes both as required arguments, the module constants are gone, and `solver/fov.py` has the frozen `SolverParams` plus `max_error_for_fov()` (`max(1.5, 0.10 * fov)`). The engine's sync solve and the solver thread pass `LEGACY_SOLVER_PARAMS` (8.86° / 1.5°); `tests/test_solver_offline.py` passes the same values and its results are unchanged. Still open for S1b: the engine-owned, lock-protected `SolverParams` holder fed from the active profile.)*
- [ ] 4.2 SolverThread drops mismatched-size results without counting failures. Test: pytest.
- [ ] 4.3 FOV learning (median of 5 `fov_deg`), never overwrite `"user"`, keep inside the active database's range minus 5 %, max 10 % per step. Test: pytest with fake results incl. outliers.
- [x] 4.4 Parameterise `scripts/build_tetra3rs_catalog.py` (`--db narrow|wide|all`, `--force`, `data/solver_dbs.json`); build and commit `data/tetra3rs_gaia_wide.bin` (18°–32°, mag 7.0); `VERSION.json` `solver_db_version_wide`. Test: script rebuilds the narrow DB byte-identical (or same counts) and writes the JSON; `bench_solvers.py --db wide` runs. *(Done: the build is not byte-reproducible: three narrow builds on tetra3rs 0.7.1 give the same size (52 769 165 B) and counts (62 793 stars, 1 914 984 patterns) but different bytes, and two wide builds differ too. The committed narrow file is therefore left untouched; the rebuilt one solves the five samples identically (same matches, RA, Dec, probability). The wide file is 9 709 902 B (21 399 stars, 348 742 patterns, exactly the §5.4.1 numbers), built in 1.2 s (narrow 5.2 s). Extras: `--out-dir`; an existing file is skipped with exit 0 instead of exit 1, and its registry entry is still written; `bench_solvers.py` also got `--fov` / `--fov-error` and a UTF-8 stdout fix for Windows. `bench_solvers.py --db wide` runs but reports FAIL for all five samples, as expected: they are 8.86° frames.)*
- [x] 4.5 `paths.solver_db_dir()` and the database registry/selection (§5.4.3), one database loaded at a time, swap on the solver thread. Test: pytest (dev/release/headless paths; selection cases; swap without dropping the solver thread). *(Done in S1a as library code: `solver/databases.py` (`load_registry`, `select_database`, `SolverDatabases.request_for_fov()` / `apply_pending()`), `PlateSolver.apply_pending_database()` called by the solver thread between solves, `tests/test_solver_databases.py` (41 tests, 53 with parametrisation; includes a real-thread swap narrow -> wide -> narrow). Without `solver_dbs.json` the narrow file is used. The engine does not request a swap yet and always keeps the narrow database; `camera.last_camera_key`, the profile FOV and the `fov_out_of_range` state flag come with S1b. `test_paths_headless.py` got the `solver_db_dir()` case but is Linux-only, so it is skipped on Windows; `solver_db_dir()` for dev, macOS bundle and Linux/Windows release is also tested by patching `paths` in `test_solver_databases.py`.)*
- [ ] 4.6 FOV search for profiles without a FOV (§5.4.4) + UI state. Test: pytest with a fake solver that only solves on one database.
- [x] 4.7 Ship the wide DB: `build_mac.sh`, `build_linux.sh`, `build_windows.bat`, `build_headless_deb.sh` copy it; `scripts/smoke_test_headless_deb.sh` checks it exists. Test: local Windows build + headless smoke test show both files. *(Done: all four scripts also copy `solver_dbs.json`; the smoke test checks `tetra3rs_gaia.bin`, `tetra3rs_gaia_wide.bin` and `solver_dbs.json` under `/usr/lib/pushnav-headless/data/`. Verified: a full local `build_windows.bat` run (roughly 20 min, exit 0) has all three files in `build\PushNav-windows\data\` and the zip (97.7 MB), and the built `PushNav.exe --no-window` logs the narrow database loaded from the release `data\` via the registry. The mac, Linux and headless scripts only got `bash -n` syntax checks and the copy lines were read, not run; the .deb smoke test was not run: it needs the arm64 container build under QEMU, and the Windows working tree has CRLF scripts that would not run in the container. The changed smoke-test lines were exercised against a fake tree: fail when the wide file is missing, pass when present. Inno Setup is not installed here, so the installer step was skipped; `pushnav.iss` takes the whole `PushNav-windows\*` directory.)*
- [x] 4.8 **[INFRA]** `build.yml` "Verify required files" (4 jobs) lists `data/tetra3rs_gaia_wide.bin`. Test: `workflow_dispatch` build green. *(Done in YAML; workflow_dispatch run pending)*
- [ ] 4.9 Web server: dynamic `image_w/h` (v1: HELLO size corrected by the first decoded frame, §5.1), `fov_h_deg`, `fov_source`, `solver_db`, `fov_out_of_range`; origin/projection parameters. Test: pytest payload tests at 1280x720 / 8.86°, 1936x1216 / 25.6° and 968x608 / 25.6°.
- [ ] 4.10 Overlay marker scaling by `image_w / 1280` (NavOverlay, SyncCandidatesOverlay, StarOverlay) and a 1936x1216 / 25.6° Wizard test fixture. Test: vitest.
- [ ] 4.11 Measure RSS with the narrow DB, the wide DB, and both, on the dev laptop (no Pi, Q9/Q11). Test: numbers in the PR.

### 5. API and UI
- [ ] 5.1 `/api/control` number + validation; `/ws` additions. Test: pytest API tests.
- [ ] 5.2 `types.ts` + `CameraControls.tsx` int/float/enum, log slider, unit labels, reconfiguring state. Test: vitest.
- [ ] 5.3 Settings: camera info, FOV + source + active database, focal-length field, out-of-range hint. Test: vitest + manual against the mock.
- [ ] 5.4 Manual UI check on desktop window and phone (LAN) against the mock in v2 mode. Test: screenshots in the PR.
- [ ] 5.5 `POST /api/camera/fov` (focal length, direct FOV, reset; §6.1). Test: pytest API tests (unknown sensor size -> 400, no camera -> 409, database swap on a range change).

### 6. Hardware verification (IDS UI-306xCP-M + 25 mm Basler lens)
- [ ] 6.1 **[HW]** IDS v2 session: all four controls appear with real ranges; exposure > frame interval lowers fps; CONTROL_INFO ranges update in the UI.
- [ ] 6.2 **[HW]** Binning 1x1 <-> 2x2 while TRACKING: image switches to 968x608 without reconnect, overlays line up, pointing continues; calibration (`_CALIBRATE_STABLE_TOL` 0.05°) still completes at 2x2.
- [ ] 6.3 **[HW]** Night test with the wide DB: solve rate, solve time and probability at 1x1 (~48"/px) and 2x2 (~97"/px) across several exposures and sky regions (incl. one near a galactic pole); check centroid params (Q6); tune DB FOV range and magnitude (Q7, Q8). Save a few frames as new `tests/samples` wide-field fixtures.
- [ ] 6.4 **[HW]** Fresh profile without a focal length: FOV search finds ~25.6° and switches to the wide DB; then with 25 mm entered: no search.
- [ ] 6.5 **[HW]** Restart the app: IDS settings and FOV restored from its profile, wide DB loaded at startup. (A second camera on a v1 server is not tested on hardware, Q11; pytest covers separate profiles and the unit filter.)
- [ ] 6.6 **[HW]** 4 s exposure: no stall-restart loop.

### 7. Docs
- [ ] 7.1 FOV and databases: `SPEC_ARCHITECTURE.md` §8 (per-profile FOV replacing the fixed §8.1 optics, both databases), §13 config keys (`profiles`, `last_camera_key`, `max_exposure_us`), `SPEC_BUILD_RELEASE.md` (both DB files), `CLAUDE.md` catalog section, `docs/` user note on lens/FOV. Test: review.
- [ ] 7.2 Camera controls: `SPEC_ARCHITECTURE.md` §5/§7 (stream geometry, stall timeout), `docs/` user note on camera controls, `SPEC_PROTOCOL_CAMERA.md` §11 status "implemented". Test: review.

### 8. Slices (order of work)

Six PRs, merged in this order. Each one leaves `uv run pytest tests/`,
vitest and (where touched) the C# xUnit tests green and the app working
with every camera server. `W7.x` / `W6.x` are tasks in
`SPEC_CAMERA_SERVER_WINDOWS.md` §12. Minimal-diff rules (§1.2) apply to
every slice: new logic in the new modules, `# fork: camera-controls`
hooks in upstream files, `camera/windows/` untouched (check with
`git diff upstream/main -- camera/windows/` before each PR).

The wide-field database comes first: it is what stops the IDS camera with
the 25 mm lens from solving, and it works over today's v1 session.

| Slice | Tasks | Needs | Size |
|---|---|---|---|
| S1 Wide-field solving | 3.1, 4.1, 4.4, 4.5, 4.7, 4.9, 7.1, W7.0; **[INFRA]** 4.8 | — | L |
| S2 FOV in the UI, learning, search | 3.3, 4.3, 4.6, 4.10, 4.11, 5.3, 5.5 | S1 | M |
| S3 Protocol v2 in the engine (mock) | 1.1–1.7, 2.1, 2.2, 3.2, 4.2 | S1 (config profiles) | M |
| S4 Controls API and UI | 5.1, 5.2, 5.4 | S3 | M |
| S5 C# server v2 session | W7.1; **[INFRA]** W6.5 | S4 | M |
| S6 IDS binning | W7.2, 7.2 | S5 | M |

**S1 Wide-field solving (v1 sessions).**
- Build and commit `data/tetra3rs_gaia_wide.bin` and `data/solver_dbs.json`; parameterised build script; `VERSION.json` `solver_db_version_wide`; both files shipped by all four build scripts and checked by the headless smoke test.
- Database registry with one database loaded at a time (`solver/databases.py`, `paths.solver_db_dir()`); `solve_frame(fov, err)` with `SolverParams`.
- Minimal profiles (`config/profiles.py`): the key rule, `fov_h_deg`, `fov_source`, `last_camera_key`. FOV resolution implements §5.2 steps 1 and 4 only: the profile value, else 8.86° with the narrow database (today's behaviour).
- Web server geometry from the engine, so the IDS frame size (1936x1216) and FOV reach the projection and the UI.
- C# HELLO gains `camera_key` and `capabilities.sensor` (W7.0).
- Files: `scripts/build_tetra3rs_catalog.py`, `scripts/bench_solvers.py`, `data/*`, `python/evf/{paths.py, solver/solver.py, solver/thread.py, solver/databases.py, config/manager.py, config/profiles.py, engine/engine.py, webserver/server.py}`, `scripts/build_{mac,linux,headless_deb}.sh`, `scripts/build_windows.bat`, `scripts/smoke_test_headless_deb.sh`, `camera/windows-dotnet/src/.../Protocol/Messages.cs`, `Server/ClientSession.cs`, `CLAUDE.md`, `specs/start/SPEC_ARCHITECTURE.md`, `SPEC_BUILD_RELEASE.md`.
- Infra: `build.yml` verify lists (4.8). W6.6 (setup-dotnet in `build-windows`) edits the same job and can go in the same infra pass.
- Test: `tests/test_solver_offline.py` unchanged at 8.86° / 1.5°; new `test_solver_databases.py` (selection inside / overlap / out of range, swap on the solver thread), `test_profiles.py` (old config loads unchanged, key with and without `camera_key`), `test_paths_headless.py` (`solver_db_dir()`), payload tests at 1280x720, 1936x1216 and 968x608; xUnit on the HELLO JSON; conformance v1 suite green against the mock and the exe; local Windows build contains both database files.
- **[HW]** Night test with the IDS camera at 1x1: W5.9 and task 6.3 at 1x1 (Q6–Q8), with the FOV entered by hand in `config.json` (Q10). Exposure is limited to the v1 cap of 1.5 s, which is enough for a 25 mm lens. Save a few frames as wide-field `tests/samples` fixtures.
- Possible split if the PR gets too big: S1a = database build, shipping and registry (narrow stays selected, no behaviour change); S1b = profiles, FOV and geometry.
- **S1a status (branch `feat/s1a-wide-solver-db`).** Done: 4.4, 4.5, 4.7 and the `solve_frame` signature part of 4.1. The engine always selects the narrow database and passes 8.86° / 1.5°, so nothing changes at runtime. Left for S1b: 3.1 (profiles, `last_camera_key`, `fov_h_deg`), the engine-owned `SolverParams` from the profile (rest of 4.1), the engine calling `request_for_fov()` and the `fov_out_of_range` flag (rest of 4.5), 4.9, 7.1 and W7.0. Infra: 4.8 done in `build.yml`; the `workflow_dispatch` run is pending. Questions still open after S1a: Q6, Q7 and Q8 (night tests with the camera; nothing in S1a answers them), Q12 (S5 only). Q10 is resolved but only matters from S1b on. Findings for the night test: (1) a synthetic 25.6° / 1936x1216 frame, built from the catalog with exact star positions, solves with the wide database at 4 of 4 pointings tried (error < 0.05°, `fov_deg` 25.6; three of them are in `test_solver_databases.py`), so the committed wide file works; (2) the narrow database also solved 3 of those 4 frames (it failed at Dec -70°), because tetra3rs matches 4-star patterns smaller than the field. The §8 wording "solves with the wide database and not with the narrow one" cannot be asserted as written, and Q7's solve-rate comparison needs real frames; (3) database builds are not byte-reproducible (task 4.4 note).

**S2 FOV in the UI, learning and search.**
- FOV learning and FOV search (`solver/fov.py`), `POST /api/camera/fov`, Settings fields (camera, FOV + source + database, focal length, reset, out-of-range hint), calibration tied to `calibration.camera_key`, overlay marker scaling, RSS numbers.
- Files: `python/evf/{solver/fov.py, solver/thread.py, engine/engine.py, webserver/server.py, config/profiles.py}`, `web/src/lib/types.ts`, `web/src/components/settings/Settings.tsx`, `web/src/components/live-view/{NavOverlay,SyncCandidatesOverlay,StarOverlay}.tsx`, `web/src/components/wizard/Wizard.test.tsx`.
- Test: pytest for learning (outliers, range guard, 10 % step), search (fake solver that solves on one database only), the FOV API, `has_calibration` across camera keys; the wide-field fixture from S1 solves with the wide database (the comparison with the narrow database is a Q7 measurement on real frames, not a test assertion); vitest for the Settings fields, marker scaling and the 1936x1216 / 25.6° Wizard fixture.
- **[HW]** Task 6.4 (fresh profile finds ~25.6° by search; with 25 mm entered no search). Task 4.11 on the laptop (no Pi, Q11).

**S3 Protocol v2 in the engine, against the mock.**
- Negotiation, STREAM_INFO / FRAME_RAW parsing, float `set_control`, liveness, one restore path, per-profile control values with units, dropping results of the wrong size, mock v2 mode, conformance v2 vectors, `VERSION.json` `protocol_version: 2`.
- No real server offers v2 yet, so every real session stays v1 and behaves exactly as before.
- Files: `python/evf/camera/{protocol.py, client.py, stream.py, controls.py, subprocess_mgr.py}`, `python/evf/{engine/engine.py, solver/thread.py, config/profiles.py}`, `tests/mock_camera_server.py`, `tests/test_camera_v2.py`, `tests/test_camera_conformance.py`, expectation updates in `tests/test_camera.py` / `test_subprocess_mgr.py`, `data/VERSION.json`.
- Test: pytest (v1 and v2 mock modes, 4 s frames without a restart, restore order and unit filter, first-run defaults, legacy migration).
- No hardware test: the v1 regression (1.7) is pytest-only (Q11).

**S4 Controls API and UI.**
- `/api/control` with numbers and validation, `/ws` control and stream fields, `CameraControls.tsx` for int/float/enum, log sliders, unit labels, and the reconfiguring state.
- Must merge before S5: until then the UI never sees v2 descriptors from a real camera.
- Files: `python/evf/{webserver/server.py, engine/engine.py, camera/controls.py}`, `web/src/lib/types.ts`, `web/src/components/controls/CameraControls.tsx`.
- Test: pytest API tests; vitest (widgets, log mapping, all units incl. v1 `"100us"` / `"log2s"`); task 5.4 screenshots against the mock in v2 mode. No hardware.

**S5 C# server v2 session.**
- W7.1: handshake, native descriptors without the 1.5 s cap, STREAM_INFO, binning on the Fake backend.
- After this merge the IDS camera runs v2 with exposure (up to 4 s), fps and gain; binning is not offered yet.
- Infra: W6.5 adds the `camera-server-windows` CI job, so the v2 conformance run against the exe gates this PR. Route it to infra first, or with this slice.
- Files: `camera/windows-dotnet/**` (Protocol, Server, Controls, Fake backend, tests), `.github/workflows/test.yml` (infra).
- Test: xUnit; conformance v1 + v2 vectors against the mock and the exe (`--backend fake`).
- **[HW]** Tasks 6.1, 6.5 and 6.6, plus the conformance suite with `PUSHNAV_CONFORMANCE_BACKEND=ids`.

**S6 IDS binning.**
- W7.2: `is_SetBinning` with the in-process restart, STREAM_INFO, and long-exposure handling in v2 (Q12).
- Docs task 7.2.
- Files: `camera/windows-dotnet/src/.../Backends/Ids/*`, the IDS xUnit tests, `specs/start/SPEC_ARCHITECTURE.md`, `docs/`.
- Test: xUnit with `FakeUEyeApi`.
- **[HW]** Tasks 6.2 and 6.3 at 2x2 (Q6, Q7 at ~97"/px), and W7.2 binning 968x608 in the app. Closes #3 together with the docs.

Hardware summary: S1 (night, 1x1), S2 (FOV search), S5 (controls, 4 s), S6 (binning, night at 2x2) need Sebastian and the IDS camera; S3 and S4 need none.
