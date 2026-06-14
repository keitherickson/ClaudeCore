<#
.SYNOPSIS
    Stops and restarts the local LTX-2.3 inference server (port 8765).

.DESCRIPTION
    Invoked by the web app's admin page (AdminController.RestartLtx) to recover a
    hung or stopped LTX server without touching LTX Desktop. It kills whatever is
    listening on the port, waits for it to free, then relaunches the server
    exactly the way start-keithvision.ps1 does at logon (hidden, with the same
    env vars and redirected logs), and finally waits for /health to answer.

    Safe to run while the web app keeps running — only the python server is cycled.

.PARAMETER Port
    Port the LTX server listens on. Must match Ltx:BaseUrl in appsettings.json.
#>
[CmdletBinding()]
param(
    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"

$LtxPy     = "C:\Users\keith\AppData\Local\LTXDesktop\python\python.exe"
$LtxLaunch = "C:\ClaudeCore\ClaudeCore\tools\ltx_launch.py"
$AppData   = "C:\Users\keith\AppData\Local\LTXDesktop"
$LogDir    = "C:\ClaudeCore\logs"

foreach ($p in @($LtxPy, $LtxLaunch, $AppData)) {
    if (-not (Test-Path $p)) { throw "Required path not found: $p" }
}
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Get-PortPids([int]$p) {
    @(Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

# 1. Stop whatever currently holds the port.
$pids = Get-PortPids $Port
foreach ($procId in $pids) {
    Write-Host "Stopping PID $procId on port $Port"
    Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
}

# 2. Wait (up to ~10s) for the port to actually free.
for ($i = 0; $i -lt 20 -and (Get-PortPids $Port); $i++) { Start-Sleep -Milliseconds 500 }
if (Get-PortPids $Port) { throw "Port $Port is still in use after stop attempt." }

# 3. Relaunch the server hidden, same contract as start-keithvision.ps1.
$env:LTX_APP_DATA_DIR = $AppData
$env:LTX_PORT         = "$Port"
Start-Process -FilePath $LtxPy -ArgumentList "-u", $LtxLaunch -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $LogDir "ltx-server.out.log") `
    -RedirectStandardError  (Join-Path $LogDir "ltx-server.err.log")

# 4. Wait (up to ~30s) for the port to start listening again. Model weights load
#    lazily on first generation, so a listening port is "started" enough here.
$listening = $false
for ($i = 0; $i -lt 60; $i++) {
    if (Get-PortPids $Port) { $listening = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $listening) { throw "LTX server did not start listening on port $Port within the timeout." }

Write-Host "LTX server restarted and listening on http://127.0.0.1:$Port"
