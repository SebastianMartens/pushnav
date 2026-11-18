# SPEC_CAMERA_CONTROLS.md — PushNav

Version: 1.0
Status: Draft — awaiting approval, not yet implemented
Date: 2026-11-18
Target: all platforms (engine, config, web UI); camera side on the Windows C# server first
Issue: SebastianMartens/pushnav#3 (camera controls: exposure time, fps, gain, binning)
Depends on: `specs/start/SPEC_PROTOCOL_CAMERA.md` §11 (protocol v2) and,
for a real camera with all four controls, `SPEC_CAMERA_SERVER_WINDOWS.md`
(#1, IDS backend)
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
| **Camera key** | Stable id of a camera model from HELLO (`camera_key`, e.g. `ids:1409:3060`). For v1 servers the engine builds `"<backend>:<camera_model>"`. |
| **Camera profile** | The per-camera-key block in config: saved control values and the FOV. |
| **Stream geometry** | Width, height, binning and generation of the frames currently arriving (from STREAM_INFO, or from HELLO / the decoded frame in v1). |
| **Generation** | Counter from STREAM_INFO that increases on every stream reconfiguration. |
| **FOV** | Horizontal field of view in degrees of the whole sensor width with the user's lens. Independent of binning. |

---

## 2. Current Behaviour (baseline)

- `CameraClient` accepts only `protocol_version == 1`; controls are
  whatever CONTROL_INFO lists; SET_CONTROL values are ints
  (`python/evf/camera/client.py`).
- Engine restores only `exposure` and `gain` from the **global**
  `camera.exposure` / `camera.gain` keys, seeding the range midpoint on
  first run (`engine.py` `startup_camera()`); `SubprocessManager` repeats
  the same restore after recovery.
- Units differ per server: `"100us"` (Linux/macOS), `"log2s"` (Windows C).
  A saved value from one server is meaningless on another.
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
  "windows_server": "auto",
  "max_exposure_us": 4000000,
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

- `windows_server` is defined in `SPEC_CAMERA_SERVER_WINDOWS.md` §8.1.
- `max_exposure_us`: UI/engine cap for the exposure slider (default 4 s,
  §9 Q2). The engine clamps the advertised `max` to it before sending
  controls to the UI.
- `fov_source`: `"user"` | `"learned"` | `null` (§5.2).

### 4.2 Rules

- Profile key = `client.camera_key` (v2) or `"<backend>:<camera_model>"`
  (v1). Profiles are created on first connect.
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
  sync origin pixel is recomputed from the current width (§5.3).
- v1 sessions: geometry = HELLO `default_width/height`, corrected by the
  size of the first decoded frame (logged if they differ).

### 5.2 FOV for the solver

Binning changes pixels, not the sky area, so the solver's FOV estimate is
a property of **camera + lens**, stored per profile:

1. `profiles[key].fov_h_deg` if set (`fov_source` `"user"` or `"learned"`).
2. Else from lens data, if the user entered a focal length in Settings:
   `fov = 2 * atan(sensor_width_mm / (2 * f_mm))` with
   `sensor_width_mm = capabilities.sensor.width * pixel_size_um / 1000`.
   (IMX174: 11.34 mm wide.)
3. Else the legacy default **8.86°** (the OV9281 + stock lens setup).

Learning: after 5 consecutive valid solves, take the median of the
solver's `fov_deg` (tetra3rs `SolveResult.fov_deg`, solved horizontal FOV)
and store it as `fov_source = "learned"` if it differs from the current
value by more than 2 %. A `"user"` value is never overwritten.

`PlateSolver.solve_frame(image_bytes, fov_estimate_deg=...)` takes the FOV
as a parameter (default 8.86 keeps today's behaviour). `_FOV_MAX_ERROR_DEG`
stays 1.5°.

**Database limit**: the shipped database solves only 7°–10° FOV. With the
IMX174's 11.34 mm width that means a focal length of about **65–93 mm**
(8.86° ≈ 73 mm). Outside that range solving fails no matter what the
engine does; a wider/narrower lens needs a rebuilt database (separate
change). The engine logs a WARNING and the Settings panel shows a hint
when the configured/learned FOV is outside `[7, 10]`. See §9 Q1.

Centroid parameters (`_CENTROID_PARAMS`) were tuned on 1280x720 frames.
They are not changed here; verify on IMX174 frames at 1x1 and 2x2 (task 6.3).

### 5.3 Web server geometry

`webserver/server.py`: replace the module constants with values from the
engine:

- `image_w`, `image_h` from the current `StreamGeometry` (fallback
  1280x720 when no camera is connected, as today).
- `fov_h_deg` from §5.2.
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
- `/ws` state additions:
  - `controls`: the v2 descriptors as received (with the engine's
    `max_exposure_us` cap applied); v1 descriptors unchanged.
  - `camera.stream`: `{width, height, binning, state, frame_interval_us}`
    or `null`.
  - `camera.protocol_version`, `camera.camera_key`, `camera.backend`.
  - `fov_h_deg`, `fov_source`.

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

Add, under Camera: the connected camera (`camera_model`, backend), the
FOV with its source, and an optional **focal length (mm)** field that sets
`fov_h_deg` (`fov_source = "user"`). A "Reset to learned" action clears a
user value. (Needed for the IDS camera, §9 Q1.)

---

## 7. Out of Scope

- Engine use of FRAME_RAW / GRAY8 (needs JPEG encoding in the engine for
  `/frame.mjpg`; do after the JPEG-vs-raw solve-rate measurement,
  `SPEC_CAMERA_SERVER_WINDOWS.md` §11 Q4).
- v2 in the macOS Swift and Linux C servers (they stay v1; their
  controls keep working through §3.1).
- ROI/cropping, gain boost, pixel clock in the UI.
- Rebuilding the tetra3rs database for other FOVs.

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
- pytest, solver: `solve_frame(..., fov_estimate_deg=8.86)` matches today
  on `tests/samples`; downscaled 2x samples still solve; FOV learning median.
- pytest, web server: `image_w/h` and `fov_h_deg` follow the engine;
  `/api/control` float and enum validation (`tests/test_webserver_api.py`,
  `test_webserver_payload.py`).
- vitest: `CameraControls` renders int/float/enum, log slider mapping,
  unit labels (incl. v1 units).
- Hardware: see tasks marked **[HW]**.

---

## 9. Open Questions

| # | Question | Needs | Blocks approval? |
|---|---|---|---|
| Q1 | Which lens/focal length is on the IDS camera? The database solves only 7°–10° FOV (~65–93 mm on the IMX174). If the lens is outside that, we need a database rebuild (and a decision on its size) before the IDS camera can plate-solve. | Sebastian | **Yes** (for solving with the IDS camera) |
| Q2 | Exposure cap for the UI: 4 s proposed (sensor allows 30 s). | Sebastian | No |
| Q3 | Gain shown as `raw` 0–100 for IDS, OK? (protocol §11.9) | Sebastian | No |
| Q4 | Calibration per camera: proposed "ignore calibration from another camera" (§4.2) instead of storing calibration per profile. OK? | Sebastian | No |
| Q5 | Should a binning change be blocked while TRACKING, or allowed (pointing survives, overlays rescale)? Proposed: allowed. | Sebastian | No |
| Q6 | Centroid parameters on IMX174 frames (1x1 and 2x2): need re-tuning? | camera + sky | No |

Q1 is posted on issue #3, which is labelled `needs-human` until it is answered.

---

## 10. Implementation Tasks

Dependency: tasks 1–5 can be built and tested against the Python mock
(v2 mode) without #1. Tasks marked **[HW]** need Sebastian + the IDS camera
with the C# server from #1 (its task 7). Task 6.2 needs Q1 answered.

### 1. Protocol plumbing (Python)
- [ ] 1.1 `protocol.py`: new message ids and names. Test: pytest `type_name`.
- [ ] 1.2 `client.py`: v1/v2 negotiation, `session_version`, `camera_key`, `capabilities`. Test: pytest against mock v1 and v2; existing tests green.
- [ ] 1.3 `client.py`: STREAM_INFO -> `StreamGeometry`, callback, frame buffer clear on new generation; FRAME_RAW parsed and ignored; float `set_control`. Test: pytest.
- [ ] 1.4 `mock_camera_server.py` v2 mode (IDS-like controls, coupling, binning, STREAM_INFO). Test: pytest.
- [ ] 1.5 `tests/test_camera_conformance.py` v2 vectors (shared with #1 task 3.2). Test: green against the mock.
- [ ] 1.6 `data/VERSION.json` `protocol_version: 2` + update `tests/test_camera.py`/`test_subprocess_mgr.py` expectations. Test: pytest.

### 2. Liveness and restore
- [ ] 2.1 Per-session stall timeout, paused while reconfiguring, reset on any message. Test: pytest with a mock that sends 4 s frames.
- [ ] 2.2 `restore_controls()` used by engine startup and recovery. Test: pytest order and unit filtering.

### 3. Config profiles
- [ ] 3.1 `DEFAULT_CONFIG` additions, profile helpers in `ConfigManager`, `CONFIG_VERSION` unchanged. Test: pytest load of an old config file keeps all values.
- [ ] 3.2 First-run defaults and legacy migration. Test: pytest.
- [ ] 3.3 `calibration.camera_key` and `has_calibration` check. Test: pytest.

### 4. Geometry and FOV
- [ ] 4.1 `PlateSolver.solve_frame(fov_estimate_deg=...)`; engine passes the profile FOV. Test: pytest on `tests/samples` unchanged results.
- [ ] 4.2 SolverThread drops mismatched-size results without counting failures. Test: pytest.
- [ ] 4.3 FOV learning (median of 5 `fov_deg`), never overwrite `"user"`. Test: pytest with fake results.
- [ ] 4.4 Web server: dynamic `image_w/h`, `fov_h_deg`, origin/projection parameters. Test: pytest payload tests at 1280x720 and 968x608.
- [ ] 4.5 FOV outside 7°–10° warning (log + state flag). Test: pytest.

### 5. API and UI
- [ ] 5.1 `/api/control` number + validation; `/ws` additions. Test: pytest API tests.
- [ ] 5.2 `types.ts` + `CameraControls.tsx` int/float/enum, log slider, unit labels, reconfiguring state. Test: vitest.
- [ ] 5.3 Settings: camera info, FOV + source, focal-length field. Test: vitest + manual against the mock.
- [ ] 5.4 Manual UI check on desktop window and phone (LAN) against the mock in v2 mode. Test: screenshots in the PR.

### 6. Hardware verification
- [ ] 6.1 **[HW]** IDS v2 session: all four controls appear with real ranges; exposure > frame interval lowers fps; CONTROL_INFO ranges update in the UI.
- [ ] 6.2 **[HW]** Binning 1x1 <-> 2x2 while TRACKING: image switches to 968x608 without reconnect, overlays line up, pointing continues (needs Q1 lens in range).
- [ ] 6.3 **[HW]** Night test: solve rate at 1x1 and 2x2, several exposures; check centroid params (Q6).
- [ ] 6.4 **[HW]** Restart the app: IDS settings restored from its profile; plug the OV9281 (C server, v1): its own profile, no unit mix-up.
- [ ] 6.5 **[HW]** 4 s exposure: no stall-restart loop.

### 7. Docs
- [ ] 7.1 `SPEC_ARCHITECTURE.md` §5/§7/§8/§13 notes (stream geometry, stall timeout, FOV per profile, config keys) and `docs/` user notes on camera controls, when this lands. Test: review.
