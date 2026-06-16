#Requires -Version 5.1
<#
.SYNOPSIS
    ClaudeCore bootstrap installer (online).

.DESCRIPTION
    Provisions everything ClaudeCore needs on a Windows + NVIDIA RTX machine:
      1. Prerequisite tooling (.NET SDK, git, GitHub CLI, ffmpeg)
      2. LTX-2.3 local video generation (engine + model weights)
      3. NVIDIA Maxine video upscaling (SDK repo + models, writable copy)
      4. Build and configure the ClaudeCore app (incl. ffmpeg path for "Play faster")

    This is an online bootstrapper: it downloads from official sources rather
    than embedding ~100 GB of weights. A few components are EULA-gated downloads
    that you must run yourself; the script detects them and tells you what to do,
    then continues once they are present.

    Idempotent: safe to re-run. It skips anything already in place.

.PARAMETER AppRoot
    Path to the ClaudeCore project (folder containing ClaudeCore.csproj).

.PARAMETER SkipPrereqs
    Skip the winget prerequisite installs (assume .NET/git already present).

.PARAMETER SkipWeights
    Skip downloading LTX model weights (assume already downloaded).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$AppRoot      = $PSScriptRoot,
    [string]$MaxineRepo   = "C:\ClaudeCore\maxine-vfx",
    [string]$MaxineModels = "C:\ClaudeCore\maxine-models",
    [string]$LtxDataDir   = (Join-Path $env:LOCALAPPDATA "LTXDesktop"),
    [switch]$SkipPrereqs,
    [switch]$SkipWeights
)

$ErrorActionPreference = "Stop"

# Resolve AppRoot robustly (param default $PSScriptRoot can be empty in some invocations).
if ([string]::IsNullOrWhiteSpace($AppRoot)) {
    if ($PSScriptRoot) { $AppRoot = $PSScriptRoot }
    elseif ($PSCommandPath) { $AppRoot = Split-Path -Parent $PSCommandPath }
    else { $AppRoot = (Get-Location).Path }
}

# --- output helpers -------------------------------------------------------
function Step($m)   { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)     { Write-Host "  [ OK ] $m"  -ForegroundColor Green }
function Note($m)   { Write-Host "  [info] $m"  -ForegroundColor Gray }
function Warn($m)   { Write-Host "  [warn] $m"  -ForegroundColor Yellow }
function Action($m) { Write-Host "  [ACTION NEEDED] $m" -ForegroundColor Magenta }
function Have($cmd) { $null -ne (Get-Command $cmd -ErrorAction SilentlyContinue) }

# Locate ffmpeg.exe: prefer PATH, then the winget package folder (where the
# running app can't see PATH changes, so an absolute path is what we configure).
function Find-Ffmpeg {
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $pkgs = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    if (Test-Path $pkgs) {
        $f = Get-ChildItem $pkgs -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    return $null
}

function Wait-ForPath {
    param([string]$Path, [string]$What, [string]$Url)
    while (-not (Test-Path $Path)) {
        Action "$What is required but not found at: $Path"
        if ($Url) { Note "Download: $Url" }
        $r = Read-Host "  Press ENTER after installing to re-check, or type 'skip' to continue"
        if ($r -eq 'skip') { Warn "Continuing without $What."; return $false }
    }
    Ok "$What present."
    return $true
}

$failures = New-Object System.Collections.Generic.List[string]

# ==========================================================================
Step "0. Preflight"
# ==========================================================================
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw "Windows is required." }
Ok "Windows $([Environment]::OSVersion.Version)"

# GPU / VRAM / driver
$gpuOk = $false
if (Have nvidia-smi) {
    $smi = (& nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader 2>$null) -split ','
    if ($smi.Count -ge 3) {
        $name = $smi[0].Trim()
        $vram = [int]($smi[1] -replace '[^\d]','')
        $drv  = $smi[2].Trim()
        Ok "GPU: $name | VRAM ${vram} MiB | driver $drv"
        if ($vram -lt 24000) { Warn "VRAM under 24 GB; LTX 22B and Maxine SR may struggle." }
        try {
            if ([version]($drv.Split('.')[0..1] -join '.') -lt [version]'521.98') { Warn "Driver older than 521.98; Maxine needs newer." }
        } catch { }
        $gpuOk = $true
    }
}
if (-not $gpuOk) { Warn "No NVIDIA GPU detected via nvidia-smi. Local generation/upscaling need an RTX GPU." }

# Disk
$appItem = Get-Item $AppRoot -ErrorAction SilentlyContinue
if ($appItem -and $appItem.PSDrive) { $driveName = $appItem.PSDrive.Name } else { $driveName = 'C' }
$freeGB = [math]::Round((Get-PSDrive $driveName).Free / 1GB, 0)
if ($freeGB -lt 100) { Warn "Only ${freeGB} GB free; LTX weights need about 70 GB." } else { Ok "${freeGB} GB free." }

if (-not (Test-Path (Join-Path $AppRoot 'ClaudeCore.csproj'))) {
    throw "ClaudeCore.csproj not found under AppRoot '$AppRoot'. Pass -AppRoot [path]."
}
Ok "ClaudeCore project: $AppRoot"

# ==========================================================================
Step "1. Prerequisite tooling"
# ==========================================================================
if ($SkipPrereqs) {
    Note "Skipping prerequisite installs (-SkipPrereqs)."
} elseif (-not (Have winget)) {
    Warn "winget not available; install prerequisites manually (see Downloads index in InstallationSteps.md)."
} else {
    function Ensure-Winget($id, $probe, $name, $url) {
        if (Have $probe) { Ok "$name present."; return }
        Note "Installing $name via winget ($id)..."
        winget install --id $id --silent --accept-source-agreements --accept-package-agreements | Out-Null
        if (Have $probe) { Ok "$name installed." } else { Warn "$name not on PATH yet; open a new shell. ($url)" }
    }
    Ensure-Winget "Microsoft.DotNet.SDK.10" "dotnet" ".NET 10 SDK"   "https://dotnet.microsoft.com/download/dotnet/10.0"
    Ensure-Winget "Git.Git"                 "git"    "Git for Windows" "https://git-scm.com/download/win"
    Ensure-Winget "GitHub.cli"              "gh"     "GitHub CLI"      "https://cli.github.com/"
    Ensure-Winget "Gyan.FFmpeg"             "ffmpeg" "ffmpeg"          "https://www.gyan.dev/ffmpeg/builds/"
}

# ==========================================================================
Step "2. LTX-2.3 video generation"
# ==========================================================================
$ltxPy      = Join-Path $LtxDataDir "python\python.exe"
$ltxBackend = Join-Path $env:LOCALAPPDATA "Programs\LTX Desktop\resources\backend\ltx2_server.py"
$ltxModels  = Join-Path $LtxDataDir "models"

if (-not (Test-Path $ltxPy) -or -not (Test-Path $ltxBackend)) {
    Action "LTX Desktop (provides the Python engine + weights) is not installed."
    Note  "Install from https://ltx.io/ltx-desktop , run it once to download models, then re-run this installer."
    $failures.Add("LTX Desktop not installed")
} else {
    Ok "LTX engine present (bundled Python + backend)."

    # Required checkpoints (relative path under models dir) -> HF repo id
    $required = @(
        @{ Path = "ltx-2.3-22b-distilled.safetensors";           Repo = "Lightricks/LTX-2.3"; Folder = $false },
        @{ Path = "ltx-2.3-spatial-upscaler-x2-1.0.safetensors"; Repo = "Lightricks/LTX-2.3"; Folder = $false },
        @{ Path = "gemma-3-12b-it-qat-q4_0-unquantized";         Repo = "Lightricks/gemma-3-12b-it-qat-q4_0-unquantized"; Folder = $true }
    )
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $ltxModels $_.Path)) })

    if ($missing.Count -eq 0) {
        Ok "All required LTX weights present."
    } elseif ($SkipWeights) {
        Warn "Missing weights but -SkipWeights set: $(($missing | ForEach-Object { $_.Path }) -join ', ')"
    } else {
        Note "Downloading missing LTX weights from Hugging Face (large; uses the bundled Python)..."
        New-Item -ItemType Directory -Force -Path $ltxModels | Out-Null
        foreach ($cp in $missing) {
            $target = Join-Path $ltxModels $cp.Path
            Note "  downloading $($cp.Repo) :: $($cp.Path)"
            if ($cp.Folder) {
                $py = "from huggingface_hub import snapshot_download; snapshot_download(repo_id=r'$($cp.Repo)', local_dir=r'$target')"
            } else {
                $py = "from huggingface_hub import hf_hub_download; hf_hub_download(repo_id=r'$($cp.Repo)', filename=r'$($cp.Path)', local_dir=r'$ltxModels')"
            }
            try { & $ltxPy -c $py } catch { Warn "  download failed for $($cp.Path): $_"; $failures.Add("LTX weight $($cp.Path)") }
        }
    }
    Note "Run the LTX server per session (LTX Desktop closed): tools\run-ltx-server.ps1  (http://127.0.0.1:8765)"
}

# ==========================================================================
Step "3. NVIDIA Maxine video upscaling"
# ==========================================================================
# 3a. SDK source repo (headers + prebuilt VideoEffectsApp.exe)
if (-not (Test-Path (Join-Path $MaxineRepo "samples\VideoEffectsApp\VideoEffectsApp.exe"))) {
    if (Have git) {
        Note "Cloning Maxine VFX SDK repo to $MaxineRepo ..."
        git clone --depth 1 https://github.com/NVIDIA-Maxine/Maxine-VFX-SDK.git $MaxineRepo 2>&1 | Out-Null
        Ok "Cloned Maxine VFX SDK."
    } else {
        Warn "git missing; clone https://github.com/NVIDIA-Maxine/Maxine-VFX-SDK manually to $MaxineRepo"
    }
} else {
    Ok "Maxine VFX SDK repo present."
}

# 3b. SDK redistributable (models + runtime DLLs) - EULA-gated, user installs
$sdkInstall   = "C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects"
$sdkSrcModels = Join-Path $sdkInstall "models"
$haveSdk = Wait-ForPath -Path $sdkSrcModels -What "Maxine Video Effects SDK redistributable" -Url "https://www.nvidia.com/broadcast-sdk-resources"

# 3c. Copy models to a WRITABLE dir (Program Files is read-only; TensorRT caches there on first run)
if ($haveSdk) {
    Note "Copying Maxine models to writable dir $MaxineModels (required; Program Files is read-only)..."
    New-Item -ItemType Directory -Force -Path $MaxineModels | Out-Null
    robocopy $sdkSrcModels $MaxineModels /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if (Test-Path (Join-Path $MaxineModels "SR_2x_con_100.engine.trtpkg")) {
        Ok "Maxine models copied to writable dir."
    } else {
        Warn "Model copy may have failed; check $MaxineModels"
        $failures.Add("Maxine models copy")
    }
} else {
    $failures.Add("Maxine SDK redistributable not installed")
}

# ==========================================================================
Step "4. Build and configure ClaudeCore"
# ==========================================================================
Push-Location $AppRoot
try {
    if (Have dotnet) {
        Note "Restoring and building (dotnet build)..."
        dotnet build "ClaudeCore.csproj" -c Release --nologo -v minimal
        Ok "Build succeeded."
    } else {
        Warn "dotnet not found; install the .NET 10 SDK then run 'dotnet build'."
        $failures.Add(".NET SDK missing")
    }

    foreach ($d in @(
        "C:\Users\$env:USERNAME\Videos\LTX-Generated",
        "C:\Users\$env:USERNAME\Videos\LTX-Generated\upscaled")) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }
    Ok "Output folders ready."
    Note "appsettings.json -> Maxine: ModelDir='$MaxineModels', SdkBinDir='$sdkInstall'."

    # Configure VideoSpeed:FfmpegPath ("Play faster" re-time). The winget path is
    # version-specific, so resolve it now and write it into appsettings.json.
    $ffmpeg = Find-Ffmpeg
    if ($ffmpeg) {
        $cfg     = Join-Path $AppRoot 'appsettings.json'
        $json    = Get-Content $cfg -Raw
        $pattern = '("FfmpegPath"\s*:\s*")[^"]*(")'
        if ([regex]::IsMatch($json, $pattern)) {
            $escaped = $ffmpeg -replace '\\', '\\'   # JSON-escape backslashes
            $new = [regex]::Replace($json, $pattern, { param($m) $m.Groups[1].Value + $escaped + $m.Groups[2].Value })
            if ($new -ne $json) {
                Set-Content -Path $cfg -Value $new -Encoding UTF8 -NoNewline
                Ok "Configured VideoSpeed:FfmpegPath = $ffmpeg"
            } else {
                Ok "VideoSpeed:FfmpegPath already current."
            }
        } else {
            Warn "VideoSpeed section not found in appsettings.json; add VideoSpeed:FfmpegPath = $ffmpeg manually."
        }
    } else {
        Warn "ffmpeg not found; 'Play faster' stays disabled until ffmpeg is installed and VideoSpeed:FfmpegPath is set."
        $failures.Add("ffmpeg not installed")
    }

    # AI sound generation (optional, self-hosted). Runs a local Stable Audio server
    # (no API key). The torch cu128 venv + gated weights are a separate manual step.
    Note "Optional: AI sound generation runs a local Stable Audio server (no API key). Set up the venv + weights per InstallationSteps.md ('AI sound generation'), then start it with tools\run-audio-server.ps1."
}
finally { Pop-Location }

# ==========================================================================
Step "Summary"
# ==========================================================================
if ($failures.Count -eq 0) {
    Ok "Setup complete."
} else {
    Warn "Setup finished with items needing attention:"
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. (generation) start the LTX server with LTX Desktop closed:" -ForegroundColor Cyan
Write-Host "       $AppRoot\tools\run-ltx-server.ps1" -ForegroundColor Cyan
Write-Host "  2. start the app:" -ForegroundColor Cyan
Write-Host "       dotnet run --project `"$AppRoot\ClaudeCore.csproj`"" -ForegroundColor Cyan
Write-Host "  3. open the URL it prints. Pages: /Video (generate), /Upscale (Maxine 4K), /Admin (health)." -ForegroundColor Cyan
Write-Host "  4. (optional) for AI sound generation, set up the local Stable Audio server (see InstallationSteps.md) and run tools\run-audio-server.ps1." -ForegroundColor Cyan
Write-Host "  See InstallationSteps.md for full details and the Downloads index." -ForegroundColor Cyan
