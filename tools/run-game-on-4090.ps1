<#
.SYNOPSIS
    "game-on-4090" profile: game on the RTX 4090; generate on the RTX 5090.

.DESCRIPTION
    The inverse of run-on-4090.ps1. You game on the 4090, so generation is kept off it:
      * LTX-2.3 video generates on the 5090 (its 32 GB offloads less than the 4090's 24 GB).
      * The prompt-enhancer LLM runs on CPU / system RAM, taking no GPU VRAM at all.
      * Audio stays on the 4090 (default) but only lazy-starts on demand; this script stops
        any resident audio server so the 4090 starts clean for the game (it reloads when a
        sound is next generated, then stays resident).

    It applies the KEITHVISION_PROFILE=game4090 config layer (appsettings.game4090.json)
    and restarts both web apps under it.

    Trade-offs:
      * The prompt LLM on CPU is slower per enhance (seconds to ~a minute) and uses ~15 GB
        of system RAM for a 7B model. Pick a smaller PROMPT_MODEL if RAM is tight.
      * NVFP4 (fast video) CAN run here since the 5090 is free of the game; switch the model
        in /Admin if you want it instead of BF16.

    Run this INSTEAD of start-keithvision.ps1 / start-keithui.ps1 when you want to game on
    the 4090. To go back, relaunch those scripts (or reboot) - they start without a profile.

.PARAMETER LtxGpuName
    GPU to pin LTX video to. Defaults to "RTX 5090".

.PARAMETER LtxPort / PromptPort / AudioPort
    Server ports (must match the *:BaseUrl settings). Default 8765 / 8771 / 8770.
#>
[CmdletBinding()]
param(
    [string]$LtxGpuName = "RTX 5090",
    [int]$LtxPort       = 8765,
    [int]$PromptPort    = 8771,
    [int]$AudioPort     = 8770
)
$ErrorActionPreference = "Stop"

$Tools  = $PSScriptRoot
$LogDir = "C:\ClaudeCore\logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Select the game4090 profile for every app this script (re)starts.
$env:KEITHVISION_PROFILE = "game4090"

# 1. LTX video -> the 5090 (off the gaming card).
Write-Host "Pinning LTX-2.3 to the $LtxGpuName ..." -ForegroundColor Cyan
& (Join-Path $Tools "restart-ltx-server.ps1") -Port $LtxPort -GpuName $LtxGpuName

# 2. Prompt-enhancer LLM -> CPU (system RAM). Stop any GPU-resident instance, then
#    relaunch it in CPU mode so it takes no GPU VRAM.
Write-Host "Moving prompt-enhancer LLM to CPU (system RAM) ..." -ForegroundColor Cyan
& (Join-Path $Tools "stop-prompt-server.ps1") -Port $PromptPort
for ($i = 0; $i -lt 20 -and (Get-NetTCPConnection -State Listen -LocalPort $PromptPort -ErrorAction SilentlyContinue); $i++) {
    Start-Sleep -Milliseconds 500
}
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $Tools "run-prompt-server.ps1"), "-Port", "$PromptPort", "-Device", "cpu"
)

# 3. Free the 4090 for the game: stop a resident audio server if one is up. Audio stays on
#    the 4090 by default and lazy-starts again the next time a sound is generated.
$audioStop = Join-Path $Tools "stop-audio-server.ps1"
if ((Get-NetTCPConnection -State Listen -LocalPort $AudioPort -ErrorAction SilentlyContinue) -and (Test-Path $audioStop)) {
    Write-Host "Stopping resident audio server to free the 4090 (it lazy-starts on demand) ..." -ForegroundColor Cyan
    & $audioStop -Port $AudioPort
}

# 4. Restart the two web apps so they pick up the game4090 profile. Stopping the running
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
        Write-Host ("Stopping {0} (PID {1}) to apply the game4090 profile" -f $ProcName, $p.Id)
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path $ExePath)) {
        Write-Warning "$ProcName not found at $ExePath - skipping (publish it, or start it manually with KEITHVISION_PROFILE=game4090)."
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
    Write-Host "Started $ProcName with the game4090 profile." -ForegroundColor Green
}

Restart-App -ProcName "KeithVision" -ExePath "C:\ClaudeCore\KeithVision\KeithVision.exe" `
    -OutLog (Join-Path $LogDir "app.out.log") -ErrLog (Join-Path $LogDir "app.err.log")

Restart-App -ProcName "KeithUI" -ExePath "C:\ClaudeCore\KeithUI\KeithUI.exe" `
    -ExtraEnv @{ ASPNETCORE_URLS = "http://127.0.0.2:80" } `
    -OutLog (Join-Path $LogDir "keithui.out.log") -ErrLog (Join-Path $LogDir "keithui.err.log")

Write-Host ""
Write-Host "game4090 profile active: game on the 4090; LTX on the $LtxGpuName, prompt LLM on CPU." -ForegroundColor Cyan
Write-Host "NVFP4 (fast) is available here since the 5090 is free of the game - switch models in /Admin." -ForegroundColor Yellow
