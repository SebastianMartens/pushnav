// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Tests.Server;

/// <summary>Task 2.3: latest-frame slot + writer, no queue. A slow client never builds a backlog.</summary>
public class SlowReaderTests
{
    [Fact]
    public void SlowClientGetsTheNewestFrameAndNotABacklog()
    {
        // 200 KB frames as fast as the timer allows: far more than a client that does not read can take; the socket buffers fill at once.
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(1), frameSize: 200_000));
        using var client = h.Connect();
        client.Handshake();

        Thread.Sleep(1500); // the client reads nothing while the camera keeps producing
        long producedWhileSleeping = h.Backend.Counter;
        long droppedWhileSleeping = h.Slot.Dropped;
        // (Thread.Sleep(1) ticks at about 15 ms on Windows, so "1 ms" means roughly 60 frames per second.)
        Assert.True(producedWhileSleeping > 30, $"stub only produced {producedWhileSleeping} frames");
        Assert.True(droppedWhileSleeping >= producedWhileSleeping - 8, $"expected all but a handful of frames to be dropped, dropped={droppedWhileSleeping} of {producedWhileSleeping}");

        // Now drain: frames that were already in the socket buffers come first, then the newest one.
        long previous = 0;
        int received = 0;
        long newest = 0;
        for (int i = 0; i < 40 && newest < producedWhileSleeping; i++)
        {
            var frame = client.ReadUntil(MessageType.Frame);
            long counter = StubBackend.CounterOf(frame.Payload);
            Assert.True(counter > previous, "frames must arrive in order, never a duplicate or an older frame");
            previous = counter;
            newest = counter;
            received++;
        }

        Assert.True(newest >= producedWhileSleeping, "the newest frame never arrived (backlog?)");
        Assert.True(received <= 12, $"{received} frames were delivered after a {producedWhileSleeping}-frame burst; that is a backlog");
        Assert.True(received < producedWhileSleeping / 2, "most of the burst must have been skipped");
    }

    [Fact]
    public void ProducerKeepsRunningWhileTheClientDoesNotRead()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(1), frameSize: 200_000));
        using var client = h.Connect();
        client.Handshake();

        long a = h.Backend.Counter;
        Thread.Sleep(500);
        long b = h.Backend.Counter;

        Assert.True(b - a > 15, $"capture stalled behind the slow client: {b - a} frames in 500 ms"); // a blocked producer would make about 3
    }

    [Fact]
    public void ControlRequestsAreStillAnsweredAfterTheClientCatchesUp()
    {
        using var h = ServerHarness.Start(new StubBackend(TimeSpan.FromMilliseconds(1), frameSize: 100_000));
        using var client = h.Connect();
        client.Handshake();
        Thread.Sleep(300);

        client.SetControl("gain", 33);
        client.ReadUntil(MessageType.ControlInfo);

        Assert.Equal(33, client.LastControlInfo!.Controls.Single(c => c.Id == "gain").Cur);
    }
}
