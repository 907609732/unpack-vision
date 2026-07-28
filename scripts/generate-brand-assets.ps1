[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = [System.IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Source image not found: $sourcePath"
}

$desktopAssets = Join-Path $repositoryRoot 'src\UnpackVision.App\Assets'
$androidResources = Join-Path $repositoryRoot 'mobile\UnpackVision.Android\app\src\main\res'
$readmeAssets = Join-Path $repositoryRoot 'docs\assets'
New-Item -ItemType Directory -Force -Path $desktopAssets, $readmeAssets | Out-Null

function New-TransparentMaster([string]$path) {
    $sourceBitmap = [System.Drawing.Bitmap]::new($path)
    try {
        $result = [System.Drawing.Bitmap]::new(
            $sourceBitmap.Width,
            $sourceBitmap.Height,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        for ($y = 0; $y -lt $sourceBitmap.Height; $y++) {
            for ($x = 0; $x -lt $sourceBitmap.Width; $x++) {
                $pixel = $sourceBitmap.GetPixel($x, $y)
                $greenDominance = $pixel.G - [Math]::Max($pixel.R, $pixel.B)
                if ($pixel.G -gt 135 -and $greenDominance -gt 55) {
                    $alpha = [Math]::Max(0, [Math]::Min(255, 255 - (($greenDominance - 55) * 3)))
                    $result.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $pixel.R, [Math]::Min($pixel.G, [Math]::Max($pixel.R, $pixel.B)), $pixel.B))
                }
                else {
                    $result.SetPixel($x, $y, $pixel)
                }
            }
        }
        return $result
    }
    finally {
        $sourceBitmap.Dispose()
    }
}

function Save-ResizedPng(
    [System.Drawing.Bitmap]$source,
    [int]$size,
    [string]$destination,
    [System.Drawing.Color]$background = [System.Drawing.Color]::Transparent
) {
    $directory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear($background)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Write-MultiSizeIcon([System.Drawing.Bitmap]$source, [string]$destination) {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()
    try {
        foreach ($size in $sizes) {
            $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images += ,@($size, $stream.ToArray())
            $stream.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }

        $file = [System.IO.File]::Create($destination)
        $writer = [System.IO.BinaryWriter]::new($file)
        try {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$images.Count)
            $offset = 6 + (16 * $images.Count)
            foreach ($image in $images) {
                $size = [int]$image[0]
                $bytes = [byte[]]$image[1]
                $dimension = if ($size -ge 256) { 0 } else { $size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$bytes.Length)
                $writer.Write([UInt32]$offset)
                $offset += $bytes.Length
            }
            foreach ($image in $images) {
                $writer.Write([byte[]]$image[1])
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $images = @()
    }
}

$master = New-TransparentMaster $sourcePath
try {
    $masterPath = Join-Path $desktopAssets 'EcommerceUnpackRecorder-Logo.png'
    Save-ResizedPng $master 1024 $masterPath
    Save-ResizedPng $master 512 (Join-Path $readmeAssets 'logo.png')
    Write-MultiSizeIcon $master (Join-Path $desktopAssets 'EcommerceUnpackRecorder.ico')

    $densitySizes = @{
        'mipmap-mdpi' = 48
        'mipmap-hdpi' = 72
        'mipmap-xhdpi' = 96
        'mipmap-xxhdpi' = 144
        'mipmap-xxxhdpi' = 192
    }
    foreach ($entry in $densitySizes.GetEnumerator()) {
        $directory = Join-Path $androidResources $entry.Key
        Save-ResizedPng $master $entry.Value (Join-Path $directory 'ic_launcher.png')
        Save-ResizedPng $master $entry.Value (Join-Path $directory 'ic_launcher_round.png')
    }
    Save-ResizedPng $master 432 (Join-Path $androidResources 'drawable-nodpi\ic_launcher_foreground.png')
}
finally {
    $master.Dispose()
}

Write-Output "Generated desktop, Android, and README assets from $sourcePath"
