param([string]$IconDir, [string]$Out)

Add-Type -AssemblyName System.Drawing

$marks = @(
    @{ Name = 'A   pin over contours'; Prefix = 'waypoint-terrain' },
    @{ Name = 'B   pin alone';         Prefix = 'waypoint-pin' },
    @{ Name = 'C   compass rose';      Prefix = 'waypoint-compass' }
)
$shown = 16, 24, 32, 48, 64

$colW = 320
$W = 40 + $colW * $marks.Count; $H = 640
$sheet = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::White)

$titleFont = New-Object System.Drawing.Font 'Segoe UI', 12, ([System.Drawing.FontStyle]::Bold)
$font = New-Object System.Drawing.Font 'Segoe UI', 8.5
$ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 34, 42))
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 118, 130))
$paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(246, 247, 249))
$night = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 34, 38))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

$colX = 30
foreach ($m in $marks) {
    $y = 22
    $g.DrawString($m.Name, $titleFont, $ink, [float]$colX, [float]$y)
    $y += 30

    foreach ($bg in @('light', 'dark')) {
        $stripBrush = if ($bg -eq 'light') { $paper } else { $night }
        $labelBrush = if ($bg -eq 'light') { $muted } else { $white }
        $g.FillRectangle($stripBrush, [float]($colX - 10), [float]$y, ($colW - 30), 90)

        $x = $colX
        foreach ($s in $shown) {
            $img = [System.Drawing.Image]::FromFile((Join-Path $IconDir "$($m.Prefix)-$s.png"))
            $g.DrawImageUnscaled($img, [int]$x, [int]($y + 44 - $s / 2))
            $g.DrawString("${s}", $font, $labelBrush, [float]$x, [float]($y + 72))
            $x += [Math]::Max($s, 26) + 18
            $img.Dispose()
        }
        $y += 100
    }

    # 16px at 8x, both grounds, so the actual pixels are visible.
    $g.DrawString('16px at 8x', $font, $muted, [float]$colX, [float]$y)
    $y += 16
    $img = [System.Drawing.Image]::FromFile((Join-Path $IconDir "$($m.Prefix)-16.png"))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.FillRectangle($paper, [float]$colX, [float]$y, 112, 112)
    $g.DrawImage($img, [int]$colX, [int]$y, 112, 112)
    $g.FillRectangle($night, [float]($colX + 124), [float]$y, 112, 112)
    $g.DrawImage($img, [int]($colX + 124), [int]$y, 112, 112)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $img.Dispose()
    $y += 128

    $g.DrawString('128px', $font, $muted, [float]$colX, [float]$y)
    $y += 16
    $big = [System.Drawing.Image]::FromFile((Join-Path $IconDir "$($m.Prefix)-128.png"))
    $g.DrawImageUnscaled($big, [int]$colX, [int]$y)
    $big.Dispose()

    $colX += $colW
}

$g.DrawString('Waypoint icon candidates - strips are true pixel size', $font, $muted, 30, [float]($H - 22))
$sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $sheet.Dispose()
Write-Output "wrote $Out"
