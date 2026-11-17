# Gera images/app.ico: glifo de mouse (Segoe Fluent Icons U+E962) em azul #00B8FC.
# 16-48 px em DIB 32 bits (compatível com tudo); 256 px em PNG.
param([string]$Out = (Join-Path $PSScriptRoot '..\images\app.ico'))
Add-Type -AssemblyName System.Drawing

$family = 'Segoe Fluent Icons'
try { $null = New-Object System.Drawing.FontFamily($family) } catch { $family = 'Segoe MDL2 Assets' }
$glyph = [string][char]0xE962
$color = [System.Drawing.Color]::FromArgb(255, 0, 184, 252)

function Render([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $font = New-Object System.Drawing.Font($family, [float]($px * 0.92), [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.DrawString($glyph, $font, $brush, (New-Object System.Drawing.RectangleF(0, 0, $px, $px)), $fmt)
    $brush.Dispose(); $fmt.Dispose(); $font.Dispose(); $g.Dispose()
    return $bmp
}

function DibBytes($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    return ,$ms.ToArray()
}

function PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 256
$entries = New-Object System.Collections.Generic.List[object]
foreach ($s in $sizes) {
    $bmp = Render $s
    if ($s -eq 256) { $data = PngBytes $bmp } else { $data = DibBytes $bmp }
    $bmp.Dispose()
    $entries.Add([pscustomobject]@{ Size = $s; Data = [byte[]]$data })
}

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e.Data.Length); $w.Write([int]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write($e.Data) }
$w.Flush()
$full = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllBytes($full, $ms.ToArray())
"ok: $full ($($ms.Length) bytes; $($sizes -join ', ') px; fonte $family)"
