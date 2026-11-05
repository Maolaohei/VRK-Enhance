@echo off
setlocal enabledelayedexpansion
rem ============================================================================
rem  VRK-Enhance - build all managed plugins
rem
rem  Requirements: .NET SDK (any 6/7/8 - we only use its bundled Roslyn csc.exe)
rem                the game installed (we reference its Managed assemblies)
rem
rem  Usage:  build.cmd            (game dir defaults to the parent of this repo)
rem          set GAMEDIR=D:\...   build.cmd
rem ============================================================================

if not defined GAMEDIR set "GAMEDIR=%~dp0.."
set "GAMEDIR=%GAMEDIR:\=/%"

set "MANAGED=%GAMEDIR%/VR_Kanojo_Data/Managed"
set "BEPCORE=%GAMEDIR%/BepInEx/core"
set "OUT=%~dp0dist"
set "REPO=%~dp0"

if not exist "%MANAGED%/mscorlib.dll" (
  echo [ERR] cannot find "%MANAGED%/mscorlib.dll"
  echo       set GAMEDIR to the VR Kanojo install directory and retry.
  exit /b 1
)
if not exist "%BEPCORE%/BepInEx.dll" (
  echo [ERR] cannot find "%BEPCORE%/BepInEx.dll"
  echo       install BepInEx 5.4.12 x64 into the game first.
  exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"

rem --- locate the newest installed SDK and use its Roslyn -------------------------------------
set "SDKVER="
for /f "tokens=1 delims= " %%A in ('dotnet --list-sdks 2^>nul') do set "SDKVER=%%A"
if not defined SDKVER (
  echo [ERR] dotnet SDK not found. install .NET SDK 6 or newer.
  exit /b 1
)
set "CSC=%ProgramFiles%\dotnet\sdk\%SDKVER%\Roslyn\bincore\csc.dll"
if not exist "%CSC%" (
  echo [ERR] csc.dll not found at "%CSC%"
  exit /b 1
)
echo Using Roslyn from SDK %SDKVER%

set "R=-noconfig -nostdlib+ -target:library -langversion:6 -optimize+ -nologo"
set "REFS=-r:%MANAGED%/mscorlib.dll -r:%MANAGED%/System.dll -r:%MANAGED%/System.Core.dll -r:%MANAGED%/UnityEngine.dll -r:%MANAGED%/UnityEngine.UI.dll -r:%MANAGED%/Assembly-CSharp.dll -r:%BEPCORE%/BepInEx.dll -r:%BEPCORE%/0Harmony.dll"

echo.
echo === VRK_FlatFPS ===
dotnet "%CSC%" %R% -out:"%OUT%\VRK_FlatFPS.dll" %REFS% ^
  -resource:%REPO%fixtures/crosshair_128.bin,VRKanojo.FlatFPS.crosshair.bin ^
  "%REPO%plugins/VRK_FlatFPS/VRK_FlatFPS.cs" || exit /b 1

echo === VRK_UpScale ===
dotnet "%CSC%" %R% -out:"%OUT%\VRK_UpScale.dll" %REFS% ^
  "%REPO%plugins/VRK_UpScale/VRK_UpScale.cs" || exit /b 1

echo === VRK_Perf ===
dotnet "%CSC%" %R% -out:"%OUT%\VRK_Perf.dll" %REFS% ^
  "%REPO%plugins/VRK_Perf/VRK_Perf.cs" || exit /b 1

echo === VRK_PreWarm ===
dotnet "%CSC%" %R% -out:"%OUT%\VRK_PreWarm.dll" %REFS% ^
  "%REPO%plugins/VRK_PreWarm/VRK_PreWarm.cs" || exit /b 1

echo === VRK_TexPlus ===
dotnet "%CSC%" %R% -out:"%OUT%\VRK_TexPlus.dll" %REFS% ^
  "%REPO%plugins/VRK_TexPlus/VRK_TexPlus.cs" || exit /b 1

echo === VRK_Unlock ===
dotnet "%CSC%" %R% -out:"%OUT%\VRK_Unlock.dll" ^
  -r:%MANAGED%/mscorlib.dll -r:%MANAGED%/System.dll -r:%MANAGED%/System.Core.dll ^
  -r:%MANAGED%/UnityEngine.dll -r:%BEPCORE%/BepInEx.dll ^
  "%REPO%plugins/VRK_Unlock/VRK_Unlock.cs" || exit /b 1

echo.
echo Built into "%OUT%":
dir /b "%OUT%"
echo.
echo Next:  copy dist\*.dll  to  "%GAMEDIR%\BepInEx\plugins\"
echo        copy the native vrkup.dll (see build-native.cmd) to "%GAMEDIR%\VR_Kanojo_Data\Plugins\"
endlocal
