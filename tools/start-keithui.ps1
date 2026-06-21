# KeithUI startup launcher.
# Started by the "KeithUI Startup" scheduled task at logon. Launches the KeithUI
# web app (the node-graph studio) in the background (hidden) if its port isn't
# already listening. KeithUI reuses KeithVision.Core and shares the SAME backend
# servers (LTX :8765, ComfyUI :8188, audio :8770) and GPU that the KeithVision
# startup task brings up -- so this script only needs to start the web app, not
# the backends.
#
# Binds 127.0.0.2:80 (see KeithUI\appsettings.json -> Kestrel:Web) so it can sit
# on port 80 next to KeithVision (127.0.0.1:80) without a conflict; the hosts
# entry "127.0.0.2 www.keithui.com" makes http://www.keithui.com resolve here.
$ErrorActionPreference = "SilentlyContinue"

$AppDir = "C:\ClaudeCore\KeithUI"
$AppExe = Join-Path $AppDir "KeithUI.exe"
$LogDir = "C:\ClaudeCore\logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Address-specific: KeithVision already listens on 127.0.0.1:80, so a bare
# port-80 check would always say "up". We care only about KeithUI's own endpoint.
function Endpoint-Listening($address, $port) {
    [bool](Get-NetTCPConnection -State Listen -LocalAddress $address -LocalPort $port -ErrorAction SilentlyContinue)
}

# KeithUI web app (endpoints from appsettings.json -> Kestrel: 127.0.0.2:80 + 127.0.0.1:5099).
if ((Test-Path $AppExe) -and -not (Endpoint-Listening "127.0.0.2" 80) -and -not (Endpoint-Listening "127.0.0.1" 5099)) {
    Start-Process -FilePath $AppExe -WindowStyle Hidden `
        -WorkingDirectory $AppDir `
        -RedirectStandardOutput (Join-Path $LogDir "keithui.out.log") `
        -RedirectStandardError  (Join-Path $LogDir "keithui.err.log")
}
