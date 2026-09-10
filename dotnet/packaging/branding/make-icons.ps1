# Draws two candidate marks as vector geometry and rasterises each at every
# size Windows asks for. Detail drops out below 24px on purpose: a straight
# downscale of a detailed mark turns to mush in the taskbar.

param([string]$OutDir)

Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-Surface([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    return @{ Bitmap = $bmp; Graphics = $g }
}

function Add-Ellipse($path, [double]$cx, [double]$cy, [double]$w, [double]$h, [double]$deg) {
    $sub = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sub.AddEllipse([float]($cx - $w / 2), [float]($cy - $h / 2), [float]$w, [float]$h)
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.RotateAt([float]$deg, (New-Object System.Drawing.PointF([float]$cx, [float]$cy)))
    $sub.Transform($m)
    $path.AddPath($sub, $false)
    $sub.Dispose(); $m.Dispose()
}

# --- Mark A: a waypoint pin over map contours ------------------------------
# The pin carries the whole mark at 16px, where contour lines are hopeless.
# Contours are context that only appears once there are pixels to spend.
function Draw-Terrain($g, [int]$s, [bool]$bare = $false) {
    $cx = $s * 0.5

    if ($s -ge 24 -and -not $bare) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#C08A33'))
        $pen.Width = [float]([Math]::Max(1.3, $s * 0.045))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        # Irregular closed curves, not concentric ellipses: even spacing reads
        # as a target rather than ground.
        $contours = @(
            @(0.11,0.63, 0.19,0.48, 0.35,0.41, 0.55,0.40, 0.74,0.45, 0.87,0.58, 0.82,0.75, 0.62,0.84, 0.38,0.84, 0.19,0.76),
            @(0.27,0.65, 0.35,0.56, 0.50,0.53, 0.66,0.57, 0.73,0.67, 0.62,0.76, 0.42,0.76, 0.31,0.72),
            @(0.40,0.67, 0.50,0.63, 0.60,0.67, 0.53,0.73, 0.43,0.72)
        )
        # Low tension: the curve must not overshoot past the canvas edge, which
        # is what turned the first attempt into trailing scribbles.
        foreach ($c in $contours) {
            $pts = @()
            for ($i = 0; $i -lt $c.Count; $i += 2) {
                $pts += (New-Object System.Drawing.PointF([float]($c[$i] * $s), [float]($c[$i + 1] * $s)))
            }
            $g.DrawClosedCurve($pen, $pts, 0.35, [System.Drawing.Drawing2D.FillMode]::Alternate)
        }
        $pen.Dispose()
    }

    # Teardrop: arc across the top, then two lines down to the tip. The gap in
    # the arc sits at the bottom, where the point attaches.
    $headR = $s * 0.195
    $headY = $s * 0.33
    $tipY = $s * 0.72
    if ($s -lt 24 -or $bare) { $headR = $s * 0.28; $headY = $s * 0.37; $tipY = $s * 0.90 }

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([float]($cx - $headR), [float]($headY - $headR), [float]($headR * 2), [float]($headR * 2), 140, 260)
    $path.AddLine([float]($cx + [Math]::Cos(40 * [Math]::PI / 180) * $headR), [float]($headY + [Math]::Sin(40 * [Math]::PI / 180) * $headR), [float]$cx, [float]$tipY)
    $path.CloseFigure()

    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#F0A92B'))
    $g.FillPath($gold, $path)

    # A dark eye keeps the head from reading as a solid blob at 32px and up.
    if ($s -ge 24) {
        $eye = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#7A5008'))
        $er = $headR * 0.42
        $g.FillEllipse($eye, [float]($cx - $er), [float]($headY - $er), [float]($er * 2), [float]($er * 2))
        $eye.Dispose()
    }

    $path.Dispose(); $gold.Dispose()
}

# --- Mark B: compass rose --------------------------------------------------
function Draw-Compass($g, [int]$s) {
    $cx = $s * 0.5
    $cy = $s * 0.5
    $light = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#F0A92B'))
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#A8710F'))

    function Star($long, $wide, $rot, $brushA, $brushB) {
        for ($i = 0; $i -lt 4; $i++) {
            $a = [Math]::PI / 2 * $i + $rot
            $tipX = $cx + [Math]::Cos($a) * $long
            $tipY = $cy + [Math]::Sin($a) * $long
            $lx = $cx + [Math]::Cos($a + [Math]::PI / 2) * $wide
            $ly = $cy + [Math]::Sin($a + [Math]::PI / 2) * $wide
            $rx = $cx + [Math]::Cos($a - [Math]::PI / 2) * $wide
            $ry = $cy + [Math]::Sin($a - [Math]::PI / 2) * $wide
            $g.FillPolygon($brushA, @(
                (New-Object System.Drawing.PointF([float]$tipX, [float]$tipY)),
                (New-Object System.Drawing.PointF([float]$lx, [float]$ly)),
                (New-Object System.Drawing.PointF([float]$cx, [float]$cy))))
            $g.FillPolygon($brushB, @(
                (New-Object System.Drawing.PointF([float]$tipX, [float]$tipY)),
                (New-Object System.Drawing.PointF([float]$rx, [float]$ry)),
                (New-Object System.Drawing.PointF([float]$cx, [float]$cy))))
        }
    }

    # Ring and diagonal points are the first things to go; below 24px they
    # only add grey mush around the cardinal star.
    if ($s -ge 24) {
        $ring = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#B87A1F'))
        $ring.Width = [float]([Math]::Max(1.4, $s * 0.05))
        $r = $s * 0.40
        $g.DrawEllipse($ring, [float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2))
        $ring.Dispose()
        Star ($s * 0.30) ($s * 0.055) ([Math]::PI / 4) $dark $light
    }

    $lf = if ($s -ge 24) { 0.46 } else { 0.48 }
    Star ($s * $lf) ($s * 0.10) 0 $light $dark

    $c = $s * 0.085
    $g.FillEllipse($dark, [float]($cx - $c), [float]($cy - $c), [float]($c * 2), [float]($c * 2))

    $light.Dispose(); $dark.Dispose()
}

function Build-Ico([string]$name, [scriptblock]$draw) {
    $pngs = @()
    foreach ($s in $Sizes) {
        $surf = New-Surface $s
        & $draw $surf.Graphics $s
        $ms = New-Object System.IO.MemoryStream
        $surf.Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
        $surf.Bitmap.Save((Join-Path $OutDir "$name-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $ms.Dispose(); $surf.Graphics.Dispose(); $surf.Bitmap.Dispose()
    }

    # ICONDIR + one ICONDIRENTRY per image, then the PNG payloads. PNG entries
    # are what Vista and later expect; a 256px entry cannot be a BMP anyway.
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
        $w.Write([Byte]$dim); $w.Write([Byte]$dim)
        $w.Write([Byte]0); $w.Write([Byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$p.Bytes.Length); $w.Write([UInt32]$offset)
        $offset += $p.Bytes.Length
    }
    foreach ($p in $pngs) { $w.Write($p.Bytes) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir "$name.ico"), $out.ToArray())
    $w.Dispose(); $out.Dispose()
    Write-Output "  wrote $name.ico ($($pngs.Count) sizes)"
}

function Draw-Pin($g, [int]$s) { Draw-Terrain $g $s $true }

Build-Ico 'waypoint-terrain' ${function:Draw-Terrain}
Build-Ico 'waypoint-pin' ${function:Draw-Pin}
Build-Ico 'waypoint-compass' ${function:Draw-Compass}
Write-Output "done"
