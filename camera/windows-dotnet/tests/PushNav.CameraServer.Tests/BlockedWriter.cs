// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text;

namespace PushNav.CameraServer.Tests;

/// <summary>A TextWriter whose WriteLine blocks until released, like a full pipe.</summary>
internal sealed class BlockedWriter : TextWriter
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    public override Encoding Encoding => Encoding.UTF8;

    public ManualResetEventSlim Entered { get; } = new(false);

    public void Release() => _release.Set();

    public string[] Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public override void WriteLine(string? value)
    {
        Entered.Set();
        _release.Wait();
        lock (_gate)
        {
            _lines.Add(value ?? string.Empty);
        }
    }
}
