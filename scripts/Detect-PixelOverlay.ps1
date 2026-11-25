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

    $defaultVolume = Get-AppxDefaultVolume
    $defaultVolumePath = [string]$defaultVolume.PackageStorePath
    if ([string]::IsNullOrWhiteSpace($defaultVolumePath)) {
        throw 'Windows did not report a default MSIX app package volume.'
    }

    $gameBarRunning = [bool](@(Get-Process -Name 'GameBar', 'GameBarFTServer', 'XboxGameBar' -ErrorAction SilentlyContinue).Count -gt 0)

    $lines = @(
        "Mode=$mode"
        "InstalledVersion=$installedVersion"
        "AvailableVersion=$availableVersion"
        "InstalledLocation=$installedLocation"
        "DefaultVolumePath=$defaultVolumePath"
        "GameBarRunning=$([int]$gameBarRunning)"
    )

    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory -and !(Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
    [System.IO.File]::WriteAllLines($OutputPath, $lines, [System.Text.Encoding]::ASCII)

    Write-Host "Detected mode: $mode"
    Write-Host "Installed version: $(if ($installedVersion) { $installedVersion } else { 'not installed' })"
    Write-Host "Available version: $availableVersion"
    Write-Host "Windows default package volume: $defaultVolumePath"
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
