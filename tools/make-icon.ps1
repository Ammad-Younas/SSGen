# Generates src/SSGen.App/Assets/ssgen.ico (16-256 px) and ssgen-256.png.
# Each size is drawn natively (not downscaled); small sizes drop detail so they stay crisp.
# Design: vivid red squircle; two tilted "screenshot" cards (translucent back, white front showing a mini app
# screen) with soft depth; a sparkle for the AI part.
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\SSGen.App\Assets"))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

# Superellipse ("squircle") |x|^n + |y|^n = 1, the modern app-icon shape.
function Squircle([float]$cx, [float]$cy, [float]$radius, [float]$n = 5) {
    $pts = for ($i = 0; $i -lt 360; $i += 2) {
        $t = $i * [Math]::PI / 180
        $c = [Math]::Cos($t); $s = [Math]::Sin($t)
        $x = [Math]::Sign($c) * [Math]::Pow([Math]::Abs($c), 2 / $n)
        $y = [Math]::Sign($s) * [Math]::Pow([Math]::Abs($s), 2 / $n)
        [System.Drawing.PointF]::new($cx + $radius * $x, $cy + $radius * $y)
    }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddClosedCurve([System.Drawing.PointF[]]$pts, 0.2)
    return $p
}

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# A path rotated by $deg degrees around (cx, cy).
function Rotated($path, [float]$deg, [float]$cx, [float]$cy) {
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.RotateAt($deg, [System.Drawing.PointF]::new($cx, $cy))
    $path.Transform($m)
    return $path
}

# Four-pointed sparkle (concave star).
function Sparkle([float]$cx, [float]$cy, [float]$r) {
    $k = $r * 0.18
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, $cy - $r), [System.Drawing.PointF]::new($cx + $k, $cy - $k),
        [System.Drawing.PointF]::new($cx + $r, $cy), [System.Drawing.PointF]::new($cx + $k, $cy + $k),
        [System.Drawing.PointF]::new($cx, $cy + $r), [System.Drawing.PointF]::new($cx - $k, $cy + $k),
        [System.Drawing.PointF]::new($cx - $r, $cy), [System.Drawing.PointF]::new($cx - $k, $cy - $k))
    $p.AddClosedCurve($pts, 0.25)
    return $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $detail = if ($size -ge 48) { 2 } elseif ($size -ge 32) { 1 } else { 0 }

    # Background squircle with a diagonal red gradient and a soft top highlight.
    $bg = Squircle 128 128 120
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(20, 10)), ([System.Drawing.PointF]::new(236, 246)), (C 255 255 94 98), (C 255 176 18 52)
    $g.FillPath($grad, $bg)
    $g.SetClip($bg)
    # The gradient runs past the filled area so its end never shows as a line.
    $shine = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, 150)), (C 46 255 255 255), (C 0 255 255 255)
    $g.FillRectangle($shine, 0, 8, 256, 128)
    $g.ResetClip()

    # Back card (translucent), tilted left.
    if ($detail -ge 1) {
        $back = Rotated (RoundRect 62 64 96 148 18) -13 110 138
        $g.FillPath((New-Object System.Drawing.SolidBrush (C 85 255 255 255)), $back)
    }

    # Front card: soft shadow, then white, tilted right.
    $cardX = 96; $cardY = 54; $cardW = 100; $cardH = 158
    if ($size -lt 32) { $cardX = 50; $cardY = 64; $cardW = 104; $cardH = 156 } # leave the top-right corner to the sparkle
    foreach ($s in @(@(10, 22), @(6, 30), @(3, 40))) {
        $sh = Rotated (RoundRect ($cardX + $s[0] * 0.4) ($cardY + $s[0]) $cardW $cardH 20) 8 ($cardX + $cardW / 2) ($cardY + $cardH / 2)
        $g.FillPath((New-Object System.Drawing.SolidBrush (C $s[1] 60 0 10)), $sh)
    }
    $front = Rotated (RoundRect $cardX $cardY $cardW $cardH 20) 8 ($cardX + $cardW / 2) ($cardY + $cardH / 2)
    $g.FillPath([System.Drawing.Brushes]::White, $front)

    # Mini app screen inside the front card (same rotation).
    if ($detail -ge 1) {
        $state = $g.Save()
        $g.TranslateTransform($cardX + $cardW / 2, $cardY + $cardH / 2); $g.RotateTransform(8); $g.TranslateTransform(-($cardX + $cardW / 2), -($cardY + $cardH / 2))
        $ix = $cardX + 12; $iw = $cardW - 24
        $bar = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new($ix, 0)), ([System.Drawing.PointF]::new($ix + $iw, 0)), (C 255 255 94 98), (C 255 196 24 58)
        $g.FillPath($bar, (RoundRect $ix ($cardY + 14) $iw 20 7))
        if ($detail -ge 2) {
            $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 255 214 220)), (RoundRect $ix ($cardY + 44) $iw 56 9))
            $line = New-Object System.Drawing.SolidBrush (C 255 226 230 236)
            $g.FillPath($line, (RoundRect $ix ($cardY + 112) $iw 10 5))
            $g.FillPath($line, (RoundRect $ix ($cardY + 130) ($iw * 0.62) 10 5))
        }
        $g.Restore($state)
    }

    # Sparkle (AI): one large, one small at bigger sizes.
    $sparkBrush = New-Object System.Drawing.SolidBrush (C 255 255 255 255)
    if ($size -lt 32) {
        $g.FillPath($sparkBrush, (Sparkle 198 62 40))
    } else {
        $g.FillPath($sparkBrush, (Sparkle 198 66 34))
        if ($detail -ge 2) { $g.FillPath((New-Object System.Drawing.SolidBrush (C 230 255 255 255)), (Sparkle 224 104 13)) }
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $OutDir "ssgen-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    return , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { , (Draw $sz) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir "ssgen.ico"), $out.ToArray())
"Wrote $(Join-Path $OutDir 'ssgen.ico') ($($sizes -join ', ') px)"
