<#
.SYNOPSIS
    Stops the local Stable Audio server started by run-audio-server.ps1.

.DESCRIPTION
    Kills the python process running audio_server.py (this also catches a server
    that is still loading the model and not yet listening) and, as a fallback,
    whatever owns the audio port. Used by the Admin page's "Stop" button.

.PARAMETER Port
    Audio server port (LocalAudio:BaseUrl, default 8770).
#>
[CmdletBinding()]
param([int]$Port = 8770)

$ErrorActionPreference = "SilentlyContinue"
$killed = New-Object System.Collections.Generic.List[int]

# By command line: catches a server still loading the model (not yet listening).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*audio_server.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $killed.Add([int]$_.ProcessId) }

# Fallback: whatever is listening on the port.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { if ($killed -notcontains [int]$_) { Stop-Process -Id $_ -Force; $killed.Add([int]$_) } }

if ($killed.Count) { "Stopped audio server PID(s): $($killed -join ', ')" }
else { "No audio server process was running." }
exit 0
