# Draws Assets\app.ico in Windows 11 Fluent style.
# Usage: powershell -File make-icon.ps1 <out.ico> [preview.png]
param([string]$OutIco, [string]$OutPng)
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function C([int]$r, [int]$g, [int]$b, [int]$a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function Fill-Grad($g, $path, $c1, $c2, [float]$angle) {
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $path.GetBounds(), $c1, $c2, $angle
    $g.FillPath($br, $path); $br.Dispose()
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $f = $s / 256.0
    $small = $s -le 24

    # Tile: warm orange gradient with a soft top highlight.
    $tile = New-RoundRect (12 * $f) (12 * $f) (232 * $f) (232 * $f) (52 * $f)
    Fill-Grad $g $tile (C 255 170 90) (C 214 84 30) 60
    $hl = New-RoundRect (12 * $f) (12 * $f) (232 * $f) (116 * $f) (52 * $f)
    Fill-Grad $g $hl (C 255 255 255 60) (C 255 255 255 0) 90

    # Gauge: faint 240-degree track, white fill up to the needle.
    $w = if ($small) { 34 * $f } else { 26 * $f }
    $r = 82 * $f; $cx = 128 * $f; $cy = 140 * $f
    $rect = New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
    $track = New-Object System.Drawing.Pen (C 255 255 255 90), $w
    $track.StartCap = 'Round'; $track.EndCap = 'Round'
    $g.DrawArc($track, $rect, 150, 240); $track.Dispose()
    $p = New-Object System.Drawing.Pen (C 255 255 255), $w
    $p.StartCap = 'Round'; $p.EndCap = 'Round'
    $g.DrawArc($p, $rect, 150, 150); $p.Dispose()

    # Needle at ~62% plus hub.
    $ang = (150 + 150) * [Math]::PI / 180
    $len = $r - ($w * 0.2)
    $nw = if ($small) { 26 * $f } else { 16 * $f }
    $np = New-Object System.Drawing.Pen (C 90 30 10), $nw
    $np.StartCap = 'Round'; $np.EndCap = 'Round'
    $g.DrawLine($np, $cx, $cy, ($cx + $len * [Math]::Cos($ang)), ($cy + $len * [Math]::Sin($ang))); $np.Dispose()
    $hub = 22 * $f
    $hb = New-Object System.Drawing.SolidBrush (C 90 30 10)
    $g.FillEllipse($hb, ($cx - $hub), ($cy - $hub), (2 * $hub), (2 * $hub)); $hb.Dispose()
    $g.Dispose()
    return $bmp
}
# ICO frame as 32bpp DIB (BITMAPINFOHEADER + bottom-up BGRA + AND mask): readable by every decoder.
function Get-DibBytes($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int][Math]::Ceiling($s / 32.0) * 4
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]($s * $s * 4 + $maskStride * $s)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $s))) # alpha channel is used; mask all zero
    $w.Flush()
    return , $ms.ToArray()
}

# 256px stays PNG (standard for the large frame, keeps the file small).
$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Frame $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($OutPng) { $b.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png) }
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $b
    }
    $b.Dispose()
    , $bytes
}

$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Dispose()
