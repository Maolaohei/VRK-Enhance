@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "tools/configgui.ps1" %*
if errorlevel 1 pause
endlocal
