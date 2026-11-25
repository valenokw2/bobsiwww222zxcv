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
    $certificateInfo = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)
    if ($certificateInfo.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected signing certificate subject: $($certificateInfo.Subject)"
    }
    Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}
else {
    throw 'PixelOverlay.cer was not found. Keep the certificate beside Install.ps1 or pass -CertificatePath.'
}

# The UWP packaging target nests Dependencies under its generated *_Test folder.
# Search the extracted release tree and include only x64 framework packages.
$dependencyDirectories = @(Get-ChildItem -Path $PSScriptRoot -Directory -Recurse |
    Where-Object { $_.Name -eq 'Dependencies' })
$dependencies = @(
    foreach ($dependencyDirectory in $dependencyDirectories) {
        $x64Directory = Join-Path $dependencyDirectory.FullName 'x64'
        if (Test-Path $x64Directory) {
            Get-ChildItem -Path $x64Directory -File |
                Where-Object { $_.Extension -in '.appx', '.msix' } |
                Select-Object -ExpandProperty FullName
        }
    }
)

if ($dependencies.Count -gt 0) {
    Add-AppxPackage -Path $package -DependencyPath $dependencies
}
else {
    Add-AppxPackage -Path $package
}

Write-Host 'Pixel Overlay is installed.'
Write-Host 'Open Xbox Game Bar with Win+G, open the Widgets menu, and select Pixel Overlay.'
