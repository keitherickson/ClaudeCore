<#
.SYNOPSIS
    Stops the local prompt-enhancer server started by run-prompt-server.ps1.

.DESCRIPTION
    Kills the python process running prompt_server.py (this also catches a server still
    loading the model and not yet listening) and, as a fallback, whatever owns the port.
    Used by the Admin page's "Stop" button.

.PARAMETER Port
    Prompt server port (LocalLlm:BaseUrl, default 8771).
#>
[CmdletBinding()]
param([int]$Port = 8771)

$ErrorActionPreference = "SilentlyContinue"
$killed = New-Object System.Collections.Generic.List[int]

# By command line: catches a server still loading the model (not yet listening).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*prompt_server.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $killed.Add([int]$_.ProcessId) }

# Fallback: whatever is listening on the port.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { if ($killed -notcontains [int]$_) { Stop-Process -Id $_ -Force; $killed.Add([int]$_) } }

if ($killed.Count) { "Stopped prompt server PID(s): $($killed -join ', ')" }
else { "No prompt server process was running." }
exit 0
