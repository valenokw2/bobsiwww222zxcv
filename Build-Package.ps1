[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'AppPackages')
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Visual Studio with the Universal Windows Platform development workload is required.' }
$vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (!$vs) { throw 'MSBuild was not found in Visual Studio.' }
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
if (!(Test-Path $msbuild)) { throw 'MSBuild.exe was not found.' }

# Create a dedicated self-signed Code Signing certificate for sideload testing.
# Its subject must exactly match Package.appxmanifest.
$certParams = @{
    Type = 'Custom'
    Subject = 'CN=PixelOverlayDeveloper'
    FriendlyName = 'Pixel Overlay sideload test certificate'
    KeyAlgorithm = 'RSA'
    KeyLength = 2048
    HashAlgorithm = 'SHA256'
    KeyUsage = 'DigitalSignature'
    TextExtension = @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    NotAfter = (Get-Date).AddYears(2)
    CertStoreLocation = 'Cert:\CurrentUser\My'
}
$cert = New-SelfSignedCertificate @certParams

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cer = Join-Path $OutputDirectory 'PixelOverlay.cer'
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null

$packageDirectory = (Resolve-Path $OutputDirectory).Path
if (!$packageDirectory.EndsWith('\')) { $packageDirectory += '\' }
$arguments = @(
    (Join-Path $PSScriptRoot 'PixelOverlay.sln'),
    '/restore',
    '/m',
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    '/p:GenerateAppxPackageOnBuild=true',
    '/p:AppxBundle=Never',
    "/p:AppxPackageDir=$packageDirectory",
    '/p:AppxPackageSigningEnabled=true',
    "/p:PackageCertificateThumbprint=$($cert.Thumbprint)"
)
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw "Package build failed with exit code $LASTEXITCODE." }
$packages = @(Get-ChildItem -Path $OutputDirectory -Recurse -File | Where-Object { $_.Extension -in '.msix', '.appx' })
if ($packages.Count -eq 0) { throw 'MSBuild completed but did not produce an installable MSIX/AppX package.' }
Write-Host "Created $($packages.Count) installable package(s) under: $OutputDirectory"
Write-Host "Signing certificate: $cer"
