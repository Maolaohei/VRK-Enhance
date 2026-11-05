@echo off
setlocal enableextensions
chcp 936 >nul 2>nul
title VR Kanojo - flat (no VR) mode

set "GAME=E:\SteamLibrary\steamapps\common\VR_Kanojo"
set "VRKANOJO_VR="

echo [1/3] disabling OpenVR plugin (avoids a ~100s freeze without a VR runtime) ...
if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll" (
    if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll.disabled" del /f /q "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll.disabled"
    ren "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll" "openvr_api.dll.disabled"
)

echo [2/3] launching VR_Kanojo_fix.exe (flat mode) ...
if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_fix.exe" (
    pushd "E:\SteamLibrary\steamapps\common\VR_Kanojo"
    "VR_Kanojo_fix.exe"
    popd
) else (
    pushd "E:\SteamLibrary\steamapps\common\VR_Kanojo"
    "VR_Kanojo.exe"
    popd
)

echo [3/3] done.
exit /b 0
