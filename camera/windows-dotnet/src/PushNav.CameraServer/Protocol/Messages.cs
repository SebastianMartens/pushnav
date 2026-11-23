// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PushNav.CameraServer.Protocol;

// JSON payload models. Property names are snake_case on the wire (naming policy on the context);
// null properties are omitted. Everything here is serialised through the source-generated
// ProtocolJsonContext, so nothing needs runtime reflection (Native AOT).

/// <summary>Camera -> app HELLO (SPEC_PROTOCOL_CAMERA.md 4.1).</summary>
internal sealed class HelloMessage
{
    public int ProtocolVersion { get; init; } = ProtocolInfo.Version;

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

    /// <summary>Features of the opened camera that do not change during the session (protocol 11.3); only <c>sensor</c> so far.</summary>
    public HelloCapabilities? Capabilities { get; init; }
}

/// <summary>HELLO <c>capabilities</c> (SPEC_PROTOCOL_CAMERA.md 11.3). Unknown keys are ignored by clients.</summary>
internal sealed class HelloCapabilities
{
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

internal static class ProtocolInfo
{
    /// <summary>Wire protocol version spoken by this build (must match data/VERSION.json and the Python client).</summary>
    public const int Version = 1;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = false)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(ClientHelloMessage))]
[JsonSerializable(typeof(ControlInfoMessage))]
[JsonSerializable(typeof(SetControlMessage))]
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
