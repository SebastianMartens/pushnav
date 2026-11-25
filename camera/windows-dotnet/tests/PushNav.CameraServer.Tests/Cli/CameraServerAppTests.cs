// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Backends.Ids;

namespace PushNav.CameraServer.Tests.Cli;

public class CameraServerAppTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // Never touch a real camera: the IDS backend runs without the uEye API (as on a machine without the IDS Software Suite).
        var seams = new CameraServerApp.Seams
        {
            Delay = _ => { },
            BackendFactory = (o, log) => BackendRegistry.Create(o, log, () => new IdsBackend(null, o.JpegQuality, log)),
        };
        int code = CameraServerApp.Run(args, stdout, stderr, CancellationToken.None, seams);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void FakeBackendListPrintsJsonAndExitsZero()
    {
        var (code, stdout, _) = Run("--backend", "fake", "--list");

        Assert.Equal(ExitCodes.Ok, code);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("camera_server_dotnet", root.GetProperty("server").GetString());
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        var device = Assert.Single(root.GetProperty("devices").EnumerateArray());
        Assert.Equal("fake", device.GetProperty("backend").GetString());
        Assert.Equal("windows-dotnet-fake", device.GetProperty("hello_backend").GetString());
        Assert.Equal("fake:FakeCamera", device.GetProperty("camera_key").GetString());
        Assert.Equal(1936, device.GetProperty("geometry").GetProperty("width").GetInt32());
        Assert.Equal(1216, device.GetProperty("geometry").GetProperty("height").GetInt32());
        string?[] ids = device.GetProperty("controls").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new string?[] { "exposure", "gain", "fps", "binning" }, ids);
    }

    [Fact]
    public void ListWithoutAnyBackendIsAnEmptyListNotAnError()
    {
        var (code, stdout, _) = Run("--list");

        Assert.Equal(ExitCodes.Ok, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(0, doc.RootElement.GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public void BadArgumentsExitWithUsageCodeAndPrintUsage()
    {
        var (code, stdout, stderr) = Run("--port", "nope");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--port", stderr);
        Assert.Contains("Usage:", stderr);
    }

    [Fact]
    public void HelpPrintsUsageToStdoutAndExitsZero()
    {
        var (code, stdout, _) = Run("--help");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("Usage:", stdout);
        Assert.Contains("--backend ids|uvc|fake", stdout);
    }

    [Fact]
    public void NoBackendAvailableExitsWithCode3()
    {
        var (code, _, stderr) = Run("--port", "0");

        Assert.Equal(ExitCodes.NoSupportedCamera, code);
        Assert.Contains("No supported camera found", stderr);
    }

    [Fact]
    public void TheLastLogLinesReachStderrEvenWhenTheBackgroundWriterNeverRuns()
    {
        var stderr = new StringWriter();
        var seams = new CameraServerApp.Seams
        {
            Delay = _ => { },
            BackendFactory = (o, log) => BackendRegistry.Create(o, log, () => new IdsBackend(null, o.JpegQuality, log)),
            LogSchedule = _ => { }, // the pool never gets to run the log writer
        };

        int code = CameraServerApp.Run(["--port", "0"], new StringWriter(), stderr, CancellationToken.None, seams);

        Assert.Equal(ExitCodes.NoSupportedCamera, code);
        Assert.Contains("No supported camera found", stderr.ToString());
    }

    [Fact]
    public void ForcingTheUvcBackendThatIsNotBuiltInExitsWithCode3()
    {
        var (code, _, stderr) = Run("--backend", "uvc", "--port", "0");

        Assert.Equal(ExitCodes.NoSupportedCamera, code);
        Assert.Contains("'uvc'", stderr);
    }

    [Fact]
    public void ForcingTheIdsBackendWithoutTheUEyeApiExitsWithCode3()
    {
        var (code, _, stderr) = Run("--backend", "ids", "--port", "0");

        Assert.Equal(ExitCodes.NoSupportedCamera, code);
        Assert.Contains("No supported camera found", stderr);
        Assert.Contains("IDS Software Suite", stderr);
    }

    [Fact]
    public void ServesAFrameStreamAndExitsZeroAfterTheClientLeaves()
    {
        int? port = null;
        var listening = new ManualResetEventSlim();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var seams = new CameraServerApp.Seams
        {
            Delay = _ => { },
            OnListening = p =>
            {
                port = p;
                listening.Set();
            },
        };

        int code = -1;
        var server = new Thread(() => code = CameraServerApp.Run(["--backend", "fake", "--port", "0"], stdout, stderr, CancellationToken.None, seams));
        server.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)), "server did not start listening");

        using (var client = LoopbackClient.Connect(port!.Value))
        {
            client.Handshake();
            var frame = client.ReadUntil(PushNav.CameraServer.Protocol.MessageType.Frame);
            Assert.Equal(0xFF, frame.Payload[0]);
        }

        Assert.True(server.Join(TimeSpan.FromSeconds(10)), "server did not exit after the client disconnected");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains($"Listening on 127.0.0.1:{port}", stdout.ToString());
    }

    [Fact]
    public void CancellationStopsAServerThatIsWaitingForAClient()
    {
        using var cts = new CancellationTokenSource();
        var listening = new ManualResetEventSlim();
        var seams = new CameraServerApp.Seams { Delay = _ => { }, OnListening = _ => listening.Set() };
        int code = -1;
        var server = new Thread(() => code = CameraServerApp.Run(["--backend", "fake", "--port", "0"], new StringWriter(), new StringWriter(), cts.Token, seams));
        server.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)));

        cts.Cancel();

        Assert.True(server.Join(TimeSpan.FromSeconds(10)), "server did not stop");
        Assert.Equal(ExitCodes.Ok, code);
    }

    [Fact]
    public void FatalBackendErrorExitsWithCode1AndTellsTheClient()
    {
        var holder = new List<FakeBackendHolder>();
        var listening = new ManualResetEventSlim();
        int port = 0;
        var seams = new CameraServerApp.Seams
        {
            Delay = _ => { },
            BackendFactory = (o, log) =>
            {
                var backend = new FakeBackend(null, 90, log);
                holder.Add(new FakeBackendHolder(backend));
                return [backend];
            },
            OnListening = p =>
            {
                port = p;
                listening.Set();
            },
        };
        int code = -1;
        var server = new Thread(() => code = CameraServerApp.Run(["--backend", "fake", "--port", "0"], new StringWriter(), new StringWriter(), CancellationToken.None, seams));
        server.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)));

        using var client = LoopbackClient.Connect(port);
        client.Handshake();
        holder[0].Backend.SimulateDeviceLoss("camera unplugged");

        var error = client.ReadUntil(PushNav.CameraServer.Protocol.MessageType.Error);
        Assert.Equal("camera unplugged", System.Text.Encoding.UTF8.GetString(error.Payload));
        Assert.True(server.Join(TimeSpan.FromSeconds(10)), "server did not exit after the fatal error");
        Assert.Equal(ExitCodes.Fatal, code);
    }

    private sealed record FakeBackendHolder(FakeBackend Backend);

    [Fact]
    public void PortInUseExitsWithCode1()
    {
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        int busyPort = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;

        var stderrWriter = new StringWriter();
        var seams = new CameraServerApp.Seams { Delay = _ => { }, BindRetryDelay = TimeSpan.FromMilliseconds(1) };
        int code = CameraServerApp.Run(
            ["--backend", "fake", "--port", busyPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            new StringWriter(), stderrWriter, CancellationToken.None, seams);
        string stderr = stderrWriter.ToString();

        Assert.Equal(ExitCodes.Fatal, code);
        Assert.Contains("Cannot listen", stderr);
    }
}
