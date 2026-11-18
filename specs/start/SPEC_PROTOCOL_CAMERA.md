# SPEC_PROTOCOL_CAMERA.md — PushNav

Document version: 3.0
Protocol version (wire): **1** — must match `data/VERSION.json.protocol_version`
and `PROTOCOL_VERSION` in `python/evf/camera/client.py`, `camera/mac/Sources/.../main.swift`, and the Linux/Windows camera servers.
Protocol version (wire) **2**: Draft — specified in §11, not yet implemented
(issue #3). v2 is negotiated on top of v1 and keeps every v1 peer working.
Status: §1–§10 Reflect Current Implementation (v1); §11 Draft (v2)
Date: 2026-11-18 (v2 draft); 2026-04-20 (v1 text)
Transport: TCP on localhost
Applies to: macOS (Swift), Linux (C/V4L2), Windows (C/DirectShow) — all v1;
v1 + v2: Windows C#/.NET (`specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md`),
which ships side by side with the Windows C server.

The document-revision number above (3.0) is editorial — it tracks updates to
this spec file, not the wire protocol. The wire protocol was unchanged from
v1 of the project until the v2 draft in §11. See the changelog in §12.

---

## 1. Goals

- Provide a platform-agnostic contract between Python app and native camera backend.
- Stream MJPEG frames (no transcoding required in Python).
- Expose only required controls for v1: Exposure + Gain.
- Enable robust disconnect detection and allow auto-restart.

### Non-goals (v1)
- Camera selection / multiple cameras
- Changing resolution/fps (v2 adds fps and binning, §11)
- Generic UVC descriptor-driven control exposure in UI
- Overlays / in-app crosshair

---

## 2. Transport

- Camera server binds to `127.0.0.1`.
- Default port: `8764`.
- Python app connects as a TCP client at startup.
- Camera server may be spawned by Python or run standalone for debugging.

---

## 3. Message Envelope

Every message uses the same envelope:

- `type`   : 4 bytes unsigned int, little-endian
- `length` : 4 bytes unsigned int, little-endian
- `payload`: `length` bytes

```
+---------+----------+------------------+
| type    | length   | payload          |
| u32 LE  | u32 LE   | length bytes     |
+---------+----------+------------------+
```

Rules:
- `length` may be 0.
- Receiver must read exactly 8 bytes header, then exactly `length` payload bytes.
- If socket closes mid-message, treat as disconnect.
- Unknown `type` MUST be ignored (log at DEBUG) unless in handshake stage.
  This preserves forward compatibility when a newer client sends messages
  an older server doesn't recognize.

> **Known issue (not yet fixed):** the macOS Swift backend
> (`camera/mac/Sources/CameraServer/TCPServer.swift` in the `parseHeader`
> path) currently **disconnects** on unknown message types instead of
> ignoring them, violating the rule above. This breaks forward-compat for
> any future message added to the protocol. Linux/Windows C backends fall
> through their if/else dispatch chain without disconnecting, which matches
> the spec. A follow-up task should change the Swift branch to log-and-skip
> the frame rather than tear the connection down. Tracked separately; do
> not rely on "ignore unknown" from macOS until that fix lands.

---

## 4. Protocol Versioning + Handshake (Required)

Even in v1, a handshake is REQUIRED to prevent silent breakage later.

### 4.1 Message Type: HELLO (0x00)

Direction: both ways  
Payload: UTF-8 JSON object

**Camera → App**: sent immediately after client connects.  
**App → Camera**: client responds once after receiving camera HELLO.

Example payload:

```json
{
  "protocol_version": 1,
  "backend": "mac-swift",
  "backend_version": "0.1.0",
  "camera_model": "openaicam",
  "stream_format": "MJPEG",
  "default_width": 1280,
  "default_height": 720,
  "default_fps": 30
}
```

The `camera_model` value is backend-specific:
- macOS (Swift) sends the hardcoded string `"openaicam"`.
- Linux (V4L2) and Windows (DirectShow) send the probed device model string.

Clients should treat `camera_model` as free-form informational and not parse
it as an enum.

Handshake rules:
- If `protocol_version` mismatch:
  - App logs error (INFO)
  - App enters ERROR state (or SETUP with error banner)
  - App closes socket
  - Camera server may exit
- After HELLO exchange, camera server MUST send CONTROL_INFO.

---

## 5. Message Types

### 5.1 Camera → App

#### 0x01 FRAME
Payload: raw JPEG bytes (a single MJPEG frame)

Rules:
- FRAME messages may be sent continuously at camera FPS.
- No metadata required in payload v1.
- Python stores only the latest frame; older frames are overwritten.
- If app is not ready (e.g., still in SETUP), frames are still allowed.

#### 0x02 CONTROL_INFO
Payload: UTF-8 JSON object

Required schema (v1):

```json
{
  "controls": [
    {
      "id": "exposure",
      "label": "Exposure",
      "type": "int",
      "min": 1,
      "max": 5000,
      "step": 1,
      "cur": 100,
      "unit": "100us"
    },
    {
      "id": "gain",
      "label": "Gain",
      "type": "int",
      "min": 0,
      "max": 255,
      "step": 1,
      "cur": 10,
      "unit": "raw"
    }
  ]
}
```

Rules:
- CONTROL_INFO MUST be sent once right after handshake completes.
- CONTROL_INFO MUST be re-sent after every SET_CONTROL, so the app sees the
  actual applied value (which may differ from the requested value if the
  camera clamped or rounded it). Backends currently re-send unconditionally
  on every SET_CONTROL — a superset of the spec requirement that simplifies
  implementation and gives the UI a consistent update cadence.
- App builds UI controls dynamically from this list.

#### 0x03 ERROR
Payload: UTF-8 string

Rules:
- Backend should send ERROR on:
  - camera not found
  - permission failure
  - UVC control failures that prevent operation
  - capture session failure
- Fatal errors should be followed by server closing connection (and likely exiting).

---

### 5.2 App → Camera

#### 0x11 SET_CONTROL
Payload: UTF-8 JSON object

Schema:

```json
{
  "id": "exposure",
  "value": 250
}
```

Rules:
- `id` must be `"exposure"` or `"gain"` in v1.
- Backend clamps to valid range.
- Backend applies change as quickly as possible.
- Backend MUST respond with CONTROL_INFO reflecting applied values.

#### 0x12 GET_CONTROLS
Payload: empty

Rules:
- Backend responds with CONTROL_INFO immediately.

---

## 6. Required Backend Behavior (v1)

### 6.1 Force Auto Exposure OFF at initialization
Camera backend MUST force auto exposure OFF during initialization, before streaming begins.
This setting is NOT exposed in UI.

The macOS Swift backend implements this via a direct USB control request
through IOKit (the `UVCController` class in `camera/mac/Sources/CameraServer/`),
because AVFoundation does not expose UVC auto-exposure controls. Linux and
Windows backends use their native V4L2 / DirectShow APIs.

### 6.2 Single supported camera model
Backend assumes one supported camera model (known VID/PID + known control layout).
If not found:
- Send ERROR with descriptive message
- Exit process (Python will restart with retry policy)

### 6.3 Stream format and encoding
- MJPEG frames are forwarded as-is (no re-encoding) if capture provides JPEG.
- If capture provides raw frames, backend must encode JPEG.
- Protocol payload for FRAME is always JPEG bytes in v1.

---

## 7. Disconnect Semantics

- No explicit keepalive in v1.
- Liveness is based on: socket connected + frames arriving.
- If the app does not receive frames for `_FRAME_STALL_TIMEOUT` seconds it
  treats the camera as stalled and triggers a subprocess restart. Currently
  hardcoded to **2.0 seconds** in `python/evf/camera/subprocess_mgr.py`; no
  config hook. If the timeout needs to be user-tunable later, add a config
  field rather than a command-line flag (so it persists across runs).

---

## 8. Test Vectors (Required)

A simple Python test client must be able to:
1. Connect
2. Receive HELLO
3. Send HELLO
4. Receive CONTROL_INFO
5. Receive FRAME bytes continuously
6. Send SET_CONTROL and observe CONTROL_INFO update

This enables backend development independent of the full UI.

---

## 9. Reserved Extensions (Do not use in v1)

Reserved message types:
- 0x13 START_STREAM
- 0x14 STOP_STREAM
- 0x15 SET_STREAM_CONFIG (assigned in v2, §11.6)
- 0x16 GET_LATEST_FRAME
- 0x20 STATS

v2 also assigns 0x04 STREAM_INFO and 0x05 FRAME_RAW (camera → app), §11.

---

## 10. macOS Implementation Notes (Reference Only)

The macOS Swift backend lives in `camera/mac/Sources/CameraServer/` and is
built via Swift Package Manager (see `scripts/build_camera_mac.sh`).

Key classes in the backend:

- `UVCController` — performs UVC control reads/writes via IOKit USB control
  requests. Used specifically for auto-exposure-off and for the exposure /
  gain set paths that AVFoundation cannot reach.
- `CaptureSession` wrapper — AVFoundation capture session + sample-buffer
  delegate; emits MJPEG-compressed `Data` into the TCP FRAME payload.
- `TCPServer` — single-client TCP server that accepts the Python app and
  handles the length-prefixed framing. **Known issue:** currently disconnects
  on unknown message types in `parseHeader`; should instead skip and continue
  (see §3).

---

## 11. Protocol v2 (Draft — issue #3)

Status: Draft, awaiting approval. Nothing in §11 is implemented yet.
Engine/config/UI side: `specs/camera-server/SPEC_CAMERA_CONTROLS.md`.
First v2 server: the Windows C# server
(`specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md`). The Python mock
(`tests/mock_camera_server.py`) implements v2 for tests.

### 11.1 What changes vs v1 (summary)

| Area | v1 (§1–§10) | v2 |
|---|---|---|
| Version handshake | Both sides send `protocol_version: 1`; mismatch = error | Server still sends `protocol_version: 1` plus `max_protocol_version: 2`; client answers with the version it picked (§11.2) |
| Capabilities | none | `capabilities` object in server HELLO (§11.3) |
| Control ids | `exposure`, `gain` only | `exposure`, `gain`, `fps`, `binning`; clients ignore ids they don't know |
| Exposure unit | backend-native (`"100us"`, `"log2s"`) | always **microseconds** (`"us"`) |
| Control types | `int` | `int`, `float`, `enum` (§11.4) |
| Ranges after a change | CONTROL_INFO re-sent after SET_CONTROL | same, and it MUST carry the **updated ranges of all controls** (coupling, §11.4.3); may also be sent unsolicited |
| Frame size | fixed, from HELLO `default_width/height` | may change at runtime; announced by **STREAM_INFO** (0x04) before the first frame of the new size (§11.5) |
| Frame payload | JPEG only | JPEG (default) or, if negotiated, **FRAME_RAW** (0x05) GRAY8 (§11.6) |
| App → camera | SET_CONTROL, GET_CONTROLS | + SET_STREAM_CONFIG (0x15, was reserved) |
| Liveness | fixed 2.0 s stall timeout | stall timeout derived from the frame interval (§11.7) |

Unchanged: envelope (§3), message ids 0x00–0x03 / 0x11–0x12, ERROR
semantics, single client, exit-on-disconnect, auto exposure forced off (§6.1).

### 11.2 Handshake and negotiation

Server HELLO (v2-capable server):

```json
{
  "protocol_version": 1,
  "max_protocol_version": 2,
  "backend": "windows-dotnet-ids",
  "backend_version": "0.1.0",
  "camera_model": "UI306xCP-M",
  "camera_key": "ids:1409:3060",
  "camera_serial": "4102825084",
  "stream_format": "MJPEG",
  "default_width": 1936,
  "default_height": 1216,
  "default_fps": 25,
  "capabilities": { "frame_formats": ["JPEG", "GRAY8"], "stream_info": true,
                    "sensor": { "width": 1936, "height": 1216, "pixel_size_um": 5.86, "mono": true } }
}
```

Why `protocol_version` stays 1 in the server HELLO: the v1 Python client
rejects any other value, and the macOS Swift server rejects a client HELLO
other than 1. Keeping the base value at 1 and adding `max_protocol_version`
means **all four combinations work**:

| Server | Client | Result |
|---|---|---|
| v1 (Swift, Linux C, Windows C) | v1 | v1 (today) |
| v1 | v2 | client sees no `max_protocol_version`, answers `protocol_version: 1`, runs v1 |
| v2 | v1 | client answers 1 (it ignores unknown HELLO keys), server runs v1 |
| v2 | v2 | client answers `protocol_version: 2`, both run v2 |

Client HELLO (v2 client talking to a v2 server):

```json
{ "protocol_version": 2, "app": "evf", "app_version": "0.4.0" }
```

Rules:
- The client picks `min(client_max, server max_protocol_version or 1)`.
- The server MUST accept a client `protocol_version` in
  `[1, max_protocol_version]` and run that version for the session. Any
  other value: ERROR "Protocol version mismatch", close.
- In a v1 session a v2 server MUST behave exactly as §1–§10 (only
  `exposure` and `gain`, v1 units, JPEG frames, no STREAM_INFO).
- After the client HELLO in a v2 session the server sends, in order:
  CONTROL_INFO, STREAM_INFO, then FRAMEs.
- `camera_key` (`"<backend>:<vid>:<pid>"`, or `"<backend>:<model>"` when
  there is no USB id) is stable per camera model and is what the engine
  uses to store per-camera settings. `camera_serial` is optional.
- `data/VERSION.json.protocol_version` becomes `2` and means "highest
  version this release speaks"; `PROTOCOL_VERSION` in `client.py` becomes
  `MAX_PROTOCOL_VERSION = 2` with `MIN_PROTOCOL_VERSION = 1`.

### 11.3 Capabilities

`capabilities` in the server HELLO describes features of the opened camera
that do not change during the session:

- `frame_formats`: formats the server can send (`"JPEG"` is mandatory;
  `"GRAY8"` means FRAME_RAW is available).
- `stream_info`: `true` if the server sends STREAM_INFO (required in v2).
- `sensor`: physical sensor size, pixel pitch and mono flag, used by the
  engine for plate scale (`SPEC_CAMERA_CONTROLS.md` §5.2).
  `pixel_size_um` may be omitted when unknown (UVC).
- Which controls exist is **not** in HELLO; it is the CONTROL_INFO list.
  A backend advertises only controls its camera supports, with real ranges.
- Unknown capability keys MUST be ignored.

### 11.4 Controls (v2)

#### 11.4.1 Descriptor

```json
{
  "controls": [
    { "id": "exposure", "label": "Exposure", "type": "int", "unit": "us",
      "min": 92, "max": 4000000, "step": 78, "cur": 200000, "scale": "log" },
    { "id": "fps", "label": "Frame rate", "type": "float", "unit": "fps",
      "min": 1.0, "max": 10.25, "step": 0.01, "cur": 4.0 },
    { "id": "gain", "label": "Gain", "type": "int", "unit": "raw",
      "min": 0, "max": 100, "step": 1, "cur": 20 },
    { "id": "binning", "label": "Binning", "type": "enum", "cur": 1,
      "options": [ { "value": 1, "label": "1x1" }, { "value": 2, "label": "2x2" } ],
      "restarts_stream": true }
  ]
}
```

Fields: `id`, `label`, `type` (`int` | `float` | `enum`) and `cur` are
required. `min`/`max`/`step` are required for `int`/`float`, `options`
for `enum`. Optional: `unit`, `scale` (`"linear"` default | `"log"`, a UI
hint), `restarts_stream` (bool: changing it causes STREAM_INFO and resized
frames), `default`.

#### 11.4.2 Known control ids

| id | type | unit | meaning |
|---|---|---|---|
| `exposure` | `int`, or `enum` if the camera only has discrete steps (UVC log2) | `us` | exposure time in microseconds |
| `gain` | `int` or `float` | `raw`, `dB` or `x` | sensor gain; the unit says how to show it |
| `fps` | `float`, or `enum` of the camera's discrete rates | `fps` | target frame rate; frame interval = `1e6 / fps` µs |
| `binning` | `enum` | — | NxN binning; option `value` = N |

Clients ignore control ids they don't know. Servers ignore SET_CONTROL for
ids they don't advertise (and re-send CONTROL_INFO).

#### 11.4.3 SET_CONTROL and coupling

- Payload as v1: `{"id": "...", "value": <number>}`; `value` may be a float.
- `int`/`float`: the server clamps to `[min, max]` and rounds to `step`.
  `enum`: a value not in `options` is rejected (no change).
- **Exposure priority**: if `exposure` is set longer than the current
  frame interval, the server lowers `fps` to fit (if `fps` is adjustable);
  otherwise it clamps exposure. Setting `fps` higher than the current
  exposure allows clamps `fps` (exposure is kept).
- After every SET_CONTROL the server MUST send CONTROL_INFO with the
  **current values and ranges of all controls**, because one change can
  move other ranges (fps → exposure max, binning → fps max).
- The server MAY send CONTROL_INFO unsolicited whenever ranges change.
- Recommended order for clients restoring saved settings:
  `binning`, `fps`, `exposure`, `gain`.

### 11.5 STREAM_INFO (0x04, camera → app)

Payload: UTF-8 JSON.

```json
{
  "generation": 2,
  "state": "streaming",
  "width": 968,
  "height": 608,
  "binning": 2,
  "frame_format": "JPEG",
  "frame_interval_us": 250000,
  "exposure_us": 200000
}
```

Rules:
- Sent once after CONTROL_INFO in the v2 handshake (`generation` 1).
- When a change will alter the stream (binning, a UVC media-type change
  for `fps`, SET_STREAM_CONFIG): the server first sends STREAM_INFO with
  `"state": "reconfiguring"` (same generation), restarts the stream
  in-process, then sends STREAM_INFO with `"state": "streaming"`, the new
  geometry and `generation + 1` **before** the first frame of the new size.
- Also re-sent when `frame_interval_us` changes (fps/exposure change), with
  the same generation, so the client can adapt its stall timeout.
- The TCP connection stays open across a reconfiguration.
- A reconfiguration SHOULD complete within 2 s. If it fails: ERROR, exit(1).

### 11.6 SET_STREAM_CONFIG (0x15) and FRAME_RAW (0x05)

SET_STREAM_CONFIG (app → camera), payload JSON:

```json
{ "frame_format": "GRAY8" }
```

- `frame_format` must be one of `capabilities.frame_formats`; otherwise it
  is ignored. The server answers with STREAM_INFO (the reconfiguration
  rules of §11.5 apply; geometry is unchanged).
- The default for every session is `"JPEG"`.

FRAME_RAW (camera → app), binary payload, little-endian:

| Offset | Size | Field |
|---|---|---|
| 0 | u32 | `generation` (matches STREAM_INFO) |
| 4 | u16 | `width` |
| 6 | u16 | `height` |
| 8 | u32 | `stride` (bytes per row, ≥ width) |
| 12 | u32 | `pixel_format` (1 = GRAY8; other values reserved) |
| 16 | u64 | `timestamp_us` (server monotonic clock) |
| 24 | … | pixel rows, `stride * height` bytes |

1936x1216 GRAY8 is ~2.35 MB, within the 16 MB message limit. FRAME_RAW is
optional for servers and clients; JPEG FRAME stays the default. When the
engine requests GRAY8 it must JPEG-encode for `/frame.mjpg` itself; that is
a later change (`SPEC_CAMERA_CONTROLS.md` §7).

### 11.7 Liveness (v2)

- The client's frame-stall timeout is
  `max(2.0 s, 2 × frame_interval + 1.0 s)`, using the latest STREAM_INFO
  `frame_interval_us`, so a 4 s exposure does not trigger a restart.
- While the last STREAM_INFO says `"reconfiguring"`, the client suspends
  stall detection for up to 5 s.
- v1 sessions keep the fixed 2.0 s (§7).

### 11.8 Test vectors (v2)

In addition to §8, a v2 test client must be able to:
1. Negotiate v2 with a v2 server and v1 with a v1 server (and the reverse).
2. Receive CONTROL_INFO then STREAM_INFO before the first FRAME.
3. Set `exposure` longer than the frame interval and see `fps` lowered and
   the ranges updated in the next CONTROL_INFO.
4. Set `binning` to 2 and see STREAM_INFO `reconfiguring` → `streaming`
   with the new size before a frame of the new size arrives.
5. Send an unknown message type and keep streaming.
6. Request GRAY8 (if advertised) and receive FRAME_RAW with matching
   generation and size.

These live in `tests/test_camera_conformance.py` and run against the
Python mock and, when available, real servers.

### 11.9 Open questions (protocol)

- Gain units: IDS reports a 0–100 scaled master gain; real dB is not
  available from the uEye API for every model. Proposed: `raw` now; a
  backend may switch to `x` (gain factor) later without a protocol change.
- The upstream servers (macOS Swift, Linux C, **Windows C/DirectShow**)
  stay v1 and are not modified by this fork, so upstream changes to them
  merge cleanly. They keep working as v1 peers through the negotiation in
  §11.2. Giving them v2 would be an upstream decision, not part of #3.
- Fix the Swift server's disconnect-on-unknown-type (§3 known issue) before
  any v2 client sends SET_STREAM_CONFIG to it? Not needed: a v2 client only
  sends v2 messages in a v2 session, and Swift never negotiates v2.

---

## 12. Changelog

| Doc version | Date | Change |
|---|---|---|
| 3.0 | 2026-11-18 | (Also: the Windows C server stays v1 permanently beside the C# server.) Added §11 protocol v2 draft (capability handshake, µs exposure, fps/binning controls, coupling, STREAM_INFO, FRAME_RAW, SET_STREAM_CONFIG, adaptive stall timeout). §1–§10 (v1) unchanged except header and cross-references. |
| 2.1 | 2026-04-20 | Editorial updates; wire protocol v1 unchanged. |
