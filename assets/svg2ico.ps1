Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework, System.Drawing

# The three filled outlines from the source SVG: face, left ear sweep, right ear sweep.
# WPF's path mini-language accepts SVG path data directly, so no conversion is needed.
$paths = @(
'M14,27.2c-0.9,0-2-0.1-2.8-0.3c-3.3-0.9-5.9-3.5-6.8-6.4c-0.6-2.2-0.4-3.7,0.9-5.2c0.9-1,1.7-2.2,2.5-3.4 c0.4-0.6,0.8-1.2,1.3-1.8c0.2-0.3,0.6-0.7,1-0.9c2.4-0.8,4.8-0.8,7.2,0c0.5,0.2,0.8,0.6,1,0.8c1.5,2.1,3.2,4.4,4.8,6.8 c0.3,0.4,0.5,1.2,0.3,1.7c-0.1,0.4-0.2,0.8-0.3,1.1c-0.2,0.8-0.3,1.3-0.9,2.4c-1.3,2.5-4.8,4.9-7.3,5.1 C14.7,27.2,14.4,27.2,14,27.2z M13.8,9.7c-1.1,0-2.2,0.2-3.2,0.6c-0.2,0.1-0.4,0.2-0.6,0.5c-0.4,0.6-0.8,1.2-1.3,1.8 c-0.8,1.2-1.6,2.4-2.6,3.5c-1.1,1.2-1.3,2.4-0.7,4.2c0.8,2.6,3.2,4.9,6.1,5.7c1,0.3,2.7,0.3,3.4,0.2l0,0c2.1-0.1,5.3-2.4,6.4-4.6 c0.5-1,0.6-1.4,0.8-2.2c0.1-0.3,0.2-0.7,0.3-1.1c0.1-0.2,0-0.7-0.2-0.9c-1.6-2.4-3.3-4.7-4.8-6.8c-0.2-0.2-0.3-0.4-0.5-0.4 C15.9,9.9,14.8,9.7,13.8,9.7z',
'M3.8,16.4l-0.3-0.9c-0.2-0.6-0.4-1.2-0.6-1.7c-0.4-1.2-0.8-2.4-1.1-3.5C1.5,8.7,1.3,7.1,1.2,5.5C1.1,4.9,1,4.4,0.9,3.8 c0-0.4-0.2-1.5,0.8-2c1.1-0.6,1.8,0.3,2,0.6l1.3,1.5c1.1,1.3,2.2,2.5,3.3,3.8C9.1,8.4,9.1,9.1,8.4,10c-0.9,1.2-1.9,2.5-2.8,3.9 L3.8,16.4z M2.5,2.6c-0.1,0-0.1,0-0.2,0.1C2,2.8,1.9,3,1.9,3.7C2,4.2,2.1,4.8,2.1,5.4C2.3,6.9,2.5,8.5,2.8,10 c0.2,1.1,0.6,2.2,1,3.4c0.1,0.3,0.2,0.5,0.3,0.8l0.7-0.9c1-1.4,1.9-2.7,2.8-3.9c0.4-0.5,0.3-0.7,0-1C6.5,7,5.4,5.8,4.4,4.5L3.1,3 C2.8,2.7,2.6,2.6,2.5,2.6z',
'M23.2,16.8L22,15c-1.1-1.6-2.3-3.3-3.4-4.9c-0.2-0.4-0.1-1.1,0.1-1.4l0.2-0.2c1.6-2,3.3-4.1,5-6.1c0.4-0.5,1-0.6,1.6-0.4 c0.6,0.2,1,0.9,1,1.6c-0.2,5.3-1,9.1-2.6,12.5c-0.1,0.1-0.2,0.2-0.2,0.3L23.2,16.8z M25.1,2.8c-0.1,0-0.2,0-0.3,0.2 c-1.7,2-3.4,4.1-5,6.1l-0.2,0.2c0,0.1-0.1,0.3,0,0.3c1.1,1.6,2.2,3.2,3.3,4.9l0.4,0.7c1.4-3.1,2.1-6.8,2.3-11.6 c0-0.4-0.2-0.6-0.3-0.6C25.2,2.8,25.1,2.8,25.1,2.8z'
)

$viewBox = 28.3

function Render-Size([int]$size, [byte]$r, [byte]$g, [byte]$b, [double]$pad, [double]$strokePx = 0) {
  $visual = New-Object System.Windows.Media.DrawingVisual
  $dc = $visual.RenderOpen()

  # Leave a little breathing room so the artwork does not touch the tray edge.
  $usable = $size * (1.0 - 2 * $pad)
  $scale = $usable / $viewBox
  $offset = $size * $pad

  $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform($offset, $offset)))
  $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))

  $colour = [System.Windows.Media.Color]::FromRgb($r, $g, $b)
  $brush = New-Object System.Windows.Media.SolidColorBrush($colour)
  $brush.Freeze()

  foreach ($p in $paths) {
    $geo = [System.Windows.Media.Geometry]::Parse(($p -replace '\s+', ' '))
    $pen = $null
    if ($strokePx -gt 0) { $pen = New-Object System.Windows.Media.Pen($brush, ($strokePx / $scale)); $pen.Freeze() }
    $dc.DrawGeometry($brush, $pen, $geo)
  }

  $dc.Pop(); $dc.Pop()
  $dc.Close()

  $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
  $rtb.Render($visual)

  $stride = $size * 4
  $buf = New-Object byte[] ($stride * $size)
  $rtb.CopyPixels($buf, $stride, 0)

  # RenderTargetBitmap hands back premultiplied alpha; ICO wants it straight.
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  for ($y = 0; $y -lt $size; $y++) {
    for ($x = 0; $x -lt $size; $x++) {
      $i = $y * $stride + $x * 4
      $a = $buf[$i + 3]
      if ($a -eq 0) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0)); continue }
      $bb = [Math]::Min(255, [int]($buf[$i]     * 255 / $a))
      $gg = [Math]::Min(255, [int]($buf[$i + 1] * 255 / $a))
      $rr = [Math]::Min(255, [int]($buf[$i + 2] * 255 / $a))
      $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $rr, $gg, $bb))
    }
  }
  return $bmp
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
  $bw.Flush(); $bytes = $ms.ToArray(); $bw.Dispose(); $ms.Dispose()
  return ,$bytes
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

$scratch = 'C:\Users\MNIMIP~1\AppData\Local\Temp\claude\G--baldcat-website\a0a6ac25-c6b9-40ed-9688-7bd481163913\scratchpad'

$rose = @(255, 80, 140)

# How much extra line weight each size needs. At 32 the artwork is already about a
# pixel wide and needs nothing; at 16 the same line falls below one pixel and
# dissolves into grey, so it gets thickened until it survives.
$strokeFor = @{ 16 = 0.8; 20 = 0.6; 24 = 0.4; 32 = 0.0; 48 = 0.0 }
$sizes = @(16, 20, 24, 32, 48)

$bmps = @()
foreach ($s in $sizes) { $bmps += (Render-Size $s $rose[0] $rose[1] $rose[2] 0.06 $strokeFor[$s]) }
Write-Ico $bmps "$scratch\svgcat-final.ico"
"written svgcat-final.ico with sizes: $($sizes -join ', ')"

# Preview each frame at its own weight, magnified, on dark and light.
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
$sheet.Save("$scratch\svgcat-final.png", [System.Drawing.Imaging.ImageFormat]::Png)
'preview: 16 | 20 | 24 | 32 | 48, dark row then light row'
