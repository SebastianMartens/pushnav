// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests.Backends;

public class BackendSelectorTests
{
    /// <summary>Probe() returns nothing for the first <c>emptyProbes</c> calls, then one device.</summary>
    private sealed class ScriptedBackend(string id, int emptyProbes, bool throws = false) : ICameraBackend
    {
        public int ProbeCalls { get; private set; }

        public string Id => id;

        public string HelloName => "windows-dotnet-" + id;

        public DeviceInfo Device => new(id, id + "-cam");

        public StreamGeometry Geometry => default;

        public int DefaultFps => 0;

        public IReadOnlyList<ControlDescriptor> Controls => [];

        public event Action<string>? Fatal { add { } remove { } }

        public IReadOnlyList<DeviceInfo> Probe()
        {
            ProbeCalls++;
            if (throws)
            {
                throw new InvalidOperationException("driver exploded");
            }

            return ProbeCalls > emptyProbes ? [new DeviceInfo(id, id + "-cam")] : [];
        }

        public void Open(DeviceInfo device) { }

        public ControlResult SetControl(string id, double value) => ControlResult.Ok;

        public void Start(IFrameSink sink) { }

        public void Stop() { }

        public void Dispose() { }
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void FirstBackendWithADeviceWins()
    {
        var ids = new ScriptedBackend("ids", emptyProbes: 0);
        var uvc = new ScriptedBackend("uvc", emptyProbes: 0);

        var selected = BackendSelector.Select([ids, uvc], Wait, Step, _ => { });

        Assert.Same(ids, selected!.Backend);
        Assert.Equal(0, uvc.ProbeCalls); // not even asked once a device is found
    }

    [Fact]
    public void FallsThroughToTheNextBackendWhenTheFirstHasNoDevice()
    {
        var ids = new ScriptedBackend("ids", emptyProbes: int.MaxValue);
        var uvc = new ScriptedBackend("uvc", emptyProbes: 0);

        var selected = BackendSelector.Select([ids, uvc], Wait, Step, _ => { });

        Assert.Same(uvc, selected!.Backend);
        Assert.Equal(1, ids.ProbeCalls);
    }

    [Fact]
    public void WaitsAndRetriesForAFreshlyPluggedCamera()
    {
        var ids = new ScriptedBackend("ids", emptyProbes: 5);
        var delays = new List<TimeSpan>();

        var selected = BackendSelector.Select([ids], Wait, Step, delays.Add);

        Assert.Same(ids, selected!.Backend);
        Assert.Equal(6, ids.ProbeCalls);
        Assert.Equal(5, delays.Count);
        Assert.All(delays, d => Assert.Equal(Step, d));
    }

    [Fact]
    public void GivesUpAfterTheWaitHasPassed()
    {
        var ids = new ScriptedBackend("ids", emptyProbes: int.MaxValue);
        var delays = new List<TimeSpan>();

        var selected = BackendSelector.Select([ids], Wait, Step, delays.Add);

        Assert.Null(selected);
        Assert.Equal(17, ids.ProbeCalls);                     // t = 0, 0.5, ... 8.0 s
        Assert.Equal(Wait, TimeSpan.FromTicks(delays.Sum(d => d.Ticks))); // 8 s of waiting in total
    }

    [Fact]
    public void ZeroWaitProbesExactlyOnce()
    {
        var ids = new ScriptedBackend("ids", emptyProbes: int.MaxValue);

        Assert.Null(BackendSelector.Select([ids], TimeSpan.Zero, Step, _ => Assert.Fail("must not wait")));
        Assert.Equal(1, ids.ProbeCalls);
    }

    [Fact]
    public void NoBackendsMeansNoDevice()
    {
        Assert.Null(BackendSelector.Select([], Wait, Step, _ => { }));
    }

    [Fact]
    public void ABackendWhoseProbeThrowsIsTreatedAsHavingNoDevice()
    {
        var broken = new ScriptedBackend("ids", emptyProbes: 0, throws: true);
        var uvc = new ScriptedBackend("uvc", emptyProbes: 0);

        var selected = BackendSelector.Select([broken, uvc], Wait, Step, _ => { });

        Assert.Same(uvc, selected!.Backend);
    }

    [Fact]
    public void CancellationStopsTheWait()
    {
        using var cts = new CancellationTokenSource();
        var ids = new ScriptedBackend("ids", emptyProbes: int.MaxValue);

        var selected = BackendSelector.Select([ids], Wait, Step, _ => cts.Cancel(), cancellation: cts.Token);

        Assert.Null(selected);
        Assert.Equal(1, ids.ProbeCalls);
    }

    [Fact]
    public void CameraKeyUsesVidPidWhenKnownAndTheModelOtherwise()
    {
        Assert.Equal("ids:1409:3060", new DeviceInfo("ids", "UI306xCP-M", Vid: 0x1409, Pid: 0x3060).CameraKey);
        Assert.Equal("uvc:0c45:6366", new DeviceInfo("uvc", "OV9281", Vid: 0x0c45, Pid: 0x6366).CameraKey);
        Assert.Equal("fake:FakeCamera", new DeviceInfo("fake", "FakeCamera").CameraKey);
    }
}
