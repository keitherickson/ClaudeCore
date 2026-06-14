# KeithVision startup launcher.
# Started by the "KeithVision Startup" scheduled task at logon. Launches the
# LTX-2.3 generation server and the web app in the background (hidden), each only
# if its port isn't already listening. Note: keep LTX Desktop closed so the 22B
# model fits in VRAM.
$ErrorActionPreference = "SilentlyContinue"

$AppDir    = "C:\ClaudeCore\KeithVision"
$AppExe    = Join-Path $AppDir "ClaudeCore.exe"
$Url       = "http://127.0.0.1:5080"
$LtxPy     = "C:\Users\keith\AppData\Local\LTXDesktop\python\python.exe"
$LtxLaunch = "C:\ClaudeCore\ClaudeCore\tools\ltx_launch.py"
$LogDir    = "C:\ClaudeCore\logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Port-Listening($port) {
    [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}

# 1. LTX-2.3 generation server on :8765
if ((Test-Path $LtxPy) -and -not (Port-Listening 8765)) {
    $env:LTX_APP_DATA_DIR = "C:\Users\keith\AppData\Local\LTXDesktop"
    $env:LTX_PORT = "8765"
    Start-Process -FilePath $LtxPy -ArgumentList "-u", $LtxLaunch -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $LogDir "ltx-server.out.log") `
        -RedirectStandardError  (Join-Path $LogDir "ltx-server.err.log")
}

# 2. KeithVision web app (ports come from appsettings.json -> Kestrel: 80 + 5080)
if ((Test-Path $AppExe) -and -not (Port-Listening 80) -and -not (Port-Listening 5080)) {
    Start-Process -FilePath $AppExe -WindowStyle Hidden `
        -WorkingDirectory $AppDir `
        -RedirectStandardOutput (Join-Path $LogDir "app.out.log") `
        -RedirectStandardError  (Join-Path $LogDir "app.err.log")
}
