# Stops and deletes the SboxServerConsole Windows service. Run as Administrator.

param(
    [string] $ServiceName = "SboxServerConsole"
)

$ErrorActionPreference = "Stop"

$existing = sc.exe query $ServiceName 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "[uninstall-service] service '$ServiceName' not installed; nothing to do."
    return
}

sc.exe stop $ServiceName | Out-Null
Start-Sleep -Seconds 2
sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit $LASTEXITCODE)" }

Write-Host "[uninstall-service] removed: $ServiceName"
