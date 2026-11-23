// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Backends.Ids;
using PushNav.CameraServer.Protocol;
using PushNav.CameraServer.Server;
using PushNav.CameraServer.Tests.Backends.Ids;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>
/// Task 7.0: HELLO carries <c>camera_key</c>, <c>camera_serial</c> and <c>capabilities.sensor</c> in every session,
/// with the protocol version still 1 (SPEC_PROTOCOL_CAMERA.md 11.2 / 11.3, SPEC_CAMERA_CONTROLS.md 4.2).
/// </summary>
public class HelloExtrasTests
{
    private static JsonElement Hello(ICameraBackend backend) =>
        JsonDocument.Parse(ProtocolJson.Serialize(ClientSession.BuildHello(backend))).RootElement;

    [Fact]
    public void HelloJsonMatchesTheSpecExample()
    {
        var hello = new HelloMessage
        {
            Backend = "windows-dotnet-ids",
            BackendVersion = "0.1.0",
            CameraModel = "UI306xCP-M",
            CameraKey = "ids:1409:3060",
            CameraSerial = "4102825084",
            DefaultWidth = 1936,
            DefaultHeight = 1216,
            DefaultFps = 10,
            Capabilities = new HelloCapabilities { Sensor = new HelloSensor { Width = 1936, Height = 1216, PixelSizeUm = 5.86, Mono = true } },
        };

        var root = JsonDocument.Parse(ProtocolJson.Serialize(hello)).RootElement;

        Assert.Equal(
            ["protocol_version", "backend", "backend_version", "camera_model", "camera_key", "camera_serial", "stream_format", "default_width", "default_height", "default_fps", "capabilities"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.False(root.TryGetProperty("max_protocol_version", out _)); // v2 is a later task
        Assert.Equal("ids:1409:3060", root.GetProperty("camera_key").GetString());
        Assert.Equal("4102825084", root.GetProperty("camera_serial").GetString());

        var capabilities = root.GetProperty("capabilities");
        Assert.Equal(["sensor"], capabilities.EnumerateObject().Select(p => p.Name));
        var sensor = capabilities.GetProperty("sensor");
        Assert.Equal(["width", "height", "pixel_size_um", "mono"], sensor.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1936, sensor.GetProperty("width").GetInt32());
        Assert.Equal(1216, sensor.GetProperty("height").GetInt32());
        Assert.Equal(5.86, sensor.GetProperty("pixel_size_um").GetDouble());
        Assert.True(sensor.GetProperty("mono").GetBoolean());
    }

    [Fact]
    public void PixelSizeIsOmittedWhenUnknown()
    {
        var hello = new HelloMessage { Capabilities = new HelloCapabilities { Sensor = new HelloSensor { Width = 1280, Height = 720, Mono = false } } };

        var sensor = JsonDocument.Parse(ProtocolJson.Serialize(hello)).RootElement.GetProperty("capabilities").GetProperty("sensor");

        Assert.False(sensor.TryGetProperty("pixel_size_um", out _));
        Assert.False(sensor.GetProperty("mono").GetBoolean());
    }

    [Fact]
    public void HelloWithExtrasRoundTrips()
    {
        var parsed = ProtocolJson.ParseHello(ProtocolJson.Serialize(new HelloMessage
        {
            CameraKey = "fake:FakeCamera",
            CameraSerial = "S1",
            Capabilities = new HelloCapabilities { Sensor = new HelloSensor { Width = 4, Height = 3, PixelSizeUm = 2.5, Mono = true } },
        }));

        Assert.NotNull(parsed);
        Assert.Equal(("fake:FakeCamera", "S1"), (parsed.CameraKey, parsed.CameraSerial));
        Assert.Equal((4, 3, 2.5, true), (parsed.Capabilities!.Sensor!.Width, parsed.Capabilities.Sensor.Height, parsed.Capabilities.Sensor.PixelSizeUm, parsed.Capabilities.Sensor.Mono));
    }

    [Fact]
    public void ABackendWithoutSensorFactsSendsNoCapabilities()
    {
        using var backend = new StubBackend();
        backend.Open(backend.Probe()[0]);

        var root = Hello(backend);

        Assert.Equal("stub:1234:abcd", root.GetProperty("camera_key").GetString()); // vid:pid in hex
        Assert.False(root.TryGetProperty("capabilities", out _));
        Assert.False(root.TryGetProperty("camera_serial", out _));
        Assert.False(root.TryGetProperty("max_protocol_version", out _));
    }

    [Fact]
    public void FakeBackendReportsTheIdsSensor()
    {
        using var backend = new FakeBackend(null, 90, new Logger(TextWriter.Null, LogLevel.Debug));
        backend.Open(backend.Probe()[0]);

        var root = Hello(backend);

        Assert.Equal("fake:FakeCamera", root.GetProperty("camera_key").GetString()); // no USB id: backend:model
        Assert.Equal("FAKE0001", root.GetProperty("camera_serial").GetString());
        var sensor = root.GetProperty("capabilities").GetProperty("sensor");
        Assert.Equal((1936, 1216, 5.86, true), (sensor.GetProperty("width").GetInt32(), sensor.GetProperty("height").GetInt32(), sensor.GetProperty("pixel_size_um").GetDouble(), sensor.GetProperty("mono").GetBoolean()));
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
    }

    [Fact]
    public void IdsBackendReportsItsKeySerialAndSensorFromTheDriver()
    {
        using var api = new FakeUEyeApi();
        using var backend = new IdsBackend(api, 90, new Logger(TextWriter.Null, LogLevel.Debug)) { MinimumFrameTimeoutMilliseconds = 20 };
        Assert.Null(backend.Sensor); // not opened yet
        backend.Open(backend.Probe()[0]);

        var root = Hello(backend);

        Assert.Equal("ids:1409:3060", root.GetProperty("camera_key").GetString());
        Assert.Equal(backend.Device.Serial, root.GetProperty("camera_serial").GetString());
        var sensor = root.GetProperty("capabilities").GetProperty("sensor");
        Assert.Equal((FakeUEyeApi.Width, FakeUEyeApi.Height), (sensor.GetProperty("width").GetInt32(), sensor.GetProperty("height").GetInt32()));
        Assert.Equal(5.86, sensor.GetProperty("pixel_size_um").GetDouble(), precision: 6); // the driver reports 5860 nm
        Assert.True(sensor.GetProperty("mono").GetBoolean());
        // The HELLO size is the stream size, the sensor is the sensor: they are separate fields.
        Assert.Equal(FakeUEyeApi.Width, root.GetProperty("default_width").GetInt32());
    }

    [Fact]
    public void ASessionSendsTheExtrasInAV1Hello()
    {
        using var h = ServerHarness.Start();
        using var client = h.Connect();

        var hello = client.Read();

        Assert.Equal(MessageType.Hello, hello.Type);
        var root = JsonDocument.Parse(hello.Payload).RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("stub:1234:abcd", root.GetProperty("camera_key").GetString());
        Assert.False(root.TryGetProperty("max_protocol_version", out _));

        // A v1 client answers as before and gets exactly the v1 session.
        client.SendJson(MessageType.Hello, ProtocolJson.SerializeClientHello(new ClientHelloMessage { ProtocolVersion = 1, App = "evf", AppVersion = "0.2.1" }));
        Assert.Equal(MessageType.ControlInfo, client.Read().Type);
    }
}
