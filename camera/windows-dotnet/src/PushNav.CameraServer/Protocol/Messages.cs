// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PushNav.CameraServer.Protocol;

// JSON payload models. Property names are snake_case on the wire (naming policy on the context);
// null properties are omitted. Everything here is serialised through the source-generated
// ProtocolJsonContext, so nothing needs runtime reflection (Native AOT).

/// <summary>Camera -> app HELLO (SPEC_PROTOCOL_CAMERA.md 4.1 and 11.2).</summary>
internal sealed class HelloMessage
{
    /// <summary>Always 1: the v1 client rejects any other value (protocol 11.2); the real maximum is <see cref="MaxProtocolVersion"/>.</summary>
    public int ProtocolVersion { get; init; } = ProtocolInfo.Version;

    /// <summary>Highest protocol version the server speaks; a v1 client ignores the key.</summary>
    public int? MaxProtocolVersion { get; init; }

    public string Backend { get; init; } = "";

    public string BackendVersion { get; init; } = "";

    public string CameraModel { get; init; } = "";

    /// <summary>Stable per-model key the engine stores per-camera settings under (<c>ids:1409:3060</c>); a v1 client ignores it.</summary>
    public string? CameraKey { get; init; }

    /// <summary>Serial number, when the backend knows it.</summary>
    public string? CameraSerial { get; init; }

    public string StreamFormat { get; init; } = "MJPEG";

    public int DefaultWidth { get; init; }

    public int DefaultHeight { get; init; }

    public int DefaultFps { get; init; }

    /// <summary>Features of the opened camera that do not change during the session (protocol 11.3).</summary>
    public HelloCapabilities? Capabilities { get; init; }
}

/// <summary>HELLO <c>capabilities</c> (SPEC_PROTOCOL_CAMERA.md 11.3). Unknown keys are ignored by clients.</summary>
internal sealed class HelloCapabilities
{
    /// <summary>Formats the server can send; <c>"JPEG"</c> is mandatory (<c>"GRAY8"</c> would mean FRAME_RAW).</summary>
    public List<string>? FrameFormats { get; init; }

    /// <summary><c>true</c>: the server sends STREAM_INFO in a v2 session.</summary>
    public bool? StreamInfo { get; init; }

    public HelloSensor? Sensor { get; init; }
}

/// <summary>Physical sensor size, pixel pitch and mono flag; the engine derives plate scale and FOV from it.</summary>
internal sealed class HelloSensor
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Pixel pitch in micrometres; omitted when unknown.</summary>
    public double? PixelSizeUm { get; init; }

    public bool Mono { get; init; }
}

/// <summary>App -> camera HELLO. Only <c>protocol_version</c> matters to the server; the rest is informational.</summary>
internal sealed class ClientHelloMessage
{
    public int? ProtocolVersion { get; init; }

    public string? App { get; init; }

    public string? AppVersion { get; init; }
}

/// <summary>CONTROL_INFO payload.</summary>
internal sealed class ControlInfoMessage
{
    public List<ControlJson> Controls { get; init; } = [];
}

/// <summary>One control descriptor (v1 uses only <c>int</c> controls; the optional fields are the v2 superset).</summary>
internal sealed class ControlJson
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public string Type { get; init; } = "int";

    public string? Unit { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    public double Cur { get; init; }

    public string? Scale { get; init; }

    public bool? RestartsStream { get; init; }

    public List<ControlOptionJson>? Options { get; init; }
}

internal sealed class ControlOptionJson
{
    public double Value { get; init; }

    public string Label { get; init; } = "";
}

/// <summary>SET_CONTROL payload. <c>value</c> is a number (int in v1, may be float in v2).</summary>
internal sealed class SetControlMessage
{
    public string? Id { get; init; }

    public double? Value { get; init; }
}

/// <summary>STREAM_INFO payload (protocol 11.5): what the frames that follow look like.</summary>
internal sealed class StreamInfoMessage
{
    /// <summary>1 at the start of the session, +1 after every reconfiguration.</summary>
    public int Generation { get; init; }

    /// <summary><c>"streaming"</c> or <c>"reconfiguring"</c> (the stream restarts; the size is still the old one).</summary>
    public string State { get; init; } = StreamStates.Streaming;

    public int Width { get; init; }

    public int Height { get; init; }

    public int Binning { get; init; }

    public string FrameFormat { get; init; } = FrameFormats.Jpeg;

    /// <summary>Time between frames; the client derives its stall timeout from it.</summary>
    public long FrameIntervalUs { get; init; }

    public long ExposureUs { get; init; }
}

internal static class StreamStates
{
    public const string Streaming = "streaming";
    public const string Reconfiguring = "reconfiguring";
}

internal static class FrameFormats
{
    public const string Jpeg = "JPEG";
}

/// <summary>SET_STREAM_CONFIG payload (protocol 11.6).</summary>
internal sealed class SetStreamConfigMessage
{
    public string? FrameFormat { get; init; }
}

internal static class ProtocolInfo
{
    /// <summary>The <c>protocol_version</c> a server HELLO carries, and the version of a v1 session (must match the Python client's minimum).</summary>
    public const int Version = 1;

    /// <summary>Lowest client version a session accepts.</summary>
    public const int MinVersion = 1;

    /// <summary>Highest version this build speaks (<c>max_protocol_version</c>; data/VERSION.json says the same for the whole release).</summary>
    public const int MaxVersion = 2;

    public static bool IsSupported(int clientVersion) => clientVersion is >= MinVersion and <= MaxVersion;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = false)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(ClientHelloMessage))]
[JsonSerializable(typeof(ControlInfoMessage))]
[JsonSerializable(typeof(SetControlMessage))]
[JsonSerializable(typeof(StreamInfoMessage))]
[JsonSerializable(typeof(SetStreamConfigMessage))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}

/// <summary>Encode/decode helpers for the JSON payloads.</summary>
internal static class ProtocolJson
{
    public static byte[] Serialize(HelloMessage hello) =>
        JsonSerializer.SerializeToUtf8Bytes(hello, ProtocolJsonContext.Default.HelloMessage);

    public static byte[] Serialize(ControlInfoMessage info) =>
        JsonSerializer.SerializeToUtf8Bytes(info, ProtocolJsonContext.Default.ControlInfoMessage);

    public static byte[] Serialize(StreamInfoMessage info) =>
        JsonSerializer.SerializeToUtf8Bytes(info, ProtocolJsonContext.Default.StreamInfoMessage);

    public static StreamInfoMessage? ParseStreamInfo(ReadOnlySpan<byte> payload) =>
        TryParse(payload, ProtocolJsonContext.Default.StreamInfoMessage);

    public static SetStreamConfigMessage? ParseSetStreamConfig(ReadOnlySpan<byte> payload) =>
        TryParse(payload, ProtocolJsonContext.Default.SetStreamConfigMessage);

    public static byte[] SerializeSetStreamConfig(SetStreamConfigMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.SetStreamConfigMessage);

    public static HelloMessage? ParseHello(ReadOnlySpan<byte> payload) =>
        TryParse(payload, ProtocolJsonContext.Default.HelloMessage);

    public static ClientHelloMessage? ParseClientHello(ReadOnlySpan<byte> payload) =>
        TryParse(payload, ProtocolJsonContext.Default.ClientHelloMessage);

    public static ControlInfoMessage? ParseControlInfo(ReadOnlySpan<byte> payload) =>
        TryParse(payload, ProtocolJsonContext.Default.ControlInfoMessage);

    public static SetControlMessage? ParseSetControl(ReadOnlySpan<byte> payload) =>
        TryParse(payload, ProtocolJsonContext.Default.SetControlMessage);

    public static byte[] SerializeSetControl(SetControlMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.SetControlMessage);

    public static byte[] SerializeClientHello(ClientHelloMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ClientHelloMessage);

    /// <summary>Returns null for malformed JSON, a JSON <c>null</c>, or a wrong shape.</summary>
    private static T? TryParse<T>(ReadOnlySpan<byte> payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(payload, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
