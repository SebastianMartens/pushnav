@echo off
rem SPDX-License-Identifier: GPL-3.0-or-later
rem This file is part of PushNav. Fork addition (SebastianMartens/pushnav): Windows dev launcher.
rem
rem Rebuilds what is out of date (web UI, C# and C camera servers), then starts PushNav from the checkout.
rem Thin wrapper around run-windows.ps1; see there for details.
rem Usage: run-windows.bat [-Check] [-Rebuild] [-NoRun] [-Fake] [-Server c^|dotnet] [extra evf.main args]

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-windows.ps1" %*
exit /b %ERRORLEVEL%
