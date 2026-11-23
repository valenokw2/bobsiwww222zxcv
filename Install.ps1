[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackagePath, [string]$CertificatePath)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path $PackagePath).Path
if ($CertificatePath) {
    $certificate = (Resolve-Path $CertificatePath).Path
    Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}
Add-AppxPackage -Path $package
Write-Host 'Pixel Overlay is installed. Open Xbox Game Bar with Win+G, then choose Pixel Overlay from Widgets.'
