# Build a multi-resolution Icon.ico from the RXDK extension logo (extension-icon.png).
# ICO frames are PNG-encoded (Vista+), so the neon logo stays crisp at every size.
param(
    [Parameter(Mandatory = $true)][string]$SrcPath,
    [Parameter(Mandatory = $true)][string]$OutPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$src = [System.Drawing.Image]::FromFile($SrcPath)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose(); $ms.Dispose()
}
$src.Dispose()
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim); $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$f.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()
Write-Host "Wrote $OutPath ($((Get-Item $OutPath).Length) bytes, $($frames.Count) sizes)"
