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
/// Controls advertised in native units: <c>exposure</c> (µs), <c>gain</c> (master, 0..100), <c>fps</c> and, where the sensor
/// offers it (<c>IS_GET_SUPPORTED_BINNING</c>), <c>binning</c>. A protocol v1 client sees exposure in <c>"100us"</c> and gain only
/// (<see cref="V1ControlAdapter"/>), so it never changes the binning.
/// <para>
/// Two changes restart the stream (spec 5.5, protocol 11.5), both inside the process and with the TCP connection open: a
/// <b>binning</b> change (new image buffers, new frame timing) and the <b>exit from long-exposure mode</b> when the exposure drops
/// back into the normal range. Both only for a caller that says it may restart (<see cref="ControlChange.MayRestartStream"/>, which
/// only a v2 session does): without that permission binning is refused and long-exposure mode is sticky (spec 5.6).
/// </para>
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
    private List<PixelClockTiming> _clocks = [];
    private PixelClockTiming _clock;
    private double _appliedFps;          // last frame rate requested from the camera (0 = unknown, force)
    private double _actualFps;           // what the camera answered for it
    private double _exposureMaxMs;       // normal-mode exposure maximum at the applied clock / fps
    private double _normalMaxAtFloorMs;  // normal-mode exposure maximum at the lowest clock and the fps floor
    private bool _longSupported;
    private double _longMaxMs;
    private bool _longEnabled;
    private IReadOnlyList<int> _binningFactors = [];   // NxN factors the sensor offers besides 1 (spec 5.3)
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
    private StreamGeometry _geometry;    // width, height, binning of the frames now; default (width 0) while closed. Replaced whole by PublishGeometryLocked

    // -- threads -----------------------------------------------------------------------------------------
    private IFrameSink? _sink;
    private Thread? _captureThread;
    private Thread? _watcherThread;
    private volatile bool _captureStop;
    private volatile bool _watcherStop;
    private int _fatalRaised;
    private long _framesProduced;
    private readonly ManualResetEventSlim _firstFrame = new(false); // set by the capture thread after each frame; reset before a restart

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

    /// <summary>How long a restart that gaps the stream (leaving long-exposure mode) waits for the first frame afterwards, so the gap falls inside <c>reconfiguring</c>.</summary>
    internal TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Shortest wait before an empty wait counts as a timeout (spec 5.2: 1 s; tests shorten it).</summary>
    internal int MinimumFrameTimeoutMilliseconds { get; init; } = IdsTimingPolicy.DefaultMinimumTimeoutMilliseconds;

    public string Id => BackendId;

    public string HelloName => "windows-dotnet-ids";

    public event Action<string>? Fatal;

    public DeviceInfo Device => _device ?? throw new InvalidOperationException("Backend not opened");

    public int DefaultFps => (int)Math.Round(DefaultFpsValue);

    /// <summary>The sensor facts the driver reported (<c>is_GetSensorInfo</c>); the pixel pitch is reported in nm.</summary>
    public SensorInfo? Sensor
    {
        get
        {
            var sensor = _sensor;
            return sensor.MaxWidth > 0 && sensor.MaxHeight > 0
                ? new SensorInfo(
                    sensor.MaxWidth,
                    sensor.MaxHeight,
                    sensor.PixelSizeNanometers > 0 ? sensor.PixelSizeNanometers / 1000.0 : null,
                    sensor.IsMonochrome)
                : null;
        }
    }

    /// <summary>Frames produced since <see cref="Start"/> (tests, diagnostics).</summary>
    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    public StreamGeometry Geometry
    {
        get
        {
            lock (_state)
            {
                return _geometry.Width == 0 ? throw new InvalidOperationException("Backend not opened") : _geometry;
            }
        }
    }

    private int CurrentBinning
    {
        get
        {
            lock (_state)
            {
                return _geometry.Binning;
            }
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
            if (rc == UEyeConst.StarterFirmwareUploadNeeded)
            {
                // The flag that would let is_InitCamera upload the firmware itself is deliberately never set: that writes to the camera.
                throw new CameraException($"Could not open {device.Model}: {UEyeConst.StatusText(rc)}.");
            }

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

        api.SetBinning(_camera, IdsBinning.ModeFor(1)); // 1x1; a camera without binning answers NOT_SUPPORTED, which is fine
        _binningFactors = api.GetSupportedBinning(_camera, out int supportedBinning) == UEyeConst.Success
            ? IdsBinning.FactorsIn(supportedBinning)
            : [];

        Check(api.GetSensorInfo(_camera, out _sensor), "read sensor info");
        var (width, height) = ReadImageSizeLocked("opening the camera");
        PublishGeometryLocked(new StreamGeometry(width, height, 1, PixelFormat.Gray8));

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
            $"IDS camera opened: {device.Model} (serial {device.Serial ?? "?"}), sensor {_sensor.Name}, {_geometry.Width}x{_geometry.Height}, " +
            $"{(_sensor.IsMonochrome ? "mono" : "color")}, pixel clocks {string.Join('/', _clocks.Select(c => c.Megahertz))} MHz, " +
            $"clock {_clock.Megahertz} MHz, {_effectiveFps:0.##} fps, " +
            $"binning {(_binningFactors.Count == 0 ? "none" : string.Join('/', _binningFactors.Select(f => $"{f}x{f}")))}");
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

    /// <summary>The current image size from the driver (the area of interest, which follows the binning). A size of 0 is a failure.</summary>
    private (int Width, int Height) ReadImageSizeLocked(string when)
    {
        Check(_api!.GetImageSize(_camera, out int width, out int height), "read image size");
        if (width <= 0 || height <= 0)
        {
            throw new CameraException($"The camera reported an invalid image size {width}x{height} after {when}");
        }

        return (width, height);
    }

    private void PublishGeometryLocked(StreamGeometry geometry)
    {
        lock (_state)
        {
            _geometry = geometry;
        }
    }

    private void AllocateBuffersLocked()
    {
        var api = _api!;
        var geometry = _geometry;
        for (int i = 0; i < BufferCount; i++)
        {
            Check(api.AllocImageMemory(_camera, geometry.Width, geometry.Height, out nint memory, out int id), "allocate image memory");
            Check(api.GetImageMemoryPitch(_camera, memory, id, out int pitch), "read image pitch");
            _buffers.Add(new ImageBuffer(memory, id, pitch > 0 ? pitch : geometry.Width));
            Check(api.AddToSequence(_camera, memory, id), "add buffer to the sequence");
        }

        Check(api.InitImageQueue(_camera), "start the image queue");
    }

    // -- controls -------------------------------------------------------------------------------------------

    public ControlResult SetControl(ControlChange change)
    {
        var (id, value, _) = change;
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

            if (id == ControlIds.Fps && _longEnabled)
            {
                return ControlResult.Rejected(
                    "the frame rate is fixed while long-exposure mode is on (a v2 session leaves the mode when the exposure is back in the normal range)");
            }

            if (control == null)
            {
                return ControlResult.Rejected($"unknown control '{id}'");
            }

            if (!control.TryCoerce(value, out double coerced))
            {
                return ControlResult.Rejected($"value {value} not accepted for '{id}'");
            }

            if (id == ControlIds.Binning && BinningRefusalLocked(change, coerced) is { } refusal)
            {
                return ControlResult.Rejected(refusal);
            }

            var timer = Stopwatch.StartNew();
            switch (id)
            {
                case ControlIds.Exposure when RestartKindLocked(change, coerced) == RestartKind.LeaveLongExposure:
                    LeaveLongExposureLocked(coerced);
                    break;

                case ControlIds.Exposure:
                    ApplyTimingAndExposureLocked(CurrentPreferredFps(), coerced);
                    break;

                case ControlIds.Binning:
                    if ((int)coerced == _geometry.Binning)
                    {
                        return ControlResult.Ok; // already there: nothing to restart
                    }

                    var result = ChangeBinningLocked((int)coerced);
                    _log.Debug($"SET {id} = {coerced:0.###} took {timer.ElapsedMilliseconds} ms");
                    return result;

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

    /// <summary>
    /// For a caller that may restart (<see cref="ControlChange.MayRestartStream"/>; a v1 session never does): a binning change to a
    /// factor the sensor offers restarts the stream, unless long-exposure mode is on (the backend refuses that, so there is nothing
    /// to announce), and so does an exposure that brings the camera out of long-exposure mode. Without it nothing restarts.
    /// </summary>
    public bool WouldRestartStream(ControlChange change)
    {
        lock (_ctl)
        {
            if (_camera == 0)
            {
                return false;
            }

            ControlDescriptor? control;
            lock (_state)
            {
                control = BuildControls().FirstOrDefault(c => c.Id == change.Id);
            }

            return control != null
                   && control.TryCoerce(change.Value, out double coerced)
                   && RestartKindLocked(change, coerced) != RestartKind.None;
        }
    }

    private enum RestartKind
    {
        None,
        Binning,
        LeaveLongExposure,
    }

    /// <summary>What <see cref="SetControl"/> does for a change whose value has already been coerced; the one rule behind <see cref="WouldRestartStream"/>.</summary>
    private RestartKind RestartKindLocked(ControlChange change, double coerced)
    {
        switch (change.Id)
        {
            case ControlIds.Binning:
                return (int)coerced != _geometry.Binning && BinningRefusalLocked(change, coerced) == null ? RestartKind.Binning : RestartKind.None;

            case ControlIds.Exposure:
                return change.MayRestartStream && _longEnabled && !IdsTimingPolicy.NeedsLongExposure(coerced / 1000.0, _normalMaxAtFloorMs)
                    ? RestartKind.LeaveLongExposure
                    : RestartKind.None;

            default:
                return RestartKind.None;
        }
    }

    /// <summary>
    /// Why a binning change to <paramref name="coerced"/> is refused, or null when it is allowed (or no change). Long-exposure mode comes
    /// first, because that is what the user can act on: the frame timing is measured in normal mode, and leaving the mode before a
    /// restart and entering it after would stretch it past what the engine waits.
    /// </summary>
    private string? BinningRefusalLocked(ControlChange change, double coerced)
    {
        if ((int)coerced == _geometry.Binning)
        {
            return null;
        }

        if (_longEnabled)
        {
            return "binning cannot change while long-exposure mode is on (lower the exposure to the normal range first)";
        }

        return change.MayRestartStream ? null : "binning restarts the stream, which this session cannot announce";
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

    /// <remarks>Binning is last, and the only control whose change restarts the stream; it moves the ranges of the others (a binned frame is quicker, so <c>fps</c> goes up).</remarks>
    private List<ControlDescriptor> BuildControls()
    {
        double maxFps = IdsTimingPolicy.MaxFpsFor(_exposureUs, Math.Min(MaxAdvertisedFps, _clocks.Count > 0 ? _clocks[^1].MaxFps : IdsTimingPolicy.FloorFps));
        List<ControlDescriptor> controls =
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
                // In long-exposure mode the camera does not regulate the frame rate (a frame lasts the exposure):
                // the control shows what it does and is not settable until the mode is left (v2 sessions do that when
                // the exposure is back in the normal range; a v1 session never does).
                Min = _longActive ? Math.Round(_effectiveFps, 2) : IdsTimingPolicy.FloorFps,
                Max = _longActive ? Math.Round(_effectiveFps, 2) : maxFps,
                Step = 0.01,
                Cur = _longActive ? Math.Round(_effectiveFps, 2) : Math.Clamp(Math.Round(_effectiveFps, 2), IdsTimingPolicy.FloorFps, maxFps),
            },
        ];
        if (IdsBinning.Descriptor(_binningFactors, _geometry.Binning) is { } binning)
        {
            controls.Add(binning);
        }

        return controls;
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
        // Long-exposure mode is left only by LeaveLongExposureLocked (a v2 session announces that restart). Here it is
        // sticky: switching it on is cheap (see below), but leaving it blocks the API call for about a second and then
        // delivers no frame for another second (measured on the UI-306xCP-M: a 2.5 s gap), and a v1 engine restarts the
        // camera server after 2.0 s without a frame. Inside long mode a shorter exposure is just a cheap SetExposure, and
        // frames keep coming at the frame rate the camera had when the mode went on (10 fps at a 100 ms exposure, measured).
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

    // -- stream restart (spec 5.5) ---------------------------------------------------------------------------

    /// <summary>
    /// Changes the binning: stop the capture, free the buffers, <c>is_SetBinning</c>, read the new image size, measure the frame
    /// timing again (a binned sensor reads out faster, so the pixel clock table and exposure limits change), apply the preferred
    /// frame rate and the exposure to it, allocate buffers for the new size and start the capture again. A camera that refuses the
    /// mode stays at its old binning and the stream is brought back as it was: a rejected result, not a failure.
    /// </summary>
    private ControlResult ChangeBinningLocked(int factor) =>
        RestartStreamLocked($"binning {factor}x{factor}", waitForFirstFrame: false, () =>
        {
            var api = _api!;
            FreeBuffersLocked();
            int binning = _geometry.Binning;
            var result = ControlResult.Ok;
            int rc = api.SetBinning(_camera, IdsBinning.ModeFor(factor));
            if (rc == UEyeConst.Success)
            {
                binning = factor;
            }
            else if (IsDeviceGone(rc))
            {
                Check(rc, "set binning");
            }
            else
            {
                result = ControlResult.Rejected($"the camera did not take binning {factor}x{factor}: {UEyeConst.StatusText(rc)} ({rc})");
            }

            var (width, height) = ReadImageSizeLocked("changing the binning"); // after a refusal it is the old size again
            PublishGeometryLocked(new StreamGeometry(width, height, binning, PixelFormat.Gray8));
            BuildClockTableLocked();
            MeasureExposureLimitsLocked();
            ApplyTimingAndExposureLocked(CurrentPreferredFps(), CurrentExposureUs());
            AllocateBuffersLocked();
            return result;
        });

    /// <summary>
    /// Leaves long-exposure mode for an exposure in the normal range, as a stream restart. On the real camera this blocks about a
    /// second and then delivers no frame for another second (a gap of about 2.5 s, spec 5.6), which is why only a caller that has
    /// announced the restart gets here. The camera then runs at its own frame rate again, so the preferred one is applied afresh.
    /// </summary>
    private void LeaveLongExposureLocked(double exposureUs) =>
        RestartStreamLocked("leaving long-exposure mode", waitForFirstFrame: true, () =>
        {
            Check(_api!.SetLongExposureEnabled(_camera, false), "leave long exposure");
            _longEnabled = false;
            _appliedFps = 0; // unknown: force the frame rate and pixel clock to be set again
            ApplyTimingAndExposureLocked(CurrentPreferredFps(), exposureUs);
            return ControlResult.Ok;
        });

    /// <summary>
    /// Runs <paramref name="change"/> with the capture stopped: the capture thread is joined first, so no frame of the old
    /// configuration is published once the restart is under way, and a new one starts afterwards (it reads the new image size).
    /// What <paramref name="change"/> returns is the result of the restart (a refused change that left the stream as it was).
    /// Failures are <see cref="CameraException"/>s; the caller, a v2 session, then ends with an ERROR (protocol 11.5).
    /// </summary>
    /// <param name="waitForFirstFrame">
    /// Return only once the first frame after the restart has been captured (at most <see cref="FirstFrameTimeout"/>; on timeout a
    /// warning, not a failure). The caller announces <c>streaming</c> when this returns, and a gap that follows it is judged by the
    /// engine's short stall timeout instead of the 5 s it grants while <c>reconfiguring</c>.
    /// </param>
    private ControlResult RestartStreamLocked(string what, bool waitForFirstFrame, Func<ControlResult> change)
    {
        var timer = Stopwatch.StartNew();
        bool capturing = StopCaptureThreadLocked();
        if (capturing)
        {
            _api!.StopCapture(_camera); // forced; its status says nothing the next call would not
        }

        var result = change();

        if (capturing && !FatalRaised) // a lost camera ends the server; do not start a thread on it
        {
            _firstFrame.Reset();
            Check(_api!.StartCapture(_camera), "restart capture");
            StartCaptureThreadLocked();
            if (waitForFirstFrame && !_firstFrame.Wait(FirstFrameTimeout))
            {
                _log.Warn($"No frame within {FirstFrameTimeout.TotalSeconds:0.#} s after {what}; announcing the stream anyway");
            }
        }

        var geometry = _geometry;
        _log.Info($"Stream restarted ({what}) in {timer.ElapsedMilliseconds} ms: {geometry.Width}x{geometry.Height}, {_effectiveFps:0.##} fps");
        return result;
    }

    /// <summary>Exits the image queue, clears the sequence and frees every buffer, in the order <see cref="CloseCameraLocked"/> uses.</summary>
    private void FreeBuffersLocked()
    {
        var api = _api!;
        api.ExitImageQueue(_camera);
        api.ClearSequence(_camera);
        foreach (var buffer in _buffers)
        {
            api.FreeImageMemory(_camera, buffer.Memory, buffer.Id);
        }

        _buffers.Clear();
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
            StartCaptureThreadLocked();
        }
    }

    private void StartCaptureThreadLocked()
    {
        _captureStop = false;
        var thread = new Thread(CaptureLoop) { Name = "ids-capture", IsBackground = true };
        _captureThread = thread;
        thread.Start();
    }

    /// <summary>Stops and joins the capture thread (false if none ran). It never takes <c>_ctl</c>, so joining under it is safe.</summary>
    private bool StopCaptureThreadLocked()
    {
        var thread = _captureThread;
        if (thread == null)
        {
            return false;
        }

        _captureStop = true;
        _captureThread = null;
        if (!thread.Join(TimeSpan.FromSeconds(3)))
        {
            // It would see the flag cleared by the next thread and carry on with the old geometry: never let that happen.
            throw new CameraException("The capture thread did not stop for the stream restart");
        }

        return true;
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
            var geometry = Geometry; // the geometry of this thread: a restart that changes it starts a new thread
            var gray = new byte[geometry.Width * geometry.Height];
            var jpeg = new byte[WicJpegEncoder.MaxEncodedSize(geometry.Width, geometry.Height)];
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
                int pitch = PitchOf(memoryId, geometry.Width);
                try
                {
                    CopyRows(memory, pitch, gray, geometry.Width, geometry.Height);
                }
                finally
                {
                    api.UnlockBuffer(camera, memory);
                }

                int n = encoder.Encode(gray, geometry.Width, geometry.Height, geometry.Width, _jpegQuality, jpeg);
                if (_captureStop)
                {
                    return;
                }

                _sink!.OnFrame(jpeg.AsSpan(0, n), FrameKind.Jpeg, geometry);
                Interlocked.Increment(ref _framesProduced);
                _firstFrame.Set();
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

    private int PitchOf(int memoryId, int width)
    {
        foreach (var buffer in _buffers)
        {
            if (buffer.Id == memoryId)
            {
                return buffer.Pitch;
            }
        }

        return width;
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
        PublishGeometryLocked(default);
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
