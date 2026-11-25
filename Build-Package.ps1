[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'AppPackages'),
    [string]$SigningPfxPath = $env:PIXEL_OVERLAY_SIGNING_PFX_PATH,
    [string]$SigningPfxPassword = $env:PIXEL_OVERLAY_SIGNING_PFX_PASSWORD
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SigningPfxPath) -or !(Test-Path -LiteralPath $SigningPfxPath)) {
    throw 'A persistent signing PFX is required. Run scripts/Initialize-SigningCertificate.ps1 once and configure the repository Actions secrets.'
}
if ([string]::IsNullOrWhiteSpace($SigningPfxPassword)) {
    throw 'PIXELOVERLAY_SIGNING_PFX_PASSWORD is empty.'
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) {
    throw 'Visual Studio with the Universal Windows Platform development workload is required.'
}

$vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (!$vs) {
    throw 'MSBuild was not found in Visual Studio.'
}
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
if (!(Test-Path $msbuild)) {
    throw 'MSBuild.exe was not found.'
}

$securePassword = ConvertTo-SecureString $SigningPfxPassword -AsPlainText -Force
$cert = Import-PfxCertificate -FilePath (Resolve-Path $SigningPfxPath).Path -CertStoreLocation 'Cert:\CurrentUser\My' -Password $securePassword -Exportable
if (!$cert) {
    throw 'The signing certificate could not be imported from the PFX.'
}

$thumbprint = $cert.Thumbprint
try {
    if ($cert.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected signing certificate subject '$($cert.Subject)'. Expected CN=PixelOverlayDeveloper."
    }
    if ($cert.NotAfter -le (Get-Date).AddDays(30)) {
        throw "The signing certificate expires too soon: $($cert.NotAfter)."
    }

    $hasCodeSigningEku = @($cert.EnhancedKeyUsageList | Where-Object {
        $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3'
    }).Count -gt 0
    if (!$hasCodeSigningEku) {
        throw 'The supplied certificate does not contain the Code Signing EKU.'
    }
    if (!$cert.HasPrivateKey) {
        throw 'The imported signing certificate does not contain its private key.'
    }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $cer = Join-Path $OutputDirectory 'PixelOverlay.cer'
    Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null

    $packageDirectory = (Resolve-Path $OutputDirectory).Path
    if (!$packageDirectory.EndsWith('\')) {
        $packageDirectory += '\'
    }

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
        "/p:PackageCertificateThumbprint=$thumbprint"
    )

    & $msbuild @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Package build failed with exit code $LASTEXITCODE."
    }

    $packages = @(Get-ChildItem -Path $OutputDirectory -Recurse -File | Where-Object {
        $_.Extension -in '.msix', '.appx'
    })
    if ($packages.Count -eq 0) {
        throw 'MSBuild completed but did not produce an installable MSIX/AppX package.'
    }

    Write-Host "Created $($packages.Count) installable package(s) under: $OutputDirectory"
    Write-Host "Signing certificate: $($cert.Subject) [$thumbprint]"
    Write-Host "Public certificate exported to: $cer"
}
