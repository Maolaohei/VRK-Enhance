@echo off
chcp 65001 >nul
setlocal
rem 安装推荐组合：核心修复 + 超分 + 性能 + 预热 + 贴图 + 启动器
rem （不装 unlock，不动 openvr/devtools —— 免VR 卡顿由 启动_免VR模式.bat 自动处理）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools/setup.ps1" -Components flatfps,upscale,perf,prewarm,texplus,launcher -Yes
echo.
pause
endlocal
