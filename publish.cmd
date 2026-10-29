@echo off
rem UiTreeMonitor 独立 exe 打包入口（实际逻辑在 publish.ps1）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
pause
