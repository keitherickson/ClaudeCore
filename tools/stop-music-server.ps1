<#
.SYNOPSIS
    Stops the local MusicGen server started by run-music-server.ps1.

.DESCRIPTION
    Kills the python process running music_server.py (this also catches a server
    that is still loading the model and not yet listening) and, as a fallback,
    whatever owns the music port. Used by the Admin page's "Stop" button.

.PARAMETER Port
    Music server port (LocalMusic:BaseUrl, default 8772).
#>
[CmdletBinding()]
param([int]$Port = 8772)

$ErrorActionPreference = "SilentlyContinue"
$killed = New-Object System.Collections.Generic.List[int]

# By command line: catches a server still loading the model (not yet listening).
Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
    Where-Object { $_.CommandLine -like "*music_server.py*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $killed.Add([int]$_.ProcessId) }

# Fallback: whatever is listening on the port.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { if ($killed -notcontains [int]$_) { Stop-Process -Id $_ -Force; $killed.Add([int]$_) } }

if ($killed.Count) { "Stopped music server PID(s): $($killed -join ', ')" }
else { "No music server process was running." }
exit 0
