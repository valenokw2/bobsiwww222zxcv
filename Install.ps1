[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$CertificatePath = (Join-Path $PSScriptRoot 'PixelOverlay.cer')
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackagePath).Path
if ([System.IO.Path]::GetExtension($package).ToLowerInvariant() -notin @('.msix', '.appx')) {
    throw 'PackagePath must point to an .msix or .appx package.'
}

if ($CertificatePath -and (Test-Path $CertificatePath)) {
    $certificate = (Resolve-Path $CertificatePath).Path
    $certificateInfo = Get-PfxCertificate -FilePath $certificate
    if ($certificateInfo.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected signing certificate subject: $($certificateInfo.Subject)"
    }
    Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}
else {
    throw 'PixelOverlay.cer was not found. Keep the certificate beside Install.ps1 or pass -CertificatePath.'
}

# Install x64 framework dependencies packaged by the UWP build, if any.
$dependencyRoot = Join-Path $PSScriptRoot 'Dependencies'
$dependencies = @()
if (Test-Path $dependencyRoot) {
    $dependencies = @(Get-ChildItem -Path $dependencyRoot -Recurse -File |
        Where-Object { $_.Extension -in '.appx', '.msix' -and $_.FullName -match '[\\/]x64[\\/]' } |
        Select-Object -ExpandProperty FullName)
}

if ($dependencies.Count -gt 0) {
    Add-AppxPackage -Path $package -DependencyPath $dependencies
}
else {
    Add-AppxPackage -Path $package
}

Write-Host 'Pixel Overlay is installed.'
Write-Host 'Open Xbox Game Bar with Win+G, open the Widgets menu, and select Pixel Overlay.'
