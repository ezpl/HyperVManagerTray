<#
.SYNOPSIS
    Generates Assets\app.ico — the static icon embedded in the exe and used by the Start Menu shortcut.

.DESCRIPTION
    Renders the same VM-monitor glyph as Helpers\IconGenerator.cs (blue background, green connection
    dot = bridged/default state) using System.Drawing (GDI+) and writes a Vista+ PNG-in-ICO file
    with frames at 64, 48, 32, 24, 20, and 16 px.

    Called automatically by build-installer.ps1 if Assets\app.ico is absent.  Run manually after
    any icon design change and commit the resulting Assets\app.ico.
#>
param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path)

Add-Type -AssemblyName System.Drawing

$background = [System.Drawing.Color]::FromArgb(255, 0x00, 0x78, 0xD7)   # Windows blue
$dotColor   = [System.Drawing.Color]::FromArgb(255, 0x10, 0xB9, 0x81)   # Green (bridged)
$sizes      = @(64, 48, 32, 24, 20, 16)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d    = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size, [System.Drawing.Color]$dot) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size,
               [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 16.0, $size / 16.0)

    # Blue rounded background
    $bgPath  = New-RoundedPath 0.5 0.5 15.0 15.0 3.2
    $bgBrush = New-Object System.Drawing.SolidBrush($background)
    $g.FillPath($bgBrush, $bgPath)
    $bgPath.Dispose(); $bgBrush.Dispose()

    $pen           = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.1)
    $pen.LineJoin  = [System.Drawing.Drawing2D.LineJoin]::Round
    $dotBrush      = New-Object System.Drawing.SolidBrush($dot)

    # VM monitor frame (rounded rect, white stroke)
    $monPath = New-RoundedPath 2.0 1.5 12.0 8.5 1.2
    $g.DrawPath($pen, $monPath)
    $monPath.Dispose()

    # Two screen content lines
    $g.DrawLine($pen, [float]3.5, [float]4.0, [float]12.5, [float]4.0)
    $g.DrawLine($pen, [float]3.5, [float]6.0, [float]9.0,  [float]6.0)

    # Stand stub
    $g.DrawLine($pen, [float]8.0, [float]10.0, [float]8.0, [float]11.5)

    # Connection dot: green = bridged/default
    $g.FillEllipse($dotBrush, [float]5.8, [float]11.5, [float]4.4, [float]4.4)
    $g.DrawEllipse($pen,      [float]5.8, [float]11.5, [float]4.4, [float]4.4)

    $pen.Dispose(); $dotBrush.Dispose(); $g.Dispose()
    return $bmp
}

# Render each size as PNG bytes
$frames = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz $dotColor
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , $ms.ToArray()
}

# Write ICO file (Vista+ PNG-in-ICO format)
$assetsDir = Join-Path $ProjectRoot "Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir | Out-Null }
$icoPath = Join-Path $assetsDir "app.ico"

$fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([int16]0)                       # reserved
$bw.Write([int16]1)                       # type: icon
$bw.Write([int16]$sizes.Length)           # image count

$dataOffset = 6 + $sizes.Length * 16
for ($i = 0; $i -lt $sizes.Length; $i++) {
    $sz = $sizes[$i]
    $wh = [byte]$sz           # all our sizes are <256; 0 would encode 256
    $bw.Write($wh)  # width
    $bw.Write($wh)  # height
    $bw.Write([byte]0)                    # colour count (0 = true-colour)
    $bw.Write([byte]0)                    # reserved
    $bw.Write([int16]1)                   # colour planes
    $bw.Write([int16]32)                  # bits per pixel
    $bw.Write([int]$frames[$i].Length)    # data size
    $bw.Write([int]$dataOffset)           # data offset in file
    $dataOffset += $frames[$i].Length
}
foreach ($frame in $frames) { $bw.Write($frame) }
$bw.Dispose(); $fs.Dispose()

Write-Host "Generated: $icoPath" -ForegroundColor Green
