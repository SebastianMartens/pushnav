// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Diagnostics;
using System.Globalization;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>
/// IDS uEye cameras through the uEye API (spec 5; first target: UI-306xCP-M, USB 1409:3060, mono, 1936x1216).
/// The camera runs Mono8 with all auto features off; frames are copied out of the driver's image queue, encoded as
/// grayscale JPEG and pushed to the sink. All hardware access goes through <see cref="IUEyeApi"/>, so the policy
/// (pixel clock, exposure priority, long exposure, timeouts, removal) is testable without a camera.
/// </summary>
/// <remarks>
/// Controls advertised in native units: <c>exposure</c> (µs), <c>gain</c> (master, 0..100) and <c>fps</c>. Binning is a v2
/// feature (spec task 7.2) and is not offered yet. A protocol v1 client sees exposure in <c>"100us"</c> and gain only
/// (<see cref="V1ControlAdapter"/>).
/// </remarks>
internal sealed class IdsBackend : ICameraBackend
{
    public const string BackendId = "ids";

    public const double DefaultExposureUs = 50_000;
    public const int DefaultGain = 0;
    public const double DefaultFpsValue = 10;

    /// <summary>Longest exposure the backend offers (spec Q5, proposed default); the sensor itself allows 30 s.</summary>
    public const double MaxExposureUs = 4_000_000;

    /// <summary>Highest frame rate the fps control offers; the sensor's fastest pixel clock could do more than the USB link and the JPEG path are meant for.</summary>
    public const double MaxAdvertisedFps = 60;

    /// <summary>Image buffers in the driver's ring (spec 5.1).</summary>
    public const int BufferCount = 4;

    /// <summary>Capture waits in slices of this length so <see cref="Stop"/> and shutdown never wait for a long exposure.</summary>
    private const int WaitSliceMilliseconds = 250;

    /// <summary>Frame rate and exposure the camera is brought to before long-exposure mode is switched on (see ApplyTimingAndExposure).</summary>
    private const double EnterLongFps = 10;

    private const double EnterLongQuickExposureMs = 10;

    /// <summary>Consecutive empty waits (or errors) before the camera counts as dead (spec 5.2).</summary>
    public const int MaxConsecutiveFailures = 3;

    private static readonly uint[] RemovalEvents = [UEyeConst.EventRemove, UEyeConst.EventDeviceUnplugged];

    private readonly IUEyeApi? _api;
    private readonly int _jpegQuality;
    private readonly Logger _log;

    private readonly object _ctl = new();    // serializes open/close and every hardware change
    private readonly object _state = new();  // guards the values the capture thread and Controls read

    // -- hardware state (mutated under _ctl) -------------------------------------------------------------
    private int _camera;
    private DeviceInfo? _device;
    private UEyeSensorInfo _sensor = new(string.Empty, false, 0, 0, false, false, 0);
    private int _width;
    private int _height;
    private List<PixelClockTiming> _clocks = [];
    private PixelClockTiming _clock;
    private double _appliedFps;          // last frame rate requested from the camera (0 = unknown, force)
    private double _actualFps;           // what the camera answered for it
    private double _exposureMaxMs;       // normal-mode exposure maximum at the applied clock / fps
    private double _normalMaxAtFloorMs;  // normal-mode exposure maximum at the lowest clock and the fps floor
    private bool _longSupported;
    private double _longMaxMs;
    private bool _longEnabled;
    private double _minExposureUs = 92;
    private double _maxExposureUs = MaxExposureUs;
    private readonly List<ImageBuffer> _buffers = [];
    private readonly List<uint> _registeredEvents = [];

    // -- state published to other threads (under _state) -----------------------------------------------
    private double _exposureUs = DefaultExposureUs;
    private int _gain = DefaultGain;
    private double _preferredFps = DefaultFpsValue;
    private double _effectiveFps = DefaultFpsValue;
    private bool _longActive;

    // -- threads -----------------------------------------------------------------------------------------
    private IFrameSink? _sink;
    private Thread? _captureThread;
    private Thread? _watcherThread;
    private volatile bool _captureStop;
    private volatile bool _watcherStop;
    private int _fatalRaised;
    private long _framesProduced;

    private readonly record struct ImageBuffer(nint Memory, int Id, int Pitch);

    public IdsBackend(IUEyeApi? api, int jpegQuality, Logger log)
    {
        _api = api;
        _jpegQuality = jpegQuality;
        _log = log;
    }

    /// <summary>The backend on the real uEye API; without the IDS Software Suite it has no API and <see cref="Probe"/> returns nothing.</summary>
    public static IdsBackend CreateDefault(int jpegQuality, Logger log)
    {
        var api = UEyeApi.TryCreate();
        if (api == null)
        {
            log.Debug("uEye API (ueye_api_64.dll) not found; IDS cameras are not available");
        }
        else if (log.DebugEnabled)
        {
            log.Debug($"uEye API {UEyeApi.DescribeLibraryVersion() ?? "?"} loaded");
        }

        return new IdsBackend(api, jpegQuality, log);
    }

    /// <summary>Shortest wait before an empty wait counts as a timeout (spec 5.2: 1 s; tests shorten it).</summary>
    internal int MinimumFrameTimeoutMilliseconds { get; init; } = IdsTimingPolicy.DefaultMinimumTimeoutMilliseconds;

    public string Id => BackendId;

    public string HelloName => "windows-dotnet-ids";

    public event Action<string>? Fatal;

    public DeviceInfo Device => _device ?? throw new InvalidOperationException("Backend not opened");

    public int DefaultFps => (int)Math.Round(DefaultFpsValue);

    /// <summary>Frames produced since <see cref="Start"/> (tests, diagnostics).</summary>
    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    public StreamGeometry Geometry
    {
        get
        {
            if (_width == 0)
            {
                throw new InvalidOperationException("Backend not opened");
            }

            return new StreamGeometry(_width, _height, 1, PixelFormat.Gray8);
        }
    }

    public IReadOnlyList<ControlDescriptor> Controls
    {
        get
        {
            lock (_state)
            {
                return BuildControls();
            }
        }
    }

    // -- probe / open ---------------------------------------------------------------------------------------

    public IReadOnlyList<DeviceInfo> Probe()
    {
        if (_api == null)
        {
            return [];
        }

        try
        {
            int rc = _api.GetCameraList(out var cameras);
            if (rc != UEyeConst.Success || cameras.Count == 0)
            {
                return [];
            }

            var devices = new List<DeviceInfo>(cameras.Count);
            foreach (var camera in cameras.OrderBy(c => c.DeviceId))
            {
                var (vid, pid) = IdsQuirks.UsbIdsFor(camera.Model);
                devices.Add(new DeviceInfo(
                    BackendId,
                    camera.Model,
                    Serial: camera.Serial,
                    Vid: vid,
                    Pid: pid,
                    Handle: camera.DeviceId.ToString(CultureInfo.InvariantCulture)));
            }

            return devices;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Probe must never throw (e.g. an older IDS runtime without one of the functions).
            _log.Debug($"IDS probe failed: {ex.Message}");
            return [];
        }
    }

    public void Open(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var api = _api ?? throw new CameraException("The IDS Software Suite (ueye_api_64.dll) is not installed.");
        if (!int.TryParse(device.Handle, NumberStyles.None, CultureInfo.InvariantCulture, out int deviceId))
        {
            throw new CameraException($"Not an IDS device: '{device.Handle}'");
        }

        lock (_ctl)
        {
            CloseCameraLocked();

            int rc = api.InitCamera(deviceId, out int camera);
            if (rc != UEyeConst.Success)
            {
                string hint = rc is UEyeConst.AllDevicesBusy or UEyeConst.DeviceInUse or UEyeConst.DeviceBusy or UEyeConst.CantOpenDevice
                    ? " Is another program (for example uEye Cockpit) using the camera?"
                    : string.Empty;
                throw new CameraException($"Could not open {device.Model}: {UEyeConst.StatusText(rc)} (is_InitCamera {rc}).{hint}");
            }

            _camera = camera;
            try
            {
                OpenLocked(device);
            }
            catch
            {
                CloseCameraLocked();
                throw;
            }
        }
    }

    private void OpenLocked(DeviceInfo device)
    {
        var api = _api!;
        Check(api.SetColorModeMono8(_camera), "set Mono8");
        Check(api.SetDisplayModeDib(_camera), "set display mode");

        // Spec 5.1 / protocol 6.1: the IDS equivalent of "force auto exposure OFF". Sensor-side variants are not
        // supported by every sensor (the UI-306 answers NOT_SUPPORTED); that is fine, the switch is simply not there.
        foreach (int parameter in new[]
        {
            UEyeConst.SetEnableAutoGain,
            UEyeConst.SetEnableAutoShutter,
            UEyeConst.SetEnableAutoFramerate,
            UEyeConst.SetEnableAutoSensorGain,
            UEyeConst.SetEnableAutoSensorShutter,
            UEyeConst.SetEnableAutoSensorGainShutter,
            UEyeConst.SetEnableAutoSensorFramerate,
        })
        {
            int rc = api.SetAutoParameter(_camera, parameter, 0);
            if (rc != UEyeConst.Success && rc != UEyeConst.NotSupported)
            {
                _log.Debug($"Auto feature 0x{parameter:X} could not be switched off: {UEyeConst.StatusText(rc)}");
            }
        }

        api.DisableBinning(_camera); // 1x1; a camera without binning answers NOT_SUPPORTED, which is fine

        Check(api.GetSensorInfo(_camera, out _sensor), "read sensor info");
        Check(api.GetImageSize(_camera, out _width, out _height), "read image size");
        if (_width <= 0 || _height <= 0)
        {
            throw new CameraException($"The camera reported an invalid image size {_width}x{_height}");
        }

        BuildClockTableLocked();
        MeasureExposureLimitsLocked();

        lock (_state)
        {
            _exposureUs = DefaultExposureUs;
            _gain = DefaultGain;
            _preferredFps = DefaultFpsValue;
            _longActive = false;
        }

        Check(api.SetMasterGain(_camera, DefaultGain), "set gain");
        ApplyTimingAndExposureLocked(DefaultFpsValue, DefaultExposureUs);
        RefreshGainLocked();

        AllocateBuffersLocked();
        StartRemovalWatcherLocked();

        _device = device;
        _log.Info(
            $"IDS camera opened: {device.Model} (serial {device.Serial ?? "?"}), sensor {_sensor.Name}, {_width}x{_height}, " +
            $"{(_sensor.IsMonochrome ? "mono" : "color")}, pixel clocks {string.Join('/', _clocks.Select(c => c.Megahertz))} MHz, " +
            $"clock {_clock.Megahertz} MHz, {_effectiveFps:0.##} fps");
    }

    private void BuildClockTableLocked()
    {
        var api = _api!;
        _clocks = [];
        int rc = api.GetPixelClockList(_camera, out var list);
        if (rc != UEyeConst.Success || list.Count == 0)
        {
            // No selectable clock: work with the one the camera runs at.
            Check(api.GetPixelClock(_camera, out uint current), "read pixel clock");
            list = [current];
        }

        foreach (uint mhz in list.Order())
        {
            if (api.SetPixelClock(_camera, mhz) != UEyeConst.Success)
            {
                continue;
            }

            if (api.GetFrameTimeRange(_camera, out double minFrame, out double maxFrame, out _) != UEyeConst.Success
                || api.GetExposureRange(_camera, out double minExposure, out _, out _) != UEyeConst.Success
                || minFrame <= 0 || maxFrame < minFrame)
            {
                continue;
            }

            _clocks.Add(new PixelClockTiming(mhz, minFrame, maxFrame, minExposure));
        }

        if (_clocks.Count == 0)
        {
            throw new CameraException("The camera did not report usable frame timing for any pixel clock.");
        }

        _clock = _clocks[^1]; // the last one set; ApplyTimingAndExposure moves to the policy's choice
        _appliedFps = 0;
        _longEnabled = false;
    }

    /// <summary>Learns where long-exposure mode begins and how long it goes, at the lowest clock (the one with the longest exposures).</summary>
    private void MeasureExposureLimitsLocked()
    {
        var api = _api!;
        SetClockAndFpsLocked(_clocks[0], IdsTimingPolicy.FloorFps);
        _normalMaxAtFloorMs = _exposureMaxMs;
        _longSupported = false;
        _longMaxMs = 0;

        // The long range can be read without switching the mode on (switching costs a frame time, and leaving it more).
        if (api.SupportsLongExposure(_camera))
        {
            _longSupported = api.GetLongExposureRange(_camera, out _, out _longMaxMs, out _) == UEyeConst.Success
                             && _longMaxMs > _normalMaxAtFloorMs;
        }

        double minUs = Math.Ceiling(_clocks.Max(c => c.MinExposureMs) * 1000.0);
        double maxMs = _longSupported ? _longMaxMs : _normalMaxAtFloorMs;
        _minExposureUs = minUs;
        _maxExposureUs = Math.Max(minUs, Math.Min(MaxExposureUs, Math.Floor(maxMs * 1000.0)));
    }

    private void AllocateBuffersLocked()
    {
        var api = _api!;
        for (int i = 0; i < BufferCount; i++)
        {
            Check(api.AllocImageMemory(_camera, _width, _height, out nint memory, out int id), "allocate image memory");
            Check(api.GetImageMemoryPitch(_camera, memory, id, out int pitch), "read image pitch");
            _buffers.Add(new ImageBuffer(memory, id, pitch > 0 ? pitch : _width));
            Check(api.AddToSequence(_camera, memory, id), "add buffer to the sequence");
        }

        Check(api.InitImageQueue(_camera), "start the image queue");
    }

    // -- controls -------------------------------------------------------------------------------------------

    public ControlResult SetControl(string id, double value)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_ctl)
        {
            if (_camera == 0)
            {
                return ControlResult.Rejected("camera not open");
            }

            ControlDescriptor? control;
            lock (_state)
            {
                control = BuildControls().FirstOrDefault(c => c.Id == id);
            }

            if (control == null)
            {
                return ControlResult.Rejected($"unknown control '{id}'");
            }

            if (!control.TryCoerce(value, out double coerced))
            {
                return ControlResult.Rejected($"value {value} not accepted for '{id}'");
            }

            var timer = Stopwatch.StartNew();
            switch (id)
            {
                case ControlIds.Exposure:
                    ApplyTimingAndExposureLocked(CurrentPreferredFps(), coerced);
                    break;

                case ControlIds.Gain:
                    Check(_api!.SetMasterGain(_camera, (int)coerced), "set gain");
                    RefreshGainLocked();
                    break;

                case ControlIds.Fps:
                    lock (_state)
                    {
                        _preferredFps = coerced;
                    }

                    ApplyTimingAndExposureLocked(coerced, CurrentExposureUs());
                    break;
            }

            _log.Debug($"SET {id} = {coerced:0.###} took {timer.ElapsedMilliseconds} ms");
            return ControlResult.Ok;
        }
    }

    private double CurrentPreferredFps()
    {
        lock (_state)
        {
            return _preferredFps;
        }
    }

    private double CurrentExposureUs()
    {
        lock (_state)
        {
            return _exposureUs;
        }
    }

    private List<ControlDescriptor> BuildControls()
    {
        double maxFps = IdsTimingPolicy.MaxFpsFor(_exposureUs, Math.Min(MaxAdvertisedFps, _clocks.Count > 0 ? _clocks[^1].MaxFps : IdsTimingPolicy.FloorFps));
        return
        [
            new ControlDescriptor
            {
                Id = ControlIds.Exposure,
                Label = "Exposure",
                Kind = ControlKind.Int,
                Unit = "us",
                Min = _minExposureUs,
                Max = _maxExposureUs,
                Step = 1,
                Cur = Math.Clamp(Math.Round(_exposureUs), _minExposureUs, _maxExposureUs),
                Scale = "log",
            },
            new ControlDescriptor
            {
                Id = ControlIds.Gain,
                Label = "Gain",
                Kind = ControlKind.Int,
                Unit = "raw",
                Min = 0,
                Max = 100,
                Step = 1,
                Cur = Math.Clamp(_gain, 0, 100),
            },
            new ControlDescriptor
            {
                Id = ControlIds.Fps,
                Label = "Frame rate",
                Kind = ControlKind.Float,
                Unit = "fps",
                Min = IdsTimingPolicy.FloorFps,
                Max = maxFps,
                Step = 0.01,
                Cur = Math.Clamp(Math.Round(_effectiveFps, 2), IdsTimingPolicy.FloorFps, maxFps),
            },
        ];
    }

    /// <summary>
    /// Applies the fps / pixel clock / long-exposure policy for the wanted exposure and sets it (spec 5.4): exposure has
    /// priority, so a longer exposure lowers the fps (and the pixel clock) instead of being clamped, and one that does not fit
    /// at the fps floor turns long-exposure mode on. Updates the published state with what the camera really applied.
    /// </summary>
    private void ApplyTimingAndExposureLocked(double preferredFps, double exposureUs)
    {
        var api = _api!;
        double exposureMs = exposureUs / 1000.0;
        // Long-exposure mode is sticky: once on, it stays on until the camera is closed. Switching it on is cheap (see
        // below), but leaving it blocks the API call for about a second and then delivers no frame for another second
        // (measured on the UI-306xCP-M: a 2.5 s gap), and the v1 engine restarts the camera server after 2.0 s without a
        // frame. Inside long mode a shorter exposure is just a cheap SetExposure, and frames keep coming at the frame
        // rate the camera had when the mode went on (10 fps at a 100 ms exposure, measured).
        bool wantLong = _longEnabled || (_longSupported && IdsTimingPolicy.NeedsLongExposure(exposureMs, _normalMaxAtFloorMs));
        double effectiveFps;

        if (wantLong)
        {
            if (!_longEnabled)
            {
                // Switching the mode on takes about one frame time of the timing the camera has at that moment: 1.1 s
                // at 1 fps, 0.2 s at 10 fps (measured). So after a long normal-mode exposure (slow frames) first get
                // back to a short exposure and a fast frame rate, then switch; the long exposure itself is set after.
                if (_appliedFps < EnterLongFps - 1e-6)
                {
                    double quickMs = Math.Min(EnterLongQuickExposureMs, exposureMs);
                    Check(api.SetExposure(_camera, ref quickMs), "shorten the exposure");
                    SetClockAndFpsLocked(IdsTimingPolicy.ChooseClock(_clocks, EnterLongFps), EnterLongFps);
                }

                Check(api.SetLongExposureEnabled(_camera, true), "enable long exposure");
                _longEnabled = true;
            }

            // The frame rate is not regulated in long mode: a frame takes the exposure, but not less than the clock's frame time.
            effectiveFps = 1000.0 / Math.Max(exposureMs, _clock.MinFrameTimeSeconds * 1000.0);
        }
        else
        {
            double target = IdsTimingPolicy.TargetFps(preferredFps, exposureUs);
            for (int attempt = 0; ; attempt++)
            {
                var clock = IdsTimingPolicy.ChooseClock(_clocks, target);
                if (_appliedFps == 0 || Math.Abs(target - _appliedFps) > 1e-6 || clock.Megahertz != _clock.Megahertz)
                {
                    SetClockAndFpsLocked(clock, target);
                }

                if (exposureMs <= _exposureMaxMs + 1e-6 || target <= IdsTimingPolicy.FloorFps || attempt >= 3)
                {
                    break;
                }

                // The headroom guess was too small for this camera: lower the fps to what the real range allows.
                target = Math.Max(IdsTimingPolicy.FloorFps, target * (_exposureMaxMs / exposureMs) * 0.99);
            }

            effectiveFps = _actualFps;
        }

        double applied = exposureMs;
        Check(api.SetExposure(_camera, ref applied), "set exposure");

        lock (_state)
        {
            _exposureUs = applied * 1000.0;
            _effectiveFps = effectiveFps;
            _longActive = _longEnabled;
        }

        if (_log.DebugEnabled)
        {
            _log.Debug($"Exposure {exposureUs:0} us -> {applied * 1000.0:0} us, {effectiveFps:0.##} fps, {_clock.Megahertz} MHz, long exposure {(_longEnabled ? "on" : "off")}");
        }
    }

    /// <summary>Switches to <paramref name="clock"/> (if not already there), sets the frame rate and re-reads the exposure range, which depends on both.</summary>
    private void SetClockAndFpsLocked(PixelClockTiming clock, double fps)
    {
        var api = _api!;
        if (clock.Megahertz != _clock.Megahertz)
        {
            Check(api.SetPixelClock(_camera, clock.Megahertz), $"set pixel clock {clock.Megahertz} MHz");
            _clock = clock;
        }

        Check(api.SetFrameRate(_camera, fps, out double actual), "set frame rate");
        Check(api.GetExposureRange(_camera, out _, out _exposureMaxMs, out _), "read exposure range");
        _appliedFps = fps;
        _actualFps = actual > 0 ? actual : fps;
    }

    private void RefreshGainLocked()
    {
        if (_api!.GetMasterGain(_camera, out int gain) == UEyeConst.Success)
        {
            lock (_state)
            {
                _gain = gain;
            }
        }
    }

    // -- capture -----------------------------------------------------------------------------------------------

    public void Start(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_ctl)
        {
            if (_camera == 0)
            {
                throw new InvalidOperationException("Backend not opened");
            }

            if (_captureThread != null)
            {
                throw new InvalidOperationException("Already started");
            }

            Check(_api!.StartCapture(_camera), "start capture");
            _sink = sink;
            _captureStop = false;
            var thread = new Thread(CaptureLoop) { Name = "ids-capture", IsBackground = true };
            _captureThread = thread;
            thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_ctl)
        {
            _captureStop = true;
            thread = _captureThread;
            _captureThread = null;
        }

        if (thread == null)
        {
            return;
        }

        thread.Join(TimeSpan.FromSeconds(3)); // the capture loop waits in short slices

        lock (_ctl)
        {
            if (_camera != 0 && !FatalRaised)
            {
                _api!.StopCapture(_camera);
            }
        }
    }

    private bool FatalRaised => Volatile.Read(ref _fatalRaised) != 0;

    private void CaptureLoop()
    {
        var api = _api!;
        int camera = _camera;
        try
        {
            using var encoder = new WicJpegEncoder();
            var gray = new byte[_width * _height];
            var jpeg = new byte[WicJpegEncoder.MaxEncodedSize(_width, _height)];
            var geometry = Geometry;
            int failures = 0;

            while (!_captureStop)
            {
                int timeoutMs;
                lock (_state)
                {
                    timeoutMs = IdsTimingPolicy.FrameTimeoutMilliseconds(_effectiveFps, _exposureUs / 1000.0, _longActive, MinimumFrameTimeoutMilliseconds);
                }

                var waited = Stopwatch.StartNew();
                int rc;
                nint memory;
                int memoryId;
                do
                {
                    int slice = (int)Math.Min(WaitSliceMilliseconds, Math.Max(1, timeoutMs - waited.ElapsedMilliseconds));
                    rc = api.WaitForNextImage(camera, slice, out memory, out memoryId);
                }
                while (rc == UEyeConst.TimedOut && !_captureStop && waited.ElapsedMilliseconds < timeoutMs);

                if (_captureStop)
                {
                    if (rc == UEyeConst.Success)
                    {
                        api.UnlockBuffer(camera, memory);
                    }

                    return;
                }

                if (rc != UEyeConst.Success)
                {
                    if (IsDeviceGone(rc))
                    {
                        RaiseFatal($"Camera lost ({UEyeConst.StatusText(rc)})");
                        return;
                    }

                    if (++failures >= MaxConsecutiveFailures)
                    {
                        RaiseFatal(rc == UEyeConst.TimedOut
                            ? $"No frame from the camera for {failures} consecutive waits (last wait {timeoutMs} ms)"
                            : $"Camera capture failed: {UEyeConst.StatusText(rc)} ({rc})");
                        return;
                    }

                    if (rc != UEyeConst.TimedOut)
                    {
                        Thread.Sleep(50); // an error is not a wait: do not spin on it
                    }

                    continue;
                }

                failures = 0;
                int pitch = PitchOf(memoryId);
                try
                {
                    CopyRows(memory, pitch, gray, _width, _height);
                }
                finally
                {
                    api.UnlockBuffer(camera, memory);
                }

                int n = encoder.Encode(gray, _width, _height, _width, _jpegQuality, jpeg);
                if (_captureStop)
                {
                    return;
                }

                _sink!.OnFrame(jpeg.AsSpan(0, n), FrameKind.Jpeg, geometry);
                Interlocked.Increment(ref _framesProduced);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_captureStop)
            {
                RaiseFatal($"IDS capture failed: {ex.Message}");
            }
        }
    }

    private int PitchOf(int memoryId)
    {
        foreach (var buffer in _buffers)
        {
            if (buffer.Id == memoryId)
            {
                return buffer.Pitch;
            }
        }

        return _width;
    }

    private static unsafe void CopyRows(nint source, int pitch, byte[] destination, int width, int height)
    {
        var src = (byte*)source;
        fixed (byte* dst = destination)
        {
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(src + ((long)y * pitch), dst + ((long)y * width), width, width);
            }
        }
    }

    private static bool IsDeviceGone(int rc) =>
        rc is UEyeConst.InvalidHandle or UEyeConst.IoRequestFailed or UEyeConst.CantCommunicateWithDriver
            or UEyeConst.CameraNotConnected or UEyeConst.NoSuchDevice;

    private void RaiseFatal(string message)
    {
        if (Interlocked.Exchange(ref _fatalRaised, 1) != 0)
        {
            return;
        }

        _captureStop = true;
        _watcherStop = true;
        Fatal?.Invoke(message);
    }

    // -- device removal ----------------------------------------------------------------------------------

    private void StartRemovalWatcherLocked()
    {
        var api = _api!;
        _registeredEvents.Clear();
        foreach (uint eventId in RemovalEvents)
        {
            if (api.InitEvent(_camera, eventId) == UEyeConst.Success)
            {
                _registeredEvents.Add(eventId);
            }
        }

        if (_registeredEvents.Count == 0)
        {
            _log.Warn("Device-removal events are not available; an unplugged camera is detected through missing frames only");
            return;
        }

        if (api.InitEvent(_camera, UEyeConst.EventUserDefinedBegin) == UEyeConst.Success)
        {
            _registeredEvents.Add(UEyeConst.EventUserDefinedBegin); // wakes the watcher at shutdown
        }

        _watcherStop = false;
        int camera = _camera;
        var events = _registeredEvents.ToArray();
        var thread = new Thread(() => WatchRemoval(camera, events)) { Name = "ids-removal-watch", IsBackground = true };
        _watcherThread = thread;
        thread.Start();
    }

    private void WatchRemoval(int camera, uint[] events)
    {
        var api = _api!;
        int errors = 0;
        try
        {
            while (!_watcherStop)
            {
                int rc = api.WaitEvents(camera, events, 1000, out uint signaled);
                if (_watcherStop)
                {
                    return;
                }

                if (rc == UEyeConst.Success)
                {
                    errors = 0;
                    if (signaled is UEyeConst.EventRemove or UEyeConst.EventDeviceUnplugged)
                    {
                        RaiseFatal("Camera was unplugged (device removal event)");
                        return;
                    }

                    if (signaled == UEyeConst.EventUserDefinedBegin)
                    {
                        return;
                    }
                }
                else if (rc == UEyeConst.TimedOut)
                {
                    errors = 0;
                }
                else if (IsDeviceGone(rc))
                {
                    RaiseFatal($"Camera lost ({UEyeConst.StatusText(rc)})");
                    return;
                }
                else if (++errors >= 3)
                {
                    _log.Warn($"Device-removal watch stopped after repeated errors ({UEyeConst.StatusText(rc)}); relying on the capture loop");
                    return;
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn($"Device-removal watch failed: {ex.Message}");
        }
    }

    // -- close ------------------------------------------------------------------------------------------------

    public void Dispose()
    {
        Stop();
        lock (_ctl)
        {
            CloseCameraLocked();
        }
    }

    /// <summary>
    /// Releases the camera: stop capture, free the buffers, <c>is_ExitCamera</c>. Runs on a helper thread with a time limit,
    /// so a driver call that hangs on a vanished device cannot keep the process from exiting.
    /// </summary>
    private void CloseCameraLocked()
    {
        int camera = _camera;
        if (camera == 0)
        {
            return;
        }

        _camera = 0;
        _watcherStop = true;
        var api = _api!;
        var buffers = _buffers.ToArray();
        var events = _registeredEvents.ToArray();
        var watcher = _watcherThread;
        _buffers.Clear();
        _registeredEvents.Clear();
        _watcherThread = null;
        _device = null;
        _width = _height = 0;
        _longEnabled = false;

        var teardown = new Thread(() =>
        {
            try
            {
                if (events.Length > 0 && !FatalRaised)
                {
                    api.SetEvent(camera, UEyeConst.EventUserDefinedBegin); // wake the removal watcher
                }

                if (!FatalRaised)
                {
                    watcher?.Join(TimeSpan.FromSeconds(2));
                }

                api.StopCapture(camera);
                api.ExitImageQueue(camera);
                api.ClearSequence(camera);
                foreach (var buffer in buffers)
                {
                    api.FreeImageMemory(camera, buffer.Memory, buffer.Id);
                }

                foreach (uint eventId in events)
                {
                    api.ExitEvent(camera, eventId);
                }

                api.ExitCamera(camera);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn($"Closing the camera failed: {ex.Message}");
            }
        })
        {
            Name = "ids-close",
            IsBackground = true,
        };
        teardown.Start();
        if (!teardown.Join(TimeSpan.FromSeconds(FatalRaised ? 1.5 : 4)))
        {
            _log.Warn("The uEye driver did not finish closing the camera in time; giving up on it");
        }
    }

    /// <summary>Raises <see cref="UEyeConst"/>-based failures as <see cref="CameraException"/>; device loss also ends the server.</summary>
    private void Check(int rc, string what)
    {
        if (rc == UEyeConst.Success)
        {
            return;
        }

        string detail = _camera != 0 ? _api!.DescribeLastError(_camera) : UEyeConst.StatusText(rc);
        if (IsDeviceGone(rc))
        {
            RaiseFatal($"Camera lost while trying to {what} ({UEyeConst.StatusText(rc)})");
        }

        throw new CameraException($"IDS camera: could not {what}: {UEyeConst.StatusText(rc)} (code {rc}; {detail})");
    }
}
