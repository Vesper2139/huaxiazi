param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('SkinAssetRaster' -as [type])) {
    Add-Type -ReferencedAssemblies ([System.Drawing.Bitmap].Assembly.Location) -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class SkinAssetRaster
{
    private static void Enqueue(int x, int y, int width, int height, bool[] visited, int[] queue, ref int tail)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        var index = y * width + x;
        if (visited[index]) return;
        visited[index] = true;
        queue[tail++] = index;
    }

    private static bool IsMatte(int index, int width, int stride, byte[] pixels, bool dark)
    {
        var offset = (index / width) * stride + (index % width) * 4;
        var b = pixels[offset];
        var g = pixels[offset + 1];
        var r = pixels[offset + 2];
        var a = pixels[offset + 3];
        if (a == 0) return true;
        if (dark) return Math.Max(r, Math.Max(g, b)) <= 48;
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        return min >= 238 && max - min <= 24;
    }

    public static void RemoveSmallOpaqueComponents(Bitmap bitmap, int minArea)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * height;
            var pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, length);
            var visited = new bool[width * height];
            var queue = new int[width * height];
            var component = new int[width * height];
            var componentCount = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var start = y * width + x;
                    if (visited[start] || pixels[(start / width) * data.Stride + (start % width) * 4 + 3] < 64) continue;
                    var head = 0;
                    var tail = 0;
                    queue[tail++] = start;
                    visited[start] = true;
                    componentCount = 0;
                    while (head < tail)
                    {
                        var index = queue[head++];
                        component[componentCount++] = index;
                        var cx = index % width;
                        var cy = index / width;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                var nx = cx + dx;
                                var ny = cy + dy;
                                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                                var next = ny * width + nx;
                                var alpha = pixels[(next / width) * data.Stride + (next % width) * 4 + 3];
                                if (visited[next] || alpha < 64) continue;
                                visited[next] = true;
                                queue[tail++] = next;
                            }
                        }
                    }
                    if (componentCount < minArea)
                    {
                        for (var i = 0; i < componentCount; i++)
                        {
                            var index = component[i];
                            pixels[(index / width) * data.Stride + (index % width) * 4 + 3] = 0;
                        }
                    }
                }
            }
            Marshal.Copy(pixels, 0, data.Scan0, length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    public static void RemoveConnectedMatte(Bitmap bitmap, bool dark)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * height;
            var pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, length);
            var visited = new bool[width * height];
            var queue = new int[width * height];
            var head = 0;
            var tail = 0;

            for (var x = 0; x < width; x++) { Enqueue(x, 0, width, height, visited, queue, ref tail); Enqueue(x, height - 1, width, height, visited, queue, ref tail); }
            for (var y = 1; y < height - 1; y++) { Enqueue(0, y, width, height, visited, queue, ref tail); Enqueue(width - 1, y, width, height, visited, queue, ref tail); }

            while (head < tail)
            {
                var index = queue[head++];
                if (!IsMatte(index, width, data.Stride, pixels, dark)) continue;
                var offset = (index / width) * data.Stride + (index % width) * 4;
                pixels[offset + 3] = 0;
                var x = index % width;
                var y = index / width;
                Enqueue(x + 1, y, width, height, visited, queue, ref tail);
                Enqueue(x - 1, y, width, height, visited, queue, ref tail);
                Enqueue(x, y + 1, width, height, visited, queue, ref tail);
                Enqueue(x, y - 1, width, height, visited, queue, ref tail);
            }

            Marshal.Copy(pixels, 0, data.Scan0, length);
        }
        finally { bitmap.UnlockBits(data); }
    }
}
"@
}

# App icons and floating-ball companions intentionally have different sources.
# IconSource is the supplied close-up reference; CompanionSource is the supplied
# full character sheet. This prevents the shell icon from being reused as the
# animated floating-ball artwork.
$skins = @(
    @{
        Id = 'YongWeiXiaoFei'
        IconName = 'YongWeiXiaoFei'
        IconSource = 'C:\Users\lenovo\AppData\Local\Temp\codex-clipboard-69bff46e-afc8-4e45-a94d-562d64c38dbf.png'
        CompanionSource = 'C:\Users\lenovo\AppData\Local\Temp\codex-clipboard-54f619e4-cddd-499c-be4a-45be7ec9cd56.png'
        CompanionCrops = @(
            @{ X = 125; Y = 80; Width = 460; Height = 820 }
            @{ X = 590; Y = 75; Width = 430; Height = 825 }
            @{ X = 1010; Y = 390; Width = 330; Height = 490 }
            @{ X = 590; Y = 75; Width = 430; Height = 825 }
        )
    }
    @{
        Id = 'StarSailor'
        IconName = 'StarSailor'
        IconSource = 'C:\Users\lenovo\AppData\Local\Temp\codex-clipboard-b836c1fa-c370-4f15-868f-240bc1cd2835.png'
        CompanionSource = 'C:\Users\lenovo\AppData\Local\Temp\codex-clipboard-a5a704a3-14af-40bd-8035-cc402c227dc6.png'
        CompanionCrops = @(
            @{ X = 35; Y = 100; Width = 540; Height = 680 }
            @{ X = 540; Y = 100; Width = 480; Height = 680 }
            @{ X = 1000; Y = 90; Width = 500; Height = 690 }
            @{ X = 35; Y = 100; Width = 540; Height = 680 }
        )
    }
    @{
        Id = 'BlueWhaleMaid'
        IconName = 'BlueWhaleMaid'
        # Keep the whole latest square reference; do not crop away the face.
        IconSource = 'C:\Users\lenovo\AppData\Local\Temp\codex-clipboard-96018efc-494d-4026-9099-60c3d281cd49.png'
        CompanionSource = 'C:\Users\lenovo\AppData\Local\Temp\codex-clipboard-387e60a9-11f1-4129-88a8-f80cfa3e300e.jpg'
        IconMask = @{ X = 1260; Y = 1390; Width = 276; Height = 146 }
        CompanionCrops = @(
            @{ X = 350; Y = 0; Width = 645; Height = 690 }
            @{ X = 420; Y = 680; Width = 570; Height = 759 }
            @{ X = 0; Y = 0; Width = 410; Height = 680 }
            @{ X = 0; Y = 680; Width = 440; Height = 759 }
        )
    }
)

$legacyIcons = @(
    @{ Id = 'LuoXiaoHei'; Source = (Join-Path $ProjectRoot 'Resources\Skins\LuoXiaoHei\Idle.png') }
    @{ Id = 'MaoDie'; Source = (Join-Path $ProjectRoot 'Resources\Skins\MaoDie\Idle.png') }
)

function Save-Png([System.Drawing.Bitmap]$Bitmap, [string]$Path) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Test-MattePixel([System.Drawing.Color]$Pixel, [ValidateSet('Dark', 'Light')][string]$Mode) {
    if ($Pixel.A -eq 0) { return $true }
    if ($Mode -eq 'Dark') {
        return [Math]::Max($Pixel.R, [Math]::Max($Pixel.G, $Pixel.B)) -le 48
    }

    $spread = [Math]::Max($Pixel.R, [Math]::Max($Pixel.G, $Pixel.B)) - [Math]::Min($Pixel.R, [Math]::Min($Pixel.G, $Pixel.B))
    return [Math]::Min($Pixel.R, [Math]::Min($Pixel.G, $Pixel.B)) -ge 238 -and $spread -le 24
}

function Remove-ConnectedMatte([System.Drawing.Bitmap]$Bitmap, [ValidateSet('Dark', 'Light')][string]$Mode) {
    [SkinAssetRaster]::RemoveConnectedMatte($Bitmap, $Mode -eq 'Dark')
}

function New-ScaledBitmap([System.Drawing.Bitmap]$Source, [int]$Width, [int]$Height, [int]$Padding) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $scale = [Math]::Min(($Width - (2 * $Padding)) / [double]$Source.Width, ($Height - (2 * $Padding)) / [double]$Source.Height)
        $drawWidth = [Math]::Max(1, [Math]::Round($Source.Width * $scale))
        $drawHeight = [Math]::Max(1, [Math]::Round($Source.Height * $scale))
        $left = [Math]::Round(($Width - $drawWidth) / 2)
        $top = [Math]::Round(($Height - $drawHeight) / 2)
        $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new($left, $top, $drawWidth, $drawHeight))
    } finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function New-NormalizedCompanion([System.Drawing.Bitmap]$Source, [hashtable]$Crop) {
    $cropBitmap = [System.Drawing.Bitmap]::new($Crop.Width, $Crop.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($cropBitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Crop.Width, $Crop.Height), $Crop.X, $Crop.Y, $Crop.Width, $Crop.Height, [System.Drawing.GraphicsUnit]::Pixel)
    } finally {
        $graphics.Dispose()
    }
    Remove-ConnectedMatte $cropBitmap 'Light'
    $normalized = New-ScaledBitmap $cropBitmap 512 512 18
    $cropBitmap.Dispose()
    [SkinAssetRaster]::RemoveSmallOpaqueComponents($normalized, 48)
    return $normalized
}

function New-NormalizedIcon([hashtable]$Skin) {
    $source = [System.Drawing.Bitmap]::new((Resolve-Path $Skin.IconSource).Path)
    $clean = [System.Drawing.Bitmap]::new($source.Width, $source.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($clean)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.DrawImage($source, 0, 0, $source.Width, $source.Height)
    } finally {
        $graphics.Dispose()
        $source.Dispose()
    }
    if ($Skin.ContainsKey('IconMask')) {
        $mask = $Skin.IconMask
        for ($x = [Math]::Max(0, $mask.X); $x -lt [Math]::Min($clean.Width, $mask.X + $mask.Width); $x++) {
            for ($y = [Math]::Max(0, $mask.Y); $y -lt [Math]::Min($clean.Height, $mask.Y + $mask.Height); $y++) {
                $clean.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
            }
        }
    }
    $normalized = New-ScaledBitmap $clean 512 512 0
    $clean.Dispose()
    # Flood-fill after normalization so the expensive connected-component pass
    # never scans the original 1K/1.5K source dimensions.
    Remove-ConnectedMatte $normalized 'Dark'
    return $normalized
}

function New-FrameSheet([System.Drawing.Bitmap[]]$Frames, [string]$Path, [int]$Columns, [int]$Rows) {
    $tileSize = 256
    $sheet = [System.Drawing.Bitmap]::new($tileSize * $Columns, $tileSize * $Rows, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        for ($index = 0; $index -lt ($Columns * $Rows); $index++) {
            $frame = New-ScaledBitmap $Frames[$index % $Frames.Count] $tileSize $tileSize 8
            try {
                $column = $index % $Columns
                $row = [Math]::Floor($index / $Columns)
                $graphics.DrawImage($frame, $column * $tileSize, $row * $tileSize, $tileSize, $tileSize)
            } finally {
                $frame.Dispose()
            }
        }
    } finally {
        $graphics.Dispose()
    }
    Save-Png $sheet $Path
    $sheet.Dispose()
}

$iconGenerator = Join-Path $PSScriptRoot 'generate-brand-icon.ps1'
$brandRoot = Join-Path $ProjectRoot 'Resources\Brand\Skins'
New-Item -ItemType Directory -Force -Path $brandRoot | Out-Null

foreach ($skin in $skins) {
    $skinRoot = Join-Path $ProjectRoot "Resources\Skins\$($skin.Id)"
    New-Item -ItemType Directory -Force -Path $skinRoot | Out-Null
    if (-not (Test-Path $skin.CompanionSource)) {
        # A missing clipboard/source image is not permission to mutate an
        # existing product asset with guessed rectangles. Keep the last
        # validated output unchanged and require a checked-in source for a
        # reproducible rebuild.
        Write-Warning "跳过 $($skin.Id)：找不到源素材 $($skin.CompanionSource)。现有规范化产物保持不变；请补充入库源图后再重建。"
        continue
    }
    $companionSource = [System.Drawing.Bitmap]::new((Resolve-Path $skin.CompanionSource).Path)
    $variants = @()
    try {
        foreach ($crop in $skin.CompanionCrops) {
            $variants += New-NormalizedCompanion -Source $companionSource -Crop $crop
        }
    } finally {
        $companionSource.Dispose()
    }

    try {
        Save-Png $variants[0] (Join-Path $skinRoot 'portrait.png')
        Save-Png $variants[0] (Join-Path $skinRoot 'companion.png')
        Save-Png $variants[0] (Join-Path $skinRoot 'preview.png')
        New-FrameSheet $variants (Join-Path $skinRoot 'companion-sprite-sheet.png') 4 3
        New-FrameSheet $variants (Join-Path $skinRoot 'companion-idle-variants.png') 4 1

        $iconMaster = New-NormalizedIcon $skin
        try {
            $iconSourcePath = Join-Path $skinRoot 'app-icon-source.png'
            Save-Png $iconMaster $iconSourcePath
            & $iconGenerator -Source $iconSourcePath -OutputPng (Join-Path $brandRoot "$($skin.IconName).png") -OutputIco (Join-Path $brandRoot "$($skin.IconName).ico")
        } finally {
            $iconMaster.Dispose()
        }
    } finally {
        foreach ($variant in $variants) { $variant.Dispose() }
    }
}

foreach ($legacy in $legacyIcons) {
    & $iconGenerator -Source $legacy.Source -OutputPng (Join-Path $brandRoot "$($legacy.Id).png") -OutputIco (Join-Path $brandRoot "$($legacy.Id).ico")
}

Write-Output 'Generated separate companion sheets from full character references and app icons from exact close-up references.'
