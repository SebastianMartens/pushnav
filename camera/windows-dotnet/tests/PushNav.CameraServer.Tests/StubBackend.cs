// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Buffers.Binary;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests;

/// <summary>
/// A minimal backend for server tests: exposure/gain controls and tiny (or large) JPEG-looking frames that carry a
/// running counter, so tests can tell which frame they received.
/// </summary>
internal sealed class StubBackend : ICameraBackend
{
    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly int _frameSize;
    private double _exposure = 50_000;
    private double _gain = 20;
    private Thread? _thread;
    private volatile bool _stop;
    private long _counter;

    public StubBackend(TimeSpan? frameInterval = null, int frameSize = 16)
    {
        _interval = frameInterval ?? TimeSpan.FromMilliseconds(5);
        _frameSize = Math.Max(16, frameSize);
    }

    public string Id => "stub";

    public string HelloName => "windows-dotnet-stub";

    public event Action<string>? Fatal;

    public DeviceInfo Device { get; private set; } = new("stub", "StubCam", Vid: 0x1234, Pid: 0xabcd);

    public StreamGeometry Geometry => new(640, 480);

    public int DefaultFps => 30;

    /// <summary>Number of frames produced so far; the counter of the newest frame equals this.</summary>
    public long Counter => Interlocked.Read(ref _counter);

    public IReadOnlyList<ControlDescriptor> Controls
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    new ControlDescriptor { Id = ControlIds.Exposure, Label = "Exposure", Kind = ControlKind.Int, Unit = "us", Min = 100, Max = 4_000_000, Step = 100, Cur = _exposure },
                    new ControlDescriptor { Id = ControlIds.Gain, Label = "Gain", Kind = ControlKind.Int, Unit = "raw", Min = 0, Max = 100, Step = 1, Cur = _gain },
                ];
            }
        }
    }

    public IReadOnlyList<DeviceInfo> Probe() => [Device];

    public void Open(DeviceInfo device) => Device = device;

    public ControlResult SetControl(ControlChange change)
    {
        lock (_gate)
        {
            var (id, value, _) = change;
            var control = Controls.FirstOrDefault(c => c.Id == id);
            if (control == null || !control.TryCoerce(value, out double coerced))
            {
                return ControlResult.Rejected("rejected");
            }

            if (id == ControlIds.Exposure)
            {
                _exposure = coerced;
            }
            else
            {
                _gain = coerced;
            }

            return ControlResult.Ok;
        }
    }

    public void Start(IFrameSink sink)
    {
        _thread = new Thread(() =>
        {
            var frame = new byte[_frameSize];
            frame[0] = 0xFF;
            frame[1] = 0xD8;
            frame[^2] = 0xFF;
            frame[^1] = 0xD9;
            while (!_stop)
            {
                long n = Interlocked.Increment(ref _counter);
                BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(2), n);
                sink.OnFrame(frame, FrameKind.Jpeg, Geometry);
                Thread.Sleep(_interval);
            }
        })
        { IsBackground = true, Name = "stub-capture" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void RaiseFatal(string message) => Fatal?.Invoke(message);

    public void Dispose() => Stop();

    /// <summary>Counter stored in a frame produced by this backend.</summary>
    public static long CounterOf(ReadOnlySpan<byte> frame) => BinaryPrimitives.ReadInt64LittleEndian(frame[2..]);
}
