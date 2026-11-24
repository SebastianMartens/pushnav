// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Server;

/// <summary>
/// The backend could not restart the stream for a v2 reconfiguration, so nobody knows what state the camera is in
/// (protocol 11.5: ERROR, exit 1). The only exception that ends a session because of a control change: a control the camera
/// merely refuses is a <see cref="Backends.ControlResult"/>, never an exception that gets this far.
/// </summary>
internal sealed class StreamRestartFailedException : Exception
{
    public StreamRestartFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
