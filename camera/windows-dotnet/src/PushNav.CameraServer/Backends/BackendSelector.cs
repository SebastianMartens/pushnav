// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Backends;

internal sealed record SelectedDevice(ICameraBackend Backend, DeviceInfo Device);

/// <summary>
/// Picks the backend and device to serve (spec 4.3): backends are probed in registration order, the first
/// device wins. When nothing is found it keeps probing for a while (a camera that was just plugged in or is
/// waking up), like the C server's 8 s wait.
/// </summary>
internal static class BackendSelector
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DefaultStep = TimeSpan.FromMilliseconds(500);

    /// <summary>One probing pass over all backends; the first device found wins. Never throws for a misbehaving backend.</summary>
    public static SelectedDevice? ProbeOnce(IReadOnlyList<ICameraBackend> backends, Logger? log = null)
    {
        foreach (var backend in backends)
        {
            IReadOnlyList<DeviceInfo> devices;
            try
            {
                devices = backend.Probe();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Probe() must not throw; if it does anyway, treat the backend as having no device.
                log?.Warn($"Backend '{backend.Id}' probe failed: {ex.Message}");
                continue;
            }

            if (devices.Count > 0)
            {
                return new SelectedDevice(backend, devices[0]);
            }
        }

        return null;
    }

    /// <summary>
    /// Probes at t = 0, step, 2 * step ... up to <paramref name="wait"/>, calling <paramref name="delay"/> between passes.
    /// Returns null when nothing showed up or <paramref name="cancellation"/> fired.
    /// </summary>
    public static SelectedDevice? Select(
        IReadOnlyList<ICameraBackend> backends,
        TimeSpan wait,
        TimeSpan step,
        Action<TimeSpan> delay,
        Logger? log = null,
        CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        if (backends.Count == 0)
        {
            return null; // nothing that could ever find a device: do not wait
        }

        int passes = 1 + (int)(wait.Ticks / step.Ticks);
        for (int pass = 0; pass < passes; pass++)
        {
            if (cancellation.IsCancellationRequested)
            {
                return null;
            }

            var found = ProbeOnce(backends, log);
            if (found != null)
            {
                return found;
            }

            if (pass < passes - 1)
            {
                delay(step);
            }
        }

        return null;
    }
}
