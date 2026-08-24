Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $PSScriptRoot 'logo-source.png'
$scratch = $PSScriptRoot
$pink = @(255, 80, 140)

$src = New-Object System.Drawing.Bitmap $srcPath
$w = $src.Width; $h = $src.Height

$data = $src.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h),
  [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
  [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$bytes = New-Object byte[] ($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$src.UnlockBits($data); $src.Dispose()

$minX = $w; $minY = $h; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $h; $y++) {
  $row = $y * $stride
  for ($x = 0; $x -lt $w; $x++) {
    if ($bytes[$row + $x * 4 + 3] -eq 0) { continue }
    if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
    if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
  }
}
$cw = $maxX - $minX + 1; $ch = $maxY - $minY + 1

# Measure a wall in the source so the scale can be chosen to land it on whole pixels.
$midY = [int](($minY + $maxY) / 2)
$row = $midY * $stride
$runs = @(); $in = $false; $start = 0
for ($x = 0; $x -lt $w; $x++) {
  $a = $bytes[$row + $x * 4 + 3]
  if ($a -gt 128 -and -not $in) { $in = $true; $start = $x }
  elseif ($a -le 128 -and $in) { $in = $false; $runs += ($x - $start) }
}
$wallSrc = ($runs | Measure-Object -Minimum).Minimum
$wallRatio = $wallSrc / $cw
"content $cw x $ch, wall $wallSrc px, ratio {0:F5}" -f $wallRatio

$tinted = New-Object System.Drawing.Bitmap $cw, $ch, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $ch; $y++) {
  $r = ($y + $minY) * $stride
  for ($x = 0; $x -lt $cw; $x++) {
    $a = $bytes[$r + ($x + $minX) * 4 + 3]
    $tinted.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $pink[0], $pink[1], $pink[2]))
  }
}

# Pick a content width where the wall lands closest to a whole number of pixels and
# the leftover margin splits evenly, so both edges sit on the same grid lines.
function Pick-Fit([int]$size, [double]$maxPadFraction) {
  # Walk from the largest usable size down and take the first fit that is good
  # enough. Accuracy on the wall matters, but not at the cost of leaving the mark
  # rattling around in half an empty frame, so the search prefers size and only
  # requires the wall to land near a whole pixel.
  $minContent = [int][Math]::Floor($size * 0.55)
  $maxContent = [int][Math]::Floor($size * (1.0 - 2 * $maxPadFraction))
  $fallback = $null

  for ($c = $maxContent; $c -ge $minContent; $c--) {
    if ((($size - $c) % 2) -ne 0) { continue }      # integer margin on both sides
    $wall = $c * $wallRatio
    if ($wall -lt 1.4) { continue }
    $err = [Math]::Abs($wall - [Math]::Round($wall))
    $cand = [pscustomobject]@{ Content = $c; Wall = $wall; Err = $err }
    if ($err -le 0.12) { return $cand }
    if ($null -eq $fallback -or $err -lt $fallback.Err) { $fallback = $cand }
  }
  return $fallback
}

function Render([int]$size) {
  $fit = Pick-Fit $size 0.04
  $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($out)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.PixelOffsetMode = 'HighQuality'
  $g.CompositingQuality = 'HighQuality'

  $dw = $fit.Content
  $dh = [int][Math]::Round($dw * $ch / $cw)
  if ((($size - $dh) % 2) -ne 0) { $dh += 1 }
  $g.DrawImage($tinted, [int](($size - $dw) / 2), [int](($size - $dh) / 2), $dw, $dh)
  $g.Dispose()
  "  {0,2}px -> content {1}, wall {2:F2} (err {3:F2})" -f $size, $fit.Content, $fit.Wall, $fit.Err | Write-Host
  return $out
}

function Check-Symmetry($bmp) {
  $y = [int]($bmp.Height * 0.45)
  $runs = @(); $in = $false; $start = 0
  for ($x = 0; $x -lt $bmp.Width; $x++) {
    $a = $bmp.GetPixel($x, $y).A
    if ($a -gt 100 -and -not $in) { $in = $true; $start = $x }
    elseif ($a -le 100 -and $in) { $in = $false; $runs += ($x - $start) }
  }
  return ($runs -join '/')
}

function New-Dib($bmp) {
  $size = $bmp.Width
  $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2))
  $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0); $bw.Write([int]0)
  $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
  for ($y = $size - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $size; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  $mr = [Math]::Ceiling($size / 8.0); if ($mr % 4 -ne 0) { $mr += 4 - ($mr % 4) }
  for ($y = 0; $y -lt $size; $y++) { $bw.Write((New-Object byte[] $mr)) }
  $bw.Flush(); $b = $ms.ToArray(); $bw.Dispose(); $ms.Dispose(); return ,$b
}

function Write-Ico($bitmaps, $outPath) {
  $dibs = @(); foreach ($b in $bitmaps) { $dibs += ,(New-Dib $b) }
  $fs = New-Object System.IO.FileStream($outPath, [System.IO.FileMode]::Create)
  $bw = New-Object System.IO.BinaryWriter($fs)
  $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$bitmaps.Count)
  $offset = 6 + 16 * $bitmaps.Count
  for ($i = 0; $i -lt $bitmaps.Count; $i++) {
    $s = $bitmaps[$i].Width
    $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$dibs[$i].Length); $bw.Write([int]$offset); $offset += $dibs[$i].Length
  }
  foreach ($d in $dibs) { $bw.Write($d) }
  $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

$sizes = @(16, 20, 24, 32, 48)
$bmps = @(); foreach ($s in $sizes) { $bmps += (Render $s) }
'--- wall widths across the middle (should match left and right) ---'
for ($i = 0; $i -lt $sizes.Count; $i++) { "  {0,2}px : {1}" -f $sizes[$i], (Check-Symmetry $bmps[$i]) }

Write-Ico $bmps "$scratch\qlow-aligned.ico"

$sheet = New-Object System.Drawing.Bitmap (5 * 112 + 6 * 12), (112 + 2 * 12)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
  $g.DrawImage($bmps[$i], (12 + $i * (112 + 12)), 12, 112, 112)
}
$g.Dispose()
$sheet.Save("$scratch\qlow-aligned.png", [System.Drawing.Imaging.ImageFormat]::Png)
'written qlow-aligned.ico and preview'
