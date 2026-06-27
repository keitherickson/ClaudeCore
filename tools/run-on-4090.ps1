<#
.SYNOPSIS
    "Run on 4090" profile: do generation on the RTX 4090 (+ CPU/RAM) and leave the
    RTX 5090 free for gaming.

.DESCRIPTION
    Pins the LTX-2.3 BF16 video server to the 4090 and (re)starts the KeithVision app
    and the KeithUI studio with the KEITHVISION_PROFILE=4090 config layer
    (appsettings.4090.json -> Ltx:GpuIndex/GpuName = the 4090).

    Because LTX then shares the 4090 with the prompt-enhancer LLM, the app frees the
    LLM's VRAM before each BF16 generation (PromptVramCoordinator) so the 22B model has
    room in 24 GB. The prompt server reloads its model lazily on the next enhance.

    Trade-offs in this profile:
      * NVFP4 (the fast ComfyUI model) is UNAVAILABLE - it has no FP4 path on the 4090.
        Use the BF16 model (the default) for generation.
      * BF16 on a 24 GB card leans on CPU/RAM offload, so generation is slower than on
        the 5090. Quit anything else holding 4090 VRAM (e.g. the audio server) for headroom.

    Run this INSTEAD of start-keithvision.ps1 / start-keithui.ps1 when you want to game
    on the 5090. To go back to the 5090 for generation, just relaunch those two scripts
    (or reboot) - they start without the profile.

.PARAMETER GpuName
    GPU model substring to pin generation to. Defaults to "RTX 4090".

.PARAMETER LtxPort
    Port the LTX server listens on (must match Ltx:BaseUrl). Defaults to 8765.
#>
[CmdletBinding()]
param(
    [string]$GpuName = "RTX 4090",
    [int]$LtxPort    = 8765
)
$ErrorActionPreference = "Stop"

$Tools  = $PSScriptRoot
$LogDir = "C:\ClaudeCore\logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Select the 4090 profile for every app this script (re)starts. Start-Process inherits
# this process's environment, so the relaunched apps load appsettings.4090.json.
$env:KEITHVISION_PROFILE = "4090"

# 1. Move the LTX BF16 server onto the 4090 (kills whatever holds the port, relaunches
#    there). restart-ltx-server.ps1 prefers -GpuName and resolves it slot-order-proof.
Write-Host "Pinning LTX-2.3 (BF16) to the $GpuName ..." -ForegroundColor Cyan
& (Join-Path $Tools "restart-ltx-server.ps1") -Port $LtxPort -GpuName $GpuName

# 2. Ensure the prompt-enhancer LLM is up on the 4090 (it also auto-starts on demand;
#    in this profile it yields its VRAM before each BF16 generation).
if (-not (Get-NetTCPConnection -State Listen -LocalPort 8771 -ErrorAction SilentlyContinue)) {
    Write-Host "Starting prompt-enhancer LLM on the $GpuName ..." -ForegroundColor Cyan
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $Tools "run-prompt-server.ps1"), "-GpuName", $GpuName
    )
}

# 3. Restart the two web apps so they pick up the 4090 profile. Stopping the running
#    instance is required - the profile is read from KEITHVISION_PROFILE at startup.
function Restart-App {
    param(
        [Parameter(Mandatory)][string]$ProcName,
        [Parameter(Mandatory)][string]$ExePath,
        [hashtable]$ExtraEnv,
        [string]$OutLog,
        [string]$ErrLog
    )
    foreach ($p in @(Get-Process -Name $ProcName -ErrorAction SilentlyContinue)) {
        Write-Host ("Stopping {0} (PID {1}) to apply the 4090 profile" -f $ProcName, $p.Id)
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path $ExePath)) {
        Write-Warning "$ProcName not found at $ExePath - skipping (publish it, or start it manually with KEITHVISION_PROFILE=4090)."
        return
    }
    if ($ExtraEnv) {
        foreach ($k in $ExtraEnv.Keys) { Set-Item -Path "Env:$k" -Value $ExtraEnv[$k] }
    }
    $spArgs = @{
        FilePath         = $ExePath
        WindowStyle      = "Hidden"
        WorkingDirectory = (Split-Path $ExePath)
    }
    if ($OutLog) { $spArgs["RedirectStandardOutput"] = $OutLog }
    if ($ErrLog) { $spArgs["RedirectStandardError"]  = $ErrLog }
    Start-Process @spArgs
    Write-Host "Started $ProcName with the 4090 profile." -ForegroundColor Green
}

# KeithVision (127.0.0.1:80 + :5080 from its Kestrel config - no URL env needed).
Restart-App -ProcName "KeithVision" -ExePath "C:\ClaudeCore\KeithVision\KeithVision.exe" `
    -OutLog (Join-Path $LogDir "app.out.log") -ErrLog (Join-Path $LogDir "app.err.log")

# KeithUI studio (binds 127.0.0.2:80 via ASPNETCORE_URLS, same as start-keithui.ps1).
Restart-App -ProcName "KeithUI" -ExePath "C:\ClaudeCore\KeithUI\KeithUI.exe" `
    -ExtraEnv @{ ASPNETCORE_URLS = "http://127.0.0.2:80" } `
    -OutLog (Join-Path $LogDir "keithui.out.log") -ErrLog (Join-Path $LogDir "keithui.err.log")

Write-Host ""
Write-Host "4090 profile active: BF16 video + prompt LLM on the $GpuName; the 5090 is free for gaming." -ForegroundColor Cyan
Write-Host "Reminder: NVFP4 (fast) is unavailable here, and BF16 is slower on 24 GB (CPU/RAM offload)." -ForegroundColor Yellow
