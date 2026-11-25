[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$CertificatePath = (Join-Path $PSScriptRoot 'PixelOverlay.cer')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-AppxManifestInfo {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (!$entry) { throw "Package '$Path' has no AppxManifest.xml." }
        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            try { [xml]$manifest = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }

        $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
        if (!$identity) { throw "Package '$Path' has no Package/Identity." }
        return [pscustomobject]@{
            Path = $resolved
            Manifest = $manifest
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = [version]$identity.GetAttribute('Version')
            Architecture = $identity.GetAttribute('ProcessorArchitecture').ToLowerInvariant()
        }
    }
    finally { $archive.Dispose() }
}

function Test-CompatibleArchitecture {
    param([string]$Actual, [string]$Required)

    $actual = if ($Actual) { $Actual.ToLowerInvariant() } else { '' }
    $required = if ($Required) { $Required.ToLowerInvariant() } else { 'x64' }
    if ($required -eq 'neutral') { return $actual -eq 'neutral' }
    return ($actual -eq $required -or $actual -eq 'neutral')
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetExtension($package).ToLowerInvariant() -notin @('.msix', '.appx')) {
    throw 'PackagePath must point to an .msix or .appx package.'
}

if (!$CertificatePath -or !(Test-Path -LiteralPath $CertificatePath)) {
    throw 'PixelOverlay.cer was not found. Keep the certificate beside Install.ps1 or pass -CertificatePath.'
}

$certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
$certificateInfo = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)
if ($certificateInfo.Subject -ne 'CN=PixelOverlayDeveloper') {
    throw "Unexpected signing certificate subject: $($certificateInfo.Subject)"
}
Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null

$mainInfo = Get-AppxManifestInfo -Path $package
$requiredNodes = $mainInfo.Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']")

# Release pages contain flat assets; local UWP build output nests frameworks in
# Dependencies\x64 or Dependencies\neutral. Inspect package identity rather than names.
$candidateFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse |
    Where-Object {
        $_.FullName -ne $package -and $_.Extension -in @('.appx', '.msix')
    })
$candidates = @(
    foreach ($file in $candidateFiles) {
        try { Get-AppxManifestInfo -Path $file.FullName }
        catch { Write-Warning "Skipping non-package file '$($file.FullName)': $($_.Exception.Message)" }
    }
)

$dependencies = @()
foreach ($node in $requiredNodes) {
    $name = $node.GetAttribute('Name')
    $publisher = $node.GetAttribute('Publisher')
    $minimumVersionText = $node.GetAttribute('MinVersion')
    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($publisher)) {
        throw 'The application manifest contains a package dependency without Name or Publisher.'
    }
    if ([string]::IsNullOrWhiteSpace($minimumVersionText)) { $minimumVersionText = '0.0.0.0' }
    $minimumVersion = [version]$minimumVersionText
    $requiredArchitecture = $node.GetAttribute('ProcessorArchitecture')
    if ([string]::IsNullOrWhiteSpace($requiredArchitecture)) {
        $requiredArchitecture = $mainInfo.Architecture
    }

    $installed = @(Get-AppxPackage -Name $name -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Publisher -eq $publisher -and
            (Test-CompatibleArchitecture -Actual ([string]$_.Architecture) -Required $requiredArchitecture)
        } |
        Sort-Object { [version]$_.Version } -Descending)

    $satisfied = $installed | Where-Object { [version]$_.Version -ge $minimumVersion } | Select-Object -First 1
    if ($satisfied) {
        Write-Host "Dependency already installed: $name $($satisfied.Version)"
        continue
    }

    $candidate = $candidates |
        Where-Object {
            $_.Name -eq $name -and
            $_.Publisher -eq $publisher -and
            $_.Version -ge $minimumVersion -and
            (Test-CompatibleArchitecture -Actual $_.Architecture -Required $requiredArchitecture)
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1

    if (!$candidate) {
        throw "Required dependency '$name' ($requiredArchitecture, version >= $minimumVersion) is not installed and no matching package was found under '$PSScriptRoot'."
    }

    $dependencies += $candidate.Path
}

if ($dependencies.Count -gt 0) {
    Add-AppxPackage -Path $package -DependencyPath @($dependencies | Select-Object -Unique)
}
else {
    Add-AppxPackage -Path $package
}

Write-Host 'Pixel Overlay is installed.'
Write-Host 'Open Xbox Game Bar with Win+G, open the Widgets menu, and select Pixel Overlay.'
