// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer;

internal enum LogLevel
{
    Info = 0,
    Debug = 1,
}

/// <summary>
/// Thread-safe stderr logger that never blocks its callers.
/// </summary>
/// <remarks>
/// The engine spawns the server with stdout/stderr on pipes it does not read while the server runs
/// (<c>subprocess_mgr.py</c>), and a Windows pipe holds only a few KB. A synchronous write would therefore freeze
/// whichever thread logs once the pipe is full (the session reader, the capture thread). Lines go into a small
/// bounded queue instead; a pooled worker drains it into the writer, and when the queue is full the newest lines are
/// dropped and counted. Only the writer can ever block on the pipe, never a caller.
/// <para>
/// <see cref="Flush"/> is the shutdown path. It must not lose a queued line just because the pool did not run the
/// worker in time (a busy machine, a starved pool), and it must not hang on a stuck pipe either. So when nobody is
/// writing, <see cref="Flush"/> takes over the queue on a dedicated thread (no pool involved) and waits for it at most
/// its timeout. At any moment exactly one loop writes (<c>_writing</c>), so lines are never written twice, never out
/// of order and never concurrently; a write that is already in flight (possibly stuck on the pipe) is never
/// overtaken, <see cref="Flush"/> just reports that it timed out.
/// </para>
/// </remarks>
internal sealed class Logger
{
    /// <summary>Lines kept while the writer is slow or blocked.</summary>
    public const int DefaultCapacity = 256;

    private readonly TextWriter _writer;
    private readonly int _capacity;
    private readonly Action<Action> _schedule;
    private readonly object _gate = new();
    private readonly Queue<string> _queue = new();
    private bool _writing;       // a drain loop owns the writer right now (the pooled worker or a flush takeover)
    private bool _workerQueued;  // a pooled worker was scheduled and has not started yet
    private long _dropped;
    private long _droppedReported;

    /// <param name="schedule">Runs the background drain; tests replace it. Default: the thread pool.</param>
    public Logger(TextWriter writer, LogLevel level, int capacity = DefaultCapacity, Action<Action>? schedule = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _writer = writer;
        _capacity = capacity;
        _schedule = schedule ?? (static work => ThreadPool.UnsafeQueueUserWorkItem(static w => ((Action)w!)(), work));
        Level = level;
    }

    public LogLevel Level { get; }

    public bool DebugEnabled => Level >= LogLevel.Debug;

    /// <summary>Lines discarded so far because the queue was full (the writer was blocked or too slow).</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public void Info(string message) => Enqueue("INFO", message);

    public void Warn(string message) => Enqueue("WARN", message);

    public void Error(string message) => Enqueue("ERROR", message);

    public void Debug(string message)
    {
        if (DebugEnabled)
        {
            Enqueue("DEBUG", message);
        }
    }

    /// <summary>
    /// Waits until everything queued so far has been handed to the writer, at most <paramref name="timeout"/>.
    /// Lines nobody is writing yet (the pooled worker has not been scheduled) are written by a dedicated thread
    /// straight away. Returns false when that did not finish in time (the writer is blocked, e.g. on a full pipe).
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        bool takeOver;
        lock (_gate)
        {
            takeOver = !_writing && _queue.Count > 0;
            if (takeOver)
            {
                _writing = true;
            }
        }

        if (takeOver)
        {
            try
            {
                new Thread(WriteQueued) { IsBackground = true, Name = "log-flush" }.Start();
            }
            catch (Exception)
            {
                // No thread to write with (out of memory, thread limit): hand the writer back so a later Flush or the
                // pool worker can still drain, and tell the caller its lines are not out.
                lock (_gate)
                {
                    _writing = false;
                    Monitor.PulseAll(_gate);
                }

                return false;
            }
        }

        lock (_gate)
        {
            while (_writing || _queue.Count > 0)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Min(left, int.MaxValue));
            }

            return true;
        }
    }

    private void Enqueue(string level, string message)
    {
        string line = $"[{level}] {message}";
        lock (_gate)
        {
            if (_queue.Count >= _capacity)
            {
                _dropped++;
                return;
            }

            _queue.Enqueue(line);
            if (_writing || _workerQueued)
            {
                return; // the running loop, or the worker about to start, will pick the line up
            }

            _workerQueued = true;
        }

        _schedule(DrainOnWorker);
    }

    private void DrainOnWorker()
    {
        lock (_gate)
        {
            _workerQueued = false;
            if (_writing)
            {
                return; // a flush takeover owns the writer and drains whatever is queued
            }

            _writing = true;
        }

        WriteQueued();
    }

    /// <summary>The single drain loop. The caller has set <c>_writing</c>; only one loop runs at a time.</summary>
    private void WriteQueued()
    {
        while (true)
        {
            string? line;
            lock (_gate)
            {
                if (_dropped > _droppedReported)
                {
                    long n = _dropped - _droppedReported;
                    _droppedReported = _dropped;
                    line = $"[WARN] {n} log line(s) dropped (log output not being read)";
                }
                else if (!_queue.TryDequeue(out line))
                {
                    _writing = false;
                    Monitor.PulseAll(_gate);
                    return;
                }
            }

            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // stderr closed (parent gone); nothing sensible left to do.
            }
        }
    }
}
