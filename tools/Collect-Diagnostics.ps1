[CmdletBinding()]
param(
    [string]$OutputDirectory = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP "PixelOverlay-Diagnostics-$stamp"
New-Item -ItemType Directory -Force -Path $work | Out-Null

"Collected: $(Get-Date -Format o)" | Set-Content (Join-Path $work 'collection.txt')
"Computer: $env:COMPUTERNAME" | Add-Content (Join-Path $work 'collection.txt')
"User: $env:USERNAME" | Add-Content (Join-Path $work 'collection.txt')
Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber, OsArchitecture |
    Format-List | Out-File (Join-Path $work 'windows.txt')

$packages = Get-AppxPackage -Name 'PixelOverlay' -ErrorAction SilentlyContinue
if ($packages) {
    $packages | Select-Object Name, PackageFullName, PackageFamilyName, Version, Architecture, InstallLocation |
        Format-List | Out-File (Join-Path $work 'package.txt')

    foreach ($package in $packages) {
        $localState = Join-Path $env:LOCALAPPDATA ("Packages\" + $package.PackageFamilyName + "\LocalState")
        $runtimeLog = Join-Path $localState 'pixeloverlay-diagnostics.log'
        if (Test-Path $runtimeLog) {
            Copy-Item $runtimeLog (Join-Path $work 'pixeloverlay-diagnostics.log') -Force
        } else {
            "Runtime log not found at: $runtimeLog" | Add-Content (Join-Path $work 'collection.txt')
        }
    }
} else {
    'Get-AppxPackage did not find a package named PixelOverlay.' | Add-Content (Join-Path $work 'collection.txt')
}

try {
    Get-CimInstance Win32_VideoController |
        Select-Object Name, DriverVersion, VideoModeDescription |
        Format-List | Out-File (Join-Path $work 'display.txt')
} catch {}

$channels = @(Get-WinEvent -ListLog '*GameBar*' -ErrorAction SilentlyContinue)
foreach ($channel in $channels) {
    try {
        Get-WinEvent -FilterHashtable @{ LogName = $channel.LogName; StartTime = (Get-Date).AddHours(-2) } -MaxEvents 300 -ErrorAction Stop |
            Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message |
            Format-List | Out-File (Join-Path $work (($channel.LogName -replace '[\\/:*?"<>|]', '_') + '.txt'))
    } catch {
        "Could not read $($channel.LogName): $($_.Exception.Message)" |
            Add-Content (Join-Path $work 'collection.txt')
    }
}

$zip = Join-Path $OutputDirectory "PixelOverlay-Diagnostics-$stamp.zip"
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
Write-Host "Diagnostics archive: $zip"
