// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text;

namespace PushNav.CameraServer.Tests;

/// <summary>
/// The engine never reads the server's stderr pipe (about 4 KB on Windows), so a log write must never be able to
/// block a caller: a blocked sink may only cost log lines, not the session.
/// </summary>
public class LoggerTests
{
    [Fact]
    public void WritesLinesWithALevelPrefixInOrder()
    {
        var sw = new StringWriter();
        var log = new Logger(sw, LogLevel.Debug);

        log.Info("one");
        log.Warn("two");
        log.Error("three");
        log.Debug("four");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["[INFO] one", "[WARN] two", "[ERROR] three", "[DEBUG] four"], sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void DebugLinesAreSkippedAtInfoLevel()
    {
        var sw = new StringWriter();
        var log = new Logger(sw, LogLevel.Info);

        log.Debug("noise");
        log.Info("kept");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal("[INFO] kept" + Environment.NewLine, sw.ToString());
    }

    [Fact]
    public void ABlockedSinkNeverBlocksTheCaller()
    {
        var sink = new BlockedWriter();
        var log = new Logger(sink, LogLevel.Debug, capacity: 16);

        var logging = new Thread(() =>
        {
            for (int i = 0; i < 100_000; i++)
            {
                log.Info($"line {i}");
            }
        });
        logging.Start();

        Assert.True(logging.Join(TimeSpan.FromSeconds(10)), "logging blocked behind a stuck sink");
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)));
        sink.Release();
    }

    [Fact]
    public void LinesAreDroppedAndCountedWhenTheQueueIsFull()
    {
        var sink = new BlockedWriter();
        var log = new Logger(sink, LogLevel.Debug, capacity: 4);
        log.Info("first");
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5))); // the worker holds "first" and is stuck

        for (int i = 0; i < 20; i++)
        {
            log.Info($"queued {i}");
        }

        Assert.Equal(16, log.DroppedCount); // 4 fit, 16 do not
        sink.Release();
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        var lines = sink.Lines;
        Assert.Equal("[INFO] first", lines[0]);
        Assert.Equal(["[INFO] queued 0", "[INFO] queued 1", "[INFO] queued 2", "[INFO] queued 3"], lines.Where(l => l.Contains("queued")).ToArray());
        Assert.Contains(lines, l => l.Contains("16 log line(s) dropped"));
    }

    [Fact]
    public void FlushGivesUpAfterItsTimeoutWhileTheSinkIsBlocked()
    {
        var sink = new BlockedWriter();
        var log = new Logger(sink, LogLevel.Info);
        log.Error("fatal");
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool flushed = log.Flush(TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.False(flushed);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 3000); // bounded, never forever
        sink.Release();
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["[ERROR] fatal"], sink.Lines);
    }

    [Fact]
    public void FlushWritesTheQueuedLinesItselfWhenTheBackgroundWriterNeverRuns()
    {
        var sw = new StringWriter();
        var scheduled = new List<Action>();
        var log = new Logger(sw, LogLevel.Info, schedule: scheduled.Add); // the "pool" never runs the worker

        log.Info("one");
        log.Error("two");
        Assert.Equal(string.Empty, sw.ToString());

        Assert.True(log.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(["[INFO] one", "[ERROR] two"], sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));

        // The worker finally gets its turn: nothing is written a second time, and later lines still flow.
        Assert.Single(scheduled);
        scheduled[0]();
        log.Info("three");
        Assert.True(log.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(["[INFO] one", "[ERROR] two", "[INFO] three"], sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void FlushNeverOvertakesALineTheWorkerIsWriting()
    {
        var sink = new BlockedWriter();
        var log = new Logger(sink, LogLevel.Info);
        log.Info("first");
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5))); // the worker is stuck inside the write of "first"
        log.Info("second");

        Assert.False(log.Flush(TimeSpan.FromMilliseconds(100))); // gives up; no second writer, no reordering
        Assert.Empty(sink.Lines);

        sink.Release();
        Assert.True(log.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(["[INFO] first", "[INFO] second"], sink.Lines);
    }

    [Fact]
    public void FlushWithAStuckWriterAndNothingInFlightStillReturnsAfterItsTimeout()
    {
        var sink = new BlockedWriter();
        var scheduled = new List<Action>();
        var log = new Logger(sink, LogLevel.Info, schedule: scheduled.Add);
        log.Info("fatal");

        Assert.False(log.Flush(TimeSpan.FromMilliseconds(100))); // the takeover thread is stuck on the sink, the caller is not

        sink.Release();
        Assert.True(log.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(["[INFO] fatal"], sink.Lines);
    }

    [Fact]
    public void FlushReturnsAtOnceWhenNothingIsPending()
    {
        var log = new Logger(TextWriter.Null, LogLevel.Info);

        Assert.True(log.Flush(TimeSpan.Zero));
    }

    [Fact]
    public void ManyThreadsCanLogConcurrently()
    {
        var sw = new StringWriter();
        var log = new Logger(TextWriter.Synchronized(sw), LogLevel.Info, capacity: 100_000);

        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 500; i++)
            {
                log.Info($"t{t} i{i}");
            }
        });

        Assert.True(log.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, log.DroppedCount);
        Assert.Equal(4000, sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ASinkThatThrowsDoesNotBreakTheLogger()
    {
        var log = new Logger(new ThrowingWriter(), LogLevel.Info);

        log.Info("a");
        log.Info("b");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("pipe closed");
    }
}
