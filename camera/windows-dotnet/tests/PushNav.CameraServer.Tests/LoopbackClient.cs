// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Net.Sockets;
using System.Text;
using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Tests;

/// <summary>A small camera-protocol client for tests (the C# twin of python/evf/camera/client.py).</summary>
internal sealed class LoopbackClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    private LoopbackClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public HelloMessage? ServerHello { get; private set; }

    public ControlInfoMessage? LastControlInfo { get; private set; }

    public static LoopbackClient Connect(int port, int receiveTimeoutMs = 10_000)
    {
        var tcp = new TcpClient { ReceiveTimeout = receiveTimeoutMs, NoDelay = true };
        tcp.Connect("127.0.0.1", port);
        return new LoopbackClient(tcp);
    }

    /// <summary>The v1 handshake as the Python client performs it: read HELLO, send HELLO, read CONTROL_INFO.</summary>
    public ControlInfoMessage Handshake(int clientVersion = 1)
    {
        var hello = Read();
        Assert.Equal(MessageType.Hello, hello.Type);
        ServerHello = ProtocolJson.ParseHello(hello.Payload);
        Assert.NotNull(ServerHello);

        SendJson(MessageType.Hello, ProtocolJson.SerializeClientHello(new ClientHelloMessage { ProtocolVersion = clientVersion, App = "evf-test", AppVersion = "0" }));

        var info = Read();
        Assert.Equal(MessageType.ControlInfo, info.Type);
        LastControlInfo = ProtocolJson.ParseControlInfo(info.Payload);
        Assert.NotNull(LastControlInfo);
        return LastControlInfo;
    }

    /// <summary>The latest STREAM_INFO seen by <see cref="HandshakeV2"/> or <see cref="ReadUntilStreamInfo"/>.</summary>
    public StreamInfoMessage? LastStreamInfo { get; private set; }

    /// <summary>
    /// The v2 handshake: read HELLO, answer with <c>protocol_version: 2</c>, then CONTROL_INFO and STREAM_INFO must follow, in that order,
    /// before any frame (protocol 11.2).
    /// </summary>
    public (ControlInfoMessage Controls, StreamInfoMessage Stream) HandshakeV2()
    {
        var controls = Handshake(clientVersion: 2);
        var stream = Read();
        Assert.Equal(MessageType.StreamInfo, stream.Type);
        LastStreamInfo = ProtocolJson.ParseStreamInfo(stream.Payload);
        Assert.NotNull(LastStreamInfo);
        return (controls, LastStreamInfo);
    }

    /// <summary>Reads until a STREAM_INFO in <paramref name="state"/>; returns it with the frames and other messages seen before it.</summary>
    public (StreamInfoMessage Info, List<Message> Before) ReadUntilStreamInfo(string state, int maxMessages = 500)
    {
        var before = new List<Message>();
        for (int i = 0; i < maxMessages; i++)
        {
            var m = Read();
            if (m.Type == MessageType.ControlInfo)
            {
                LastControlInfo = ProtocolJson.ParseControlInfo(m.Payload);
            }

            if (m.Type == MessageType.StreamInfo)
            {
                var info = ProtocolJson.ParseStreamInfo(m.Payload) ?? throw new InvalidDataException("malformed STREAM_INFO");
                LastStreamInfo = info;
                if (info.State == state)
                {
                    return (info, before);
                }
            }

            before.Add(m);
        }

        throw new TimeoutException($"No STREAM_INFO {state} within {maxMessages} messages");
    }

    /// <summary>Reads through the <paramref name="count"/>-th CONTROL_INFO (each updates <see cref="LastControlInfo"/>); returns the types of everything read, in order.</summary>
    public List<uint> ReadThroughControlInfos(int count, int maxMessages = 500)
    {
        var types = new List<uint>();
        for (int seen = 0; seen < count; )
        {
            if (types.Count >= maxMessages)
            {
                throw new TimeoutException($"No {count} CONTROL_INFO within {maxMessages} messages");
            }

            var m = Read();
            types.Add(m.Type);
            if (m.Type == MessageType.ControlInfo)
            {
                LastControlInfo = ProtocolJson.ParseControlInfo(m.Payload);
                seen++;
            }
        }

        return types;
    }

    /// <summary>Changes how long a <see cref="Read"/> waits before it gives up (the connection stays usable).</summary>
    public void SetReceiveTimeout(int milliseconds) => _tcp.ReceiveTimeout = milliseconds;

    public Message Read() => Envelope.Read(_stream);

    /// <summary>Reads messages until one of <paramref name="type"/> arrives (CONTROL_INFO on the way updates <see cref="LastControlInfo"/>).</summary>
    public Message ReadUntil(uint type, int maxMessages = 500)
    {
        for (int i = 0; i < maxMessages; i++)
        {
            var m = Read();
            if (m.Type == MessageType.ControlInfo)
            {
                LastControlInfo = ProtocolJson.ParseControlInfo(m.Payload);
            }

            if (m.Type == type)
            {
                return m;
            }
        }

        throw new TimeoutException($"No {MessageType.Name(type)} within {maxMessages} messages");
    }

    public void Send(uint type, ReadOnlySpan<byte> payload) => Envelope.Write(_stream, type, payload);

    public void SendJson(uint type, byte[] json) => Send(type, json);

    public void SendRawText(uint type, string text) => Send(type, Encoding.UTF8.GetBytes(text));

    public void SetControl(string id, double value) =>
        SendJson(MessageType.SetControl, ProtocolJson.SerializeSetControl(new SetControlMessage { Id = id, Value = value }));

    /// <summary>Sends a raw header + payload (to test oversize announcements without sending the payload).</summary>
    public void SendRaw(byte[] bytes)
    {
        _stream.Write(bytes);
        _stream.Flush();
    }

    /// <summary>Drops the connection with a reset instead of a clean close (a client that crashed or was killed).</summary>
    public void Abort()
    {
        _tcp.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);
        Dispose();
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
