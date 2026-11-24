// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Server;

/// <summary>
/// The one place a session hands a SET_CONTROL to the backend and logs what came of it. The two methods own the
/// <see cref="ControlChange.MayRestartStream"/> flag: a backend restarts its stream only for a change the session announced.
/// </summary>
internal static class BackendControls
{
    /// <summary>
    /// Sets a control without permission to restart the stream (<see cref="ControlChange.MayRestartStream"/> false). A camera that
    /// refuses the change, or fails to apply it, is a rejected <see cref="ControlResult"/> (logged) and the session carries on.
    /// </summary>
    /// <param name="requested">The value as the client sent it, for the log, when that is not <paramref name="value"/> (a v1 client counts exposure in 100 µs).</param>
    public static ControlResult Set(ICameraBackend backend, Logger log, string id, double value, double? requested = null)
    {
        try
        {
            var result = backend.SetControl(new ControlChange(id, value));
            LogResult(log, id, requested ?? value, result, restarted: false);
            return result;
        }
        catch (CameraException ex)
        {
            log.Warn($"SET_CONTROL {id} failed: {ex.Message}");
            return ControlResult.Rejected(ex.Message);
        }
    }

    /// <summary>
    /// Sets a control with permission to restart the stream (<see cref="ControlChange.MayRestartStream"/> true); the session has
    /// announced the restart (<see cref="ICameraBackend.WouldRestartStream"/>). A camera that refuses the change is a rejected
    /// <see cref="ControlResult"/>, as in <see cref="Set"/>; one that fails to restart the stream throws
    /// <see cref="StreamRestartFailedException"/>, because nobody knows what state it is in.
    /// </summary>
    public static ControlResult SetRestartingStream(ICameraBackend backend, Logger log, string id, double value)
    {
        try
        {
            var result = backend.SetControl(new ControlChange(id, value, MayRestartStream: true));
            LogResult(log, id, value, result, restarted: true);
            return result;
        }
        catch (CameraException ex)
        {
            throw new StreamRestartFailedException($"Stream reconfiguration failed ({id} = {value}): {ex.Message}", ex);
        }
    }

    private static void LogResult(Logger log, string id, double shown, ControlResult result, bool restarted)
    {
        if (result.Accepted)
        {
            log.Debug(restarted ? $"Set {id} = {shown} (stream restarted)" : $"Set {id} = {shown}");
        }
        else
        {
            log.Debug($"SET_CONTROL {id}={shown} rejected: {result.Message}");
        }
    }
}
