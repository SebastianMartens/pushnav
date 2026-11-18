// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Controls;

/// <summary>Maps backend controls (native units) to their JSON descriptor form (protocol 11.4.1 superset).</summary>
internal static class ControlMapper
{
    public static ControlJson ToJson(ControlDescriptor c) => new()
    {
        Id = c.Id,
        Label = c.Label,
        Type = c.Kind switch
        {
            ControlKind.Int => "int",
            ControlKind.Float => "float",
            _ => "enum",
        },
        Unit = c.Unit,
        Min = c.Kind == ControlKind.Enum ? null : c.Min,
        Max = c.Kind == ControlKind.Enum ? null : c.Max,
        Step = c.Kind == ControlKind.Enum ? null : c.Step,
        Cur = c.Cur,
        Scale = c.Scale,
        RestartsStream = c.RestartsStream ? true : null,
        Options = c.Options?.Select(o => new ControlOptionJson { Value = o.Value, Label = o.Label }).ToList(),
    };
}
