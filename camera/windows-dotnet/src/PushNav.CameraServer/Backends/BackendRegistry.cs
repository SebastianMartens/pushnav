// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Cli;

namespace PushNav.CameraServer.Backends;

/// <summary>
/// The static list of backends (no plugin loading under Native AOT, spec 4.1). Probe order is IDS, then UVC;
/// the fake backend is only ever present when asked for with <c>--backend fake</c>.
/// </summary>
internal static class BackendRegistry
{
    public static IReadOnlyList<ICameraBackend> Create(CommandLineOptions options, Logger log)
    {
        var backends = new List<ICameraBackend>();

        // IDS (spec 5) and UVC (spec 6) are registered here by their own tasks, in this order.
        if (options.Backend == FakeBackend.BackendId)
        {
            backends.Add(new FakeBackend(options.FakeImagesDirectory, options.JpegQuality, log));
        }

        return backends;
    }
}
