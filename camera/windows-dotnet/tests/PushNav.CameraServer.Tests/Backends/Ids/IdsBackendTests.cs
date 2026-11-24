// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Ids;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Imaging;

namespace PushNav.CameraServer.Tests.Backends.Ids;

public class IdsBackendTests : IDisposable
{
    private readonly FakeUEyeApi _api = new();
    private readonly List<IdsBackend> _backends = [];

    public void Dispose()
    {
        foreach (var backend in _backends)
        {
            backend.Dispose();
        }

        _api.Dispose();
    }

    private IdsBackend CreateBackend(bool open = true)
    {
        var backend = new IdsBackend(_api, 90, new Logger(TextWriter.Null, LogLevel.Debug)) { MinimumFrameTimeoutMilliseconds = 20 };
        _backends.Add(backend);
        if (open)
        {
            backend.Open(backend.Probe()[0]);
        }

        return backend;
    }

    /// <summary>A started backend with a long wait for the first frame after a restart (no assertion depends on it running out) and its log.</summary>
    private IdsBackend CreateBackendWithFirstFrameTimeout(TimeSpan timeout, int minimumFrameTimeoutMs, Logger log)
    {
        var backend = new IdsBackend(_api, 90, log) { MinimumFrameTimeoutMilliseconds = minimumFrameTimeoutMs, FirstFrameTimeout = timeout };
        _backends.Add(backend);
        backend.Open(backend.Probe()[0]);
        return backend;
    }

    private static ControlDescriptor Control(IdsBackend backend, string id) => backend.Controls.Single(c => c.Id == id);

    private sealed class CollectingSink : IFrameSink
    {
        private readonly object _gate = new();
        private readonly List<(byte[] Data, FrameKind Kind, StreamGeometry Geometry)> _frames = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _frames.Count;
                }
            }
        }

        public List<(byte[] Data, FrameKind Kind, StreamGeometry Geometry)> Snapshot()
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }

        public (byte[] Data, FrameKind Kind, StreamGeometry Geometry) First()
        {
            lock (_gate)
            {
                return _frames[0];
            }
        }

        public void OnFrame(ReadOnlySpan<byte> data, FrameKind kind, StreamGeometry geometry)
        {
            lock (_gate)
            {
                _frames.Add((data.ToArray(), kind, geometry));
            }
        }

        public bool WaitForFrames(int count, int timeoutMs = 5000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Count < count)
            {
                if (Environment.TickCount64 > deadline)
                {
                    return false;
                }

                Thread.Sleep(5);
            }

            return true;
        }
    }

    // -- probe -------------------------------------------------------------------------------------------

    [Fact]
    public void ProbeListsTheCamerasWithTheirStableKey()
    {
        var backend = CreateBackend(open: false);

        var device = Assert.Single(backend.Probe());

        Assert.Equal("ids", device.BackendId);
        Assert.Equal("UI306xCP-M", device.Model);
        Assert.Equal("4102825084", device.Serial);
        Assert.Equal("ids:1409:3060", device.CameraKey);
        Assert.Equal(FakeUEyeApi.DeviceId.ToString(), device.Handle);
    }

    [Fact]
    public void ProbeIsEmptyWithoutCameras()
    {
        _api.Cameras.Clear();

        Assert.Empty(CreateBackend(open: false).Probe());
    }

    [Fact]
    public void ProbeNeverThrowsEvenWhenTheRuntimeMisbehaves()
    {
        _api.ThrowOnGetCameraList = true;

        Assert.Empty(CreateBackend(open: false).Probe());
    }

    [Fact]
    public void AnUnknownModelIsKeyedByItsNameAndStillWorks()
    {
        _api.Cameras[0] = _api.Cameras[0] with { Model = "UI1220LE" };
        var backend = CreateBackend(open: false);

        var device = Assert.Single(backend.Probe());

        Assert.Equal("ids:UI1220LE", device.CameraKey);
        backend.Open(device);
        Assert.Equal(FakeUEyeApi.Width, backend.Geometry.Width);
    }

    // -- open ---------------------------------------------------------------------------------------------

    [Fact]
    public void OpenConfiguresMono8WithAutoFeaturesOffAndFullFrame()
    {
        var backend = CreateBackend();

        var calls = _api.Calls;
        Assert.Contains($"InitCamera {FakeUEyeApi.DeviceId}", calls);
        Assert.Contains("SetColorModeMono8", calls);
        Assert.Contains("SetDisplayModeDib", calls);
        Assert.Contains("SetBinning 0x0", calls);
        foreach (int parameter in new[] { UEyeConst.SetEnableAutoGain, UEyeConst.SetEnableAutoShutter, UEyeConst.SetEnableAutoFramerate })
        {
            Assert.Contains($"SetAutoParameter 0x{parameter:X} 0", calls);
        }

        Assert.Equal(new StreamGeometry(FakeUEyeApi.Width, FakeUEyeApi.Height, 1, PixelFormat.Gray8), backend.Geometry);
        Assert.Equal("UI306xCP-M", backend.Device.Model);
        Assert.Equal("windows-dotnet-ids", backend.HelloName);
        Assert.Equal(10, backend.DefaultFps);
    }

    [Fact]
    public void OpenAllocatesTheBufferRingAndTheImageQueue()
    {
        CreateBackend();

        Assert.Equal(IdsBackend.BufferCount, _api.CallCount("AllocImageMemory"));
        Assert.Equal(IdsBackend.BufferCount, _api.CallCount("AddToSequence"));
        Assert.Equal(1, _api.CallCount("InitImageQueue"));
        Assert.Equal(IdsBackend.BufferCount, _api.OpenBuffers);
    }

    [Fact]
    public void OpenStartsAtTheLowestPixelClockThatReachesTheDefaultFrameRate()
    {
        var backend = CreateBackend();

        Assert.Equal(30u, _api.PixelClockMhz);          // 30 MHz reaches 10.24 fps
        Assert.Equal(10.0, _api.FrameRate, precision: 3);
        Assert.Equal(50.0, _api.ExposureMs, precision: 3);
        Assert.False(_api.LongExposureEnabled);
        Assert.Equal(0, _api.Gain);
        Assert.Equal(50_000, Control(backend, ControlIds.Exposure).Cur);
    }

    [Fact]
    public void OpenFailureBecauseTheCameraIsBusyNamesTheLikelyCause()
    {
        _api.InitStatus = UEyeConst.DeviceInUse;
        var backend = CreateBackend(open: false);

        var ex = Assert.Throws<CameraException>(() => backend.Open(backend.Probe()[0]));

        Assert.Contains("another program", ex.Message);
        Assert.Equal(0, _api.CallCount("ExitCamera")); // nothing was opened
    }

    [Fact]
    public void ACameraThatNeedsAFirmwareUpdateSaysSoAndIsNeverUpdatedByPushNav()
    {
        _api.InitStatus = UEyeConst.StarterFirmwareUploadNeeded;
        var backend = CreateBackend(open: false);

        var ex = Assert.Throws<CameraException>(() => backend.Open(backend.Probe()[0]));

        Assert.Contains("firmware must be updated", ex.Message);
        Assert.Contains("IDS Camera Manager", ex.Message);
        Assert.DoesNotContain("is_InitCamera 184", ex.Message); // the message is the advice, not a bare code
        Assert.Equal(new[] { $"InitCamera {FakeUEyeApi.DeviceId}" }, _api.Calls); // nothing else was tried
    }

    [Fact]
    public void AFailureHalfwayThroughOpenReleasesTheCamera()
    {
        _api.SensorInfoStatus = UEyeConst.NoSuccess;
        var backend = CreateBackend(open: false);

        Assert.Throws<CameraException>(() => backend.Open(backend.Probe()[0]));

        Assert.Equal(1, _api.CallCount("ExitCamera"));
        Assert.False(_api.IsOpen);
    }

    [Fact]
    public void OpeningWithoutRemovalEventsStillWorks()
    {
        _api.EventInitStatus = UEyeConst.NotSupported;

        var backend = CreateBackend();

        Assert.Equal(FakeUEyeApi.Width, backend.Geometry.Width);
    }

    [Fact]
    public void ReopeningClosesTheFirstCamera()
    {
        var backend = CreateBackend();

        backend.Open(backend.Probe()[0]);

        Assert.Equal(1, _api.CallCount("ExitCamera"));
        Assert.Equal(IdsBackend.BufferCount, _api.OpenBuffers);
    }

    // -- controls -------------------------------------------------------------------------------------------

    [Fact]
    public void ControlsAreAdvertisedInNativeUnitsWithTheRealRanges()
    {
        var backend = CreateBackend();

        var exposure = Control(backend, ControlIds.Exposure);
        Assert.Equal("us", exposure.Unit);
        Assert.Equal(92, exposure.Min);               // the 30 MHz minimum, 91.6 us, rounded up
        Assert.Equal(4_000_000, exposure.Max);        // capped (spec Q5), the sensor itself offers 30 s
        var gain = Control(backend, ControlIds.Gain);
        Assert.Equal(0, gain.Min);
        Assert.Equal(100, gain.Max);
        var fps = Control(backend, ControlIds.Fps);
        Assert.Equal(1.0, fps.Min);
        Assert.InRange(fps.Max, 19.0, 20.0);          // 50 ms exposure + headroom: 1e6 / 51 000 = 19.6

        // binning comes from IS_GET_SUPPORTED_BINNING (the fake camera offers 2x2) and restarts the stream
        var binning = Control(backend, ControlIds.Binning);
        Assert.Equal(ControlKind.Enum, binning.Kind);
        Assert.True(binning.RestartsStream);
        Assert.Equal([1d, 2d], binning.Options!.Select(o => o.Value));
        Assert.Equal(["1x1", "2x2"], binning.Options!.Select(o => o.Label));
        Assert.Equal(1, binning.Cur);
    }

    [Fact]
    public void NoBinningIsAdvertisedForASensorThatOffersNone()
    {
        _api.SupportedBinningModes = 0;
        var backend = CreateBackend();

        Assert.DoesNotContain(backend.Controls, c => c.Id == ControlIds.Binning);
    }

    [Fact]
    public void OnlyFactorsTheSensorSupportsOnBothAxesAreOffered()
    {
        _api.SupportedBinningModes = UEyeConst.BinningVertical2x | UEyeConst.BinningHorizontal2x
                                     | UEyeConst.BinningVertical3x // horizontal 3x is missing: no 3x3
                                     | UEyeConst.BinningVertical4x | UEyeConst.BinningHorizontal4x;
        var backend = CreateBackend();

        Assert.Equal([1d, 2d, 4d], Control(backend, ControlIds.Binning).Options!.Select(o => o.Value));
    }

    [Fact]
    public void ProtocolV1SeesExposureIn100MicrosecondUnitsCappedAt1500Milliseconds()
    {
        var backend = CreateBackend();

        var info = V1ControlAdapter.Project(backend.Controls);

        var exposure = info.Controls.Single(c => c.Id == "exposure");
        Assert.Equal("100us", exposure.Unit);
        Assert.Equal(1, exposure.Min);
        Assert.Equal(15_000, exposure.Max);
        Assert.Equal(500, exposure.Cur);
        var gain = info.Controls.Single(c => c.Id == "gain");
        Assert.Equal("raw", gain.Unit);
        Assert.Equal(0, gain.Min);
        Assert.Equal(100, gain.Max);
        Assert.Equal(2, info.Controls.Count);
    }

    [Fact]
    public void GainIsAppliedAndReadBack()
    {
        var backend = CreateBackend();

        Assert.True(backend.SetControl(ControlIds.Gain, 55).Accepted);
        Assert.Equal(55, _api.Gain);
        Assert.Equal(55, Control(backend, ControlIds.Gain).Cur);

        Assert.True(backend.SetControl(ControlIds.Gain, 5000).Accepted); // clamped like the protocol says
        Assert.Equal(100, _api.Gain);
        Assert.Equal(100, Control(backend, ControlIds.Gain).Cur);
    }

    [Fact]
    public void ARoutineExposureChangeTouchesOnlyTheExposure()
    {
        var backend = CreateBackend();
        _api.ClearCalls();

        Assert.True(backend.SetControl(ControlIds.Exposure, 20_000).Accepted);

        Assert.Equal(new[] { "SetExposure 20" }, _api.Calls); // no pixel clock / frame rate / long mode traffic
        Assert.Equal(20_000, Control(backend, ControlIds.Exposure).Cur);
    }

    [Fact]
    public void ALongerExposureLowersTheFrameRateInsteadOfBeingClamped()
    {
        var backend = CreateBackend();

        Assert.True(backend.SetControl(ControlIds.Exposure, 200_000).Accepted);

        Assert.Equal(200.0, _api.ExposureMs, precision: 3);   // not clamped to the ~99 ms a 10 fps frame allows
        Assert.InRange(_api.FrameRate, 4.9, 5.0);              // 1e6 / 201 000
        Assert.Equal(30u, _api.PixelClockMhz);                 // a slower frame rate never needs a faster clock
        Assert.Equal(200_000, Control(backend, ControlIds.Exposure).Cur);
        Assert.InRange(Control(backend, ControlIds.Fps).Cur, 4.9, 5.0);
    }

    [Fact]
    public void ShorteningTheExposureRestoresThePreferredFrameRate()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 500_000);
        Assert.InRange(_api.FrameRate, 1.9, 2.0);

        backend.SetControl(ControlIds.Exposure, 10_000);

        Assert.Equal(10.0, _api.FrameRate, precision: 3);
        Assert.Equal(10.0, Control(backend, ControlIds.Fps).Cur);
    }

    [Fact]
    public void FrameRatesAboveWhatTheLowestClockOffersSwitchThePixelClock()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 5_000);

        foreach (var (fps, mhz) in new[] { (20.0, 59u), (40.0, 118u), (41.0, 237u), (10.0, 30u) })
        {
            Assert.True(backend.SetControl(ControlIds.Fps, fps).Accepted);
            Assert.Equal(mhz, _api.PixelClockMhz);
            Assert.Equal(fps, _api.FrameRate, precision: 3);
        }
    }

    [Fact]
    public void TheFpsControlIsLimitedByTheExposure()
    {
        var backend = CreateBackend();

        backend.SetControl(ControlIds.Fps, 100); // at 50 ms exposure at most ~19.6 fps fit

        Assert.InRange(_api.FrameRate, 19.0, 20.0);
    }

    [Fact]
    public void AnExposureBeyondTheNormalRangeSwitchesLongExposureModeOn()
    {
        var backend = CreateBackend();
        _api.ClearCalls();

        Assert.True(backend.SetControl(ControlIds.Exposure, 1_200_000).Accepted);

        Assert.True(_api.LongExposureEnabled);
        Assert.Equal(1200.0, _api.ExposureMs, precision: 3);
        // From the default 10 fps the camera is fast enough already: mode on first, then the exposure.
        Assert.Equal(new[] { "SetLongExposure True", "SetExposure 1200" }, _api.Calls);
        Assert.Equal(1_200_000, Control(backend, ControlIds.Exposure).Cur);
        Assert.Equal(0.83, Control(backend, ControlIds.Fps).Cur); // a 1.2 s frame: what the camera really does
    }

    [Fact]
    public void InLongModeTheFrameRateIsShownAsWhatItIsAndCannotBeSet()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.ClearCalls();

        var result = backend.SetControl(ControlIds.Fps, 20);

        Assert.False(result.Accepted);
        Assert.Contains("long-exposure mode", result.Message);
        Assert.Empty(_api.Calls);
        var fps = Control(backend, ControlIds.Fps);
        Assert.Equal(fps.Min, fps.Max);
        Assert.Equal(fps.Cur, fps.Max);
        Assert.Equal(0.83, fps.Cur);
    }

    [Fact]
    public void EnteringLongModeFromSlowFramesFirstGoesBackToAFastShortExposure()
    {
        // Switching the mode on takes about one frame time of the current timing (1.1 s at 1 fps on the real camera,
        // 0.2 s at 10 fps), so a camera that is exposing for most of a second is sped up before the switch.
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 900_000);
        Assert.InRange(_api.FrameRate, 1.0, 1.2);
        _api.ClearCalls();

        backend.SetControl(ControlIds.Exposure, 1_200_000);

        Assert.Equal(new[] { "SetExposure 10", "SetFrameRate 10", "SetLongExposure True", "SetExposure 1200" }, _api.Calls);
        Assert.True(_api.LongExposureEnabled);
        Assert.Equal(1200.0, _api.ExposureMs, precision: 3);
    }

    [Fact]
    public void OpeningNeverSwitchesLongExposureModeOnOrOff()
    {
        CreateBackend();

        Assert.Equal(0, _api.CallCount("SetLongExposure"));
        Assert.False(_api.LongExposureEnabled);
    }
    [Fact]
    public void LongExposureModeStaysOnForTheSession()
    {
        // Leaving long mode blocks about a second and then delivers no frame for another second (a 2.5 s gap on the real
        // camera), which makes the v1 engine restart the server: so a shorter exposure is set inside long mode.
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.ClearCalls();

        backend.SetControl(ControlIds.Exposure, 30_000);

        Assert.True(_api.LongExposureEnabled);
        Assert.DoesNotContain("SetLongExposure False", _api.Calls);
        Assert.Equal(new[] { "SetExposure 30" }, _api.Calls);
        Assert.Equal(30.0, _api.ExposureMs, precision: 3);
        Assert.InRange(Control(backend, ControlIds.Fps).Cur, 10.0, 10.3); // 1 / the 30 MHz clock's 97.6 ms frame time
    }
    [Fact]
    public void ChangingTheExposureInsideLongModeDoesNotToggleTheMode()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.ClearCalls();

        backend.SetControl(ControlIds.Exposure, 2_500_000);

        Assert.Equal(new[] { "SetExposure 2500" }, _api.Calls);
        Assert.Equal(2_500_000, Control(backend, ControlIds.Exposure).Cur);
    }

    [Fact]
    public void ExposureIsClampedToTheAdvertisedMaximum()
    {
        var backend = CreateBackend();

        backend.SetControl(ControlIds.Exposure, 25_000_000);

        Assert.Equal(4000.0, _api.ExposureMs, precision: 3);
    }

    [Fact]
    public void WithoutLongExposureSupportTheNormalMaximumIsTheLimit()
    {
        _api.LongExposureSupported = false;
        var backend = CreateBackend();

        var exposure = Control(backend, ControlIds.Exposure);
        backend.SetControl(ControlIds.Exposure, 3_000_000);

        Assert.InRange(exposure.Max, 999_000, 1_000_000);
        Assert.False(_api.LongExposureEnabled);
        Assert.InRange(_api.ExposureMs, 999.0, 1000.0);
    }

    [Fact]
    public void UnknownOrBadControlsAreRejectedWithoutTouchingTheCamera()
    {
        var backend = CreateBackend();
        _api.ClearCalls();

        Assert.False(backend.SetControl("binning", 2).Accepted); // would restart the stream, which a caller without permission cannot announce
        Assert.False(backend.SetControl("nope", 1).Accepted);
        Assert.False(backend.SetControl(ControlIds.Gain, double.NaN).Accepted);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void SetControlBeforeOpenIsRejected()
    {
        var backend = CreateBackend(open: false);

        Assert.False(backend.SetControl(ControlIds.Gain, 10).Accepted);
    }

    [Fact]
    public void AFailingHardwareCallIsACameraExceptionButNotFatal()
    {
        var backend = CreateBackend();
        var fatal = new List<string>();
        backend.Fatal += fatal.Add;
        _api.ExposureSetStatus = UEyeConst.InvalidParameter;

        var ex = Assert.Throws<CameraException>(() => backend.SetControl(ControlIds.Exposure, 20_000));

        Assert.Contains("set exposure", ex.Message);
        Assert.Empty(fatal);
    }

    [Fact]
    public void ALostCameraDuringAControlChangeIsFatal()
    {
        var backend = CreateBackend();
        var fatal = new List<string>();
        backend.Fatal += fatal.Add;
        _api.ExposureSetStatus = UEyeConst.CameraNotConnected;

        Assert.Throws<CameraException>(() => backend.SetControl(ControlIds.Exposure, 20_000));

        Assert.Single(fatal);
    }

    // -- binning: the in-process restart (spec 5.5) ---------------------------------------------------------

    private static List<int> Indices(IReadOnlyList<string> calls, string call) =>
        calls.Select((c, i) => (c, i)).Where(t => t.c == call).Select(t => t.i).ToList();

    private IdsBackend StartedBackend(out CollectingSink sink)
    {
        var backend = CreateBackend();
        sink = new CollectingSink();
        backend.Start(sink);
        Assert.True(sink.WaitForFrames(1), "no frames arrived");
        return backend;
    }

    [Fact]
    public void ABinningChangeRestartsTheStreamInTheOrderOfSpec55()
    {
        var backend = StartedBackend(out var sink);
        _api.ClearCalls();

        Assert.True(backend.SetControlMayRestart(ControlIds.Binning, 2).Accepted);

        // stop the capture, free the sequence, set the binning, allocate for the new size, capture again
        var calls = _api.Calls;
        int stop = calls.ToList().IndexOf("StopCapture");
        int exitQueue = calls.ToList().IndexOf("ExitImageQueue");
        int clear = calls.ToList().IndexOf("ClearSequence");
        int lastFree = Indices(calls, "FreeImageMemory")[^1];
        int set = calls.ToList().IndexOf("SetBinning 0x3");
        int firstAlloc = calls.ToList().IndexOf("AllocImageMemory");
        int initQueue = calls.ToList().IndexOf("InitImageQueue");
        int start = calls.ToList().IndexOf("StartCapture");
        Assert.True(stop >= 0 && stop < exitQueue && exitQueue < clear && clear < lastFree, string.Join(", ", calls));
        Assert.True(lastFree < set && set < firstAlloc && firstAlloc < initQueue && initQueue < start, string.Join(", ", calls));
        Assert.Equal(IdsBackend.BufferCount, _api.CallCount("FreeImageMemory"));
        Assert.Equal(IdsBackend.BufferCount, _api.CallCount("AllocImageMemory"));
        Assert.Equal(IdsBackend.BufferCount, _api.OpenBuffers);
        Assert.Equal(2, _api.Binning);
        Assert.Equal(new StreamGeometry(FakeUEyeApi.Width / 2, FakeUEyeApi.Height / 2, 2, PixelFormat.Gray8), backend.Geometry);
        Assert.Equal(2, Control(backend, ControlIds.Binning).Cur);
    }

    [Fact]
    public void AfterABinningChangeFramesHaveTheNewSizeAndTheOldSizeNeverComesBack()
    {
        var backend = StartedBackend(out var sink);
        backend.SetControlMayRestart(ControlIds.Binning, 2);
        int mark = sink.Count;

        Assert.True(sink.WaitForFrames(mark + 3));
        backend.Stop();

        var frames = sink.Snapshot().Skip(mark).ToList();
        var binned = new StreamGeometry(FakeUEyeApi.Width / 2, FakeUEyeApi.Height / 2, 2, PixelFormat.Gray8);
        Assert.All(frames, f => Assert.Equal(binned, f.Geometry));
        using var decoder = new WicGrayDecoder();
        var image = decoder.Decode(frames[0].Data);
        Assert.Equal((binned.Width, binned.Height), (image.Width, image.Height));
        Assert.InRange(image.Pixels.Average(p => (double)p), 78, 82); // the fill value (80), not the row padding (255)
    }

    [Fact]
    public void TheRestartStaysWellInsideItsBudgetOnTheFakeCamera()
    {
        // Spec 5.5 targets 500 ms. The fake camera answers at once, so this checks the backend's own work (stopping and
        // starting the capture thread, rebuilding the timing table), with ample room for a slow CI runner. The first
        // change is a warm-up for JIT and thread start; the next two are measured.
        var backend = StartedBackend(out _);
        backend.SetControlMayRestart(ControlIds.Binning, 2);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        backend.SetControlMayRestart(ControlIds.Binning, 1);
        long back = timer.ElapsedMilliseconds;
        timer.Restart();
        backend.SetControlMayRestart(ControlIds.Binning, 2);

        Assert.True(back < 500, $"restart to 1x1 took {back} ms");
        Assert.True(timer.ElapsedMilliseconds < 500, $"restart to 2x2 took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TheFrameTimingIsMeasuredAgainAtTheNewBinning()
    {
        // A binned sensor reads out faster (modelled in the fake as N times quicker frames), so the same frame rate needs a
        // lower pixel clock: 20 fps takes 59 MHz at 1x1 but only 30 MHz at 2x2.
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 5_000);
        backend.SetControl(ControlIds.Fps, 20);
        Assert.Equal(59u, _api.PixelClockMhz);

        backend.SetControlMayRestart(ControlIds.Binning, 2);
        backend.SetControl(ControlIds.Fps, 20);

        Assert.Equal(30u, _api.PixelClockMhz);
        Assert.Equal(20.0, _api.FrameRate, precision: 3);
    }

    [Fact]
    public void ExposureAndPreferredFrameRateSurviveABinningChangeAndTheCouplingIsReported()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 200_000); // exposure priority: fps drops to about 5
        double fpsBefore = Control(backend, ControlIds.Fps).Cur;

        backend.SetControlMayRestart(ControlIds.Binning, 2);

        Assert.Equal(200_000, Control(backend, ControlIds.Exposure).Cur);
        Assert.Equal(fpsBefore, Control(backend, ControlIds.Fps).Cur, precision: 1);
        Assert.InRange(Control(backend, ControlIds.Fps).Max, 4.9, 5.0); // still limited by the exposure

        backend.SetControl(ControlIds.Exposure, 10_000);

        Assert.Equal(10.0, Control(backend, ControlIds.Fps).Cur); // the preferred rate was kept through the restart
    }

    [Fact]
    public void ABinningChangeIsRefusedWithoutThePermissionToRestartAndTouchesNothing()
    {
        var backend = StartedBackend(out _);
        _api.ClearCalls();

        var result = backend.SetControl(ControlIds.Binning, 2); // what a v1 session would send, if it could

        Assert.False(result.Accepted);
        Assert.Contains("cannot announce", result.Message);
        Assert.Empty(_api.Calls);
        Assert.False(((ICameraBackend)backend).WouldRestartStream(new ControlChange(ControlIds.Binning, 2)));
        Assert.True(((ICameraBackend)backend).WouldRestartStream(new ControlChange(ControlIds.Binning, 2, MayRestartStream: true)));
    }

    [Fact]
    public void TheBinningItAlreadyHasOrOneItDoesNotOfferIsNoRestart()
    {
        var backend = StartedBackend(out _);
        _api.ClearCalls();

        Assert.True(backend.SetControlMayRestart(ControlIds.Binning, 1).Accepted); // current value
        Assert.False(backend.SetControlMayRestart(ControlIds.Binning, 3).Accepted); // not an option

        Assert.Empty(_api.Calls);
        Assert.False(((ICameraBackend)backend).WouldRestartStream(new ControlChange(ControlIds.Binning, 1, MayRestartStream: true)));
        Assert.False(((ICameraBackend)backend).WouldRestartStream(new ControlChange(ControlIds.Binning, 3, MayRestartStream: true)));
    }

    [Fact]
    public void ABinningModeTheCameraRefusesIsRejectedAndTheStreamKeepsRunningAtTheOldSize()
    {
        var backend = StartedBackend(out var sink);
        _api.SetBinningStatus = UEyeConst.NotSupported;

        var result = backend.SetControlMayRestart(ControlIds.Binning, 2);

        Assert.False(result.Accepted);
        Assert.Contains("did not take binning 2x2", result.Message);
        Assert.Equal(1, Control(backend, ControlIds.Binning).Cur);
        Assert.Equal(new StreamGeometry(FakeUEyeApi.Width, FakeUEyeApi.Height, 1, PixelFormat.Gray8), backend.Geometry);
        int mark = sink.Count;
        Assert.True(sink.WaitForFrames(mark + 3), "the stream did not come back after the refused change");
        Assert.Equal(IdsBackend.BufferCount, _api.OpenBuffers);
    }

    [Fact]
    public void AStreamThatCannotBeRestartedIsACameraException()
    {
        var backend = StartedBackend(out _);
        _api.StartCaptureStatus = UEyeConst.NoSuccess;

        var ex = Assert.Throws<CameraException>(() => backend.SetControlMayRestart(ControlIds.Binning, 2));

        Assert.Contains("restart capture", ex.Message);
    }

    [Fact]
    public void BinningIsRefusedWhileLongExposureModeIsOnWithTheReasonTheUserCanActOn()
    {
        var backend = StartedBackend(out _);
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.ClearCalls();

        // What the v2 dialect really sends: WouldRestartStream said no, so the change goes out without the flag.
        var withoutFlag = backend.SetControl(ControlIds.Binning, 2);
        var withFlag = backend.SetControlMayRestart(ControlIds.Binning, 2);

        foreach (var result in new[] { withoutFlag, withFlag })
        {
            Assert.False(result.Accepted);
            Assert.Contains("lower the exposure", result.Message); // not "cannot announce": long mode wins
        }

        Assert.Empty(_api.Calls);
        Assert.False(((ICameraBackend)backend).WouldRestartStream(new ControlChange(ControlIds.Binning, 2, MayRestartStream: true)));
    }

    [Fact]
    public void AFatalErrorDuringARestartDoesNotStartANewCaptureThread()
    {
        var backend = StartedBackend(out _);
        var raised = new ManualResetEventSlim();
        backend.Fatal += _ => raised.Set();
        _api.WaitStatus = UEyeConst.CameraNotConnected;
        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
        _api.WaitStatus = 0;
        int starts = _api.CallCount("StartCapture");

        backend.SetControlMayRestart(ControlIds.Binning, 2);

        Assert.Equal(starts, _api.CallCount("StartCapture"));
    }

    [Fact]
    public void ABinningChangeBeforeCaptureStartedDoesNotStartTheCapture()
    {
        var backend = CreateBackend();
        _api.ClearCalls();

        Assert.True(backend.SetControlMayRestart(ControlIds.Binning, 2).Accepted);

        Assert.Equal(0, _api.CallCount("StartCapture"));
        Assert.Equal(0, _api.CallCount("StopCapture"));
        Assert.Equal(IdsBackend.BufferCount, _api.OpenBuffers);
        Assert.Equal(FakeUEyeApi.Width / 2, backend.Geometry.Width);
    }

    // -- leaving long-exposure mode (Q12) ----------------------------------------------------------------

    [Fact]
    public void LeavingLongExposureModeNeedsThePermissionToRestartAndAnExposureInTheNormalRange()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        ICameraBackend b = backend;

        Assert.True(b.WouldRestartStream(new ControlChange(ControlIds.Exposure, 30_000, MayRestartStream: true)));
        Assert.False(b.WouldRestartStream(new ControlChange(ControlIds.Exposure, 30_000)));                         // a v1 session: sticky
        Assert.False(b.WouldRestartStream(new ControlChange(ControlIds.Exposure, 2_500_000, MayRestartStream: true))); // still long
        Assert.False(b.WouldRestartStream(new ControlChange(ControlIds.Gain, 50, MayRestartStream: true)));
    }

    [Fact]
    public void InNormalModeNoExposureChangeEverRestartsTheStream()
    {
        var backend = CreateBackend();
        ICameraBackend b = backend;

        Assert.False(b.WouldRestartStream(new ControlChange(ControlIds.Exposure, 30_000, MayRestartStream: true)));
        Assert.False(b.WouldRestartStream(new ControlChange(ControlIds.Exposure, 3_000_000, MayRestartStream: true))); // enters long mode: no gap to wrap
    }

    [Fact]
    public void LeavingLongExposureModeRestartsTheStreamAndGivesTheFrameRateBack()
    {
        var backend = StartedBackend(out var sink);
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        var fps = Control(backend, ControlIds.Fps);
        Assert.Equal(fps.Min, fps.Max); // fixed in long mode
        _api.ClearCalls();

        Assert.True(backend.SetControlMayRestart(ControlIds.Exposure, 30_000).Accepted);

        var calls = _api.Calls.ToList();
        int stop = calls.IndexOf("StopCapture");
        int leave = calls.IndexOf("SetLongExposure False");
        int rate = calls.IndexOf("SetFrameRate 10");
        int exposure = calls.IndexOf("SetExposure 30");
        int start = calls.IndexOf("StartCapture");
        Assert.True(stop >= 0 && stop < leave && leave < rate && rate < exposure && exposure < start, string.Join(", ", calls));
        Assert.Equal(0, _api.CallCount("AllocImageMemory")); // same geometry: the buffers stay
        Assert.Equal(0, _api.CallCount("FreeImageMemory"));
        Assert.False(_api.LongExposureEnabled);
        Assert.Equal(30.0, _api.ExposureMs, precision: 3);
        Assert.Equal(10.0, _api.FrameRate, precision: 3); // the preferred rate again, not the 1 fps the camera drops to
        var after = Control(backend, ControlIds.Fps);
        Assert.True(after.Max > after.Min, "fps must be settable again");
        Assert.Equal(10.0, after.Cur);
        Assert.Equal(new StreamGeometry(FakeUEyeApi.Width, FakeUEyeApi.Height, 1, PixelFormat.Gray8), backend.Geometry);

        Assert.True(backend.SetControl(ControlIds.Fps, 4).Accepted);
        Assert.Equal(4.0, _api.FrameRate, precision: 3);
        int mark = sink.Count;
        Assert.True(sink.WaitForFrames(mark + 3), "frames did not resume after leaving long mode");
    }

    [Fact]
    public async Task LeavingLongExposureModeReturnsOnlyAfterTheFirstFrameFollowingTheRestart()
    {
        // The gap after the switch (about a second on the real camera) must fall inside "reconfiguring": the caller announces
        // "streaming" when SetControl returns. The fake holds back every frame until the gate opens; the call cannot return before.
        using var gate = new ManualResetEventSlim();
        var backend = CreateBackendWithFirstFrameTimeout(TimeSpan.FromSeconds(60), minimumFrameTimeoutMs: 60_000, new Logger(TextWriter.Null, LogLevel.Debug));
        var sink = new CollectingSink();
        backend.Start(sink);
        Assert.True(sink.WaitForFrames(1));
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.FrameGate = gate;
        int startsBefore = _api.CallCount("StartCapture");
        int framesBefore = sink.Count;

        var leaving = Task.Run(() => backend.SetControlMayRestart(ControlIds.Exposure, 30_000));
        // The old capture thread is joined before the capture starts again, so a wait that finds the gate closed after that
        // is the new capture thread asking for its first frame.
        Assert.True(SpinWait.SpinUntil(() => _api.CallCount("StartCapture") > startsBefore, TimeSpan.FromSeconds(10)), "the capture was not restarted");
        int waitersAtStart = _api.GateWaiters;
        Assert.True(SpinWait.SpinUntil(() => _api.GateWaiters > waitersAtStart, TimeSpan.FromSeconds(10)), "the new capture never asked for a frame");
        Assert.False(leaving.IsCompleted, "SetControl returned before the first frame after the restart");
        gate.Set(); // the camera delivers
        var result = await leaving.WaitAsync(TimeSpan.FromSeconds(20)); // times out with an exception if SetControl never returns
        Assert.True(result.Accepted);
        Assert.True(sink.Count > framesBefore, "no frame was captured when the call returned");
    }

    [Fact]
    public void WhenNoFrameFollowsTheRestartTheCallReturnsAnywayWithAWarning()
    {
        using var gate = new ManualResetEventSlim();
        var written = new StringWriter();
        var log = new Logger(written, LogLevel.Info);
        var backend = CreateBackendWithFirstFrameTimeout(TimeSpan.FromMilliseconds(100), minimumFrameTimeoutMs: 60_000, log);
        var fatal = new List<string>();
        backend.Fatal += fatal.Add;
        backend.Start(new CollectingSink());
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.FrameGate = gate; // never opened

        var result = backend.SetControlMayRestart(ControlIds.Exposure, 30_000);

        Assert.True(result.Accepted); // not a failure
        Assert.Empty(fatal);
        log.Flush(TimeSpan.FromSeconds(5));
        Assert.Contains("No frame within", written.ToString());
        gate.Set();
    }

    [Fact]
    public void AnExposureStillBeyondTheNormalRangeKeepsLongModeEvenForACallerThatMayRestart()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.ClearCalls();

        Assert.True(backend.SetControlMayRestart(ControlIds.Exposure, 2_500_000).Accepted);

        Assert.Equal(new[] { "SetExposure 2500" }, _api.Calls);
        Assert.True(_api.LongExposureEnabled);
    }

    [Fact]
    public void ACallerThatCannotAnnounceARestartKeepsLongModeStickyAsInV1()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.ClearCalls();

        Assert.True(backend.SetControl(ControlIds.Exposure, 30_000).Accepted);

        Assert.Equal(new[] { "SetExposure 30" }, _api.Calls);
        Assert.True(_api.LongExposureEnabled);
    }

    [Fact]
    public void LongModeCanBeEnteredAgainAfterLeavingIt()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        backend.SetControlMayRestart(ControlIds.Exposure, 30_000);
        _api.ClearCalls();

        backend.SetControl(ControlIds.Exposure, 3_000_000);

        Assert.True(_api.LongExposureEnabled);
        Assert.Contains("SetLongExposure True", _api.Calls);
        Assert.Equal(3000.0, _api.ExposureMs, precision: 3);
    }

    [Fact]
    public void AFailureLeavingLongModeIsACameraException()
    {
        var backend = CreateBackend();
        backend.SetControl(ControlIds.Exposure, 1_200_000);
        _api.LeaveLongExposureStatus = UEyeConst.InvalidParameter;

        var ex = Assert.Throws<CameraException>(() => backend.SetControlMayRestart(ControlIds.Exposure, 30_000));

        Assert.Contains("leave long exposure", ex.Message);
    }

    // -- capture -----------------------------------------------------------------------------------------------

    [Fact]
    public void FramesAreCopiedHonouringThePitchAndSentAsJpeg()
    {
        var backend = CreateBackend();
        var sink = new CollectingSink();
        _api.FillValue = 90;

        backend.Start(sink);

        Assert.True(sink.WaitForFrames(3), "no frames arrived");
        backend.Stop();
        var (data, kind, geometry) = sink.First();
        Assert.Equal(FrameKind.Jpeg, kind);
        Assert.Equal(new StreamGeometry(FakeUEyeApi.Width, FakeUEyeApi.Height, 1, PixelFormat.Gray8), geometry);
        using var decoder = new WicGrayDecoder();
        var image = decoder.Decode(data);
        Assert.Equal((FakeUEyeApi.Width, FakeUEyeApi.Height), (image.Width, image.Height));
        // The padding behind each row is 255: a copy that ignored the pitch would be far brighter than the 90 of the image.
        double mean = image.Pixels.Average(p => (double)p);
        Assert.InRange(mean, 88, 92);
        Assert.True(backend.FramesProduced >= 3);
        Assert.Equal(1, _api.CallCount("StartCapture"));
    }

    [Fact]
    public void StopEndsCaptureAndTheBackendCanStartAgain()
    {
        var backend = CreateBackend();
        var first = new CollectingSink();
        backend.Start(first);
        Assert.True(first.WaitForFrames(1));

        backend.Stop();
        backend.Stop(); // idempotent
        Assert.Equal(1, _api.CallCount("StopCapture"));

        var second = new CollectingSink();
        backend.Start(second);
        Assert.True(second.WaitForFrames(1));
    }

    [Fact]
    public void StartingTwiceIsRejected()
    {
        var backend = CreateBackend();
        backend.Start(new CollectingSink());

        Assert.Throws<InvalidOperationException>(() => backend.Start(new CollectingSink()));
    }

    [Fact]
    public void ThreeConsecutiveEmptyWaitsAreFatalAndReportedOnce()
    {
        var backend = CreateBackend();
        var fatal = new List<string>();
        var raised = new ManualResetEventSlim();
        backend.Fatal += m =>
        {
            lock (fatal)
            {
                fatal.Add(m);
            }

            raised.Set();
        };
        _api.WaitStatus = UEyeConst.TimedOut;

        backend.Start(new CollectingSink());

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "no fatal error after repeated timeouts");
        backend.Stop();
        lock (fatal)
        {
            Assert.Single(fatal);
            Assert.Contains("No frame", fatal[0]);
        }
    }

    [Fact]
    public void TwoTimeoutsBetweenFramesAreTolerated()
    {
        var backend = CreateBackend();
        var fatal = new List<string>();
        backend.Fatal += fatal.Add;
        _api.ScriptWaitResults(UEyeConst.TimedOut, UEyeConst.TimedOut);
        var sink = new CollectingSink();

        backend.Start(sink);

        Assert.True(sink.WaitForFrames(2));
        Assert.Empty(fatal);
    }

    [Theory]
    [InlineData(UEyeConst.CameraNotConnected)]
    [InlineData(UEyeConst.InvalidHandle)]
    [InlineData(UEyeConst.IoRequestFailed)]
    public void ALostCameraSeenByTheCaptureLoopIsFatalAtOnce(int status)
    {
        var backend = CreateBackend();
        var raised = new ManualResetEventSlim();
        string? message = null;
        backend.Fatal += m =>
        {
            message = m;
            raised.Set();
        };
        _api.ScriptWaitResults(status);

        backend.Start(new CollectingSink());

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
        Assert.Contains("Camera lost", message);
    }

    [Fact]
    public void ARemovalEventIsFatalEvenWhileNothingIsCapturing()
    {
        var backend = CreateBackend();
        var raised = new ManualResetEventSlim();
        string? message = null;
        backend.Fatal += m =>
        {
            message = m;
            raised.Set();
        };

        _api.SignalRemoval();

        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)), "removal event was not noticed");
        Assert.Contains("unplugged", message);
    }

    // -- close ---------------------------------------------------------------------------------------------

    [Fact]
    public void DisposeReleasesEverythingInOrderAndOnlyOnce()
    {
        var backend = CreateBackend();
        backend.Start(new CollectingSink());
        _api.ClearCalls();

        backend.Dispose();
        backend.Dispose();

        var calls = _api.Calls;
        Assert.Equal(1, calls.Count(c => c == "ExitCamera"));
        Assert.Equal(IdsBackend.BufferCount, calls.Count(c => c == "FreeImageMemory"));
        Assert.Equal(0, _api.OpenBuffers);
        Assert.True(calls.ToList().IndexOf("StopCapture") < calls.ToList().IndexOf("ExitImageQueue"));
        Assert.True(calls.ToList().IndexOf("ClearSequence") < calls.ToList().IndexOf("FreeImageMemory"));
        Assert.Equal("ExitCamera", calls[^1]); // the camera handle goes last
        Assert.False(_api.IsOpen);
    }

    [Fact]
    public void DisposingABackendThatWasNeverOpenedTouchesNothing()
    {
        var backend = CreateBackend(open: false);

        backend.Dispose();

        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void AfterAFatalErrorTheCameraIsStillReleased()
    {
        var backend = CreateBackend();
        var raised = new ManualResetEventSlim();
        backend.Fatal += _ => raised.Set();
        _api.WaitStatus = UEyeConst.CameraNotConnected;
        backend.Start(new CollectingSink());
        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));

        backend.Stop();
        backend.Dispose();

        Assert.Equal(1, _api.CallCount("ExitCamera"));
    }
}

public class IdsBackendWithoutTheApiTests
{
    [Fact]
    public void CreatingTheDefaultBackendNeverThrowsWhateverIsInstalled()
    {
        using var backend = IdsBackend.CreateDefault(90, new Logger(TextWriter.Null, LogLevel.Debug));

        // Either there is no IDS runtime (nothing found) or there is one (whatever cameras it knows); never an exception.
        var devices = backend.Probe();
        Assert.NotNull(devices);
    }

    [Fact]
    public void ABackendWithoutTheApiHasNoDevicesAndCannotOpen()
    {
        using var backend = new IdsBackend(null, 90, new Logger(TextWriter.Null, LogLevel.Debug));

        Assert.Empty(backend.Probe());
        var ex = Assert.Throws<CameraException>(() => backend.Open(new DeviceInfo("ids", "UI306xCP-M", Handle: "1")));
        Assert.Contains("IDS Software Suite", ex.Message);
    }
}