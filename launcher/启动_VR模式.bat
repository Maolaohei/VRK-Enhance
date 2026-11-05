@echo off
setlocal enableextensions
chcp 936 >nul 2>nul
title VR Kanojo - VR mode

set "GAME=E:\SteamLibrary\steamapps\common\VR_Kanojo"
set "SYS=%SystemRoot%\System32"
set "VRKANOJO_VR=1"

echo [1/4] checking SteamVR (vrserver.exe) ...
"%SYS%\tasklist.exe" /FI "IMAGENAME eq vrserver.exe" 2>nul | "%SYS%\find.exe" /I "vrserver.exe" >nul
if not errorlevel 1 goto steamvr_ok

if defined VRK_DRY (
    echo [DRY] SteamVR is not running - would start it via steam://rungameid/250820 and wait 90s.
    exit /b 0
)
echo       not running - asking Steam to start SteamVR ...
start "" "steam://rungameid/250820"

set /a _wait=0
:wait_steamvr
"%SYS%\ping.exe" -n 3 127.0.0.1 >nul
"%SYS%\tasklist.exe" /FI "IMAGENAME eq vrserver.exe" 2>nul | "%SYS%\find.exe" /I "vrserver.exe" >nul
if not errorlevel 1 goto steamvr_ok
set /a _wait+=3
if %_wait% LSS 90 goto wait_steamvr

echo.
echo [ERROR] SteamVR did not start within 90 seconds.
echo         Launching the game now would freeze it for about 100 seconds
echo         while Unity waits for the VR runtime, so it is aborted.
echo         Start SteamVR manually, then run this file again.
echo.
pause
exit /b 2

:steamvr_ok
echo       SteamVR is running.

echo [2/4] enabling OpenVR plugin ...
if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll.disabled" (
    if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll" del /f /q "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll"
    ren "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll.disabled" "openvr_api.dll"
)

if defined VRK_DRY (
    echo [DRY] would launch VR_Kanojo_fix.exe now - stopping here.
    exit /b 0
)
echo [3/4] launching VR_Kanojo_fix.exe (VR mode) ...
if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_fix.exe" (
    pushd "E:\SteamLibrary\steamapps\common\VR_Kanojo"
    "VR_Kanojo_fix.exe"
    popd
) else (
    if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo.exe" (
        pushd "E:\SteamLibrary\steamapps\common\VR_Kanojo"
        "VR_Kanojo.exe"
        popd
    ) else (
        echo [ERROR] neither VR_Kanojo_fix.exe nor VR_Kanojo.exe found.
        pause
        exit /b 3
    )
)

echo [4/4] restoring OpenVR plugin (flat mode wants it disabled) ...
if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll" (
    if exist "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll.disabled" del /f /q "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll.disabled"
    ren "E:\SteamLibrary\steamapps\common\VR_Kanojo\VR_Kanojo_Data\Plugins\openvr_api.dll" "openvr_api.dll.disabled"
)

echo done.
exit /b 0
