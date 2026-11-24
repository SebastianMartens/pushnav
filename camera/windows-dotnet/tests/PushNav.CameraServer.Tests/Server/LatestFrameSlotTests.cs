// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Protocol;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Tests.Server;

public class LatestFrameSlotTests
{
    private static byte[] Frame(int number) => [0xFF, 0xD8, (byte)number, (byte)(number >> 8), 0xFF, 0xD9];

    [Fact]
    public void OnlyTheNewestFrameIsKept()
    {
        var slot = new LatestFrameSlot();

        for (int i = 1; i <= 100; i++)
        {
            slot.Publish(Frame(i), default);
        }

        var taken = slot.TryTake();
        Assert.NotNull(taken);
        Assert.Equal(100, taken.Sequence);
        Assert.Equal(Frame(100), taken.Payload.ToArray());
        Assert.Null(slot.TryTake()); // nothing queued behind it
        Assert.Equal(100, slot.Published);
        Assert.Equal(99, slot.Dropped);
    }

    [Fact]
    public void BufferCarriesAHeaderGapSoTheWriterCanSendItInOneWrite()
    {
        var slot = new LatestFrameSlot();
        slot.Publish(Frame(7), default);
        var buffer = slot.TryTake()!;

        Envelope.WriteHeader(buffer.Data, MessageType.Frame, buffer.PayloadLength);

        var wire = buffer.Data.AsSpan(0, buffer.WireLength).ToArray();
        Assert.Equal(Envelope.HeaderSize + 6, wire.Length);
        using var ms = new MemoryStream(wire);
        var message = Envelope.Read(ms);
        Assert.Equal(MessageType.Frame, message.Type);
        Assert.Equal(Frame(7), message.Payload);
    }

    [Fact]
    public void TakeBlocksUntilAFrameIsPublished()
    {
        var slot = new LatestFrameSlot();
        LatestFrameSlot.Buffer? got = null;
        var consumer = new Thread(() => got = slot.Take());
        consumer.Start();

        Assert.False(consumer.Join(100), "Take returned without a frame");
        slot.Publish(Frame(1), default);

        Assert.True(consumer.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, got!.Sequence);
    }

    [Fact]
    public void CompleteWakesAWaitingConsumerWithNull()
    {
        var slot = new LatestFrameSlot();
        LatestFrameSlot.Buffer? got = new();
        var consumer = new Thread(() => got = slot.Take());
        consumer.Start();
        Thread.Sleep(50);

        slot.Complete();

        Assert.True(consumer.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(got);
    }

    [Fact]
    public void CancellationWakesAWaitingConsumerWithNull()
    {
        var slot = new LatestFrameSlot();
        using var cts = new CancellationTokenSource();
        LatestFrameSlot.Buffer? got = new();
        var consumer = new Thread(() => got = slot.Take(cts.Token));
        consumer.Start();
        Thread.Sleep(50);

        cts.Cancel();

        Assert.True(consumer.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(got);
    }

    [Fact]
    public void PublishAfterCompleteIsIgnored()
    {
        var slot = new LatestFrameSlot();
        slot.Complete();

        slot.Publish(Frame(1), default);

        Assert.Null(slot.TryTake());
    }

    [Fact]
    public void ClearDropsAStaleFrame()
    {
        var slot = new LatestFrameSlot();
        slot.Publish(Frame(1), default);

        slot.Clear();

        Assert.Null(slot.TryTake());
    }

    [Fact]
    public void AFrameTakenWhileTheNextIsPublishedStaysIntact()
    {
        var slot = new LatestFrameSlot();
        slot.Publish(Frame(1), default);
        var inFlight = slot.TryTake()!;

        slot.Publish(Frame(2), default);
        slot.Publish(Frame(3), default); // overwrites 2, must not touch the buffer that is "being sent"

        Assert.Equal(Frame(1), inFlight.Payload.ToArray());
        slot.Return(inFlight);
        Assert.Equal(3, slot.TryTake()!.Sequence);
    }

    [Fact]
    public void BuffersAreReusedInsteadOfAllocatedPerFrame()
    {
        var slot = new LatestFrameSlot();
        var seen = new HashSet<LatestFrameSlot.Buffer>(ReferenceEqualityComparer.Instance);
        slot.Publish(Frame(0), default);
        var held = slot.TryTake()!; // a writer stuck on a slow socket: never returned
        seen.Add(held);

        for (int i = 1; i <= 200; i++)
        {
            slot.Publish(Frame(i), default);
            slot.Publish(Frame(i), default); // two publishes per take: one frame is overwritten each round
            var buffer = slot.TryTake()!;
            seen.Add(buffer);
            slot.Return(buffer);
        }

        Assert.True(seen.Count <= 4, $"{seen.Count} distinct buffers were allocated (expected at most 4: in flight, pending, two spare)");
    }

    [Fact]
    public void GrowsForLargerFramesAndKeepsContent()
    {
        var slot = new LatestFrameSlot();
        slot.Publish(new byte[10], default);
        slot.Return(slot.TryTake()!);
        var big = new byte[500_000];
        new Random(5).NextBytes(big);

        slot.Publish(big, default);

        Assert.Equal(big, slot.TryTake()!.Payload.ToArray());
    }

    [Fact]
    public void SinkPublishesJpegFramesAndIgnoresRawOnes()
    {
        var slot = new LatestFrameSlot();
        var sink = new LatestFrameSink(slot);
        var g = new StreamGeometry(4, 4);

        sink.OnFrame(new byte[100], FrameKind.Gray8, g); // FRAME_RAW belongs to protocol v2
        Assert.Null(slot.TryTake());

        sink.OnFrame(Frame(9), FrameKind.Jpeg, g);
        Assert.Equal(Frame(9), slot.TryTake()!.Payload.ToArray());
    }

    [Fact]
    public void ProducerIsNeverBlockedByASlowConsumer()
    {
        var slot = new LatestFrameSlot();
        var done = new ManualResetEventSlim();
        long last = 0;
        var consumer = new Thread(() =>
        {
            while (slot.Take() is { } buffer)
            {
                last = buffer.Sequence;
                Thread.Sleep(20); // slow reader
                slot.Return(buffer);
                if (last >= 5000)
                {
                    break;
                }
            }

            done.Set();
        });
        consumer.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 1; i <= 5000; i++)
        {
            slot.Publish(Frame(i), default);
        }

        sw.Stop();
        // 5000 publishes against a consumer that needs 20 ms per frame: the producer must not have waited for it.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"producer took {sw.ElapsedMilliseconds} ms");
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "consumer never saw the newest frame");
        Assert.Equal(5000, last);
        Assert.True(slot.Dropped > 4000, $"expected most frames dropped, got {slot.Dropped}");
    }
}
