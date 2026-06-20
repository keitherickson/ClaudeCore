<#
.SYNOPSIS
    Stops the local LTX-2.3 (BF16) inference server started by start-keithvision.ps1
    / run-ltx-server.ps1 (frees its GPU memory).

.DESCRIPTION
    Kills the python process running ltx_launch.py (also catches a server still
    loading and not yet listening) and, as a fallback, whatever owns the LTX port.
    Used by the /Admin model switch when it moves OFF the BF16 model so the NVFP4
    (ComfyUI) backend can take the same GPU's VRAM. Unlike restart-ltx-server.ps1
    this does NOT relaunch — the switch coordinator brings the right backend up.

.PARAMETER Port
    LTX server port (Ltx:BaseUrl, default 8765).
#>
[CmdletBinding()]
param([int]$Port = 8765)

$ErrorActionPreference = "SilentlyContinue"
$killed = New-Object System.Collections.Generic.List[int]

# By command line: catches a server still loading the model (not yet listening).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*ltx_launch.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $killed.Add([int]$_.ProcessId) }

# Fallback: whatever is listening on the port.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { if ($killed -notcontains [int]$_) { Stop-Process -Id $_ -Force; $killed.Add([int]$_) } }

if ($killed.Count) { "Stopped LTX server PID(s): $($killed -join ', ')" }
else { "No LTX server process was running." }
exit 0
