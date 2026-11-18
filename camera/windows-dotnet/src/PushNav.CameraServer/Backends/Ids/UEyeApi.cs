// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Runtime.InteropServices;
using System.Text;

namespace PushNav.CameraServer.Backends.Ids;

/// <summary><see cref="IUEyeApi"/> on top of <see cref="UEyeNative"/> (the real <c>ueye_api_64.dll</c>).</summary>
internal sealed unsafe class UEyeApi : IUEyeApi
{
    private UEyeApi()
    {
    }

    /// <summary>The API, or null when the IDS Software Suite is not installed (the backend is then simply unavailable).</summary>
    public static UEyeApi? TryCreate() => UEyeNative.TryLoad() ? new UEyeApi() : null;

    /// <summary>Driver DLL version as <c>major.minor.build</c> for the log (e.g. 4.97.4111), or null if unreadable.</summary>
    public static string? DescribeLibraryVersion()
    {
        try
        {
            // High byte major, next byte minor, low 16 bits build (documented by the installed API's version resource).
            int v = UEyeNative.GetDllVersion();
            return $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{v & 0xFFFF}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public int GetCameraList(out IReadOnlyList<UEyeCameraEntry> cameras)
    {
        cameras = [];
        int count = 0;
        int rc = UEyeNative.GetNumberOfCameras(&count);
        if (rc != UEyeConst.Success || count <= 0)
        {
            return rc;
        }

        var buffer = new byte[UEyeCameraList.BufferSize(count)];
        fixed (byte* p = buffer)
        {
            *(int*)p = count;
            rc = UEyeNative.GetCameraList(p);
        }

        if (rc == UEyeConst.Success)
        {
            cameras = UEyeCameraList.Parse(buffer);
        }

        return rc;
    }

    public int InitCamera(int deviceId, out int camera)
    {
        int handle = deviceId | UEyeConst.UseDeviceId;
        int rc = UEyeNative.InitCamera(&handle, 0);
        camera = rc == UEyeConst.Success ? handle : 0;
        return rc;
    }

    public int ExitCamera(int camera) => UEyeNative.ExitCamera(camera);

    public string DescribeLastError(int camera)
    {
        int code = 0;
        byte* text = null;
        if (UEyeNative.GetError(camera, &code, &text) != UEyeConst.Success)
        {
            return "no error information";
        }

        string detail = text == null ? string.Empty : Marshal.PtrToStringAnsi((nint)text) ?? string.Empty;
        return detail.Length == 0 ? UEyeConst.StatusText(code) : $"{UEyeConst.StatusText(code)} ({detail})";
    }

    public int GetSensorInfo(int camera, out UEyeSensorInfo info)
    {
        // SENSORINFO (80 bytes): u16 id @0, name[32] @2, color mode @34, max width @36, max height @40, master gain @44,
        // R/G/B gain @48/52/56, global shutter @60, pixel size (10 nm) @64.
        var raw = new byte[80];
        int rc;
        fixed (byte* p = raw)
        {
            rc = UEyeNative.GetSensorInfo(camera, p);
        }

        if (rc != UEyeConst.Success)
        {
            info = new UEyeSensorInfo(string.Empty, false, 0, 0, false, false, 0);
            return rc;
        }

        int nameEnd = Array.IndexOf(raw, (byte)0, 2, 32);
        string name = Encoding.ASCII.GetString(raw, 2, (nameEnd < 0 ? 34 : nameEnd) - 2);
        info = new UEyeSensorInfo(
            Name: name,
            IsMonochrome: raw[34] == 1,
            MaxWidth: BitConverter.ToInt32(raw, 36),
            MaxHeight: BitConverter.ToInt32(raw, 40),
            HasMasterGain: BitConverter.ToInt32(raw, 44) != 0,
            GlobalShutter: BitConverter.ToInt32(raw, 60) != 0,
            PixelSizeNanometers: BitConverter.ToUInt16(raw, 64) * 10);
        return rc;
    }

    public int SetColorModeMono8(int camera) => UEyeNative.SetColorMode(camera, UEyeConst.ColorModeMono8);

    public int SetDisplayModeDib(int camera) => UEyeNative.SetDisplayMode(camera, UEyeConst.DisplayModeDib);

    public int SetAutoParameter(int camera, int parameter, double value)
    {
        double v1 = value;
        double v2 = 0;
        return UEyeNative.SetAutoParameter(camera, parameter, &v1, &v2);
    }

    public int DisableBinning(int camera) => UEyeNative.SetBinning(camera, UEyeConst.BinningDisable);

    public int GetImageSize(int camera, out int width, out int height)
    {
        int* size = stackalloc int[2]; // IS_SIZE_2D
        int rc = UEyeNative.Aoi(camera, UEyeConst.AoiImageGetSize, size, 8);
        width = size[0];
        height = size[1];
        return rc;
    }

    public int GetPixelClockList(int camera, out IReadOnlyList<uint> megahertz)
    {
        megahertz = [];
        uint count = 0;
        int rc = UEyeNative.PixelClock(camera, UEyeConst.PixelClockGetNumber, &count, 4);
        if (rc != UEyeConst.Success || count == 0 || count > 64)
        {
            return rc != UEyeConst.Success ? rc : UEyeConst.NoSuccess;
        }

        var list = new uint[count];
        fixed (uint* p = list)
        {
            rc = UEyeNative.PixelClock(camera, UEyeConst.PixelClockGetList, p, count * 4);
        }

        if (rc == UEyeConst.Success)
        {
            Array.Sort(list);
            megahertz = list;
        }

        return rc;
    }

    public int GetPixelClock(int camera, out uint megahertz)
    {
        uint value = 0;
        int rc = UEyeNative.PixelClock(camera, UEyeConst.PixelClockGet, &value, 4);
        megahertz = value;
        return rc;
    }

    public int SetPixelClock(int camera, uint megahertz) => UEyeNative.PixelClock(camera, UEyeConst.PixelClockSet, &megahertz, 4);

    public int SetFrameRate(int camera, double fps, out double actual)
    {
        double value = 0;
        int rc = UEyeNative.SetFrameRate(camera, fps, &value);
        actual = value;
        return rc;
    }

    public int GetFrameTimeRange(int camera, out double min, out double max, out double increment)
    {
        double a = 0, b = 0, c = 0;
        int rc = UEyeNative.GetFrameTimeRange(camera, &a, &b, &c);
        min = a;
        max = b;
        increment = c;
        return rc;
    }

    public int GetExposureRange(int camera, out double minMs, out double maxMs, out double incrementMs) =>
        GetRange(camera, UEyeConst.ExposureGetRangeMin, UEyeConst.ExposureGetRangeMax, UEyeConst.ExposureGetRangeInc, out minMs, out maxMs, out incrementMs);

    public int GetLongExposureRange(int camera, out double minMs, out double maxMs, out double incrementMs) =>
        GetRange(camera, UEyeConst.ExposureGetLongRangeMin, UEyeConst.ExposureGetLongRangeMax, UEyeConst.ExposureGetLongRangeInc, out minMs, out maxMs, out incrementMs);

    private static int GetRange(int camera, uint minCommand, uint maxCommand, uint incrementCommand, out double min, out double max, out double increment)
    {
        double a = 0, b = 0, c = 0;
        int rc = UEyeNative.Exposure(camera, minCommand, &a, 8);
        if (rc == UEyeConst.Success)
        {
            rc = UEyeNative.Exposure(camera, maxCommand, &b, 8);
        }

        if (rc == UEyeConst.Success)
        {
            rc = UEyeNative.Exposure(camera, incrementCommand, &c, 8);
        }

        min = a;
        max = b;
        increment = c;
        return rc;
    }

    public bool SupportsLongExposure(int camera)
    {
        uint caps = 0;
        return UEyeNative.Exposure(camera, UEyeConst.ExposureGetCaps, &caps, 4) == UEyeConst.Success
            && (caps & UEyeConst.ExposureCapLongExposure) != 0;
    }

    public int GetLongExposureEnabled(int camera, out bool enabled)
    {
        uint value = 0;
        int rc = UEyeNative.Exposure(camera, UEyeConst.ExposureGetLongEnable, &value, 4);
        enabled = rc == UEyeConst.Success && value != 0;
        return rc;
    }

    public int SetLongExposureEnabled(int camera, bool enabled)
    {
        uint value = enabled ? 1u : 0u;
        return UEyeNative.Exposure(camera, UEyeConst.ExposureSetLongEnable, &value, 4);
    }

    public int GetExposure(int camera, out double ms)
    {
        double value = 0;
        int rc = UEyeNative.Exposure(camera, UEyeConst.ExposureGet, &value, 8);
        ms = value;
        return rc;
    }

    public int SetExposure(int camera, ref double ms)
    {
        double value = ms;
        int rc = UEyeNative.Exposure(camera, UEyeConst.ExposureSet, &value, 8);
        if (rc == UEyeConst.Success)
        {
            ms = value;
        }

        return rc;
    }

    public int SetMasterGain(int camera, int gain) =>
        UEyeNative.SetHardwareGain(camera, gain, UEyeConst.IgnoreParameter, UEyeConst.IgnoreParameter, UEyeConst.IgnoreParameter);

    public int GetMasterGain(int camera, out int gain)
    {
        // The "get" variant returns the gain itself (not a status); a negative value would be an error code.
        int value = UEyeNative.SetHardwareGain(camera, UEyeConst.GetMasterGain, UEyeConst.IgnoreParameter, UEyeConst.IgnoreParameter, UEyeConst.IgnoreParameter);
        gain = Math.Max(0, value);
        return value >= 0 ? UEyeConst.Success : value;
    }

    public int AllocImageMemory(int camera, int width, int height, out nint memory, out int memoryId)
    {
        byte* mem = null;
        int id = 0;
        int rc = UEyeNative.AllocImageMem(camera, width, height, 8, &mem, &id);
        memory = (nint)mem;
        memoryId = id;
        return rc;
    }

    public int FreeImageMemory(int camera, nint memory, int memoryId) => UEyeNative.FreeImageMem(camera, (byte*)memory, memoryId);

    public int GetImageMemoryPitch(int camera, nint memory, int memoryId, out int pitch)
    {
        int x = 0, y = 0, bpp = 0, p = 0;
        int rc = UEyeNative.InquireImageMem(camera, (byte*)memory, memoryId, &x, &y, &bpp, &p);
        pitch = p;
        return rc;
    }

    public int AddToSequence(int camera, nint memory, int memoryId) => UEyeNative.AddToSequence(camera, (byte*)memory, memoryId);

    public int ClearSequence(int camera) => UEyeNative.ClearSequence(camera);

    public int InitImageQueue(int camera) => UEyeNative.InitImageQueue(camera, 0);

    public int ExitImageQueue(int camera) => UEyeNative.ExitImageQueue(camera);

    public int WaitForNextImage(int camera, int timeoutMilliseconds, out nint memory, out int memoryId)
    {
        byte* mem = null;
        int id = 0;
        int rc = UEyeNative.WaitForNextImage(camera, (uint)Math.Max(0, timeoutMilliseconds), &mem, &id);
        memory = (nint)mem;
        memoryId = id;
        return rc;
    }

    public int UnlockBuffer(int camera, nint memory) => UEyeNative.UnlockSeqBuf(camera, UEyeConst.IgnoreParameter, (byte*)memory);

    public int StartCapture(int camera) => UEyeNative.CaptureVideo(camera, UEyeConst.DontWait);

    public int StopCapture(int camera) => UEyeNative.StopLiveVideo(camera, UEyeConst.ForceVideoStop);

    public int InitEvent(int camera, uint eventId)
    {
        var init = new UEyeInitEvent { Event = eventId, ManualReset = 0, InitialState = 0 };
        int rc = UEyeNative.Event(camera, UEyeConst.EventInit, &init, (uint)sizeof(UEyeInitEvent));
        if (rc != UEyeConst.Success)
        {
            return rc;
        }

        uint id = eventId;
        rc = UEyeNative.Event(camera, UEyeConst.EventEnable, &id, 4);
        if (rc != UEyeConst.Success)
        {
            UEyeNative.Event(camera, UEyeConst.EventExit, &id, 4);
        }

        return rc;
    }

    public int WaitEvents(int camera, IReadOnlyList<uint> events, int timeoutMilliseconds, out uint signaled)
    {
        signaled = 0;
        if (events.Count == 0)
        {
            return UEyeConst.InvalidParameter;
        }

        uint* ids = stackalloc uint[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            ids[i] = events[i];
        }

        var wait = new UEyeWaitEvents
        {
            Events = ids,
            Count = (uint)events.Count,
            WaitAll = 0,
            TimeoutMilliseconds = (uint)Math.Max(0, timeoutMilliseconds),
        };
        int rc = UEyeNative.Event(camera, UEyeConst.EventWait, &wait, (uint)sizeof(UEyeWaitEvents));
        if (rc == UEyeConst.Success)
        {
            signaled = wait.Signaled;
        }

        return rc;
    }

    public int SetEvent(int camera, uint eventId) => UEyeNative.Event(camera, UEyeConst.EventSet, &eventId, 4);

    public int ExitEvent(int camera, uint eventId)
    {
        uint id = eventId;
        int rc = UEyeNative.Event(camera, UEyeConst.EventDisable, &id, 4);
        int rc2 = UEyeNative.Event(camera, UEyeConst.EventExit, &id, 4);
        return rc != UEyeConst.Success ? rc : rc2;
    }
}
