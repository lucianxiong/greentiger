# Generates the GreenTiger brand icon (green tech tiger stripes).
# Outputs assets\GreenTiger.ico and ui\icon-192.png / ui\icon-512.png.
# ASCII only on purpose: Windows PowerShell 5.1 mis-reads non-ASCII .ps1 files.
[CmdletBinding()]
param(
    [string]$Out = "",
    [switch]$NoPng
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $root "assets\GreenTiger.ico" }
$dir = Split-Path -Parent $Out
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$sizes = @(16, 32, 48, 64, 128, 256)

function New-RoundedPath([float]$s, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $w = [float]$s
    $h = [float]$s
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
    $path.AddArc(0, $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-TigerStripe($path, [float]$s, [float]$x1, [float]$y1, [float]$x2, [float]$y2, [float]$w1, [float]$w2, [float]$curve) {
    # Tapered, slightly curved claw stripe. The curve and taper are what make it
    # read as a tiger mark instead of a generic diagonal bar.
    $points = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $n = 14
    for ($i = 0; $i -le $n; $i++) {
        $t = $i / $n
        $cx = ($x1 + ($x2 - $x1) * $t) * $s
        $cy = ($y1 + ($y2 - $y1) * $t) * $s + [Math]::Sin($t * [Math]::PI) * $curve * $s
        $tw = ($w1 + ($w2 - $w1) * $t) * $s
        $points.Add((New-Object System.Drawing.PointF([float]$cx, [float]($cy - $tw / 2))))
    }
    for ($i = $n; $i -ge 0; $i--) {
        $t = $i / $n
        $cx = ($x1 + ($x2 - $x1) * $t) * $s
        $cy = ($y1 + ($y2 - $y1) * $t) * $s + [Math]::Sin($t * [Math]::PI) * $curve * $s
        $tw = ($w1 + ($w2 - $w1) * $t) * $s
        $points.Add((New-Object System.Drawing.PointF([float]$cx, [float]($cy + $tw / 2))))
    }
    $path.AddPolygon($points.ToArray())
}

function New-BrandBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [float]([Math]::Max(2.0, $s * 0.23))
    $shell = New-RoundedPath $s $radius
    $rect = New-Object System.Drawing.RectangleF(0, 0, [float]$s, [float]$s)
    $baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 16, 61, 37),
        [System.Drawing.Color]::FromArgb(255, 5, 22, 14),
        52.0)
    $g.FillPath($baseBrush, $shell)

    # Thin mint rim: keeps the icon crisp on dark taskbars.
    $rim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(95, 126, 224, 150), [float]([Math]::Max(0.8, $s * 0.018)))
    $g.DrawPath($rim, $shell)

    $stripes = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-TigerStripe $stripes $s 0.17 0.28 0.61 0.39 0.085 0.018 0.035
    Add-TigerStripe $stripes $s 0.12 0.50 0.72 0.55 0.105 0.022 0.045
    Add-TigerStripe $stripes $s 0.22 0.73 0.58 0.71 0.080 0.016 0.030

    $stripeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 201, 255, 214),
        [System.Drawing.Color]::FromArgb(255, 63, 198, 112),
        20.0)
    $g.FillPath($stripeBrush, $stripes)

    # A faint circuit trace in the lower-right corner, visible at 32px+.
    if ($s -ge 32) {
        $circuit = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 146, 236, 166), [float]([Math]::Max(1.0, $s * 0.018)))
        $circuit.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $circuit.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($circuit, [float]($s * 0.68), [float]($s * 0.82), [float]($s * 0.83), [float]($s * 0.82))
        $g.DrawLine($circuit, [float]($s * 0.83), [float]($s * 0.82), [float]($s * 0.83), [float]($s * 0.70))
        $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(190, 201, 255, 214))
        $dotR = [float]([Math]::Max(1.0, $s * 0.026))
        $g.FillEllipse($dot, [float]($s * 0.83 - $dotR), [float]($s * 0.70 - $dotR), [float]($dotR * 2), [float]($dotR * 2))
        $circuit.Dispose()
        $dot.Dispose()
    }

    # Tiny diagonal sheen across the top-left.
    $sheen = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sheen.AddEllipse([float](-$s * 0.28), [float](-$s * 0.42), [float]($s * 0.95), [float]($s * 0.70))
    $sheenBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(24, 255, 255, 255))
    $g.FillPath($sheenBrush, $sheen)

    $sheen.Dispose()
    $sheenBrush.Dispose()
    $stripeBrush.Dispose()
    $stripes.Dispose()
    $rim.Dispose()
    $baseBrush.Dispose()
    $shell.Dispose()
    $g.Dispose()
    return $bmp
}

$images = @()
foreach ($s in $sizes) {
    $bmp = New-BrandBitmap $s
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # 256px is stored as PNG to keep the executable small; Vista+ supports it.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , @{ Size = $s; Bytes = $ms.ToArray() }
        $ms.Dispose()
        $bmp.Dispose()
        continue
    }
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40)          # biSize
    $bw.Write([int]$s)          # biWidth
    $bw.Write([int]($s * 2))    # biHeight (XOR + AND)
    $bw.Write([int16]1)         # biPlanes
    $bw.Write([int16]32)        # biBitCount
    $bw.Write([int]0)           # biCompression = BI_RGB
    $bw.Write([int]($s * $s * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B)
            $bw.Write([byte]$c.G)
            $bw.Write([byte]$c.R)
            $bw.Write([byte]$c.A)
        }
    }

    $maskBytes = [int]([Math]::Ceiling($s / 8.0))
    $pad = (4 - ($maskBytes % 4)) % 4
    $row = New-Object byte[] ($maskBytes + $pad)
    for ($y = 0; $y -lt $s; $y++) { $bw.Write($row) }

    $bw.Flush()
    $images += , @{ Size = $s; Bytes = $ms.ToArray() }
    $bw.Dispose()
    $ms.Dispose()
    $bmp.Dispose()
}

$buffer = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($buffer)
$writer.Write([int16]0)
$writer.Write([int16]1)
$writer.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = [int]$img.Size
    $writer.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $writer.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]$img.Bytes.Length)
    $writer.Write([int]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $writer.Write([byte[]]$img.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($Out, $buffer.ToArray())
$writer.Dispose()
$buffer.Dispose()

$info = Get-Item -LiteralPath $Out
Write-Host ("Icon : " + $info.FullName) -ForegroundColor Green
Write-Host ("Size : " + $info.Length + " bytes, " + $images.Count + " sizes")

if (-not $NoPng) {
    $pngSizes = @(192, 512)
    foreach ($ps in $pngSizes) {
        $png = Join-Path $root ("ui\icon-" + $ps + ".png")
        $bmp = New-BrandBitmap $ps
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pi = Get-Item -LiteralPath $png
        Write-Host ("PNG  : " + $pi.FullName + " (" + $pi.Length + " bytes)") -ForegroundColor Green
    }
}
