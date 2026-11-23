Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
function New-Logo([string]$Name, [int]$Width, [int]$Height = $Width) {
  $path = Join-Path $assets $Name
  if (Test-Path $path) {
    $existing = [System.Drawing.Image]::FromFile($path)
    $valid = $existing.Width -eq $Width -and $existing.Height -eq $Height
    $existing.Dispose()
    if ($valid) { return }
    Remove-Item -LiteralPath $path -Force
  }
  $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
  $g = [System.Drawing.Graphics]::FromImage($bitmap); $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $size = [Math]::Min($Width, $Height); $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255,110,168,254), [Math]::Max(2, [int]($size / 12)))
  $x = $Width / 2; $y = $Height / 2; $r = $size * .28
  $g.DrawEllipse($pen, $x-$r, $y-$r, $r*2, $r*2); $g.DrawLine($pen, $x-$r*1.4, $y, $x+$r*1.4, $y); $g.DrawLine($pen, $x, $y-$r*1.4, $x, $y+$r*1.4)
  $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $pen.Dispose(); $g.Dispose(); $bitmap.Dispose()
}
New-Logo 'StoreLogo.png' 50; New-Logo 'Square44x44Logo.png' 44; New-Logo 'Square150x150Logo.png' 150; New-Logo 'Wide310x150Logo.png' 310 150; New-Logo 'SplashScreen.png' 620
