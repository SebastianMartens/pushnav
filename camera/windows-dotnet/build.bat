@echo off
rem SPDX-License-Identifier: GPL-3.0-or-later
rem This file is part of PushNav (camera server, C# / .NET).
rem
rem Builds the C# camera server (Native AOT) into camera\windows-dotnet\publish\camera_server_dotnet.exe.
rem Thin wrapper around build.ps1 so it can be called from build_windows.bat and plain cmd windows.
rem Usage: build.bat [-Configuration Release] [-Output <dir>]

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
