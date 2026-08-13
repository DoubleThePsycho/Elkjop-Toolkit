@echo off
setlocal

set "ARCH=%PROCESSOR_ARCHITEW6432%"
if "%ARCH%"=="" set "ARCH=%PROCESSOR_ARCHITECTURE%"

if /i "%ARCH%"=="AMD64" set "TARGET=Support Tool-x64.exe"
if /i "%ARCH%"=="ARM64" set "TARGET=Support Tool-arm64.exe"
if /i "%ARCH%"=="x86"   set "TARGET=Support Tool-x86.exe"

if not defined TARGET (
    echo Unsupported or unrecognized processor architecture: %ARCH%
    pause & exit /b 1
)

set "TARGETPATH=%~dp0Elkjop Toolkit\Support Tool\%TARGET%"

if not exist "%TARGETPATH%" (
    echo Could not find "%TARGETPATH%"
    pause & exit /b 1
)

start "" "%TARGETPATH%"
