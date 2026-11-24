// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests;

/// <summary>
/// A <see cref="FakeBackend"/> (synthetic star field, 1936x1216, binning 1 or 2) with hooks to script what a real camera might do:
/// answer or throw in <see cref="ICameraBackend.SetControl"/>, say which changes restart the stream, and run code when the controls are first read.
/// Every <see cref="ControlChange"/> it receives is kept in <see cref="Changes"/>.
/// </summary>
internal sealed class ScriptedBackend : ICameraBackend
{
    private readonly FakeBackend _inner = new(null, 90, new Logger(TextWriter.Null, LogLevel.Debug));
    private readonly List<ControlChange> _changes = [];
    private Action? _beforeFirstControlsRead;

    /// <summary>Runs first in <c>SetControl</c>; may throw; a non-null result replaces the fake's own handling.</summary>
    public Func<string, double, ControlResult?>? OnSetControl { get; set; }

    /// <summary>Replaces the default answer of <see cref="ICameraBackend.WouldRestartStream"/> when it returns non-null.</summary>
    public Func<ControlChange, bool?>? WouldRestartStreamOverride { get; set; }

    /// <summary>Every change <c>SetControl</c> received, in order.</summary>
    public IReadOnlyList<ControlChange> Changes
    {
        get
        {
            lock (_changes)
            {
                return _changes.ToArray();
            }
        }
    }

    /// <summary>Runs once, on the thread that reads <see cref="Controls"/> first, before that read returns.</summary>
    public Action? BeforeFirstControlsRead
    {
        get => _beforeFirstControlsRead;
        set => _beforeFirstControlsRead = value;
    }

    public string Id => _inner.Id;

    public string HelloName => _inner.HelloName;

    public DeviceInfo Device => _inner.Device;

    public StreamGeometry Geometry => _inner.Geometry;

    public int DefaultFps => _inner.DefaultFps;

    public SensorInfo? Sensor => _inner.Sensor;

    public IReadOnlyList<ControlDescriptor> Controls
    {
        get
        {
            Interlocked.Exchange(ref _beforeFirstControlsRead, null)?.Invoke();
            return _inner.Controls;
        }
    }

    public event Action<string>? Fatal
    {
        add => _inner.Fatal += value;
        remove => _inner.Fatal -= value;
    }

    public IReadOnlyList<DeviceInfo> Probe() => _inner.Probe();

    public void Open(DeviceInfo device) => _inner.Open(device);

    public ControlResult SetControl(ControlChange change)
    {
        lock (_changes)
        {
            _changes.Add(change);
        }

        return OnSetControl?.Invoke(change.Id, change.Value) ?? _inner.SetControl(change);
    }

    public bool WouldRestartStream(ControlChange change) =>
        WouldRestartStreamOverride?.Invoke(change) ?? ((ICameraBackend)_inner).WouldRestartStream(change);

    public void Start(IFrameSink sink) => _inner.Start(sink);

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}
