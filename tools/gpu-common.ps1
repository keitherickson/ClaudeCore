<#
.SYNOPSIS
    Shared GPU helpers for the launch scripts.

.DESCRIPTION
    Resolve-GpuIndex maps a GPU model name (substring, e.g. "RTX 5090") to its CUDA
    device ordinal so the launch scripts can pin work to a SPECIFIC card regardless of
    PCIe slot order. nvidia-smi's reported index matches the CUDA ordinal when callers
    set CUDA_DEVICE_ORDER=PCI_BUS_ID (which every launch script here does), so resolving
    by name is slot-order-proof: re-seating cards or a BIOS reorder can't silently move
    video off the 5090. Falls back to a given index if nvidia-smi is unavailable or no
    card matches.

    Dot-source it:  . (Join-Path $PSScriptRoot "gpu-common.ps1")
#>

function Resolve-GpuIndex {
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$Fallback = 0
    )
    try {
        $rows = @(& nvidia-smi --query-gpu=index,name --format=csv,noheader 2>$null)
        foreach ($row in $rows) {
            $parts = $row -split ',', 2
            if ($parts.Count -eq 2 -and $parts[1].Trim() -like "*$Name*") {
                return [int]$parts[0].Trim()
            }
        }
        Write-Host "Resolve-GpuIndex: no GPU matching '$Name'; using fallback index $Fallback" -ForegroundColor Yellow
    } catch {
        Write-Host "Resolve-GpuIndex: nvidia-smi unavailable; using fallback index $Fallback" -ForegroundColor Yellow
    }
    return $Fallback
}
