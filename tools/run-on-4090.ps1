<#
.SYNOPSIS
    "Run on 4090" profile: split generation across both cards while gaming on the 5090.

.DESCRIPTION
    Dedicates the RTX 4090 to LTX-2.3 BF16 video, and moves the prompt-enhancer LLM onto
    the RTX 5090 to sit beside the game. Rationale: a 22B BF16 model wants the whole 24 GB
    of the 4090, while the game rarely uses more than ~16 GB of the 5090's 32 GB - so the
    ~15 GB prompt LLM fits in the 5090's headroom. Because LTX and the LLM are then on
    DIFFERENT cards, neither has to yield VRAM to the other.

    It (re)starts the KeithVision app and the KeithUI studio with the KEITHVISION_PROFILE=4090
    config layer (appsettings.4090.json -> Ltx on the 4090, LocalLlm on the 5090), pins the
    LTX server to the 4090, and moves the prompt server to the 5090.

    Trade-offs in this profile:
      * NVFP4 (the fast ComfyUI model) is UNAVAILABLE - it has no FP4 path on the 4090,
        and the 5090 is busy gaming. Use the BF16 model (the default) for generation.
      * BF16 on a 24 GB card leans on CPU/RAM offload, so generation is slower than NVFP4
        on an idle 5090. Keep the audio server off the 4090 (it defaults there) for headroom.
      * Prompt LLM + game share the 5090: if a game spikes past ~17 GB you risk an OOM on
        one of them. The Enhance node's model selector can pick a smaller LLM if needed.

    Run this INSTEAD of start-keithvision.ps1 / start-keithui.ps1 when you want to game on
    the 5090. To go back to all-5090 generation, relaunch those two scripts (or reboot) -
    they start without the profile.

.PARAMETER GpuName
    GPU to pin LTX (BF16 video) to. Defaults to "RTX 4090".

.PARAMETER PromptGpuName
    GPU to pin the prompt-enhancer LLM to. Defaults to "RTX 5090" (beside the game).

.PARAMETER LtxPort
    Port the LTX server listens on (must match Ltx:BaseUrl). Defaults to 8765.

.PARAMETER PromptPort
    Port the prompt server listens on (must match LocalLlm:BaseUrl). Defaults to 8771.
#>
[CmdletBinding()]
param(
    [string]$GpuName       = "RTX 4090",
    [string]$PromptGpuName = "RTX 5090",
    [int]$LtxPort          = 8765,
    [int]$PromptPort       = 8771
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

# 2. Move the prompt-enhancer LLM onto the 5090 (beside the game). It defaults to the
#    4090 and may already be resident there, so stop it first to free the 4090 for LTX,
#    then relaunch it pinned to the 5090.
Write-Host "Moving prompt-enhancer LLM to the $PromptGpuName ..." -ForegroundColor Cyan
& (Join-Path $Tools "stop-prompt-server.ps1") -Port $PromptPort
for ($i = 0; $i -lt 20 -and (Get-NetTCPConnection -State Listen -LocalPort $PromptPort -ErrorAction SilentlyContinue); $i++) {
    Start-Sleep -Milliseconds 500
}
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $Tools "run-prompt-server.ps1"), "-Port", "$PromptPort", "-GpuName", $PromptGpuName
)

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
Write-Host "4090 profile active: LTX BF16 on the $GpuName; prompt LLM on the $PromptGpuName beside the game." -ForegroundColor Cyan
Write-Host "Reminder: NVFP4 (fast) is unavailable here, and BF16 is slower on 24 GB (CPU/RAM offload)." -ForegroundColor Yellow
