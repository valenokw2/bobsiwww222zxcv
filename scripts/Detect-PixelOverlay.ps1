[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$appInstallerUri = 'https://github.com/valenokw2/bobsiwww222zxcv/releases/download/update-channel/PixelOverlay.appinstaller'
$tempManifestPath = Join-Path $env:TEMP ("PixelOverlay-Preflight-" + [guid]::NewGuid().ToString('N') + '.appinstaller')

try {
    Write-Host 'Checking the Pixel Overlay update channel...'
    Invoke-WebRequest -Uri $appInstallerUri -OutFile $tempManifestPath -UseBasicParsing -MaximumRedirection 5

    [xml]$manifest = Get-Content -LiteralPath $tempManifestPath -Raw
    if ($manifest.DocumentElement.LocalName -ne 'AppInstaller') {
        throw 'The update channel did not return a valid App Installer manifest.'
    }
    if ($manifest.DocumentElement.GetAttribute('Uri') -ne $appInstallerUri) {
        throw 'The update manifest URI does not match the official Pixel Overlay update channel.'
    }

    $mainPackage = $manifest.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")
    if (!$mainPackage -or
        $mainPackage.GetAttribute('Name') -ne 'PixelOverlay' -or
        $mainPackage.GetAttribute('Publisher') -ne 'CN=PixelOverlayDeveloper') {
        throw 'The update manifest package identity is not the expected Pixel Overlay package.'
    }

    $availableVersion = [version]$mainPackage.GetAttribute('Version')
    $installedPackage = Get-AppxPackage -Name 'PixelOverlay' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1

    $installedVersion = ''
    $installedLocation = ''
    if ($installedPackage) {
        $installedVersion = [string]$installedPackage.Version
        $installedLocation = [string]$installedPackage.InstallLocation
        $currentVersion = [version]$installedVersion

        if ($currentVersion -lt $availableVersion) {
            $mode = 'Update'
        }
        elseif ($currentVersion -eq $availableVersion) {
            $mode = 'Current'
        }
        else {
            $mode = 'Newer'
        }
    }
    else {
        $mode = 'Install'
    }

    $volumes = @(Get-AppxVolume | Where-Object { !$_.IsOffline })
    if ($volumes.Count -eq 0) {
        throw 'Windows has no online MSIX package volumes available.'
    }

    $defaultVolume = Get-AppxDefaultVolume
    $defaultVolumePath = if ($defaultVolume) { [string]$defaultVolume.PackageStorePath } else { '' }
    if ([string]::IsNullOrWhiteSpace($defaultVolumePath)) {
        $systemVolume = $volumes | Where-Object { $_.IsSystemVolume } | Select-Object -First 1
        if ($systemVolume) {
            $defaultVolumePath = [string]$systemVolume.PackageStorePath
        }
    }

    $installedVolumePath = ''
    if ($installedPackage) {
        foreach ($volume in $volumes) {
            $packageStorePath = [string]$volume.PackageStorePath
            if ($packageStorePath -and
                $installedLocation.StartsWith($packageStorePath, [StringComparison]::OrdinalIgnoreCase)) {
                $installedVolumePath = $packageStorePath
                break
            }
        }
    }

    $gameBarRunning = [bool](@(Get-Process -Name 'GameBar', 'GameBarFTServer', 'XboxGameBar' -ErrorAction SilentlyContinue).Count -gt 0)

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add("Mode=$mode")
    $lines.Add("InstalledVersion=$installedVersion")
    $lines.Add("AvailableVersion=$availableVersion")
    $lines.Add("InstalledLocation=$installedLocation")
    $lines.Add("InstalledVolumePath=$installedVolumePath")
    $lines.Add("DefaultVolumePath=$defaultVolumePath")
    $lines.Add("GameBarRunning=$([int]$gameBarRunning)")

    foreach ($volume in $volumes) {
        $name = [string]$volume.Name
        $path = [string]$volume.PackageStorePath
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }
        if ($name.Contains('|') -or $path.Contains('|')) {
            continue
        }

        $isDefault = $path.Equals($defaultVolumePath, [StringComparison]::OrdinalIgnoreCase)
        $lines.Add("Volume=$name|$path|$([int]$isDefault)")
    }

    if ($mode -eq 'Install' -and !$lines.Exists({ param($line) $line.StartsWith('Volume=') })) {
        throw 'No selectable online MSIX package volume was found.'
    }

    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory -and !(Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
    [System.IO.File]::WriteAllLines($OutputPath, $lines, [System.Text.Encoding]::ASCII)

    Write-Host "Detected mode: $mode"
    Write-Host "Installed version: $(if ($installedVersion) { $installedVersion } else { 'not installed' })"
    Write-Host "Available version: $availableVersion"
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if (Test-Path -LiteralPath $tempManifestPath) {
        Remove-Item -LiteralPath $tempManifestPath -Force -ErrorAction SilentlyContinue
    }
}
