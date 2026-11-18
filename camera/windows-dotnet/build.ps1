# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (camera server, C# / .NET).
#
# Publishes the C# camera server as a Native AOT single-file exe:
#   camera\windows-dotnet\publish\camera_server_dotnet.exe
# (specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 10.1)
#
# Needs: the .NET SDK pinned in global.json, and the MSVC linker (Visual Studio or Build Tools with the
# "Desktop development with C++" workload). Any normal PowerShell / cmd window works; a Developer Command
# Prompt is not required.
#
# Usage: .\build.ps1 [-Configuration Release] [-Output <dir>]

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably set in param defaults on Windows PowerShell 5.1, so resolve the folder here.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Output) { $Output = Join-Path $here 'publish' }
Set-Location $here

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet CLI not found on PATH. Install the .NET 10 SDK (see global.json).'
    exit 1
}

# The Native AOT targets locate the MSVC linker through vswhere.exe, which lives in a fixed folder that is
# normally not on PATH outside a Developer Command Prompt.
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $installer = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer'
    if (Test-Path (Join-Path $installer 'vswhere.exe')) {
        $env:PATH = "$env:PATH;$installer"
    }
    else {
        Write-Warning 'vswhere.exe not found; if the publish fails at the link step, install the Visual Studio Build Tools (C++ workload).'
    }
}

$project = Join-Path $here 'src\PushNav.CameraServer'
Write-Host "Publishing $project ($Configuration, win-x64, Native AOT) -> $Output"

# AOT/trim warnings are errors (TreatWarningsAsErrors in Directory.Build.props), so a clean exit means zero warnings.
dotnet publish $project -c $Configuration -r win-x64 -p:PublishAot=true -o $Output
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit code $LASTEXITCODE)"
    exit $LASTEXITCODE
}

$exe = Join-Path $Output 'camera_server_dotnet.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Publish finished but $exe is missing."
    exit 1
}

$size = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "OK: $exe ($size MB)"
exit 0
