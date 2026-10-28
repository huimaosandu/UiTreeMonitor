@echo off
rem UI Tree Monitor launcher
rem .NET SDK: D:\dotnet (PATH dotnet has runtime only)
taskkill /F /IM UiTreeMonitor.App.exe >nul 2>&1
D:\dotnet\dotnet.exe run --project src\UiTreeMonitor.App
pause
