<#
.SYNOPSIS
    Stops the ComfyUI NVFP4 server started by run-comfyui.ps1 (frees its GPU memory).

.DESCRIPTION
    Kills the python process running ComfyUI's main.py (this also catches a server
    still importing/loading and not yet listening) and, as a fallback, whatever owns
    the ComfyUI port. Used by the /Admin model switch when it moves OFF the NVFP4
    model and by the co-resident-backend logic that frees VRAM before bringing up the
    other video model on the same GPU.

.PARAMETER Port
    ComfyUI server port (ComfyUI:BaseUrl, default 8188).
#>
[CmdletBinding()]
param([int]$Port = 8188)

$ErrorActionPreference = "SilentlyContinue"
$killed = New-Object System.Collections.Generic.List[int]

# By command line: catches a server still importing nodes / loading (not yet listening).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*ComfyUI*main.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $killed.Add([int]$_.ProcessId) }

# Fallback: whatever is listening on the port.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { if ($killed -notcontains [int]$_) { Stop-Process -Id $_ -Force; $killed.Add([int]$_) } }

if ($killed.Count) { "Stopped ComfyUI PID(s): $($killed -join ', ')" }
else { "No ComfyUI process was running." }
exit 0
