#Requires -Version 5.1
<#
.SYNOPSIS
    Re-publish the KeithUI app so the logon auto-start picks up code changes.

.DESCRIPTION
    The "KeithUI Startup" scheduled task runs the PUBLISHED build at
    C:\ClaudeCore\KeithUI (KeithUI.exe), not the live source. Run this after code
    changes: it stops the running published app (to free locked files), publishes
    a fresh Release build, then relaunches via tools\start-keithui.ps1 (which binds
    127.0.0.2:80). Mirrors tools\publish-keithvision.ps1.

.PARAMETER NoRestart
    Publish only; do not relaunch the app afterward.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\publish-keithui.ps1
#>
[CmdletBinding()]
param(
    [string]$Project    = "C:\ClaudeCore\ClaudeCore\KeithUI\KeithUI.csproj",
    [string]$PublishDir = "C:\ClaudeCore\KeithUI",
    [switch]$NoRestart
)
$ErrorActionPreference = "Stop"

Write-Host "Stopping running published app (to free locked files)..." -ForegroundColor Cyan
Get-Process -Name "KeithUI" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "Publishing Release build to $PublishDir ..." -ForegroundColor Cyan
dotnet publish $Project -c Release -o $PublishDir --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Write-Host "Publish complete." -ForegroundColor Green

if ($NoRestart) {
    Write-Host "Skipping relaunch (-NoRestart). It will start at next logon, or run tools\start-keithui.ps1." -ForegroundColor Yellow
    return
}

$toolsDir = if ($PSScriptRoot) { $PSScriptRoot } else { "C:\ClaudeCore\ClaudeCore\tools" }
$launcher = Join-Path $toolsDir "start-keithui.ps1"
Write-Host "Relaunching KeithUI..." -ForegroundColor Cyan
& $launcher

# start-keithui.ps1 binds KeithUI to 127.0.0.2:80 (next to KeithVision on 127.0.0.1).
$up = $false
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 2
    try { Invoke-WebRequest "http://127.0.0.2/" -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop | Out-Null; $up = $true; break } catch { }
}
if ($up) {
    Write-Host "KeithUI UP at http://www.keithui.com (127.0.0.2:80)" -ForegroundColor Green
} else {
    Write-Host "KeithUI not responding yet; check C:\ClaudeCore\logs\keithui.err.log." -ForegroundColor Yellow
}
