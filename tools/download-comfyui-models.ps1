<#
.SYNOPSIS
    Downloads every ComfyUI model weight KeithVision needs (NVFP4 + Wan 2.2 video
    backends + Real-ESRGAN AI upscale) into the right ComfyUI models folders.

.DESCRIPTION
    Turns InstallationSteps.md §8's prose into one command. Idempotent — skips any
    file already present, so it's safe to re-run / resume. All sources are ungated
    (no HF login needed); HF_HUB_DISABLE_XET works around the xet backend stalling.
    The Stable Audio model (self-hosted sound, §9) is gated and downloaded separately
    by run-audio-server.ps1 after `hf auth login`.

.PARAMETER ModelsRoot
    ComfyUI models directory (must match the file names in the ComfyUI/Wan/ComfyUiUpscale
    appsettings sections).

.PARAMETER Hf
    Path to the Hugging Face CLI. Defaults to the audio venv's copy; falls back to `hf`
    on PATH (install with `pip install huggingface_hub`).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\download-comfyui-models.ps1
#>
[CmdletBinding()]
param(
    [string]$ModelsRoot = "C:\ComfyUI\ComfyUI\models",
    [string]$Hf = "C:\ClaudeCore\audio-venv\Scripts\hf.exe"
)

$ErrorActionPreference = "Stop"
$env:HF_HUB_DISABLE_XET = "1"   # the xet fast-transfer backend stalls on this box
if (-not (Test-Path $Hf)) { $Hf = "hf" }   # fall back to PATH

function Get-HfFile($repo, $repoPath, $destDir) {
    $name = Split-Path $repoPath -Leaf
    $dest = Join-Path $destDir $name
    if (Test-Path $dest) { Write-Host "  [skip] $name" -ForegroundColor DarkGray; return }
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    $staging = Join-Path $env:TEMP ("hfdl_" + [guid]::NewGuid().ToString("N"))
    Write-Host "  [get]  $name  ($repo)" -ForegroundColor Cyan
    & $Hf download $repo $repoPath --local-dir $staging
    if ($LASTEXITCODE -ne 0) { throw "hf download failed for $repo/$repoPath" }
    Move-Item (Join-Path $staging $repoPath) $dest -Force
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

function Get-UrlFile($url, $destDir) {
    $name = Split-Path $url -Leaf
    $dest = Join-Path $destDir $name
    if (Test-Path $dest) { Write-Host "  [skip] $name" -ForegroundColor DarkGray; return }
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Write-Host "  [get]  $name  ($url)" -ForegroundColor Cyan
    curl.exe -sL -o $dest $url
}

Write-Host "NVFP4 LTX-2.3 (fast text-to-video) ->" -ForegroundColor Green
Get-HfFile "Lightricks/LTX-2.3-nvfp4" "ltx-2.3-22b-dev-nvfp4.safetensors"                              "$ModelsRoot\checkpoints"
Get-HfFile "Lightricks/LTX-2.3"       "ltx-2.3-22b-distilled-lora-384-1.1.safetensors"                 "$ModelsRoot\loras"
Get-HfFile "Comfy-Org/ltx-2"          "split_files/text_encoders/gemma_3_12B_it_fp8_scaled.safetensors" "$ModelsRoot\text_encoders"

Write-Host "Wan 2.2 14B (quality image-to-video) ->" -ForegroundColor Green
$wan = "Comfy-Org/Wan_2.2_ComfyUI_Repackaged"
Get-HfFile $wan "split_files/diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" "$ModelsRoot\diffusion_models"
Get-HfFile $wan "split_files/diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors"  "$ModelsRoot\diffusion_models"
Get-HfFile $wan "split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors"              "$ModelsRoot\text_encoders"
Get-HfFile $wan "split_files/vae/wan_2.1_vae.safetensors"                                       "$ModelsRoot\vae"
Get-HfFile $wan "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors"   "$ModelsRoot\loras"
Get-HfFile $wan "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors"    "$ModelsRoot\loras"

Write-Host "Real-ESRGAN x4 (AI upscale) ->" -ForegroundColor Green
Get-UrlFile "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth" "$ModelsRoot\upscale_models"

Write-Host "`nAll ComfyUI models present under $ModelsRoot." -ForegroundColor Green
