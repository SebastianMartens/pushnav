// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Buffers.Binary;
using System.Text;

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>
/// Reads the buffer <c>is_GetCameraList</c> fills: a 32-bit count, then one 112-byte entry per camera
/// (camera id, device id, sensor id, in-use flag, 16-byte serial, 16-byte model, status, reserved, 32-byte full model name, reserved).
/// </summary>
internal static class UEyeCameraList
{
    public const int HeaderSize = 4;
    public const int EntrySize = 112;

    private const int CameraIdOffset = 0;
    private const int DeviceIdOffset = 4;
    private const int SensorIdOffset = 8;
    private const int InUseOffset = 12;
    private const int SerialOffset = 16;
    private const int SerialLength = 16;
    private const int ModelOffset = 32;
    private const int ModelLength = 16;
    private const int FullModelOffset = 60;
    private const int FullModelLength = 32;

    /// <summary>Size of the buffer to pass to <c>is_GetCameraList</c> for <paramref name="count"/> cameras (the count goes in its first 4 bytes).</summary>
    public static int BufferSize(int count) => HeaderSize + (count * EntrySize);

    public static IReadOnlyList<UEyeCameraEntry> Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            return [];
        }

        long count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        long available = (buffer.Length - HeaderSize) / EntrySize;
        count = Math.Min(count, available);

        var cameras = new List<UEyeCameraEntry>((int)count);
        for (int i = 0; i < count; i++)
        {
            var entry = buffer.Slice(HeaderSize + (i * EntrySize), EntrySize);
            string model = Text(entry.Slice(ModelOffset, ModelLength));
            string full = Text(entry.Slice(FullModelOffset, FullModelLength));
            cameras.Add(new UEyeCameraEntry(
                CameraId: BinaryPrimitives.ReadInt32LittleEndian(entry[CameraIdOffset..]),
                DeviceId: BinaryPrimitives.ReadInt32LittleEndian(entry[DeviceIdOffset..]),
                SensorId: BinaryPrimitives.ReadInt32LittleEndian(entry[SensorIdOffset..]),
                InUse: BinaryPrimitives.ReadInt32LittleEndian(entry[InUseOffset..]) != 0,
                Serial: Text(entry.Slice(SerialOffset, SerialLength)),
                Model: model.Length > 0 ? model : full));
        }

        return cameras;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end >= 0)
        {
            field = field[..end];
        }

        return Encoding.ASCII.GetString(field).Trim();
    }
}
