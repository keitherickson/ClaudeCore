<#
.SYNOPSIS
    Stops the local RVC server started by run-rvc-server.ps1.

.DESCRIPTION
    Kills the python process running `rvc_python api` (also catches one still loading
    base models and not yet listening) and, as a fallback, whatever owns the RVC port.
    Used by the Admin page's "Stop" button.

.PARAMETER Port
    RVC server port (LocalRvc:BaseUrl, default 8773).
#>
[CmdletBinding()]
param([int]$Port = 8773)

$ErrorActionPreference = "SilentlyContinue"
$killed = New-Object System.Collections.Generic.List[int]

# By command line: catches a server still loading models (not yet listening).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*rvc_python*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $killed.Add([int]$_.ProcessId) }

# Fallback: whatever is listening on the port.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { if ($killed -notcontains [int]$_) { Stop-Process -Id $_ -Force; $killed.Add([int]$_) } }

if ($killed.Count) { "Stopped RVC server PID(s): $($killed -join ', ')" }
else { "No RVC server process was running." }
exit 0
