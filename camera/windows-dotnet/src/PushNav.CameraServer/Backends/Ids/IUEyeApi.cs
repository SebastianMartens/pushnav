// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>One entry of the uEye camera list (<c>is_GetCameraList</c>).</summary>
/// <param name="CameraId">User-assignable camera id.</param>
/// <param name="DeviceId">Device id the driver assigned; what <c>is_InitCamera</c> opens by.</param>
/// <param name="SensorId">Sensor type id.</param>
/// <param name="InUse">Another process has the camera open.</param>
/// <param name="Serial">Serial number.</param>
/// <param name="Model">Model name, e.g. <c>UI306xCP-M</c>.</param>
internal sealed record UEyeCameraEntry(int CameraId, int DeviceId, int SensorId, bool InUse, string Serial, string Model);

/// <summary>Sensor facts from <c>is_GetSensorInfo</c>.</summary>
/// <param name="Name">Sensor name, e.g. <c>UI306xCP-M</c>.</param>
/// <param name="IsMonochrome">The sensor has no color filter.</param>
/// <param name="MaxWidth">Full-frame width in pixels.</param>
/// <param name="MaxHeight">Full-frame height in pixels.</param>
/// <param name="HasMasterGain">The camera has a master gain.</param>
/// <param name="GlobalShutter">The sensor is a global shutter sensor.</param>
/// <param name="PixelSizeNanometers">Pixel pitch in nm (the API reports it in 10 nm units).</param>
internal sealed record UEyeSensorInfo(string Name, bool IsMonochrome, int MaxWidth, int MaxHeight, bool HasMasterGain, bool GlobalShutter, int PixelSizeNanometers);

/// <summary>
/// The part of the uEye API the IDS backend uses, as plain managed calls returning the uEye status code
/// (<see cref="UEyeConst.Success"/> = 0). This is the seam that lets the policy code (pixel clock, exposure priority,
/// long exposure, timeouts) run in xUnit against <c>FakeUEyeApi</c>; <see cref="UEyeApi"/> is the real thing.
/// Image buffers are passed as <see cref="nint"/> addresses.
/// </summary>
internal interface IUEyeApi
{
    int GetCameraList(out IReadOnlyList<UEyeCameraEntry> cameras);

    int InitCamera(int deviceId, out int camera);

    int ExitCamera(int camera);

    /// <summary>The last error code and text of <paramref name="camera"/> (diagnostics only; never throws).</summary>
    string DescribeLastError(int camera);

    int GetSensorInfo(int camera, out UEyeSensorInfo info);

    int SetColorModeMono8(int camera);

    int SetDisplayModeDib(int camera);

    /// <summary>Sets one of the <c>IS_SET_ENABLE_AUTO_*</c> switches (1 = on, 0 = off).</summary>
    int SetAutoParameter(int camera, int parameter, double value);

    /// <summary>
    /// <c>IS_GET_SUPPORTED_BINNING</c>: a bit mask of the binning modes the sensor offers (see <see cref="IdsBinning"/>); not a status.
    /// A negative status means the camera could not say, which counts as "no binning".
    /// </summary>
    int GetSupportedBinning(int camera, out int modes);

    /// <summary>
    /// Sets binning with a <c>IS_BINNING_*</c> mode (<see cref="IdsBinning.ModeFor"/>, 0 for 1x1); the image size follows (read it with
    /// <see cref="GetImageSize"/>). A camera without binning answers NOT_SUPPORTED.
    /// </summary>
    int SetBinning(int camera, int mode);

    /// <summary>Size of the current image area of interest.</summary>
    int GetImageSize(int camera, out int width, out int height);

    // -- timing ----------------------------------------------------------------------------------------

    int GetPixelClockList(int camera, out IReadOnlyList<uint> megahertz);

    int GetPixelClock(int camera, out uint megahertz);

    int SetPixelClock(int camera, uint megahertz);

    /// <summary>Sets the frame rate; <paramref name="actual"/> is what the camera can do (it rounds to its line time).</summary>
    int SetFrameRate(int camera, double fps, out double actual);

    /// <summary>Frame time range in seconds at the current pixel clock.</summary>
    int GetFrameTimeRange(int camera, out double min, out double max, out double increment);

    // -- exposure (milliseconds) ---------------------------------------------------------------------

    int GetExposureRange(int camera, out double minMs, out double maxMs, out double incrementMs);

    int GetLongExposureRange(int camera, out double minMs, out double maxMs, out double incrementMs);

    bool SupportsLongExposure(int camera);

    int GetLongExposureEnabled(int camera, out bool enabled);

    int SetLongExposureEnabled(int camera, bool enabled);

    int GetExposure(int camera, out double ms);

    /// <summary>Sets the exposure; the camera clamps to its current range and reports the value it applied in <paramref name="ms"/>.</summary>
    int SetExposure(int camera, ref double ms);

    // -- gain -------------------------------------------------------------------------------------------

    /// <summary>Master gain, scaled 0..100.</summary>
    int SetMasterGain(int camera, int gain);

    int GetMasterGain(int camera, out int gain);

    // -- memory and acquisition ----------------------------------------------------------------------

    int AllocImageMemory(int camera, int width, int height, out nint memory, out int memoryId);

    int FreeImageMemory(int camera, nint memory, int memoryId);

    /// <summary>Row pitch in bytes of an allocated buffer.</summary>
    int GetImageMemoryPitch(int camera, nint memory, int memoryId, out int pitch);

    int AddToSequence(int camera, nint memory, int memoryId);

    int ClearSequence(int camera);

    int InitImageQueue(int camera);

    int ExitImageQueue(int camera);

    /// <summary>Waits for the next image; returns <see cref="UEyeConst.TimedOut"/> when none arrived. The buffer stays locked until <see cref="UnlockBuffer"/>.</summary>
    int WaitForNextImage(int camera, int timeoutMilliseconds, out nint memory, out int memoryId);

    int UnlockBuffer(int camera, nint memory);

    int StartCapture(int camera);

    /// <summary>Stops the live capture (forced, so it also works when the camera is gone).</summary>
    int StopCapture(int camera);

    // -- events (device removal) -----------------------------------------------------------------------

    /// <summary>Creates and enables one event (<c>IS_SET_EVENT_*</c> id) on <paramref name="camera"/>.</summary>
    int InitEvent(int camera, uint eventId);

    /// <summary>Waits for any of the events; <paramref name="signaled"/> is the id that fired. Returns <see cref="UEyeConst.TimedOut"/> on timeout.</summary>
    int WaitEvents(int camera, IReadOnlyList<uint> events, int timeoutMilliseconds, out uint signaled);

    /// <summary>Signals an event ourselves (wakes a <see cref="WaitEvents"/> at shutdown).</summary>
    int SetEvent(int camera, uint eventId);

    int ExitEvent(int camera, uint eventId);
}
