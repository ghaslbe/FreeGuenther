# Erzeugt LmChat/app.ico (16–256 px) und LmChat/app-logo.png (512 px, scharfes Logo in der App):
# Sprechblase auf Farbverlauf.
# Aufruf: powershell -File tools\make-icon.ps1
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\LmChat\app.ico'),
    [string]$LogoOut = (Join-Path $PSScriptRoot '..\LmChat\app-logo.png')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $s = [float]$size
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)

    # Hintergrund: abgerundetes Quadrat mit Blau-Violett-Verlauf
    $bg = New-RoundedRect 0 0 ($s - 1) ($s - 1) ($s * 0.22)
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s),
        ([Drawing.Color]::FromArgb(255, 59, 130, 246)), ([Drawing.Color]::FromArgb(255, 124, 58, 237))
    $g.FillPath($brush, $bg)

    # Weiße Sprechblase mit Spitze unten links
    $bubble = New-RoundedRect ($s * 0.17) ($s * 0.20) ($s * 0.66) ($s * 0.46) ($s * 0.12)
    $tail = New-Object Drawing.Drawing2D.GraphicsPath
    $tail.AddPolygon([Drawing.PointF[]]@(
        (New-Object Drawing.PointF ($s * 0.30), ($s * 0.60)),
        (New-Object Drawing.PointF ($s * 0.26), ($s * 0.82)),
        (New-Object Drawing.PointF ($s * 0.48), ($s * 0.64))))
    $g.FillPath([Drawing.Brushes]::White, $bubble)
    $g.FillPath([Drawing.Brushes]::White, $tail)

    # Drei Punkte ("schreibt ...")
    $dot = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 99, 102, 241))
    $r = $s * 0.055
    foreach ($cx in 0.35, 0.50, 0.65) {
        $g.FillEllipse($dot, ($s * $cx - $r), ($s * 0.43 - $r), 2 * $r, 2 * $r)
    }

    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# ICO-Datei mit PNG-Einträgen zusammensetzen
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

$stream = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter -ArgumentList $stream
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
$w.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $stream.ToArray())
"Icon geschrieben: $([IO.Path]::GetFullPath($Out))"

[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($LogoOut), (New-IconPng 512))
"Logo geschrieben: $([IO.Path]::GetFullPath($LogoOut))"
