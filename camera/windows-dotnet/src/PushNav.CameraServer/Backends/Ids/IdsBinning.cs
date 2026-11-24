// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Controls;

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>
/// The binning factors of a uEye camera. <c>is_SetBinning</c> takes one bit per axis and factor; the protocol's
/// <c>binning</c> control is NxN (option value = N, protocol 11.4.2), so a factor counts as offered when the sensor
/// supports it on both axes.
/// </summary>
internal static class IdsBinning
{
    private static readonly (int Factor, int Mode)[] Modes =
    [
        (2, UEyeConst.BinningVertical2x | UEyeConst.BinningHorizontal2x),
        (3, UEyeConst.BinningVertical3x | UEyeConst.BinningHorizontal3x),
        (4, UEyeConst.BinningVertical4x | UEyeConst.BinningHorizontal4x),
        (5, UEyeConst.BinningVertical5x | UEyeConst.BinningHorizontal5x),
        (6, UEyeConst.BinningVertical6x | UEyeConst.BinningHorizontal6x),
        (8, UEyeConst.BinningVertical8x | UEyeConst.BinningHorizontal8x),
        (16, UEyeConst.BinningVertical16x | UEyeConst.BinningHorizontal16x),
    ];

    /// <summary>The factors a <c>IS_GET_SUPPORTED_BINNING</c> mask offers on both axes, ascending, without 1 (no binning is always possible).</summary>
    public static IReadOnlyList<int> FactorsIn(int supportedModes) =>
        Modes.Where(m => (supportedModes & m.Mode) == m.Mode).Select(m => m.Factor).ToArray();

    /// <summary>The <c>is_SetBinning</c> mode for NxN binning; 0 (<see cref="UEyeConst.BinningDisable"/>) for 1. Throws for a factor the API has no mode for.</summary>
    public static int ModeFor(int factor)
    {
        if (factor == 1)
        {
            return UEyeConst.BinningDisable;
        }

        foreach (var (f, mode) in Modes)
        {
            if (f == factor)
            {
                return mode;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(factor), factor, "No uEye binning mode for this factor");
    }

    /// <summary>The <c>binning</c> control for a camera that offers <paramref name="factors"/> besides 1x1, or null when it offers none.</summary>
    public static ControlDescriptor? Descriptor(IReadOnlyList<int> factors, int current) =>
        factors.Count == 0
            ? null
            : new ControlDescriptor
            {
                Id = ControlIds.Binning,
                Label = "Binning",
                Kind = ControlKind.Enum,
                Cur = current,
                Options = new[] { 1 }.Concat(factors).Select(f => new ControlOption(f, $"{f}x{f}")).ToArray(),
                RestartsStream = true,
            };
}
