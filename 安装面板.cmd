@echo off
chcp 65001 >nul
setlocal
set "HERE=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%tools\gui.ps1" %*
if errorlevel 1 pause
endlocal
