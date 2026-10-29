# UiTreeMonitor 独立 exe 打包脚本
# 产物:
#   dist\publish\            原始发布目录
#   dist\UiTreeMonitor.exe   自包含单文件（目标机无需安装 .NET，双击即用）
#   dist\UiTreeMonitor_win-x64_<时间戳>.zip  分发压缩包
# 用法: powershell -ExecutionPolicy Bypass -File publish.ps1  （或双击 publish.cmd）
param(
    [string]$Runtime = 'win-x64'   # 目标平台，如 win-x64 / win-arm64
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# ---- 1) 定位带 SDK 的 dotnet（PATH 中的可能只有运行时） ----
$dotnet = $null
$candidates = @()
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $candidates += $cmd.Source }
$candidates += 'D:\dotnet\dotnet.exe'
$candidates += "$env:ProgramFiles\dotnet\dotnet.exe"
foreach ($c in ($candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)) {
    $sdks = & $c --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $c; break }
}
if (-not $dotnet) {
    Write-Host 'ERROR: 未找到 .NET SDK（dotnet --list-sdks 为空）。请安装 .NET 8 SDK 后重试。' -ForegroundColor Red
    exit 1
}
Write-Host "使用 SDK: $dotnet"

# ---- 2) 停止运行中的实例（防文件占用） ----
Get-Process -Name 'UiTreeMonitor.App', 'UiTreeMonitor' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

# ---- 3) 发布：Release + 自包含 + 单文件 + 压缩 + 原生库内嵌 ----
$pubDir = Join-Path $root 'dist\publish'
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
Write-Host "发布中（$Runtime, self-contained single-file）..."
& $dotnet publish (Join-Path $root 'src\UiTreeMonitor.App\UiTreeMonitor.App.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $pubDir
if ($LASTEXITCODE -ne 0) { Write-Host '发布失败。' -ForegroundColor Red; exit 1 }

# ---- 4) 整理产物：exe 提到 dist 根 + 打 zip ----
$srcExe = Join-Path $pubDir 'UiTreeMonitor.App.exe'
if (-not (Test-Path $srcExe)) { Write-Host "ERROR: 未找到 $srcExe" -ForegroundColor Red; exit 1 }
$finalExe = Join-Path $root 'dist\UiTreeMonitor.exe'
Copy-Item $srcExe $finalExe -Force
$zip = Join-Path $root ('dist\UiTreeMonitor_{0}_{1:yyyyMMdd_HHmm}.zip' -f $Runtime, (Get-Date))
Compress-Archive -Path $finalExe -DestinationPath $zip -Force

Write-Host ''
Write-Host '打包完成：' -ForegroundColor Green
Write-Host ('  单文件 exe : {0} ({1} MB)' -f $finalExe, [math]::Round((Get-Item $finalExe).Length / 1MB, 1))
Write-Host ('  分发 zip   : {0}' -f $zip)
Write-Host '目标机无需安装 .NET 运行时，双击 exe 即可运行。'
