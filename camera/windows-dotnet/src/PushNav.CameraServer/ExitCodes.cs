// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer;

/// <summary>
/// Process exit codes (spec 4.3). The engine's SubprocessManager treats any exit as "server gone";
/// code 3 additionally triggers the fallback to the C server (spec 8.2).
/// </summary>
internal static class ExitCodes
{
    /// <summary>Normal end: client disconnected, or Ctrl+C / terminate, or <c>--list</c> done.</summary>
    public const int Ok = 0;

    /// <summary>Fatal: device lost, capture failure, protocol failure, port unavailable.</summary>
    public const int Fatal = 1;

    /// <summary>Command line could not be parsed (not in the spec's 0/1/3; kept apart so it is never mistaken for code 3).</summary>
    public const int Usage = 2;

    /// <summary><c>NO_SUPPORTED_CAMERA</c>: no usable camera found; the engine may start the C server instead.</summary>
    public const int NoSupportedCamera = 3;
}
