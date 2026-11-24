// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Server;

/// <summary>What a dialect needs from the session: a way to write messages that is ordered with the frames.</summary>
internal interface ISessionChannel
{
    /// <summary>Writes one message. Silent once the session has ended.</summary>
    void Send(uint type, byte[] payload);

    /// <summary>
    /// Runs <paramref name="build"/> and sends what it returns (nothing for null) as one step that no frame can interleave with:
    /// the frame writer checks <see cref="IProtocolDialect.AdmitFrame"/> and writes under the same lock. This is the only way a
    /// dialect changes what frames are let through, so "AdmitFrame agrees with the last STREAM_INFO sent" holds by construction.
    /// Not run at all once the session has ended.
    /// </summary>
    void SendIfAny(uint type, Func<byte[]?> build);
}

/// <summary>
/// What differs between the protocol versions a session can run (<c>SPEC_PROTOCOL_CAMERA.md</c> 11.2): which controls the client sees
/// and how it sets them, and whether the stream is announced with STREAM_INFO. The handshake picks one per session.
/// </summary>
internal interface IProtocolDialect
{
    int Version { get; }

    /// <summary>What follows the handshake: CONTROL_INFO (and STREAM_INFO in v2), before the first frame.</summary>
    void SendInitial();

    /// <summary>GET_CONTROLS.</summary>
    void SendControlInfo();

    /// <summary>
    /// SET_CONTROL with a usable id and value. Always answers with CONTROL_INFO. A control the camera merely refuses is logged and
    /// answered like any other; only a stream restart that fails throws, <see cref="StreamRestartFailedException"/>, and the
    /// session then ends with an ERROR.
    /// </summary>
    void HandleSetControl(string id, double value);

    /// <summary>SET_STREAM_CONFIG (v2 only; a v1 session ignores it like any unknown type).</summary>
    void HandleSetStreamConfig(byte[] payload);

    /// <summary>True if the frame writer may send a frame of this geometry now. The session calls it under the channel's send lock.</summary>
    bool AdmitFrame(StreamGeometry geometry);
}
