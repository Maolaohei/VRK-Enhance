@echo off
setlocal enabledelayedexpansion
rem ============================================================================
rem  Run every static verifier against the built plugins in dist\.
rem  Each verifier is a plain .NET 8 console app that reads the DLL with
rem  Mono.Cecil and asserts behaviour WITHOUT launching the game.
rem
rem  Usage:  build.cmd  &&  verify.cmd
rem ============================================================================
set "REPO=%~dp0"
set "DIST=%REPO%dist"
set "FAILED=0"

if not exist "%DIST%\VRK_FlatFPS.dll" (
  echo [ERR] run build.cmd first
  exit /b 1
)

for %%P in (VRK_FlatFPS VRK_UpScale VRK_Perf VRK_PreWarm VRK_TexPlus VRK_Unlock AwakeCheck PerfAudit) do (
  set "PROJ="
  if exist "%REPO%plugins\%%P\_verify\*.csproj" set "PROJ=%REPO%plugins\%%P\_verify"
  if exist "%REPO%tools\%%P\*.csproj"          set "PROJ=%REPO%tools\%%P"
  if not defined PROJ (
    echo [SKIP] %%P - no verifier project
  ) else (
    echo.
    echo === %%P verifier ===
    pushd "!PROJ!"
    dotnet build -c Release -v q --nologo || set FAILED=1
    popd
  )
)

echo.
echo === FlatFPS (needs the game's Assembly-CSharp.dll) ===
set "MANAGED=%~dp0..\VR_Kanojo_Data\Managed\Assembly-CSharp.dll"
"%REPO%plugins\VRK_FlatFPS\_verify\bin\Release\net8.0\verify.exe" "%MANAGED%" "%DIST%\VRK_FlatFPS.dll" || set FAILED=1

echo.
echo === UpScale ===
set "VRKUP=%DIST%\vrkup.dll"
if not exist "%VRKUP%" set "VRKUP=%~dp0..\VR_Kanojo_Data\Plugins\vrkup.dll"
"%REPO%plugins\VRK_UpScale\_verify\bin\Release\net8.0\verify.exe" "%DIST%\VRK_UpScale.dll" "%VRKUP%" || set FAILED=1

echo.
echo === Perf ===
"%REPO%plugins\VRK_Perf\_verify\bin\Release\net8.0\perfverify.exe" "%DIST%\VRK_Perf.dll" || set FAILED=1

echo.
echo === PreWarm ===
"%REPO%plugins\VRK_PreWarm\_verify\bin\Release\net8.0\prewarmverify.exe" "%DIST%\VRK_PreWarm.dll" || set FAILED=1

echo.
echo === TexPlus ===
"%REPO%plugins\VRK_TexPlus\_verify\bin\Release\net8.0\texverify.exe" "%DIST%\VRK_TexPlus.dll" || set FAILED=1

echo.
echo === Unlock ===
"%REPO%plugins\VRK_Unlock\_verify\bin\Release\net8.0\unlockverify.exe" "%DIST%\VRK_Unlock.dll" || set FAILED=1

echo.
echo === AwakeCheck (Awake must not touch graphics/VR APIs) ===
set "AWFILES="
for %%F in ("%DIST%\VRK_*.dll") do call set "AWFILES=%%AWFILES%% "%%~fF""
"%REPO%tools\AwakeCheck\bin\Release\net8.0\awakecheck.exe" %AWFILES% || set FAILED=1

echo.
if "%FAILED%"=="0" (echo ALL VERIFIERS PASSED) else (echo SOME VERIFIERS FAILED ^(see above^))
exit /b %FAILED%
