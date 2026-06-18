<#
.SYNOPSIS
    Launches the local Stable Audio Open server for ClaudeCore to call.

.DESCRIPTION
    Runs tools/audio_server.py in the dedicated audio venv. Serves text -> sound-effect
    generation at http://127.0.0.1:<Port>; ClaudeCore's SoundGenService talks to it.
    This is the self-hosted (no API key / no per-call cost) alternative to ElevenLabs.

    Set up the venv + weights first — see InstallationSteps.md, "AI sound generation".

    VRAM note: Stable Audio Open is small (a few GB) next to the LTX 22B model. Pin it
    to its own GPU with -Gpu (see below) so the audio and video models stay resident on
    separate cards; only if you force both onto one GPU do you risk OOM and need to
    generate audio and video sequentially.

.PARAMETER Port
    Port to listen on. Must match the "LocalAudio:BaseUrl" port in appsettings.json (8770).

.PARAMETER Gpu
    Physical GPU index (CUDA device ordinal) to pin this server to. Exported as
    CUDA_VISIBLE_DEVICES so the audio model loads on that GPU only, separate from the
    LTX video server's GPU. Must match "LocalAudio:GpuIndex" in appsettings.json.
#>
[CmdletBinding()]
param(
    [int]$Port = 8770,
    [int]$Gpu  = 1
)

$ErrorActionPreference = "Stop"

$Python = "C:\ClaudeCore\audio-venv\Scripts\python.exe"
$Server = Join-Path $PSScriptRoot "audio_server.py"

foreach ($p in @($Python, $Server)) {
    if (-not (Test-Path $p)) {
        throw "Required path not found: $p  (run the audio-server setup in InstallationSteps.md first)"
    }
}

$env:AUDIO_PORT           = "$Port"
$env:CUDA_VISIBLE_DEVICES = "$Gpu"   # pin the audio model to this GPU only
# AUDIO_MODEL / AUDIO_STEPS / AUDIO_MAX_SECONDS use audio_server.py defaults unless set here.

# Force the classic HTTPS downloader: the default hf_xet fast-transfer backend
# stalled mid-download on this machine (a file stuck at 0 bytes, no progress).
$env:HF_HUB_DISABLE_XET = "1"

Write-Host "Starting Stable Audio server on http://127.0.0.1:$Port (GPU $Gpu)  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "Venv: $Python" -ForegroundColor DarkGray

& $Python -u $Server
