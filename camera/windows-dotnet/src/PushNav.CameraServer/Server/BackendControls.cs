// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Server;

/// <summary>The one place a session hands a SET_CONTROL to the backend and logs what came of it.</summary>
internal static class BackendControls
{
    /// <summary>
    /// Sets a control. A camera that refuses the change, or fails to apply it, is a rejected <see cref="ControlResult"/> (logged) and
    /// the session carries on. The one fatal case is a stream restart (<paramref name="restartsStream"/>) that throws:
    /// <see cref="StreamRestartFailedException"/>.
    /// </summary>
    public static ControlResult Set(ICameraBackend backend, Logger log, string id, double value, bool restartsStream = false)
    {
        try
        {
            var result = backend.SetControl(id, value);
            if (result.Accepted)
            {
                log.Debug(restartsStream ? $"Set {id} = {value} (stream restarted)" : $"Set {id} = {value}");
            }
            else
            {
                log.Debug($"SET_CONTROL {id}={value} rejected: {result.Message}");
            }

            return result;
        }
        catch (CameraException ex) when (restartsStream)
        {
            throw new StreamRestartFailedException($"Stream reconfiguration failed ({id} = {value}): {ex.Message}", ex);
        }
        catch (CameraException ex)
        {
            log.Warn($"SET_CONTROL {id} failed: {ex.Message}");
            return ControlResult.Rejected(ex.Message);
        }
    }
}
