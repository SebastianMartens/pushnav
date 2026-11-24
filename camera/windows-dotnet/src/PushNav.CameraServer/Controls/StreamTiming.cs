// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Controls;

/// <summary>What the controls say about the pace of the stream (STREAM_INFO <c>frame_interval_us</c> and <c>exposure_us</c>).</summary>
/// <param name="FrameIntervalUs">Time between frames.</param>
/// <param name="ExposureUs">Current exposure in microseconds (0 when the camera has no exposure control).</param>
internal readonly record struct StreamTiming(long FrameIntervalUs, long ExposureUs)
{
    /// <summary>
    /// Reads the timing off <paramref name="controls"/>. The interval is one over the <c>fps</c> control, but never shorter than the
    /// exposure: below the frame-rate floor a longer exposure stretches the frame (a 4 s exposure comes out every 4 s), which is
    /// also what the client's stall timeout has to allow for (protocol 11.7). A backend without an <c>fps</c> control runs at
    /// <paramref name="fallbackFps"/>.
    /// </summary>
    public static StreamTiming From(IReadOnlyList<ControlDescriptor> controls, int fallbackFps)
    {
        double fps = controls.FirstOrDefault(c => c.Id == ControlIds.Fps)?.Cur ?? fallbackFps;
        double exposure = controls.FirstOrDefault(c => c.Id == ControlIds.Exposure)?.Cur ?? 0;
        double interval = fps > 0 ? 1_000_000.0 / fps : 0;
        long exposureUs = (long)Math.Round(exposure);
        return new StreamTiming((long)Math.Round(Math.Max(interval, exposureUs)), exposureUs);
    }
}
