<#
.SYNOPSIS
    Builds the ComfyUI environment KeithVision's NVFP4 / Wan 2.2 / AI-upscale
    backends need: venv + cu130 nightly torch + ComfyUI + LTX nodes (pinned
    commits) + the kornia patch + the pinned Python deps. Optionally downloads
    the model weights too.

.DESCRIPTION
    Automates InstallationSteps.md §8. Idempotent — safe to re-run; each step
    detects what's already done. Requires Python 3.11 on PATH (`py -3.11`).
    Needs a Blackwell GPU (RTX 5090, sm_120) for the FP4 path to actually engage.

.PARAMETER ComfyRoot
    Install root (holds the venv + the ComfyUI clone).

.PARAMETER WithModels
    Also run download-comfyui-models.ps1 at the end (~100 GB of weights).
#>
[CmdletBinding()]
param(
    [string]$ComfyRoot = "C:\ComfyUI",
    [switch]$WithModels
)

$ErrorActionPreference = "Stop"
$ComfyUiCommit = "5ef0092"   # pinned for graph/template stability (see comfyui-requirements.txt)
$LtxNodeCommit = "4f45fd6"
$Venv    = Join-Path $ComfyRoot "venv"
$Py      = Join-Path $Venv "Scripts\python.exe"
$ComfyUi = Join-Path $ComfyRoot "ComfyUI"
$Reqs    = Join-Path $PSScriptRoot "comfyui-requirements.txt"

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

New-Item -ItemType Directory -Force -Path $ComfyRoot | Out-Null

Step "1. Python 3.11 venv"
if (-not (Test-Path $Py)) {
    & py -3.11 -m venv $Venv
    if ($LASTEXITCODE -ne 0) { throw "Failed to create venv (is Python 3.11 installed? `py -3.11 --version`)" }
    & $Py -m pip install --upgrade pip | Out-Null
} else { Write-Host "venv exists -> $Venv" }

Step "2. PyTorch cu130 nightly (sm_120 / native FP4)"
# Always the latest cu130 nightly: the exact pinned build may be purged from the
# index. comfyui-requirements.txt records what was used if you need to match it.
& $Py -c "import torch,sys; sys.exit(0 if (torch.version.cuda or '').startswith('13') else 1)" 2>$null
$hasTorch = ($LASTEXITCODE -eq 0)
if (-not $hasTorch) {
    & $Py -m pip install --pre torch torchvision torchaudio --index-url https://download.pytorch.org/whl/nightly/cu130
    if ($LASTEXITCODE -ne 0) { throw "torch cu130 nightly install failed" }
} else { Write-Host "cu130 torch already present" }

Step "3. Clone ComfyUI @ $ComfyUiCommit"
if (-not (Test-Path (Join-Path $ComfyUi "main.py"))) {
    & git clone https://github.com/comfyanonymous/ComfyUI $ComfyUi
}
& git -C $ComfyUi fetch --quiet
& git -C $ComfyUi checkout --quiet $ComfyUiCommit

Step "4. Clone LTX-Video nodes @ $LtxNodeCommit + kornia patch"
$nodeDir = Join-Path $ComfyUi "custom_nodes\ComfyUI-LTXVideo"
if (-not (Test-Path $nodeDir)) {
    & git clone https://github.com/Lightricks/ComfyUI-LTXVideo $nodeDir
}
& git -C $nodeDir fetch --quiet
& git -C $nodeDir checkout --quiet $LtxNodeCommit
# Kornia patch: kornia >=0.8.3 dropped the `pad` re-export from the pyramid module.
$pf = Join-Path $nodeDir "pyramid_blending.py"
$content = Get-Content $pf
if ($content -match 'from torch\.nn\.functional import pad') {
    Write-Host "kornia patch already applied"
} else {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in $content) {
        if ($line -match '^\s*pad,?\s*$') { continue }                 # drop `pad,` from the kornia import block
        if ($line -match '^from kornia\.geometry\.transform\.pyramid import') {
            $out.Add('from torch.nn.functional import pad  # kornia >=0.8.3 dropped its re-export')
        }
        $out.Add($line)
    }
    Set-Content -Path $pf -Value $out -Encoding UTF8
    Write-Host "applied kornia patch -> pyramid_blending.py"
}

Step "5. Python deps (pinned, torch-stripped so the nightly isn't downgraded)"
$tmpReq = Join-Path $env:TEMP ("comfyreq_" + [guid]::NewGuid().ToString("N") + ".txt")
Get-Content $Reqs | Where-Object { $_ -notmatch '^(torch|torchvision|torchaudio)==' } | Set-Content $tmpReq -Encoding UTF8
& $Py -m pip install -r $tmpReq
Remove-Item $tmpReq -Force -ErrorAction SilentlyContinue
if ($LASTEXITCODE -ne 0) { throw "dependency install failed" }

Step "6. Verify GPU (sm_120 / cu130)"
& $Py -c "import torch; print('cuda', torch.version.cuda, '| device', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NONE', '| sm_120', 'sm_120' in torch.cuda.get_arch_list())"

if ($WithModels) {
    Step "7. Download model weights"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "download-comfyui-models.ps1")
} else {
    Write-Host "`nEnv ready. Get the weights with: tools\download-comfyui-models.ps1 (or re-run with -WithModels)." -ForegroundColor Green
}

Write-Host "`nComfyUI environment ready at $ComfyRoot. Launch: tools\run-comfyui.ps1" -ForegroundColor Green
