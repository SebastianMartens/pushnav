// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Text.Json;
using System.Text.Json.Serialization;
using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;
using PushNav.CameraServer.Server;

namespace PushNav.CameraServer.Cli;

/// <summary><c>--list</c> output: detected devices and their capabilities (diagnostics, spec 4.4).</summary>
internal sealed class ListOutput
{
    public string Server { get; init; } = "camera_server_dotnet";

    public string Version { get; init; } = "";

    public int ProtocolVersion { get; init; } = ProtocolInfo.Version;

    public List<ListedDevice> Devices { get; init; } = [];
}

internal sealed class ListedDevice
{
    public string Backend { get; init; } = "";

    /// <summary>HELLO <c>backend</c> value.</summary>
    public string HelloBackend { get; init; } = "";

    public string CameraKey { get; init; } = "";

    public string Model { get; init; } = "";

    public string? Serial { get; init; }

    public string? Vid { get; init; }

    public string? Pid { get; init; }

    public ListedGeometry? Geometry { get; init; }

    public int? DefaultFps { get; init; }

    /// <summary>Controls in the backend's real units and ranges (protocol v2 descriptor form).</summary>
    public List<ControlJson>? Controls { get; init; }

    /// <summary>Set instead of geometry/controls when the device was found but could not be opened.</summary>
    public string? Error { get; init; }
}

internal sealed class ListedGeometry
{
    public int Width { get; init; }

    public int Height { get; init; }

    public int Binning { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ListOutput))]
internal sealed partial class ListJsonContext : JsonSerializerContext
{
}

internal static class DeviceLister
{
    /// <summary>
    /// Probes every backend once (no waiting), opens each device to read its capabilities, closes it again.
    /// </summary>
    public static ListOutput Collect(IReadOnlyList<ICameraBackend> backends, Logger log)
    {
        var output = new ListOutput { Version = ClientSession.BackendVersion };
        foreach (var backend in backends)
        {
            IReadOnlyList<DeviceInfo> devices;
            try
            {
                devices = backend.Probe();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Warn($"Backend '{backend.Id}' probe failed: {ex.Message}");
                continue;
            }

            foreach (var device in devices)
            {
                output.Devices.Add(Describe(backend, device, log));
            }
        }

        return output;
    }

    public static string ToJson(ListOutput output) =>
        JsonSerializer.Serialize(output, ListJsonContext.Default.ListOutput);

    private static ListedDevice Describe(ICameraBackend backend, DeviceInfo device, Logger log)
    {
        string? vid = device.Vid is { } v ? v.ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : null;
        string? pid = device.Pid is { } p ? p.ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : null;
        try
        {
            backend.Open(device);
            var geometry = backend.Geometry;
            return new ListedDevice
            {
                Backend = backend.Id,
                HelloBackend = backend.HelloName,
                CameraKey = device.CameraKey,
                Model = device.Model,
                Serial = device.Serial,
                Vid = vid,
                Pid = pid,
                Geometry = new ListedGeometry { Width = geometry.Width, Height = geometry.Height, Binning = geometry.Binning },
                DefaultFps = backend.DefaultFps,
                Controls = backend.Controls.Select(ControlMapper.ToJson).ToList(),
            };
        }
        catch (CameraException ex)
        {
            log.Warn($"Could not open {device.Model}: {ex.Message}");
            return new ListedDevice
            {
                Backend = backend.Id,
                HelloBackend = backend.HelloName,
                CameraKey = device.CameraKey,
                Model = device.Model,
                Serial = device.Serial,
                Vid = vid,
                Pid = pid,
                Error = ex.Message,
            };
        }
        finally
        {
            backend.Stop();
        }
    }
}
