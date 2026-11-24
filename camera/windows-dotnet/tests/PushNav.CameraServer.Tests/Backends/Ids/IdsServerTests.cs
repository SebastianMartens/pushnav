// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Ids;
using PushNav.CameraServer.Imaging;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Tests.Backends.Ids;

/// <summary>The whole server with the IDS backend on top of the fake uEye API: exit codes, <c>--list</c>, and a v1 session.</summary>
public class IdsServerTests : IDisposable
{
    private readonly FakeUEyeApi _api = new();

    public void Dispose() => _api.Dispose();

    private CameraServerApp.Seams SeamsWith(Action<int>? onListening = null) => new()
    {
        Delay = _ => { },
        BackendFactory = (o, log) => BackendRegistry.Create(o, log, () => new IdsBackend(_api, o.JpegQuality, log) { MinimumFrameTimeoutMilliseconds = 50 }),
        OnListening = onListening,
    };

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CameraServerApp.Run(args, stdout, stderr, CancellationToken.None, SeamsWith());
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ListShowsTheCameraWithItsGeometryAndControls()
    {
        var (code, stdout, _) = Run("--backend", "ids", "--list");

        Assert.Equal(ExitCodes.Ok, code);
        using var doc = JsonDocument.Parse(stdout);
        var device = Assert.Single(doc.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Equal("ids", device.GetProperty("backend").GetString());
        Assert.Equal("windows-dotnet-ids", device.GetProperty("hello_backend").GetString());
        Assert.Equal("UI306xCP-M", device.GetProperty("model").GetString());
        Assert.Equal("ids:1409:3060", device.GetProperty("camera_key").GetString());
        Assert.Equal("1409", device.GetProperty("vid").GetString());
        Assert.Equal("3060", device.GetProperty("pid").GetString());
        Assert.Equal(FakeUEyeApi.Width, device.GetProperty("geometry").GetProperty("width").GetInt32());
        string?[] ids = device.GetProperty("controls").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new string?[] { "exposure", "gain", "fps", "binning" }, ids);
        Assert.Equal(1, _api.CallCount("ExitCamera")); // --list closes the camera again
    }

    [Fact]
    public void NoCameraExitsWithCode3()
    {
        _api.Cameras.Clear();

        var (code, _, stderr) = Run("--backend", "ids");

        Assert.Equal(ExitCodes.NoSupportedCamera, code);
        Assert.Contains("No supported camera found", stderr);
        Assert.Equal(0, _api.CallCount("InitCamera"));
    }

    [Fact]
    public void ACameraAnotherProgramHoldsExitsWithCode1AndSaysSo()
    {
        _api.InitStatus = UEyeConst.DeviceInUse;

        var (code, _, stderr) = Run("--backend", "ids");

        Assert.Equal(ExitCodes.Fatal, code);
        Assert.Contains("another program", stderr);
    }

    [Fact]
    public void ACaptureThatCannotStartExitsWithCode1InsteadOfCrashing()
    {
        _api.StartCaptureStatus = UEyeConst.NoSuccess;

        var (code, _, stderr) = Run("--backend", "ids", "--port", "0");

        Assert.Equal(ExitCodes.Fatal, code);
        Assert.Contains("Failed to start capture", stderr);
        Assert.Equal(1, _api.CallCount("ExitCamera"));
    }

    [Fact]
    public void AV1SessionGetsFramesAndTheCappedExposureAndTheCameraIsReleasedAfterwards()
    {
        int port = 0;
        var listening = new ManualResetEventSlim();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var seams = SeamsWith(p =>
        {
            port = p;
            listening.Set();
        });
        int code = -1;
        var server = new Thread(() => code = CameraServerApp.Run(["--backend", "ids", "--port", "0"], stdout, stderr, CancellationToken.None, seams));
        server.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)), "server did not start listening");

        using (var client = LoopbackClient.Connect(port))
        {
            var info = client.Handshake();

            var hello = client.ServerHello!;
            Assert.Equal("windows-dotnet-ids", hello.Backend);
            Assert.Equal("UI306xCP-M", hello.CameraModel);
            Assert.Equal((FakeUEyeApi.Width, FakeUEyeApi.Height), (hello.DefaultWidth, hello.DefaultHeight));
            Assert.Equal("100us", info.Controls.Single(c => c.Id == "exposure").Unit);
            Assert.Equal(15_000, info.Controls.Single(c => c.Id == "exposure").Max);

            var frame = client.ReadUntil(MessageType.Frame);
            Assert.Equal(0xFF, frame.Payload[0]);
            Assert.Equal(0xD8, frame.Payload[1]);

            // 300 ms: below the normal limit at the fps floor, so the frame rate drops and long mode stays off
            client.SetControl("exposure", 3000);
            client.ReadUntil(MessageType.ControlInfo);
            Assert.Equal(3000, client.LastControlInfo!.Controls.Single(c => c.Id == "exposure").Cur);
            Assert.False(_api.LongExposureEnabled);
            Assert.InRange(_api.FrameRate, 3.0, 3.4);

            // far beyond the v1 limit: the adapter caps it at 1.5 s, which needs long exposure mode
            client.SetControl("exposure", 100_000);
            client.ReadUntil(MessageType.ControlInfo);
            Assert.Equal(15_000, client.LastControlInfo!.Controls.Single(c => c.Id == "exposure").Cur);
            Assert.True(_api.LongExposureEnabled);
            Assert.Equal(1500.0, _api.ExposureMs, precision: 3);

            client.SetControl("gain", 70);
            client.ReadUntil(MessageType.ControlInfo);
            Assert.Equal(70, client.LastControlInfo!.Controls.Single(c => c.Id == "gain").Cur);
            Assert.Equal(70, _api.Gain);

            client.ReadUntil(MessageType.Frame); // still streaming
        }

        Assert.True(server.Join(TimeSpan.FromSeconds(10)), "server did not exit after the client left");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(1, _api.CallCount("ExitCamera"));
        Assert.Equal(0, _api.OpenBuffers);
    }

    [Fact]
    public void AV2SessionGetsNativeControlsWithoutTheV1CapAndAStreamInfoThatFollowsTheExposure()
    {
        int port = 0;
        var listening = new ManualResetEventSlim();
        var seams = SeamsWith(p =>
        {
            port = p;
            listening.Set();
        });
        int code = -1;
        var server = new Thread(() => code = CameraServerApp.Run(["--backend", "ids", "--port", "0"], new StringWriter(), new StringWriter(), CancellationToken.None, seams));
        server.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)), "server did not start listening");

        using (var client = LoopbackClient.Connect(port))
        {
            var (controls, stream) = client.HandshakeV2();

            // exposure, fps and gain with their native units, and the binning the sensor offers
            Assert.Equal(new[] { "exposure", "gain", "fps", "binning" }, controls.Controls.Select(c => c.Id));
            var binning = controls.Controls.Single(c => c.Id == "binning");
            Assert.Equal(("enum", true), (binning.Type, binning.RestartsStream));
            Assert.Equal([1d, 2d], binning.Options!.Select(o => o.Value));
            var exposure = controls.Controls.Single(c => c.Id == "exposure");
            Assert.Equal("us", exposure.Unit);
            Assert.Equal(4_000_000, exposure.Max);
            Assert.Equal(("float", "fps"), (controls.Controls.Single(c => c.Id == "fps").Type, controls.Controls.Single(c => c.Id == "fps").Unit));
            Assert.Equal((1, "streaming", FakeUEyeApi.Width, FakeUEyeApi.Height, 1), (stream.Generation, stream.State, stream.Width, stream.Height, stream.Binning));
            Assert.True(stream.FrameIntervalUs > 0);
            client.ReadUntil(MessageType.Frame);

            // 3 s: beyond the v1 cap of 1.5 s, taken as it is; the frame interval in STREAM_INFO covers it (same generation)
            client.SetControl("exposure", 3_000_000);
            var (info, _) = client.ReadUntilStreamInfo("streaming");
            Assert.Equal(3_000_000, client.LastControlInfo!.Controls.Single(c => c.Id == "exposure").Cur);
            Assert.Equal(1, info.Generation);
            Assert.InRange(info.FrameIntervalUs, 3_000_000, 4_000_000);
            Assert.Equal(3_000_000, info.ExposureUs);
            Assert.Equal(3000.0, _api.ExposureMs, precision: 3);
        }

        Assert.True(server.Join(TimeSpan.FromSeconds(10)), "server did not exit after the client left");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(1, _api.CallCount("ExitCamera"));
    }

    [Fact]
    public void AV1ClientNeverSeesBinningAndCannotChangeItOrLeaveLongExposureMode()
    {
        // The camera has binning and a long-exposure mode, but a v1 client has no way to be told about a restart (its stall
        // timeout is a fixed 2 s): it sees neither, and nothing it sends restarts the stream.
        using var run = StartServer();
        using var client = LoopbackClient.Connect(run.Port);
        var info = client.Handshake();
        Assert.DoesNotContain(info.Controls, c => c.Id == "binning");

        client.SetControl("binning", 2);
        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(1, _api.Binning);
        Assert.Equal(0, _api.CallCount("SetBinning"));

        client.SetControl("exposure", 100_000); // far beyond 1.5 s: capped, which needs long-exposure mode
        client.ReadUntil(MessageType.ControlInfo);
        Assert.True(_api.LongExposureEnabled);
        client.SetControl("exposure", 100);      // 10 ms in v1 units: long mode stays on (sticky), no restart
        client.ReadUntil(MessageType.ControlInfo);

        Assert.True(_api.LongExposureEnabled);
        Assert.Equal(0, _api.CallCount("SetLongExposure False"));
        Assert.Equal(1, _api.CallCount("StartCapture")); // never restarted
        client.ReadUntil(MessageType.Frame);
    }

    [Fact]
    public void AV2BinningChangeIsWrappedInReconfiguringAndStreamingWithTheNewSize()
    {
        using var run = StartServer();
        using var client = LoopbackClient.Connect(run.Port);
        var (_, first) = client.HandshakeV2();
        client.ReadUntil(MessageType.Frame);

        client.SetControl("binning", 2);

        var (reconfiguring, framesBefore) = client.ReadUntilStreamInfo("reconfiguring");
        var (streaming, framesDuring) = client.ReadUntilStreamInfo("streaming");
        var frame = client.ReadUntil(MessageType.Frame);
        Assert.Equal((first.Generation, FakeUEyeApi.Width, FakeUEyeApi.Height), (reconfiguring.Generation, reconfiguring.Width, reconfiguring.Height));
        Assert.Equal((first.Generation + 1, FakeUEyeApi.Width / 2, FakeUEyeApi.Height / 2, 2), (streaming.Generation, streaming.Width, streaming.Height, streaming.Binning));
        Assert.Equal(2, client.LastControlInfo!.Controls.Single(c => c.Id == "binning").Cur);
        using var decoder = new WicGrayDecoder();
        foreach (var old in framesBefore.Concat(framesDuring).Where(m => m.Type == MessageType.Frame))
        {
            Assert.Equal(FakeUEyeApi.Width, decoder.Decode(old.Payload).Width);
        }

        var image = decoder.Decode(frame.Payload);
        Assert.Equal((streaming.Width, streaming.Height), (image.Width, image.Height));
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(streaming.Width, decoder.Decode(client.ReadUntil(MessageType.Frame).Payload).Width); // the old size never comes back
        }
    }

    [Fact]
    public void AV2ExposureBackIntoTheNormalRangeLeavesLongModeInsideReconfiguringAndStreamingAtTheSameSize()
    {
        using var run = StartServer();
        using var client = LoopbackClient.Connect(run.Port);
        var (controls, _) = client.HandshakeV2();
        var fpsInNormalMode = controls.Controls.Single(c => c.Id == "fps");
        Assert.True(fpsInNormalMode.Max > fpsInNormalMode.Min);

        client.SetControl("exposure", 3_000_000); // long-exposure mode on, same generation
        var (afterLong, _) = client.ReadUntilStreamInfo("streaming");
        Assert.True(_api.LongExposureEnabled);
        var fpsInLongMode = client.LastControlInfo!.Controls.Single(c => c.Id == "fps");
        Assert.Equal(fpsInLongMode.Min, fpsInLongMode.Max); // fixed while the mode is on
        client.SetControl("fps", 5);                         // refused: nothing restarts, nothing changes
        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(0, _api.CallCount("SetLongExposure False"));

        client.SetControl("exposure", 100_000);              // back into the normal range: the exit gaps the stream
        var (reconfiguring, _) = client.ReadUntilStreamInfo("reconfiguring");
        var (streaming, _) = client.ReadUntilStreamInfo("streaming");

        Assert.Equal(afterLong.Generation, reconfiguring.Generation);
        Assert.Equal(afterLong.Generation + 1, streaming.Generation); // the generation moves although the geometry does not
        Assert.Equal((FakeUEyeApi.Width, FakeUEyeApi.Height, 1), (streaming.Width, streaming.Height, streaming.Binning));
        Assert.Equal(100_000, streaming.ExposureUs);
        Assert.Equal(1, _api.CallCount("SetLongExposure False"));
        Assert.False(_api.LongExposureEnabled);
        var fpsAfter = client.LastControlInfo!.Controls.Single(c => c.Id == "fps");
        Assert.True(fpsAfter.Max > fpsAfter.Min, "fps is settable again after the exit");
        client.ReadUntil(MessageType.Frame);

        client.SetControl("fps", 5);
        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(5.0, _api.FrameRate, precision: 2);
    }

    /// <summary>The server on the fake uEye API, on a free port; disposing the run ends the session and waits for the server.</summary>
    private ServerRun StartServer() => new(this);

    private sealed class ServerRun : IDisposable
    {
        private readonly Thread _server;
        private readonly int _port;
        private int _code = -1;

        public ServerRun(IdsServerTests owner)
        {
            int port = 0;
            var listening = new ManualResetEventSlim();
            var seams = owner.SeamsWith(p =>
            {
                port = p;
                listening.Set();
            });
            _server = new Thread(() => _code = CameraServerApp.Run(["--backend", "ids", "--port", "0"], new StringWriter(), new StringWriter(), CancellationToken.None, seams));
            _server.Start();
            Assert.True(listening.Wait(TimeSpan.FromSeconds(10)), "server did not start listening");
            _port = port;
        }

        public int Port => _port;

        public void Dispose()
        {
            // the client has gone (it is disposed first): the server ends after the session
            Assert.True(_server.Join(TimeSpan.FromSeconds(10)), "server did not exit after the client left");
            Assert.Equal(ExitCodes.Ok, _code);
        }
    }

    [Fact]
    public void AnUnpluggedCameraTellsTheClientAndExitsWithCode1()
    {
        int port = 0;
        var listening = new ManualResetEventSlim();
        var seams = SeamsWith(p =>
        {
            port = p;
            listening.Set();
        });
        int code = -1;
        var server = new Thread(() => code = CameraServerApp.Run(["--backend", "ids", "--port", "0"], new StringWriter(), new StringWriter(), CancellationToken.None, seams));
        server.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)));

        using var client = LoopbackClient.Connect(port);
        client.Handshake();
        client.ReadUntil(MessageType.Frame);

        _api.SignalRemoval();

        var error = client.ReadUntil(MessageType.Error);
        Assert.Contains("unplugged", System.Text.Encoding.UTF8.GetString(error.Payload));
        Assert.True(server.Join(TimeSpan.FromSeconds(10)), "server did not exit after the camera was removed");
        Assert.Equal(ExitCodes.Fatal, code);
    }
}
