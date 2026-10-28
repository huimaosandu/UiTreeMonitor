@echo off
rem ============================================
rem  UI Tree Monitor (C#/WPF) launcher
rem  .NET SDK is installed at D:\dotnet (the one on PATH
rem  at C:\Program Files\dotnet has runtime only, no SDK)
rem ============================================
rem 先结束旧实例：运行中的应用会锁定 bin\Debug 下的 DLL，
rem 不关掉会导致 dotnet run 重新构建时报 MSB3026/MSB3027 文件锁定失败
taskkill /F /IM UiTreeMonitor.App.exe >nul 2>&1
D:\dotnet\dotnet.exe run --project src\UiTreeMonitor.App
pause
