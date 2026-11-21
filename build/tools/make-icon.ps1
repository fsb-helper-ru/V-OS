<#
.SYNOPSIS
  Generates assets\icons\vos.ico (16-256 px) and vos-256.png: the V-OS mark, a two-tone squircle with a white "V",
  drawn as vectors at every size so small sizes stay crisp. Deterministic: same script, same bytes.
.EXAMPLE
  .\build\tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = Join-Path $repo 'assets\icons'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $inset = [single]([Math]::Max(0.5, $size * 0.03))
    $s = [single]($size - 2 * $inset)
    $body = New-RoundedPath $inset $inset $s $s ([single]($s * 0.27))
    $from = [System.Drawing.Color]::FromArgb(255, 0x8E, 0xA2, 0xFF)
    $to = [System.Drawing.Color]::FromArgb(255, 0x4C, 0x5F, 0xD6)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(($size * 0.15), 0)), (New-Object System.Drawing.PointF(($size * 0.85), $size)), $from, $to)
    $g.FillPath($grad, $body)
    if ($size -ge 32) {
        $rim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), [single]([Math]::Max(1, $size / 64)))
        $g.DrawPath($rim, $body)
        $rim.Dispose()
    }
    # The "V": two strokes meeting low in the middle, round caps and join; heavier at small sizes for legibility.
    $weight = if ($size -le 20) { 0.155 } elseif ($size -le 32) { 0.13 } else { 0.115 }
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]($size * $weight))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($size * 0.31), ($size * 0.33))),
        (New-Object System.Drawing.PointF(($size * 0.50), ($size * 0.69))),
        (New-Object System.Drawing.PointF(($size * 0.69), ($size * 0.33))))
    $g.DrawLines($pen, $pts)
    $pen.Dispose(); $grad.Dispose(); $body.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $outDir 'vos-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported by Windows Vista and later).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'vos.ico'), $out.ToArray())
"Wrote $(Join-Path $outDir 'vos.ico') ($($out.Length) bytes, sizes $($sizes -join ', '))"
