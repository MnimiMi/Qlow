Add-Type -AssemblyName System.Drawing

$srcPath = 'C:\Users\Mnimi PC\Desktop\34343434.png'
$scratch = 'C:\Users\MNIMIP~1\AppData\Local\Temp\claude\G--baldcat-website\a0a6ac25-c6b9-40ed-9688-7bd481163913\scratchpad'

$pink = @(255, 80, 140)

$src = New-Object System.Drawing.Bitmap $srcPath
$w = $src.Width; $h = $src.Height

# Work on the actual artwork, not the empty margin around it.
$minX = $w; $minY = $h; $maxX = -1; $maxY = -1
$data = $src.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h),
  [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
  [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$stride = $data.Stride
$bytes = New-Object byte[] ($stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$src.UnlockBits($data)

for ($y = 0; $y -lt $h; $y++) {
  $row = $y * $stride
  for ($x = 0; $x -lt $w; $x++) {
    if ($bytes[$row + $x * 4 + 3] -eq 0) { continue }
    if ($x -lt $minX) { $minX = $x }
    if ($x -gt $maxX) { $maxX = $x }
    if ($y -lt $minY) { $minY = $y }
    if ($y -gt $maxY) { $maxY = $y }
  }
}
"content bounds: X $minX..$maxX  Y $minY..$maxY  (canvas $w x $h)"

$cw = $maxX - $minX + 1
$ch = $maxY - $minY + 1

# Flat pink everywhere, alpha untouched. Painting the fully transparent pixels too
# means any later interpolation only ever blends alpha, never colour, so the edges
# cannot pick up a pale fringe from the white that was hiding under them.
$tinted = New-Object System.Drawing.Bitmap $cw, $ch, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $ch; $y++) {
  $row = ($y + $minY) * $stride
  for ($x = 0; $x -lt $cw; $x++) {
    $a = $bytes[$row + ($x + $minX) * 4 + 3]
    $tinted.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $pink[0], $pink[1], $pink[2]))
  }
}
$src.Dispose()

function Render([int]$size, [double]$pad) {
  $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($out)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.PixelOffsetMode = 'HighQuality'
  $g.CompositingQuality = 'HighQuality'

  $usable = $size * (1.0 - 2 * $pad)
  $scale = [Math]::Min($usable / $cw, $usable / $ch)
  $dw = $cw * $scale; $dh = $ch * $scale
  $g.DrawImage($tinted, (($size - $dw) / 2.0), (($size - $dh) / 2.0), $dw, $dh)
  $g.Dispose()
  return $out
}

function New-Dib($bmp) {
  $size = $bmp.Width
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([int]40); $bw.Write([int]$size); $bw.Write([int]($size * 2))
  $bw.Write([int16]1); $bw.Write([int16]32)
  $bw.Write([int]0); $bw.Write([int]0)
  $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
  for ($y = $size - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $size; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  $maskRow = [Math]::Ceiling($size / 8.0)
  if ($maskRow % 4 -ne 0) { $maskRow += 4 - ($maskRow % 4) }
  for ($y = 0; $y -lt $size; $y++) { $bw.Write((New-Object byte[] $maskRow)) }
  $bw.Flush(); $b = $ms.ToArray(); $bw.Dispose(); $ms.Dispose()
  return ,$b
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
    $bw.Write([int]$dibs[$i].Length); $bw.Write([int]$offset)
    $offset += $dibs[$i].Length
  }
  foreach ($d in $dibs) { $bw.Write($d) }
  $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

$sizes = @(16, 20, 24, 32, 48)
$bmps = @(); foreach ($s in $sizes) { $bmps += (Render $s 0.05) }
Write-Ico $bmps "$scratch\outglow.ico"
"written outglow.ico: $($sizes -join ', ')"

$sheet = New-Object System.Drawing.Bitmap (5 * 112 + 6 * 12), (2 * 112 + 3 * 12)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(255, 70, 70, 70))
$bgs = @([System.Drawing.Color]::FromArgb(255, 32, 32, 32), [System.Drawing.Color]::FromArgb(255, 243, 243, 243))
for ($r = 0; $r -lt 2; $r++) {
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $x = 12 + $i * (112 + 12); $y = 12 + $r * (112 + 12)
    $br = New-Object System.Drawing.SolidBrush $bgs[$r]
    $g.FillRectangle($br, $x, $y, 112, 112); $br.Dispose()
    $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.DrawImage($bmps[$i], $x, $y, 112, 112)
  }
}
$g.Dispose()
$sheet.Save("$scratch\outglow-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
'preview: 16 | 20 | 24 | 32 | 48, dark row then light row'
