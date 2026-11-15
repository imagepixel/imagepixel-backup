# Draws the pictures the icons for macOS and Linux are made of, from the hand of the
# Imagepixel logo:
#
#   installer/macos/app-icon-1024.png   the hand on a white tile with rounded corners,
#                                       the shape macOS expects of an application icon
#   installer/linux/icons/<size>.png    the hand alone, in the sizes desktops ask for
#
# The results are checked in. Run this script again only when the logo changes.
#
# Requirements: Windows PowerShell 5.1 or later on Windows.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$logoPath = Join-Path $root 'logo-imagepixel.png'

# the left hand of the logo, in pixels of logo-imagepixel.png
$hand = New-Object System.Drawing.Rectangle 146, 70, 862, 838

function New-RoundedRectangle([single] $x, [single] $y, [single] $width, [single] $height, [single] $radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $width - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $width - $d, $y + $height - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Canvas([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    return $bitmap, $graphics
}

# Draws the hand into the middle of a canvas, as large as fits into a square of the
# given size without changing its proportions.
function Add-Hand($graphics, $logo, [int] $canvasSize, [single] $handSize) {
    $scale = [Math]::Min($handSize / $hand.Width, $handSize / $hand.Height)
    $width = $hand.Width * $scale
    $height = $hand.Height * $scale
    $destination = New-Object System.Drawing.RectangleF (($canvasSize - $width) / 2), (($canvasSize - $height) / 2), $width, $height
    $source = New-Object System.Drawing.RectangleF $hand.X, $hand.Y, $hand.Width, $hand.Height
    $graphics.DrawImage($logo, $destination, $source, [System.Drawing.GraphicsUnit]::Pixel)
}

$logo = [System.Drawing.Bitmap]::FromFile($logoPath)
try {
    # --- macOS ---------------------------------------------------------------------
    $size = 1024
    $tile = 824          # the tile leaves room for its shadow, as the icons of macOS do
    $radius = 185
    $icon, $graphics = New-Canvas $size
    $origin = ($size - $tile) / 2

    # a soft shadow below the tile, built from layers that grow and fade
    for ($i = 14; $i -ge 1; $i--) {
        $grow = $i * 2
        $shadow = New-RoundedRectangle ($origin - $grow) ($origin - $grow + 12) ($tile + 2 * $grow) ($tile + 2 * $grow) ($radius + $grow)
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(5, 0, 0, 0))
        $graphics.FillPath($brush, $shadow)
        $brush.Dispose()
        $shadow.Dispose()
    }

    $shape = New-RoundedRectangle $origin $origin $tile $tile $radius
    $graphics.FillPath([System.Drawing.Brushes]::White, $shape)
    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(26, 0, 0, 0)), 1.5
    $graphics.DrawPath($border, $shape)
    $border.Dispose()
    $shape.Dispose()

    Add-Hand $graphics $logo $size 610
    $target = Join-Path $PSScriptRoot 'macos\app-icon-1024.png'
    $icon.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $icon.Dispose()
    Write-Host "Written: $target"

    # --- Linux ---------------------------------------------------------------------
    $directory = Join-Path $PSScriptRoot 'linux\icons'
    New-Item -ItemType Directory -Force $directory | Out-Null
    foreach ($size in 48, 64, 128, 256, 512) {
        $icon, $graphics = New-Canvas $size
        Add-Hand $graphics $logo $size ($size * 0.94)
        $target = Join-Path $directory "$size.png"
        $icon.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $icon.Dispose()
        Write-Host "Written: $target"
    }
}
finally {
    $logo.Dispose()
}
