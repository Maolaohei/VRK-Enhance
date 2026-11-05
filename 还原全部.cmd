@echo off
chcp 65001 >nul
setlocal
rem 还原全部：按 state\manifest.json 删除已装文件、回滚被覆盖的文件、把 .disabled 改回来
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools/setup.ps1" -Mode restore -Yes
echo.
pause
endlocal
