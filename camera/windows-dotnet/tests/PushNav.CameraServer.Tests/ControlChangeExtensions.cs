// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Tests;

internal static class ControlChangeExtensions
{
    /// <summary>A change as a v1 session sends it: no permission to restart the stream.</summary>
    public static ControlResult SetControl(this ICameraBackend backend, string id, double value) =>
        backend.SetControl(new ControlChange(id, value));

    /// <summary>A change as a v2 session sends it for a change it has announced: the backend may restart the stream.</summary>
    public static ControlResult SetControlMayRestart(this ICameraBackend backend, string id, double value) =>
        backend.SetControl(new ControlChange(id, value, MayRestartStream: true));
}
