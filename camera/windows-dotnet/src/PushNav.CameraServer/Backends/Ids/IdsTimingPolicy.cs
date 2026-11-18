// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>What one pixel clock allows (measured by the backend at open, spec 5.4).</summary>
/// <param name="Megahertz">Pixel clock in MHz.</param>
/// <param name="MinFrameTimeSeconds">Shortest frame time at this clock (sets the highest fps).</param>
/// <param name="MaxFrameTimeSeconds">Longest frame time at this clock (sets the lowest fps).</param>
/// <param name="MinExposureMs">Shortest exposure at this clock.</param>
internal readonly record struct PixelClockTiming(uint Megahertz, double MinFrameTimeSeconds, double MaxFrameTimeSeconds, double MinExposureMs)
{
    public double MaxFps => 1.0 / MinFrameTimeSeconds;

    public double MinFps => 1.0 / MaxFrameTimeSeconds;
}

/// <summary>
/// The frame-rate / pixel-clock / exposure policy of the IDS backend (spec 5.4), free of any camera calls so it can be
/// tested exactly. The protocol never exposes the pixel clock; the backend picks it:
/// <list type="bullet">
/// <item>for a requested fps, the <b>lowest</b> pixel clock whose maximum fps reaches it (a lower clock allows longer frames and so longer exposures);</item>
/// <item>exposure has priority: an exposure longer than the frame interval lowers the fps instead of being clamped;</item>
/// <item>an exposure that does not fit even at the fps floor switches the sensor to long-exposure mode.</item>
/// </list>
/// </summary>
internal static class IdsTimingPolicy
{
    /// <summary>The normal (non long-exposure) frame rate never goes below this; slower frames are long-exposure mode.</summary>
    public const double FloorFps = 1.0;

    /// <summary>
    /// Time that has to stay free in a frame interval next to the exposure. The camera reports 0.5 to 0.8 ms less
    /// exposure than frame time; 1 ms is the first guess, the backend then verifies against the real exposure range.
    /// </summary>
    public const double ExposureHeadroomUs = 1000;

    /// <summary>The lowest pixel clock that reaches <paramref name="fps"/>; the highest one when none does. <paramref name="table"/> must be sorted ascending.</summary>
    public static PixelClockTiming ChooseClock(IReadOnlyList<PixelClockTiming> table, double fps)
    {
        if (table.Count == 0)
        {
            throw new ArgumentException("No pixel clock known", nameof(table));
        }

        foreach (var clock in table)
        {
            if (clock.MaxFps >= fps)
            {
                return clock;
            }
        }

        return table[^1];
    }

    /// <summary>
    /// The frame rate to run at: the preferred one, but never so high that the exposure no longer fits into the frame
    /// interval, and never below <see cref="FloorFps"/>.
    /// </summary>
    public static double TargetFps(double preferredFps, double exposureUs)
    {
        double fitsExposure = 1_000_000.0 / (exposureUs + ExposureHeadroomUs);
        return Math.Max(FloorFps, Math.Min(preferredFps, fitsExposure));
    }

    /// <summary>Highest fps the exposure allows on a camera whose fastest clock reaches <paramref name="clockMaxFps"/> (floored to 0.01 for the control's step).</summary>
    public static double MaxFpsFor(double exposureUs, double clockMaxFps)
    {
        double limit = Math.Floor(Math.Min(clockMaxFps, 1_000_000.0 / (exposureUs + ExposureHeadroomUs)) * 100) / 100;
        return Math.Max(FloorFps, limit);
    }

    /// <summary>True when the exposure exceeds what the normal mode offers at the fps floor, so long-exposure mode is needed.</summary>
    public static bool NeedsLongExposure(double exposureMs, double normalMaxAtFloorMs) => exposureMs > normalMaxAtFloorMs;

    public const int DefaultMinimumTimeoutMilliseconds = 1000;

    /// <summary>
    /// How long to wait for a frame before counting a timeout (spec 5.2): twice the frame interval plus the exposure,
    /// but at least one second.
    /// </summary>
    public static int FrameTimeoutMilliseconds(double fps, double exposureMs, bool longExposure, int minimumMilliseconds = DefaultMinimumTimeoutMilliseconds)
    {
        double intervalMs = longExposure ? exposureMs : 1000.0 / Math.Max(fps, 0.1);
        double ms = (2 * intervalMs) + exposureMs;
        return (int)Math.Clamp(Math.Ceiling(Math.Max(minimumMilliseconds, ms)), minimumMilliseconds, 3_600_000.0);
    }
}
