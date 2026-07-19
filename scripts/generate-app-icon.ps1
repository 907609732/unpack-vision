param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'src\UnpackVision.App\Assets'
$iconPath = Join-Path $assetDirectory 'UnpackVision.ico'
$pngPath = Join-Path $assetDirectory 'UnpackVision.png'
New-Item -ItemType Directory -Force -Path $assetDirectory | Out-Null

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$rounded = [System.Drawing.Drawing2D.GraphicsPath]::new()
$radius = 54
$diameter = $radius * 2
$rounded.AddArc(8, 8, $diameter, $diameter, 180, 90)
$rounded.AddArc($size - 8 - $diameter, 8, $diameter, $diameter, 270, 90)
$rounded.AddArc($size - 8 - $diameter, $size - 8 - $diameter, $diameter, $diameter, 0, 90)
$rounded.AddArc(8, $size - 8 - $diameter, $diameter, $diameter, 90, 90)
$rounded.CloseFigure()

$gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Point]::new(36, 20),
    [System.Drawing.Point]::new(220, 236),
    [System.Drawing.Color]::FromArgb(255, 30, 169, 255),
    [System.Drawing.Color]::FromArgb(255, 48, 84, 242))
$graphics.FillPath($gradient, $rounded)

$whitePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 10)
$whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$whitePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

$front = @(
    [System.Drawing.PointF]::new(54, 82),
    [System.Drawing.PointF]::new(126, 42),
    [System.Drawing.PointF]::new(198, 82),
    [System.Drawing.PointF]::new(126, 124),
    [System.Drawing.PointF]::new(54, 82))
$graphics.DrawLines($whitePen, $front)
$graphics.DrawLine($whitePen, 54, 82, 54, 161)
$graphics.DrawLine($whitePen, 54, 161, 126, 205)
$graphics.DrawLine($whitePen, 126, 124, 126, 205)
$graphics.DrawLine($whitePen, 198, 82, 198, 137)

$lensBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 30, 104, 246))
$graphics.FillEllipse($lensBrush, 151, 139, 78, 78)
$graphics.DrawEllipse($whitePen, 151, 139, 78, 78)
$graphics.FillEllipse([System.Drawing.Brushes]::White, 180, 168, 20, 20)

$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$fileStream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($fileStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Dispose()

$pngStream.Dispose()
$lensBrush.Dispose()
$whitePen.Dispose()
$gradient.Dispose()
$rounded.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Output $iconPath
Write-Output $pngPath
