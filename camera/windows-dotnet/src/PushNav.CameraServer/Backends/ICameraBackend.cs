// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Backends;

/// <summary>A camera a backend found.</summary>
/// <param name="BackendId"><c>"ids"</c>, <c>"uvc"</c>, <c>"fake"</c>.</param>
/// <param name="Model">Model string, e.g. <c>"UI306xCP-M"</c> (HELLO <c>camera_model</c>).</param>
/// <param name="Serial">Serial number if the backend knows it.</param>
/// <param name="Vid">USB vendor id, if USB.</param>
/// <param name="Pid">USB product id, if USB.</param>
/// <param name="Handle">Backend-private id used to open the device (device id, symbolic link, index, ...).</param>
internal sealed record DeviceInfo(string BackendId, string Model, string? Serial = null, int? Vid = null, int? Pid = null, string? Handle = null)
{
    /// <summary>
    /// Stable per-model key the engine stores per-camera settings under (spec 4.2):
    /// <c>"&lt;backend&gt;:&lt;vid&gt;:&lt;pid&gt;"</c> (hex, e.g. <c>ids:1409:3060</c>), or <c>"&lt;backend&gt;:&lt;model&gt;"</c> without a USB id.
    /// </summary>
    public string CameraKey => Vid is { } vid && Pid is { } pid
        ? $"{BackendId}:{vid:x4}:{pid:x4}"
        : $"{BackendId}:{Model}";
}

/// <summary>Physical sensor of the opened camera (HELLO <c>capabilities.sensor</c>).</summary>
/// <param name="Width">Full-frame width in pixels (not affected by binning).</param>
/// <param name="Height">Full-frame height in pixels.</param>
/// <param name="PixelSizeUm">Pixel pitch in micrometres, null when unknown.</param>
/// <param name="Mono">The sensor has no color filter.</param>
internal sealed record SensorInfo(int Width, int Height, double? PixelSizeUm, bool Mono);

internal enum PixelFormat
{
    Gray8,
    Yuy2,
    Nv12,
    Mjpg,
}

/// <summary>Width, height, binning and pixel format of the frames currently produced (spec 1.1 "Stream geometry").</summary>
internal readonly record struct StreamGeometry(int Width, int Height, int Binning = 1, PixelFormat Format = PixelFormat.Gray8);

/// <summary>What a frame handed to <see cref="IFrameSink"/> contains.</summary>
internal enum FrameKind
{
    /// <summary>A complete JPEG image (the only kind v1 sends).</summary>
    Jpeg,

    /// <summary>Raw GRAY8 pixels (protocol v2 FRAME_RAW; not sent yet).</summary>
    Gray8,
}

/// <summary>
/// Receives frames from a backend's capture thread. The server keeps only the newest frame
/// (single slot, overwrite: the "no frame queues" rule), so <see cref="OnFrame"/> never blocks on the network.
/// </summary>
internal interface IFrameSink
{
    void OnFrame(ReadOnlySpan<byte> data, FrameKind kind, StreamGeometry geometry);
}

/// <summary>Outcome of <see cref="ICameraBackend.SetControl"/>.</summary>
/// <param name="Accepted">False if the id is unknown or the value was rejected (enum value not offered); nothing changed.</param>
/// <param name="Message">Why it was rejected, for the log.</param>
internal readonly record struct ControlResult(bool Accepted, string? Message = null)
{
    public static ControlResult Ok { get; } = new(true);

    public static ControlResult Rejected(string message) => new(false, message);
}

/// <summary>Something went wrong talking to the camera.</summary>
internal sealed class CameraException : Exception
{
    public CameraException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One family of cameras (spec 4.2): probes for devices, opens one, produces frames, exposes controls.
/// Registered in a static list; there is no plugin loading under Native AOT.
/// </summary>
internal interface ICameraBackend : IDisposable
{
    /// <summary>Short id used by <c>--backend</c> and in the camera key (<c>"ids"</c>, <c>"uvc"</c>, <c>"fake"</c>).</summary>
    string Id { get; }

    /// <summary>Value of HELLO <c>backend</c>, e.g. <c>"windows-dotnet-fake"</c> (spec 7).</summary>
    string HelloName { get; }

    /// <summary>Cheap; never throws; empty when the SDK/driver is missing.</summary>
    IReadOnlyList<DeviceInfo> Probe();

    /// <summary>Opens the device and prepares streaming (auto exposure off, controls read). Throws <see cref="CameraException"/>.</summary>
    void Open(DeviceInfo device);

    /// <summary>The device opened by <see cref="Open"/>.</summary>
    DeviceInfo Device { get; }

    /// <summary>Current frame size, binning and pixel format. Valid after <see cref="Open"/>.</summary>
    StreamGeometry Geometry { get; }

    /// <summary>Frame rate announced as HELLO <c>default_fps</c>.</summary>
    int DefaultFps { get; }

    /// <summary>Physical sensor size and pixel pitch for HELLO <c>capabilities.sensor</c>; null when the backend does not know it.</summary>
    SensorInfo? Sensor => null;

    /// <summary>Snapshot of all controls with real units, ranges and current values. Thread-safe.</summary>
    IReadOnlyList<ControlDescriptor> Controls { get; }

    /// <summary>Applies a control change (clamped and rounded per the protocol); may change other controls or the geometry.</summary>
    ControlResult SetControl(string id, double value);

    /// <summary>Starts producing frames on a backend thread, pushing JPEG frames into <paramref name="sink"/>.</summary>
    void Start(IFrameSink sink);

    void Stop();

    /// <summary>Raised (from any thread) when the device is lost or capture fails for good; the server then exits with code 1.</summary>
    event Action<string>? Fatal;
}
