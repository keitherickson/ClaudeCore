<#
.SYNOPSIS
    Launches the local MusicGen (text-to-music) server for ClaudeCore to call.

.DESCRIPTION
    Runs tools/music_server.py to serve text -> instrumental-music generation at
    http://127.0.0.1:<Port>; ClaudeCore's MusicGenService talks to it. This is the
    self-hosted (no API key / no per-call cost) music counterpart to the Stable Audio
    sound-effects server.

    VENV: MusicGen (via Transformers) needs only torch + transformers + soundfile,
    which the audio venv already provides — so this reuses C:\ClaudeCore\audio-venv
    rather than standing up a second multi-GB torch install. See tools/music-requirements.txt.
    Accept no license (MusicGen weights download on first run; ~3 GB for medium).

    VRAM note: MusicGen medium is ~6 GB. Pin it to its own GPU with -Gpu / -GpuName
    (defaults to the 4090) so the music model stays resident on a different card than
    the LTX video models on the 5090.

.PARAMETER Port
    Port to listen on. Must match the "LocalMusic:BaseUrl" port in appsettings.json (8772).

.PARAMETER Gpu
    Physical GPU index (CUDA device ordinal) to pin this server to. Exported as
    CUDA_VISIBLE_DEVICES. Fallback when -GpuName can't be resolved.

.PARAMETER GpuName
    Preferred way to pick the card: a GPU model substring (e.g. "RTX 4090"). Resolved to
    its CUDA index by name (slot-order-proof) and used in preference to -Gpu.

.PARAMETER Model
    HF model id. Override to facebook/musicgen-small (faster/lighter) or
    facebook/musicgen-large (best quality, more VRAM). Default: facebook/musicgen-medium.
#>
[CmdletBinding()]
param(
    [int]$Port       = 8772,
    [int]$Gpu        = 1,
    [string]$GpuName = "RTX 4090",
    [string]$Model   = "facebook/musicgen-medium"
)

$ErrorActionPreference = "Stop"

# Reuse the audio venv — MusicGen's deps (torch/transformers/soundfile) are a subset of it.
$Python = "C:\ClaudeCore\audio-venv\Scripts\python.exe"
$Server = Join-Path $PSScriptRoot "music_server.py"

foreach ($p in @($Python, $Server)) {
    if (-not (Test-Path $p)) {
        throw "Required path not found: $p  (set up the audio venv per tools/audio-requirements.txt first)"
    }
}

# Pick the card by NAME first (slot-order-proof), falling back to the -Gpu index.
. (Join-Path $PSScriptRoot "gpu-common.ps1")
if ($GpuName) { $Gpu = Resolve-GpuIndex -Name $GpuName -Fallback $Gpu }

# Safety net: pinning to a missing index hides every CUDA device and the model
# silently loads on CPU (painfully slow). If the chosen index isn't present, fall back to GPU 0.
try {
    $gpuCount = (@(& nvidia-smi --query-gpu=index --format=csv,noheader 2>$null)).Count
    if ($gpuCount -gt 0 -and $Gpu -ge $gpuCount) {
        Write-Host "GPU $Gpu not present ($gpuCount GPU(s) detected); falling back to GPU 0" -ForegroundColor Yellow
        $Gpu = 0
    }
} catch { }

$env:MUSIC_PORT           = "$Port"
$env:MUSIC_MODEL          = "$Model"
$env:CUDA_DEVICE_ORDER    = "PCI_BUS_ID" # stable index across mixed 5090/4090
$env:CUDA_VISIBLE_DEVICES = "$Gpu"   # pin the music model to this GPU only
# MUSIC_MAX_SECONDS / MUSIC_GUIDANCE use music_server.py defaults unless set here.

# Force the classic HTTPS downloader: the default hf_xet fast-transfer backend
# stalled mid-download on this machine (a file stuck at 0 bytes, no progress).
$env:HF_HUB_DISABLE_XET = "1"

Write-Host "Starting MusicGen server on http://127.0.0.1:$Port (GPU $Gpu, model $Model)  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "Venv: $Python" -ForegroundColor DarkGray

& $Python -u $Server
