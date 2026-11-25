[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$ErrorPath
)

$ErrorActionPreference = 'Stop'
$appInstallerUri = 'https://github.com/valenokw2/bobsiwww222zxcv/releases/download/update-channel/PixelOverlay.appinstaller'
$tempManifestPath = Join-Path $env:TEMP ("PixelOverlay-Preflight-" + [guid]::NewGuid().ToString('N') + '.appinstaller')
$stage = 'Initialize'

function Write-PreflightLog {
    param([string]$Message)
    try {
        $logDirectory = Join-Path $env:LOCALAPPDATA 'PixelOverlay'
        if (!(Test-Path -LiteralPath $logDirectory)) {
            New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        }
        $logPath = Join-Path $logDirectory 'Preflight.log'
        $entry = "[{0}] {1}: {2}{3}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $stage, $Message, [Environment]::NewLine
        [System.IO.File]::AppendAllText($logPath, $entry, [System.Text.Encoding]::UTF8)
    }
    catch {
        # Diagnostics must never prevent setup from continuing.
    }
}

try {
    Write-PreflightLog 'Starting preflight.'

    $stage = 'Enable secure HTTPS'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    catch {
        Write-PreflightLog ("Could not explicitly enable TLS 1.2: " + $_.Exception.Message)
    }

    $stage = 'Download the official update manifest'
    Write-Host 'Checking the Pixel Overlay update channel...'
    Invoke-WebRequest -Uri $appInstallerUri -OutFile $tempManifestPath -UseBasicParsing -MaximumRedirection 10 -TimeoutSec 30 -Headers @{ 'User-Agent' = 'PixelOverlay-Setup' }

    $stage = 'Validate update manifest XML'
    [xml]$manifest = Get-Content -LiteralPath $tempManifestPath -Raw
    if (!$manifest.DocumentElement -or $manifest.DocumentElement.LocalName -ne 'AppInstaller') {
        throw 'The update channel did not return a valid App Installer manifest.'
    }
    if ($manifest.DocumentElement.GetAttribute('Uri') -ne $appInstallerUri) {
        throw 'The update manifest URI does not match the official Pixel Overlay update channel.'
    }

    $stage = 'Validate Pixel Overlay package identity'
    $mainPackage = $manifest.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")
    if (!$mainPackage -or
        $mainPackage.GetAttribute('Name') -ne 'PixelOverlay' -or
        $mainPackage.GetAttribute('Publisher') -ne 'CN=PixelOverlayDeveloper') {
        throw 'The update manifest package identity is not the expected Pixel Overlay package.'
    }

    $availableVersion = [version]$mainPackage.GetAttribute('Version')
    if (!$availableVersion) {
        throw 'The update manifest does not contain a valid package version.'
    }

    $stage = 'Read installed Pixel Overlay version'
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

    $stage = 'Read Windows default package volume'
    $defaultVolumePath = 'Windows-managed default package volume (path unavailable)'
    try {
        $defaultVolume = Get-AppxDefaultVolume -ErrorAction Stop
        $reportedPath = [string]$defaultVolume.PackageStorePath
        if (![string]::IsNullOrWhiteSpace($reportedPath)) {
            $defaultVolumePath = $reportedPath
        }
        else {
            Write-PreflightLog 'Windows returned an empty PackageStorePath; continuing with a generic location label.'
        }
    }
    catch {
        Write-PreflightLog ("Could not read the Windows default package volume; continuing because this is informational only. " + $_.Exception.Message)
    }

    $stage = 'Check Xbox Game Bar status'
    $gameBarRunning = $false
    try {
        $gameBarRunning = [bool](@(Get-Process -Name 'GameBar', 'GameBarFTServer', 'XboxGameBar' -ErrorAction SilentlyContinue).Count -gt 0)
    }
    catch {
        Write-PreflightLog ("Could not inspect Game Bar processes: " + $_.Exception.Message)
    }

    $stage = 'Write preflight result'
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

    Write-PreflightLog ("Completed successfully. Mode=$mode; installed=$installedVersion; available=$availableVersion; volume=$defaultVolumePath")
    Write-Host "Detected mode: $mode"
    Write-Host "Installed version: $(if ($installedVersion) { $installedVersion } else { 'not installed' })"
    Write-Host "Available version: $availableVersion"
    Write-Host "Windows default package volume: $defaultVolumePath"
}
catch {
    $message = $_.Exception.Message
    $details = @(
        "Stage: $stage"
        "Error: $message"
        "Exception: $($_.Exception.GetType().FullName)"
        "Time: $([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))"
    ) -join [Environment]::NewLine

    Write-PreflightLog ("FAILED: " + $details)
    if (![string]::IsNullOrWhiteSpace($ErrorPath)) {
        try {
            $errorDirectory = Split-Path -Parent $ErrorPath
            if ($errorDirectory -and !(Test-Path -LiteralPath $errorDirectory)) {
                New-Item -ItemType Directory -Force -Path $errorDirectory | Out-Null
            }
            [System.IO.File]::WriteAllText($ErrorPath, $details, [System.Text.Encoding]::UTF8)
        }
        catch {
            Write-PreflightLog ("Could not write error details file: " + $_.Exception.Message)
        }
    }

    Write-Error $details
    exit 1
}
finally {
    if (Test-Path -LiteralPath $tempManifestPath) {
        Remove-Item -LiteralPath $tempManifestPath -Force -ErrorAction SilentlyContinue
    }
}
