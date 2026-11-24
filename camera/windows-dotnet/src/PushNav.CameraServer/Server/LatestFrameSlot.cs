// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Server;

/// <summary>
/// Single-slot, overwrite-on-publish holder of the newest encoded frame ("no frame queues", spec 4.2).
/// The producer (capture thread) never waits for the consumer (TCP writer): a frame that was not taken
/// before the next one arrives is dropped. Buffers are recycled through a tiny free list (at most two kept), so a
/// steady stream allocates nothing and a stuck consumer can never make the slot grow.
/// </summary>
/// <remarks>
/// Buffers carry an 8-byte prefix reserved for the message header, so the writer can send header and
/// JPEG with one socket write without copying the frame again.
/// </remarks>
internal sealed class LatestFrameSlot
{
    private const int MaxFreeBuffers = 2;

    private readonly object _gate = new();
    private Buffer? _pending;
    private readonly Stack<Buffer> _free = new();
    private bool _completed;
    private long _published;
    private long _dropped;

    /// <summary>A frame buffer; <see cref="Data"/> holds an 8-byte header gap, then <see cref="PayloadLength"/> payload bytes.</summary>
    internal sealed class Buffer
    {
        public byte[] Data { get; set; } = [];

        public int PayloadLength { get; set; }

        /// <summary>Size and binning of the image in the payload (v2 checks it against STREAM_INFO); default when the producer did not say.</summary>
        public StreamGeometry Geometry { get; set; }

        /// <summary>1-based number of the frame in publish order (for tests and diagnostics).</summary>
        public long Sequence { get; set; }

        /// <summary>Payload only (without the header gap).</summary>
        public ReadOnlySpan<byte> Payload => Data.AsSpan(Envelope.HeaderSize, PayloadLength);

        /// <summary>Header gap + payload: what goes on the wire once the header is filled in.</summary>
        public int WireLength => Envelope.HeaderSize + PayloadLength;
    }

    /// <summary>Frames published so far.</summary>
    public long Published
    {
        get { lock (_gate) { return _published; } }
    }

    /// <summary>Frames overwritten before anyone took them.</summary>
    public long Dropped
    {
        get { lock (_gate) { return _dropped; } }
    }

    /// <summary>Stores a copy of <paramref name="payload"/> (an image of <paramref name="geometry"/>) as the newest frame, replacing an untaken one.</summary>
    public void Publish(ReadOnlySpan<byte> payload, StreamGeometry geometry = default)
    {
        if (payload.Length > Envelope.MaxPayloadLength)
        {
            throw new ArgumentException("Frame exceeds the protocol's payload limit.", nameof(payload));
        }

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            var buffer = _free.Count > 0 ? _free.Pop() : new Buffer();
            int needed = Envelope.HeaderSize + payload.Length;
            if (buffer.Data.Length < needed)
            {
                buffer.Data = new byte[Math.Max(needed, buffer.Data.Length * 2)];
            }

            payload.CopyTo(buffer.Data.AsSpan(Envelope.HeaderSize));
            buffer.PayloadLength = payload.Length;
            buffer.Geometry = geometry;
            buffer.Sequence = ++_published;

            if (_pending != null)
            {
                _dropped++;
                Recycle(_pending); // the replaced frame was never sent
            }

            _pending = buffer;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Waits for a frame and takes it (the slot is empty afterwards). Returns null when the slot was completed
    /// (no more frames will come) or <paramref name="cancellation"/> fired. Give the buffer back with <see cref="Return"/>.
    /// </summary>
    public Buffer? Take(CancellationToken cancellation = default)
    {
        using var registration = cancellation.Register(static state =>
        {
            var slot = (LatestFrameSlot)state!;
            lock (slot._gate)
            {
                Monitor.PulseAll(slot._gate);
            }
        }, this);

        lock (_gate)
        {
            while (_pending == null)
            {
                if (_completed || cancellation.IsCancellationRequested)
                {
                    return null;
                }

                Monitor.Wait(_gate);
            }

            var buffer = _pending;
            _pending = null;
            return buffer;
        }
    }

    /// <summary>Takes the pending frame if there is one, without waiting.</summary>
    public Buffer? TryTake()
    {
        lock (_gate)
        {
            var buffer = _pending;
            _pending = null;
            return buffer;
        }
    }

    /// <summary>Hands a taken buffer back for reuse.</summary>
    public void Return(Buffer buffer)
    {
        lock (_gate)
        {
            Recycle(buffer);
        }
    }

    /// <summary>Drops a pending frame (e.g. a stale one from before a client connected).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_pending != null)
            {
                Recycle(_pending);
                _pending = null;
            }
        }
    }

    private void Recycle(Buffer buffer)
    {
        if (_free.Count < MaxFreeBuffers)
        {
            _free.Push(buffer);
        }
    }

    /// <summary>No more frames: wakes a waiting <see cref="Take"/> with null and ignores further publishes.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary><see cref="IFrameSink"/> that feeds the <see cref="LatestFrameSlot"/>; only JPEG frames are carried so far.</summary>
internal sealed class LatestFrameSink : IFrameSink
{
    private readonly LatestFrameSlot _slot;

    public LatestFrameSink(LatestFrameSlot slot)
    {
        _slot = slot;
    }

    public void OnFrame(ReadOnlySpan<byte> data, FrameKind kind, StreamGeometry geometry)
    {
        if (kind == FrameKind.Jpeg)
        {
            _slot.Publish(data, geometry);
        }

        // FrameKind.Gray8 would become FRAME_RAW in protocol v2 (task 7.3); no session asks for it yet.
    }
}
