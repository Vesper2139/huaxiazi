param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\generated-images\0fcea388-63ed-46c4-90d6-60d06c159d5e.png'),
    [string]$OutputPng = (Join-Path $PSScriptRoot '..\Resources\Brand\Huaxiazi-256.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot '..\Resources\Brand\Huaxiazi.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Save-PngBytes([System.Drawing.Bitmap]$Bitmap, [string]$Path) {
    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    } finally {
        $stream.Dispose()
    }
}

function Resize-PngBytes([System.Drawing.Bitmap]$SourceBitmap, [int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($SourceBitmap, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))

        # Reapply the tile mask after downsampling. Bicubic filtering can otherwise
        # reintroduce translucent white corner pixels at shell icon sizes.
        $left = [Math]::Max(1, [Math]::Round($Size * 5 / 256))
        $top = [Math]::Max(1, [Math]::Round($Size * 5 / 256))
        $right = $Size - 1 - $left
        $bottom = $Size - 1 - $top
        $radius = [Math]::Max(1, [Math]::Round($Size * 50 / 256))
        $radiusSquared = $radius * $radius
        for ($x = 0; $x -lt $Size; $x++) {
            for ($y = 0; $y -lt $Size; $y++) {
                $inside = $true
                if ($x -lt ($left + $radius) -and $y -lt ($top + $radius)) {
                    $dx = ($left + $radius) - $x
                    $dy = ($top + $radius) - $y
                    $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
                } elseif ($x -gt ($right - $radius) -and $y -lt ($top + $radius)) {
                    $dx = $x - ($right - $radius)
                    $dy = ($top + $radius) - $y
                    $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
                } elseif ($x -lt ($left + $radius) -and $y -gt ($bottom - $radius)) {
                    $dx = ($left + $radius) - $x
                    $dy = $y - ($bottom - $radius)
                    $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
                } elseif ($x -gt ($right - $radius) -and $y -gt ($bottom - $radius)) {
                    $dx = $x - ($right - $radius)
                    $dy = $y - ($bottom - $radius)
                    $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
                } elseif ($x -lt $left -or $x -gt $right -or $y -lt $top -or $y -gt $bottom) {
                    $inside = $false
                }

                if (-not $inside) { $bitmap.SetPixel($x, $y, [System.Drawing.Color]::Transparent) }
            }
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $stream.ToArray()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Convert-PngToDib([byte[]]$PngBytes, [int]$Size) {
    $pngStream = [System.IO.MemoryStream]::new($PngBytes, $false)
    $bitmap = [System.Drawing.Bitmap]::new($pngStream)
    $xorStride = $Size * 4
    $andStride = [int]([Math]::Ceiling($Size / 32.0) * 4)
    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        # BITMAPINFOHEADER; the doubled height reserves the second half for the AND mask.
        $writer.Write([uint32]40)
        $writer.Write([int32]$Size)
        $writer.Write([int32]($Size * 2))
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]0)
        $writer.Write([uint32]($xorStride * $Size))
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([uint32]0)
        $writer.Write([uint32]0)

        for ($y = $Size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $Size; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.A -ge 128) {
                    $writer.Write([byte]$pixel.B)
                    $writer.Write([byte]$pixel.G)
                    $writer.Write([byte]$pixel.R)
                    $writer.Write([byte]255)
                } else {
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                }
            }
        }

        for ($y = $Size - 1; $y -ge 0; $y--) {
            $maskRow = [byte[]]::new($andStride)
            for ($x = 0; $x -lt $Size; $x++) {
                if ($bitmap.GetPixel($x, $y).A -lt 128) {
                    $byteIndex = [int][Math]::Floor($x / 8.0)
                    $maskRow[$byteIndex] = [byte]($maskRow[$byteIndex] -bor (1 -shl (7 - ($x % 8))))
                }
            }
            $writer.Write($maskRow)
        }

        return [byte[]]$stream.ToArray()
    } finally {
        $writer.Dispose()
        $stream.Dispose()
        $bitmap.Dispose()
        $pngStream.Dispose()
    }
}

$sourceBitmap = [System.Drawing.Bitmap]::new((Resolve-Path $Source).Path)
$cleanBitmap = [System.Drawing.Bitmap]::new($sourceBitmap.Width, $sourceBitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($x = 0; $x -lt $sourceBitmap.Width; $x++) {
    for ($y = 0; $y -lt $sourceBitmap.Height; $y++) {
        $cleanBitmap.SetPixel($x, $y, $sourceBitmap.GetPixel($x, $y))
    }
}
$sourceBitmap.Dispose()

# The supplied master contains a near-black matte outside the rounded white tile.
# Remove only connected near-black matte pixels; coloured artwork and white tile remain unchanged.
$width = $cleanBitmap.Width
$height = $cleanBitmap.Height
$visited = New-Object 'bool[,]' $width, $height
$queue = [System.Collections.Generic.Queue[System.Drawing.Point]]::new()
for ($x = 0; $x -lt $width; $x++) {
    $queue.Enqueue([System.Drawing.Point]::new($x, 0))
    $queue.Enqueue([System.Drawing.Point]::new($x, $height - 1))
}
for ($y = 1; $y -lt ($height - 1); $y++) {
    $queue.Enqueue([System.Drawing.Point]::new(0, $y))
    $queue.Enqueue([System.Drawing.Point]::new($width - 1, $y))
}

while ($queue.Count -gt 0) {
    $point = $queue.Dequeue()
    $x = $point.X
    $y = $point.Y
    if ($x -lt 0 -or $x -ge $width -or $y -lt 0 -or $y -ge $height -or $visited[$x, $y]) { continue }
    $visited[$x, $y] = $true
    $pixel = $cleanBitmap.GetPixel($x, $y)
    if ([Math]::Max($pixel.R, [Math]::Max($pixel.G, $pixel.B)) -gt 110) { continue }
    $cleanBitmap.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
    $queue.Enqueue([System.Drawing.Point]::new($x + 1, $y))
    $queue.Enqueue([System.Drawing.Point]::new($x - 1, $y))
    $queue.Enqueue([System.Drawing.Point]::new($x, $y + 1))
    $queue.Enqueue([System.Drawing.Point]::new($x, $y - 1))
}

# Make the outside of the rounded tile genuinely transparent. Semi-transparent
# white corners are rendered as dark fringes by the Windows shell on some themes.
$left = 5
$top = 5
$right = $width - 6
$bottom = $height - 6
$radius = 50
$radiusSquared = $radius * $radius
for ($x = 0; $x -lt $width; $x++) {
    for ($y = 0; $y -lt $height; $y++) {
        $inside = $true
        if ($x -lt ($left + $radius) -and $y -lt ($top + $radius)) {
            $dx = ($left + $radius) - $x
            $dy = ($top + $radius) - $y
            $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
        } elseif ($x -gt ($right - $radius) -and $y -lt ($top + $radius)) {
            $dx = $x - ($right - $radius)
            $dy = ($top + $radius) - $y
            $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
        } elseif ($x -lt ($left + $radius) -and $y -gt ($bottom - $radius)) {
            $dx = ($left + $radius) - $x
            $dy = $y - ($bottom - $radius)
            $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
        } elseif ($x -gt ($right - $radius) -and $y -gt ($bottom - $radius)) {
            $dx = $x - ($right - $radius)
            $dy = $y - ($bottom - $radius)
            $inside = (($dx * $dx) + ($dy * $dy)) -le $radiusSquared
        } elseif ($x -lt $left -or $x -gt $right -or $y -lt $top -or $y -gt $bottom) {
            $inside = $false
        }

        if (-not $inside) { $cleanBitmap.SetPixel($x, $y, [System.Drawing.Color]::Transparent) }
    }
}

Save-PngBytes $cleanBitmap (Join-Path (Split-Path $OutputPng) (Split-Path $OutputPng -Leaf))

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
try {
    foreach ($size in $sizes) {
        $pngFrame = [byte[]](Resize-PngBytes $cleanBitmap $size)
        $frames += ,([byte[]](Convert-PngToDib $pngFrame $size))
    }
} finally {
    $cleanBitmap.Dispose()
}

$headerSize = 6 + (16 * $frames.Count)
$offset = $headerSize
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $size = $sizes[$i]
        $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    [System.IO.File]::WriteAllBytes((Join-Path (Split-Path $OutputIco) (Split-Path $OutputIco -Leaf)), $stream.ToArray())
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Output "Generated transparent Huaxiazi icon: $OutputPng and $OutputIco"
