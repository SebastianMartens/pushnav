// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests;

/// <summary>
/// A <see cref="FakeBackend"/> (synthetic star field, 1936x1216, binning 1 or 2) with hooks to script what a real camera might do:
/// answer or throw in <see cref="ICameraBackend.SetControl"/>, say which changes restart the stream, and hold back the controls.
/// </summary>
internal sealed class ScriptedBackend : ICameraBackend
{
    private readonly FakeBackend _inner = new(null, 90, new Logger(TextWriter.Null, LogLevel.Debug));

    /// <summary>Runs first in <c>SetControl</c>; may throw; a non-null result replaces the fake's own handling.</summary>
    public Func<string, double, ControlResult?>? OnSetControl { get; set; }

    /// <summary>Replaces the default answer of <see cref="ICameraBackend.RestartsStream"/> when it returns non-null.</summary>
    public Func<string, double, bool?>? RestartsStreamOverride { get; set; }

    /// <summary>When set, reading <see cref="Controls"/> waits until it is signalled.</summary>
    public ManualResetEventSlim? ControlsGate { get; set; }

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
            ControlsGate?.Wait(TimeSpan.FromSeconds(10));
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

    public ControlResult SetControl(string id, double value) => OnSetControl?.Invoke(id, value) ?? _inner.SetControl(id, value);

    public bool RestartsStream(string id, double value) =>
        RestartsStreamOverride?.Invoke(id, value) ?? ((ICameraBackend)_inner).RestartsStream(id, value);

    public void Start(IFrameSink sink) => _inner.Start(sink);

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}
