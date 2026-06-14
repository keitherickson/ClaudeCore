@echo off
title Publish KeithVision
echo Publishing the latest KeithVision build and relaunching...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-keithvision.ps1"
echo.
echo Done. You can close this window.
pause
