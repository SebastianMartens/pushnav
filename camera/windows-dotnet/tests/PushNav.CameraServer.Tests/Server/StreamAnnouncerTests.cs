// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>The STREAM_INFO rules of protocol 11.5, without a socket.</summary>
public class StreamAnnouncerTests
{
    private static readonly StreamGeometry Full = new(1936, 1216, 1);
    private static readonly StreamGeometry Binned = new(968, 608, 2);

    private static StreamAnnouncer Started(out StreamInfoMessage first)
    {
        var announcer = new StreamAnnouncer();
        first = announcer.Start(Full, 100_000, 50_000);
        return announcer;
    }

    [Fact]
    public void The_session_starts_with_generation_1_streaming()
    {
        Started(out var first);

        Assert.Equal((1, "streaming", 1936, 1216, 1), (first.Generation, first.State, first.Width, first.Height, first.Binning));
        Assert.Equal((100_000, 50_000, "JPEG"), (first.FrameIntervalUs, first.ExposureUs, first.FrameFormat));
    }

    [Fact]
    public void A_reconfiguration_keeps_the_generation_and_the_old_size_until_it_completes_with_the_next_generation()
    {
        var announcer = Started(out _);

        var reconfiguring = announcer.BeginReconfiguration(50_000);
        var streaming = announcer.Complete(Binned, 100_000, 50_000);

        Assert.Equal((1, "reconfiguring", 1936, 1216), (reconfiguring.Generation, reconfiguring.State, reconfiguring.Width, reconfiguring.Height));
        Assert.Equal((2, "streaming", 968, 608, 2), (streaming.Generation, streaming.State, streaming.Width, streaming.Height, streaming.Binning));
    }

    [Fact]
    public void A_restart_that_changed_nothing_resumes_the_same_generation()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration(50_000);

        var resumed = announcer.Resume(100_000, 50_000);

        Assert.Equal((1, "streaming", 1936), (resumed.Generation, resumed.State, resumed.Width));
        Assert.True(announcer.Admits(Full));
    }

    [Fact]
    public void Only_frames_of_the_announced_geometry_are_admitted()
    {
        var announcer = Started(out _);

        Assert.True(announcer.Admits(Full));
        Assert.False(announcer.Admits(Binned));
        Assert.False(announcer.Admits(default));
    }

    [Fact]
    public void No_frame_is_admitted_while_reconfiguring_and_the_new_size_only_after_streaming_is_announced()
    {
        var announcer = Started(out _);

        announcer.BeginReconfiguration(50_000);
        Assert.False(announcer.Admits(Full));
        Assert.False(announcer.Admits(Binned)); // a new-size frame must not overtake STREAM_INFO

        announcer.Complete(Binned, 100_000, 50_000);
        Assert.True(announcer.Admits(Binned));
        Assert.False(announcer.Admits(Full)); // and an old-size frame must not follow it
    }

    [Fact]
    public void A_changed_frame_interval_is_announced_once_with_the_same_generation()
    {
        var announcer = Started(out _);

        var changed = announcer.IntervalChanged(200_000, 200_000);
        var again = announcer.IntervalChanged(200_000, 200_000);

        Assert.NotNull(changed);
        Assert.Equal((1, "streaming", 200_000, 200_000), (changed.Generation, changed.State, changed.FrameIntervalUs, changed.ExposureUs));
        Assert.Null(again);
    }

    [Fact]
    public void An_unchanged_frame_interval_is_not_announced()
    {
        var announcer = Started(out _);

        Assert.Null(announcer.IntervalChanged(100_000, 80_000)); // the exposure alone does not move the interval
    }

    [Fact]
    public void No_interval_change_is_announced_while_reconfiguring()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration(50_000);

        Assert.Null(announcer.IntervalChanged(250_000, 50_000));
    }

    [Fact]
    public void Repeating_the_info_gives_the_current_state_and_generation()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration(50_000);
        announcer.Complete(Binned, 100_000, 50_000);

        var repeated = announcer.Repeat(60_000);

        Assert.Equal((2, "streaming", 968, 60_000), (repeated.Generation, repeated.State, repeated.Width, repeated.ExposureUs));
    }
}
