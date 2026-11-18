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
/// The engine spawns the server with stdout/stderr on pipes it never reads (<c>subprocess_mgr.py</c>), and a Windows
/// pipe holds only a few KB. A synchronous write would therefore freeze whichever thread logs once the pipe is full
/// (the session reader, the capture thread). Lines go into a small bounded queue instead; a pooled worker drains it
/// into the writer, and when the queue is full the newest lines are dropped and counted. Only the worker can ever
/// block on the pipe, and it is a pool thread that dies with the process. <see cref="Flush"/> gives the queue a
/// bounded chance to reach the writer before the process exits.
/// </remarks>
internal sealed class Logger
{
    /// <summary>Lines kept while the writer is slow or blocked.</summary>
    public const int DefaultCapacity = 256;

    private readonly TextWriter _writer;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<string> _queue = new();
    private bool _draining;
    private long _dropped;
    private long _droppedReported;

    public Logger(TextWriter writer, LogLevel level, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _writer = writer;
        _capacity = capacity;
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
    /// Returns false when that did not happen in time (the writer is blocked, e.g. on a full pipe).
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (_draining || _queue.Count > 0)
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
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => ((Logger)state!).Drain(), this);
    }

    private void Drain()
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
                    _draining = false;
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
