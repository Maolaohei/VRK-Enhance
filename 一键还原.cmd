@echo off
chcp 65001 >nul
setlocal
rem 一键还原（交互式）：按 state\manifest.json 撤销安装
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools/setup.ps1" -Mode restore
echo.
pause
endlocal
