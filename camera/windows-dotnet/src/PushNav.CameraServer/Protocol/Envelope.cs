// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Buffers.Binary;

namespace PushNav.CameraServer.Protocol;

/// <summary>Message type ids of camera protocol v1 (SPEC_PROTOCOL_CAMERA.md sections 4-5).</summary>
internal static class MessageType
{
    // camera -> app
    public const uint Hello = 0x00;
    public const uint Frame = 0x01;
    public const uint ControlInfo = 0x02;
    public const uint Error = 0x03;

    // app -> camera
    public const uint SetControl = 0x11;
    public const uint GetControls = 0x12;

    public static string Name(uint type) => type switch
    {
        Hello => "HELLO",
        Frame => "FRAME",
        ControlInfo => "CONTROL_INFO",
        Error => "ERROR",
        SetControl => "SET_CONTROL",
        GetControls => "GET_CONTROLS",
        _ => $"UNKNOWN(0x{type:x2})",
    };
}

/// <summary>A received message.</summary>
internal readonly record struct Message(uint Type, byte[] Payload);

/// <summary>The peer sent something that breaks the protocol (e.g. a message larger than the limit).</summary>
internal sealed class ProtocolException : Exception
{
    public ProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>The peer closed the connection (cleanly or mid-message).</summary>
internal sealed class DisconnectException : Exception
{
    public DisconnectException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Message envelope (spec 3): <c>type</c> u32 LE, <c>length</c> u32 LE, then <c>length</c> payload bytes.
/// </summary>
internal static class Envelope
{
    public const int HeaderSize = 8;

    /// <summary>Largest payload accepted or sent: 16 MiB (spec 2.1 / protocol 11.6).</summary>
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    public static void WriteHeader(Span<byte> destination, uint type, int payloadLength)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException("Header needs 8 bytes.", nameof(destination));
        }

        if ((uint)payloadLength > MaxPayloadLength)
        {
            throw new ProtocolException($"Payload of {payloadLength} bytes exceeds the {MaxPayloadLength} byte limit.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, type);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)payloadLength);
    }

    /// <summary>Parses a header; throws <see cref="ProtocolException"/> when the announced length is over the limit.</summary>
    public static (uint Type, int PayloadLength) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            throw new ArgumentException("Header needs 8 bytes.", nameof(header));
        }

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (length > MaxPayloadLength)
        {
            throw new ProtocolException($"Message 0x{type:x2} announces {length} payload bytes, over the {MaxPayloadLength} byte limit.");
        }

        return (type, (int)length);
    }

    /// <summary>Reads one message; blocks. Throws <see cref="DisconnectException"/> if the stream ends, also mid-message.</summary>
    public static Message Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExactly(stream, header);
        var (type, length) = ParseHeader(header);

        // Allocation sized by a validated length (<= 16 MiB). Client messages are tiny in practice.
        byte[] payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            ReadExactly(stream, payload);
        }

        return new Message(type, payload);
    }

    /// <summary>Writes header and payload (two writes; the caller serialises concurrent writers).</summary>
    public static void Write(Stream stream, uint type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        WriteHeader(header, type, payload.Length);
        if (payload.Length <= 4096)
        {
            // Small control messages go out in a single write.
            Span<byte> combined = stackalloc byte[HeaderSize + 4096];
            header.CopyTo(combined);
            payload.CopyTo(combined[HeaderSize..]);
            stream.Write(combined[..(HeaderSize + payload.Length)]);
        }
        else
        {
            stream.Write(header);
            stream.Write(payload);
        }

        stream.Flush();
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new DisconnectException("Connection closed", ex);
        }
        catch (IOException ex)
        {
            throw new DisconnectException($"Connection failed: {ex.Message}", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new DisconnectException("Connection closed locally", ex);
        }
    }
}
