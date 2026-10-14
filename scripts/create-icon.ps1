$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root 'src\Dynex.Desktop\Assets'
[IO.Directory]::CreateDirectory($destination) | Out-Null
$images = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 32, 48, 256)
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $black = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#08090C'))
    $red = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#C91F32'))
    $accent = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FF4655'))
    $graphics.FillRectangle($black, 0, 0, 256, 256)
    $outer = [Drawing.PointF[]]@([Drawing.PointF]::new(48,42),[Drawing.PointF]::new(151,42),[Drawing.PointF]::new(210,92),[Drawing.PointF]::new(210,164),[Drawing.PointF]::new(151,214),[Drawing.PointF]::new(48,214))
    $inner = [Drawing.PointF[]]@([Drawing.PointF]::new(89,82),[Drawing.PointF]::new(136,82),[Drawing.PointF]::new(168,110),[Drawing.PointF]::new(168,146),[Drawing.PointF]::new(136,174),[Drawing.PointF]::new(89,174))
    $graphics.FillPolygon($red, $outer)
    $graphics.FillPolygon($black, $inner)
    $graphics.FillRectangle($accent, 48, 42, 41, 72)
    $memory = [IO.MemoryStream]::new()
    $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
    $images.Add($memory.ToArray())
    $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $black.Dispose(); $red.Dispose(); $accent.Dispose()
}
$file = [IO.File]::Create((Join-Path $destination 'dynex.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write($bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
