<#
.SYNOPSIS
    Launches the ComfyUI server that hosts the NVFP4 LTX-2.3 "fast" video model.

.DESCRIPTION
    Runs ComfyUI (C:\ComfyUI\ComfyUI\main.py) from its dedicated venv on a fixed
    port for ClaudeCore's ComfyUiVideoBackend to call. This is the FP4 (Blackwell)
    path — it MUST run on the RTX 5090; the Ada 4090 has no native FP4 cores.

    Started on demand by the web app (ComfyUiServerControl) when the /Admin model
    switch selects the NVFP4 model — NOT auto-started at logon. ComfyUI auto-loads
    extra_model_paths.yaml from its base dir for the model/text-encoder folders.

.PARAMETER Port
    Port to listen on. Must match the "ComfyUI:BaseUrl" port in appsettings.json (8188).

.PARAMETER Gpu
    Physical GPU index (CUDA device ordinal) to pin ComfyUI to. Exported as
    CUDA_VISIBLE_DEVICES. Used as a fallback when -GpuName can't be resolved.

.PARAMETER GpuName
    Preferred way to pick the card: a GPU model substring (e.g. "RTX 5090"). Resolved to
    its CUDA index by name (slot-order-proof) and used in preference to -Gpu. MUST resolve
    to the Blackwell (5090) card — NVFP4 has no Ada (4090) fallback.
#>
[CmdletBinding()]
param(
    [int]$Port       = 8188,
    [int]$Gpu        = 0,
    [string]$GpuName = "RTX 5090"
)

$ErrorActionPreference = "Stop"

$ComfyDir = "C:\ComfyUI\ComfyUI"
$Python   = "C:\ComfyUI\venv\Scripts\python.exe"
$Main     = Join-Path $ComfyDir "main.py"

foreach ($p in @($Python, $Main)) {
    if (-not (Test-Path $p)) { throw "Required path not found: $p" }
}

# Pick the card by NAME first (slot-order-proof), falling back to the -Gpu index.
. (Join-Path $PSScriptRoot "gpu-common.ps1")
if ($GpuName) { $Gpu = Resolve-GpuIndex -Name $GpuName -Fallback $Gpu }

$env:CUDA_DEVICE_ORDER     = "PCI_BUS_ID"   # stable index across mixed 5090/4090
$env:CUDA_VISIBLE_DEVICES   = "$Gpu"        # pin NVFP4 to the Blackwell card only

Write-Host "Starting ComfyUI (NVFP4) on http://127.0.0.1:$Port (GPU $Gpu)  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "ComfyUI dir: $ComfyDir" -ForegroundColor DarkGray

Set-Location $ComfyDir   # ComfyUI resolves custom nodes / extra_model_paths.yaml from here
& $Python -u $Main --listen 127.0.0.1 --port $Port
