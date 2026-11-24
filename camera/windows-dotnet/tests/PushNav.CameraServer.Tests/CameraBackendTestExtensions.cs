// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Tests;

internal static class CameraBackendTestExtensions
{
    /// <summary>A change without permission to restart the stream (<c>MayRestartStream</c> false): what a v1 session, or a v2 session for a change that does not restart, sends.</summary>
    public static ControlResult SetControl(this ICameraBackend backend, string id, double value) =>
        backend.SetControl(new ControlChange(id, value));

    /// <summary>A change with permission to restart the stream: what a v2 session sends for a change it announces with STREAM_INFO <c>reconfiguring</c>.</summary>
    public static ControlResult SetControlMayRestart(this ICameraBackend backend, string id, double value) =>
        backend.SetControl(new ControlChange(id, value, MayRestartStream: true));
}
