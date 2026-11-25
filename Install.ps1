[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$CertificatePath = (Join-Path $PSScriptRoot 'PixelOverlay.cer'),

    [string[]]$DependencyPaths = @()
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-AppxManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (!$entry) { throw "Package '$Path' does not contain AppxManifest.xml." }

        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            try {
                [xml]$manifest = $reader.ReadToEnd()
                return $manifest
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Get-AppxIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifest = Get-AppxManifest -Path $Path
    $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if (!$identity) { throw "Package '$Path' does not contain Package/Identity." }

    return [pscustomobject]@{
        Path = (Resolve-Path -LiteralPath $Path).Path
        Name = $identity.GetAttribute('Name')
        Publisher = $identity.GetAttribute('Publisher')
        Version = [version]$identity.GetAttribute('Version')
        Architecture = $identity.GetAttribute('ProcessorArchitecture')
    }
}

function Get-InstalledDependency {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Publisher,
        [Parameter(Mandatory = $true)][string]$Architecture
    )

    return Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Publisher -eq $Publisher -and
            ([string]$_.Architecture -ieq $Architecture)
        } |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([System.IO.Path]::GetExtension($package).ToLowerInvariant() -notin @('.msix', '.appx')) {
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
Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null

$mainManifest = Get-AppxManifest -Path $package
$dependencyNodes = @($mainManifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"))
$resolvedDependencies = @()

if ($dependencyNodes.Count -gt 0) {
    $candidatePaths = @()

    if ($DependencyPaths.Count -gt 0) {
        $candidatePaths += $DependencyPaths
    }
    else {
        # GitHub release downloads place the MSIX and APPX files side-by-side.
        $packageDirectory = Split-Path -Parent $package
        $candidateRoots = @($packageDirectory, $PSScriptRoot) | Select-Object -Unique

        foreach ($root in $candidateRoots) {
            if (Test-Path -LiteralPath $root) {
                $candidatePaths += Get-ChildItem -LiteralPath $root -File |
                    Where-Object { $_.Extension -in '.appx', '.msix' } |
                    Select-Object -ExpandProperty FullName
            }
        }

        # Visual Studio stores generated framework packages under *_Test\Dependencies\x64.
        # Search only below the selected package directory, then match exact manifest identities.
        if (Test-Path -LiteralPath $packageDirectory) {
            $dependencyDirectories = @(Get-ChildItem -LiteralPath $packageDirectory -Directory -Recurse |
                Where-Object { $_.Name -eq 'Dependencies' })
            foreach ($directory in $dependencyDirectories) {
                $x64Directory = Join-Path $directory.FullName 'x64'
                if (Test-Path -LiteralPath $x64Directory) {
                    $candidatePaths += Get-ChildItem -LiteralPath $x64Directory -File |
                        Where-Object { $_.Extension -in '.appx', '.msix' } |
                        Select-Object -ExpandProperty FullName
                }
            }
        }
    }

    $candidatePaths = @($candidatePaths | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)
    $candidateIdentities = @(
        foreach ($candidatePath in $candidatePaths) {
            try { Get-AppxIdentity -Path $candidatePath }
            catch { Write-Warning "Ignoring invalid dependency candidate '$candidatePath': $($_.Exception.Message)" }
        }
    )

    foreach ($dependency in $dependencyNodes) {
        $name = $dependency.GetAttribute('Name')
        $publisher = $dependency.GetAttribute('Publisher')
        $minimumVersionText = $dependency.GetAttribute('MinVersion')
        $minimumVersion = if ([string]::IsNullOrWhiteSpace($minimumVersionText)) { [version]'0.0.0.0' } else { [version]$minimumVersionText }

        $matchingCandidates = @($candidateIdentities | Where-Object {
            $_.Name -eq $name -and
            $_.Publisher -eq $publisher -and
            $_.Architecture -in @('x64', 'neutral') -and
            $_.Version -ge $minimumVersion
        } | Sort-Object -Property @{ Expression = { $_.Version }; Descending = $true }, @{ Expression = { if ($_.Architecture -ieq 'x64') { 1 } else { 0 } }; Descending = $true })

        $selectedCandidate = $matchingCandidates | Select-Object -First 1
        if ($selectedCandidate) {
            $installed = Get-InstalledDependency -Name $name -Publisher $publisher -Architecture $selectedCandidate.Architecture
            if (!$installed -and $selectedCandidate.Architecture -ieq 'x64') {
                $installed = Get-InstalledDependency -Name $name -Publisher $publisher -Architecture 'neutral'
            }
            if (!$installed -or ([version]$installed.Version -lt $selectedCandidate.Version)) {
                $resolvedDependencies += $selectedCandidate.Path
            }
            else {
                Write-Host "Dependency already satisfied: $name $($installed.Version) [$($installed.Architecture)]"
            }
            continue
        }

        $installedX64 = Get-InstalledDependency -Name $name -Publisher $publisher -Architecture 'x64'
        $installedNeutral = Get-InstalledDependency -Name $name -Publisher $publisher -Architecture 'neutral'
        $installed = @($installedX64, $installedNeutral) |
            Where-Object { $_ -and ([version]$_.Version -ge $minimumVersion) } |
            Sort-Object { [version]$_.Version } -Descending |
            Select-Object -First 1

        if ($installed) {
            Write-Host "Dependency already satisfied: $name $($installed.Version) [$($installed.Architecture)]"
        }
        else {
            throw "Required dependency '$name' ($publisher), minimum version $minimumVersion, was not found. Put its x64/neutral APPX/MSIX beside the main package or pass -DependencyPaths."
        }
    }
}

if ($resolvedDependencies.Count -gt 0) {
    Add-AppxPackage -Path $package -DependencyPath $resolvedDependencies
}
else {
    Add-AppxPackage -Path $package
}

Write-Host 'Pixel Overlay is installed.'
Write-Host 'Open Xbox Game Bar with Win+G, open the Widgets menu, and select Pixel Overlay.'
