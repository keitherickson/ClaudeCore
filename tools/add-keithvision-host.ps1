#Requires -Version 5.1
<#
.SYNOPSIS
    Map www.keithvision.com (and keithvision.com) to this PC. Run as Administrator.

.DESCRIPTION
    Adds a loopback entry to the Windows hosts file so that, on THIS machine,
    http://www.keithvision.com opens the locally running KeithVision app.
    The app must be listening on port 80 (it is, via appsettings.json -> Kestrel).
#>
$ErrorActionPreference = "Stop"

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) {
    Write-Host "This must run as Administrator. Right-click PowerShell > 'Run as administrator', then run this script." -ForegroundColor Red
    exit 1
}

$hosts = Join-Path $env:WINDIR "System32\drivers\etc\hosts"
$entry = "127.0.0.1`twww.keithvision.com keithvision.com"

if ((Get-Content $hosts) -match "keithvision\.com") {
    Write-Host "hosts already contains a keithvision.com entry; nothing to add." -ForegroundColor Yellow
} else {
    Add-Content -Path $hosts -Value "`r`n# KeithVision local app"
    Add-Content -Path $hosts -Value $entry
    Write-Host "Added to hosts: $entry" -ForegroundColor Green
}

ipconfig /flushdns | Out-Null
Write-Host "Done. On this PC, http://www.keithvision.com now points to the local app." -ForegroundColor Cyan
