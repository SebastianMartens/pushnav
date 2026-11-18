// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using System.Runtime.CompilerServices;

// Every P/Invoke in this exe passes blittable data only (spec 3.2: LibraryImport for the uEye API, CsWin32 for Windows).
// With runtime marshalling off, a declaration that would need it fails at build time instead of at the first call.
[assembly: DisableRuntimeMarshalling]
