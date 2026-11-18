// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace PushNav.CameraServer.Imaging;

/// <summary>
/// COM bootstrap for the WIC code. Built-in COM interop is not available under Native AOT,
/// so every COM call goes through the CsWin32 struct/vtable wrappers; this only makes sure the
/// calling thread has joined the multithreaded apartment.
/// </summary>
internal static class ComRuntime
{
    private static readonly HRESULT RpcEChangedMode = new(unchecked((int)0x80010106));

    [ThreadStatic]
    private static bool s_initialized;

    /// <summary>Joins the MTA on the current thread (idempotent; tolerates an already-initialised apartment).</summary>
    public static void EnsureInitialized()
    {
        if (s_initialized)
        {
            return;
        }

        // S_OK / S_FALSE: initialised. RPC_E_CHANGED_MODE: the thread is already in an STA, which WIC also works in.
        HRESULT hr = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        if (hr.Failed && hr != RpcEChangedMode)
        {
            hr.ThrowOnFailure();
        }

        s_initialized = true;
    }
}
