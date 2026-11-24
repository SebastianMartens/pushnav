// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Server;

/// <summary>Where a v2 stream is in its life (protocol 11.5).</summary>
internal enum StreamState
{
    /// <summary>No STREAM_INFO sent yet (before the handshake ends).</summary>
    NotStarted,

    Streaming,

    /// <summary>The stream is being restarted; no frame may go out.</summary>
    Reconfiguring,
}

/// <summary>
/// The STREAM_INFO bookkeeping of one v2 session (protocol 11.5): which generation is on the wire, what geometry its frames have,
/// and at which timing. Pure state, no I/O: the session sends what these methods return, and <see cref="V2Dialect"/> runs every
/// transition inside the channel's atomic send, so <see cref="Admits"/> (asked by the frame writer under the same lock) always
/// agrees with the STREAM_INFO already sent. A transition from a state it does not belong to is a programming error and throws.
/// </summary>
/// <remarks>
/// Frames are only let through while they match the announced geometry. That is what keeps a frame of the new size from
/// overtaking STREAM_INFO <c>streaming</c> after a binning change, and a frame of the old size from following it.
/// </remarks>
internal sealed class StreamAnnouncer
{
    private StreamState _state = StreamState.NotStarted;
    private int _generation;
    private StreamGeometry _geometry;
    private StreamTiming _timing;

    public StreamState State => _state;

    /// <summary>STREAM_INFO of generation 1, sent once after the first CONTROL_INFO.</summary>
    public StreamInfoMessage Start(StreamGeometry geometry, StreamTiming timing)
    {
        Require(StreamState.NotStarted, nameof(Start));
        _state = StreamState.Streaming;
        _generation = 1;
        _geometry = geometry;
        _timing = timing;
        return Describe();
    }

    /// <summary>
    /// The stream is about to restart: <c>reconfiguring</c>, same generation, still the old size. No frame goes out until
    /// <see cref="Complete"/> or <see cref="Resume"/>.
    /// </summary>
    public StreamInfoMessage BeginReconfiguration()
    {
        Require(StreamState.Streaming, nameof(BeginReconfiguration));
        _state = StreamState.Reconfiguring;
        return Describe();
    }

    /// <summary>The restart is done: <c>streaming</c> with the new geometry and generation + 1, before the first frame of the new size.</summary>
    public StreamInfoMessage Complete(StreamGeometry geometry, StreamTiming timing)
    {
        Require(StreamState.Reconfiguring, nameof(Complete));
        _state = StreamState.Streaming;
        _generation++;
        _geometry = geometry;
        _timing = timing;
        return Describe();
    }

    /// <summary>The restart changed nothing (the backend refused the change): <c>streaming</c> again, same generation and geometry.</summary>
    public StreamInfoMessage Resume(StreamTiming timing)
    {
        Require(StreamState.Reconfiguring, nameof(Resume));
        _state = StreamState.Streaming;
        _timing = timing;
        return Describe();
    }

    /// <summary>The current STREAM_INFO again (same generation), for a request that changes nothing (SET_STREAM_CONFIG to the format already in use).</summary>
    public StreamInfoMessage Repeat()
    {
        if (_state == StreamState.NotStarted)
        {
            throw new InvalidOperationException("Repeat before Start");
        }

        return Describe();
    }

    /// <summary>
    /// Takes in the current timing. Returns a STREAM_INFO (same generation) when the frame interval differs from the one announced, so
    /// the client can adapt its stall timeout (protocol 11.5); null when it does not, or while the stream restarts.
    /// </summary>
    public StreamInfoMessage? TimingChanged(StreamTiming timing)
    {
        if (_state == StreamState.NotStarted)
        {
            throw new InvalidOperationException("TimingChanged before Start");
        }

        if (_state == StreamState.Reconfiguring)
        {
            return null;
        }

        bool intervalChanged = timing.FrameIntervalUs != _timing.FrameIntervalUs;
        _timing = timing;
        return intervalChanged ? Describe() : null;
    }

    /// <summary>True if a frame of <paramref name="geometry"/> may be sent now.</summary>
    public bool Admits(StreamGeometry geometry) => _state == StreamState.Streaming && geometry.HasSameImageAs(_geometry);

    private void Require(StreamState expected, string transition)
    {
        if (_state != expected)
        {
            throw new InvalidOperationException($"{transition} while {_state} (expected {expected})");
        }
    }

    private StreamInfoMessage Describe() => new()
    {
        Generation = _generation,
        State = _state == StreamState.Reconfiguring ? StreamStates.Reconfiguring : StreamStates.Streaming,
        Width = _geometry.Width,
        Height = _geometry.Height,
        Binning = _geometry.Binning,
        FrameFormat = FrameFormats.Jpeg,
        FrameIntervalUs = _timing.FrameIntervalUs,
        ExposureUs = _timing.ExposureUs,
    };
}
