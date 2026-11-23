$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage | Where-Object { $_.Name -eq 'PixelOverlay' }
if ($package) { Remove-AppxPackage -Package $package.PackageFullName; Write-Host 'Pixel Overlay was removed.' }
else { Write-Host 'Pixel Overlay is not installed for the current user.' }
