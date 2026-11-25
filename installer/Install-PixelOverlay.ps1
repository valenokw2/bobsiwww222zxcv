[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Detect', 'Install')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string]$TargetVersion,

    [Parameter(Mandatory = $true)]
    [string]$AppInstallerUri
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$logDirectory = Join-Path $env:LOCALAPPDATA 'PixelOverlay\Installer'
$null = New-Item -ItemType Directory -Force -Path $logDirectory
$logPath = Join-Path $logDirectory 'Setup.log'

function Send-SetupProgress {
    param([int]$Percent, [string]$Message)
    $safe = $Message -replace '[\r\n|]', ' '
    Write-Output "PIXEL_SETUP_PROGRESS|$Percent|$safe"
}

function Get-InstalledPixelOverlay {
    return Get-AppxPackage -Name 'PixelOverlay' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1
}

function Write-SetupLog {
    param([string]$Message)
    try {
        $line = "[{0}] {1}{2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message, [Environment]::NewLine
        [System.IO.File]::AppendAllText($logPath, $line, [System.Text.Encoding]::UTF8)
    }
    catch {}
}

try {
    Write-SetupLog "Started action '$Action'. Target version: $TargetVersion."

    if ($Action -eq 'Detect') {
        $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $build = [int]$windows.CurrentBuildNumber
        if (-not [Environment]::Is64BitOperatingSystem) {
            throw 'Pixel Overlay requires 64-bit Windows.'
        }
        if ($build -lt 19041) {
            throw "Windows build $build is not supported. Windows 10 build 19041 or later is required."
        }

        $installed = Get-InstalledPixelOverlay
        $installedVersion = if ($installed) { [string]$installed.Version } else { '' }
        $target = [version]$TargetVersion

        if (!$installed) {
            $mode = 'Install'
        }
        elseif ([version]$installedVersion -lt $target) {
            $mode = 'Update'
        }
        elseif ([version]$installedVersion -eq $target) {
            $mode = 'Current'
        }
        else {
            $mode = 'Newer'
        }

        Write-Output "PIXEL_SETUP_STATE|$mode|$installedVersion|$TargetVersion|$build"
        Write-SetupLog "Detection complete. Mode=$mode; installed=$installedVersion; target=$TargetVersion; build=$build."
        exit 0
    }

    Send-SetupProgress 5 'Checking the embedded package files...'
    $mainPackage = Join-Path $PayloadDirectory 'PixelOverlay.msix'
    $certificatePath = Join-Path $PayloadDirectory 'PixelOverlay.cer'
    if (!(Test-Path -LiteralPath $mainPackage)) { throw 'The embedded Pixel Overlay MSIX package is missing.' }
    if (!(Test-Path -LiteralPath $certificatePath)) { throw 'The embedded publisher certificate is missing.' }

    $dependencyPaths = @(
        (Join-Path $PayloadDirectory 'Microsoft.NET.CoreFramework.Debug.2.2.appx'),
        (Join-Path $PayloadDirectory 'Microsoft.NET.CoreRuntime.2.2.appx'),
        (Join-Path $PayloadDirectory 'Microsoft.VCLibs.x64.14.00.appx')
    )
    foreach ($dependency in $dependencyPaths) {
        if (!(Test-Path -LiteralPath $dependency)) {
            throw "A required framework dependency is missing: $(Split-Path -Leaf $dependency)"
        }
    }

    Send-SetupProgress 12 'Verifying the publisher certificate...'
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $certificatePath).Path
    )
    if ($certificate.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected package publisher certificate: $($certificate.Subject)"
    }
    if ($certificate.Thumbprint -ne 'A68A682C3193352F350F0FC6D7A5F8990863E150') {
        throw 'The embedded publisher certificate thumbprint does not match the expected Pixel Overlay signing certificate.'
    }

    $trusted = Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' |
        Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
        Select-Object -First 1
    if (!$trusted) {
        throw 'The publisher certificate is not trusted by Windows. Close setup and approve the certificate trust prompt, then retry.'
    }

    $target = [version]$TargetVersion
    $installed = Get-InstalledPixelOverlay
    if ($installed -and [version]$installed.Version -ge $target) {
        $message = "Pixel Overlay $($installed.Version) is already installed. No downgrade or reinstall was performed."
        Write-SetupLog $message
        Send-SetupProgress 100 $message
        Write-Output "PIXEL_SETUP_SUCCESS|$message"
        exit 0
    }

    Send-SetupProgress 25 'Installing the signed application and bundled framework dependencies...'
    Write-SetupLog 'Calling Add-AppxPackage with embedded MSIX and dependency packages.'
    Add-AppxPackage -Path $mainPackage -DependencyPath $dependencyPaths -ErrorAction Stop

    Send-SetupProgress 78 'Verifying the installed application...'
    $installed = Get-InstalledPixelOverlay
    if (!$installed) {
        throw 'Windows did not register Pixel Overlay after package deployment.'
    }
    if ([version]$installed.Version -lt $target) {
        throw "Windows registered version $($installed.Version), but setup expected $target."
    }

    $updateWarning = ''
    Send-SetupProgress 88 'Connecting the automatic update channel...'
    $manifestPath = Join-Path $env:TEMP ("PixelOverlay-Update-" + [guid]::NewGuid().ToString('N') + '.appinstaller')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $AppInstallerUri -OutFile $manifestPath -UseBasicParsing -MaximumRedirection 10 -TimeoutSec 30 -Headers @{ 'User-Agent' = 'PixelOverlay-Setup' }

        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        if (!$manifest.DocumentElement -or $manifest.DocumentElement.LocalName -ne 'AppInstaller') {
            throw 'The downloaded update manifest is invalid.'
        }
        if ($manifest.DocumentElement.GetAttribute('Uri') -ne $AppInstallerUri) {
            throw 'The update manifest does not match the official Pixel Overlay update channel.'
        }
        $remoteMain = $manifest.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")
        if (!$remoteMain -or $remoteMain.GetAttribute('Name') -ne 'PixelOverlay' -or
            $remoteMain.GetAttribute('Publisher') -ne 'CN=PixelOverlayDeveloper') {
            throw 'The update manifest package identity is not Pixel Overlay.'
        }

        # Register the remote App Installer source after the offline payload has
        # installed the same package locally. Windows sees the current version
        # and only records the update source unless a newer release is available.
        Add-AppxPackage -Path $manifestPath -AppInstallerFile -ErrorAction Stop
        Write-SetupLog 'The stable App Installer update source was registered successfully.'
    }
    catch {
        $updateWarning = 'The app was installed, but automatic update registration could not be completed. Open the official App Installer link once while online to enable updates. Details: ' + $_.Exception.Message
        Write-SetupLog ("Update registration warning: " + $_.Exception.Message)
    }
    finally {
        if (Test-Path -LiteralPath $manifestPath) {
            Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
        }
    }

    $gameBar = Get-AppxPackage -Name 'Microsoft.XboxGamingOverlay' -ErrorAction SilentlyContinue
    if (!$gameBar) {
        Write-SetupLog 'Xbox Game Bar was not found for the current user.'
        $updateWarning = ($updateWarning + ' ' + 'Xbox Game Bar was not detected for this Windows user. Install or enable it before launching Pixel Overlay.').Trim()
    }

    Send-SetupProgress 100 'Pixel Overlay is ready.'
    Write-SetupLog "Installation completed. Version=$($installed.Version)."
    Write-Output "PIXEL_SETUP_SUCCESS|Pixel Overlay $($installed.Version) is ready."
    if ($updateWarning) {
        Write-Output "PIXEL_SETUP_WARNING|$updateWarning"
    }
}
catch {
    $message = $_.Exception.Message
    Write-SetupLog ("FAILED: " + $message)
    Write-Output "PIXEL_SETUP_FAILURE|$message"
    exit 1
}
