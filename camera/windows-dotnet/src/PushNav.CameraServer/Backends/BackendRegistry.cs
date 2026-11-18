// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends.Fake;
using PushNav.CameraServer.Backends.Ids;
using PushNav.CameraServer.Cli;

namespace PushNav.CameraServer.Backends;

/// <summary>
/// The static list of backends (no plugin loading under Native AOT, spec 4.1). Probe order is IDS, then UVC;
/// the fake backend is only ever present when asked for with <c>--backend fake</c>.
/// </summary>
internal static class BackendRegistry
{
    public static IReadOnlyList<ICameraBackend> Create(CommandLineOptions options, Logger log) =>
        Create(options, log, () => IdsBackend.CreateDefault(options.JpegQuality, log));

    /// <summary>The registry with the IDS backend supplied by <paramref name="createIds"/> (tests pass one without the uEye API).</summary>
    public static IReadOnlyList<ICameraBackend> Create(CommandLineOptions options, Logger log, Func<IdsBackend> createIds)
    {
        var backends = new List<ICameraBackend>();

        // IDS (spec 5), then UVC (spec 6, registered by its own task). `--backend` limits the list to one backend.
        if (options.Backend is null or IdsBackend.BackendId)
        {
            backends.Add(createIds());
        }

        if (options.Backend == FakeBackend.BackendId)
        {
            backends.Add(new FakeBackend(options.FakeImagesDirectory, options.JpegQuality, log));
        }

        return backends;
    }
}
