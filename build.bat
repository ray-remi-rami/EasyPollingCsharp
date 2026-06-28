@echo off
REM Build FastInputNative.dll (x86_64, statically linked) for Unity.
REM Requires MSYS2 UCRT64 g++ on PATH or at the default location below.

set GPP=C:\msys64\ucrt64\bin\g++.exe
if not exist "%GPP%" set GPP=g++

"%GPP%" -shared -O2 -static -static-libgcc -static-libstdc++ ^
    -o "%~dp0dll\FastInputNative.dll" ^
    "%~dp0src\FastInputWindows.cpp" ^
    -luser32 -lkernel32 -lwinmm

if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

REM Copy into the Unity project so the plugin is updated in one step.
copy /Y "%~dp0dll\FastInputNative.dll" "C:\InputTest\Assets\Plugins\FastInputNative.dll"
echo BUILD OK
