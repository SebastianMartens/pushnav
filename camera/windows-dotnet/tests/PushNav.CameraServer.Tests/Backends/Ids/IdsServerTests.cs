// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Ids;
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
        Assert.Equal(new string?[] { "exposure", "gain", "fps" }, ids);
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
