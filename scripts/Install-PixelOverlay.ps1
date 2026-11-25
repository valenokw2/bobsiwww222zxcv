[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,
    [switch]$TrustCertificateOnly,
    [string]$VolumePath
)

$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path $env:LOCALAPPDATA 'PixelOverlay'
$null = New-Item -ItemType Directory -Force -Path $logDirectory
$logPath = Join-Path $logDirectory 'Installer.log'
$manifestPath = Join-Path $env:TEMP ("PixelOverlay-" + [guid]::NewGuid().ToString('N') + '.appinstaller')
$transcriptStarted = $false
$appInstallerUri = 'https://github.com/valenokw2/bobsiwww222zxcv/releases/download/update-channel/PixelOverlay.appinstaller'
$expectedPublisherThumbprint = 'A68A682C3193352F350F0FC6D7A5F8990863E150'

function Set-InstallerProgress {
    param(
        [Parameter(Mandatory = $true)][ValidateRange(0, 100)][int]$Percent,
        [Parameter(Mandatory = $true)][string]$Status
    )

    $safeStatus = $Status -replace '[\r\n|]', ' '
    Write-Host "INSTALL_PROGRESS|$Percent|$safeStatus"
}

try {
    Start-Transcript -Path $logPath -Append | Out-Null
    $transcriptStarted = $true

    Set-InstallerProgress -Percent 2 -Status 'Checking Windows compatibility...'

    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Pixel Overlay requires 64-bit Windows.'
    }

    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $buildNumber = [int]$windows.CurrentBuildNumber
    if ($buildNumber -lt 19041) {
        throw "Windows build $buildNumber is not supported. Windows 10 version 2004 (build 19041) or later is required."
    }

    if (!(Test-Path -LiteralPath $CertificatePath)) {
        throw "The included publisher certificate was not found: $CertificatePath"
    }

    Set-InstallerProgress -Percent 8 -Status 'Validating the publisher certificate...'

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $CertificatePath).Path
    )
    if ($certificate.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected certificate publisher: $($certificate.Subject)"
    }
    if ($certificate.Thumbprint -ne $expectedPublisherThumbprint) {
        throw "Unexpected Pixel Overlay signing certificate thumbprint: $($certificate.Thumbprint)"
    }
    if ($certificate.NotAfter -le (Get-Date)) {
        throw 'The included signing certificate has expired.'
    }

    $ekuExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
        Select-Object -First 1

    $hasCodeSigningEku = $false
    if ($ekuExtension) {
        $parsedEku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
            $ekuExtension,
            $ekuExtension.Critical
        )
        $hasCodeSigningEku = @($parsedEku.EnhancedKeyUsages | Where-Object {
            $_.Value -eq '1.3.6.1.5.5.7.3.3'
        }).Count -gt 0
    }

    if (!$hasCodeSigningEku) {
        throw 'The included certificate is not a Code Signing certificate (OID 1.3.6.1.5.5.7.3.3).'
    }

    $trustedCertificate = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
        Select-Object -First 1

    if ($TrustCertificateOnly) {
        Set-InstallerProgress -Percent 70 -Status 'Registering the trusted publisher certificate...'
        if (!$trustedCertificate) {
            Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        }
        Write-Host "Trusted publisher certificate installed for this Windows device: $($certificate.Thumbprint)"
        Set-InstallerProgress -Percent 100 -Status 'Publisher certificate is ready.'
        return
    }

    if (!$trustedCertificate) {
        throw 'The Pixel Overlay publisher certificate is not installed in Local Computer > Trusted People. Run the setup installer again and approve its administrator prompt.'
    }

    Set-InstallerProgress -Percent 15 -Status 'Downloading the latest release manifest...'
    Invoke-WebRequest -Uri $appInstallerUri -OutFile $manifestPath -UseBasicParsing -MaximumRedirection 5

    Set-InstallerProgress -Percent 28 -Status 'Verifying the release and package identity...'
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    if ($manifest.DocumentElement.LocalName -ne 'AppInstaller') {
        throw 'The downloaded update manifest is not a valid App Installer document.'
    }
    if ($manifest.DocumentElement.GetAttribute('Uri') -ne $appInstallerUri) {
        throw 'The downloaded manifest does not point to the expected stable update URI.'
    }

    $mainPackage = $manifest.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")
    if (!$mainPackage) {
        throw 'The update manifest does not contain a main package.'
    }
    if ($mainPackage.GetAttribute('Name') -ne 'PixelOverlay' -or
        $mainPackage.GetAttribute('Publisher') -ne $certificate.Subject) {
        throw 'The downloaded package identity does not match the trusted Pixel Overlay publisher certificate.'
    }

    $availableVersion = [version]$mainPackage.GetAttribute('Version')
    $installedPackage = Get-AppxPackage -Name 'PixelOverlay' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1

    if ($installedPackage) {
        $installedVersion = [version]$installedPackage.Version
        if ($installedVersion -ge $availableVersion) {
            if ($installedVersion -eq $availableVersion) {
                $message = "Pixel Overlay $installedVersion is already the latest available version."
            }
            else {
                $message = "Installed version $installedVersion is newer than channel version $availableVersion. No downgrade was performed."
            }
            Write-Host $message
            Set-InstallerProgress -Percent 100 -Status $message
            return
        }
        Write-Host "Updating Pixel Overlay from $installedVersion to $availableVersion. App data will be preserved."
    }
    else {
        Write-Host "Installing Pixel Overlay version $availableVersion..."
    }

    Set-InstallerProgress -Percent 40 -Status 'Starting Windows App Installer...'

    if ($installedPackage) {
        $installedVolume = $null
        $installedLocation = [string]$installedPackage.InstallLocation
        foreach ($candidateVolume in @(Get-AppxVolume | Where-Object { !$_.IsOffline })) {
            $candidatePath = [string]$candidateVolume.PackageStorePath
            $normalizedCandidatePath = $candidatePath.TrimEnd([char]92) + [char]92
            if (![string]::IsNullOrWhiteSpace($candidatePath) -and
                $installedLocation.StartsWith($normalizedCandidatePath, [StringComparison]::OrdinalIgnoreCase)) {
                $installedVolume = $candidateVolume
                break
            }
        }

        if ($installedVolume) {
            Write-Host "Keeping the existing Windows-managed package volume: $($installedVolume.PackageStorePath)"
            Add-AppxPackage -Path $manifestPath -AppInstallerFile -Volume $installedVolume -ErrorAction Stop
        }
        else {
            Write-Warning 'The existing package volume could not be identified. Windows will use its normal update location.'
            Add-AppxPackage -Path $manifestPath -AppInstallerFile -ErrorAction Stop
        }
    }
    elseif (![string]::IsNullOrWhiteSpace($VolumePath)) {
        $volume = Get-AppxVolume | Where-Object {
            $_.PackageStorePath -eq $VolumePath -and !$_.IsOffline
        } | Select-Object -First 1

        if (!$volume) {
            throw "The selected Windows app package volume is no longer available: $VolumePath"
        }

        Write-Host "Selected Windows-managed package volume: $($volume.PackageStorePath)"
        Add-AppxPackage -Path $manifestPath -AppInstallerFile -Volume $volume -ErrorAction Stop
    }
    else {
        Add-AppxPackage -Path $manifestPath -AppInstallerFile -ErrorAction Stop
    }

    Set-InstallerProgress -Percent 90 -Status 'Verifying the installed package...'
    $installedPackage = Get-AppxPackage -Name 'PixelOverlay' |
        Sort-Object { [version]$_.Version } -Descending |
        Select-Object -First 1
    if (!$installedPackage) {
        throw 'Windows did not register the Pixel Overlay package after installation.'
    }
    if ([version]$installedPackage.Version -lt $availableVersion) {
        throw "Windows registered version $($installedPackage.Version), but the expected version is $availableVersion."
    }

    $gameBar = Get-AppxPackage -Name 'Microsoft.XboxGamingOverlay' -ErrorAction SilentlyContinue
    if (!$gameBar) {
        Write-Warning 'Xbox Game Bar is not installed for the current user. Install or enable Xbox Game Bar before opening Pixel Overlay.'
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show(
            'Pixel Overlay was installed, but Xbox Game Bar was not detected for this Windows user.' + [Environment]::NewLine + [Environment]::NewLine +
            'Install or enable Xbox Game Bar from Microsoft Store, then press Win+G and open Widgets.',
            'Pixel Overlay — Xbox Game Bar required',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        )
    }

    Write-Host "Pixel Overlay installed. Package version: $($installedPackage.Version)"
    Write-Host 'Updates are configured through the stable App Installer manifest.'
    Set-InstallerProgress -Percent 100 -Status "Pixel Overlay $($installedPackage.Version) is ready."
}
catch {
    Set-InstallerProgress -Percent 100 -Status 'Installation failed. Open the installer log for details.'
    Write-Error $_
    Write-Host "Installation log: $logPath" -ForegroundColor Yellow
    exit 1
}
finally {
    if ($transcriptStarted) {
        try {
            Stop-Transcript | Out-Null
        }
        catch {}
    }
    if (Test-Path -LiteralPath $manifestPath) {
        Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
    }
}
