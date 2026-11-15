# Draws the netroute logo (two linked network dots) at every size Windows
# uses and writes app\netroute.ico. Run from anywhere; needs only Windows.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "netroute.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Draw-Logo([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $s * 0.03)
    $d   = $s * 0.60                       # circle diameter
    $x1 = $pad;            $y1 = $pad
    $x2 = $s - $pad - $d;  $y2 = $s - $pad - $d
    $c1x = $x1 + $d / 2;   $c1y = $y1 + $d / 2
    $c2x = $x2 + $d / 2;   $c2y = $y2 + $d / 2

    $blue  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 110, 200))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 170, 90))
    $lw = [Math]::Max(1.5, $s * 0.11)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $lw
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round

    $g.FillEllipse($blue,  [single]$x1, [single]$y1, [single]$d, [single]$d)
    $g.FillEllipse($green, [single]$x2, [single]$y2, [single]$d, [single]$d)
    $g.DrawLine($pen, [single]$c1x, [single]$c1y, [single]$c2x, [single]$c2y)

    # Small white dots at the two centres make the link read as "two nodes".
    if ($s -ge 24) {
        $dot = $s * 0.14
        $white = [System.Drawing.Brushes]::White
        $g.FillEllipse($white, [single]($c1x - $dot/2), [single]($c1y - $dot/2), [single]$dot, [single]$dot)
        $g.FillEllipse($white, [single]($c2x - $dot/2), [single]($c2y - $dot/2), [single]$dot, [single]$dot)
    }
    $g.Dispose(); $pen.Dispose(); $blue.Dispose(); $green.Dispose()
    return $bmp
}

$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw-Logo $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# ICO container: header, one 16-byte directory entry per image, then PNG data.
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$p.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Close(); $fs.Close()

# Also drop a PNG preview next to it so the logo can be looked at.
$preview = Draw-Logo 256
$preview.Save((Join-Path $PSScriptRoot "netroute-logo.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Output "wrote $out ($((Get-Item $out).Length) bytes, $($pngs.Count) sizes)"
