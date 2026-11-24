// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>
/// Task 7.1: a v2 session against the fake backend (synthetic star field, 1936x1216, binning 1 or 2): the handshake, the native
/// controls without the v1 cap, CONTROL_INFO after every SET_CONTROL, STREAM_INFO and the reconfiguration around binning.
/// A v1 client of the same server still gets exactly the v1 session.
/// </summary>
public class V2SessionTests
{
    private static readonly Logger NullLog = new(TextWriter.Null, LogLevel.Debug);

    /// <summary>Server + capture over <paramref name="backend"/> on a free loopback port.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ICameraBackend _backend;
        private readonly CameraTcpServer _server;
        private readonly Task<int> _exit;

        public Harness(ICameraBackend? backend = null)
        {
            _backend = backend ?? new FakeBackend(null, 90, NullLog);
            _backend.Open(_backend.Probe()[0]);
            var slot = new LatestFrameSlot();
            _server = new CameraTcpServer(_backend, slot, NullLog, port: 0);
            _server.Start();
            _backend.Fatal += _server.ReportFatal;
            _backend.Start(new LatestFrameSink(slot));
            _exit = Task.Run(() => _server.Run(_stop.Token));
        }

        public LoopbackClient Connect() => LoopbackClient.Connect(_server.Port);

        public int WaitForExit() => _exit.Wait(TimeSpan.FromSeconds(10)) ? _exit.Result : throw new TimeoutException("server did not exit");

        public void Dispose()
        {
            _stop.Cancel();
            _exit.Wait(TimeSpan.FromSeconds(5));
            _backend.Dispose();
            _server.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>A fake camera whose stream cannot be restarted: the binning change throws.</summary>
    private sealed class FailingRestartBackend : ICameraBackend
    {
        private readonly FakeBackend _inner = new(null, 90, NullLog);

        public string Id => _inner.Id;

        public string HelloName => _inner.HelloName;

        public DeviceInfo Device => _inner.Device;

        public StreamGeometry Geometry => _inner.Geometry;

        public int DefaultFps => _inner.DefaultFps;

        public SensorInfo? Sensor => _inner.Sensor;

        public IReadOnlyList<ControlDescriptor> Controls => _inner.Controls;

        public event Action<string>? Fatal
        {
            add => _inner.Fatal += value;
            remove => _inner.Fatal -= value;
        }

        public IReadOnlyList<DeviceInfo> Probe() => _inner.Probe();

        public void Open(DeviceInfo device) => _inner.Open(device);

        public ControlResult SetControl(string id, double value) =>
            id == ControlIds.Binning ? throw new CameraException("image buffers could not be allocated") : _inner.SetControl(id, value);

        public void Start(IFrameSink sink) => _inner.Start(sink);

        public void Stop() => _inner.Stop();

        public void Dispose() => _inner.Dispose();
    }

    private static ControlJson Control(ControlInfoMessage info, string id) => info.Controls.Single(c => c.Id == id);

    /// <summary>Width and height from the JPEG's start-of-frame marker.</summary>
    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        for (int i = 2; i + 8 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            }
        }

        throw new InvalidDataException("no start-of-frame marker in the JPEG");
    }

    // -- handshake -----------------------------------------------------------

    [Fact]
    public void A_v2_handshake_sends_control_info_then_stream_info_generation_1_before_the_first_frame()
    {
        using var h = new Harness();
        using var client = h.Connect();

        var (controls, stream) = client.HandshakeV2();

        Assert.Equal(["exposure", "gain", "fps", "binning"], controls.Controls.Select(c => c.Id));
        Assert.Equal(1, stream.Generation);
        Assert.Equal("streaming", stream.State);
        Assert.Equal((1936, 1216, 1), (stream.Width, stream.Height, stream.Binning));
        Assert.Equal("JPEG", stream.FrameFormat);
        Assert.Equal(100_000, stream.FrameIntervalUs); // 10 fps
        Assert.Equal(50_000, stream.ExposureUs);
        Assert.Equal(MessageType.Frame, client.Read().Type); // nothing but frames after STREAM_INFO
    }

    [Fact]
    public void Exposure_is_in_microseconds_up_to_4_s_without_the_v1_cap()
    {
        using var h = new Harness();
        using var client = h.Connect();

        var (controls, _) = client.HandshakeV2();

        var exposure = Control(controls, "exposure");
        Assert.Equal("us", exposure.Unit);
        Assert.Equal(4_000_000, exposure.Max);
        Assert.Equal("log", exposure.Scale);
        var fps = Control(controls, "fps");
        Assert.Equal(("float", "fps"), (fps.Type, fps.Unit));
        Assert.Equal(("int", "raw"), (Control(controls, "gain").Type, Control(controls, "gain").Unit));
        var binning = Control(controls, "binning");
        Assert.Equal("enum", binning.Type);
        Assert.True(binning.RestartsStream);
        Assert.Equal([1, 2], binning.Options!.Select(o => o.Value));
    }

    [Fact]
    public void A_v1_client_of_the_same_server_gets_exactly_the_v1_session()
    {
        using var h = new Harness();
        using var client = h.Connect();

        var controls = client.Handshake(clientVersion: 1);

        Assert.Equal(["exposure", "gain"], controls.Controls.Select(c => c.Id));
        Assert.Equal("100us", Control(controls, "exposure").Unit);
        Assert.Equal(15_000, Control(controls, "exposure").Max);
        client.SetControl("binning", 2); // not a v1 control: ignored, but still answered
        client.SetControl("exposure", 100_000); // far beyond 1.5 s: capped
        var seen = client.ReadThroughControlInfos(2);

        Assert.DoesNotContain(MessageType.StreamInfo, seen);
        Assert.Equal(15_000, Control(client.LastControlInfo!, "exposure").Cur);
        Assert.Equal(1, client.ServerHello!.ProtocolVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void A_client_version_outside_1_to_2_is_refused_with_an_error(int version)
    {
        using var h = new Harness();
        using var client = h.Connect();
        Assert.Equal(MessageType.Hello, client.Read().Type);

        client.SendJson(MessageType.Hello, ProtocolJson.SerializeClientHello(new ClientHelloMessage { ProtocolVersion = version }));

        var error = client.Read();
        Assert.Equal(MessageType.Error, error.Type);
        Assert.Contains("Protocol version mismatch", Encoding.UTF8.GetString(error.Payload));
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    // -- controls and coupling ----------------------------------------------------

    [Fact]
    public void An_exposure_beyond_1_5_s_is_taken_as_it_is_and_the_frame_interval_follows_it()
    {
        using var h = new Harness();
        using var client = h.Connect();
        var (_, first) = client.HandshakeV2();

        client.SetControl("exposure", 3_000_000);

        var (info, _) = client.ReadUntilStreamInfo("streaming");
        Assert.Equal(3_000_000, Control(client.LastControlInfo!, "exposure").Cur);
        Assert.Equal(1.0, Control(client.LastControlInfo!, "fps").Cur); // exposure priority: fps drops to its floor
        Assert.Equal(first.Generation, info.Generation);
        Assert.Equal(3_000_000, info.FrameIntervalUs);
        Assert.Equal(3_000_000, info.ExposureUs);
    }

    [Fact]
    public void A_longer_exposure_lowers_fps_and_the_ranges_in_control_info_move()
    {
        using var h = new Harness();
        using var client = h.Connect();
        var (controls, _) = client.HandshakeV2();
        double fpsMaxBefore = Control(controls, "fps").Max!.Value;

        client.SetControl("exposure", 200_000);

        var (info, _) = client.ReadUntilStreamInfo("streaming");
        var after = client.LastControlInfo!;
        Assert.Equal(200_000, Control(after, "exposure").Cur);
        Assert.Equal(5.0, Control(after, "fps").Cur);
        Assert.True(Control(after, "fps").Max < fpsMaxBefore); // a longer exposure lowers the highest rate
        Assert.Equal(200_000, info.FrameIntervalUs);
    }

    [Fact]
    public void Control_info_precedes_the_stream_info_that_a_change_causes()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.SetControl("fps", 4);

        var (info, before) = client.ReadUntilStreamInfo("streaming");
        Assert.Contains(before, m => m.Type == MessageType.ControlInfo);
        Assert.Equal(250_000, info.FrameIntervalUs);
        Assert.Equal(4.0, client.LastControlInfo!.Controls.Single(c => c.Id == "fps").Cur);
    }

    [Fact]
    public void A_change_that_leaves_the_frame_interval_alone_sends_control_info_and_no_stream_info()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.SetControl("gain", 55);
        client.Send(MessageType.GetControls, []); // a second CONTROL_INFO marks the end of what the gain change sent
        var seen = client.ReadThroughControlInfos(2);

        Assert.DoesNotContain(MessageType.StreamInfo, seen);
        Assert.Equal(55, Control(client.LastControlInfo!, "gain").Cur);
    }

    [Fact]
    public void A_control_the_camera_does_not_advertise_is_ignored_but_answered_with_control_info()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.SetControl("shutter-speed", 3);

        var info = ProtocolJson.ParseControlInfo(client.ReadUntil(MessageType.ControlInfo).Payload)!;
        Assert.Equal(4, info.Controls.Count);
        client.ReadUntil(MessageType.Frame); // still streaming
    }

    // -- binning: reconfiguring -> streaming ----------------------------------------

    [Fact]
    public void Binning_reconfigures_the_stream_and_no_frame_of_the_new_size_precedes_its_stream_info()
    {
        using var h = new Harness();
        using var client = h.Connect();
        var (_, first) = client.HandshakeV2();

        client.SetControl("binning", 2);

        var (reconfiguring, framesBefore) = client.ReadUntilStreamInfo("reconfiguring");
        var (streaming, framesDuring) = client.ReadUntilStreamInfo("streaming");
        var frame = client.ReadUntil(MessageType.Frame);

        Assert.Equal(first.Generation, reconfiguring.Generation);
        Assert.Equal((first.Width, first.Height), (reconfiguring.Width, reconfiguring.Height)); // still the old size
        Assert.Equal(first.Generation + 1, streaming.Generation);
        Assert.Equal((first.Width / 2, first.Height / 2, 2), (streaming.Width, streaming.Height, streaming.Binning));
        Assert.Equal(2, Control(client.LastControlInfo!, "binning").Cur);
        foreach (var old in framesBefore.Concat(framesDuring).Where(m => m.Type == MessageType.Frame))
        {
            Assert.Equal((first.Width, first.Height), JpegSize(old.Payload));
        }

        Assert.Equal((streaming.Width, streaming.Height), JpegSize(frame.Payload));
    }

    [Fact]
    public void After_a_binning_change_the_old_size_never_comes_back()
    {
        using var h = new Harness();
        using var client = h.Connect();
        var (_, first) = client.HandshakeV2();
        client.SetControl("binning", 2);
        client.ReadUntilStreamInfo("streaming");

        for (int i = 0; i < 15; i++)
        {
            var frame = client.ReadUntil(MessageType.Frame);
            Assert.Equal((first.Width / 2, first.Height / 2), JpegSize(frame.Payload));
        }
    }

    [Fact]
    public void Binning_back_to_1x1_gives_the_next_generation()
    {
        using var h = new Harness();
        using var client = h.Connect();
        var (_, first) = client.HandshakeV2();
        client.SetControl("binning", 2);
        client.ReadUntilStreamInfo("streaming");

        client.SetControl("binning", 1);

        var (streaming, _) = client.ReadUntilStreamInfo("streaming");
        Assert.Equal(first.Generation + 2, streaming.Generation);
        Assert.Equal((first.Width, first.Height, 1), (streaming.Width, streaming.Height, streaming.Binning));
    }

    [Fact]
    public void Setting_the_binning_it_already_has_does_not_restart_the_stream()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.SetControl("binning", 1);
        client.Send(MessageType.GetControls, []);
        var seen = client.ReadThroughControlInfos(2);

        Assert.DoesNotContain(MessageType.StreamInfo, seen);
    }

    [Fact]
    public void A_binning_the_camera_does_not_offer_is_rejected_without_a_restart()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.SetControl("binning", 3);
        client.Send(MessageType.GetControls, []);
        var seen = client.ReadThroughControlInfos(2);

        Assert.DoesNotContain(MessageType.StreamInfo, seen);
        Assert.Equal(1, Control(client.LastControlInfo!, "binning").Cur);
    }

    [Fact]
    public void A_stream_that_cannot_be_restarted_ends_the_session_with_an_error_and_code_1()
    {
        using var h = new Harness(new FailingRestartBackend());
        using var client = h.Connect();
        client.HandshakeV2();

        client.SetControl("binning", 2);

        var error = client.ReadUntil(MessageType.Error);
        Assert.Contains("image buffers could not be allocated", Encoding.UTF8.GetString(error.Payload));
        Assert.Equal(ExitCodes.Fatal, h.WaitForExit());
    }

    // -- SET_STREAM_CONFIG ------------------------------------------------------------

    [Fact]
    public void A_stream_config_for_the_format_in_use_is_answered_with_the_same_generation()
    {
        using var h = new Harness();
        using var client = h.Connect();
        var (_, first) = client.HandshakeV2();

        client.Send(MessageType.SetStreamConfig, Encoding.UTF8.GetBytes("{\"frame_format\":\"JPEG\"}"));

        var (info, _) = client.ReadUntilStreamInfo("streaming");
        Assert.Equal(first.Generation, info.Generation);
        Assert.Equal("JPEG", info.FrameFormat);
    }

    [Theory]
    [InlineData("{\"frame_format\":\"GRAY8\"}")]
    [InlineData("{\"frame_format\":\"BAYER16\"}")]
    [InlineData("{not json")]
    public void A_stream_config_for_a_format_that_is_not_advertised_is_ignored(string payload)
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.Send(MessageType.SetStreamConfig, Encoding.UTF8.GetBytes(payload));
        client.Send(MessageType.GetControls, []);

        var seen = client.ReadThroughControlInfos(1);

        Assert.DoesNotContain(MessageType.StreamInfo, seen);
        Assert.Equal(MessageType.Frame, client.ReadUntil(MessageType.Frame).Type);
    }

    [Fact]
    public void A_v1_session_ignores_a_stream_config()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.Handshake(clientVersion: 1);

        client.Send(MessageType.SetStreamConfig, Encoding.UTF8.GetBytes("{\"frame_format\":\"JPEG\"}"));
        client.Send(MessageType.GetControls, []);

        var seen = client.ReadThroughControlInfos(1);

        Assert.DoesNotContain(MessageType.StreamInfo, seen);
    }

    [Fact]
    public void An_unknown_message_type_keeps_a_v2_stream_going()
    {
        using var h = new Harness();
        using var client = h.Connect();
        client.HandshakeV2();

        client.Send(0x7F, Encoding.UTF8.GetBytes("whatever"));
        client.Send(MessageType.GetControls, []);

        client.ReadUntil(MessageType.ControlInfo);
        client.ReadUntil(MessageType.Frame);
    }
}
