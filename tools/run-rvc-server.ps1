<#
.SYNOPSIS
    Launches the local RVC (voice conversion) API server for ClaudeCore to call.

.DESCRIPTION
    Runs rvc-python's BUILT-IN API server (`python -m rvc_python api`) so ClaudeCore's
    RvcVoiceService can convert a recorded clip to a target voice. Self-hosted (no API key
    / no per-call cost), runs on the local GPU. The Voice page's "AI Voice (RVC)" card calls it.

    SETUP (one-time) — see tools/rvc-requirements.txt:
      py -3.11 -m venv C:\ClaudeCore\rvc-venv
      C:\ClaudeCore\rvc-venv\Scripts\python -m pip install --upgrade pip
      # cu128 torch FIRST (Blackwell/5090 needs it; rvc-python's default cu118 won't run on sm_120):
      C:\ClaudeCore\rvc-venv\Scripts\python -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
      C:\ClaudeCore\rvc-venv\Scripts\python -m pip install rvc-python
    Drop target-voice models into -ModelsDir as <name>\<name>.pth (+ optional <name>.index),
    or as <name>.pth directly. Find community voices on weights.gg / voice-models.com.
    Base models (HuBERT/RMVPE) download automatically on first conversion.

.PARAMETER Port
    Port to listen on. Must match the "LocalRvc:BaseUrl" port in appsettings.json (8773).

.PARAMETER Gpu
    Physical GPU index (CUDA ordinal) to pin to (exported as CUDA_VISIBLE_DEVICES). Fallback for -GpuName.

.PARAMETER GpuName
    GPU model substring (e.g. "RTX 4090") resolved to its CUDA index by name (slot-order-proof).

.PARAMETER ModelsDir
    Directory of RVC target-voice models (passed to rvc_python as --models_dir).
#>
[CmdletBinding()]
param(
    [int]$Port        = 8773,
    [int]$Gpu         = 1,
    [string]$GpuName  = "RTX 4090",
    [string]$ModelsDir = "C:\ClaudeCore\rvc-models"
)

$ErrorActionPreference = "Stop"

$Python = "C:\ClaudeCore\rvc-venv\Scripts\python.exe"
if (-not (Test-Path $Python)) {
    throw "RVC venv python not found at $Python — set it up per tools/rvc-requirements.txt first."
}
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

# Pick the card by NAME first (slot-order-proof), falling back to the -Gpu index.
. (Join-Path $PSScriptRoot "gpu-common.ps1")
if ($GpuName) { $Gpu = Resolve-GpuIndex -Name $GpuName -Fallback $Gpu }

# Safety net: pinning to a missing index hides every CUDA device and inference falls back
# to CPU (painfully slow). If the chosen index isn't present, fall back to GPU 0.
try {
    $gpuCount = (@(& nvidia-smi --query-gpu=index --format=csv,noheader 2>$null)).Count
    if ($gpuCount -gt 0 -and $Gpu -ge $gpuCount) {
        Write-Host "GPU $Gpu not present ($gpuCount GPU(s) detected); falling back to GPU 0" -ForegroundColor Yellow
        $Gpu = 0
    }
} catch { }

$env:CUDA_DEVICE_ORDER    = "PCI_BUS_ID"   # stable index across mixed 5090/4090
$env:CUDA_VISIBLE_DEVICES = "$Gpu"         # pin RVC to this GPU only
$env:HF_HUB_DISABLE_XET   = "1"            # classic downloader (xet stalls on this box)

Write-Host "Starting RVC server on http://127.0.0.1:$Port (GPU $Gpu, models $ModelsDir)  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "Venv: $Python" -ForegroundColor DarkGray

# No -l: bind localhost only. -md points at the voice-models dir.
& $Python -m rvc_python api -p $Port -md $ModelsDir
