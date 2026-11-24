// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Controls;

/// <summary>What the controls say about the pace of the stream (STREAM_INFO <c>frame_interval_us</c> and <c>exposure_us</c>).</summary>
internal static class FrameTiming
{
    /// <summary>
    /// Time between frames: one over the <c>fps</c> control, but never shorter than the exposure. Below the frame-rate floor a
    /// longer exposure stretches the frame (a 4 s exposure comes out every 4 s), which is also what the client's stall timeout has to
    /// allow for (protocol 11.7). A backend without an <c>fps</c> control runs at <paramref name="fallbackFps"/>.
    /// </summary>
    public static long FrameIntervalUs(IReadOnlyList<ControlDescriptor> controls, int fallbackFps)
    {
        double fps = controls.FirstOrDefault(c => c.Id == ControlIds.Fps)?.Cur ?? fallbackFps;
        double interval = fps > 0 ? 1_000_000.0 / fps : 0;
        return (long)Math.Round(Math.Max(interval, ExposureUs(controls)));
    }

    /// <summary>The <c>exposure</c> control's current value in microseconds (v2 exposure is always in µs); 0 without one.</summary>
    public static long ExposureUs(IReadOnlyList<ControlDescriptor> controls) =>
        (long)Math.Round(controls.FirstOrDefault(c => c.Id == ControlIds.Exposure)?.Cur ?? 0);
}
