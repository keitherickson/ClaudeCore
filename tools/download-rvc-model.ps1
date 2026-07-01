<#
.SYNOPSIS
    Downloads an RVC target-voice model into the models dir in the layout the server expects.

.DESCRIPTION
    RVC target voices (the ".pth" that decides who you sound like) aren't bundled — you grab
    a community-trained model and drop it under the models dir as <name>\<name>.pth (+ optional
    <name>\<name>.index). This script does that placement for you from a URL, so the server's
    GET /models and the Voice page's "Load voices" button pick it up.

    Accepts a direct URL to a .pth, a .index, or a .zip that packs both (weights.gg commonly
    zips them). Give -IndexUrl to fetch a separate index file. HuggingFace /blob/ page URLs are
    rewritten to the raw /resolve/ form automatically. Base models (HuBERT/RMVPE) are NOT this —
    those download on their own on the first conversion.

    Find voices on https://weights.gg or https://voice-models.com (copy the direct download link).

.PARAMETER Url
    Direct URL to the model file: a .pth, a .index, or a .zip archive containing them.

.PARAMETER IndexUrl
    Optional URL to a separate .index file (better timbre). Ignored if -Url is a .zip that
    already contains one.

.PARAMETER Name
    Target-voice name (becomes the folder and the file base). Defaults to the downloaded
    file's name (minus extension). This is what shows up in the Voice page dropdown.

.PARAMETER ModelsDir
    Where voices live (must match run-rvc-server.ps1's -ModelsDir and LocalRvc:ModelsDir).

.PARAMETER Force
    Overwrite an existing voice of the same name instead of stopping.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\download-rvc-model.ps1 `
        -Url "https://huggingface.co/user/repo/resolve/main/MyVoice.pth" `
        -IndexUrl "https://huggingface.co/user/repo/resolve/main/MyVoice.index" -Name MyVoice

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\download-rvc-model.ps1 -Url "https://.../MyVoice.zip"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Url,
    [string]$IndexUrl,
    [string]$Name,
    [string]$ModelsDir = "C:\ClaudeCore\rvc-models",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# HuggingFace "blob" links point at an HTML page, not the file — the raw bytes live at /resolve/.
function Resolve-Url([string]$u) {
    if ($u -match '^https?://huggingface\.co/.+/blob/') { return ($u -replace '/blob/', '/resolve/') }
    return $u
}

# The bare file name from a URL, minus any ?query / #fragment.
function Get-UrlFileName([string]$u) {
    $path = ([uri]$u).AbsolutePath
    return [System.IO.Path]::GetFileName([uri]::UnescapeDataString($path))
}

function Get-File([string]$u, [string]$outFile) {
    $u = Resolve-Url $u
    Write-Host "  downloading $u" -ForegroundColor DarkGray
    # A UA keeps some CDNs from serving a challenge page instead of the file.
    Invoke-WebRequest -Uri $u -OutFile $outFile -Headers @{ "User-Agent" = "ClaudeCore-RVC/1.0" } -MaximumRedirection 10 -UseBasicParsing
    if (-not (Test-Path $outFile) -or (Get-Item $outFile).Length -eq 0) {
        throw "Download produced no data from $u"
    }
}

# Derive the voice name from -Name or the URL's file name.
$srcName = Get-UrlFileName $Url
if (-not $Name) {
    if (-not $srcName) { throw "Couldn't derive a name from the URL — pass -Name explicitly." }
    $Name = [System.IO.Path]::GetFileNameWithoutExtension($srcName)
}
# Keep the name filesystem-safe (it becomes a folder + file base).
$Name = ($Name -replace '[\\/:*?"<>|]', '_').Trim()
if (-not $Name) { throw "Resolved an empty voice name — pass -Name explicitly." }

$dest = Join-Path $ModelsDir $Name
if ((Test-Path $dest) -and -not $Force) {
    throw "Voice '$Name' already exists at $dest. Re-run with -Force to overwrite, or pick a different -Name."
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Work in a temp dir so a failed/partial download never lands in the models dir.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("rvcdl_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    $ext = [System.IO.Path]::GetExtension($srcName).ToLowerInvariant()
    $pthDest   = Join-Path $dest "$Name.pth"
    $indexDest = Join-Path $dest "$Name.index"

    Write-Host "Installing RVC voice '$Name' -> $dest" -ForegroundColor Cyan

    if ($ext -eq ".zip") {
        $zip = Join-Path $tmp "model.zip"
        Get-File $Url $zip
        $unzip = Join-Path $tmp "unzipped"
        Expand-Archive -Path $zip -DestinationPath $unzip -Force

        $pth = Get-ChildItem -Path $unzip -Recurse -Filter *.pth | Select-Object -First 1
        if (-not $pth) { throw "No .pth found inside the zip." }
        Copy-Item $pth.FullName $pthDest -Force

        $idx = Get-ChildItem -Path $unzip -Recurse -Filter *.index | Select-Object -First 1
        if ($idx) { Copy-Item $idx.FullName $indexDest -Force }
    }
    elseif ($ext -eq ".index") {
        Get-File $Url $indexDest
        Write-Host "  (got a .index only — you still need the matching .pth for this voice)" -ForegroundColor Yellow
    }
    else {
        # Treat anything else (incl. .pth or an extensionless resolve link) as the model weights.
        Get-File $Url $pthDest
    }

    # A separate index URL, if the .pth came without one.
    if ($IndexUrl -and -not (Test-Path $indexDest)) {
        Get-File $IndexUrl $indexDest
    }

    if (-not (Test-Path $pthDest)) {
        throw "No .pth ended up in $dest — an RVC voice needs one. Check the URL (a HuggingFace /blob/ page is not the file)."
    }

    $pthMb = [math]::Round((Get-Item $pthDest).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Installed '$Name':" -ForegroundColor Green
    Write-Host "  $pthDest  (${pthMb} MB)"
    if (Test-Path $indexDest) {
        $idxMb = [math]::Round((Get-Item $indexDest).Length / 1MB, 1)
        Write-Host "  $indexDest  (${idxMb} MB)"
    } else {
        Write-Host "  (no .index — conversion still works; an index can improve timbre)" -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "Restart isn't needed: click 'Load voices' on the Voice page (or GET /models) to see it." -ForegroundColor DarkGray
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
