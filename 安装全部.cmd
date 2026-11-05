@echo off
chcp 65001 >nul
setlocal
rem 安装全部组件（含 unlock 解锁、devtools 关开发工具、openvr 免VR 禁用）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools/setup.ps1" -Components all -Yes
echo.
pause
endlocal
