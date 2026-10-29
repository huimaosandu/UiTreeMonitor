@echo off
rem UI Tree Monitor launcher - requires .NET 8 SDK
rem Prefers dotnet from PATH; falls back to D:\dotnet if PATH has runtime only
taskkill /F /IM UiTreeMonitor.App.exe >nul 2>&1

set "DOTNET_CMD="
for /f "delims=" %%i in ('dotnet --list-sdks 2^>nul') do if not defined DOTNET_CMD set "DOTNET_CMD=dotnet"
if defined DOTNET_CMD goto :run
if exist "D:\dotnet\dotnet.exe" set "DOTNET_CMD=D:\dotnet\dotnet.exe"
if not defined DOTNET_CMD (
    echo [error] .NET 8 SDK not found - install from https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)
:run
%DOTNET_CMD% run --project src\UiTreeMonitor.App
pause
