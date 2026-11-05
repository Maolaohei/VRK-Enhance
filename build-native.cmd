@echo off
setlocal
rem ============================================================================
rem  Build the native FSR1 upscaler (vrkup.dll, x64 D3D11 Unity plugin).
rem  Needs MinGW-w64 g++ on PATH (tested with gcc 15.2.0 / x86_64-w64-mingw32).
rem  Shaders are already compiled into shader_blobs.h; rebuild them with
rem  build-shaders.cmd if you edit the .hlsl files.
rem ============================================================================
set "NATIVE=%~dp0plugins\VRK_UpScale\native"
set "OUT=%~dp0dist"

if not exist "%OUT%" mkdir "%OUT%"

g++ -m64 -shared -O2 -std=c++14 -fno-exceptions -fno-rtti ^
    -o "%OUT%\vrkup.dll" ^
    "%NATIVE%\vrkup.cpp" ^
    -ld3d11 -ldxgi -luuid -lole32 -static-libgcc -static-libstdc++ || exit /b 1

echo.
echo Built "%OUT%\vrkup.dll"
echo Install to:  <game>\VR_Kanojo_Data\Plugins\vrkup.dll
endlocal
