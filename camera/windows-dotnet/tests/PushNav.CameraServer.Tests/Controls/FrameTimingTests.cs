// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Tests.Controls;

/// <summary>What the controls say about the pace of the stream.</summary>
public class FrameTimingTests
{
    private static ControlDescriptor Exposure(double us) => new() { Id = "exposure", Label = "E", Kind = ControlKind.Int, Unit = "us", Min = 92, Max = 4_000_000, Cur = us };

    private static ControlDescriptor Fps(double fps) => new() { Id = "fps", Label = "F", Kind = ControlKind.Float, Unit = "fps", Min = 1, Max = 40, Cur = fps };

    [Fact]
    public void The_interval_is_one_over_fps()
    {
        Assert.Equal(250_000, FrameTiming.FrameIntervalUs([Exposure(10_000), Fps(4)], fallbackFps: 10));
    }

    [Fact]
    public void An_exposure_longer_than_the_fps_interval_stretches_the_frame()
    {
        Assert.Equal(4_000_000, FrameTiming.FrameIntervalUs([Exposure(4_000_000), Fps(1)], fallbackFps: 10));
    }

    [Fact]
    public void Without_an_fps_control_the_default_rate_applies()
    {
        Assert.Equal(100_000, FrameTiming.FrameIntervalUs([Exposure(10_000)], fallbackFps: 10));
    }

    [Fact]
    public void The_interval_is_rounded_to_whole_microseconds()
    {
        Assert.Equal(97_561, FrameTiming.FrameIntervalUs([Exposure(1_000), Fps(10.25)], fallbackFps: 10));
    }

    [Fact]
    public void Exposure_is_read_from_the_exposure_control()
    {
        Assert.Equal(12_345, FrameTiming.ExposureUs([Fps(10), Exposure(12_345)]));
        Assert.Equal(0, FrameTiming.ExposureUs([Fps(10)]));
    }
}
