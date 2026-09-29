$ErrorActionPreference = 'Stop'

$assetsDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$edgePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$userDataDirectory = Join-Path $env:TEMP 'ArsanGazLogoRenderer'

if (-not (Test-Path $edgePath)) {
    throw 'Microsoft Edge is required to render the SVG brand assets.'
}

foreach ($asset in @(
    @{ Name = 'ArsanGazLogo'; Width = 960; Height = 760 },
    @{ Name = 'ArsanGazMark'; Width = 512; Height = 512 }
)) {
    $svgPath = Join-Path $assetsDirectory ($asset.Name + '.svg')
    $pngPath = Join-Path $assetsDirectory ($asset.Name + '.png')
    $svgUrl = 'file:///' + $svgPath.Replace('\', '/')
    $arguments = @(
        '--headless=new'
        '--disable-gpu'
        '--no-sandbox'
        '--hide-scrollbars'
        "--user-data-dir=$userDataDirectory"
        "--window-size=$($asset.Width),$($asset.Height)"
        "--screenshot=$pngPath"
        $svgUrl
    )

    $render = Start-Process -FilePath $edgePath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($render.ExitCode -ne 0 -or -not (Test-Path $pngPath)) {
        throw "Failed to render $($asset.Name).svg."
    }
}

Add-Type -AssemblyName System.Drawing
$sourceImage = [Drawing.Bitmap]::FromFile((Join-Path $assetsDirectory 'ArsanGazMark.png'))
$iconFrames = [Collections.Generic.List[byte[]]]::new()
$iconSizes = @(16, 32, 48, 256)

foreach ($size in $iconSizes) {
    $frame = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($frame)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.DrawImage($sourceImage, 0, 0, $size, $size)

    $stream = [IO.MemoryStream]::new()
    $frame.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $iconFrames.Add($stream.ToArray())

    $stream.Dispose()
    $graphics.Dispose()
    $frame.Dispose()
}

$sourceImage.Dispose()
$iconStream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($iconStream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$iconFrames.Count)
$offset = 6 + (16 * $iconFrames.Count)

for ($index = 0; $index -lt $iconFrames.Count; $index++) {
    $size = $iconSizes[$index]
    $dimension = if ($size -eq 256) { [byte]0 } else { [byte]$size }
    $writer.Write($dimension)
    $writer.Write($dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$iconFrames[$index].Length)
    $writer.Write([UInt32]$offset)
    $offset += $iconFrames[$index].Length
}

foreach ($frame in $iconFrames) {
    $writer.Write($frame)
}

$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $assetsDirectory 'ArsanGaz.ico'), $iconStream.ToArray())
$writer.Dispose()
$iconStream.Dispose()

Get-ChildItem $assetsDirectory -File | Where-Object Name -in @('ArsanGazLogo.png', 'ArsanGazMark.png', 'ArsanGaz.ico') | Select-Object Name, Length