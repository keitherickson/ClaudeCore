# KeithVision startup launcher.
# Started by the "KeithVision Startup" scheduled task at logon. Launches the
# LTX-2.3 generation server and the web app in the background (hidden), each only
# if its port isn't already listening. Note: keep LTX Desktop closed so the 22B
# model fits in VRAM.
$ErrorActionPreference = "SilentlyContinue"

$AppDir    = "C:\ClaudeCore\KeithVision"
$AppExe    = Join-Path $AppDir "KeithVision.exe"
$Url       = "http://127.0.0.1:5080"
$LtxPy     = "C:\Users\keith\AppData\Local\LTXDesktop\python\python.exe"
$LtxLaunch = "C:\ClaudeCore\ClaudeCore\tools\ltx_launch.py"
$LogDir    = "C:\ClaudeCore\logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Resolve the 5090's CUDA index by name (slot-order-proof) for the LTX launch below.
. (Join-Path $PSScriptRoot "gpu-common.ps1")
$LtxGpu = Resolve-GpuIndex -Name "RTX 5090" -Fallback 0

function Port-Listening($port) {
    [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}

# Launch a process FULLY DETACHED from this launcher: its own console, parented
# by the WMI service rather than by us. A Start-Process child stays attached to
# this launcher's (hidden) console, so when that console goes away the child gets
# a CTRL_CLOSE_EVENT and uvicorn shuts itself down cleanly -- which is exactly how
# the LTX server kept dying a few minutes after a successful start (clean exit,
# nothing in stderr). A WMI-created process has no such console tie, so it
# survives this launcher exiting or being torn down. Env vars and stdout/stderr
# redirection are applied through a `cmd /c` wrapper, which stays alive for the
# lifetime of the child (cmd /c waits on it).
function Start-Detached {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$OutLog,
        [string]$ErrLog,
        [hashtable]$EnvVars = @{}
    )
    $line = New-Object System.Text.StringBuilder
    foreach ($k in $EnvVars.Keys) { [void]$line.Append("set `"$k=$($EnvVars[$k])`"&& ") }
    [void]$line.Append("`"$FilePath`"")
    foreach ($a in $Arguments) { [void]$line.Append(" `"$a`"") }
    if ($OutLog) { [void]$line.Append(" 1>`"$OutLog`"") }
    if ($ErrLog) { [void]$line.Append(" 2>`"$ErrLog`"") }
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }  # SW_HIDE
    $res = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "cmd.exe /c " + $line.ToString()
        ProcessStartupInformation = $startup
    }
    return ($res.ReturnValue -eq 0)
}

# 1. LTX-2.3 generation server on :8765 (pinned to the 5090 by name, resolved above).
#    Launched detached (see Start-Detached) and retried until the port actually
#    comes up, so a failed or early-exiting start self-heals instead of leaving
#    the app with no video backend.
if ((Test-Path $LtxPy) -and -not (Port-Listening 8765)) {
    $ltxEnv = @{
        LTX_APP_DATA_DIR     = "C:\Users\keith\AppData\Local\LTXDesktop"
        LTX_PORT             = "8765"
        CUDA_DEVICE_ORDER    = "PCI_BUS_ID"   # stable index across mixed 5090/4090
        CUDA_VISIBLE_DEVICES = "$LtxGpu"      # the 5090, resolved by name
    }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Start-Detached -FilePath $LtxPy -Arguments @("-u", $LtxLaunch) `
            -OutLog (Join-Path $LogDir "ltx-server.out.log") `
            -ErrLog (Join-Path $LogDir "ltx-server.err.log") `
            -EnvVars $ltxEnv | Out-Null
        $up = $false
        for ($i = 0; $i -lt 30; $i++) {        # up to ~60s to bind
            Start-Sleep -Seconds 2
            if (Port-Listening 8765) { $up = $true; break }
        }
        # Confirm it stays bound briefly before declaring success (catches an
        # immediate post-bind exit); otherwise loop and relaunch.
        if ($up) { Start-Sleep -Seconds 5; if (Port-Listening 8765) { break } }
    }
}

# 2. KeithVision web app (ports come from appsettings.json -> Kestrel: 80 + 5080)
if ((Test-Path $AppExe) -and -not (Port-Listening 80) -and -not (Port-Listening 5080)) {
    Start-Process -FilePath $AppExe -WindowStyle Hidden `
        -WorkingDirectory $AppDir `
        -RedirectStandardOutput (Join-Path $LogDir "app.out.log") `
        -RedirectStandardError  (Join-Path $LogDir "app.err.log")
}
