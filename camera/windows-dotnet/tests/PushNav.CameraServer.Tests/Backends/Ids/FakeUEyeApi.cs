// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Runtime.InteropServices;
using PushNav.CameraServer.Backends.Ids;

namespace PushNav.CameraServer.Tests.Backends.Ids;

/// <summary>
/// A simulated uEye API with the timing behaviour measured on a UI-306xCP-M: five pixel clocks, frame times and exposure
/// ranges that follow the clock, silent clamping of out-of-range exposures, long-exposure mode, image buffers with a
/// row pitch wider than the image, events and scripted failures. Every call that changes something is logged in <see cref="Calls"/>.
/// </summary>
internal sealed unsafe class FakeUEyeApi : IUEyeApi, IDisposable
{
    public const int Width = 64;
    public const int Height = 48;
    public const int Pitch = 80;     // wider than Width: the backend must honour the pitch
    public const int CameraHandle = 7;
    public const int DeviceId = 3;

    // clock MHz -> (min frame time ms, min exposure ms, long exposure max ms), measured on the real camera
    private static readonly (uint Mhz, double MinFrameMs, double MinExposureMs, double LongMaxMs)[] ClockTable =
    [
        (30, 97.609, 0.0916, 30000.0),
        (59, 48.804, 0.0526, 30000.0),
        (118, 24.402, 0.0332, 20404.5),
        (237, 12.201, 0.0235, 10202.3),
        (474, 6.185, 0.0187, 5172.0),
    ];

    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly Dictionary<int, nint> _memory = [];
    private readonly Queue<int> _waitScript = new();
    private readonly Queue<uint> _pendingEvents = new();
    private readonly HashSet<uint> _events = [];
    private int _nextMemoryId = 100;
    private int _clockIndex = 3;       // 237 MHz, like the camera's default
    private int _binning = 1;          // N of NxN
    private double _fps = 25;
    private double _exposureMs = 10;
    private bool _long;
    private int _gain;
    private int _rotation;
    private bool _open;
    private byte _fill = 80;

    public List<UEyeCameraEntry> Cameras { get; } =
    [
        new UEyeCameraEntry(CameraId: 1, DeviceId: DeviceId, SensorId: 538, InUse: false, Serial: "4102825084", Model: "UI306xCP-M"),
    ];

    /// <summary>Status <c>InitCamera</c> fails with (0 = succeed).</summary>
    public int InitStatus { get; set; }

    /// <summary>Status <c>GetSensorInfo</c> fails with (0 = succeed).</summary>
    public int SensorInfoStatus { get; set; }

    public bool LongExposureSupported { get; set; } = true;

    /// <summary>What <c>IS_GET_SUPPORTED_BINNING</c> answers: 2x2 by default (vertical and horizontal 2x).</summary>
    public int SupportedBinningModes { get; set; } = UEyeConst.BinningVertical2x | UEyeConst.BinningHorizontal2x;

    /// <summary>Status <c>SetBinning</c> fails with (0 = succeed); the binning stays as it was.</summary>
    public int SetBinningStatus { get; set; }

    /// <summary>
    /// While set and not signalled, <c>WaitForNextImage</c> delivers no frame (it waits out its timeout); <see cref="GateWaiters"/>
    /// counts the waits that found it closed. For tests that need to hold the first frame after a restart back.
    /// </summary>
    public ManualResetEventSlim? FrameGate { get; set; }

    private int _gateWaiters;

    public int GateWaiters => Volatile.Read(ref _gateWaiters);

    /// <summary>Status <c>SetLongExposureEnabled(false)</c> fails with (0 = succeed).</summary>
    public int LeaveLongExposureStatus { get; set; }

    /// <summary>Makes <c>GetCameraList</c> throw, like an older runtime missing an entry point.</summary>
    public bool ThrowOnGetCameraList { get; set; }

    /// <summary>Statuses to hand out from <see cref="WaitForNextImage"/> before it delivers frames again.</summary>
    public void ScriptWaitResults(params int[] statuses)
    {
        lock (_gate)
        {
            foreach (int s in statuses)
            {
                _waitScript.Enqueue(s);
            }
        }
    }

    /// <summary>From now on every wait ends with this status instead of a frame (0 = deliver frames).</summary>
    public int WaitStatus { get; set; }

    /// <summary>Status <c>SetExposure</c> fails with (0 = succeed).</summary>
    public int ExposureSetStatus { get; set; }

    /// <summary>Status <c>StartCapture</c> fails with (0 = succeed).</summary>
    public int StartCaptureStatus { get; set; }

    /// <summary>When set, <c>InitEvent</c> fails with this status.</summary>
    public int EventInitStatus { get; set; }

    /// <summary>Pixel value of the next frames.</summary>
    public byte FillValue
    {
        get => _fill;
        set => _fill = value;
    }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public int CallCount(string prefix)
    {
        lock (_gate)
        {
            return _calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    // -- simulated hardware state, for assertions --------------------------------------------------------

    public uint PixelClockMhz => ClockTable[_clockIndex].Mhz;

    public double FrameRate => _fps;

    public double ExposureMs => _exposureMs;

    public bool LongExposureEnabled => _long;

    /// <summary>N of the NxN binning the camera runs at.</summary>
    public int Binning => _binning;

    /// <summary>Width of the frames now (the sensor width divided by the binning).</summary>
    public int CurrentWidth => Width / _binning;

    public int CurrentHeight => Height / _binning;

    public int Gain => _gain;

    public int OpenBuffers
    {
        get
        {
            lock (_gate)
            {
                return _memory.Count;
            }
        }
    }

    /// <summary>Makes <c>WaitEvents</c> report that the camera was removed.</summary>
    public void SignalRemoval() => PostEvent(UEyeConst.EventRemove);

    private void PostEvent(uint id)
    {
        lock (_gate)
        {
            _pendingEvents.Enqueue(id);
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (nint m in _memory.Values)
            {
                NativeMemory.Free((void*)m);
            }

            _memory.Clear();
        }
    }

    private void Log(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    // -- range model -------------------------------------------------------------------------------------

    private (uint Mhz, double MinFrameMs, double MinExposureMs, double LongMaxMs) Clock => ClockTable[_clockIndex];

    // A binned sensor reads out N times fewer rows (modelled, not measured), so its frames can be N times quicker.
    private double MinFrameMsNow => Clock.MinFrameMs / _binning;

    private double MaxFpsNow => 1000.0 / MinFrameMsNow;

    private double NormalExposureMaxMs => (1000.0 / _fps) - (0.75 * 30.0 / Clock.Mhz);

    // -- IUEyeApi ------------------------------------------------------------------------------------------

    public int GetCameraList(out IReadOnlyList<UEyeCameraEntry> cameras)
    {
        if (ThrowOnGetCameraList)
        {
            throw new EntryPointNotFoundException("is_GetCameraList");
        }

        lock (_gate)
        {
            cameras = Cameras.ToArray();
        }

        return UEyeConst.Success;
    }

    public int InitCamera(int deviceId, out int camera)
    {
        Log($"InitCamera {deviceId}");
        camera = 0;
        if (InitStatus != 0)
        {
            return InitStatus;
        }

        _open = true;
        camera = CameraHandle;
        return UEyeConst.Success;
    }

    public int ExitCamera(int camera)
    {
        Log("ExitCamera");
        _open = false;
        return UEyeConst.Success;
    }

    public bool IsOpen => _open;

    public string DescribeLastError(int camera) => "fake error text";

    public int GetSensorInfo(int camera, out UEyeSensorInfo info)
    {
        info = new UEyeSensorInfo("UI306xCP-M", true, Width, Height, true, true, 5860);
        return SensorInfoStatus;
    }

    public int SetColorModeMono8(int camera)
    {
        Log("SetColorModeMono8");
        return UEyeConst.Success;
    }

    public int SetDisplayModeDib(int camera)
    {
        Log("SetDisplayModeDib");
        return UEyeConst.Success;
    }

    public int SetAutoParameter(int camera, int parameter, double value)
    {
        Log($"SetAutoParameter 0x{parameter:X} {value}");
        // The sensor-side variants are not supported on this camera.
        return parameter >= UEyeConst.SetEnableAutoSensorGain ? UEyeConst.NotSupported : UEyeConst.Success;
    }

    public int GetSupportedBinning(int camera, out int modes)
    {
        modes = SupportedBinningModes;
        return UEyeConst.Success;
    }

    public int SetBinning(int camera, int mode)
    {
        Log($"SetBinning 0x{mode:X}");
        if (SetBinningStatus != 0)
        {
            return SetBinningStatus;
        }

        if (mode == UEyeConst.BinningDisable)
        {
            _binning = 1;
            _fps = Math.Clamp(_fps, 1.0, MaxFpsNow);
            return UEyeConst.Success;
        }

        foreach (int factor in new[] { 2, 3, 4, 5, 6, 8, 16 })
        {
            if (mode == IdsBinning.ModeFor(factor) && (SupportedBinningModes & mode) == mode)
            {
                _binning = factor;
                _fps = Math.Clamp(_fps, 1.0, MaxFpsNow);
                return UEyeConst.Success;
            }
        }

        return UEyeConst.NotSupported;
    }

    public int GetImageSize(int camera, out int width, out int height)
    {
        width = CurrentWidth;
        height = CurrentHeight;
        return UEyeConst.Success;
    }

    public int GetPixelClockList(int camera, out IReadOnlyList<uint> megahertz)
    {
        megahertz = ClockTable.Select(c => c.Mhz).ToArray();
        return UEyeConst.Success;
    }

    public int GetPixelClock(int camera, out uint megahertz)
    {
        megahertz = Clock.Mhz;
        return UEyeConst.Success;
    }

    public int SetPixelClock(int camera, uint megahertz)
    {
        Log($"SetPixelClock {megahertz}");
        int index = Array.FindIndex(ClockTable, c => c.Mhz == megahertz);
        if (index < 0)
        {
            return UEyeConst.InvalidParameter;
        }

        _clockIndex = index;
        _fps = Math.Clamp(_fps, 1.0, MaxFpsNow); // the frame rate follows into the new clock's range
        return UEyeConst.Success;
    }

    public int SetFrameRate(int camera, double fps, out double actual)
    {
        Log($"SetFrameRate {fps:0.###}");
        _fps = Math.Clamp(fps, 1.0, MaxFpsNow);
        actual = _fps;
        return UEyeConst.Success;
    }

    public int GetFrameTimeRange(int camera, out double min, out double max, out double increment)
    {
        min = MinFrameMsNow / 1000.0;
        max = 1.0;
        increment = 0.0001;
        return UEyeConst.Success;
    }

    public int GetExposureRange(int camera, out double minMs, out double maxMs, out double incrementMs)
    {
        minMs = Clock.MinExposureMs;
        maxMs = _long ? Clock.LongMaxMs : NormalExposureMaxMs;
        incrementMs = 0.01;
        return UEyeConst.Success;
    }

    public int GetLongExposureRange(int camera, out double minMs, out double maxMs, out double incrementMs)
    {
        minMs = Clock.MinExposureMs;
        maxMs = Clock.LongMaxMs;
        incrementMs = 0.01;
        return UEyeConst.Success;
    }

    public bool SupportsLongExposure(int camera) => LongExposureSupported;

    public int GetLongExposureEnabled(int camera, out bool enabled)
    {
        enabled = _long;
        return UEyeConst.Success;
    }

    public int SetLongExposureEnabled(int camera, bool enabled)
    {
        Log($"SetLongExposure {enabled}");
        if (!enabled && LeaveLongExposureStatus != 0)
        {
            return LeaveLongExposureStatus;
        }

        if (_long && !enabled)
        {
            _fps = 1.0; // leaving long mode drops back to the slowest frame rate (the caller has to re-apply its own)
        }

        _long = enabled;
        return UEyeConst.Success;
    }

    public int GetExposure(int camera, out double ms)
    {
        ms = _exposureMs;
        return UEyeConst.Success;
    }

    public int SetExposure(int camera, ref double ms)
    {
        Log($"SetExposure {ms:0.###}");
        if (ExposureSetStatus != 0)
        {
            return ExposureSetStatus;
        }

        GetExposureRange(camera, out double min, out double max, out _);
        _exposureMs = Math.Clamp(ms, min, max); // silently clamps, like the real camera
        ms = _exposureMs;
        return UEyeConst.Success;
    }

    public int SetMasterGain(int camera, int gain)
    {
        Log($"SetMasterGain {gain}");
        _gain = Math.Clamp(gain, 0, 100);
        return UEyeConst.Success;
    }

    public int GetMasterGain(int camera, out int gain)
    {
        gain = _gain;
        return UEyeConst.Success;
    }

    public int AllocImageMemory(int camera, int width, int height, out nint memory, out int memoryId)
    {
        Log("AllocImageMemory");
        memory = (nint)NativeMemory.AllocZeroed((nuint)(Pitch * height));
        lock (_gate)
        {
            memoryId = _nextMemoryId++;
            _memory[memoryId] = memory;
        }

        return UEyeConst.Success;
    }

    public int FreeImageMemory(int camera, nint memory, int memoryId)
    {
        Log("FreeImageMemory");
        lock (_gate)
        {
            if (_memory.Remove(memoryId, out nint m))
            {
                NativeMemory.Free((void*)m);
            }
        }

        return UEyeConst.Success;
    }

    public int GetImageMemoryPitch(int camera, nint memory, int memoryId, out int pitch)
    {
        pitch = Pitch;
        return UEyeConst.Success;
    }

    public int AddToSequence(int camera, nint memory, int memoryId)
    {
        Log("AddToSequence");
        return UEyeConst.Success;
    }

    public int ClearSequence(int camera)
    {
        Log("ClearSequence");
        return UEyeConst.Success;
    }

    public int InitImageQueue(int camera)
    {
        Log("InitImageQueue");
        return UEyeConst.Success;
    }

    public int ExitImageQueue(int camera)
    {
        Log("ExitImageQueue");
        return UEyeConst.Success;
    }

    public int WaitForNextImage(int camera, int timeoutMilliseconds, out nint memory, out int memoryId)
    {
        memory = 0;
        memoryId = 0;
        int scripted = 0;
        bool hasScripted;
        lock (_gate)
        {
            hasScripted = _waitScript.TryDequeue(out scripted);
        }

        var gate = FrameGate;
        if (!hasScripted && WaitStatus == 0 && gate is { IsSet: false })
        {
            Interlocked.Increment(ref _gateWaiters);
            if (!gate.Wait(Math.Max(1, timeoutMilliseconds)))
            {
                return UEyeConst.TimedOut;
            }
        }

        int status = hasScripted ? scripted : WaitStatus;
        if (status != 0)
        {
            Thread.Sleep(Math.Min(timeoutMilliseconds, 5));
            return status;
        }

        Thread.Sleep(2);
        lock (_gate)
        {
            if (_memory.Count == 0)
            {
                return UEyeConst.NoSuccess;
            }

            var ids = _memory.Keys.OrderBy(k => k).ToArray();
            memoryId = ids[_rotation++ % ids.Length];
            memory = _memory[memoryId];
        }

        // A frame with the image in the first Width bytes of each row and junk in the pitch padding.
        var rows = (byte*)memory;
        int width = CurrentWidth;
        for (int y = 0; y < CurrentHeight; y++)
        {
            new Span<byte>(rows + ((long)y * Pitch), width).Fill(_fill);
            new Span<byte>(rows + ((long)y * Pitch) + width, Pitch - width).Fill(255);
        }

        return UEyeConst.Success;
    }

    public int UnlockBuffer(int camera, nint memory)
    {
        return UEyeConst.Success;
    }

    public int StartCapture(int camera)
    {
        Log("StartCapture");
        return StartCaptureStatus;
    }

    public int StopCapture(int camera)
    {
        Log("StopCapture");
        return UEyeConst.Success;
    }

    public int InitEvent(int camera, uint eventId)
    {
        Log($"InitEvent {eventId}");
        if (EventInitStatus != 0)
        {
            return EventInitStatus;
        }

        lock (_gate)
        {
            _events.Add(eventId);
        }

        return UEyeConst.Success;
    }

    public int WaitEvents(int camera, IReadOnlyList<uint> events, int timeoutMilliseconds, out uint signaled)
    {
        signaled = 0;
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        lock (_gate)
        {
            while (true)
            {
                int count = _pendingEvents.Count;
                for (int i = 0; i < count; i++)
                {
                    uint id = _pendingEvents.Dequeue();
                    if (events.Contains(id))
                    {
                        signaled = id;
                        return UEyeConst.Success;
                    }
                }

                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return UEyeConst.TimedOut;
                }

                Monitor.Wait(_gate, (int)Math.Min(left, 50));
            }
        }
    }

    public int SetEvent(int camera, uint eventId)
    {
        Log($"SetEvent {eventId}");
        PostEvent(eventId);
        return UEyeConst.Success;
    }

    public int ExitEvent(int camera, uint eventId)
    {
        Log($"ExitEvent {eventId}");
        return UEyeConst.Success;
    }
}
