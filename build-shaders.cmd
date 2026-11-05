@echo off
setlocal
rem ============================================================================
rem  Recompile the FSR1 shaders into shader_blobs.h (embed as byte arrays).
rem  Needs fxc.exe from the Windows SDK (10.x) and python 3.
rem ============================================================================
set "NATIVE=%~dp0plugins\VRK_UpScale\native"
set "FXC=%ProgramFiles(x86)%\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe"

if not exist "%FXC%" (
  for /f "delims=" %%D in ('dir /b /ad /o-n "%ProgramFiles(x86)%\Windows Kits\10\bin\10.*" 2^>nul') do (
    if not defined FXC2 set "FXC2=%ProgramFiles(x86)%\Windows Kits\10\bin\%%D\x64\fxc.exe"
  )
  if defined FXC2 set "FXC=%FXC2%"
)
if not exist "%FXC%" (
  echo [ERR] fxc.exe not found. install the Windows 10/11 SDK.
  exit /b 1
)

pushd "%NATIVE%"
"%FXC%" /nologo /T vs_5_0 /E main /Fo fullscreen.cso fullscreen.hlsl || goto :fail
"%FXC%" /nologo /T ps_5_0 /E main /Fo easu.cso       easu.hlsl       || goto :fail
"%FXC%" /nologo /T ps_5_0 /E main /Fo rcas.cso       rcas.hlsl       || goto :fail
popd

python "%~dp0plugins\VRK_UpScale\native\make_blobs.py" "%NATIVE%"
echo.
echo Regenerated "%NATIVE%\shader_blobs.h"
exit /b 0

:fail
popd
echo [ERR] shader compilation failed
exit /b 1
