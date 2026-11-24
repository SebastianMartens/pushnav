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
/// <c>reconfiguring</c> then <c>streaming</c> with generation + 1 around a change that restarts the stream.
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
        var controls = _backend.Controls;
        lock (_channel.WriteLock)
        {
            SendStreamInfo(_announcer.Start(_backend.Geometry, FrameIntervalUs(controls), FrameTiming.ExposureUs(controls)));
        }
    }

    public void SendControlInfo() =>
        _channel.Send(MessageType.ControlInfo, ProtocolJson.Serialize(ControlMapper.Project(_backend.Controls)));

    public void HandleSetControl(string id, double value)
    {
        var control = _backend.Controls.FirstOrDefault(c => c.Id == id);
        if (control == null)
        {
            _log.Debug($"Unknown control: {id}"); // servers ignore ids they do not advertise and answer with CONTROL_INFO
            SendControlInfo();
        }
        else if (RestartsStream(control, value))
        {
            Reconfigure(id, value);
        }
        else
        {
            Apply(id, value);
            SendControlInfo();
            AnnounceIntervalChange();
        }
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

        lock (_channel.WriteLock)
        {
            SendStreamInfo(_announcer.Repeat(FrameTiming.ExposureUs(_backend.Controls)));
        }
    }

    public bool AdmitFrame(StreamGeometry geometry) => _announcer.Admits(geometry);

    // -- changes that restart the stream -----------------------------------------

    private static bool RestartsStream(ControlDescriptor control, double requested) =>
        control.RestartsStream && control.TryCoerce(requested, out double coerced) && coerced != control.Cur;

    /// <summary>
    /// <c>reconfiguring</c>, the backend restarts its stream in-process, CONTROL_INFO with the new ranges, then <c>streaming</c> with
    /// generation + 1 and the new size. While it runs the writer lets no frame through, so none of the new size precedes the last
    /// STREAM_INFO and none of the old size follows it.
    /// </summary>
    private void Reconfigure(string id, double value)
    {
        lock (_channel.WriteLock)
        {
            SendStreamInfo(_announcer.BeginReconfiguration(FrameTiming.ExposureUs(_backend.Controls)));
        }

        ControlResult result;
        try
        {
            result = _backend.SetControl(id, value);
        }
        catch (CameraException ex)
        {
            throw new CameraException($"Stream reconfiguration failed ({id} = {value}): {ex.Message}", ex);
        }

        if (!result.Accepted)
        {
            _log.Debug($"SET_CONTROL {id}={value} rejected: {result.Message}");
        }
        else
        {
            _log.Debug($"Set {id} = {value} (stream restarted)");
        }

        SendControlInfo();
        var controls = _backend.Controls;
        long interval = FrameIntervalUs(controls);
        long exposure = FrameTiming.ExposureUs(controls);
        lock (_channel.WriteLock)
        {
            SendStreamInfo(result.Accepted ? _announcer.Complete(_backend.Geometry, interval, exposure) : _announcer.Resume(interval, exposure));
        }
    }

    // -- changes inside a stream ---------------------------------------------------

    private void Apply(string id, double value)
    {
        try
        {
            var result = _backend.SetControl(id, value);
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

    private void AnnounceIntervalChange()
    {
        var controls = _backend.Controls;
        lock (_channel.WriteLock)
        {
            var info = _announcer.IntervalChanged(FrameIntervalUs(controls), FrameTiming.ExposureUs(controls));
            if (info != null)
            {
                SendStreamInfo(info);
            }
        }
    }

    private long FrameIntervalUs(IReadOnlyList<ControlDescriptor> controls) => FrameTiming.FrameIntervalUs(controls, _backend.DefaultFps);

    private void SendStreamInfo(StreamInfoMessage info) => _channel.Send(MessageType.StreamInfo, ProtocolJson.Serialize(info));
}
