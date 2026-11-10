using namespace System.Drawing
using namespace System.Drawing.Drawing2D
param([string]$IcoOut, [string]$PreviewOut)
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$size) {
    $bmp = New-Object Bitmap $size, $size, ([Imaging.PixelFormat]::Format32bppArgb)
    $g = [Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Fons: quadrat arrodonit fosc amb degradat
    $r = 52; $x = 8; $y = 8; $w = 240; $h = 240
    $path = New-Object GraphicsPath
    $path.AddArc($x, $y, $r, $r, 180, 90); $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object LinearGradientBrush ([Point]::new(0, 8)), ([Point]::new(0, 248)), ([Color]::FromArgb(255, 0x34, 0x38, 0x41)), ([Color]::FromArgb(255, 0x14, 0x15, 0x19))
    $g.FillPath($bg, $path)
    $g.DrawPath((New-Object Pen ([Color]::FromArgb(255, 0x4A, 0x4F, 0x5A)), 4), $path)

    # Triangle de «play» verd amb cantonades arrodonides
    $tri = [PointF[]]@([PointF]::new(56, 76), [PointF]::new(56, 180), [PointF]::new(140, 128))
    $green = [Color]::FromArgb(255, 0x3F, 0xC2, 0x66)
    $g.FillPolygon((New-Object SolidBrush $green), $tri)
    $pen = New-Object Pen $green, 22; $pen.LineJoin = 'Round'
    $g.DrawPolygon($pen, $tri)

    # Ones de so blaves
    $blue = [Color]::FromArgb(255, 0x5B, 0x9B, 0xF0)
    $wave = New-Object Pen $blue, 15; $wave.StartCap = 'Round'; $wave.EndCap = 'Round'
    $g.DrawArc($wave, 92, 82, 92, 92, -42, 84)
    $g.DrawArc($wave, 58, 48, 160, 160, -40, 80)

    $g.Dispose()
    return $bmp
}

$sizes = 256, 64, 48, 32, 24, 16
$pngs = foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    if ($sz -eq 256 -and $PreviewOut) { $bmp.Save($PreviewOut, [Imaging.ImageFormat]::Png) }
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# Fitxer ICO amb imatges PNG
$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[IO.File]::WriteAllBytes($IcoOut, $out.ToArray())
"ICO: $((Get-Item $IcoOut).Length) bytes"
