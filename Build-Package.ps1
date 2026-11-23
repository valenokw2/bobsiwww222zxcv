[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'AppPackages')
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Visual Studio 2022 with the Universal Windows Platform development workload is required.' }
$vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (!$vs) { throw 'MSBuild was not found in Visual Studio.' }
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
if (!(Test-Path $msbuild)) { throw 'MSBuild.exe was not found.' }

# A sideload package must be signed. The subject exactly matches Package.appxmanifest.
$subject = 'CN=PixelOverlayDeveloper'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $subject | Select-Object -First 1
if (!$cert) { $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cer = Join-Path $OutputDirectory 'PixelOverlay.cer'
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null

$arguments = @(
  (Join-Path $PSScriptRoot 'PixelOverlay.sln'), '/restore',
  "/p:Configuration=$Configuration", '/p:Platform=x64', '/p:GenerateAppxPackageOnBuild=true',
  '/p:AppxBundle=Never', "/p:AppxPackageDir=$OutputDirectory\",
  '/p:AppxPackageSigningEnabled=true', "/p:PackageCertificateThumbprint=$($cert.Thumbprint)"
)
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw "Package build failed with exit code $LASTEXITCODE." }
Write-Host "Package and certificate were created under: $OutputDirectory"
