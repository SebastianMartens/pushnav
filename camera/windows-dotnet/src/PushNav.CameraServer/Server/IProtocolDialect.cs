// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Backends;

namespace PushNav.CameraServer.Server;

/// <summary>What a dialect needs from the session: a way to write messages, and the lock that orders them with frames.</summary>
internal interface ISessionChannel
{
    /// <summary>Held by the frame writer while it checks and writes a frame; a dialect holds it to change what frames are let through.</summary>
    object WriteLock { get; }

    /// <summary>Writes one message under <see cref="WriteLock"/> (the lock is reentrant). Silent once the session has ended.</summary>
    void Send(uint type, byte[] payload);
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
    /// SET_CONTROL with a usable id and value. Always answers with CONTROL_INFO. Throws <see cref="CameraException"/> only when the
    /// stream could not be restarted (the session then ends with an ERROR); a control the camera merely refuses is logged.
    /// </summary>
    void HandleSetControl(string id, double value);

    /// <summary>SET_STREAM_CONFIG (v2 only; a v1 session ignores it like any unknown type).</summary>
    void HandleSetStreamConfig(byte[] payload);

    /// <summary>True if the frame writer may send a frame of this geometry now. Called with <see cref="ISessionChannel.WriteLock"/> held.</summary>
    bool AdmitFrame(StreamGeometry geometry);
}
