param([string]$PreviewPath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function RoundedPath([float]$x,[float]$y,[float]$w,[float]$h,[float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x,$y,$d,$d,180,90)
    $path.AddArc(($x+$w-$d),$y,$d,$d,270,90)
    $path.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90)
    $path.AddArc($x,($y+$h-$d),$d,$d,90,90)
    $path.CloseFigure()
    return $path
}
$canvas = New-Object System.Drawing.Bitmap 256,256
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$sage = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#70877C'))
$paper = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#F4F3EB'))
$outline = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#F4F3EB')),10
$outline.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$outline.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$line = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#DFE6DA')),10
$line.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$line.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$bg = RoundedPath 4 4 248 248 58
$sheet = RoundedPath 65 65 126 137 17
$clip = RoundedPath 98 47 60 34 12
$g.FillPath($sage,$bg)
$g.DrawPath($outline,$sheet)
$g.FillPath($paper,$clip)
$g.DrawLine($line,94,116,160,116)
$g.DrawLine($line,94,145,160,145)
$g.DrawLine($line,94,174,139,174)
$pngPath = Join-Path $PSScriptRoot 'src\Copy.png'
$canvas.Save($pngPath,[System.Drawing.Imaging.ImageFormat]::Png)
if ($PreviewPath) { $canvas.Save($PreviewPath,[System.Drawing.Imaging.ImageFormat]::Png) }
$frames = @()
$sizes = @(16,20,24,32,40,48,64,128,256)
foreach ($size in $sizes) {
    $small = New-Object System.Drawing.Bitmap $size,$size
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.DrawImage($canvas,0,0,$size,$size)
    $memory = New-Object System.IO.MemoryStream
    $small.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,($memory.ToArray())
    $memory.Dispose(); $sg.Dispose(); $small.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'src\Copy.ico'))
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose(); $stream.Dispose()
$bg.Dispose(); $sheet.Dispose(); $clip.Dispose(); $sage.Dispose(); $paper.Dispose(); $outline.Dispose(); $line.Dispose(); $g.Dispose(); $canvas.Dispose()
Write-Output 'Generated multi-resolution Copy.ico and Copy.png'
