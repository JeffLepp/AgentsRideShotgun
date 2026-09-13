$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path $PSScriptRoot '../app/Assets'
[System.IO.Directory]::CreateDirectory($assetRoot) | Out-Null
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $draw = [System.Drawing.Graphics]::FromImage($bitmap)
    $draw.Clear([System.Drawing.Color]::FromArgb(17, 21, 29))
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(143, 183, 255))
    $draw.ScaleTransform($size / 32.0, $size / 32.0)
    # Three interlocking window panes; the app shell uses the same geometric mark.
    $draw.FillRectangle($brush, 6, 6, 8, 20)
    $draw.FillRectangle($brush, 17, 6, 9, 8)
    $draw.FillRectangle($brush, 17, 17, 9, 9)
    $buffer = New-Object System.IO.MemoryStream
    $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += ,($buffer.ToArray())
    $buffer.Dispose()
    $brush.Dispose()
    $draw.Dispose()
    $bitmap.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path $assetRoot 'Deskweave.ico'))
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
}
finally { $writer.Dispose(); $stream.Dispose() }
