// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text;
using System.Text.Json;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Tests.Protocol;

public class ProtocolJsonTests
{
    private static JsonElement Parse(byte[] json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void HelloHasExactlyTheV1FieldsInSnakeCase()
    {
        var hello = new HelloMessage
        {
            Backend = "windows-dotnet-fake",
            BackendVersion = "0.1.0",
            CameraModel = "FakeCamera",
            DefaultWidth = 1936,
            DefaultHeight = 1216,
            DefaultFps = 10,
        };

        var root = Parse(ProtocolJson.Serialize(hello));

        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            ["protocol_version", "backend", "backend_version", "camera_model", "stream_format", "default_width", "default_height", "default_fps"],
            names);
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("MJPEG", root.GetProperty("stream_format").GetString());
        Assert.Equal(1936, root.GetProperty("default_width").GetInt32());
    }

    [Fact]
    public void HelloRoundTrips()
    {
        var parsed = ProtocolJson.ParseHello(ProtocolJson.Serialize(new HelloMessage { Backend = "b", BackendVersion = "1.2.3", CameraModel = "m", DefaultWidth = 4, DefaultHeight = 3, DefaultFps = 30 }));

        Assert.NotNull(parsed);
        Assert.Equal((1, "b", "1.2.3", "m", 4, 3, 30), (parsed.ProtocolVersion, parsed.Backend, parsed.BackendVersion, parsed.CameraModel, parsed.DefaultWidth, parsed.DefaultHeight, parsed.DefaultFps));
    }

    [Fact]
    public void V1ControlInfoMatchesTheSpecShape()
    {
        var native = new List<ControlDescriptor>
        {
            new() { Id = "exposure", Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 92, Max = 4_000_000, Step = 1, Cur = 50_000 },
            new() { Id = "gain", Label = "Gain", Kind = ControlKind.Int, Unit = "raw", Min = 0, Max = 100, Step = 1, Cur = 20 },
            new() { Id = "fps", Label = "Frame rate", Kind = ControlKind.Float, Unit = "fps", Min = 1, Max = 40, Step = 0.01, Cur = 10 },
        };

        var root = Parse(ProtocolJson.Serialize(V1ControlAdapter.Project(native)));

        var controls = root.GetProperty("controls").EnumerateArray().ToArray();
        Assert.Equal(2, controls.Length); // v1: exposure and gain only
        var exposure = controls[0];
        Assert.Equal(
            ["id", "label", "type", "unit", "min", "max", "step", "cur"],
            exposure.EnumerateObject().Select(p => p.Name).ToArray()); // no v2-only keys in a v1 session
        Assert.Equal("exposure", exposure.GetProperty("id").GetString());
        Assert.Equal("int", exposure.GetProperty("type").GetString());
        Assert.Equal("100us", exposure.GetProperty("unit").GetString());
        Assert.Equal(1, exposure.GetProperty("min").GetInt64());      // ceil(92 us / 100 us)
        Assert.Equal(15_000, exposure.GetProperty("max").GetInt64()); // 4 s native, capped at 1.5 s for v1 (engine stall timeout is 2 s)
        Assert.Equal(1, exposure.GetProperty("step").GetInt64());
        Assert.Equal(500, exposure.GetProperty("cur").GetInt64());    // 50 ms = 500 x 100 us
        var gain = controls[1];
        Assert.Equal("gain", gain.GetProperty("id").GetString());
        Assert.Equal("raw", gain.GetProperty("unit").GetString());
        Assert.Equal(20, gain.GetProperty("cur").GetInt64());
    }

    [Fact]
    public void ControlNumbersAreWrittenAsJsonIntegersSoThePythonClientSeesInts()
    {
        var native = new List<ControlDescriptor>
        {
            new() { Id = "exposure", Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 100, Max = 100_000, Step = 1, Cur = 5000 },
        };

        string text = Encoding.UTF8.GetString(ProtocolJson.Serialize(V1ControlAdapter.Project(native)));

        Assert.Contains("\"min\":1,", text);
        Assert.Contains("\"cur\":50}", text);
        Assert.DoesNotContain(".0", text);
    }

    [Fact]
    public void V1ProjectionOmitsControlsTheBackendDoesNotHave()
    {
        var onlyGain = new List<ControlDescriptor>
        {
            new() { Id = "gain", Label = "Gain", Kind = ControlKind.Int, Min = 0, Max = 10, Step = 1, Cur = 3 },
        };

        var info = V1ControlAdapter.Project(onlyGain);

        Assert.Equal(["gain"], info.Controls.Select(c => c.Id));
    }

    [Fact]
    public void V1ExposureValuesConvertBetweenUnits()
    {
        Assert.True(V1ControlAdapter.TryToNative("exposure", 1000, out double us));
        Assert.Equal(100_000, us);
        Assert.True(V1ControlAdapter.TryToNative("gain", 42, out double gain));
        Assert.Equal(42, gain);
        Assert.False(V1ControlAdapter.TryToNative("fps", 5, out _));
        Assert.False(V1ControlAdapter.TryToNative("binning", 2, out _));
    }

    [Fact]
    public void V1ExposureIsCappedBelowTheEnginesFixedStallTimeout()
    {
        var native = new List<ControlDescriptor>
        {
            new() { Id = "exposure", Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 92, Max = 30_000_000, Step = 1, Cur = 3_000_000 },
        };

        var c = V1ControlAdapter.Project(native).Controls[0];

        Assert.Equal(15_000, c.Max);          // 1.5 s in 100 us units
        Assert.Equal(15_000, c.Cur);          // a value above the cap is shown as the cap
        Assert.True(V1ControlAdapter.MaxExposureMicroseconds < 2_000_000, "must stay below the 2.0 s stall timeout");
    }

    [Fact]
    public void V1CapDoesNotRaiseARangeThatIsAlreadyShorter()
    {
        var native = new List<ControlDescriptor>
        {
            new() { Id = "exposure", Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 100, Max = 500_000, Step = 1, Cur = 100_000 },
        };

        Assert.Equal(5_000, V1ControlAdapter.Project(native).Controls[0].Max);
    }

    [Theory]
    [InlineData(1000, 100_000)]
    [InlineData(15_000, 1_500_000)]
    [InlineData(15_001, 1_500_000)]
    [InlineData(40_000, 1_500_000)]
    [InlineData(1_000_000_000, 1_500_000)]
    public void V1SetExposureIsClampedToTheCap(double v1Value, double expectedMicroseconds)
    {
        Assert.True(V1ControlAdapter.TryToNative("exposure", v1Value, out double us));
        Assert.Equal(expectedMicroseconds, us);
    }

    [Fact]
    public void V1ExposureCurrentValueStaysInsideTheAdvertisedRange()
    {
        var native = new List<ControlDescriptor>
        {
            new() { Id = "exposure", Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 92, Max = 4_000_000, Step = 1, Cur = 92 },
        };

        var c = V1ControlAdapter.Project(native).Controls[0];

        Assert.InRange(c.Cur, c.Min!.Value, c.Max!.Value); // 92 us rounds to 1 unit, not 0
    }

    [Theory]
    [InlineData("""{"id":"exposure","value":250}""", "exposure", 250)]
    [InlineData("""{"id":"gain","value":0}""", "gain", 0)]
    [InlineData("""{"value": 12.5, "id": "fps"}""", "fps", 12.5)]
    [InlineData("""{"id":"exposure","value":250,"extra":true}""", "exposure", 250)]
    public void ParsesSetControl(string json, string id, double value)
    {
        var cmd = ProtocolJson.ParseSetControl(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(cmd);
        Assert.Equal(id, cmd.Id);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("""{"id":"exposure","value":"loud"}""")]
    [InlineData("{")]
    public void MalformedSetControlIsNullInsteadOfAnException(string json)
    {
        Assert.Null(ProtocolJson.ParseSetControl(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void SetControlWithoutValueParsesWithNullValue()
    {
        var cmd = ProtocolJson.ParseSetControl("""{"id":"exposure"}"""u8);

        Assert.NotNull(cmd);
        Assert.Null(cmd.Value);
    }

    [Fact]
    public void ParsesTheClientHelloThePythonClientSends()
    {
        var hello = ProtocolJson.ParseClientHello("""{"protocol_version": 1, "app": "evf", "app_version": "0.2.1"}"""u8);

        Assert.NotNull(hello);
        Assert.Equal(1, hello.ProtocolVersion);
        Assert.Equal("evf", hello.App);
        Assert.Equal("0.2.1", hello.AppVersion);
    }

    [Fact]
    public void ClientHelloToleratesUnknownKeysAndMissingVersion()
    {
        var hello = ProtocolJson.ParseClientHello("""{"future":{"x":1}}"""u8);

        Assert.NotNull(hello);
        Assert.Null(hello.ProtocolVersion);
    }

    [Fact]
    public void ControlInfoRoundTripsThroughTheParser()
    {
        var native = FakeControls();

        var parsed = ProtocolJson.ParseControlInfo(ProtocolJson.Serialize(V1ControlAdapter.Project(native)));

        Assert.NotNull(parsed);
        Assert.Equal(["exposure", "gain"], parsed.Controls.Select(c => c.Id));
        Assert.Equal(500, parsed.Controls[0].Cur);
    }

    private static List<ControlDescriptor> FakeControls() =>
    [
        new() { Id = "exposure", Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 92, Max = 4_000_000, Step = 1, Cur = 50_000 },
        new() { Id = "gain", Label = "Gain", Kind = ControlKind.Int, Unit = "raw", Min = 0, Max = 100, Step = 1, Cur = 20 },
    ];
}
