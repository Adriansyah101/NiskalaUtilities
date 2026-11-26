#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$markup = Join-Path $repo 'src\Niskala.UI\Views\NiskalaMark.xaml'
$output = Join-Path $repo 'assets\niskala.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$culture = [Globalization.CultureInfo]::InvariantCulture

function Get-PathData {
    param([xml]$Xaml, [string]$Name)

    $node = $Xaml.SelectSingleNode("//*[@*[local-name()='Name'] = '$Name']")
    if ($null -eq $node) {
        throw "NiskalaMark.xaml has no element named $Name."
    }

    $figures = @()
    foreach ($figure in [regex]::Matches($node.GetAttribute('Data'), 'M[^MZ]+Z?')) {
        $points = foreach ($pair in [regex]::Matches($figure.Value, '(-?[\d.]+),(-?[\d.]+)')) {
            New-Object Drawing.PointF ([single]::Parse($pair.Groups[1].Value, $culture)), ([single]::Parse($pair.Groups[2].Value, $culture))
        }
        $figures += , [pscustomobject]@{ Points = [Drawing.PointF[]]$points; Closed = $figure.Value.EndsWith('Z') }
    }

    $figures
}

function New-GraphicsPath {
    param($Figures, [single]$Scale, [single]$X, [single]$Y)

    $path = New-Object Drawing.Drawing2D.GraphicsPath
    foreach ($figure in $Figures) {
        $points = [Drawing.PointF[]]($figure.Points | ForEach-Object { New-Object Drawing.PointF ($X + $_.X * $Scale), ($Y + $_.Y * $Scale) })
        $path.StartFigure()
        $path.AddLines($points)
        if ($figure.Closed) {
            $path.CloseFigure()
        }
    }

    $path
}

function New-RoundedSquare {
    param([int]$Size, [single]$Radius)

    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $diameter = 2 * $Radius
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($Size - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($Size - $diameter, $Size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $Size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $path
}

function New-IconImage {
    param([int]$Size, $Outline, $Tips, $Shard)

    $bitmap = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)

        $tile = New-RoundedSquare -Size $Size -Radius ([Math]::Max(2, $Size * 0.2))
        $top = [Drawing.ColorTranslator]::FromHtml('#183D66')
        $bottom = [Drawing.ColorTranslator]::FromHtml('#08111E')
        $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 0, $Size), $top, $bottom
        $graphics.FillPath($gradient, $tile)

        $solid = $Size -lt 32
        $height = $Size * $(if ($solid) { 0.66 } else { 0.56 })
        $scale = [single]($height / 100)
        $x = [single](($Size - 94 * $scale) / 2)
        $y = [single](($Size - 100 * $scale) / 2)
        $white = [Drawing.Brushes]::White
        $pen = New-Object Drawing.Pen ([Drawing.Color]::White), (10 * $scale)
        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Miter
        $pen.MiterLimit = 4

        if ($solid) {
            $closed = @([pscustomobject]@{ Points = $Outline[0].Points[1..($Outline[0].Points.Length - 1)]; Closed = $true })
            $body = New-GraphicsPath -Figures $closed -Scale $scale -X $x -Y $y
            $graphics.FillPath($white, $body)
            $graphics.DrawPath($pen, $body)
        }
        else {
            $graphics.DrawPath($pen, (New-GraphicsPath -Figures $Outline -Scale $scale -X $x -Y $y))
            $graphics.FillPath($white, (New-GraphicsPath -Figures $Tips -Scale $scale -X $x -Y $y))
            $graphics.FillPath($white, (New-GraphicsPath -Figures $Shard -Scale $scale -X $x -Y $y))
        }
    }
    finally {
        $graphics.Dispose()
    }

    $bitmap
}

function Get-IconBytes {
    param([Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    if ($size -ge 256) {
        $stream = New-Object IO.MemoryStream
        $Bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }

    $mask = [int]([Math]::Ceiling($size / 32.0) * 4) * $size
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $stream
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $mask)); $writer.Write([int]0); $writer.Write([int]0)
    $writer.Write([int]0); $writer.Write([int]0)

    $data = $Bitmap.LockBits((New-Object Drawing.Rectangle 0, 0, $size, $size), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($size * 4)
        for ($y = $size - 1; $y -ge 0; $y--) {
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
            $writer.Write($row)
        }
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $writer.Write((New-Object byte[] $mask))
    $writer.Flush()
    $stream.ToArray()
}

[xml]$xaml = Get-Content -Raw -LiteralPath $markup
$outline = Get-PathData -Xaml $xaml -Name 'Outline'
$tips = Get-PathData -Xaml $xaml -Name 'Tips'
$shard = Get-PathData -Xaml $xaml -Name 'Shard'

$images = foreach ($size in $sizes) {
    $bitmap = New-IconImage -Size $size -Outline $outline -Tips $tips -Shard $shard
    try {
        [pscustomobject]@{ Size = $size; Bytes = (Get-IconBytes -Bitmap $bitmap) }
    }
    finally {
        $bitmap.Dispose()
    }
}

$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = [byte]($image.Size % 256)
    $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$image.Bytes.Length); $writer.Write([int]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) {
    $writer.Write([byte[]]$image.Bytes)
}
$writer.Flush()
[IO.File]::WriteAllBytes($output, $stream.ToArray())
Write-Host "$output ($($stream.Length) bytes, $($sizes -join ', ') px)"
