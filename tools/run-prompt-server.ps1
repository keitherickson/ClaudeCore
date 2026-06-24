<#
.SYNOPSIS
    Launches the local prompt-enhancer LLM server for ClaudeCore / KeithUI to call.

.DESCRIPTION
    Runs tools/prompt_server.py in the dedicated prompt venv. Serves idea -> cinematic
    text-to-video prompt at http://127.0.0.1:<Port>; the Enhance Prompt node (and the
    PromptEnhanceService) talk to it. Self-hosted on the 4090 — no API key / no per-call
    cost. The model downloads from Hugging Face on first run.

    Set up the venv + deps first: a prompt-venv with transformers, torch (CUDA), fastapi,
    uvicorn (see InstallationSteps.md, "Prompt enhancer").

.PARAMETER Port
    Port to listen on. Must match the "LocalLlm:BaseUrl" port in appsettings.json (8771).

.PARAMETER Gpu
    GPU index (CUDA ordinal) to pin to. Used as a fallback when -GpuName can't resolve.

.PARAMETER GpuName
    Preferred way to pick the card: a GPU model substring (e.g. "RTX 4090"). Resolved to
    its CUDA index by name (slot-order-proof) and used in preference to -Gpu, so the LLM
    runs on the 4090 alongside the audio server and leaves the 5090 for video.
#>
[CmdletBinding()]
param(
    [int]$Port       = 8771,
    [int]$Gpu        = 1,
    [string]$GpuName = "RTX 4090"
)

$ErrorActionPreference = "Stop"

$Python = "C:\ClaudeCore\prompt-venv\Scripts\python.exe"
$Server = Join-Path $PSScriptRoot "prompt_server.py"

foreach ($p in @($Python, $Server)) {
    if (-not (Test-Path $p)) {
        throw "Required path not found: $p  (run the prompt-server setup in InstallationSteps.md first)"
    }
}

# Pick the card by NAME first (slot-order-proof), falling back to the -Gpu index.
. (Join-Path $PSScriptRoot "gpu-common.ps1")
if ($GpuName) { $Gpu = Resolve-GpuIndex -Name $GpuName -Fallback $Gpu }

# Safety net: pinning to a missing index hides every CUDA device and the model
# silently loads on CPU. If the chosen index isn't present, fall back to GPU 0.
try {
    $gpuCount = (@(& nvidia-smi --query-gpu=index --format=csv,noheader 2>$null)).Count
    if ($gpuCount -gt 0 -and $Gpu -ge $gpuCount) {
        Write-Host "GPU $Gpu not present ($gpuCount GPU(s) detected); falling back to GPU 0" -ForegroundColor Yellow
        $Gpu = 0
    }
} catch { }

$env:PROMPT_PORT          = "$Port"
$env:CUDA_DEVICE_ORDER    = "PCI_BUS_ID" # stable index across mixed 5090/4090
$env:CUDA_VISIBLE_DEVICES = "$Gpu"       # pin the LLM to this GPU only
# PROMPT_MODEL / PROMPT_MAX_TOKENS / PROMPT_TEMPERATURE use prompt_server.py defaults unless set here.

# Force the classic HTTPS downloader (the hf_xet fast-transfer backend has stalled here before).
$env:HF_HUB_DISABLE_XET = "1"

Write-Host "Starting prompt-enhancer server on http://127.0.0.1:$Port (GPU $Gpu)  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "Venv: $Python" -ForegroundColor DarkGray

& $Python -u $Server
