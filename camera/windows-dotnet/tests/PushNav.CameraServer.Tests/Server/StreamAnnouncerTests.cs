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
    private static readonly StreamTiming Ten = new(100_000, 50_000);

    private static StreamAnnouncer Started(out StreamInfoMessage first)
    {
        var announcer = new StreamAnnouncer();
        first = announcer.Start(Full, Ten);
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

        var reconfiguring = announcer.BeginReconfiguration();
        var streaming = announcer.Complete(Binned, Ten);

        Assert.Equal((1, "reconfiguring", 1936, 1216), (reconfiguring.Generation, reconfiguring.State, reconfiguring.Width, reconfiguring.Height));
        Assert.Equal((2, "streaming", 968, 608, 2), (streaming.Generation, streaming.State, streaming.Width, streaming.Height, streaming.Binning));
    }

    [Fact]
    public void A_reconfiguration_that_keeps_the_geometry_still_gives_the_next_generation()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration();

        var streaming = announcer.Complete(Full, Ten); // e.g. a long-exposure exit that restarts the capture at the same size

        Assert.Equal((2, "streaming", 1936), (streaming.Generation, streaming.State, streaming.Width));
    }

    [Fact]
    public void A_restart_that_changed_nothing_resumes_the_same_generation()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration();

        var resumed = announcer.Resume(Ten);

        Assert.Equal((1, "streaming", 1936), (resumed.Generation, resumed.State, resumed.Width));
        Assert.True(announcer.Admits(Full));
    }

    [Fact]
    public void Only_frames_of_the_announced_image_are_admitted()
    {
        var announcer = Started(out _);

        Assert.True(announcer.Admits(Full));
        Assert.True(announcer.Admits(Full with { Format = PixelFormat.Mjpg })); // the pixel format is not the client's business
        Assert.False(announcer.Admits(Binned));
        Assert.False(announcer.Admits(default));
    }

    [Fact]
    public void No_frame_is_admitted_while_reconfiguring_and_the_new_size_only_after_streaming_is_announced()
    {
        var announcer = Started(out _);

        announcer.BeginReconfiguration();
        Assert.False(announcer.Admits(Full));
        Assert.False(announcer.Admits(Binned)); // a new-size frame must not overtake STREAM_INFO

        announcer.Complete(Binned, Ten);
        Assert.True(announcer.Admits(Binned));
        Assert.False(announcer.Admits(Full)); // and an old-size frame must not follow it
    }

    [Fact]
    public void A_changed_frame_interval_is_announced_once_with_the_same_generation()
    {
        var announcer = Started(out _);

        var changed = announcer.TimingChanged(new StreamTiming(200_000, 200_000));
        var again = announcer.TimingChanged(new StreamTiming(200_000, 200_000));

        Assert.NotNull(changed);
        Assert.Equal((1, "streaming", 200_000, 200_000), (changed.Generation, changed.State, changed.FrameIntervalUs, changed.ExposureUs));
        Assert.Null(again);
    }

    [Fact]
    public void An_unchanged_frame_interval_is_not_announced_but_the_exposure_is_remembered()
    {
        var announcer = Started(out _);

        Assert.Null(announcer.TimingChanged(new StreamTiming(100_000, 80_000))); // the exposure alone does not move the interval
        Assert.Equal(80_000, announcer.Repeat().ExposureUs);
    }

    [Fact]
    public void No_timing_change_is_announced_while_reconfiguring()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration();

        Assert.Null(announcer.TimingChanged(new StreamTiming(250_000, 50_000)));
    }

    [Fact]
    public void Repeating_the_info_gives_the_current_state_and_generation()
    {
        var announcer = Started(out _);
        announcer.BeginReconfiguration();
        announcer.Complete(Binned, new StreamTiming(100_000, 60_000));

        var repeated = announcer.Repeat();

        Assert.Equal((2, "streaming", 968, 60_000), (repeated.Generation, repeated.State, repeated.Width, repeated.ExposureUs));
    }

    [Fact]
    public void Transitions_that_do_not_belong_to_the_state_throw()
    {
        var fresh = new StreamAnnouncer();
        Assert.Throws<InvalidOperationException>(() => fresh.BeginReconfiguration());
        Assert.Throws<InvalidOperationException>(() => fresh.Repeat());
        Assert.Throws<InvalidOperationException>(() => fresh.TimingChanged(Ten));

        var streaming = Started(out _);
        Assert.Throws<InvalidOperationException>(() => streaming.Start(Full, Ten));
        Assert.Throws<InvalidOperationException>(() => streaming.Complete(Binned, Ten));
        Assert.Throws<InvalidOperationException>(() => streaming.Resume(Ten));

        streaming.BeginReconfiguration();
        Assert.Equal(StreamState.Reconfiguring, streaming.State);
        Assert.Throws<InvalidOperationException>(() => streaming.BeginReconfiguration());
    }
}
