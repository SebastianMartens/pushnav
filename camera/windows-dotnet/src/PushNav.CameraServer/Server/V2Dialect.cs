// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;
using PushNav.CameraServer.Controls;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Server;

/// <summary>
/// Protocol v2 (<c>SPEC_PROTOCOL_CAMERA.md</c> 11): the backend's native controls (exposure in µs without the v1 cap, <c>fps</c>, gain,
/// binning where the camera has it), CONTROL_INFO with every range after every SET_CONTROL, and STREAM_INFO for the stream:
/// generation 1 after the first CONTROL_INFO, the same generation again when the frame interval changes, and
/// <c>reconfiguring</c> then <c>streaming</c> with generation + 1 around a change that restarts the stream. Which changes restart the
/// stream is the backend's call (<see cref="ICameraBackend.WouldRestartStream"/>); a v2 session is the one that allows it
/// (<see cref="ControlChange.MayRestartStream"/>), because it can announce it.
/// </summary>
internal sealed class V2Dialect : IProtocolDialect
{
    private readonly ICameraBackend _backend;
    private readonly ISessionChannel _channel;
    private readonly Logger _log;
    private readonly StreamAnnouncer _announcer = new();

    public V2Dialect(ICameraBackend backend, ISessionChannel channel, Logger log)
    {
        _backend = backend;
        _channel = channel;
        _log = log;
    }

    public int Version => 2;

    public void SendInitial()
    {
        SendControlInfo();
        var geometry = _backend.Geometry;
        var timing = CurrentTiming();
        Announce(() => _announcer.Start(geometry, timing));
    }

    public void SendControlInfo() =>
        _channel.Send(MessageType.ControlInfo, ProtocolJson.Serialize(ControlMapper.Project(_backend.Controls)));

    public void HandleSetControl(string id, double value)
    {
        // One change per request: the question and the answer are about the same object. This is the one place a
        // session grants the permission to restart the stream; BackendControls.Set takes it away again for a change that does not.
        var change = new ControlChange(id, value, MayRestartStream: true);
        if (_backend.WouldRestartStream(change))
        {
            Reconfigure(change);
            return;
        }

        // servers ignore ids they do not advertise and answer with CONTROL_INFO: the backend rejects them
        BackendControls.Set(_backend, _log, change);
        SendControlInfo();
        var timing = CurrentTiming();
        Announce(() => _announcer.TimingChanged(timing));
    }

    public void HandleSetStreamConfig(byte[] payload)
    {
        // Only JPEG is advertised, so the one request that is not ignored is the one for the format already in use,
        // which is answered with the current STREAM_INFO (a reconfiguration that changes nothing).
        var command = ProtocolJson.ParseSetStreamConfig(payload);
        if (command?.FrameFormat != FrameFormats.Jpeg)
        {
            _log.Debug($"Ignoring SET_STREAM_CONFIG frame_format={command?.FrameFormat ?? "(none)"}: not advertised");
            return;
        }

        Announce(() => _announcer.Repeat());
    }

    public bool AdmitFrame(StreamGeometry geometry) => _announcer.Admits(geometry);

    /// <summary>
    /// <c>reconfiguring</c>, the backend restarts its stream in-process, CONTROL_INFO with the new ranges, then <c>streaming</c> with
    /// generation + 1 and the new size. While it runs the writer lets no frame through, so none of the new size precedes the last
    /// STREAM_INFO and none of the old size follows it.
    /// </summary>
    private void Reconfigure(ControlChange change)
    {
        Announce(() => _announcer.BeginReconfiguration());
        var result = BackendControls.SetRestartingStream(_backend, _log, change); // throws if the restart fails
        SendControlInfo();
        var geometry = _backend.Geometry;
        var timing = CurrentTiming();
        Announce(() => result.Accepted ? _announcer.Complete(geometry, timing) : _announcer.Resume(timing));
    }

    private StreamTiming CurrentTiming() => StreamTiming.From(_backend.Controls, _backend.DefaultFps);

    /// <summary>
    /// The one place the announcer changes: the transition and the send are one step under the channel's lock, so what the frame
    /// writer admits is always what the client was last told.
    /// </summary>
    private void Announce(Func<StreamInfoMessage?> transition) =>
        _channel.SendIfAny(MessageType.StreamInfo, () => transition() is { } info ? ProtocolJson.Serialize(info) : null);
}
