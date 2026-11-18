// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>
/// The numeric values of the uEye API we use, under our own names. Values were read from the installed IDS
/// Software Suite (4.97) and confirmed on a UI-306xCP-M; nothing here is copied from IDS source.
/// </summary>
internal static class UEyeConst
{
    // -- status codes (return values) ---------------------------------------------------------------
    public const int Success = 0;
    public const int NoSuccess = -1;
    public const int InvalidHandle = 1;
    public const int IoRequestFailed = 2;
    public const int CantOpenDevice = 3;
    public const int CantCommunicateWithDriver = 17;
    public const int CameraNotConnected = 111;
    public const int InvalidDeviceId = 118;
    public const int AllDevicesBusy = 120;
    public const int TimedOut = 122;
    public const int InvalidParameter = 125;
    public const int NotSupported = 155;

    /// <summary>The camera runs starter firmware and needs a firmware update before it can be opened (IS_STARTER_FW_UPLOAD_NEEDED).</summary>
    public const int StarterFirmwareUploadNeeded = 184;

    public const int DeviceBusy = 205;
    public const int NoSuchDevice = 212;
    public const int DeviceInUse = 213;

    // -- is_InitCamera / color / display ----------------------------------------------------------
    /// <summary>OR-ed into the camera handle passed to <c>is_InitCamera</c> to open by device id.</summary>
    public const int UseDeviceId = 0x8000;

    public const int ColorModeMono8 = 6;
    public const int DisplayModeDib = 1;
    public const int BinningDisable = 0;
    public const int IgnoreParameter = -1;

    // -- is_SetAutoParameter -------------------------------------------------------------------------
    public const int SetEnableAutoGain = 0x8800;
    public const int SetEnableAutoShutter = 0x8802;
    public const int SetEnableAutoFramerate = 0x8806;
    public const int SetEnableAutoSensorGain = 0x8808;
    public const int SetEnableAutoSensorShutter = 0x8810;
    public const int SetEnableAutoSensorGainShutter = 0x8812;
    public const int SetEnableAutoSensorFramerate = 0x8814;

    // -- is_AOI ---------------------------------------------------------------------------------------
    public const uint AoiImageGetSize = 6;

    // -- is_PixelClock --------------------------------------------------------------------------------
    public const uint PixelClockGetNumber = 1;
    public const uint PixelClockGetList = 2;
    public const uint PixelClockGet = 5;
    public const uint PixelClockSet = 6;

    // -- is_Exposure (values are milliseconds as double, flags are uint) ----------------------------
    public const uint ExposureGetCaps = 1;
    public const uint ExposureGetRangeMin = 3;
    public const uint ExposureGetRangeMax = 4;
    public const uint ExposureGetRangeInc = 5;
    public const uint ExposureGet = 7;
    public const uint ExposureSet = 12;
    public const uint ExposureGetLongRangeMin = 13;
    public const uint ExposureGetLongRangeMax = 14;
    public const uint ExposureGetLongRangeInc = 15;
    public const uint ExposureGetLongEnable = 17;
    public const uint ExposureSetLongEnable = 18;

    /// <summary>Capability bit: the camera supports long exposure.</summary>
    public const uint ExposureCapLongExposure = 4;

    // -- is_SetHardwareGain ---------------------------------------------------------------------------
    public const int GetMasterGain = 0x8000;

    // -- is_CaptureVideo / is_StopLiveVideo -----------------------------------------------------------
    public const int DontWait = 0;
    public const int Wait = 1;
    public const int ForceVideoStop = 0x4000;

    // -- is_Event -------------------------------------------------------------------------------------
    public const uint EventInit = 1;
    public const uint EventExit = 2;
    public const uint EventEnable = 3;
    public const uint EventDisable = 4;
    public const uint EventSet = 5;
    public const uint EventWait = 7;

    /// <summary>Event ids (<c>IS_SET_EVENT_*</c>).</summary>
    public const uint EventDeviceUnplugged = 23;

    public const uint EventRemove = 128;

    /// <summary>Start of the range reserved for our own events; used to wake the removal watcher at shutdown.</summary>
    public const uint EventUserDefinedBegin = 10000;

    /// <summary>Short text for the status codes this backend can run into; the number alone for the rest.</summary>
    public static string StatusText(int status) => status switch
    {
        Success => "success",
        NoSuccess => "general error",
        InvalidHandle => "invalid camera handle",
        IoRequestFailed => "I/O request to the driver failed",
        CantOpenDevice => "cannot open the device",
        CantCommunicateWithDriver => "cannot communicate with the driver",
        CameraNotConnected => "camera not connected",
        InvalidDeviceId => "invalid device id",
        AllDevicesBusy => "all devices busy",
        TimedOut => "timed out",
        InvalidParameter => "invalid parameter",
        NotSupported => "not supported",
        StarterFirmwareUploadNeeded => "camera firmware must be updated: open the IDS Camera Manager once (PushNav never uploads firmware)",
        DeviceBusy => "device busy",
        NoSuchDevice => "no such device",
        DeviceInUse => "device in use by another application",
        _ => $"uEye error {status}",
    };
}
