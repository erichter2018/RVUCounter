# PowerShell script to generate a simple application icon
# Uses .NET System.Drawing to create a basic icon

Add-Type -AssemblyName System.Drawing

function Create-IconBitmap {
    param([int]$size)

    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Background colors
    $bgColor1 = [System.Drawing.Color]::FromArgb(255, 26, 82, 118)  # Teal
    $bgColor2 = [System.Drawing.Color]::FromArgb(255, 20, 55, 90)   # Dark blue

    # Create gradient brush
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, $bgColor1, $bgColor2, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal
    )

    # Draw circular background
    $g.FillEllipse($brush, 1, 1, $size - 2, $size - 2)

    # Draw border
    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 255, 255, 255), [Math]::Max(1, $size / 32))
    $g.DrawEllipse($borderPen, 2, 2, $size - 4, $size - 4)

    $cx = $size / 2
    $cy = $size / 2

    # Draw scan arcs
    $scanPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 200, 230, 255), [Math]::Max(2, $size / 20))

    $innerR = $size * 0.25
    $g.DrawArc($scanPen, $cx - $innerR, $cy - $innerR, $innerR * 2, $innerR * 2, -135, 90)

    $outerR = $size * 0.35
    $g.DrawArc($scanPen, $cx - $outerR, $cy - $outerR, $outerR * 2, $outerR * 2, 45, 90)

    # Draw chart bars
    $chartBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 100, 200, 150))
    $barWidth = [Math]::Max(2, $size * 0.06)
    $barSpacing = $size * 0.08
    $baseY = $cy + $size * 0.15
    $barX = $cx - $size * 0.12

    $barHeights = @($size * 0.12, $size * 0.18, $size * 0.24)
    for ($i = 0; $i -lt 3; $i++) {
        $x = $barX + ($i * $barSpacing)
        $h = $barHeights[$i]
        $g.FillRectangle($chartBrush, $x, $baseY - $h, $barWidth, $h)
    }

    # Cleanup
    $brush.Dispose()
    $borderPen.Dispose()
    $scanPen.Dispose()
    $chartBrush.Dispose()
    $g.Dispose()

    return $bitmap
}

# Ensure Resources directory exists
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path $scriptDir)) {
    New-Item -ItemType Directory -Path $scriptDir -Force | Out-Null
}

# Generate icon at 256x256
$bitmap = Create-IconBitmap -size 256

# Save as PNG first (simpler)
$pngPath = Join-Path $scriptDir "app_preview.png"
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Preview saved: $pngPath"

# For the ICO, we need to save as icon
# Convert bitmap to icon
$iconPath = Join-Path $scriptDir "app.ico"
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())

# Save icon using a file stream
$fs = New-Object System.IO.FileStream($iconPath, [System.IO.FileMode]::Create)
$icon.Save($fs)
$fs.Close()

Write-Host "Icon saved: $iconPath"

$bitmap.Dispose()
