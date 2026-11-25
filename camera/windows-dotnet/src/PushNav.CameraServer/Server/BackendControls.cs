// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Server;

/// <summary>
/// The one place a session hands a SET_CONTROL to the backend and logs what came of it. A session builds one
/// <see cref="ControlChange"/> per request; the method it passes it to decides whether the backend may restart the stream:
/// <see cref="Set"/> never allows it, <see cref="SetRestartingStream"/> requires the change to carry the permission.
/// </summary>
internal static class BackendControls
{
    /// <summary>
    /// Sets a control **without** permission to restart the stream: the backend always gets <see cref="ControlChange.MayRestartStream"/>
    /// false, whatever <paramref name="change"/> carries (a session builds one change per request and asks the backend
    /// <see cref="ICameraBackend.WouldRestartStream"/> with it; when the answer is no, this is the way to set it). A camera that
    /// refuses the change, or fails to apply it, is a rejected <see cref="ControlResult"/> (logged) and the session carries on.
    /// </summary>
    /// <param name="requested">The value as the client sent it, for the log, when that is not the change's value (a v1 client counts exposure in 100 µs).</param>
    public static ControlResult Set(ICameraBackend backend, Logger log, ControlChange change, double? requested = null)
    {
        try
        {
            var result = backend.SetControl(change with { MayRestartStream = false });
            LogResult(log, change.Id, requested ?? change.Value, result, restarted: false);
            return result;
        }
        catch (CameraException ex)
        {
            log.Warn($"SET_CONTROL {change.Id} failed: {ex.Message}");
            return ControlResult.Rejected(ex.Message);
        }
    }

    /// <summary>
    /// Sets a control that <see cref="ICameraBackend.WouldRestartStream"/> said restarts the stream: <paramref name="change"/> is the
    /// very change that was asked about, so it must carry <see cref="ControlChange.MayRestartStream"/> (an
    /// <see cref="ArgumentException"/> otherwise), and the session has announced the restart. A camera that refuses the change is a
    /// rejected <see cref="ControlResult"/>, as in <see cref="Set"/>; one that fails to restart the stream throws
    /// <see cref="StreamRestartFailedException"/>, because nobody knows what state it is in.
    /// </summary>
    public static ControlResult SetRestartingStream(ICameraBackend backend, Logger log, ControlChange change)
    {
        if (!change.MayRestartStream)
        {
            throw new ArgumentException("A restarting change must carry MayRestartStream", nameof(change));
        }

        try
        {
            var result = backend.SetControl(change);
            LogResult(log, change.Id, change.Value, result, restarted: true);
            return result;
        }
        catch (CameraException ex)
        {
            throw new StreamRestartFailedException($"Stream reconfiguration failed ({change.Id} = {change.Value}): {ex.Message}", ex);
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
