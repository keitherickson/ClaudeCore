#Requires -Version 5.1
<#
.SYNOPSIS
    Re-publish the KeithVision app so the logon auto-start picks up code changes.

.DESCRIPTION
    The "KeithVision Startup" scheduled task runs the PUBLISHED build at
    C:\ClaudeCore\KeithVision, not the live source. Run this after code changes:
    it stops the running published app (to free locked files), publishes a fresh
    Release build, then relaunches the services.

.PARAMETER NoRestart
    Publish only; do not relaunch the app afterward.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\publish-keithvision.ps1
#>
[CmdletBinding()]
param(
    [string]$Project    = "C:\ClaudeCore\ClaudeCore\ClaudeCore.csproj",
    [string]$PublishDir = "C:\ClaudeCore\KeithVision",
    [switch]$NoRestart
)
$ErrorActionPreference = "Stop"

Write-Host "Stopping running published app (to free locked files)..." -ForegroundColor Cyan
Get-Process -Name "ClaudeCore" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "Publishing Release build to $PublishDir ..." -ForegroundColor Cyan
dotnet publish $Project -c Release -o $PublishDir --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
Write-Host "Publish complete." -ForegroundColor Green

if ($NoRestart) {
    Write-Host "Skipping relaunch (-NoRestart). It will start at next logon, or run tools\start-keithvision.ps1." -ForegroundColor Yellow
    return
}

$toolsDir = if ($PSScriptRoot) { $PSScriptRoot } else { "C:\ClaudeCore\ClaudeCore\tools" }
$launcher = Join-Path $toolsDir "start-keithvision.ps1"
Write-Host "Relaunching services..." -ForegroundColor Cyan
& $launcher

$up = $false
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 2
    try { Invoke-WebRequest "http://127.0.0.1:5080/" -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop | Out-Null; $up = $true; break } catch { }
}
if ($up) {
    Write-Host "Web app UP at http://127.0.0.1:5080" -ForegroundColor Green
} else {
    Write-Host "Web app not responding yet; check C:\ClaudeCore\logs." -ForegroundColor Yellow
}
