// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Controls;

/// <summary>Known control ids (SPEC_PROTOCOL_CAMERA.md 11.4.2; v1 only uses exposure and gain).</summary>
internal static class ControlIds
{
    public const string Exposure = "exposure";
    public const string Gain = "gain";
    public const string Fps = "fps";
    public const string Binning = "binning";
}

internal enum ControlKind
{
    Int,
    Float,
    Enum,
}

/// <summary>One selectable value of an <see cref="ControlKind.Enum"/> control.</summary>
internal sealed record ControlOption(double Value, string Label);

/// <summary>
/// A camera control in the backend's real units and ranges (exposure in microseconds, fps in Hz, ...).
/// Immutable snapshot: backends return a new list after every change.
/// </summary>
internal sealed record ControlDescriptor
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required ControlKind Kind { get; init; }

    public required double Cur { get; init; }

    /// <summary>Unit tag ("us", "raw", "fps", ...); null for unit-less controls.</summary>
    public string? Unit { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    public double Step { get; init; } = 1;

    /// <summary>Options of an enum control.</summary>
    public IReadOnlyList<ControlOption>? Options { get; init; }

    /// <summary>Changing the control restarts the stream (new geometry / STREAM_INFO in v2).</summary>
    public bool RestartsStream { get; init; }

    /// <summary>UI hint, <c>"log"</c> or null (linear).</summary>
    public string? Scale { get; init; }

    /// <summary>
    /// Applies the protocol's SET_CONTROL rule (SPEC_PROTOCOL_CAMERA.md 11.4.3): numeric controls are clamped to
    /// [Min, Max] and rounded to the nearest step (counted from Min); int controls end up whole.
    /// Returns false for an enum value that is not one of the options (the change is rejected).
    /// </summary>
    public bool TryCoerce(double requested, out double coerced)
    {
        coerced = Cur;
        if (double.IsNaN(requested) || double.IsInfinity(requested))
        {
            return false;
        }

        if (Kind == ControlKind.Enum)
        {
            foreach (var option in Options ?? [])
            {
                if (option.Value == requested)
                {
                    coerced = requested;
                    return true;
                }
            }

            return false;
        }

        double value = Math.Clamp(requested, Min, Max);
        if (Step > 0)
        {
            double steps = Math.Round((value - Min) / Step, MidpointRounding.AwayFromZero);
            value = Min + (steps * Step);
            if (value > Max)
            {
                value -= Step; // rounded past the last whole step: take the step below
            }
        }

        if (Kind == ControlKind.Int)
        {
            value = Math.Round(value, MidpointRounding.AwayFromZero);
        }
        else
        {
            value = Math.Round(value, 9); // drop floating-point noise from Min + n * Step
        }

        coerced = Math.Clamp(value, Min, Max);
        return true;
    }
}
