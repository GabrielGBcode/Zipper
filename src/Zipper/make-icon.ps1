# Draws Zipper.ico: a graphite rounded square with a column of interlocking white zipper teeth and a coral slider
# with a ring pull. Run again after changing the drawing.
param([string]$Out = "$PSScriptRoot\Zipper.ico", [string]$Preview = "")
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
$C = [System.Drawing.Color]

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Circle([float]$cx, [float]$cy, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r); $p
}

# Drawn on a 256-unit grid.
function DrawIcon($g) {
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 256, 256),
        ($C::FromArgb(75, 85, 99)), ($C::FromArgb(17, 24, 39))
    $g.FillPath($back, (RoundRect 8 8 240 240 56))

    $teeth = New-Object System.Drawing.SolidBrush ($C::FromArgb(250, 255, 255, 255))
    $top = 26; $rows = 7; $step = 16
    for ($i = 0; $i -lt $rows; $i++) {
        $x = if ($i % 2) { 122 } else { 94 }  # left and right teeth interlock across the center
        $g.FillPath($teeth, (RoundRect $x ($top + $i * $step) 40 10 4.5))
    }

    # slider, link and ring pull; the ring's hole is painted with the background gradient
    $coral = New-Object System.Drawing.SolidBrush ($C::FromArgb(255, 122, 107))
    $slider = $top + $rows * $step + 2
    $g.FillPath($coral, (RoundRect 100 $slider 56 30 10))
    $g.FillPath($coral, (RoundRect 120 ($slider + 24) 16 14 4))
    $g.FillPath($coral, (Circle 128 ($slider + 62) 26))
    $g.FillPath($back, (Circle 128 ($slider + 62) 12))
}

# Drawn at 1024 px and reduced, so every size is smoothly antialiased.
function Render([int]$size) {
    $big = New-Object System.Drawing.Bitmap 1024, 1024
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"; $g.ScaleTransform(4, 4)
    DrawIcon $g
    $g.Dispose()
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = "HighQualityBicubic"; $g.PixelOffsetMode = "HighQuality"
    $g.DrawImage($big, 0, 0, $size, $size)
    $g.Dispose(); $big.Dispose(); $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO with PNG entries (supported since Windows Vista)
$w = New-Object IO.BinaryWriter ([IO.File]::Create($Out))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()

if ($Preview) {
    # every size side by side on a dark and a light strip
    $sheet = New-Object System.Drawing.Bitmap 760, 560
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ($C::FromArgb(18, 20, 26))), 0, 0, 760, 280)
    $g.FillRectangle([System.Drawing.Brushes]::WhiteSmoke, 0, 280, 760, 280)
    foreach ($band in 0, 280) {
        $x = 10
        foreach ($s in 16, 24, 32, 48, 64, 128, 256) {
            $bmp = Render $s
            $g.DrawImageUnscaled($bmp, $x, $band + 12)
            $x += $s + 14
            $bmp.Dispose()
        }
    }
    $sheet.Save($Preview)
}
Write-Host "Pronto: $Out"
