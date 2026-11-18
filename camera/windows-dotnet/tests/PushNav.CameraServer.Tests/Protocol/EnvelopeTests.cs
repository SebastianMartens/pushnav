// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Protocol;

namespace PushNav.CameraServer.Tests.Protocol;

public class EnvelopeTests
{
    [Fact]
    public void HeaderIsTypeThenLengthLittleEndian()
    {
        var header = new byte[8];

        Envelope.WriteHeader(header, 0x11, 0x0001_0203);

        Assert.Equal(new byte[] { 0x11, 0, 0, 0, 0x03, 0x02, 0x01, 0x00 }, header);
    }

    [Theory]
    [InlineData(MessageType.Hello, new byte[0])]
    [InlineData(MessageType.Frame, new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 })]
    [InlineData(MessageType.SetControl, new byte[] { 123, 125 })]
    [InlineData(0xDEAD_BEEFu, new byte[] { 1 })]
    public void WriteThenReadRoundTrips(uint type, byte[] payload)
    {
        using var ms = new MemoryStream();

        Envelope.Write(ms, type, payload);
        ms.Position = 0;
        var message = Envelope.Read(ms);

        Assert.Equal(type, message.Type);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(ms.Length, ms.Position); // exactly one message was consumed
    }

    [Fact]
    public void LargePayloadsRoundTrip()
    {
        var payload = new byte[300_000];
        new Random(3).NextBytes(payload);
        using var ms = new MemoryStream();

        Envelope.Write(ms, MessageType.Frame, payload);
        ms.Position = 0;

        Assert.Equal(payload, Envelope.Read(ms).Payload);
        Assert.Equal(8 + payload.Length, ms.Length);
    }

    [Fact]
    public void ReadsBackToBackMessages()
    {
        using var ms = new MemoryStream();
        Envelope.Write(ms, MessageType.ControlInfo, [1, 2]);
        Envelope.Write(ms, MessageType.Frame, [3]);
        ms.Position = 0;

        Assert.Equal(MessageType.ControlInfo, Envelope.Read(ms).Type);
        Assert.Equal(MessageType.Frame, Envelope.Read(ms).Type);
    }

    [Fact]
    public void SixteenMebibytesIsTheLargestAcceptedPayload()
    {
        var header = new byte[8];
        Envelope.WriteHeader(header, MessageType.Frame, 16 * 1024 * 1024);

        var (type, length) = Envelope.ParseHeader(header);

        Assert.Equal(MessageType.Frame, type);
        Assert.Equal(16 * 1024 * 1024, length);
    }

    [Theory]
    [InlineData(16 * 1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public void OversizeMessageIsRejectedFromTheHeaderAlone(int announced)
    {
        // Only a header on the wire: the reader must refuse before trying to read or allocate the payload.
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), MessageType.Frame);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), announced);
        using var ms = new MemoryStream(bytes);

        Assert.Throws<ProtocolException>(() => Envelope.Read(ms));
    }

    [Fact]
    public void LengthAboveInt32RangeIsRejected()
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 0xFFFF_FFFFu);

        Assert.Throws<ProtocolException>(() => Envelope.ParseHeader(bytes));
    }

    [Fact]
    public void WritingAnOversizePayloadIsRejected()
    {
        Assert.Throws<ProtocolException>(() => Envelope.WriteHeader(new byte[8], MessageType.Frame, 16 * 1024 * 1024 + 1));
        Assert.Throws<ProtocolException>(() => Envelope.WriteHeader(new byte[8], MessageType.Frame, -1));
    }

    [Fact]
    public void EmptyStreamIsADisconnect()
    {
        using var ms = new MemoryStream();

        Assert.Throws<DisconnectException>(() => Envelope.Read(ms));
    }

    [Fact]
    public void ClosingMidHeaderIsADisconnect()
    {
        using var ms = new MemoryStream([1, 0, 0, 0, 5]);

        Assert.Throws<DisconnectException>(() => Envelope.Read(ms));
    }

    [Fact]
    public void ClosingMidPayloadIsADisconnect()
    {
        using var ms = new MemoryStream([1, 0, 0, 0, 10, 0, 0, 0, 1, 2, 3]);

        Assert.Throws<DisconnectException>(() => Envelope.Read(ms));
    }

    [Fact]
    public void ReadingFromADisposedStreamIsADisconnect()
    {
        var ms = new MemoryStream();
        ms.Dispose();

        Assert.Throws<DisconnectException>(() => Envelope.Read(ms));
    }

    [Fact]
    public void TypeNamesCoverTheV1Messages()
    {
        Assert.Equal("HELLO", MessageType.Name(MessageType.Hello));
        Assert.Equal("FRAME", MessageType.Name(MessageType.Frame));
        Assert.Equal("CONTROL_INFO", MessageType.Name(MessageType.ControlInfo));
        Assert.Equal("ERROR", MessageType.Name(MessageType.Error));
        Assert.Equal("SET_CONTROL", MessageType.Name(MessageType.SetControl));
        Assert.Equal("GET_CONTROLS", MessageType.Name(MessageType.GetControls));
        Assert.Equal("UNKNOWN(0x7f)", MessageType.Name(0x7F));
    }

    [Fact]
    public void WireIdsMatchTheSpec()
    {
        Assert.Equal(0x00u, MessageType.Hello);
        Assert.Equal(0x01u, MessageType.Frame);
        Assert.Equal(0x02u, MessageType.ControlInfo);
        Assert.Equal(0x03u, MessageType.Error);
        Assert.Equal(0x11u, MessageType.SetControl);
        Assert.Equal(0x12u, MessageType.GetControls);
    }
}
