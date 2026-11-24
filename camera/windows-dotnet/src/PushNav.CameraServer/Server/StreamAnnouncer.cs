// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Server;

/// <summary>
/// The STREAM_INFO bookkeeping of one v2 session (protocol 11.5): which generation is on the wire, what geometry its frames have,
/// and at which frame interval. Pure state, no I/O: the session sends what these methods return, holding its write lock, so
/// <see cref="Admits"/> (asked by the frame writer under the same lock) is always consistent with the STREAM_INFO already sent.
/// </summary>
/// <remarks>
/// Frames are only let through while they match the announced geometry. That is what keeps a frame of the new size from
/// overtaking STREAM_INFO <c>streaming</c> after a binning change, and a frame of the old size from following it.
/// </remarks>
internal sealed class StreamAnnouncer
{
    private int _generation;
    private StreamGeometry _geometry;
    private bool _reconfiguring;
    private long _frameIntervalUs;

    /// <summary>STREAM_INFO of generation 1, sent once after the first CONTROL_INFO.</summary>
    public StreamInfoMessage Start(StreamGeometry geometry, long frameIntervalUs, long exposureUs)
    {
        _generation = 1;
        _geometry = geometry;
        _reconfiguring = false;
        _frameIntervalUs = frameIntervalUs;
        return Describe(StreamStates.Streaming, exposureUs);
    }

    /// <summary>
    /// The stream is about to restart: <c>reconfiguring</c>, same generation, still the old size. No frame goes out until
    /// <see cref="Complete"/> or <see cref="Resume"/>.
    /// </summary>
    public StreamInfoMessage BeginReconfiguration(long exposureUs)
    {
        _reconfiguring = true;
        return Describe(StreamStates.Reconfiguring, exposureUs);
    }

    /// <summary>The restart is done: <c>streaming</c> with the new geometry and generation + 1, before the first frame of the new size.</summary>
    public StreamInfoMessage Complete(StreamGeometry geometry, long frameIntervalUs, long exposureUs)
    {
        _generation++;
        _geometry = geometry;
        _frameIntervalUs = frameIntervalUs;
        _reconfiguring = false;
        return Describe(StreamStates.Streaming, exposureUs);
    }

    /// <summary>The restart changed nothing (the backend refused the change): <c>streaming</c> again, same generation and geometry.</summary>
    public StreamInfoMessage Resume(long frameIntervalUs, long exposureUs)
    {
        _frameIntervalUs = frameIntervalUs;
        _reconfiguring = false;
        return Describe(StreamStates.Streaming, exposureUs);
    }

    /// <summary>The current STREAM_INFO again (same generation), for a request that changes nothing (SET_STREAM_CONFIG to the format already in use).</summary>
    public StreamInfoMessage Repeat(long exposureUs) =>
        Describe(_reconfiguring ? StreamStates.Reconfiguring : StreamStates.Streaming, exposureUs);

    /// <summary>
    /// A new STREAM_INFO (same generation) when the frame interval changed, so the client can adapt its stall timeout
    /// (protocol 11.5); null when it did not.
    /// </summary>
    public StreamInfoMessage? IntervalChanged(long frameIntervalUs, long exposureUs)
    {
        if (_reconfiguring || frameIntervalUs == _frameIntervalUs)
        {
            return null;
        }

        _frameIntervalUs = frameIntervalUs;
        return Describe(StreamStates.Streaming, exposureUs);
    }

    /// <summary>True if a frame of <paramref name="geometry"/> may be sent now.</summary>
    public bool Admits(StreamGeometry geometry) =>
        !_reconfiguring
        && geometry.Width == _geometry.Width
        && geometry.Height == _geometry.Height
        && geometry.Binning == _geometry.Binning;

    private StreamInfoMessage Describe(string state, long exposureUs) => new()
    {
        Generation = _generation,
        State = state,
        Width = _geometry.Width,
        Height = _geometry.Height,
        Binning = _geometry.Binning,
        FrameFormat = FrameFormats.Jpeg,
        FrameIntervalUs = _frameIntervalUs,
        ExposureUs = exposureUs,
    };
}
