[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Detect', 'Install')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string]$TargetVersion,

    [string]$AppInstallerUri = '',

    [switch]$ForceApplicationShutdown
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Utf8Encoding = New-Object System.Text.UTF8Encoding($false)
try { [Console]::OutputEncoding = $Utf8Encoding } catch {}
$OutputEncoding = $Utf8Encoding
$ExpectedCertificateSha256 = '65EBD21ACD334C64F84A61048CD987B958449EF193BF8A1EE5C2A8604F311D4F'
$logDirectory = Join-Path $env:LOCALAPPDATA 'PixelOverlay\Installer'
$null = New-Item -ItemType Directory -Force -Path $logDirectory
$logPath = Join-Path $logDirectory 'Setup.log'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function ConvertFrom-SafeXmlText {
    param([Parameter(Mandatory = $true)][string]$XmlText)

    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 2097152
    $document = New-Object System.Xml.XmlDocument
    $document.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create([System.IO.StringReader]::new($XmlText), $settings)
    try { $document.Load($reader) }
    finally { $reader.Dispose() }
    return ,$document
}

function Send-SetupProgress {
    param([int]$Percent, [string]$Message)
    $safe = $Message -replace '[\r\n|]', ' '
    Write-Output "PIXEL_SETUP_PROGRESS|$Percent|$safe"
}

function Write-SetupLog {
    param([string]$Message)
    try {
        $line = "[{0}] {1}{2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message, [Environment]::NewLine
        [System.IO.File]::AppendAllText($logPath, $line, [System.Text.Encoding]::UTF8)
    }
    catch {}
}

function Get-InstalledPixelOverlay {
    return Get-AppxPackage -Name 'PixelOverlay' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1
}

function Get-AppxManifestXml {
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
                $manifestText = $reader.ReadToEnd()
                $manifest = ConvertFrom-SafeXmlText -XmlText $manifestText
                return ,$manifest
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Normalize-AppxArchitecture {
    param([string]$Architecture)

    if ($null -eq $Architecture) { $Architecture = '' }
    switch ($Architecture.ToString().Trim().ToLowerInvariant()) {
        'amd64' { return 'x64' }
        'x64' { return 'x64' }
        'x86' { return 'x86' }
        'arm' { return 'arm' }
        'arm64' { return 'arm64' }
        'neutral' { return 'neutral' }
        default { return '' }
    }
}

function Test-AppxArchitectureCompatible {
    param(
        [string]$ActualArchitecture,
        [string]$RequiredArchitecture
    )

    $actual = Normalize-AppxArchitecture $ActualArchitecture
    $required = Normalize-AppxArchitecture $RequiredArchitecture
    if (!$required) { $required = 'x64' }
    if ($required -eq 'neutral') { return $actual -eq 'neutral' }
    return ($actual -eq $required -or $actual -eq 'neutral')
}

function Get-AppxFileIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifest = Get-AppxManifestXml -Path $Path
    $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if (!$identity) {
        throw "Package '$Path' does not contain Package/Identity."
    }

    return [pscustomobject]@{
        Path = (Resolve-Path -LiteralPath $Path).Path
        Name = $identity.GetAttribute('Name')
        Publisher = $identity.GetAttribute('Publisher')
        Version = [version]$identity.GetAttribute('Version')
        Architecture = Normalize-AppxArchitecture $identity.GetAttribute('ProcessorArchitecture')
    }
}

function Get-RequiredPackageDependencies {
    param([Parameter(Mandatory = $true)][string]$MainPackagePath)

    $mainIdentity = Get-AppxFileIdentity -Path $MainPackagePath
    $manifest = Get-AppxManifestXml -Path $MainPackagePath
    $nodes = $manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']")
    $required = @()

    foreach ($node in $nodes) {
        $name = $node.GetAttribute('Name')
        $publisher = $node.GetAttribute('Publisher')
        if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($publisher)) {
            throw 'The application manifest contains a package dependency without Name or Publisher.'
        }

        $minimumVersionText = $node.GetAttribute('MinVersion')
        if ([string]::IsNullOrWhiteSpace($minimumVersionText)) { $minimumVersionText = '0.0.0.0' }
        $architecture = Normalize-AppxArchitecture $node.GetAttribute('ProcessorArchitecture')
        if (!$architecture) { $architecture = $mainIdentity.Architecture }

        $required += [pscustomobject]@{
            Name = $name
            Publisher = $publisher
            MinVersion = [version]$minimumVersionText
            Architecture = $architecture
        }
    }

    return $required
}

function Get-DependenciesToInstall {
    param(
        [Parameter(Mandatory = $true)][string]$MainPackagePath,
        [Parameter(Mandatory = $true)][string]$PayloadDirectory
    )

    $requiredDependencies = @(Get-RequiredPackageDependencies -MainPackagePath $MainPackagePath)
    if ($requiredDependencies.Count -eq 0) {
        Write-SetupLog 'The main package manifest declares no framework package dependencies.'
        return @()
    }

    $candidateFiles = @(Get-ChildItem -LiteralPath $PayloadDirectory -File |
        Where-Object { $_.Extension -in @('.appx', '.msix') -and $_.Name -ne 'PixelOverlay.msix' })
    $candidates = @(
        foreach ($candidateFile in $candidateFiles) {
            try { Get-AppxFileIdentity -Path $candidateFile.FullName }
            catch { Write-SetupLog ("Ignoring invalid embedded dependency '{0}': {1}" -f $candidateFile.Name, $_.Exception.Message) }
        }
    )

    $needed = @()
    foreach ($dependency in $requiredDependencies) {
        $installed = @(Get-AppxPackage -Name $dependency.Name -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Publisher -eq $dependency.Publisher -and
                (Test-AppxArchitectureCompatible -ActualArchitecture ([string]$_.Architecture) -RequiredArchitecture $dependency.Architecture)
            } |
            Sort-Object { [version]$_.Version } -Descending)

        $satisfied = $installed | Where-Object { [version]$_.Version -ge $dependency.MinVersion } | Select-Object -First 1
        if ($satisfied) {
            Write-SetupLog ("Dependency already satisfied: {0} {1} ({2})" -f
                $dependency.Name, $satisfied.Version, $dependency.Architecture)
            continue
        }

        $candidate = $candidates |
            Where-Object {
                $_.Name -eq $dependency.Name -and
                $_.Publisher -eq $dependency.Publisher -and
                (Test-AppxArchitectureCompatible -ActualArchitecture $_.Architecture -RequiredArchitecture $dependency.Architecture) -and
                $_.Version -ge $dependency.MinVersion
            } |
            Sort-Object Version -Descending |
            Select-Object -First 1

        if (!$candidate) {
            throw ("Required dependency '{0}' ({1}, {2}, version >= {3}) is not installed and no matching embedded package was found." -f
                $dependency.Name, $dependency.Publisher, $dependency.Architecture, $dependency.MinVersion)
        }

        Write-SetupLog ("Dependency will be deployed: {0} {1} ({2})" -f
            $candidate.Name, $candidate.Version, $candidate.Architecture)
        $needed += $candidate.Path
    }

    return @($needed | Select-Object -Unique)
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

        if (!$installed) { $mode = 'Install' }
        elseif ([version]$installedVersion -lt $target) { $mode = 'Update' }
        elseif ([version]$installedVersion -eq $target) { $mode = 'Current' }
        else { $mode = 'Newer' }

        Write-Output "PIXEL_SETUP_STATE|$mode|$installedVersion|$TargetVersion|$build"
        Write-SetupLog "Detection complete. Mode=$mode; installed=$installedVersion; target=$TargetVersion; build=$build."
        exit 0
    }

    Send-SetupProgress 5 'Checking the embedded package files...'
    $mainPackage = Join-Path $PayloadDirectory 'PixelOverlay.msix'
    $certificatePath = Join-Path $PayloadDirectory 'PixelOverlay.cer'
    if (!(Test-Path -LiteralPath $mainPackage)) { throw 'The embedded Pixel Overlay MSIX package is missing.' }
    if (!(Test-Path -LiteralPath $certificatePath)) { throw 'The embedded publisher certificate is missing.' }

    Send-SetupProgress 12 'Verifying the publisher certificate...'
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $certificatePath).Path
    )
    if ($certificate.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected package publisher certificate: $($certificate.Subject)"
    }
    $actualCertificateSha256 = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash
    if ($actualCertificateSha256 -ne $ExpectedCertificateSha256) {
        throw 'The embedded publisher certificate SHA-256 does not match the pinned Pixel Overlay signing certificate.'
    }

    $trusted = Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' |
        Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
        Select-Object -First 1
    if (!$trusted) {
        throw 'The publisher certificate is not trusted by Windows. Close setup and approve the certificate trust prompt, then retry.'
    }

    $target = [version]$TargetVersion
    $mainIdentity = Get-AppxFileIdentity -Path $mainPackage
    if ($mainIdentity.Name -ne 'PixelOverlay' -or
        $mainIdentity.Publisher -ne 'CN=PixelOverlayDeveloper' -or
        $mainIdentity.Architecture -ne 'x64' -or
        $mainIdentity.Version -ne $target) {
        throw ("Unexpected embedded MSIX identity/version. Expected PixelOverlay / CN=PixelOverlayDeveloper / x64 / {0}; received {1} / {2} / {3} / {4}." -f
            $target, $mainIdentity.Name, $mainIdentity.Publisher, $mainIdentity.Architecture, $mainIdentity.Version)
    }

    $installed = Get-InstalledPixelOverlay
    $packageAlreadyPresent = $installed -and ([version]$installed.Version -ge $target)

    if ($packageAlreadyPresent) {
        Write-SetupLog ("Package deployment skipped; installed version {0} is already >= target {1}." -f $installed.Version, $target)
        Send-SetupProgress 30 'The application package is already installed; checking its update channel...'
    }
    else {
        $dependenciesToInstall = @(Get-DependenciesToInstall -MainPackagePath $mainPackage -PayloadDirectory $PayloadDirectory)
        Write-SetupLog ("Framework dependencies to deploy: {0}." -f $dependenciesToInstall.Count)

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
            Write-SetupLog 'Starting package deployment without forced application shutdown.'
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
    if ([string]::IsNullOrWhiteSpace($AppInstallerUri)) {
        Write-SetupLog 'Automatic update channel registration was skipped for this prerelease build.'
    }
    else {
        Send-SetupProgress 88 'Connecting the automatic update channel...'
        $manifestPath = Join-Path $PayloadDirectory ("PixelOverlay-Update-" + [guid]::NewGuid().ToString('N') + '.appinstaller')
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $AppInstallerUri -OutFile $manifestPath -UseBasicParsing -MaximumRedirection 10 -TimeoutSec 30 -Headers @{ 'User-Agent' = 'PixelOverlay-Setup' }

            $manifest = ConvertFrom-SafeXmlText -XmlText (Get-Content -LiteralPath $manifestPath -Raw)
            if (!$manifest.DocumentElement -or $manifest.DocumentElement.LocalName -ne 'AppInstaller') {
                throw 'The downloaded update manifest is invalid.'
            }
            if ($manifest.DocumentElement.GetAttribute('Uri') -ne $AppInstallerUri) {
                throw 'The update manifest does not match the configured Pixel Overlay update channel.'
            }
            $remoteMain = $manifest.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")
            if (!$remoteMain -or $remoteMain.GetAttribute('Name') -ne 'PixelOverlay' -or
                $remoteMain.GetAttribute('Publisher') -ne 'CN=PixelOverlayDeveloper' -or
                $remoteMain.GetAttribute('ProcessorArchitecture') -ne 'x64') {
                throw 'The update manifest package identity or architecture is not Pixel Overlay x64.'
            }
            $remoteVersion = [version]$remoteMain.GetAttribute('Version')
            $manifestVersion = [version]$manifest.DocumentElement.GetAttribute('Version')
            if ($remoteVersion -lt $target -or $manifestVersion -ne $remoteVersion) {
                throw 'The update manifest version is older than the installed target or internally inconsistent.'
            }
            $remotePackageUri = [Uri]$remoteMain.GetAttribute('Uri')
            $expectedPackagePath = "/valenokw2/bobsiwww222zxcv/releases/download/v$remoteVersion/PixelOverlay-$remoteVersion-x64.msix"
            if (!$remotePackageUri.IsAbsoluteUri -or $remotePackageUri.Scheme -ne 'https' -or
                $remotePackageUri.Host -ne 'github.com' -or $remotePackageUri.AbsolutePath -ne $expectedPackagePath) {
                throw 'The update manifest package URL is outside the official versioned Pixel Overlay release path.'
            }

            # A stable installer registers only the stable feed. Beta installers are built
            # with an empty URI and can never redirect installed users to a prerelease.
            Add-AppxPackage -Path $manifestPath -AppInstallerFile -ErrorAction Stop
            Write-SetupLog 'The configured App Installer update source was registered successfully.'
        }
        catch {
            $updateWarning = 'The app was installed, but automatic update registration could not be completed. Details: ' + $_.Exception.Message
            Write-SetupLog ("Update registration warning: " + $_.Exception.Message)
        }
        finally {
            if (Test-Path -LiteralPath $manifestPath) {
                Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
            }
        }
    }

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
