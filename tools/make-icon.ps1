# Generates src/SSGen.App/Assets/ssgen.ico (16-256 px) and ssgen-256.png.
# Each size is drawn natively (not downscaled) so small sizes stay crisp.
# Design: red rounded square; a white phone outline with app-screen content; a white "capture" corner frame.
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\SSGen.App\Assets"))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Background: rounded square, red gradient.
    $bg = RoundRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (52 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($size, $size)),
        ([System.Drawing.Color]::FromArgb(255, 232, 64, 52)), ([System.Drawing.Color]::FromArgb(255, 150, 16, 22))
    $g.FillPath($grad, $bg)

    $white = [System.Drawing.Color]::White
    $stroke = [Math]::Max(1.5, 14 * $s)

    # Phone outline.
    $phone = RoundRect (84 * $s) (44 * $s) (88 * $s) (168 * $s) (18 * $s)
    $pen = New-Object System.Drawing.Pen $white, $stroke
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $phone)

    # Screen content: a header bar and two rows (dropped at tiny sizes where they'd turn to mush).
    if ($size -ge 32) {
        $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
        $g.FillPath($fill, (RoundRect (102 * $s) (70 * $s) (52 * $s) (26 * $s) (5 * $s)))
        $g.FillPath($fill, (RoundRect (102 * $s) (110 * $s) (52 * $s) (12 * $s) (4 * $s)))
        $g.FillPath($fill, (RoundRect (102 * $s) (134 * $s) (36 * $s) (12 * $s) (4 * $s)))
    }

    # Capture frame: four corner brackets around the phone.
    $cpen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 12 * $s))
    $cpen.StartCap = 'Round'; $cpen.EndCap = 'Round'; $cpen.LineJoin = 'Round'
    $l = 50 * $s; $t = 30 * $s; $r = 206 * $s; $b = 226 * $s; $k = 34 * $s
    $g.DrawLines($cpen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($l, $t + $k), [System.Drawing.PointF]::new($l, $t), [System.Drawing.PointF]::new($l + $k, $t)))
    $g.DrawLines($cpen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($r - $k, $t), [System.Drawing.PointF]::new($r, $t), [System.Drawing.PointF]::new($r, $t + $k)))
    $g.DrawLines($cpen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($r, $b - $k), [System.Drawing.PointF]::new($r, $b), [System.Drawing.PointF]::new($r - $k, $b)))
    $g.DrawLines($cpen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($l + $k, $b), [System.Drawing.PointF]::new($l, $b), [System.Drawing.PointF]::new($l, $b - $k)))

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
