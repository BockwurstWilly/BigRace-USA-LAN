# Erzeugt src\icon.ico (Zielflagge auf rotem Grund) in 16/24/32/48/64/256 Pixeln.
# Eigenes Symbol, damit keine Grafik aus dem Spiel in den EXEs steckt.
Add-Type -AssemblyName System.Drawing

function New-FlagBitmap([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)

    # Abgerundetes rotes Quadrat mit gelbem Rand
    $r = [Math]::Max(2, [int]($size * 0.18))
    $rect = New-Object Drawing.Rectangle 0, 0, ($size - 1), ($size - 1)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.Left, $rect.Top, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Top, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(235, 45, 50)), ([Drawing.Color]::FromArgb(140, 12, 20)), 90
    $g.FillPath($bg, $path)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 200, 30)), ([Math]::Max(1, $size / 16))
    $g.DrawPath($pen, $path)

    # Fahnenstange
    $poleX = [int]($size * 0.24)
    $poleW = [Math]::Max(1, [int]($size * 0.06))
    $g.FillRectangle([Drawing.Brushes]::Gold, $poleX, [int]($size * 0.16), $poleW, [int]($size * 0.70))

    # Zielflagge (4 x 3 Felder, leicht wehend)
    $cols = 4; $rows = 3
    $fx = $poleX + $poleW
    $fy = [int]($size * 0.18)
    $fw = [int]($size * 0.58)
    $fh = [int]($size * 0.42)
    $cw = $fw / $cols; $ch = $fh / $rows
    for ($c = 0; $c -lt $cols; $c++) {
        $wave = [Math]::Sin($c / ($cols - 1) * [Math]::PI) * $size * 0.04
        for ($row = 0; $row -lt $rows; $row++) {
            $brush = if ((($c + $row) % 2) -eq 0) { [Drawing.Brushes]::White } else { [Drawing.Brushes]::Black }
            $g.FillRectangle($brush, [float]($fx + $c * $cw), [float]($fy + $row * $ch + $wave), [float]($cw + 0.5), [float]($ch + 0.5))
        }
    }
    $g.Dispose()
    return $bmp
}

# Kleine Groessen als klassisches 32-Bit-DIB (versteht jede Windows-Stelle), 256 als PNG
function Get-IconEntry([int]$s) {
    $bmp = New-FlagBitmap $s
    $ms = New-Object IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))      # BITMAPINFOHEADER, Hoehe doppelt (XOR + AND)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]0); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {                                             # Pixel von unten nach oben, BGRA
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)                                # AND-Maske leer (Alpha regelt Transparenz)
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { , (Get-IconEntry $s) }

# ICO-Datei schreiben
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon.ico'), $out.ToArray())
"icon.ico geschrieben"
