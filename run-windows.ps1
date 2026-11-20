# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav. Fork addition (SebastianMartens/pushnav): Windows dev launcher.
#
# Rebuilds what is out of date, then starts PushNav from the checkout (engine + pywebview window).
# The engine itself starts the camera server (C# by default, C as fallback; evf/camera/server_select.py).
#
# What is checked (a part is rebuilt when its output is missing or older than any of its sources):
#   web\node_modules     <- web\package-lock.json                         (npm ci)
#   web\dist             <- web\src, web\public, web\index.html, configs   (npm run build)
#   C# camera server     <- camera\windows-dotnet\src, *.props, global.json (camera\windows-dotnet\build.bat)
#   C camera server      <- camera\windows\camera_server.c                (camera\windows\build.bat + MSVC)
#   Python environment   synced by `uv run` on every start
#
# Usage: run-windows.bat [options] [-- extra arguments for evf.main, e.g. --no-window]
#   -Check     only report what is out of date; exit 0 = up to date, 1 = rebuild needed
#   -Rebuild   rebuild everything, even if it looks up to date
#   -NoRun     build only, don't start the app
#   -Fake      use the C# server's fake camera with tests\samples (no camera needed)
#   -Server c|dotnet   force a camera server (sets PUSHNAV_CAMERA_SERVER for this run)

[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Rebuild,
    [switch]$NoRun,
    [switch]$Fake,
    [ValidateSet('dotnet', 'c')]
    [string]$Server = '',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location $root

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host "    ok: $msg" -ForegroundColor Green }
function Write-Stale([string]$msg) { Write-Host "    stale: $msg" -ForegroundColor Yellow }

# Newest LastWriteTimeUtc among the given files/folders (folders recursively; build output folders skipped).
function Get-NewestSource([string[]]$paths) {
    $newest = [datetime]::MinValue
    $newestFile = $null
    foreach ($p in $paths) {
        $full = Join-Path $root $p
        if (-not (Test-Path $full)) { continue }
        $files = if ((Get-Item $full).PSIsContainer) {
            Get-ChildItem $full -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj|publish|node_modules)\\' }
        } else { Get-Item $full }
        foreach ($f in $files) {
            if ($f.LastWriteTimeUtc -gt $newest) { $newest = $f.LastWriteTimeUtc; $newestFile = $f.FullName }
        }
    }
    return @{ Time = $newest; File = $newestFile }
}

# Returns $null when $output is up to date, else the reason it needs a rebuild.
function Get-StaleReason([string]$output, [string[]]$sources) {
    $out = Join-Path $root $output
    if (-not (Test-Path $out)) { return "$output is missing" }
    if ($Rebuild) { return 'forced (-Rebuild)' }
    $src = Get-NewestSource $sources
    if ($src.Time -gt (Get-Item $out).LastWriteTimeUtc) {
        return "$($src.File.Substring($root.Length + 1)) is newer than $output"
    }
    return $null
}

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# Calls camera\windows\build.bat inside an MSVC environment (vcvars64 found via vswhere), like
# scripts\build_windows.bat Phase 0 does. Returns $false if no C++ tools are installed.
function Build-CServer {
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
        Push-Location camera\windows
        try { Invoke-Checked 'C camera server build' { cmd /c "$root\camera\windows\build.bat" } } finally { Pop-Location }
        return $true
    }
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $vswhere = Join-Path $installer 'vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $vcvars = if ($vs) { Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat' } else { $null }
    if (-not $vcvars -or -not (Test-Path $vcvars)) { return $false }
    # vcvars64 calls vswhere itself; a temp .cmd avoids Windows PowerShell 5.1's quoting of nested quotes.
    $env:PATH = "$env:PATH;$installer"
    $cmdFile = Join-Path $env:TEMP "pushnav-build-c-server-$PID.cmd"
    Set-Content -Path $cmdFile -Encoding ascii -Value @(
        '@echo off'
        "call `"$vcvars`" >nul || exit /b 1"
        "cd /d `"$root\camera\windows`" || exit /b 1"
        "call `"$root\camera\windows\build.bat`""
    )
    try { Invoke-Checked 'C camera server build' { cmd /c $cmdFile } } finally { Remove-Item $cmdFile -ErrorAction SilentlyContinue }
    return $true
}

# ---------------------------------------------------------------------------------------------------
# 1. Tools
# ---------------------------------------------------------------------------------------------------
Write-Step 'Checking tools'
$missing = @()
foreach ($tool in 'uv', 'npm', 'dotnet') {
    if (Get-Command $tool -ErrorAction SilentlyContinue) { Write-Ok $tool } else { $missing += $tool }
}
if ($missing -contains 'uv' -or $missing -contains 'npm') {
    Write-Host "ERROR: missing on PATH: $($missing -join ', ')" -ForegroundColor Red
    exit 1
}
if ($missing -contains 'dotnet') {
    Write-Host '    WARNING: dotnet not found; the C# camera server cannot be built (only the C server is used).' -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------------------
# 2. What is out of date
# ---------------------------------------------------------------------------------------------------
Write-Step 'Checking build outputs'
$parts = @(
    @{ Name = 'web dependencies'; Output = 'web\node_modules\.package-lock.json'
       Sources = @('web\package-lock.json') }
    @{ Name = 'React UI'; Output = 'web\dist\index.html'
       Sources = @('web\src', 'web\public', 'web\index.html', 'web\vite.config.ts', 'web\package.json',
                   'web\package-lock.json', 'web\tsconfig.json', 'web\tsconfig.app.json', 'web\tsconfig.node.json') }
    @{ Name = 'C# camera server'; Output = 'camera\windows-dotnet\publish\camera_server_dotnet.exe'
       Sources = @('camera\windows-dotnet\src', 'camera\windows-dotnet\Directory.Build.props',
                   'camera\windows-dotnet\global.json') }
    @{ Name = 'C camera server'; Output = 'camera\windows\camera_server.exe'
       Sources = @('camera\windows\camera_server.c', 'camera\windows\build.bat') }
)
$stale = @{}
foreach ($p in $parts) {
    $reason = Get-StaleReason $p.Output $p.Sources
    if ($reason) { Write-Stale "$($p.Name): $reason"; $stale[$p.Name] = $true } else { Write-Ok $p.Name }
}
Write-Ok 'Python environment (synced by uv run)'

if ($Check) {
    if ($stale.Count -gt 0) { Write-Host "$($stale.Count) part(s) need a rebuild." -ForegroundColor Yellow; exit 1 }
    Write-Host 'Everything is up to date.' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------------------------------
# 3. Rebuild
# ---------------------------------------------------------------------------------------------------
try {
    if ($stale['web dependencies']) {
        Write-Step 'npm ci (web dependencies)'
        Push-Location web
        try { Invoke-Checked 'npm ci' { npm ci } } finally { Pop-Location }
        $stale['React UI'] = $true
    }
    if ($stale['React UI']) {
        Write-Step 'npm run build (React UI)'
        Push-Location web
        try { Invoke-Checked 'npm run build' { npm run build } } finally { Pop-Location }
    }
    if ($stale['C# camera server']) {
        if ($missing -contains 'dotnet') {
            Write-Host '    skipped: C# camera server (no dotnet)' -ForegroundColor Yellow
        } else {
            Write-Step 'Building C# camera server'
            Invoke-Checked 'C# camera server build' { cmd /c "$root\camera\windows-dotnet\build.bat" }
        }
    }
    if ($stale['C camera server']) {
        Write-Step 'Building C camera server (fallback for non-IDS cameras)'
        if (-not (Build-CServer)) {
            Write-Host '    skipped: no MSVC C++ tools found. Only matters for cameras the C# server does not support.' -ForegroundColor Yellow
        }
    }
} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    exit 1
}

if ($NoRun) { Write-Host 'Build done (-NoRun).' -ForegroundColor Green; exit 0 }

# ---------------------------------------------------------------------------------------------------
# 4. Leftovers from an earlier run
# ---------------------------------------------------------------------------------------------------
# A second engine would fight over the ports; an orphaned camera server keeps the IDS camera open.
Write-Step 'Checking for running instances'
$engines = @(Get-CimInstance Win32_Process -Filter "Name = 'python.exe' OR Name = 'pythonw.exe'" |
    Where-Object { $_.CommandLine -match 'evf\.main' })
if ($engines.Count -gt 0) {
    Write-Host "ERROR: PushNav is already running (PID $($engines.ProcessId -join ', ')). Close it first." -ForegroundColor Red
    exit 1
}
$orphans = @(Get-Process camera_server_dotnet, camera_server -ErrorAction SilentlyContinue)
foreach ($o in $orphans) {
    Write-Host "    stopping orphaned $($o.ProcessName) (PID $($o.Id))" -ForegroundColor Yellow
    Stop-Process -Id $o.Id -Force
}
if ($orphans.Count -eq 0) { Write-Ok 'none' }

# ---------------------------------------------------------------------------------------------------
# 5. Start
# ---------------------------------------------------------------------------------------------------
if ($Server) { $env:PUSHNAV_CAMERA_SERVER = $Server }
if ($Fake) {
    $env:PUSHNAV_CAMERA_SERVER = 'dotnet'
    $env:PUSHNAV_CAMERA_SERVER_ARGS = "--backend fake --fake-images $root\tests\samples"
}
$desc = if ($env:PUSHNAV_CAMERA_SERVER) { $env:PUSHNAV_CAMERA_SERVER } else { 'from config (default dotnet)' }
if ($Fake) { $desc += ', fake camera' }
Write-Step "Starting PushNav (camera server: $desc)"
& uv run python -m evf.main @AppArgs
exit $LASTEXITCODE
