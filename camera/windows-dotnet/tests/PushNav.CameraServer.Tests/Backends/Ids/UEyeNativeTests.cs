// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Buffers.Binary;
using System.Text;
using PushNav.CameraServer.Backends.Ids;

namespace PushNav.CameraServer.Tests.Backends.Ids;

public class UEyeNativeTests
{
    [Fact]
    public void ALibraryThatIsNotInstalledIsReportedNotThrown()
    {
        // The situation on a machine without the IDS Software Suite (CI): no DllNotFoundException, just "not available".
        Assert.False(UEyeNative.TryLoad("definitely_not_the_ueye_api_dll.dll"));
    }
}

public class UEyeCameraListTests
{
    private static byte[] Build(params (int CameraId, int DeviceId, int SensorId, int InUse, string Serial, string Model, string Full)[] entries)
    {
        var buffer = new byte[UEyeCameraList.BufferSize(entries.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            int o = UEyeCameraList.HeaderSize + (i * UEyeCameraList.EntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o), e.CameraId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o + 4), e.DeviceId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o + 8), e.SensorId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o + 12), e.InUse);
            Encoding.ASCII.GetBytes(e.Serial).CopyTo(buffer, o + 16);
            Encoding.ASCII.GetBytes(e.Model).CopyTo(buffer, o + 32);
            Encoding.ASCII.GetBytes(e.Full).CopyTo(buffer, o + 60);
        }

        return buffer;
    }

    [Fact]
    public void EntriesAreReadFromTheDocumentedOffsets()
    {
        var buffer = Build(
            (1, 5, 538, 0, "4102825084", "UI306xCP-M", "UI-306xCP-M"),
            (2, 6, 100, 1, "41000", "UI1220", "UI-1220LE"));

        var cameras = UEyeCameraList.Parse(buffer);

        Assert.Equal(2, cameras.Count);
        Assert.Equal(new UEyeCameraEntry(1, 5, 538, false, "4102825084", "UI306xCP-M"), cameras[0]);
        Assert.Equal(new UEyeCameraEntry(2, 6, 100, true, "41000", "UI1220"), cameras[1]);
    }

    [Fact]
    public void TheFullModelNameIsUsedWhenTheShortOneIsEmpty()
    {
        var cameras = UEyeCameraList.Parse(Build((1, 1, 1, 0, "1", "", "UI-5240CP-M-GL")));

        Assert.Equal("UI-5240CP-M-GL", cameras[0].Model);
    }

    [Fact]
    public void ACountLargerThanTheBufferIsLimitedToWhatFits()
    {
        var buffer = Build((1, 1, 1, 0, "1", "A", "A"));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 9);

        Assert.Single(UEyeCameraList.Parse(buffer));
    }

    [Fact]
    public void AnEmptyOrTruncatedBufferIsNoCameras()
    {
        Assert.Empty(UEyeCameraList.Parse([]));
        Assert.Empty(UEyeCameraList.Parse(new byte[3]));
        Assert.Empty(UEyeCameraList.Parse(new byte[UEyeCameraList.HeaderSize]));
    }

    [Fact]
    public void TheBufferSizeCoversTheHeaderAndEveryEntry()
    {
        Assert.Equal(4 + 112, UEyeCameraList.BufferSize(1));
        Assert.Equal(4 + (3 * 112), UEyeCameraList.BufferSize(3));
    }
}

public class IdsQuirksTests
{
    [Theory]
    [InlineData("UI306xCP-M", 0x1409, 0x3060)]
    [InlineData("UI-306xCP-M", 0x1409, 0x3060)]
    [InlineData("ui306xcp-c", 0x1409, 0x3060)]
    public void TheUI306xHasItsUsbIds(string model, int vid, int pid)
    {
        var ids = IdsQuirks.UsbIdsFor(model);

        Assert.Equal(vid, ids.Vid);
        Assert.Equal(pid, ids.Pid);
    }

    [Fact]
    public void OtherModelsWorkWithGenericDefaultsAndNoUsbIds()
    {
        var ids = IdsQuirks.UsbIdsFor("UI1220LE");

        Assert.Null(ids.Vid);
        Assert.Null(ids.Pid);
    }
}
