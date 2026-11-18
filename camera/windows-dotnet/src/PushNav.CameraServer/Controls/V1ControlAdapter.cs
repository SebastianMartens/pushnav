// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Controls;

/// <summary>
/// Projects a backend's native controls onto protocol v1 (spec 5.3 / 7): only <c>exposure</c> and <c>gain</c>,
/// both <c>int</c>, with exposure in the legacy <c>"100us"</c> unit so the existing UI formats it correctly.
/// Native values stay in real units (microseconds); this is the only place that knows about the v1 unit and cap.
/// </summary>
internal static class V1ControlAdapter
{
    /// <summary>Microseconds per v1 exposure unit ("100us").</summary>
    public const double ExposureUnitMicroseconds = 100;

    public const string ExposureUnitTag = "100us";

    /// <summary>
    /// Longest exposure a v1 session may use: 1.5 s. The engine's frame-stall timeout is a fixed 2.0 s
    /// (SPEC_PROTOCOL_CAMERA.md section 7), so a longer exposure makes it restart the camera server. Protocol v2 derives
    /// the timeout from the frame interval (section 11.7) and lifts this cap for v2 sessions.
    /// </summary>
    public const double MaxExposureMicroseconds = 1_500_000;

    public static bool IsExposedInV1(string id) => id is ControlIds.Exposure or ControlIds.Gain;

    /// <summary>The v1 CONTROL_INFO view of <paramref name="native"/>, in the order exposure, gain.</summary>
    public static ControlInfoMessage Project(IReadOnlyList<ControlDescriptor> native)
    {
        var info = new ControlInfoMessage();
        foreach (string id in new[] { ControlIds.Exposure, ControlIds.Gain })
        {
            var control = native.FirstOrDefault(c => c.Id == id);
            if (control is { Kind: ControlKind.Int or ControlKind.Float })
            {
                info.Controls.Add(id == ControlIds.Exposure ? ProjectExposure(control) : ProjectGain(control));
            }
        }

        return info;
    }

    /// <summary>
    /// Converts a v1 SET_CONTROL value to the native unit. Returns false for ids that v1 does not expose
    /// (the server then ignores the change and just re-sends CONTROL_INFO).
    /// </summary>
    public static bool TryToNative(string id, double v1Value, out double nativeValue)
    {
        switch (id)
        {
            case ControlIds.Exposure:
                nativeValue = Math.Min(v1Value * ExposureUnitMicroseconds, MaxExposureMicroseconds);
                return true;
            case ControlIds.Gain:
                nativeValue = v1Value;
                return true;
            default:
                nativeValue = 0;
                return false;
        }
    }

    private static ControlJson ProjectExposure(ControlDescriptor c)
    {
        double min = Math.Max(1, Math.Ceiling(c.Min / ExposureUnitMicroseconds));
        double max = Math.Max(min, Math.Floor(Math.Min(c.Max, MaxExposureMicroseconds) / ExposureUnitMicroseconds));
        double cur = Math.Clamp(Math.Round(c.Cur / ExposureUnitMicroseconds, MidpointRounding.AwayFromZero), min, max);
        return new ControlJson
        {
            Id = c.Id,
            Label = c.Label,
            Type = "int",
            Min = min,
            Max = max,
            Step = 1,
            Cur = cur,
            Unit = ExposureUnitTag,
        };
    }

    private static ControlJson ProjectGain(ControlDescriptor c) => new()
    {
        Id = c.Id,
        Label = c.Label,
        Type = "int",
        Min = Math.Ceiling(c.Min),
        Max = Math.Floor(c.Max),
        Step = Math.Max(1, Math.Round(c.Step, MidpointRounding.AwayFromZero)),
        Cur = Math.Round(c.Cur, MidpointRounding.AwayFromZero),
        Unit = "raw",
    };
}
