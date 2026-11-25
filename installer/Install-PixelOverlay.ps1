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
    [string]$AppInstallerUri,

    [switch]$ForceApplicationShutdown
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Utf8Encoding = New-Object System.Text.UTF8Encoding($false)
try { [Console]::OutputEncoding = $Utf8Encoding } catch {}
$OutputEncoding = $Utf8Encoding
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

function Get-AppxFileIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if (!$entry) {
            throw "Package '$Path' does not contain AppxManifest.xml."
        }

        $stream = $entry.Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            try {
                [xml]$manifest = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }

        $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
        if (!$identity) {
            throw "Package '$Path' does not contain Package/Identity."
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = [version]$identity.GetAttribute('Version')
            Architecture = $identity.GetAttribute('ProcessorArchitecture')
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-DependenciesToInstall {
    param([Parameter(Mandatory = $true)][string[]]$DependencyPaths)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $needed = @()

    foreach ($path in $DependencyPaths) {
        $identity = Get-AppxFileIdentity -Path $path
        $installed = Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue |
            Where-Object { $_.Publisher -eq $identity.Publisher } |
            Sort-Object { [version]$_.Version } -Descending |
            Select-Object -First 1

        if ($installed -and ([version]$installed.Version -ge $identity.Version)) {
            Write-SetupLog ("Skipping already-installed dependency: {0} {1}" -f $identity.Name, $installed.Version)
        }
        else {
            Write-SetupLog ("Dependency will be installed: {0} {1}" -f $identity.Name, $identity.Version)
            $needed += $path
        }
    }

    return $needed
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
    $dependenciesToInstall = @(Get-DependenciesToInstall -DependencyPaths $dependencyPaths)
    Write-SetupLog ("Dependencies supplied by setup: {0}; already satisfied: {1}" -f
        $dependenciesToInstall.Count, ($dependencyPaths.Count - $dependenciesToInstall.Count))

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

    Send-SetupProgress 25 'Installing the signed application and required framework dependencies...'
    $deploymentArguments = @{
        Path = $mainPackage
        ErrorAction = 'Stop'
    }
    if ($dependenciesToInstall.Count -gt 0) {
        $deploymentArguments.DependencyPath = $dependenciesToInstall
    }
    if ($ForceApplicationShutdown) {
        Write-SetupLog 'Retrying deployment with -ForceApplicationShutdown after explicit user approval.'
        $deploymentArguments.ForceApplicationShutdown = $true
    }
    else {
        Write-SetupLog 'Installing the app without re-deploying dependencies already present for this user.'
    }

    try {
        Add-AppxPackage @deploymentArguments
    }
    catch {
        $deploymentMessage = [string]$_.Exception.Message
        if (!$ForceApplicationShutdown -and $deploymentMessage -match '(?i)0x80073D02') {
            Write-SetupLog 'Windows reported ERROR_PACKAGES_IN_USE (0x80073D02). Waiting for user confirmation before force-closing dependent apps.'
            $safeDeploymentMessage = $deploymentMessage -replace '[\r\n|]+', ' '
            Write-Output "PIXEL_SETUP_PACKAGES_IN_USE|$safeDeploymentMessage"
            exit 23
        }
        throw
    }

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

    # App Installer may have applied a newer feed version during update registration.
    $installed = Get-InstalledPixelOverlay
    if (!$installed) {
        throw 'Pixel Overlay could not be found after update-channel registration.'
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
    $safeMessage = $message -replace '[\r\n|]+', ' '
    Write-Output "PIXEL_SETUP_FAILURE|$safeMessage"
    exit 1
}
