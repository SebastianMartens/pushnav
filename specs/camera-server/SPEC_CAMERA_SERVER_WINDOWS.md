# SPEC_CAMERA_SERVER_WINDOWS.md — PushNav

Version: 1.3 (2026-11-18: status after PRs #9, #10, #14; task 7 (v2) detailed and scheduled in `SPEC_CAMERA_CONTROLS.md` §10.8; Q2, Q5 resolved. 1.2: C server kept permanently side by side, server selection, upstream minimal-diff rules; 1.1: 25 mm reference lens, wide-field solver database)
Status: Approved (PR #8). Issue #1 closed on 2026-11-24. Tasks 1–6 done (PRs #9, #10, #14, #15; 5.6, 5.7 (manual start without a camera), 5.9 and 6.8 on 2026-11-24); 6.7 optional, 6.2a not planned. Task 7 (protocol v2): 7.0 done (S1b), 7.1 done (S5), 7.2 done in S6 apart from its **[HW]** runs; 7.3 optional and not scheduled; tracked under #3 (`SPEC_CAMERA_CONTROLS.md` §10.8). Task 8 (UVC) not planned
Date: 2026-11-18
Target: Windows 10/11 x64
Issue: SebastianMartens/pushnav#1 (IDS uEye UI-306xCP-M support)
Related: #3 (camera controls; see `SPEC_CAMERA_CONTROLS.md` and
`specs/start/SPEC_PROTOCOL_CAMERA.md` §11, protocol v2)
Baseline: `specs/start/SPEC_ARCHITECTURE.md` v2.0, `SPEC_PROTOCOL_CAMERA.md`
doc 2.1 / wire v1, `SPEC_BUILD_RELEASE.md` v2.1

---

## 1. Overview

PushNav's Windows camera server today is `camera/windows/camera_server.c`
(C / DirectShow). It only accepts allowlisted OV9281 UVC modules, negotiates
a fixed 1280x720 MJPG/YUY2 stream, and reaches exposure/gain through
`IAMCameraControl` / `IAMVideoProcAmp`.

This spec adds a **new** Windows camera server written in **C# / .NET 10**,
published as a **Native AOT single-file exe**. It speaks the existing camera
TCP protocol (`SPEC_PROTOCOL_CAMERA.md`), so the Python engine does not care
which server is running. Inside the server, pluggable **backends** drive
the actual hardware:

1. **IDS backend** (first; the goal of #1): IDS uEye UI-306xCP-M, USB
   VID `1409` / PID `3060`, mono, Sony IMX174, 1936x1216.
2. **UVC backend** via Media Foundation (**optional, no deadline**): an
   alternative to the C/DirectShow server for the OV9281 allowlist
   (Waveshare / Arducam / DECXIN). It is off by default; OV9281 users are
   served by the C server through the automatic fallback (§8).
3. **Later**: astronomy-camera SDKs (ZWO, QHY, ...) or ASCOM. Room is left
   in the backend interface (§4); not specified here.

**Side by side, permanently.** The C# server is added **next to** the
existing C/DirectShow server; it does not replace it. The C server
(`camera/windows/camera_server.c`, `camera/windows/build.bat`) stays in the
build for good and is **not modified** by this work, so upstream fixes to
it merge cleanly. A setting picks which server starts; the default is the
C# one (§8). The fork stays close to upstream by following the
minimal-diff rules in §8.5.

What does **not** change:

- Architecture rule: the camera server is a separate OS process talking TCP
  on `127.0.0.1:8764` (`SPEC_ARCHITECTURE.md` §1.6, §7).
- Exit semantics: the server `exit(1)`s on device disconnect / fatal capture
  error, and `SubprocessManager` runs its 5-attempt backoff (1, 2, 4, 8, 15 s).
- The macOS (Swift), Linux (C/V4L2) **and Windows C/DirectShow** servers,
  and the Python engine's frame path (JPEG in, `LatestFrame`, solver thread).

### 1.1 Terms

| Term | Meaning |
|---|---|
| **Camera server** | The native process that owns the camera and serves the camera TCP protocol. One per platform, except Windows, which has two side by side for good: the C# server (default) and the upstream C/DirectShow server. |
| **Backend** | A class inside the C# camera server that knows one family of cameras (IDS, UVC, Fake, ...). It probes for devices, opens one, produces frames, and exposes controls. |
| **Quirk** | A small per-camera-model class that adjusts a backend's defaults (formats, control mapping, ranges) for one VID/PID. Replaces `#ifdef`s and special cases in the backend. |
| **Control** | A camera setting the app can read and change over the protocol (`exposure`, `gain`, `fps`, `binning`). Described by a control descriptor in CONTROL_INFO. |
| **Capability** | What a backend says it supports for the opened camera: which controls, their real ranges, frame formats. Sent in HELLO / CONTROL_INFO (protocol v2). |
| **Binning** | Combining NxN sensor pixels into one output pixel. Changes the frame size (1936x1216 at 1x1, 968x608 at 2x2 on the UI-306xCP-M). |
| **Frame interval** | Time between two frames (1/fps). Upper bound for exposure on most cameras. |
| **Stream geometry** | Width, height, binning and pixel format of the frames currently being sent. Changes when binning changes. |
| **Fake backend** | A backend with no hardware that replays sample images and simulates controls, for tests and CI. |

---

## 2. Hardware Findings (verified 2026-11-18)

A read-only probe (scratch .NET 10 program using the installed
`uEyeDotNet.dll`, not committed) ran against the attached camera:

| Item | Value |
|---|---|
| PnP instance | `USB\VID_1409&PID_3060\4102825084`, device class **USB** (not Camera/Image) |
| Driver | `oem147.inf` (IDS `ueye_usb_64.sys`), IDS Software Suite **4.97** (`ueye_api_64.dll` 4.97.4111) |
| Sensor | `UI306xCP-M`, sensor id `UI1060_M` (IMX174), monochrome, **global shutter**, 5.86 µm pixels |
| Full frame | 1936 x 1216 |
| Default pixel format | **BGR8Packed** (must be set to `Mono8` explicitly); `Mono8`, `Mono12`, `Mono16` accepted |
| Binning | 2x2 only (`Horizontal2X | Vertical2X`) → **968 x 608**; did not change the fps range |
| Subsampling | 2x, 4x, 6x, 8x, 16x (not used; binning is better for SNR) |
| Pixel clock | discrete list 30, 59, 118, 237, 474 MHz (default 118) |
| Frame rate | 1.0 – 40.98 fps @ 118 MHz; 1.0 – 10.25 fps @ 30 MHz |
| Exposure | 0.033 – 39.5 ms @ 118 MHz / 25 fps; 0.092 – 999.2 ms @ 30 MHz / 1 fps (bounded by frame interval) |
| Long exposure | supported, 0.092 ms – **30 s** |
| Gain | master gain only, scaled 0–100 (factor 100 = 1.0x), gain boost supported |
| USB | bus speed flag `0x8` (USB 3.0) |

**Reference configuration** (Sebastian's setup): this camera with a
**25 mm Basler lens**. Sensor 11.35 x 7.13 mm → FOV **25.6° x 16.2°**
(~30° diagonal), **48.3"/px** at 1x1 and **96.7"/px** at 2x2. Binning
does not change the FOV. The shipped tetra3rs database (7°–10°) cannot
solve this field; `SPEC_CAMERA_CONTROLS.md` §5.2–§5.4 adds a wide-field
database and per-profile FOV. That is engine work under #3; the camera
server only reports the sensor size and pixel pitch (HELLO `capabilities.sensor`).

Consequences:

- The camera is **not** a UVC device. Media Foundation will not enumerate
  it, so a native IDS backend is required (the DirectShow `uEyeCapture.ax`
  route from the issue is not pursued; §3.1).
- Exposure is coupled to frame interval and pixel clock. The backend owns
  pixel clock selection (§5.4); the protocol never exposes it.
- Exposures above 2 s collide with the engine's fixed 2.0 s frame-stall
  timeout (`SPEC_PROTOCOL_CAMERA.md` §7). Handled in `SPEC_CAMERA_CONTROLS.md`.

---

## 3. Key Decisions

### 3.1 IDS access path: uEye API via own P/Invoke

Options considered:

| Option | Status on this machine | Verdict |
|---|---|---|
| **uEye API** (`ueye_api_64.dll`) | Installed in `C:\Windows\System32` by the IDS driver; all needed exports present (`is_InitCamera`, `is_Exposure`, `is_SetBinning`, `is_PixelClock`, `is_SetFrameRate`, `is_GetFrameTimeRange`, `is_AllocImageMem`, `is_AddToSequence`, `is_InitImageQueue`, `is_WaitForNextImage`, `is_CaptureVideo`, `is_SetHardwareGain`, `is_EnableEvent`, ...); probe works | **Chosen** |
| IDS peak / GenTL with uEye transport layer | Not installed (no peak, no GenTL producer, no `GENICAM_GENTL64_PATH`) | Fallback, see below |
| DirectShow `uEyeCapture.ax` through the C server | Registered, but RGB/Y800 formats, unknown control mapping, no binning/long exposure | Rejected |
| `uEyeDotNet.dll` wrapper | netstandard2.0, pure managed, 338 `DllImport`s into `ueye_api_64`, 3 delegate types | Reference only (§3.2) |
| Media Foundation (directly, or via IDS peak) | The camera is not a KS video-capture device, so MF can't see it; IDS peak offers no MF path (see below) | Rejected |

Reasons for the uEye API: it is what the installed driver ships, it is
verified on the real camera today, it covers all four controls (exposure
incl. long exposure, fps, gain, binning), and it needs no extra install.
IDS has moved new development to IDS peak, so the uEye API is in
maintenance; this is acceptable for a camera model that is itself legacy.

Fallback: an `IdsPeakBackend` (GenTL via IDS peak's C API `ids_peak_comfort_c`
or `ids_peak` + `uEye transport layer`), only if the uEye API turns out to
be unusable. According to IDS, peak reaches legacy uEye models through its
uEye Transport Layer together with IDS Software Suite ≥ 4.95 (4.97 is
installed). Whether this 2015 UI-306xCP-M is actually listed is an open
question (§11, Q1). The backend interface (§4) keeps this a drop-in.

**Why not Media Foundation for the IDS camera** (Sebastian's question,
answered 2026-11-18):

- Measured with the camera attached: WinRT
  `DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)` (the same KS
  camera category Media Foundation enumerates) lists only "C505e HD
  Webcam" and "HP True Vision 5MP Camera". The uEye appears as PnP class
  `USB`, service `ueye_usb`, status OK. It is not a video-capture device,
  so Media Foundation cannot open it.
- IDS peak does not change that. It provides a **DirectShow** interface
  (since peak 2.9; legacy UI models via the uEye Transport Layer + IDS
  Software Suite ≥ 4.95), not a Media Foundation source and not a
  registered `IMFVirtualCamera`. IDS's own UVC cameras are a different
  product line. Sources:
  [IDS peak 2.9 release notes](https://www.ids-imaging.us/release-note/release-notes-ids-peak-2-9.html),
  [IDS peak DirectShow manual, usage notes](https://inosaki.com/info/IDS-Imaging-Cameras/Manual/ids-peak-directshow-manual/en/ds-notes-usage.html),
  [IDS peak 26.06 readme](https://www.1stvision.com/software/IDS-Peak-26.06-Readme.pdf).
- Even through a bridge, the result would be worse: Media Foundation's
  standard capture pipeline delivers YUV/RGB, not mono/Bayer directly, and
  a DirectShow bridge exposes binning, µs exposure and long exposure only
  partially.

So the native uEye API stays the decision (IDS peak API as the
alternative), and Media Foundation is used only for the UVC backend (§6).

### 3.2 Interop under Native AOT

- **IDS**: own `[LibraryImport("ueye_api_64")]` declarations for the ~25
  functions used, blittable structs only, `[assembly: DisableRuntimeMarshalling]`.
  Do **not** reference `uEyeDotNet.dll`: compiling it into our AOT exe
  would redistribute IDS code (§3.4), it is not on NuGet (path-only
  reference), and its reflection/trim behaviour under AOT is untested.
  The DLL is resolved through the normal Windows search path
  (System32, put there by the IDS driver install). If it is missing, the
  IDS backend reports "not available" and the server falls through to the
  next backend. Load it with `NativeLibrary.TryLoad` first so a missing
  DLL never throws `DllNotFoundException` at probe time.
- **Media Foundation / COM**: built-in COM interop (`[ComImport]`) is not
  supported by Native AOT. Use **CsWin32** (`Microsoft.Windows.CsWin32`
  source generator) for the flat MF exports (`MFStartup`,
  `MFEnumDeviceSources`, `MFCreateSourceReaderFromMediaSource`, ...) and
  the COM interfaces (`IMFAttributes`, `IMFActivate`, `IMFMediaSource`,
  `IMFSourceReader`, `IMFMediaType`, `IMFSample`, `IMFMediaBuffer`,
  `IAMCameraControl`, `IAMVideoProcAmp`, `IKsControl`), configured for
  AOT (source-generated COM / `ComWrappers`, `allowMarshaling: false`
  where needed). Fallback: hand-written `[GeneratedComInterface]`
  declarations for the same small set.
- **JPEG encoding**: Windows Imaging Component (WIC) through the same
  CsWin32 COM path (§3.3).
- Build must have **zero** AOT/trim warnings (`TreatWarningsAsErrors` for
  IL2xxx/IL3xxx).

### 3.3 Frame format: JPEG encoded in the server (v1 and v2 default)

The C# server sends JPEG FRAME messages exactly as today
(`SPEC_PROTOCOL_CAMERA.md` §5.1, §6.3):

- UVC MJPG streams: forwarded as-is (no re-encode), like the C server.
- IDS `Mono8` and UVC YUY2/NV12: encoded in the server as **8-bit
  grayscale JPEG** (one component, quality 90 by default, configurable
  via `--jpeg-quality`).
- Encoder: **WIC** JPEG encoder (`GUID_WICPixelFormat8bppGray` input) —
  part of Windows, no redistribution, works under AOT via CsWin32 COM.
  Fallback if WIC is too slow: libjpeg-turbo statically linked into the
  AOT exe (`DirectPInvoke` + `NativeLibrary` item, BSD/IJG licences).

Reasons:

- Zero change to the solver (`PIL.Image.open(...).convert("L")`), the
  `LatestFrame` buffer and `/frame.mjpg` (which forwards camera JPEG bytes
  to browsers unchanged).
- A grayscale JPEG of a star field at q90 is roughly 150–400 KB (estimate, to be measured) vs 2.35 MB raw;
  localhost bandwidth is not the limit, but the engine's 10 Hz MJPEG
  fan-out and the solver's decode cost both stay as today.
- JPEG artifacts are small compared with sensor noise for centroiding at
  q90; this must be confirmed on real sky frames (§11, Q4).

Raw frames: protocol v2 defines an optional **FRAME_RAW** (GRAY8) message
(`SPEC_PROTOCOL_CAMERA.md` §11.6) so a later change can feed the solver
lossless pixels. The C# server implements it behind the v2 capability;
the engine keeps requesting JPEG until a measurement shows a solve-rate
gain worth the extra encode for `/frame.mjpg`. Not part of this spec's
done criteria.

Performance budget: encoding 1936x1216 GRAY8 must take **≤ 15 ms** on the
dev laptop (so ≥ 40 fps is possible); at 2x2 binning ≤ 5 ms. Measured in
a benchmark test (task 4.3).

### 3.4 Licensing and redistribution

- PushNav is GPL-3.0-or-later. The C# server is GPL-3.0-or-later too.
- No IDS file is bundled or redistributed: not `ueye_api_64.dll`, not
  `uEyeDotNet.dll`, not headers. The user installs the **IDS Software Suite**
  (driver + runtime). The installer and docs say so (task 6.4).
- P/Invoke declarations are written by us against the documented uEye API
  (function names, struct layouts, constants). Do not copy `uEye.h` into
  the repo. Note: the installed suite has **no** `develop/include` headers
  or manual; the implementer needs the IDS SDK headers/manual from the IDS
  download (developer option) or derives constants from the documented
  API (§11, Q2).
- Runtime-loading a proprietary, user-installed driver DLL from a GPL
  program is the same pattern as today's DirectShow/OS camera APIs.
  **Decided (Sebastian, 2026-11-18): acceptable**, on two conditions: no
  IDS file is bundled or redistributed, and the decision is documented for
  users. That documentation is a "Third-party software" note in `README.md`
  plus the IDS section in `docs/hardware.md` (task 6.4). Together they say:
  - IDS components are proprietary and not part of PushNav.
  - The user installs them separately.
  - The C# server only loads `ueye_api_64.dll` at runtime when it is
    present, and without it the IDS backend is simply unavailable.

### 3.5 Location and binary name

- Source: `camera/windows-dotnet/`.
- Exe: `camera_server_dotnet.exe`. The C server keeps `camera_server.exe`;
  both ship side by side permanently (§8).
- `SubprocessManager._kill_stale_server()` must kill **both** names on
  Windows (it uses `taskkill /IM <name>` today; §8.4).

---

## 4. Server Structure

### 4.1 Projects

```
camera/windows-dotnet/
  PushNav.CameraServer.slnx
  Directory.Build.props            # net10.0-windows, Nullable, AOT analyzers, warnings as errors
  src/PushNav.CameraServer/        # the exe (Native AOT, single file)
    Program.cs                     # arg parsing, backend selection, exit codes
    Protocol/                      # envelope, message types, HELLO/CONTROL_INFO JSON (System.Text.Json source-gen)
    Server/                        # single-client TCP server + session state machine
    Controls/                      # control descriptors, clamping, coupling helpers
    Imaging/                       # WIC JPEG encoder, pixel conversions (YUY2/NV12 -> GRAY8)
    Backends/
      ICameraBackend.cs
      Ids/                         # IdsBackend, UEyeNative (LibraryImport), quirks
      Uvc/                         # UvcBackend (Media Foundation), quirks per VID/PID
      Fake/                        # FakeBackend (sample images, simulated controls)
  tests/PushNav.CameraServer.Tests/   # xUnit (protocol, controls, fake backend, encoder)
```

One exe, no plugin loading (AOT forbids it anyway). "Pluggable" means:
backends implement one interface and are registered in a static list.

### 4.2 Backend interface (sketch)

```csharp
interface ICameraBackend : IDisposable
{
    string Id { get; }                              // "ids", "uvc", "fake"
    IReadOnlyList<DeviceInfo> Probe();              // cheap; never throws; [] if SDK/driver missing
    void Open(DeviceInfo device);                   // throws CameraException on failure
    StreamGeometry Geometry { get; }                // width, height, binning, pixel format
    IReadOnlyList<ControlDescriptor> Controls { get; }   // real units and ranges, current values
    ControlResult SetControl(string id, ControlValue value); // clamps; may change other controls/geometry
    void Start(IFrameSink sink);                    // frames pushed on a backend thread
    void Stop();
    event Action<string> Fatal;                     // device lost / capture failure -> exit(1)
}
```

- `DeviceInfo`: backend id, VID/PID (if USB), serial, model name, and a
  stable **camera key** (`"<backend>:<vid>:<pid>"`, e.g. `ids:1409:3060`)
  that the engine uses to store per-camera settings
  (`SPEC_CAMERA_CONTROLS.md` §4).
- `IFrameSink.OnFrame(ReadOnlySpan<byte> data, FrameKind kind, StreamGeometry g)`:
  the server keeps **only the latest frame** (single-slot, overwrite — same
  "no frame queues" rule as the engine) and the TCP writer thread sends it.
  A slow client never builds a backlog.
- Quirks: `IUvcQuirk` / `IIdsQuirk` with `Matches(DeviceInfo)` and hooks
  such as `PreferredFormats`, `MapExposure`, `DefaultControls`. One small
  class per camera model; the OV9281 modules start with one shared quirk.

### 4.3 Backend selection

At startup the server probes registered backends in order
**IDS → UVC** (Fake only with `--backend fake`):

1. If `--backend <id>` is given, only that backend is probed.
2. Else the first backend whose `Probe()` returns a device wins; within a
   backend the first allowlisted device wins.
3. Device wait: like the C server, retry probing for up to 8 s
   (500 ms steps) before giving up.
4. No device: send nothing (no client yet), print the devices seen
   (VID:PID + name, like the C server's diagnostic) to stderr and exit
   with **code 3** (`NO_SUPPORTED_CAMERA`). Code 1 stays "fatal / device
   lost". The engine uses code 3 for the fallback in §8.

The allowlist for UVC is the same as the C server (built-ins +
`PUSHNAV_CAMERA_IDS` env var, which `SubprocessManager._build_env()`
already injects from `camera.extra_camera_ids`). The IDS backend accepts
any camera the uEye API reports (`is_GetCameraList`), with a quirk for
`1409:3060`; other IDS models work with generic defaults.

### 4.4 Command line

| Flag | Meaning |
|---|---|
| `--port <n>` | TCP port (default 8764) |
| `--backend ids\|uvc\|fake` | force a backend |
| `--fake-images <dir>` | images for the Fake backend (default: none -> synthetic star field) |
| `--list` | print detected devices + capabilities as JSON and exit 0 (diagnostics; used by tests and the issue's "dump the caps" step) |
| `--jpeg-quality <1-100>` | default 90 |
| `--log-level debug\|info` | stderr logging |

No command-line flag for anything the user tunes at runtime (that goes
through protocol controls and config, as `SPEC_PROTOCOL_CAMERA.md` §7 asks).

### 4.5 Threads and lifecycle

- Main thread: arg parsing, probe, open, then the TCP accept loop.
- Backend capture thread (IDS: `is_WaitForNextImage` loop; UVC: source
  reader `ReadSample` loop) pushes into the latest-frame slot.
- Session thread: reads client messages (HELLO, SET_CONTROL,
  GET_CONTROLS, unknown types ignored) and writes CONTROL_INFO / ERROR.
- Writer: sends the latest frame when a new one is present. All socket
  writes are serialized with one lock.
- Single client, exit after disconnect — same as the C and Swift servers
  (the engine respawns it).
- Ctrl+C / Ctrl+Break / `CTRL_CLOSE_EVENT` and `process.terminate()` from
  Python (TerminateProcess on Windows) must leave the camera usable for the
  next spawn: the IDS backend calls `is_ExitCamera` in a finally / console
  handler; a hard kill is tolerated because the uEye driver frees the
  handle when the process dies (verify, task 5.8).

---

## 5. IDS Backend

### 5.1 Open sequence

1. `NativeLibrary.TryLoad("ueye_api_64.dll")`; missing -> backend unavailable.
2. `is_GetNumberOfCameras` / `is_GetCameraList` -> `DeviceInfo` list
   (model, serial, device id; VID/PID from the quirk table by model).
3. `is_InitCamera(&hCam, NULL)` for the chosen device id.
4. `is_SetColorMode(IS_CM_MONO8)` (default is BGR8 — verified).
5. `is_SetDisplayMode(IS_SET_DM_DIB)`; turn off all auto features
   (`is_SetAutoParameter`: auto shutter, auto gain, auto frame rate) — this
   is the IDS equivalent of `SPEC_PROTOCOL_CAMERA.md` §6.1 "force auto
   exposure OFF".
6. Apply the initial geometry (binning 1x1, full AOI) and the pixel clock
   for the initial fps (§5.4).
7. Allocate a ring of 4 image buffers (`is_AllocImageMem` + `is_AddToSequence`),
   `is_InitImageQueue`, `is_CaptureVideo(IS_DONT_WAIT)`.
8. Enable the device-removal event (`is_EnableEvent(IS_SET_EVENT_REMOVE)`)
   and watch it on a thread; on removal -> `Fatal` -> `exit(1)`.

### 5.2 Capture loop

`is_WaitForNextImage(timeout = max(1 s, 2 x frame interval + exposure))`,
copy the buffer (stride from `is_GetImageMemPitch`) into a pooled GRAY8
array, `is_UnlockSeqBuf`, JPEG-encode, push to the sink. Three consecutive
timeouts with no removal event -> ERROR message + `exit(1)`.

### 5.3 Controls advertised

| Control | Source | Range on UI-306xCP-M |
|---|---|---|
| `exposure` (µs) | `is_Exposure` (`GET_EXPOSURE_RANGE`, `SET_EXPOSURE`); long-exposure mode switched on transparently above the normal max | 92 µs – 4 s (sensor: 30 s; capped at 4 s, §11 Q5; v1 view 1.5 s, Q13) |
| `fps` | `is_SetFrameRate`, `is_GetFrameTimeRange` | 1.0 – 41 fps (depends on pixel clock) |
| `gain` | `is_SetHardwareGain` master (0–100 scaled) | 0 – 100 |
| `binning` | `is_SetBinning` (`IS_GET_SUPPORTED_BINNING` decides the options) | `1`, `2` |

Descriptor format, units and coupling rules: `SPEC_PROTOCOL_CAMERA.md`
§11.4 (v2). With a **v1** client the IDS backend advertises only
`exposure` and `gain`, with exposure in unit `"100us"` (int) so today's UI
formats it correctly, and keeps 1x1 binning (§7).

Gain boost (`is_SetGainBoost`) is off and not exposed in this spec.

### 5.4 Pixel clock and frame-rate policy

The protocol never exposes pixel clock. The backend picks it:

- For a requested fps, choose the **lowest** pixel clock from
  `IS_PIXELCLOCK_CMD_GET_LIST` whose max fps ≥ requested fps. Lower clock =
  longer max frame time = longer max exposure (verified: 999 ms @ 30 MHz
  vs 39.5 ms @ 118 MHz).
- After any change in clock / fps / exposure, re-read all ranges and send
  CONTROL_INFO (v2 coupling rule).
- Exposure priority: if `exposure` is set above the current frame
  interval, the backend lowers fps (and pixel clock if needed) so the
  exposure fits, instead of clamping the exposure. If exposure exceeds the
  normal range at minimum fps (1 fps), long-exposure mode is enabled.

### 5.5 Binning change (stream restart in-process)

Binning needs new image buffers, so the backend does an in-process
restart: `is_StopLiveVideo` -> free sequence -> `is_SetBinning` -> AOI
re-read -> allocate -> `is_CaptureVideo`. The TCP
connection stays open. The server then sends **STREAM_INFO** (v2) with the
new geometry before the first frame of the new size (protocol §11.5).
The engine side is in `SPEC_CAMERA_CONTROLS.md` §5.

**Time.** The 500 ms is a budget for the fake API (`FakeUEyeApi`: the backend's own work, thread stop and
start, rebuilding the timing table). On the real camera the target is the protocol's: §11.5 says a
reconfiguration SHOULD complete within 2 s, and the engine waits 5 s (`SPEC_CAMERA_CONTROLS.md` §3.3). Confirmed by Sebastian on 2026-11-25.

As built (S6, `IdsBackend.ChangeBinningLocked`): join the capture thread and `is_StopLiveVideo`, then
`is_ExitImageQueue`, `is_ClearSequence`, free the buffers, `is_SetBinning`, read the new size
(`IS_AOI_IMAGE_GET_SIZE`), **measure the frame timing again** (pixel clock table and exposure limits,
as at open: a binned sensor reads out faster, so the frame-rate range moves), apply the preferred frame
rate and the exposure to it, allocate the buffers for the new size, `is_CaptureVideo`, start a new
capture thread. The options of the `binning` control come from `IS_GET_SUPPORTED_BINNING`: a factor N
is offered when the sensor supports it on both axes (`IdsBinning`). The restart only happens for a
caller that may announce it (`ControlChange.MayRestartStream`, a v2 session) and not while
long-exposure mode is on (a binning change is then refused, with that reason). The per-call intent is
`ICameraBackend.SetControl(ControlChange)` with `MayRestartStream`, which only the v2 dialect sets, and
`WouldRestartStream` for the decision, so the backend never asks which protocol runs. Leaving
long-exposure mode in a v2 session (§5.6) is the second change that restarts, by the same restart.
**Every restart returns only after the first frame following it has been captured** (at most 2 s, then a
warning and carry on): this is the contract of `ICameraBackend.SetControl` for a change that restarts the
stream, which the Fake backend honours too. The caller announces `streaming` when the call returns, so the
gap that follows a restart (about 1 s after leaving long-exposure mode, about one frame after a binning
change) must fall inside `reconfiguring`, where the engine waits 5 s, and not after `streaming`, where its
stall timeout is only 2 s. The 2 s is the 5 s grace minus the switch (about 1.1 s) minus margin. A camera that refuses the mode stays at its binning and the stream is
brought back: a rejected SET_CONTROL, not a failure; any other failure inside the restart ends the
session with an ERROR (protocol §11.5). The time it takes is logged (`Stream restarted (...) in N ms`);
the re-measured timing may make it long on the real camera if reading the five pixel clocks costs what
it did at open (§5.6, about 2.7 s with `is_InitCamera`), to be measured against the 2 s above. If it is
over, the fallback is a timing cache per binning (the table depends only on the binning).

---

### 5.6 Measured behaviour (tasks 5.1 to 5.8, UI-306xCP-M, IDS Software Suite 4.97)

What the implementation relies on, measured on the real camera:

- **Open** takes about 2.7 s (`is_InitCamera` plus reading the timing of all five pixel clocks), so the server needs that long from start to "Listening". The camera's current pixel clock at open was 237 MHz on this machine (§2 recorded 118 MHz); it does not matter because the backend sets the clock itself.
- **Exposure clamps silently.** `is_Exposure(SET)` beyond the current maximum returns success with the clamped value. That is why the backend lowers the frame rate first (exposure priority, §5.4) and reads the exposure back. The headroom between exposure and frame time is 0.5 to 0.8 ms; the backend guesses 1 ms and verifies it against the real range.
- **Pixel clock and frame rate can be changed while streaming**; frames keep coming. A change of the exposure inside the normal range costs nothing. Going from a 1 s exposure at 1 fps to 100 ms at 10 fps leaves a gap of 1.0 s between frames.
- **Long-exposure mode is sticky, and is switched on from a fast frame rate.** `is_Exposure(SET_LONG_EXPOSURE_ENABLE)` blocks for about one frame time of the timing the camera has at that moment: 0.2 s at 10 fps, but 1.1 s at 1 fps (a gap of 2 to 3 s in the stream). So before switching on, the backend returns to a 10 ms exposure at 10 fps if the camera was exposing for most of a second, then switches, then sets the long exposure: the stream then shows a gap of 0.4 s at the switch. Switching **off** blocks for about 1.1 s and then delivers no frame for another second: a gap of about 2.5 s, which is more than the engine's fixed 2.0 s stall timeout (it would restart the server, and recovery would then repeat the same gap). So the backend never leaves long mode until the camera is closed; a shorter exposure is a plain `SetExposure` inside it, and frames keep coming at the frame rate the camera had when the mode went on (10 fps at 100 ms). In long mode the camera does not regulate the frame rate, so the `fps` control is not settable: SET_CONTROL `fps` is rejected (nothing is sent to the camera) and the control is advertised with min = max = cur = the rate the camera really runs at (a v1 client never sees `fps`). Reading the long-exposure range does not need the mode to be on. This is what a v1 session gets; a v2 session leaves the mode (next bullet).
- **Leaving long-exposure mode in a v2 session (S6, `SPEC_CAMERA_CONTROLS.md` §9 Q12).** When the exposure drops back into the normal range (at most the normal-mode maximum at the fps floor, about 1 s, the limit that switches the mode on), the backend leaves long mode as a stream restart wrapped in STREAM_INFO `reconfiguring` → `streaming` with generation + 1 and the same geometry, after which `fps` is settable again; the camera then runs at its own frame rate, so the preferred one is applied afresh. A v1 session never gets there: it cannot be told, and its fixed 2.0 s stall timeout would fire. While the mode is on a binning change is refused. How it is done is in §5.5 "As built". The gap after the switch (about 1 s) is to be measured with the server waiting for the first frame inside `reconfiguring` (S6 status, hardware checklist).
- **Unsupported auto features** (the sensor-side `IS_SET_ENABLE_AUTO_SENSOR_*` switches) answer `IS_NOT_SUPPORTED` on this camera; the backend ignores that.
- **The test scene was nearly black** (lens covered or dark room): the frame mean stayed below 2 of 255 even at 1.5 s and gain 100, so the brightness checks used the mean together with the maximum, the standard deviation and the JPEG size, all of which follow exposure and gain.

## 6. UVC Backend (Media Foundation)

Status: **optional, no deadline, off by default.** The C server remains
the supported path for UVC webcams. With the default setting `"dotnet"`,
the C# server exits with code 3 for an OV9281 and the engine starts the C
server (§8.2, §8.3), so OV9281 users keep working without this backend.
It exists so that UVC cameras can later get v2 controls (µs exposure, fps)
without touching the upstream C server. Enable it with the C# flag
`--enable-uvc`. The engine passes that flag only when config
`camera.dotnet_uvc` is `true` (default `false`).

Independently of the backend, the C# server always does a cheap Media
Foundation **enumeration** (symbolic links only, no streaming) for the fast
code-3 exit in §8.3.

Backend design (when built):

- Enumerate with `MFEnumDeviceSources(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID)`;
  read `MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK`
  (contains `vid_XXXX&pid_XXXX`) and match the allowlist (same rules as
  `find_device()` in the C server).
- Open with `IMFSourceReader` (no topology, no window). Choose the native
  media type: prefer **MJPG** at the largest allowlisted size the quirk
  allows (1280x720 for the OV9281 quirk, same as today), else YUY2/NV12
  (converted to GRAY8 and JPEG-encoded — this closes the C server's
  "YUYV needs libjpeg" gap).
- fps: pick the native type's **lowest** frame rate by default (keeps
  upstream issue #26 behaviour: max exposure headroom). With a v2 client,
  `fps` is advertised as an **enum** of the frame rates the device
  offers for the chosen size; changing it re-selects the media type
  (in-process restart, STREAM_INFO).
- Exposure/gain: `QueryInterface` the media source for `IAMCameraControl`
  (exposure, log2 seconds) and `IAMVideoProcAmp` (gain). Set exposure to
  manual at open (`SPEC_PROTOCOL_CAMERA.md` §6.1).
  - v1 client: advertise exactly what the C server advertises today
    (exposure unit `"log2s"`, gain `"raw"`), so behaviour is identical.
  - v2 client: exposure is advertised in µs. Preferred source is the
    extended control `KSPROPERTY_CAMERACONTROL_EXTENDED_EXPOSUREMODE`
    (100 ns units) via `IKsControl` if the driver supports it; otherwise
    an **enum** control whose options are the log2 steps converted to µs
    (e.g. 15 625, 31 250, 62 500 µs ...). Which one the OV9281 modules
    support must be checked on hardware (§11, Q6).
- Binning: not advertised for UVC (no standard UVC control).
- Device loss: `MF_E_HW_MFT_FAILED_START_STREAMING` / `MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED`
  or `ReadSample` stream-tick/error flags -> `Fatal` -> `exit(1)`.
- HELLO `backend` value: `"windows-dotnet-mf"` (C server: `"windows-directshow"`).

Done criteria for turning `camera.dotnet_uvc` on for a given user. These
are never a gate for removing the C server, which stays. On each OV9281
module the UVC backend must (a) find the module with the same allowlist,
(b) stream 1280x720 MJPG at the lowest fps, (c) disable auto exposure and
apply exposure/gain, (d) exit(1) within 2 s of unplugging, and (e) pass the
protocol conformance suite (§9.2). Whether the default ever flips to
`true` is Sebastian's call, later (§11, Q11).

---

## 7. Protocol Support

- The C# server implements wire **v1** exactly (`SPEC_PROTOCOL_CAMERA.md`
  §1–§9) and **v2** (`SPEC_PROTOCOL_CAMERA.md` §11). It negotiates per
  the v2 handshake rules: an old (v1) engine keeps working.
- Fixes known v1 gaps in its own implementation: unknown message types are
  ignored (§3), JSON is parsed with a real parser (System.Text.Json
  source generator), HELLO carries the real stream size instead of a
  hardcoded 1280x720.
- HELLO fields for the C# server: `backend` = `"windows-dotnet-ids"` /
  `"windows-dotnet-mf"` / `"windows-dotnet-fake"`, `backend_version` from the
  assembly version, `camera_model` = model string (`"UI306xCP-M"`).

---

## 8. Server Selection and Upstream Compatibility

The C# server and the C/DirectShow server are shipped **side by side,
permanently**. Nothing here retires, moves or edits the C server.

### 8.1 Setting

| Source | Values | Default | Notes |
|---|---|---|---|
| config `camera.windows_server` | `"dotnet"` \| `"c"` | `"dotnet"` | new key; merged into existing configs by `_merge_defaults` (no `CONFIG_VERSION` bump) |
| env `PUSHNAV_CAMERA_SERVER` | `"dotnet"` \| `"c"` | unset | overrides the config; for debugging |

- Unknown values: log a WARNING and use `"dotnet"`.
- On macOS and Linux the setting and the env var are ignored. Those
  platforms keep their single server and `paths.camera_binary()` as today.
- Settings UI: not required. An optional small later task (task 6.7)
  could add a "Camera server: C# (default) / C" select to the Settings
  panel. It would write the config key and trigger the existing
  "Retry camera" path.

### 8.2 Which exe starts

Binary locations (resolved in a new module, `evf/camera/server_select.py`,
which reuses `evf.paths` for the release/dev root):

| Mode | C# server | C server (unchanged) |
|---|---|---|
| Windows release | `<root>/camera_server_dotnet.exe` | `<root>/camera_server.exe` |
| Windows dev | `camera/windows-dotnet/publish/camera_server_dotnet.exe` | `camera/windows/camera_server.exe` |

Rules:

| Setting | Spawn order | Code-3 fallback |
|---|---|---|
| `"dotnet"` (default) | C#, then C if C# exits with code 3 | **yes** |
| `"c"` | C only | no (C# is never started) |

- **Code-3 fallback** (only with `"dotnet"`): if the C# server exits with
  code **3** (`NO_SUPPORTED_CAMERA`, §4.3) before the handshake,
  `SubprocessManager` spawns the C server in the **same** attempt. This
  does not consume a backoff step. Any other failure (code 1, timeout)
  follows the existing backoff unchanged.
- The server that connected is remembered for the session, and recovery
  attempts start with it. A new app start, or the user's "Retry camera",
  goes back to the setting's order.
- **Selected exe missing** (e.g. a dev checkout where
  `camera\windows-dotnet\build.bat` was never run, or a build without the
  .NET SDK): log a WARNING naming the missing path and how to build it
  ("C# camera server not found at …; run camera\windows-dotnet\build.bat.
  Falling back to the C server."), then use the other exe. This applies in
  both directions (`"c"` selected but `camera_server.exe` missing → C#).
  If neither exists: the existing `FileNotFoundError` path, with both
  paths in the message.
- Log at INFO which server was started and, after HELLO, its `backend` and
  `camera_model`.

### 8.3 Fast exit for cameras the C server handles

With the default `"dotnet"`, OV9281 users go through the code-3 fallback,
so the C# server must not hold them up with its 8 s device wait (§4.3):

- While the UVC backend is disabled (default, §6), the C# server
  enumerates Media Foundation video-capture devices once at startup. If
  one matches the UVC allowlist (built-ins + `PUSHNAV_CAMERA_IDS`) and no
  IDS camera is present, it exits with code 3 **immediately**, with a
  stderr note ("UVC camera vid:pid is handled by the C server").
- The 8 s wait only applies when no candidate device of any kind is seen
  (camera just plugged in or waking up).
- Target: an OV9281 user with the default setting gets frames at most
  about 1 s later than with `"c"`. Measured in task 6.2 / 8.1 **[HW: OV9281]**.

### 8.4 Stale-process cleanup

`_kill_stale_server()` kills both exe names on Windows (`camera_server.exe`
and `camera_server_dotnet.exe`), not only the one about to be spawned, so
a leftover of either server can't hold the camera or port 8764.

### 8.5 Upstream compatibility: files we touch

Upstream (`meridianfield/pushnav`) accepts no PRs, so this fork merges
upstream changes. Rules:

- New code goes into **new files and directories** wherever practical:
  `camera/windows-dotnet/**`, `evf/camera/server_select.py`,
  `tests/test_camera_conformance.py`, `tests/test_server_select.py`,
  `specs/camera-server/**`.
- Edits to existing upstream files are **small, isolated hooks** that
  call into the new modules. Each hook is marked with a one-line comment
  `# fork: camera-server-dotnet (see specs/camera-server/...)` so it is
  easy to find during a merge.
- **Never modified:** `camera/windows/camera_server.c`,
  `camera/windows/build.bat`, `camera/linux/**`, `camera/mac/**`,
  `scripts/pushnav.iss`.

Upstream files touched by **this** spec (#1):

| File | Hook | Why |
|---|---|---|
| `python/evf/camera/subprocess_mgr.py` | `_resolve_binary` / `_spawn_process` call `server_select`; one branch in `_connect_with_retry` for exit code 3; `_kill_stale_server` iterates the names `server_select` returns | selection, code-3 fallback, cleanup |
| `python/evf/config/manager.py` | one key `"windows_server": "dotnet"` in `DEFAULT_CONFIG["camera"]` | setting |
| `python/evf/paths.py` | one small helper `windows_camera_server_paths()` (dev and release layout of both exes) | selection |
| `scripts/build_windows.bat` | one block "Phase 1a: build C# camera server" after the C server build, plus one `copy` line in Phase 3; a "Phase 0" block that finds MSVC via `vswhere` + `vcvars64.bat` when `cl.exe` is not on `PATH` (#13) | packaging |
| `.github/workflows/test.yml` | one new job `camera-server-windows` appended; existing jobs untouched | CI |
| `.github/workflows/build.yml` | one `actions/setup-dotnet` step in `build-windows` | release build |
| `docs/hardware.md`, `CLAUDE.md`, `README.md`, `specs/start/SPEC_BUILD_RELEASE.md` | short added paragraphs (`README.md`: "Third-party software", §3.4) | docs |

Merge-risk notes: `subprocess_mgr.py` and `build_windows.bat` are the
likeliest conflict points. Keep the hooks at the edges of functions, not
interleaved with upstream logic. The protocol v2 work (#3) touches more
upstream files; it lists them in `SPEC_CAMERA_CONTROLS.md` §1.2.

`build_windows.bat` Phase 1a: if the `dotnet` CLI is not on `PATH`, print a
WARNING and skip it, so the upstream-style build still works. The release
build in CI always has the SDK (§10.3), and its "Verify" step checks that
`camera_server_dotnet.exe` exists in the release directory.

### 8.6 USB presence check

`_usb_video_device_present()` in the engine is Linux-only today; nothing
to change on Windows. (The headless watchdog is Pi-only.)

---

## 9. Testing

### 9.1 C# unit tests (xUnit, no hardware)

- Protocol: envelope encode/decode, HELLO negotiation v1/v2, unknown type
  ignored, oversize (> 16 MB) rejected, CONTROL_INFO JSON shape.
- Controls: clamping, step rounding, exposure/fps coupling, enum
  validation.
- Fake backend: frames arrive, binning changes geometry and sends
  STREAM_INFO first.
- JPEG encoder: GRAY8 round trip (decode with WIC, mean abs error < 2 at
  q90), size sanity, benchmark (§3.3 budget, reported, not failing in CI).
- IDS backend logic behind an `IUEyeApi` seam with a fake implementation
  (pixel-clock policy, exposure priority, long-exposure switch, timeout ->
  fatal).

### 9.2 Protocol conformance suite (shared, Python)

New `tests/test_camera_conformance.py`, parametrized over servers:

- always: `tests/mock_camera_server.py` (extended for v2 in #3),
- when `PUSHNAV_CONFORMANCE_SERVER=<path-to-exe>` is set: that binary,
  spawned with `--backend fake --port <free port>` (C# server), or the C
  server when a camera is attached (manual; v1 vectors only, because the
  C server stays v1).

Covers `SPEC_PROTOCOL_CAMERA.md` §8 test vectors for v1, plus v2:
handshake both directions, CONTROL_INFO after every SET_CONTROL,
coupling, STREAM_INFO before resized frames, unknown types ignored.
The C# job in CI runs it against the published AOT exe (§10.2).

### 9.3 Hardware tests (manual; need Sebastian + the camera)

Listed in the task checklist (§12) and marked **[HW]**.

---

## 10. Build, Packaging, CI

### 10.1 Local build

`camera\windows-dotnet\build.bat` (and `build.ps1`):
`dotnet publish src/PushNav.CameraServer -c Release -r win-x64 -p:PublishAot=true -o publish`
-> `publish\camera_server_dotnet.exe` (single file; AOT needs the MSVC
linker, which `ilammy/msvc-dev-cmd` / VS Build Tools already provide).
SDK pinned in `camera/windows-dotnet/global.json` (10.0.x,
`rollForward: latestFeature`; the dev box also has an 11.0 preview SDK).

### 10.2 `scripts/build_windows.bat` and Inno Setup

- New Phase 1a after the (unchanged) C server build: run the .NET publish.
  If the `dotnet` CLI is missing, print a WARNING and skip it (§8.5); if the
  publish runs and fails, fail the build. Copy `camera_server_dotnet.exe`
  next to `PushNav.exe` in Phase 3, beside `camera_server.exe`. Both
  additions are isolated blocks (§8.5).
- `scripts/pushnav.iss`: no change needed; its `[Files]` entry packages
  the whole assembled directory (`..\build\PushNav-windows\*`, recursive),
  so the new exe is picked up. No IDS files in the installer. The installer's finished page /
  docs mention "IDS cameras need the IDS Software Suite".
- `SPEC_BUILD_RELEASE.md` §6.1 gets a line for the new server when this
  lands (not edited by this spec).

### 10.3 GitHub Actions (infra)

The repo already has a PR test workflow, `.github/workflows/test.yml`
(jobs `python` on ubuntu + windows, and `web`; runs on every PR to `main`,
on pushes to `main`, and on `workflow_dispatch`). Extend it rather than
adding a new workflow:

- New job `camera-server-windows` in `test.yml` on `windows-latest`
  (timeout ~20 min):
  1. `actions/checkout`, `actions/setup-dotnet` (10.0.x, from
     `camera/windows-dotnet/global.json`), NuGet cache.
  2. `ilammy/msvc-dev-cmd` (the Native AOT linker needs MSVC).
  3. `dotnet test camera/windows-dotnet` (xUnit).
  4. AOT publish -> `camera_server_dotnet.exe`; fail on any AOT/trim warning.
  5. `astral-sh/setup-uv` + `uv sync --locked --no-group desktop` (same as
     the `python` job), then
     `uv run --no-sync pytest tests/test_camera_conformance.py -q` with
     `PUSHNAV_CONFORMANCE_SERVER` set to the exe (Fake backend).
  6. Upload the exe as an artifact (7-day retention).
- Runs on every PR like the other jobs, with no `paths` filter: the
  workflow has no filters today, and the job is cheap. If it gets slow,
  add a `dorny/paths-filter` step later. Don't use workflow-level `paths`,
  because it would skip the Python and web jobs too.
- The existing `python` job (windows) runs the conformance suite against
  the Python mock only, since `PUSHNAV_CONFORMANCE_SERVER` is unset there.
- `build.yml` `build-windows` job: add `actions/setup-dotnet` (10.0.x)
  before "Build zip + installer", and a post-build check that
  `camera_server_dotnet.exe` is in the release directory. The existing
  MSVC step stays for good (needed by the C server and by the AOT linker).

---

## 11. Open Questions

| # | Question | Needs | Blocks approval? |
|---|---|---|---|
| Q1 | Is this UI-306xCP-M listed by IDS peak's uEye Transport Layer (IDS Software Suite 4.97 is installed)? Only matters if the uEye API path fails. Check the label/revision and, optionally, install IDS peak and see if `ids_peak_cockpit` lists it. | Sebastian + camera | No (fallback only) |
| Q2 | ~~uEye headers/manual are not installed (`develop/` has only `bin/`). Install the IDS SDK developer option (headers + manual) on the dev box, or accept constants derived from the API docs?~~ **Resolved in practice (tasks 5.1–5.5):** constants derived from the documented API (`UEyeConst.cs`) and verified on the camera. | — | — |
| Q3 | ~~Lens / focal length~~ **Resolved:** 25 mm Basler lens (reference configuration, §2). Follow-up [HW] checks (solve rate/time with the wide database, centroid parameters at ~48/97"/px) are tracked in `SPEC_CAMERA_CONTROLS.md` §9 Q6–Q8. | — | No |
| Q4 | Is q90 grayscale JPEG good enough for solving faint fields with the IMX174, or do we need FRAME_RAW soon? Compare solve rate on a real sky session (JPEG vs raw dump). | Sebastian + camera + sky | No |
| Q5 | ~~Max exposure to offer in the UI: the sensor allows 30 s; plate solving rarely needs > 2 s, and long frames make the UI sluggish. Proposed cap: 4 s, configurable.~~ **Resolved 2026-11-18 (Sebastian, `SPEC_CAMERA_CONTROLS.md` §9 Q2): 4 s.** The IDS and Fake backends cap at 4 s (`MaxExposureUs`); the engine's `camera.max_exposure_us` can lower it further. | — | — |
| Q6 | ~~Do the OV9281 modules support `KSPROPERTY_CAMERACONTROL_EXTENDED_EXPOSUREMODE` (µs exposure) under the inbox UVC driver, or only log2 steps?~~ **Dropped 2026-11-18:** no OV9281 available (Q10), and task 8 is not planned. | — | — |
| Q7 | ~~Does the uEye driver release the camera after `TerminateProcess` without `is_ExitCamera` (Python's `process.terminate()` on Windows)? Verify by kill + respawn.~~ **Resolved (task 5.8): yes.** `taskkill /F` on a streaming server, three times in a row on a UI-306xCP-M (driver 4.97): the process was gone 0.3 s later and every respawn opened the camera and streamed (3.7 s from start to listening). The engine's own recovery after a kill (RECONNECTING, then SETUP) took about 6 s. One caveat: a kill that lands while the server is already closing the camera can leave the process in "terminating" for several seconds (the driver call has to return first), so a respawn in that window would be expected to find the camera busy (not tested); the engine's backoff would cover it. | camera | No |
| Q8 | ~~OK to runtime-load the user-installed, proprietary `ueye_api_64.dll` from GPL-3 code (no redistribution)?~~ **Resolved 2026-11-18: yes, documented per §3.4 (task 6.4).** | Sebastian | — |
| Q9 | ~~Is the WIC JPEG encoder within the 15 ms budget for 1936x1216 GRAY8? If not, switch to static libjpeg-turbo.~~ **Resolved 2026-11-18 (task 4.3): yes, median 3.6 ms at 1936x1216 and 0.95 ms at 968x608 (Release, dev laptop); no fallback needed.** | measurement | No |
| Q10 | ~~**[HW: OV9281]** With the default `"dotnet"`, how much later does an OV9281 user get frames than with `"c"`?~~ **Resolved 2026-11-18 (Sebastian): the IDS camera is the only camera; nothing else is hardware-tested.** The new features target the C# server only; with an upstream server they are not available and not tested (`SPEC_CAMERA_CONTROLS.md` §1.2). An OV9281 user still works via the code-3 fallback (pytest-covered, task 6.2), just after the C# server's device wait; the fast exit (6.2a) is not planned. | — | — |
| Q11 | ~~Should the optional C# UVC backend ever become the default for OV9281?~~ **Dropped 2026-11-18:** task 8 is not planned (no UVC camera to test with, Q10). The C server stays the UVC path. | — | — |
| Q12 | Add the server choice to the Settings UI (task 6.7)? Proposed: optional, later. | Sebastian | No |
| Q13 | ~~Longest exposure a v1 client may set: the sensor allows 30 s, the engine's v1 stall timeout is a fixed 2.0 s.~~ **Resolved (review of tasks 1-4): the v1 view caps exposure at 1.5 s and clamps SET_CONTROL to it (`V1ControlAdapter`). v2 sessions are not capped (adaptive stall timeout, protocol §11.7).** | — | No |

---

## 12. Implementation Tasks

Dependency: tasks 1–6 do not depend on #3. Task 8 is optional and has no deadline. Task 7 (v2 controls in the
C# server) lands in the slices of `SPEC_CAMERA_CONTROLS.md` §10.8 (the
protocol v2 section was approved in PR #8). **[HW]** = needs Sebastian + the real
camera. **[INFRA]** = route to the infra agent.

### 1. Skeleton
- [x] 1.1 Create `camera/windows-dotnet/` solution, `global.json`, `Directory.Build.props` (net10.0-windows, AOT, trim/AOT warnings as errors). Test: `dotnet build` clean. *(Done: the `Microsoft.DotNet.ILCompiler` pack is pinned to 10.0.15 via `KnownILCompilerPack`, because SDK 10.0.301 bundles 10.0.9, which NuGet audit flags and warnings are errors.)*
- [x] 1.2 `Program.cs` with `--port/--backend/--list/--log-level`, exit codes 0/1/3. Test: xUnit for arg parsing; `--backend fake --list` prints JSON. *(Done: bad command line exits with 2, `--port 0` picks a free port.)*
- [x] 1.3 AOT publish script `build.bat`/`build.ps1` -> `publish/camera_server_dotnet.exe`. Test: exe runs `--list` on a clean machine without the .NET runtime. *(Done: `build.bat` wraps `build.ps1`; exe is 3.3 MB and imports only OS DLLs.)*

### 2. Protocol v1 core
- [x] 2.1 Envelope + message types + System.Text.Json source-gen models. Test: xUnit round trips; > 16 MB rejected.
- [x] 2.2 Single-client TCP server, HELLO exchange, CONTROL_INFO after handshake and after every SET_CONTROL, GET_CONTROLS, unknown types ignored, exit after disconnect. Test: xUnit with a loopback client.
- [x] 2.3 Latest-frame slot + writer (no queue). Test: slow-reader test shows no backlog (only newest frame delivered).

### 3. Fake backend + conformance
- [x] 3.1 `FakeBackend`: replays `--fake-images` (PNG -> GRAY8 -> JPEG) or a synthetic star field; simulated exposure/gain/fps/binning with IDS-like ranges. Test: xUnit.
- [x] 3.2 `tests/test_camera_conformance.py` (v1 vectors) parametrized over the Python mock and `PUSHNAV_CONFORMANCE_SERVER`. Test: passes locally against the mock and the C# exe. *(Done: 24 passed against the mock and the published exe; 9 passed + 6 exe-only skipped with the mock alone.)*
- [x] 3.3 Engine smoke: run the app with `PUSHNAV_CAMERA_SERVER=dotnet` and the fake backend (dev flag), solver solves `tests/samples` frames. Test: manual + log check. *(Done: the dev flag is the environment variable `PUSHNAV_CAMERA_SERVER_ARGS` (extra arguments for the C# server only, e.g. `--backend fake --fake-images tests/samples`). With the fake backend at its default exposure/gain the engine's sync solve found 6 candidates and tracking solved 131 of 131 frames (5 distinct sky positions, 12 to 27 matches, 0 failures); the log shows `Camera server 'dotnet' ... backend=windows-dotnet-fake`.)*

### 4. Imaging
- [x] 4.1 WIC JPEG encoder (GRAY8, quality param) via CsWin32 COM, AOT-clean. Test: xUnit round trip error bound.
- [x] 4.2 YUY2/NV12 -> GRAY8 conversion. Test: xUnit with known patterns.
- [x] 4.3 Encoder benchmark (1936x1216 and 968x608). Test: reported numbers meet §3.3 or open the libjpeg-turbo fallback (Q9). *(Done, within budget: WIC q90 median 3.6 ms at 1936x1216 and 0.95 ms at 968x608 in Release, so no libjpeg-turbo fallback; Q9 answered.)*

### 5. IDS backend
- [x] 5.1 `UEyeNative` `[LibraryImport]` declarations + `NativeLibrary.TryLoad`; backend unavailable if DLL missing. Test: xUnit on a machine without the DLL (CI) -> `Probe()` returns []. *(Done: `Backends/Ids/UEyeNative.cs`, cdecl, blittable only, `[assembly: DisableRuntimeMarshalling]`; the DLL is loaded from System32 first, then the default search path; `TryLoad` of a missing name returns false and a backend without the API probes to `[]`.)*
- [x] 5.2 `IUEyeApi` seam + fake; pixel-clock policy, exposure priority, long-exposure switch, timeout -> fatal. Test: xUnit. *(Done: `IUEyeApi`, `UEyeApi` (real), `FakeUEyeApi` (tests, modelled on the measured camera), `IdsTimingPolicy`. Long-exposure mode is sticky, see §5.6.)*
- [x] 5.3 Probe/open: camera list, Mono8, auto features off, buffers, image queue, capture. Test **[HW]**: `--backend ids --list` shows `UI306xCP-M`, 1936x1216, controls. *(Done and verified on the camera: `ids:1409:3060`, 1936x1216, controls `exposure` 92 to 4 000 000 us, `gain` 0 to 100, `fps` 1 to 19.6. Binning is not offered until task 7.2.)*
- [x] 5.4 Capture loop + JPEG frames over TCP (v1). Test **[HW]**: conformance suite against the real camera; app shows live image. *(Done: conformance suite 24 passed + 1 skipped (fake-only) against the real camera with `PUSHNAV_CONFORMANCE_BACKEND=ids`; the engine received live frames, 1936x1216 at 10 fps.)*
- [x] 5.5 v1 controls (`exposure` in `"100us"`, `gain` raw 0–100). Test **[HW]**: sliders change brightness; CONTROL_INFO reflects applied values. *(Note: the v1 view caps `exposure` at 1.5 s = 15000 units, `V1ControlAdapter.MaxExposureMicroseconds`, and clamps SET_CONTROL to it, because the engine's fixed 2.0 s stall timeout would otherwise restart the server; protocol v2 lifts it, see Q13. Already implemented and tested in the adapter.)* *(Done: brightness follows exposure and gain on the real camera; the "sliders" were driven through the engine's `/api/control`.)*
- [x] 5.6 Device removal -> exit(1); engine recovers when re-plugged. Test **[HW]**: unplug/replug during streaming. *(Implemented: removal events plus the capture loop's error and timeout handling, all covered by xUnit with the fake API. Still open: the real unplug/replug, which needs a person at the camera.)* *(Done: manual hardware test by Sebastian, 2026-11-24: unplug/replug during streaming, the engine recovered.)*
- [x] 5.7 Exit code 3 when no supported camera is present. Exit 3 immediately when only an allowlisted UVC camera is seen and UVC is disabled (§8.3; needs the MF enumeration from task 6.2a). Test: xUnit (fake device lists) + manual without camera. *(Done for "no camera": xUnit with an empty camera list and with no API at all. The "immediate exit for a UVC camera" half stays with 6.2a. The manual run without the camera is part of the 5.6 steps for Sebastian, since the camera cannot be unplugged remotely.)* *(Done: manual start without a camera, Sebastian, 2026-11-24.)*
- [x] 5.8 Kill/respawn without `is_ExitCamera` (Q7). Test **[HW]**: `taskkill /F` then restart, camera opens. *(Done: see Q7.)*
- [x] 5.9 Night test: plate solve with the IDS camera (25 mm lens), at least one target tracked. Needs the wide solver database and per-profile FOV from `SPEC_CAMERA_CONTROLS.md` slice S1 (§10.8). Test **[HW]** + sky. *(Done: manual night test by Sebastian, 2026-11-24: plate solve with the IDS camera and the 25 mm lens, at least one target tracked. The measurements of `SPEC_CAMERA_CONTROLS.md` task 6.3 (solve rate and time, Q6 to Q8, wide-field sample frames) are not part of this and stay open there.)*

### 6. Engine, build, packaging
- [x] 6.1 New `evf/camera/server_select.py`: `camera.windows_server` (`"dotnet"` default, or `"c"`), `PUSHNAV_CAMERA_SERVER` override, warning on invalid values, missing-exe fallback in both directions, ignored on macOS/Linux. Plus one config default line in `config/manager.py`. Test: new `tests/test_server_select.py` (dev/release layouts, both settings, env override, each exe missing, neither present, non-Windows). *(Done. The dev layout is `camera/windows-dotnet/publish/camera_server_dotnet.exe` (the `build.bat` output) and `camera/windows/camera_server.exe`; the release layout has both next to the app exe. The layout comes from one small helper, `evf.paths.windows_camera_server_paths()` (marked `# fork:`; §8.5 allows "at most one small helper" in `paths.py`). Dev only: `PUSHNAV_CAMERA_SERVER_ARGS` passes extra arguments to the C# server, e.g. `--backend fake --fake-images tests/samples` (task 3.3).)*
- [x] 6.2 `SubprocessManager` hooks (marked `# fork:`):
  - spawn via `server_select`;
  - exit-code-3 fallback only for `"dotnet"`, within one attempt;
  - remember the working server for recovery;
  - kill both exe names.

  Test: pytest with two fake server scripts (one exits 3); `"c"` never starts the C# script; existing `tests/test_subprocess_mgr.py` unchanged and green. *(Done: `tests/test_subprocess_mgr_select.py`. The fallback restarts the connect deadline, because the C# server may have used up most of it with its 8 s device wait before exiting with code 3. An explicit `binary_path` bypasses the selection.)*
- [ ] 6.2a *(Not planned, Q10: no UVC camera to test with; the code-3 fallback after the device wait stays.)* MF device enumeration in the C# server (symbolic links, VID/PID, allowlist incl. `PUSHNAV_CAMERA_IDS`), used by the fast exit in §8.3.
- [x] 6.3 `build_windows.bat` Phase 1a (skips with a warning when `dotnet` is missing) + copy into the release dir (`pushnav.iss` needs no change). Test: local build with and without the .NET SDK on `PATH`; with it, the zip/installer contains both exes.
- [x] 6.4 Docs: `docs/hardware.md` IDS section (install IDS Software Suite, supported model, licensing note), a `README.md` "Third-party software" note on the proprietary, separately installed IDS runtime that is loaded at runtime and never bundled (§3.4), a `SPEC_BUILD_RELEASE.md` §6.1 line, and a `CLAUDE.md` build line. Test: review.
- [x] 6.5 **[INFRA]** Add job `camera-server-windows` to `.github/workflows/test.yml` (xUnit, AOT publish, conformance against the exe, artifact; §10.3). Test: job green on the PR; existing `python`/`web` jobs unchanged.
- [x] 6.6 **[INFRA]** `build.yml`: add setup-dotnet to `build-windows` and a check that `camera_server_dotnet.exe` is in the release dir. Test: `workflow_dispatch` with platform=windows produces the installer with both exes. *(Done in YAML; the `workflow_dispatch` build is green: run 45158886304, all four platforms.)*
- [ ] 6.7 *(Optional, later; Q12)* Settings UI select "Camera server: C# (default) / C" that writes `camera.windows_server` and triggers "Retry camera". Test: vitest + manual.
- [x] 6.8 Upstream-merge check before the PR: `git diff upstream/main -- camera/windows/` is empty, and every edited upstream file is in the §8.5 table. *(Done 2026-11-24 against upstream/main 4f563af and origin/main a240755: `git diff upstream/main origin/main -- camera/windows/` is empty. Of the #1 files, `subprocess_mgr.py`, `config/manager.py`, `paths.py`, `build_windows.bat`, `build.yml`, `docs/hardware.md`, `CLAUDE.md` and `SPEC_BUILD_RELEASE.md` are edited upstream files and in the table; `server_select.py` and the tests are new files. Two gaps were fixed in the table: `README.md` ("Third-party software" section, task 6.4) was missing, and the `paths.py` row now names the helper `windows_camera_server_paths()` (task 6.1). `test.yml` is not an upstream file: the fork added it in 699ba8c, before #1. The other upstream files that differ belong to #3 (listed in `SPEC_CAMERA_CONTROLS.md` §1.2) or to fork work outside these specs.)*

### 7. Protocol v2 in the server (#3 approved in PR #8)

From 2026-11-24 these tasks are tracked under issue #3 (#1 is closed):
`SPEC_CAMERA_CONTROLS.md` §10.8 schedules 7.0 in S1 (done), 7.1 in S5 and
7.2 in S6; 7.3 is optional and not scheduled. The checkboxes stay here and
are ticked with the slice (7.1 in S5, 7.2 in S6).

- [x] 7.0 HELLO extras in **every** session, no version change: `camera_key` (`DeviceInfo.CameraKey`, e.g. `ids:1409:3060`), `camera_serial` when known, `capabilities.sensor` (`width`, `height`, `pixel_size_um`, `mono`; IDS from the sensor info, 5.86 µm on the UI-306xCP-M; Fake: the IDS values). No `max_protocol_version` yet. A v1 client ignores unknown HELLO keys (protocol §11.2), so this is safe before v2 exists, and it fixes the engine's profile key early (`SPEC_CAMERA_CONTROLS.md` §4.2). Test: xUnit on the HELLO JSON; conformance v1 suite unchanged and green against the exe. *(Done in S1b: `HelloMessage` gained `camera_key` (`DeviceInfo.CameraKey`), `camera_serial` and `capabilities.sensor`; `ICameraBackend.Sensor` (default null = no capabilities) is implemented by the IDS backend (`is_GetSensorInfo`: width, height, pixel pitch nm -> µm, mono) and the fake backend (IDS values, whatever the replayed images measure). `protocol_version` stays 1 and there is no `max_protocol_version`. xUnit: `Server/HelloExtrasTests.cs`; `tests/test_camera_conformance.py` has one exe-only check of the new keys, and the v1 suite is green against the mock and the Native AOT exe.)*
- [x] 7.1 v2 handshake and session (Fake backend first): HELLO `max_protocol_version: 2`, `capabilities.frame_formats` / `stream_info: true`; accept a client `protocol_version` of 1 or 2 (`ProtocolInfo` gets min/max; anything else stays an ERROR). In a v2 session: CONTROL_INFO with the backend's native descriptors (`exposure` in µs up to the backend cap of 4 s, **no** 1.5 s v1 cap; `fps`; `gain` raw; `binning` where supported), CONTROL_INFO with all ranges after every SET_CONTROL, STREAM_INFO (generation 1) after the first CONTROL_INFO, STREAM_INFO again (same generation) when `frame_interval_us` changes, and `reconfiguring` → `streaming` (generation + 1) around a binning change before the first frame of the new size. v1 sessions stay exactly as today (`V1ControlAdapter`). Test: xUnit (session, Fake binning); `tests/test_camera_conformance.py` v2 vectors 1–5 against the exe with `--backend fake`. *(Done in S5: the session speaks through `IProtocolDialect` (`Server/`): `V1Dialect` is the old behaviour (`V1ControlAdapter`), `V2Dialect` the native controls, CONTROL_INFO after every SET_CONTROL and STREAM_INFO, kept by `StreamAnnouncer` (input: `StreamTiming`; which changes restart the stream is `ICameraBackend.RestartsStream`, a default member S6 can override); the frame writer only sends frames that match the announced geometry, so none of the new size precedes its STREAM_INFO. Every backend gets v2: the IDS backend already advertises `exposure`, `gain` and `fps` natively (task 5.3), so an IDS v2 session has those three and no binning until 7.2. xUnit: `V2SessionTests` (Fake, over TCP), `V2DialectTests` (ordering and gating against a recording channel), `StreamAnnouncerTests`, `StreamTimingTests`, `IdsServerTests` (v2 over `FakeUEyeApi`); the v2 vectors run against the Native AOT exe (`--backend fake`) and are green (vector 6 is skipped: GRAY8 is not advertised) (task 7.3). Decisions: `SPEC_CAMERA_CONTROLS.md` §10.8, S5 status.)*
- [x] 7.2 IDS v2: binning through `is_SetBinning` (options from `IS_GET_SUPPORTED_BINNING`) with the in-process restart of §5.5 and STREAM_INFO; `fps` advertised in v2; coupling (exposure priority) reported in CONTROL_INFO; long-exposure mode in v2 sessions per `SPEC_CAMERA_CONTROLS.md` §9 Q12. Test: xUnit with `FakeUEyeApi` (binning restart < 500 ms budget, STREAM_INFO order, long-mode exit wrapped in `reconfiguring`); **[HW]** conformance suite with `PUSHNAV_CONFORMANCE_BACKEND=ids` incl. v2 vectors; binning 2x2 shows 968x608 in the app. *(Done in S6, apart from the **[HW]** runs: `IdsBackend` advertises `binning` (`IdsBinning`: factors from `IS_GET_SUPPORTED_BINNING`, mode bits read from the installed uEye .NET interface) and restarts the stream in-process (§5.5 "As built"), with the frame timing measured again at the new binning, so CONTROL_INFO afterwards carries the new `fps` and exposure ranges; exposure priority is unchanged and keeps working through a restart (exposure and preferred fps survive it). `fps` was already advertised by S5. Q12: in a v2 session leaving long-exposure mode is a restart wrapped in `reconfiguring` → `streaming` with generation + 1 at the same size, after which `fps` is settable again (§5.6); every restart (this one and a binning change) waits (at most 2 s, then a warning) for the first frame after it before `streaming` goes out, so the gap after the switch falls inside `reconfiguring`; this is the contract of `SetControl` for a restarting change. The 500 ms is a budget for the fake API; on the real camera the target is the protocol's 2 s (§5.5). The v2 dialect passes `ControlChange.MayRestartStream`, the v1 dialect never does, so v1 stays byte-for-byte as before and keeps the sticky mode. The nits of the S5 reviews: `ICameraBackend.RestartsStream` is now `WouldRestartStream(ControlChange)`, `BackendControls.Set` and `SetRestartingStream` replace the bool flag. xUnit: `IdsBackendTests` (binning section: order of the API calls, frames of the new size, the < 500 ms budget on `FakeUEyeApi`, timing measured again, refusal without the permission or in long mode, a refused mode, a failing restart; long-mode exit section), `IdsBinningTests`, `IdsServerTests` (v2 binning and long-mode exit over TCP with the STREAM_INFO order; v1 never sees binning and stays sticky), `V2DialectTests` (the flag), `FakeBackendTests`. Conformance: the vector-4 skip for `PUSHNAV_CONFORMANCE_BACKEND=ids` is gone, vector 6 stays skipped. Still open: the **[HW]** runs, listed in the S6 status of `SPEC_CAMERA_CONTROLS.md` §10.8.)*
- [ ] 7.3 Optional FRAME_RAW support (capability only, off unless requested). Test: conformance v2 vector 6.

### 8. UVC backend (Media Foundation): not planned (Q10, Q11)

Kept for reference only. Sebastian has no UVC camera to test with, and the
C server stays the UVC path.

- [ ] 8.1 `UvcBackend` on top of the enumeration from 6.2a, behind `--enable-uvc` / `camera.dotnet_uvc`; `--list` shows UVC devices. Test **[HW: OV9281]**: lists the module like the C server's diagnostic.
- [ ] 8.2 Source reader MJPG passthrough at 1280x720, lowest fps; YUY2 fallback encoded. Test **[HW: OV9281]**.
- [ ] 8.3 Exposure/gain via `IAMCameraControl`/`IAMVideoProcAmp` (v1 units), auto exposure off. Test **[HW: OV9281]**.
- [ ] 8.4 v2 exposure in µs (extended control or enum of log2 steps, Q6), fps enum. Test **[HW: OV9281]**.
- [ ] 8.5 Device loss -> exit(1). Test **[HW: OV9281]** unplug.
- [ ] 8.6 Done-criteria check (§6) on each OV9281 module with `camera.dotnet_uvc = true`. **[HW]**, Sebastian. The C server is not removed or changed either way.
