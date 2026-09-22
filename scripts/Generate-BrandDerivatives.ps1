[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This script requires Windows and System.Drawing. No network calls or image generation are performed.'
}

Add-Type -AssemblyName System.Drawing

$imageDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'images'
$masterPath = Join-Path $imageDirectory 'jev-mark-1024.png'
$navy = [System.Drawing.ColorTranslator]::FromHtml('#0B1020')
$teal = [System.Drawing.ColorTranslator]::FromHtml('#2DD4BF')
$violet = [System.Drawing.ColorTranslator]::FromHtml('#A78BFA')
$white = [System.Drawing.ColorTranslator]::FromHtml('#F5F7FF')
$muted = [System.Drawing.ColorTranslator]::FromHtml('#B9C6DE')
$border = [System.Drawing.ColorTranslator]::FromHtml('#27324A')

function New-Canvas {
    param([int]$Width, [int]$Height)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    return $bitmap
}

function New-Graphics {
    param([System.Drawing.Bitmap]$Bitmap)

    $graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    $graphics.Clear($navy)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $graphics
}

function Draw-Text {
    param(
        [System.Drawing.Graphics]$Graphics,
        [string]$Text,
        [single]$Size,
        [single]$X,
        [single]$Y,
        [single]$MaximumWidth,
        [System.Drawing.Color]$Color,
        [switch]$Strong
    )

    $familyName = if ($Strong) { 'Segoe UI Semibold' } else { 'Segoe UI' }
    $font = [System.Drawing.Font]::new(
        $familyName, $Size, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = [System.Drawing.SolidBrush]::new($Color)
    $format = [System.Drawing.StringFormat]::GenericTypographic.Clone()
    try {
        if ($font.FontFamily.Name -ne $familyName) {
            throw "Required font '$familyName' is not installed. Refusing a non-deterministic font substitution."
        }
        $format.FormatFlags = $format.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap
        $bounds = $Graphics.MeasureString($Text, $font, [int]::MaxValue, $format)
        if ($bounds.Width -gt $MaximumWidth) {
            throw "Text '$Text' exceeds its safe layout width ($($bounds.Width) > $MaximumWidth)."
        }
        $Graphics.DrawString($Text, $font, $brush, [System.Drawing.PointF]::new($X, $Y), $format)
    }
    finally {
        $format.Dispose()
        $brush.Dispose()
        $font.Dispose()
    }
}

function Draw-Emblem {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Bitmap]$Emblem,
        [int]$X,
        [int]$Y,
        [int]$Size
    )

    if ($Size -gt $Emblem.Width) {
        throw 'Do not upscale the emblem; use the high-resolution master.'
    }
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $Graphics.DrawImage(
            $Emblem, [System.Drawing.Rectangle]::new($X, $Y, $Size, $Size),
            0, 0, $Emblem.Width, $Emblem.Height,
            [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    }
    finally {
        $attributes.Dispose()
    }
}

function Save-Asset {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Name)

    $path = Join-Path $imageDirectory $Name
    $Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function New-Icon {
    param([System.Drawing.Bitmap]$Emblem, [int]$Size, [string]$Name)

    $bitmap = New-Canvas $Size $Size
    $graphics = New-Graphics $bitmap
    try {
        Draw-Emblem $graphics $Emblem 0 0 $Size
        Save-Asset $bitmap $Name
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function New-Banner {
    param([System.Drawing.Bitmap]$Emblem, [switch]$Social)

    $width = if ($Social) { 1280 } else { 1600 }
    $height = if ($Social) { 640 } else { 900 }
    $margin = if ($Social) { 80 } else { 112 }
    $accentY = if ($Social) { 88 } else { 136 }
    $titleY = if ($Social) { 208 } else { 302 }
    $titleSize = if ($Social) { 62 } else { 82 }
    $taglineY = if ($Social) { 300 } else { 420 }
    $taglineSize = if ($Social) { 31 } else { 40 }
    $textWidth = if ($Social) { 748 } else { 820 }
    $badgeY = if ($Social) { 382 } else { 522 }
    $emblemX = if ($Social) { 868 } else { 1000 }
    $emblemY = if ($Social) { 132 } else { 226 }
    $emblemSize = if ($Social) { 352 } else { 488 }
    $footerY = if ($Social) { 532 } else { 766 }
    $name = if ($Social) { 'social-preview.png' } else { 'repo-hero.png' }

    $bitmap = New-Canvas $width $height
    $graphics = New-Graphics $bitmap
    $tealBrush = [System.Drawing.SolidBrush]::new($teal)
    $violetBrush = [System.Drawing.SolidBrush]::new($violet)
    $badgeBrush = [System.Drawing.SolidBrush]::new(
        [System.Drawing.ColorTranslator]::FromHtml('#142436'))
    $borderPen = [System.Drawing.Pen]::new($border, 1)
    try {
        $graphics.FillRectangle($tealBrush, $margin, $accentY, 56, 5)
        $graphics.FillRectangle($violetBrush, ($margin + 64), $accentY, 24, 5)
        Draw-Text $graphics 'COMMUNITY SDK' 18 $margin ($accentY + 31) $textWidth $muted -Strong
        Draw-Text $graphics 'ElBruno.AI.Jev' $titleSize ($margin - 4) $titleY $textWidth $white -Strong
        Draw-Text $graphics 'Typed decisions for Jev AI' $taglineSize $margin $taglineY $textWidth $muted

        $graphics.FillRectangle($badgeBrush, $margin, $badgeY, 148, 48)
        Draw-Text $graphics '.NET 10' 25 ($margin + 22) ($badgeY + 8) 110 $teal -Strong
        Draw-Emblem $graphics $Emblem $emblemX $emblemY $emblemSize

        $graphics.DrawLine($borderPen, $margin, ($footerY - 26), ($width - $margin), ($footerY - 26))
        Draw-Text $graphics 'Independent community SDK' 20 $margin $footerY ($width - 2 * $margin) $muted
        Save-Asset $bitmap $name
    }
    finally {
        $borderPen.Dispose()
        $badgeBrush.Dispose()
        $violetBrush.Dispose()
        $tealBrush.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $masterPath -PathType Leaf)) {
    throw "Missing master image: $masterPath. Follow images\README.md before generating derivatives."
}

$master = [System.Drawing.Bitmap]::new($masterPath)
$emblem = $null
try {
    if ($master.Width -ne 1024 -or $master.Height -ne 1024) {
        throw 'The master must be 1024 x 1024 pixels.'
    }
    if ($master.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) {
        throw 'The master must be a PNG image.'
    }

    # Trim excess generated padding without redrawing the original emblem.
    $emblem = $master.Clone(
        [System.Drawing.Rectangle]::new(112, 96, 800, 800),
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    for ($y = 0; $y -lt $emblem.Height; $y++) {
        for ($x = 0; $x -lt $emblem.Width; $x++) {
            $pixel = $emblem.GetPixel($x, $y)
            # Flatten only the generated dark matte; retain colored artwork and edge pixels.
            if ($pixel.R -le 48 -and $pixel.G -le 48 -and $pixel.B -le 48) {
                $emblem.SetPixel($x, $y, $navy)
            }
        }
    }

    New-Icon $emblem 128 'nuget-icon.png'
    New-Icon $emblem 64 'jev-icon-64.png'
    New-Banner $emblem
    New-Banner $emblem -Social
}
finally {
    if ($null -ne $emblem) { $emblem.Dispose() }
    $master.Dispose()
}

$expected = @(
    @{ Name = 'jev-mark-1024.png'; Width = 1024; Height = 1024 },
    @{ Name = 'nuget-icon.png'; Width = 128; Height = 128 },
    @{ Name = 'jev-icon-64.png'; Width = 64; Height = 64 },
    @{ Name = 'repo-hero.png'; Width = 1600; Height = 900 },
    @{ Name = 'social-preview.png'; Width = 1280; Height = 640 }
)

foreach ($asset in $expected) {
    $path = Join-Path $imageDirectory $asset.Name
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $signature = [BitConverter]::ToString($bytes, 0, 8)
    if ($signature -ne '89-50-4E-47-0D-0A-1A-0A') {
        throw "Invalid PNG signature: $($asset.Name)"
    }
    $image = [System.Drawing.Bitmap]::new($path)
    try {
        if ($image.Width -ne $asset.Width -or $image.Height -ne $asset.Height) {
            throw "Incorrect dimensions: $($asset.Name)"
        }
        if ($asset.Name -eq 'nuget-icon.png' -and $bytes.Length -ge 1000000) {
            throw 'The NuGet icon must be smaller than 1 MB.'
        }
        [pscustomobject]@{
            Asset = $asset.Name
            Dimensions = "$($image.Width)x$($image.Height)"
            Bytes = $bytes.Length
            Signature = $signature
        }
    }
    finally {
        $image.Dispose()
    }
}
