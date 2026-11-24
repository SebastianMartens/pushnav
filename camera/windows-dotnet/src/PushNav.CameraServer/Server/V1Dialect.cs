// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Server;

/// <summary>
/// Protocol v1 exactly as the other servers speak it (<c>SPEC_PROTOCOL_CAMERA.md</c> 1-9): <c>exposure</c> in <c>"100us"</c>
/// capped at 1.5 s and <c>gain</c>, CONTROL_INFO after every SET_CONTROL, no STREAM_INFO. <see cref="V1ControlAdapter"/> is the view.
/// </summary>
internal sealed class V1Dialect : IProtocolDialect
{
    private readonly ICameraBackend _backend;
    private readonly ISessionChannel _channel;
    private readonly Logger _log;

    public V1Dialect(ICameraBackend backend, ISessionChannel channel, Logger log)
    {
        _backend = backend;
        _channel = channel;
        _log = log;
    }

    public int Version => 1;

    public void SendInitial() => SendControlInfo();

    public void SendControlInfo() =>
        _channel.Send(MessageType.ControlInfo, ProtocolJson.Serialize(V1ControlAdapter.Project(_backend.Controls)));

    public void HandleSetControl(string id, double value)
    {
        if (V1ControlAdapter.IsExposedInV1(id) && V1ControlAdapter.TryToNative(id, value, out double native))
        {
            try
            {
                var result = _backend.SetControl(id, native);
                if (!result.Accepted)
                {
                    _log.Debug($"SET_CONTROL {id}={value} rejected: {result.Message}");
                }
                else
                {
                    _log.Debug($"Set {id} = {value}");
                }
            }
            catch (CameraException ex)
            {
                _log.Warn($"SET_CONTROL {id} failed: {ex.Message}");
            }
        }
        else
        {
            _log.Debug($"Unknown control: {id}");
        }

        // Always answer with the applied state, even if nothing changed (spec 5.1: CONTROL_INFO after every SET_CONTROL).
        SendControlInfo();
    }

    public void HandleSetStreamConfig(byte[] payload) => _log.Debug("Ignoring SET_STREAM_CONFIG in a v1 session");

    public bool AdmitFrame(StreamGeometry geometry) => true;
}
