# make_icons.ps1
# Generates BlueSelf launcher icons from a single source image.
#
# Input : C:\Users\13243\Downloads\gaeum.jpg
# Output: Android res/mipmap-* legacy PNGs + adaptive foreground PNGs,
#         values/ic_launcher_background.xml (average color), and rewrites
#         the mipmap-anydpi-v26 adaptive-icon XMLs.
#
# Run:  powershell -ExecutionPolicy Bypass -File tools\make_icons.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = 'C:\Users\13243\Downloads\gaeum.jpg'
$res = 'e:\project\selftrans\app\src\main\res'

if (-not (Test-Path $src)) { throw "Source image not found: $src" }
if (-not (Test-Path $res)) { throw "Res dir not found: $res" }

$img = [System.Drawing.Image]::FromFile($src)
$side = [Math]::Min($img.Width, $img.Height)
$cropX = [int](($img.Width - $side) / 2)
$cropY = [int](($img.Height - $side) / 2)
$srcRect = New-Object System.Drawing.Rectangle($cropX, $cropY, $side, $side)

$pngFmt = [System.Drawing.Imaging.ImageFormat]::Png

function New-Bitmap($size) {
  return New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function New-Graphics($bmp) {
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  return $g
}

function Draw-CroppedImage($g, $dstSize) {
  $dstRect = New-Object System.Drawing.Rectangle(0, 0, $dstSize, $dstSize)
  $g.DrawImage($img, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
}

# ---- 1) Legacy square launcher + round launcher PNGs ----
$legacy = @{ 'mdpi' = 48; 'hdpi' = 72; 'xhdpi' = 96; 'xxhdpi' = 144; 'xxxhdpi' = 192 }
foreach ($k in $legacy.Keys) {
  $size = $legacy[$k]
  $dir = Join-Path $res "mipmap-$k"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null

  # square
  $bmp = New-Bitmap $size
  $g = New-Graphics $bmp
  Draw-CroppedImage $g $size
  $g.Dispose()
  $bmp.Save((Join-Path $dir 'ic_launcher.png'), $pngFmt)
  $bmp.Dispose()

  # round (circular clip)
  $bmp = New-Bitmap $size
  $g = New-Graphics $bmp
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddEllipse(0, 0, $size, $size)
  $g.SetClip($path)
  Draw-CroppedImage $g $size
  $g.ResetClip()
  $g.Dispose()
  $path.Dispose()
  $bmp.Save((Join-Path $dir 'ic_launcher_round.png'), $pngFmt)
  $bmp.Dispose()

  # remove legacy template webp if present
  foreach ($f in @('ic_launcher.webp', 'ic_launcher_round.webp')) {
    $old = Join-Path $dir $f
    if (Test-Path $old) { Remove-Item $old -Force }
  }
}

# ---- 2) Adaptive foreground PNG (66% safe zone on 432px canvas) ----
$fgSizes = @{ 'mdpi' = 108; 'hdpi' = 162; 'xhdpi' = 216; 'xxhdpi' = 324; 'xxxhdpi' = 432 }
foreach ($k in $fgSizes.Keys) {
  $canvas = $fgSizes[$k]
  $content = [int]($canvas * 0.66)
  $offset = [int](($canvas - $content) / 2)
  $dir = Join-Path $res "mipmap-$k"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null

  $bmp = New-Bitmap $canvas
  $g = New-Graphics $bmp
  $dstRect = New-Object System.Drawing.Rectangle($offset, $offset, $content, $content)
  $g.DrawImage($img, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose()
  $bmp.Save((Join-Path $dir 'ic_launcher_foreground.png'), $pngFmt)
  $bmp.Dispose()
}

# ---- 3) Average color -> values/ic_launcher_background.xml ----
$step = [Math]::Max(1, [int]($side / 64))
$r = 0; $gg = 0; $b = 0; $n = 0
for ($y = 0; $y -lt $side; $y += $step) {
  for ($x = 0; $x -lt $side; $x += $step) {
    $px = $img.GetPixel($cropX + $x, $cropY + $y)
    $r += $px.R; $gg += $px.G; $b += $px.B; $n++
  }
}
$avgR = [int]($r / $n); $avgG = [int]($gg / $n); $avgB = [int]($b / $n)
$hex = '#{0:X2}{1:X2}{2:X2}' -f $avgR, $avgG, $avgB
$valsDir = Join-Path $res 'values'
New-Item -ItemType Directory -Force -Path $valsDir | Out-Null
$bgXml = @"
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">$hex</color>
</resources>
"@
[System.IO.File]::WriteAllText((Join-Path $valsDir 'ic_launcher_background.xml'), $bgXml)

# ---- 4) Rewrite adaptive-icon XMLs ----
$adaptive = @'
<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
    <monochrome android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
'@
[System.IO.File]::WriteAllText((Join-Path $res 'mipmap-anydpi-v26\ic_launcher.xml'), $adaptive)
[System.IO.File]::WriteAllText((Join-Path $res 'mipmap-anydpi-v26\ic_launcher_round.xml'), $adaptive)

# ---- 5) Remove legacy template drawables no longer referenced ----
foreach ($f in @('ic_launcher_foreground.xml', 'ic_launcher_background.xml')) {
  $old = Join-Path $res "drawable\$f"
  if (Test-Path $old) { Remove-Item $old -Force }
}

$img.Dispose()
Write-Host "Done. Average color: $hex"
