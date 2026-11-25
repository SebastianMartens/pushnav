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
internal readonly record struct StreamGeometry(int Width, int Height, int Binning = 1, PixelFormat Format = PixelFormat.Gray8)
{
    /// <summary>
    /// True if frames of <paramref name="other"/> look like frames of this geometry to the client: same size and binning
    /// (the pixel format is the backend's business; every frame on the wire is a JPEG).
    /// </summary>
    public bool HasSameImageAs(StreamGeometry other) => Width == other.Width && Height == other.Height && Binning == other.Binning;
}

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

/// <summary>One SET_CONTROL as a backend receives it: the control, the value asked for, and what the caller is prepared to handle.</summary>
/// <param name="Id">Control id.</param>
/// <param name="Value">Requested value in the backend's native unit (clamped and rounded by the backend).</param>
/// <param name="MayRestartStream">
/// True when the caller announces a stream restart to its client (a v2 session: STREAM_INFO <c>reconfiguring</c>, then
/// <c>streaming</c> with the next generation, protocol 11.5), so the backend <b>may</b> restart the stream for this change. Without
/// it the backend must not: a change that would need a restart is rejected, and a change that only sometimes restarts (the IDS
/// camera leaves long-exposure mode only if it may) takes the way that does not. A v1 session never sets it: a v1 client has no way
/// to be told, and its stall timeout would fire. The backend stays ignorant of the protocol version, it only reads the caller's intent.
/// </param>
internal readonly record struct ControlChange(string Id, double Value, bool MayRestartStream = false);

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

    /// <summary>
    /// Applies a control change (clamped and rounded per the protocol); may change other controls or the geometry. It restarts the
    /// stream only when <see cref="WouldRestartStream"/> says so for the same <paramref name="change"/>, which implies
    /// <see cref="ControlChange.MayRestartStream"/>: the caller has announced the restart.
    /// <para>
    /// <b>A change that restarts the stream returns only once the first frame after the restart has been captured</b>, or after a
    /// bounded wait (a backend picks about 2 s and logs a warning, it does not fail). The caller announces STREAM_INFO
    /// <c>streaming</c> when this returns, so whatever gap follows the restart must fall inside <c>reconfiguring</c>, where the
    /// engine waits 5 s, and not after <c>streaming</c>, where its stall timeout is only 2 s.
    /// </para>
    /// </summary>
    ControlResult SetControl(ControlChange change);

    /// <summary>
    /// True if <see cref="SetControl"/> with this change restarts the stream (new geometry or restart of the capture), so a v2
    /// session wraps it in STREAM_INFO <c>reconfiguring</c> and <c>streaming</c> with the next generation (protocol 11.5). Never
    /// true without <see cref="ControlChange.MayRestartStream"/>. The default is every change of a control that is marked
    /// <see cref="ControlDescriptor.RestartsStream"/> to a value that is accepted and differs from the current one. A backend
    /// overrides it when whether a change restarts depends on more than the descriptor: the IDS backend leaves long-exposure mode,
    /// which gaps the stream, when the exposure drops back into the normal range, and refuses a binning change while that mode is on.
    /// </summary>
    bool WouldRestartStream(ControlChange change)
    {
        var control = Controls.FirstOrDefault(c => c.Id == change.Id);
        return change.MayRestartStream
               && control is { RestartsStream: true }
               && control.TryCoerce(change.Value, out double coerced)
               && coerced != control.Cur;
    }

    /// <summary>Starts producing frames on a backend thread, pushing JPEG frames into <paramref name="sink"/>.</summary>
    void Start(IFrameSink sink);

    void Stop();

    /// <summary>Raised (from any thread) when the device is lost or capture fails for good; the server then exits with code 1.</summary>
    event Action<string>? Fatal;
}
