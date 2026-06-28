# Generates app.ico for ChessOverMesh.Gui:
# a chess knight glyph over concentric mesh-radio signal arcs, on the app's dark theme.
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background with a diagonal dark gradient.
    $pad = [Math]::Max(1, [int]($S * 0.04))
    $r   = [Math]::Max(2, [int]($S * 0.20))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($S - 2*$pad), ($S - 2*$pad))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $c1 = [System.Drawing.Color]::FromArgb(255, 24, 30, 38)
    $c2 = [System.Drawing.Color]::FromArgb(255, 13, 17, 23)
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 60.0)
    $g.FillPath($br, $path)
    $g.SetClip($path)   # keep foreground inside the rounded square

    # Concentric mesh-radio signal arcs in the open top-right corner, Meshtastic green.
    $green = [System.Drawing.Color]::FromArgb(255, 103, 234, 148)
    $cx = $S * 0.72; $cy = $S * 0.30
    $pw = [Math]::Max(1.0, $S * 0.035)
    for ($i = 1; $i -le 3; $i++) {
        $rad = $S * (0.07 + 0.085 * $i)
        $a = [int](255 - 40 * $i)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($a, $green), $pw)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        # Sweep facing up-and-right (GDI angles: 0=right, -90=up).
        $g.DrawArc($pen, ($cx - $rad), ($cy - $rad), (2*$rad), (2*$rad), -78, 66)
        $pen.Dispose()
    }
    # Signal-origin dot.
    $dot = New-Object System.Drawing.SolidBrush($green)
    $dr = $S * 0.032
    $g.FillEllipse($dot, ($cx - $dr), ($cy - $dr), (2*$dr), (2*$dr))

    # Chess knight glyph, centered, light.
    $glyph = [char]0x265E   # BLACK CHESS KNIGHT (filled)
    $font = New-Object System.Drawing.Font("Segoe UI Symbol", ($S * 0.56), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $box = New-Object System.Drawing.RectangleF(($S * 0.04), ($S * 0.04), ($S * 0.92), ($S * 0.92))
    $fg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 244, 248))
    $g.DrawString($glyph, $font, $fg, $box, $sf)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

$sizes = 16,24,32,48,64,128,256
$pngs = @{}
foreach ($s in $sizes) { $b = New-IconBitmap $s; $pngs[$s] = Get-PngBytes $b; $b.Dispose() }

# Assemble ICO (PNG-compressed entries; Vista+).
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $wb = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$wb); $bw.Write([byte]$wb); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush()
$path = Join-Path $PSScriptRoot 'app.ico'
[System.IO.File]::WriteAllBytes($path, $out.ToArray())
$out.Dispose()
Write-Output "Wrote $path ($((Get-Item $path).Length) bytes)"
