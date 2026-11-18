// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>
/// Per-model knowledge the uEye API does not report (spec 4.2 "quirks"): the USB ids behind a model name, so the camera
/// gets the stable key <c>ids:1409:3060</c> the engine stores its per-camera settings under. Unknown models work with the
/// generic defaults and are keyed by model name.
/// </summary>
internal static class IdsQuirks
{
    /// <summary>USB vendor id of IDS Imaging Development Systems.</summary>
    public const int VendorId = 0x1409;

    private static readonly (string ModelPrefix, int ProductId)[] Models =
    [
        ("UI306", 0x3060), // UI-306xCP-M / -C (Sony IMX174), verified: USB\VID_1409&PID_3060
    ];

    /// <summary>VID/PID of <paramref name="model"/> when it is a model we know; both null otherwise.</summary>
    public static (int? Vid, int? Pid) UsbIdsFor(string model)
    {
        string name = model.Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var (prefix, pid) in Models)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (VendorId, pid);
            }
        }

        return (null, null);
    }
}
