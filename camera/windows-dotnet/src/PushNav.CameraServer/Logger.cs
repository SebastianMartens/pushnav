// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer;

internal enum LogLevel
{
    Info = 0,
    Debug = 1,
}

/// <summary>
/// Minimal thread-safe stderr logger. The engine spawns the server with stdout/stderr pipes it does not
/// drain, so the default level must stay quiet: a few lines per session, never one line per frame.
/// </summary>
internal sealed class Logger
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public Logger(TextWriter writer, LogLevel level)
    {
        _writer = writer;
        Level = level;
    }

    public LogLevel Level { get; }

    public bool DebugEnabled => Level >= LogLevel.Debug;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Debug(string message)
    {
        if (DebugEnabled)
        {
            Write("DEBUG", message);
        }
    }

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                _writer.WriteLine($"[{level}] {message}");
                _writer.Flush();
            }
            catch (IOException)
            {
                // stderr closed (parent gone); nothing sensible left to do.
            }
        }
    }
}
