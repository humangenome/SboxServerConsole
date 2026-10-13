# Windows smoke test for the published SboxServerConsole.exe.
#
# The unit tests run on Linux, so the Windows-only paths (ConPTY, the job object,
# HTTP.sys and the service manager) are exercised here instead, against cmd.exe
# as a stand-in server: it reads commands from its console and answers them, which
# is the whole contract the agent relies on. Run as Administrator (CI runners are).
param(
    [Parameter(Mandatory = $true)] [string] $Exe
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("ssc-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$port = 27019
$base = "http://127.0.0.1:$port"
$headers = @{ 'X-RCON-Password' = 'ci-password' }
$failures = 0

function Check($ok, $what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:failures++ }
}
function Get-Health { try { Invoke-RestMethod "$base/health" -TimeoutSec 5 } catch { $null } }
function Post($path, $body = '{}') {
    Invoke-RestMethod "$base$path" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 60
}
function Wait-Until([scriptblock] $cond, [int] $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $cond)
}
function Cmd-Children { @(Get-CimInstance Win32_Process -Filter "Name = 'cmd.exe'" | Where-Object { $_.CommandLine -like '*ssc-smoke-child*' }) }

$config = @{
    'exe'              = "$env:SystemRoot\System32\cmd.exe"
    'game-dir'         = $work
    'child-args'       = '/q /k title ssc-smoke-child'
    'child-port'       = 27015
    'listen-port'      = $port
    'rcon-port'        = 27020
    'rcon-password'    = 'ci-password'
    'shutdown-command' = 'exit'
    'query-poll-sec'   = 0
    'audit-log'        = (Join-Path $work 'audit.jsonl')
    'banlist'          = (Join-Path $work 'bans.json')
}
$configPath = Join-Path $work 'config.json'
$config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8

# ---- console mode ----
$agent = Start-Process -FilePath $Exe -ArgumentList @('--config-file', "`"$configPath`"") -PassThru -WindowStyle Hidden
try {
    Check (Wait-Until { (Get-Health).child_alive -eq $true }) 'agent serves /health with the child running'

    $r = Post '/execute?collect=1&wait_ms=2000' '{"cmd":"echo ci-marker-4711"}'
    Check ([bool]($r.output | Where-Object { $_.stream -eq 'stdout' -and $_.line -match 'ci-marker-4711' })) 'a command reaches the child through ConPTY and its output comes back'
    Check (-not ($r.output | Where-Object { $_.stream -eq 'stdout' -and $_.line.Trim() -eq '>' })) 'no bare prompt lines in the output'

    $st = Invoke-RestMethod "$base/status" -Headers $headers
    Check ($st.server_pid -gt 0 -and $st.server_pid -ne $st.child_pid) "status reports the server process, not the wrapper (child $($st.child_pid), server $($st.server_pid))"

    $null = Post '/server/stop'
    Check (Wait-Until { (Get-Health).child_alive -eq $false }) 'stop stops the child'
    Start-Sleep -Seconds 2
    Check ((Get-Health) -ne $null -and -not $agent.HasExited) 'the agent keeps running after a stop'

    $null = Post '/server/start'
    Check (Wait-Until { (Get-Health).child_alive -eq $true }) 'start brings the child back'

    $before = (Get-Health).child_pid
    $null = Post '/server/restart'
    Check (Wait-Until { $h = Get-Health; $h.child_alive -and $h.child_pid -ne $before }) 'restart replaces the child'

    $bad = $null
    try { Post '/bans' '{"steamid":"STEAM_0:1:1"}' | Out-Null } catch { $bad = [int]$_.Exception.Response.StatusCode }
    Check ($bad -eq 400) 'a ban that could never match is refused'
}
finally {
    if (-not $agent.HasExited) { Stop-Process -Id $agent.Id -Force }
}
Check (Wait-Until { (Cmd-Children).Count -eq 0 } 15) 'killing the agent takes the child with it (job object)'

# ---- service mode ----
$scripts = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts'
& (Join-Path $scripts 'install-service.ps1') -ExePath $Exe -ConfigPath $configPath -ServiceName 'SboxServerConsoleSmoke'
try {
    Start-Service -Name 'SboxServerConsoleSmoke'
    Check ((Get-Service 'SboxServerConsoleSmoke').Status -eq 'Running') 'the service starts and reports Running'
    Check (Wait-Until { (Get-Health).child_alive -eq $true }) 'the service runs the agent and the child'
    Stop-Service -Name 'SboxServerConsoleSmoke'
    Check ((Get-Service 'SboxServerConsoleSmoke').Status -eq 'Stopped') 'the service stops cleanly'
    Check (Wait-Until { (Cmd-Children).Count -eq 0 } 15) 'stopping the service stops the child'
}
finally {
    & (Join-Path $scripts 'uninstall-service.ps1') -ServiceName 'SboxServerConsoleSmoke'
}

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
if ($failures -gt 0) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'windows smoke test passed'
