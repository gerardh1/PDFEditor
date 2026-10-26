$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot "AcrobatPdfEditor\Assets"
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null

function New-ArcoCatBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Transparent square canvas
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 512.0

    # =========================================================================
    # 1. LEFT SIDE: Adobe Acrobat Style Emblem (Red Rounded Square + Swirl + PDF)
    # =========================================================================
    $adobeX = [float](18 * $s)
    $adobeY = [float](88 * $s)
    $adobeW = [float](230 * $s)
    $adobeH = [float](290 * $s)
    $cornerR = [float](48 * $s)

    $adobePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $cornerR * 2
    $adobePath.AddArc($adobeX, $adobeY, $d, $d, 180, 90)
    $adobePath.AddArc($adobeX + $adobeW - $d, $adobeY, $d, $d, 270, 90)
    $adobePath.AddArc($adobeX + $adobeW - $d, $adobeY + $adobeH - $d, $d, $d, 0, 90)
    $adobePath.AddArc($adobeX, $adobeY + $adobeH - $d, $d, $d, 90, 90)
    $adobePath.CloseFigure()

    # Drop shadow under Adobe tile
    $shadowPath = $adobePath.Clone()
    $shadowMatrix = New-Object System.Drawing.Drawing2D.Matrix
    $shadowMatrix.Translate([float](6 * $s), [float](10 * $s))
    $shadowPath.Transform($shadowMatrix)
    $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(75, 0, 0, 0))
    $g.FillPath($shadowBrush, $shadowPath)

    # Rich Adobe Crimson Gradient
    $adobeGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new($adobeX, $adobeY),
        [System.Drawing.PointF]::new($adobeX + $adobeW, $adobeY + $adobeH),
        [System.Drawing.Color]::FromArgb(255, 237, 34, 36),
        [System.Drawing.Color]::FromArgb(255, 168, 12, 20)
    )
    $g.FillPath($adobeGrad, $adobePath)

    # Subtle glossy border on Adobe tile
    $adobeBorderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 255, 120, 120), [float](5 * $s))
    $g.DrawPath($adobeBorderPen, $adobePath)

    # Stylized Adobe "A" / Acrobat Trefoil Swirl in White
    $swirlPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [float](17 * $s))
    $swirlPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $swirlPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $swirlPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $swirlPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](132 * $s), [float](125 * $s)),
        [System.Drawing.PointF]::new([System.Single](95 * $s), [System.Single](225 * $s)),
        [System.Drawing.PointF]::new([System.Single](60 * $s), [System.Single](265 * $s)),
        [System.Drawing.PointF]::new([System.Single](185 * $s), [System.Single](242 * $s)),
        [System.Drawing.PointF]::new([System.Single](145 * $s), [System.Single](140 * $s)),
        [System.Drawing.PointF]::new([System.Single](132 * $s), [System.Single](125 * $s))
    )
    $g.DrawClosedCurve($swirlPen, $swirlPts, 0.45, [System.Drawing.Drawing2D.FillMode]::Alternate)

    # "PDF" Label on bottom of Adobe Emblem
    $pdfFont = New-Object System.Drawing.Font("Arial", [float](40 * $s), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sfCenter = New-Object System.Drawing.StringFormat
    $sfCenter.Alignment = [System.Drawing.StringAlignment]::Center
    $sfCenter.LineAlignment = [System.Drawing.StringAlignment]::Center
    $pdfRect = New-Object System.Drawing.RectangleF($adobeX, [float](298 * $s), $adobeW, [float](64 * $s))
    $g.DrawString("PDF", $pdfFont, [System.Drawing.Brushes]::White, $pdfRect, $sfCenter)

    # =========================================================================
    # 2. RIGHT SIDE: Cartoon Cat Head (White & Orange Fur, Black Nose & Whiskers)
    # =========================================================================
    $catCx = [float](334 * $s)
    $catCy = [float](262 * $s)
    $headRx = [float](124 * $s)
    $headRy = [float](114 * $s)

    $orangeFurBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 140, 36))
    $darkOrangeFurBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 217, 95, 18))
    $whiteFurBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 252, 252, 254))
    $pinkEarBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 172, 188))
    $outlinePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 34, 38, 46), [float](9 * $s))
    $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Cat head drop shadow
    $catShadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
    $g.FillEllipse($catShadowBrush, $catCx - $headRx + [float](5 * $s), $catCy - $headRy + [float](10 * $s), $headRx * 2, $headRy * 2)

    # Left Cat Ear (Orange outer fur + Pink inner ear)
    $leftEarPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](225 * $s), [float](208 * $s)),
        [System.Drawing.PointF]::new([float](216 * $s), [float](94 * $s)),
        [System.Drawing.PointF]::new([float](305 * $s), [float](155 * $s))
    )
    $g.FillPolygon($orangeFurBrush, $leftEarPts)
    $g.DrawPolygon($outlinePen, $leftEarPts)

    $leftInnerEarPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](237 * $s), [float](192 * $s)),
        [System.Drawing.PointF]::new([float](231 * $s), [float](118 * $s)),
        [System.Drawing.PointF]::new([float](288 * $s), [float](160 * $s))
    )
    $g.FillPolygon($pinkEarBrush, $leftInnerEarPts)

    # Right Cat Ear (Orange outer fur + Pink inner ear)
    $rightEarPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](363 * $s), [float](155 * $s)),
        [System.Drawing.PointF]::new([float](452 * $s), [float](94 * $s)),
        [System.Drawing.PointF]::new([float](443 * $s), [float](208 * $s))
    )
    $g.FillPolygon($orangeFurBrush, $rightEarPts)
    $g.DrawPolygon($outlinePen, $rightEarPts)

    $rightInnerEarPts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](380 * $s), [float](160 * $s)),
        [System.Drawing.PointF]::new([float](437 * $s), [float](118 * $s)),
        [System.Drawing.PointF]::new([float](431 * $s), [float](192 * $s))
    )
    $g.FillPolygon($pinkEarBrush, $rightInnerEarPts)

    # Main Cat Head Base (White Fur)
    $headRect = New-Object System.Drawing.RectangleF([float]($catCx - $headRx), [float]($catCy - $headRy), [float]($headRx * 2), [float]($headRy * 2))
    $g.FillEllipse($whiteFurBrush, $headRect)

    # Clip to Cat Head for Orange Fur Patches (White & Orange bicolor cat pattern)
    $headClipPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $headClipPath.AddEllipse($headRect)
    $oldClip = $g.Clip
    $g.SetClip($headClipPath)

    # Left & Right Orange Forehead/Temple Patches (leaving white blaze in center & white lower face/cheeks)
    $g.FillEllipse($orangeFurBrush, [float](186 * $s), [float](120 * $s), [float](142 * $s), [float](135 * $s))
    $g.FillEllipse($orangeFurBrush, [float](338 * $s), [float](120 * $s), [float](142 * $s), [float](135 * $s))
    $g.FillEllipse($orangeFurBrush, [float](264 * $s), [float](115 * $s), [float](140 * $s), [float](78 * $s))

    # Cute Tabby Stripes on the Orange Forehead Fur
    $stripePen = New-Object System.Drawing.Pen($darkOrangeFurBrush, [float](8 * $s))
    $stripePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stripePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($stripePen, [float](334 * $s), [float](148 * $s), [float](334 * $s), [float](182 * $s))
    $g.DrawLine($stripePen, [float](311 * $s), [float](152 * $s), [float](319 * $s), [float](180 * $s))
    $g.DrawLine($stripePen, [float](357 * $s), [float](152 * $s), [float](349 * $s), [float](180 * $s))

    $g.Clip = $oldClip

    # Draw crisp outline around Cat Head
    $g.DrawEllipse($outlinePen, $headRect)

    # Fluffy White Cheek Tufts
    $cheekBrush = $whiteFurBrush
    $g.FillEllipse($cheekBrush, [float](202 * $s), [float](245 * $s), [float](62 * $s), [float](54 * $s))
    $g.FillEllipse($cheekBrush, [float](404 * $s), [float](245 * $s), [float](62 * $s), [float](54 * $s))

    # Cartoon Cat Eyes (Bright Dark Eyes with White Specular Catchlights)
    $eyeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 24, 28, 36))
    $g.FillEllipse($eyeBrush, [float](266 * $s), [float](218 * $s), [float](34 * $s), [float](38 * $s))
    $g.FillEllipse($eyeBrush, [float](368 * $s), [float](218 * $s), [float](34 * $s), [float](38 * $s))

    # Eye Catchlights
    $g.FillEllipse([System.Drawing.Brushes]::White, [float](273 * $s), [float](224 * $s), [float](12 * $s), [float](12 * $s))
    $g.FillEllipse([System.Drawing.Brushes]::White, [float](375 * $s), [float](224 * $s), [float](12 * $s), [float](12 * $s))
    $g.FillEllipse([System.Drawing.Brushes]::White, [float](288 * $s), [float](240 * $s), [float](6 * $s), [float](6 * $s))
    $g.FillEllipse([System.Drawing.Brushes]::White, [float](390 * $s), [float](240 * $s), [float](6 * $s), [float](6 * $s))

    # Black Cat Nose (Rounded Triangle)
    $blackNoseBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 18, 20, 24))
    $nosePts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](315 * $s), [float](264 * $s)),
        [System.Drawing.PointF]::new([float](353 * $s), [float](264 * $s)),
        [System.Drawing.PointF]::new([float](334 * $s), [float](286 * $s))
    )
    $g.FillClosedCurve($blackNoseBrush, $nosePts, [System.Drawing.Drawing2D.FillMode]::Alternate, [float]0.25)

    # Nose Shine
    $noseShineBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 255, 255, 255))
    $g.FillEllipse($noseShineBrush, [float](323 * $s), [float](266 * $s), [float](12 * $s), [float](5 * $s))

    # Cute Cat Mouth ("w" smile below black nose)
    $mouthPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 24, 28, 36), [float](6.5 * $s))
    $mouthPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $mouthPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($mouthPen, [float](302 * $s), [float](276 * $s), [float](32 * $s), [float](26 * $s), 0, 165)
    $g.DrawArc($mouthPen, [float](334 * $s), [float](276 * $s), [float](32 * $s), [float](26 * $s), 15, 165)

    # Black Whiskers (3 on Left, 3 on Right)
    $whiskerPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 15, 17, 22), [float](6.5 * $s))
    $whiskerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whiskerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # Left Whiskers
    $g.DrawLine($whiskerPen, [float](254 * $s), [float](268 * $s), [float](184 * $s), [float](254 * $s))
    $g.DrawLine($whiskerPen, [float](252 * $s), [float](282 * $s), [float](178 * $s), [float](282 * $s))
    $g.DrawLine($whiskerPen, [float](256 * $s), [float](296 * $s), [float](186 * $s), [float](310 * $s))

    # Right Whiskers
    $g.DrawLine($whiskerPen, [float](414 * $s), [float](268 * $s), [float](488 * $s), [float](254 * $s))
    $g.DrawLine($whiskerPen, [float](416 * $s), [float](282 * $s), [float](494 * $s), [float](282 * $s))
    $g.DrawLine($whiskerPen, [float](412 * $s), [float](296 * $s), [float](486 * $s), [float](310 * $s))

    $g.Dispose()
    return $bmp
}

# Save 256x256 PNG
$pngPath = Join-Path $assetsDir "ArcoCatIcon.png"
$bmp256 = New-ArcoCatBitmap 256
$bmp256.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp256.Dispose()

# Save Multi-Resolution Windows .ICO file (16, 32, 48, 64, 128, 256) with PNG frames
$icoPath = Join-Path $assetsDir "ArcoCatIcon.ico"
$installerIcoPath = Join-Path $PSScriptRoot "ArcoCatIcon.ico"
$sizes = @(16, 32, 48, 64, 128, 256)
$pngPayloads = New-Object System.Collections.Generic.List[byte[]]

foreach ($sz in $sizes) {
    $b = New-ArcoCatBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngPayloads.Add($ms.ToArray())
    $ms.Dispose()
    $b.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR Header
$bw.Write([uint16]0)                  # Reserved
$bw.Write([uint16]1)                  # Type: 1 = ICO
$bw.Write([uint16]$sizes.Count)       # Number of images

$dataOffset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bytes = $pngPayloads[$i]
    $dimByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }

    $bw.Write($dimByte)               # Width
    $bw.Write($dimByte)               # Height
    $bw.Write([byte]0)                # ColorCount
    $bw.Write([byte]0)                # Reserved
    $bw.Write([uint16]1)              # Planes
    $bw.Write([uint16]32)             # BitCount (32-bit ARGB)
    $bw.Write([uint32]$bytes.Length)  # BytesInRes
    $bw.Write([uint32]$dataOffset)    # ImageOffset
    $dataOffset += $bytes.Length
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngPayloads[$i])
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Copy-Item $icoPath $installerIcoPath -Force
Write-Host "Generated ArcoCatIcon.png and ArcoCatIcon.ico successfully!"
