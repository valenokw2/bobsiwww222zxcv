[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,
    [switch]$TrustCertificateOnly
)

$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path $env:LOCALAPPDATA 'PixelOverlay'
$null = New-Item -ItemType Directory -Force -Path $logDirectory
$logPath = Join-Path $logDirectory 'Installer.log'
$manifestPath = Join-Path $env:TEMP ("PixelOverlay-" + [guid]::NewGuid().ToString('N') + '.appinstaller')
$transcriptStarted = $false

try {
    Start-Transcript -Path $logPath -Append | Out-Null
    $transcriptStarted = $true

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

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $CertificatePath).Path
    )
    if ($certificate.Subject -ne 'CN=PixelOverlayDeveloper') {
        throw "Unexpected certificate publisher: $($certificate.Subject)"
    }
    if ($certificate.NotAfter -le (Get-Date)) {
        throw 'The included signing certificate has expired.'
    }

    $hasCodeSigningEku = @($certificate.EnhancedKeyUsageList | Where-Object {
        $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3'
    }).Count -gt 0
    if (!$hasCodeSigningEku) {
        throw 'The included certificate is not a Code Signing certificate.'
    }

    $trustedCertificate = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
        Select-Object -First 1

    if ($TrustCertificateOnly) {
        if (!$trustedCertificate) {
            Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        }
        Write-Host "Trusted publisher certificate installed for this Windows device: $($certificate.Thumbprint)"
        return
    }

    if (!$trustedCertificate) {
        throw 'The Pixel Overlay publisher certificate is not installed in Local Computer > Trusted People. Run the setup installer again and approve its administrator prompt.'
    }

    $appInstallerUri = 'https://github.com/valenokw2/bobsiwww222zxcv/releases/download/update-channel/PixelOverlay.appinstaller'
    Write-Host "Downloading update manifest: $appInstallerUri"
    Invoke-WebRequest -Uri $appInstallerUri -OutFile $manifestPath -UseBasicParsing -MaximumRedirection 5

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

    Write-Host "Installing Pixel Overlay version $($mainPackage.GetAttribute('Version'))..."
    Add-AppxPackage -AppInstallerFile $manifestPath -ForceApplicationShutdown -ErrorAction Stop

    $installedPackage = Get-AppxPackage -Name 'PixelOverlay' |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if (!$installedPackage) {
        throw 'Windows did not register the Pixel Overlay package after installation.'
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
}
catch {
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
