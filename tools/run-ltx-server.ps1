<#
.SYNOPSIS
    Launches a private LTX-2 inference server for ClaudeCore to call.

.DESCRIPTION
    Reuses the Python interpreter, engine code, and downloaded model weights that
    LTX Desktop already installed, but runs them as OUR OWN server on a fixed port
    with authentication disabled (localhost only). ClaudeCore's ASP.NET app talks
    to http://127.0.0.1:<Port>.

    IMPORTANT: Quit LTX Desktop before running this. Both load the 22B model into
    VRAM, and two copies will not fit on a single 32 GB GPU.

.PARAMETER Port
    Port to listen on. Must match the "Ltx:BaseUrl" port in appsettings.json (8765).

.PARAMETER Gpu
    Physical GPU index (CUDA device ordinal) to pin this server to. Exported as
    CUDA_VISIBLE_DEVICES so the LTX video model loads on that GPU only, leaving the
    other GPU free for the audio server. Used as a fallback when -GpuName can't resolve.

.PARAMETER GpuName
    Preferred way to pick the card: a GPU model substring (e.g. "RTX 5090"). Resolved to
    its CUDA index by name (slot-order-proof) and used in preference to -Gpu, so video
    generation always lands on the 5090 regardless of PCIe slot order.
#>
[CmdletBinding()]
param(
    [int]$Port       = 8765,
    [int]$Gpu        = 0,
    [string]$GpuName = "RTX 5090"
)

$ErrorActionPreference = "Stop"

# Pick the card by NAME first (slot-order-proof), falling back to the -Gpu index.
. (Join-Path $PSScriptRoot "gpu-common.ps1")
if ($GpuName) { $Gpu = Resolve-GpuIndex -Name $GpuName -Fallback $Gpu }

$Python  = "C:\Users\keith\AppData\Local\LTXDesktop\python\python.exe"
$Launch  = Join-Path $PSScriptRoot "ltx_launch.py"   # space-free path; inserts the backend on sys.path
$AppData = "C:\Users\keith\AppData\Local\LTXDesktop"   # has models\ and outputs\

foreach ($p in @($Python, $Launch, $AppData)) {
    if (-not (Test-Path $p)) { throw "Required path not found: $p" }
}

# Warn if LTX Desktop is running (VRAM contention).
if (Get-Process -Name "LTX Desktop" -ErrorAction SilentlyContinue) {
    Write-Warning "LTX Desktop is running. Quit it first or generation will fail to fit in VRAM."
}

# The server reads all of its config from environment variables.
$env:LTX_APP_DATA_DIR     = $AppData   # reuse already-downloaded models + outputs
$env:LTX_PORT             = "$Port"
$env:CUDA_DEVICE_ORDER    = "PCI_BUS_ID" # stable index across mixed 5090/4090
$env:CUDA_VISIBLE_DEVICES = "$Gpu"     # pin the video model to this GPU only
# LTX_AUTH_TOKEN intentionally unset  -> auth middleware is bypassed (localhost only).

Write-Host "Starting LTX-2 server on http://127.0.0.1:$Port (GPU $Gpu)  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "Models dir: $AppData\models" -ForegroundColor DarkGray
Write-Host "Output dir: $AppData\outputs" -ForegroundColor DarkGray

& $Python -u $Launch
