// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Runtime.InteropServices;
using PushNav.CameraServer;

// Camera server for Windows (C# / Native AOT). Speaks the PushNav camera protocol on 127.0.0.1;
// see specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md. All logic lives in CameraServerApp.
using var stop = new CancellationTokenSource();

// Ctrl+C and Ctrl+Break: stop serving and exit normally.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

// CTRL_CLOSE_EVENT (SIGHUP) and termination requests, where the OS delivers them as signals.
var registrations = new List<PosixSignalRegistration>();
foreach (var signal in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM })
{
    try
    {
        registrations.Add(PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        }));
    }
    catch (PlatformNotSupportedException)
    {
        // Not available for this signal on this platform; Ctrl+C above still works.
    }
}

try
{
    return CameraServerApp.Run(args, Console.Out, Console.Error, stop.Token);
}
finally
{
    foreach (var registration in registrations)
    {
        registration.Dispose();
    }
}
