# 生成 UiTreeMonitor 应用图标（程序化绘制，无需设计工具）：
#   结构树（根+三子节点）+ 信号波形 → 透明 PNG(1024) → 多尺寸 ICO(256/64/48/32/16)
# 用法: powershell -ExecutionPolicy Bypass -File make-icon.ps1
# 产物: app_icon_1024.png / app.ico（同目录）
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $outDir

# ---------- 1) 绘制 1024x1024 透明底图 ----------
$S = 1024
$bmp = New-Object System.Drawing.Bitmap($S, $S)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)                 # 左上角
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)   # 右上角
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)   # 右下角
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)    # 左下角
    $p.CloseFigure(); $p
}

# 圆角方形渐变底：深蓝 → 青（对角线方向）
$m = 48.0; $r = 200.0
$bg = New-RoundRect $m $m ($S - 2 * $m) ($S - 2 * $m) $r
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point($S, $S)),
    [System.Drawing.Color]::FromArgb(255, 27, 79, 138), [System.Drawing.Color]::FromArgb(255, 42, 161, 168))
$g.FillPath($brush, $bg)

# 结构树：根节点(上中) → 三个子节点，白色连线+节点
$white = [System.Drawing.Brushes]::White
$whitePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 20)
$whitePen.StartCap = 'Round'; $whitePen.EndCap = 'Round'
$root = New-Object System.Drawing.PointF(512, 320)
$kids = @(
    (New-Object System.Drawing.PointF(236, 588)),
    (New-Object System.Drawing.PointF(512, 636)),
    (New-Object System.Drawing.PointF(788, 588)))
foreach ($k in $kids) { $g.DrawLine($whitePen, $root.X, $root.Y + 40, $k.X, $k.Y - 42) }
$g.FillEllipse($white, ($root.X - 78), ($root.Y - 78), 156, 156)   # 根 r=78
foreach ($k in $kids) { $g.FillEllipse($white, ($k.X - 58), ($k.Y - 58), 116, 116) } # 子 r=58
# 节点内衬渐变蓝点，层次感
$inner = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(300, 200)), (New-Object System.Drawing.Point(724, 700)),
    [System.Drawing.Color]::FromArgb(255, 52, 120, 190), [System.Drawing.Color]::FromArgb(255, 60, 170, 178))
$g.FillEllipse($inner, ($root.X - 54), ($root.Y - 54), 108, 108)
foreach ($k in $kids) { $g.FillEllipse($inner, ($k.X - 38), ($k.Y - 38), 76, 76) }

# 信号波形：橙色正弦，横穿下半部
$wavePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 150, 70), 30)
$wavePen.StartCap = 'Round'; $wavePen.EndCap = 'Round'
$pts = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
$x0, $x1, $mid = 172.0, 852.0, 812.0
for ($i = 0; $i -le 120; $i++) {
    $t = $i / 120.0
    $x = $x0 + ($x1 - $x0) * $t
    $y = $mid - [Math]::Sin($t * [Math]::PI * 4) * 92 * (0.55 + 0.45 * [Math]::Sin($t * [Math]::PI))
    $pts.Add((New-Object System.Drawing.PointF($x, $y)))
}
$g.DrawLines($wavePen, $pts.ToArray())
$g.Dispose()
$pngPath = Join-Path $outDir 'app_icon_1024.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "PNG -> $pngPath"

# ---------- 2) 生成多尺寸 ICO（PNG 条目，Vista+ 全支持） ----------
function Copy-Scaled([System.Drawing.Image]$img, [int]$size) {
    $b = New-Object System.Drawing.Bitmap($size, $size)   # 默认 32bppArgb，保留透明
    $g2 = [System.Drawing.Graphics]::FromImage($b)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g2.DrawImage($img, 0, 0, $size, $size)
    $g2.Dispose(); $b
}

$sizes = @(256, 64, 48, 32, 16)
$pngs = @()
foreach ($s in $sizes) {
    $scaled = Copy-Scaled $bmp $s
    $ms = New-Object System.IO.MemoryStream
    $scaled.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $scaled.Dispose()
    $pngs += , $ms.ToArray()
}
$bmp.Dispose()

$ico = New-Object System.IO.MemoryStream
$bw = {
    param($stream, $bytes) $stream.Write($bytes, 0, $bytes.Length)
}
# ICONDIR: reserved=0, type=1(icon), count=N
$ico.Write([BitConverter]::GetBytes([uint16]0), 0, 2)
$ico.Write([BitConverter]::GetBytes([uint16]1), 0, 2)
$ico.Write([BitConverter]::GetBytes([uint16]$sizes.Count), 0, 2)
# ICONDIRENTRY x N（数据区紧跟目录之后）
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $ico.WriteByte([byte]$dim); $ico.WriteByte([byte]$dim)   # width / height（0 表示 256）
    $ico.WriteByte(0); $ico.WriteByte(0)                     # colorCount / reserved
    $ico.Write([BitConverter]::GetBytes([uint16]1), 0, 2)    # planes
    $ico.Write([BitConverter]::GetBytes([uint16]32), 0, 2)   # bitcount
    $ico.Write([BitConverter]::GetBytes([uint32]$pngs[$i].Length), 0, 4)
    $ico.Write([BitConverter]::GetBytes([uint32]$offset), 0, 4)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $ico.Write($p, 0, $p.Length) }
$icoPath = Join-Path $outDir 'app.ico'
[System.IO.File]::WriteAllBytes($icoPath, $ico.ToArray())
Write-Host "ICO -> $icoPath ($($sizes -join ',') px, $(Get-Item $icoPath).Length bytes)"
