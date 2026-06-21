# One-time elevated installer for hosting KeithUI at http://www.keithui.com on
# this box (local only). Mirrors the KeithVision setup:
#   1. hosts entry  127.0.0.2 www.keithui.com keithui.com
#   2. "KeithUI Startup" logon scheduled task -> tools\start-keithui.ps1
# Idempotent: safe to re-run. Writes a result log for the (non-elevated) caller.
$ErrorActionPreference = "Stop"
$log = "C:\ClaudeCore\logs\keithui-install.log"
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
function Note($m) { $line = "[" + (Get-Date -Format "HH:mm:ss") + "] " + $m; Add-Content -Path $log -Value $line }
Set-Content -Path $log -Value "KeithUI host install"

try {
    # --- 1. hosts entry ---------------------------------------------------
    $hosts = "$env:windir\System32\drivers\etc\hosts"
    $content = Get-Content -Path $hosts -Raw
    if ($content -match "(?im)^\s*[\d.]+\s+.*keithui\.com") {
        Note "hosts: entry for keithui.com already present - left as-is"
    } else {
        if ($content.Length -gt 0 -and -not $content.EndsWith("`n")) { Add-Content -Path $hosts -Value "" }
        Add-Content -Path $hosts -Value "# KeithUI local app"
        Add-Content -Path $hosts -Value "127.0.0.2`twww.keithui.com keithui.com"
        Note "hosts: added 127.0.0.2 www.keithui.com keithui.com"
    }

    # --- 2. logon scheduled task -----------------------------------------
    $me = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "C:\ClaudeCore\ClaudeCore\tools\start-keithui.ps1"'
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $me
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    $settings  = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName "KeithUI Startup" -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Description "Starts the KeithUI node-graph studio at logon (http://www.keithui.com)." -Force | Out-Null
    Note "task: 'KeithUI Startup' registered for $me (logon, RunLevel Limited)"

    Note "DONE OK"
} catch {
    Note ("FAILED: " + $_.Exception.Message)
}
