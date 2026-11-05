@echo off
chcp 65001 >nul
setlocal
rem 一键安装（交互式）：勾选要装的组件
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools/setup.ps1"
echo.
pause
endlocal
