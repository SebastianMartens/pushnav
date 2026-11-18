// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PushNav.CameraServer.Backends.Ids;

/// <summary>
/// P/Invoke declarations for the uEye API (<c>ueye_api_64.dll</c>), written by us against the function names, signatures
/// and constants the installed IDS Software Suite exposes (spec 3.2 / 3.4). Nothing from IDS is bundled or referenced:
/// the DLL belongs to the IDS driver install (System32) and is loaded at runtime only when it is present.
/// </summary>
/// <remarks>
/// Everything here is blittable (pointers and ints), so the source-generated stubs need no runtime marshalling. All
/// functions use the cdecl calling convention. The library is loaded with <see cref="TryLoad"/> first; without it
/// no function in this class may be called (the call would throw <see cref="DllNotFoundException"/>).
/// </remarks>
internal static unsafe partial class UEyeNative
{
    /// <summary>Name the P/Invokes bind to; resolved to the module <see cref="TryLoad"/> loaded.</summary>
    public const string LibraryName = "ueye_api_64";

    /// <summary>File name searched by <see cref="TryLoad()"/>.</summary>
    public const string LibraryFileName = "ueye_api_64.dll";

    private static readonly object LoadGate = new();
    private static nint s_module;
    private static string? s_moduleFileName;
    private static bool s_resolverInstalled;

    /// <summary>True once <see cref="TryLoad()"/> found the library.</summary>
    public static bool IsLoaded => Volatile.Read(ref s_module) != 0;

    /// <summary>
    /// Loads <c>ueye_api_64.dll</c> from System32 (where the IDS driver install puts it), then from the default search
    /// path. Never throws; returns false when the IDS Software Suite is not installed. Idempotent.
    /// </summary>
    public static bool TryLoad() => TryLoad(LibraryFileName);

    /// <summary>Same as <see cref="TryLoad()"/> for a given file name (tests pass a name that does not exist).</summary>
    public static bool TryLoad(string fileName)
    {
        lock (LoadGate)
        {
            if (s_module != 0 && string.Equals(s_moduleFileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var assembly = typeof(UEyeNative).Assembly;
            nint module = 0;
            try
            {
                if (!NativeLibrary.TryLoad(fileName, assembly, DllImportSearchPath.System32, out module))
                {
                    NativeLibrary.TryLoad(fileName, assembly, null, out module);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
            {
                module = 0; // e.g. a 32-bit DLL of the same name; treat as not available
            }

            if (module == 0)
            {
                return false;
            }

            if (!s_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(assembly, Resolve);
                s_resolverInstalled = true;
            }

            s_moduleFileName = fileName;
            Volatile.Write(ref s_module, module);
            return true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == LibraryName ? Volatile.Read(ref s_module) : 0; // 0 = use the default resolution for anything else

    // -- enumeration / lifetime ---------------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "is_GetDLLVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetDllVersion();

    [LibraryImport(LibraryName, EntryPoint = "is_GetNumberOfCameras")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetNumberOfCameras(int* count);

    /// <summary>Buffer: a 4-byte count followed by 112-byte entries (see <see cref="UEyeCameraList"/>).</summary>
    [LibraryImport(LibraryName, EntryPoint = "is_GetCameraList")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetCameraList(byte* list);

    [LibraryImport(LibraryName, EntryPoint = "is_InitCamera")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int InitCamera(int* camera, nint windowHandle);

    [LibraryImport(LibraryName, EntryPoint = "is_ExitCamera")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ExitCamera(int camera);

    [LibraryImport(LibraryName, EntryPoint = "is_GetError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetError(int camera, int* code, byte** text);

    // -- sensor / format ------------------------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "is_GetSensorInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetSensorInfo(int camera, byte* info);

    [LibraryImport(LibraryName, EntryPoint = "is_SetColorMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SetColorMode(int camera, int mode);

    [LibraryImport(LibraryName, EntryPoint = "is_SetDisplayMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SetDisplayMode(int camera, int mode);

    [LibraryImport(LibraryName, EntryPoint = "is_SetBinning")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SetBinning(int camera, int mode);

    [LibraryImport(LibraryName, EntryPoint = "is_AOI")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int Aoi(int camera, uint command, void* parameter, uint size);

    [LibraryImport(LibraryName, EntryPoint = "is_SetAutoParameter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SetAutoParameter(int camera, int parameter, double* value1, double* value2);

    // -- timing and controls ---------------------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "is_PixelClock")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int PixelClock(int camera, uint command, void* parameter, uint size);

    [LibraryImport(LibraryName, EntryPoint = "is_SetFrameRate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SetFrameRate(int camera, double fps, double* actual);

    [LibraryImport(LibraryName, EntryPoint = "is_GetFrameTimeRange")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetFrameTimeRange(int camera, double* min, double* max, double* interval);

    [LibraryImport(LibraryName, EntryPoint = "is_Exposure")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int Exposure(int camera, uint command, void* parameter, uint size);

    /// <summary>With master = <see cref="UEyeConst.GetMasterGain"/> the master gain is returned instead of a status.</summary>
    [LibraryImport(LibraryName, EntryPoint = "is_SetHardwareGain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int SetHardwareGain(int camera, int master, int red, int green, int blue);

    // -- image memory and acquisition -----------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "is_AllocImageMem")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int AllocImageMem(int camera, int width, int height, int bitsPerPixel, byte** memory, int* memoryId);

    [LibraryImport(LibraryName, EntryPoint = "is_FreeImageMem")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FreeImageMem(int camera, byte* memory, int memoryId);

    [LibraryImport(LibraryName, EntryPoint = "is_InquireImageMem")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int InquireImageMem(int camera, byte* memory, int memoryId, int* width, int* height, int* bitsPerPixel, int* pitch);

    [LibraryImport(LibraryName, EntryPoint = "is_AddToSequence")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int AddToSequence(int camera, byte* memory, int memoryId);

    [LibraryImport(LibraryName, EntryPoint = "is_ClearSequence")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ClearSequence(int camera);

    [LibraryImport(LibraryName, EntryPoint = "is_InitImageQueue")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int InitImageQueue(int camera, int mode);

    [LibraryImport(LibraryName, EntryPoint = "is_ExitImageQueue")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ExitImageQueue(int camera);

    [LibraryImport(LibraryName, EntryPoint = "is_WaitForNextImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int WaitForNextImage(int camera, uint timeoutMilliseconds, byte** memory, int* memoryId);

    [LibraryImport(LibraryName, EntryPoint = "is_UnlockSeqBuf")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int UnlockSeqBuf(int camera, int sequenceNumber, byte* memory);

    [LibraryImport(LibraryName, EntryPoint = "is_CaptureVideo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int CaptureVideo(int camera, int wait);

    [LibraryImport(LibraryName, EntryPoint = "is_StopLiveVideo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int StopLiveVideo(int camera, int wait);

    // -- events ------------------------------------------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "is_Event")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int Event(int camera, uint command, void* parameter, uint size);
}

/// <summary>Layout of the <c>is_Event</c> init request (3 x 32 bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UEyeInitEvent
{
    public uint Event;
    public int ManualReset;
    public int InitialState;
}

/// <summary>Layout of the <c>is_Event</c> wait request (x64: pointer, 5 x 32 bit, padded to 32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UEyeWaitEvents
{
    public uint* Events;
    public uint Count;
    public int WaitAll;
    public uint TimeoutMilliseconds;
    public uint Signaled;
    public uint SetCount;
}
