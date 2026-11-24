// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests.Controls;

/// <summary>What the controls say about the pace of the stream.</summary>
public class StreamTimingTests
{
    private static ControlDescriptor Exposure(double us) => new() { Id = "exposure", Label = "E", Kind = ControlKind.Int, Unit = "us", Min = 92, Max = 4_000_000, Cur = us };

    private static ControlDescriptor Fps(double fps) => new() { Id = "fps", Label = "F", Kind = ControlKind.Float, Unit = "fps", Min = 1, Max = 40, Cur = fps };

    [Fact]
    public void The_interval_is_one_over_fps()
    {
        Assert.Equal(new StreamTiming(250_000, 10_000), StreamTiming.From([Exposure(10_000), Fps(4)], fallbackFps: 10));
    }

    [Fact]
    public void An_exposure_longer_than_the_fps_interval_stretches_the_frame()
    {
        Assert.Equal(new StreamTiming(4_000_000, 4_000_000), StreamTiming.From([Exposure(4_000_000), Fps(1)], fallbackFps: 10));
    }

    [Fact]
    public void Without_an_fps_control_the_default_rate_applies()
    {
        Assert.Equal(100_000, StreamTiming.From([Exposure(10_000)], fallbackFps: 10).FrameIntervalUs);
    }

    [Fact]
    public void The_interval_is_rounded_to_whole_microseconds()
    {
        Assert.Equal(97_561, StreamTiming.From([Exposure(1_000), Fps(10.25)], fallbackFps: 10).FrameIntervalUs);
    }

    [Fact]
    public void Without_an_exposure_control_the_exposure_is_zero()
    {
        Assert.Equal(new StreamTiming(100_000, 0), StreamTiming.From([Fps(10)], fallbackFps: 10));
    }
}
