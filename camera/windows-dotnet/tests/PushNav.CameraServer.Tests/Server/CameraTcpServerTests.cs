// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>Task 2.2: single-client TCP server, v1 handshake and message handling, exit after disconnect.</summary>
public class CameraTcpServerTests
{
    [Fact]
    public void HandshakeIsHelloThenControlInfoThenFrames()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();

        var hello = client.Read();
        Assert.Equal(MessageType.Hello, hello.Type);
        var serverHello = ProtocolJson.ParseHello(hello.Payload)!;
        Assert.Equal(1, serverHello.ProtocolVersion);
        Assert.Equal("windows-dotnet-stub", serverHello.Backend);
        Assert.Equal("StubCam", serverHello.CameraModel);
        Assert.Equal("MJPEG", serverHello.StreamFormat);
        Assert.Equal((640, 480, 30), (serverHello.DefaultWidth, serverHello.DefaultHeight, serverHello.DefaultFps)); // the stream size, not a hardcoded 1280x720
        Assert.Matches(@"^\d+\.\d+\.\d+$", serverHello.BackendVersion);

        client.SendJson(MessageType.Hello, ProtocolJson.SerializeClientHello(new ClientHelloMessage { ProtocolVersion = 1, App = "evf", AppVersion = "0.2.1" }));

        var info = client.Read();
        Assert.Equal(MessageType.ControlInfo, info.Type);
        var controls = ProtocolJson.ParseControlInfo(info.Payload)!.Controls;
        Assert.Equal(["exposure", "gain"], controls.Select(c => c.Id));
        Assert.Equal("100us", controls[0].Unit);

        var frame = client.Read();
        Assert.Equal(MessageType.Frame, frame.Type);
        Assert.Equal(0xFF, frame.Payload[0]);
        Assert.Equal(0xD8, frame.Payload[1]);
    }

    [Fact]
    public void NoFrameIsSentBeforeTheClientHasAnsweredHello()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect(receiveTimeoutMs: 400);

        Assert.Equal(MessageType.Hello, client.Read().Type);

        // The client stays silent: the server must not stream (nor send CONTROL_INFO) yet.
        Assert.ThrowsAny<Exception>(() => client.Read());
    }

    [Fact]
    public void FirstFrameIsNeverOlderThanTheConnection()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(2)));
        Thread.Sleep(100); // the slot now holds a frame from before any client existed
        long counterAtConnect = h.Backend.Counter;
        Assert.True(counterAtConnect > 5);

        using var client = h.Connect();
        client.Handshake();
        var first = client.ReadUntil(MessageType.Frame);

        Assert.True(StubBackend.CounterOf(first.Payload) > counterAtConnect, "a stale frame was delivered after connecting");
    }

    [Fact]
    public void SetControlIsAnsweredWithControlInfoShowingTheAppliedValue()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(50)));
        using var client = h.Connect();
        client.Handshake();

        client.SetControl("exposure", 1000); // v1 unit 100 us -> 100 ms
        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(1000, client.LastControlInfo!.Controls.Single(c => c.Id == "exposure").Cur);

        client.SetControl("gain", 55);
        client.ReadUntil(MessageType.ControlInfo);
        var controls = client.LastControlInfo!.Controls;
        Assert.Equal(55, controls.Single(c => c.Id == "gain").Cur);
        Assert.Equal(1000, controls.Single(c => c.Id == "exposure").Cur); // earlier change is kept
    }

    [Fact]
    public void V1ClientCannotSetAnExposureLongerThanTheEngineStallTimeout()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(50)));
        using var client = h.Connect();
        var handshake = client.Handshake();
        Assert.Equal(15_000, handshake.Controls.Single(c => c.Id == "exposure").Max); // stub offers up to 4 s natively

        client.SetControl("exposure", 40_000); // 4 s in v1 units
        client.ReadUntil(MessageType.ControlInfo);

        var exposure = client.LastControlInfo!.Controls.Single(c => c.Id == "exposure");
        Assert.Equal(15_000, exposure.Cur);
        Assert.Equal(1_500_000, h.Backend.Controls.Single(c => c.Id == "exposure").Cur); // the camera really got 1.5 s
    }

    [Fact]
    public void SessionKeepsWorkingWhenTheLogOutputIsStuck()
    {
        // Regression: the engine never reads the server's stderr pipe; a blocking log write froze the session reader.
        var stuck = new BlockedWriter();
        var log = new Logger(stuck, LogLevel.Debug); // debug: every SET_CONTROL logs, the worst case
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(20)), log: log);
        using var client = h.Connect();
        client.Handshake();

        int answered = 0;
        for (int i = 0; i < 1000; i++)
        {
            client.SetControl(i % 7 == 0 ? "unknown_control" : "gain", i % 100);
            while (client.Read().Type != MessageType.ControlInfo)
            {
            }

            answered++;
        }

        Assert.Equal(1000, answered);
        Assert.True(log.DroppedCount > 0, "the stuck sink should have cost log lines, not the session");
        client.Dispose();
        Assert.Equal(ExitCodes.Ok, h.WaitForExit()); // and the server still exits after the client leaves
        stuck.Release();
    }

    [Fact]
    public void ControlInfoReportsTheClampedValueNotTheRequestedOne()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        client.SetControl("gain", 5000);
        client.ReadUntil(MessageType.ControlInfo);

        var gain = client.LastControlInfo!.Controls.Single(c => c.Id == "gain");
        Assert.Equal(100, gain.Cur);
    }

    [Fact]
    public void EverySetControlGetsExactlyOneControlInfoReply()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(50)));
        using var client = h.Connect();
        client.Handshake();

        for (int gain = 1; gain <= 5; gain++)
        {
            client.SetControl("gain", gain);
            client.ReadUntil(MessageType.ControlInfo);
            Assert.Equal(gain, client.LastControlInfo!.Controls.Single(c => c.Id == "gain").Cur);
        }
    }

    [Fact]
    public void GetControlsIsAnsweredWithControlInfo()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        client.Send(MessageType.GetControls, []);

        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(2, client.LastControlInfo!.Controls.Count);
    }

    [Fact]
    public void UnknownMessageTypesAreIgnoredAndStreamingContinues()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(10)));
        using var client = h.Connect();
        client.Handshake();

        client.Send(0x7F, [1, 2, 3]);          // never assigned
        client.Send(0x13, []);                  // reserved START_STREAM
        client.Send(0x15, "{\"frame_format\":\"GRAY8\"}"u8); // v2 SET_STREAM_CONFIG, unknown in a v1 session
        client.Send(MessageType.Frame, [9]);    // a camera->app type sent the wrong way round

        // Still alive: answers GET_CONTROLS and keeps sending frames.
        client.Send(MessageType.GetControls, []);
        client.ReadUntil(MessageType.ControlInfo);
        long before = StubBackend.CounterOf(client.ReadUntil(MessageType.Frame).Payload);
        long after = StubBackend.CounterOf(client.ReadUntil(MessageType.Frame).Payload);
        Assert.True(after > before);
    }

    [Fact]
    public void UnknownControlIdIsNotAnErrorAndStillGetsControlInfo()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        client.SetControl("pixel_clock", 118);

        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(2, client.LastControlInfo!.Controls.Count);
    }

    [Fact]
    public void V2OnlyControlsAreNotExposedToAV1Client()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        client.SetControl("binning", 2);
        client.SetControl("fps", 5);
        client.Send(MessageType.GetControls, []);

        client.ReadUntil(MessageType.ControlInfo);
        Assert.DoesNotContain(client.LastControlInfo!.Controls, c => c.Id is "binning" or "fps");
    }

    [Fact]
    public void MalformedSetControlDoesNotKillTheSession()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        client.SendRawText(MessageType.SetControl, "this is not json");
        client.SendRawText(MessageType.SetControl, "{\"id\":\"gain\"}");   // no value
        client.SendRawText(MessageType.SetControl, "{\"value\":3}");       // no id
        client.SetControl("gain", 7);

        client.ReadUntil(MessageType.ControlInfo);
        Assert.Equal(7, client.LastControlInfo!.Controls.Single(c => c.Id == "gain").Cur);
    }

    [Fact]
    public void ServerExitsWithCodeZeroAfterTheClientDisconnects()
    {
        using var h = ServerHarness.Start();
        var client = h.Connect();
        client.Handshake();
        client.ReadUntil(MessageType.Frame);

        client.Dispose();

        Assert.Equal(ExitCodes.Ok, h.WaitForExit());
    }

    [Fact]
    public void ClientThatLeavesDuringTheHandshakeEndsTheServerToo()
    {
        using var h = ServerHarness.Start();
        var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        client.Dispose(); // never answers

        Assert.Equal(ExitCodes.Ok, h.WaitForExit());
    }

    [Fact]
    public void ClientThatNeverAnswersHelloIsDroppedAfterTheHandshakeTimeout()
    {
        using var h = ServerHarness.Start(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        using var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        Assert.Equal(ExitCodes.Ok, h.WaitForExit(timeoutSeconds: 5));
    }

    [Fact]
    public void ProtocolVersionMismatchGetsAnErrorMessageAndCode1()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        client.SendJson(MessageType.Hello, ProtocolJson.SerializeClientHello(new ClientHelloMessage { ProtocolVersion = 3 }));

        var error = client.Read();
        Assert.Equal(MessageType.Error, error.Type);
        Assert.Contains("Protocol version mismatch", Encoding.UTF8.GetString(error.Payload));
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    [Fact]
    public void FirstMessageThatIsNotHelloEndsTheSession()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        client.Send(MessageType.GetControls, []);

        var error = client.Read();
        Assert.Equal(MessageType.Error, error.Type);
        Assert.Contains("Expected HELLO", Encoding.UTF8.GetString(error.Payload));
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    [Fact]
    public void MalformedClientHelloEndsTheSession()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        client.SendRawText(MessageType.Hello, "{{{");

        Assert.Equal(MessageType.Error, client.Read().Type);
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    [Fact]
    public void ClientHelloWithoutAVersionIsTreatedAsV1()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        client.SendRawText(MessageType.Hello, "{\"app\":\"someone\"}");

        Assert.Equal(MessageType.ControlInfo, client.Read().Type);
    }

    [Fact]
    public void OversizeMessageFromTheClientIsRejectedWithoutReadingIt()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), MessageType.SetControl);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), 16 * 1024 * 1024 + 1);
        client.SendRaw(header);

        var error = client.ReadUntil(MessageType.Error);
        Assert.Contains("limit", Encoding.UTF8.GetString(error.Payload));
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    [Fact]
    public void BackendFatalErrorIsReportedToTheClientAndEndsTheServerWithCode1()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        h.Backend.RaiseFatal("device lost");

        var error = client.ReadUntil(MessageType.Error);
        Assert.Equal("device lost", Encoding.UTF8.GetString(error.Payload));
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    [Fact]
    public void BackendFatalErrorBeforeAnyClientEndsTheServerWithCode1()
    {
        using var h = ServerHarness.Start();

        h.Backend.RaiseFatal("no sensor");

        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    [Fact]
    public void StopRequestEndsAnActiveSessionWithCodeZero()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();
        client.Handshake();

        h.StopServer();

        Assert.Equal(ExitCodes.Ok, h.WaitForExit());
    }

    [Fact]
    public void StopRequestEndsAServerWaitingForAClient()
    {
        using var h = ServerHarness.Start();

        h.StopServer();

        Assert.Equal(ExitCodes.Ok, h.WaitForExit());
    }

    [Fact]
    public void ServerAcceptsExactlyOneClient()
    {
        using var h = ServerHarness.Start();
        using var first = h.Connect();
        first.Handshake();
        first.Dispose();
        h.WaitForExit();

        // The session is over and the process would be gone: nobody serves a second client.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var second = h.Connect(receiveTimeoutMs: 500);
            second.Read();
        });
    }

    [Fact]
    public void ListensOnLoopbackOnly()
    {
        using var h = ServerHarness.Start();
        var other = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
        if (other == null)
        {
            return; // no second interface on this machine, nothing to prove
        }

        using var tcp = new System.Net.Sockets.TcpClient();
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => tcp.Connect(other, h.Server.Port));
    }
}
