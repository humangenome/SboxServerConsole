# Installs SboxServerConsole as a Windows service using sc.exe.
# Run as Administrator. The exe path and config path you pass here are baked
# into the service binPath; reinstall to change them.
#
# Note on session visibility: services run under Session 0 by default and the
# child sbox-server.exe process will not appear in the interactive desktop's
# Task Manager. If you need the server visible while RDP'd in, run
# SboxServerConsole.exe directly from a startup script under your interactive
# user instead of using a service.

param(
    [Parameter(Mandatory=$true)] [string] $ExePath,
    [Parameter(Mandatory=$true)] [string] $ConfigPath,
    [string] $ServiceName = "SboxServerConsole",
    [string] $DisplayName = "S&box Server Console",
    [string] $Description = "Process agent for sbox-server.exe with HTTP/SSE console API.",
    [ValidateSet("auto", "delayed-auto", "demand", "disabled")]
    [string] $Start = "auto",
    [string] $Username = "",
    [string] $Password = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath))    { throw "exe not found: $ExePath" }
if (-not (Test-Path $ConfigPath)) { throw "config not found: $ConfigPath" }

$ExePath    = (Resolve-Path $ExePath).Path
$ConfigPath = (Resolve-Path $ConfigPath).Path

$existing = sc.exe query $ServiceName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "[install-service] service '$ServiceName' already exists; stopping and removing first."
    sc.exe stop   $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

# binPath needs literal quoting because the args contain spaces.
$binPath = "`"$ExePath`" --config-file `"$ConfigPath`""

$args = @(
    "create", $ServiceName,
    "binPath=", $binPath,
    "DisplayName=", $DisplayName,
    "start=", $Start
)
if ($Username) {
    $args += @("obj=", $Username)
    if ($Password) { $args += @("password=", $Password) }
}

& sc.exe @args | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed (exit $LASTEXITCODE)" }

& sc.exe description $ServiceName $Description | Out-Null

# Restart on failure: 1st 5s, 2nd 5s, 3rd 60s, reset counter after 1 day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

Write-Host "[install-service] installed: $ServiceName"
Write-Host "[install-service] binPath  : $binPath"
Write-Host "[install-service] start it : Start-Service $ServiceName"
Write-Host "[install-service] view logs: Get-EventLog -LogName Application -Source $ServiceName -Newest 20"
