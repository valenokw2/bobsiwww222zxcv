[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MainPackagePath,
    [string[]]$DependencyPaths = @(),
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,
    [string]$RepositoryFullName = 'valenokw2/bobsiwww222zxcv',
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-PackageIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPath)
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
            throw "Package '$Path' has no Package/Identity element."
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = $identity.GetAttribute('Version')
            ProcessorArchitecture = $identity.GetAttribute('ProcessorArchitecture')
        }
    }
    finally {
        $archive.Dispose()
    }
}

$mainPackage = (Resolve-Path -LiteralPath $MainPackagePath).Path
$mainIdentity = Get-PackageIdentity -Path $mainPackage

if ($mainIdentity.Name -ne 'PixelOverlay') {
    throw "Unexpected main package identity: $($mainIdentity.Name)."
}
if ($mainIdentity.Version -ne $Version) {
    throw "Manifest version '$Version' does not match MSIX version '$($mainIdentity.Version)'."
}
if ($mainIdentity.Publisher -ne 'CN=PixelOverlayDeveloper') {
    throw "Unexpected package publisher: $($mainIdentity.Publisher)."
}
if ($mainIdentity.ProcessorArchitecture -ne 'x64') {
    throw "Expected an x64 MSIX, received '$($mainIdentity.ProcessorArchitecture)'."
}

$releaseBase = "https://github.com/$RepositoryFullName/releases/download/$ReleaseTag"
$stableAppInstallerUri = "https://raw.githubusercontent.com/$RepositoryFullName/main/PixelOverlay.appinstaller"
$namespace = 'http://schemas.microsoft.com/appx/appinstaller/2021'

$document = New-Object System.Xml.XmlDocument
$document.AppendChild($document.CreateXmlDeclaration('1.0', 'UTF-8', $null)) | Out-Null

$root = $document.CreateElement('AppInstaller', $namespace)
$root.SetAttribute('Version', $Version)
$root.SetAttribute('Uri', $stableAppInstallerUri)
$document.AppendChild($root) | Out-Null

$main = $document.CreateElement('MainPackage', $namespace)
$main.SetAttribute('Name', $mainIdentity.Name)
$main.SetAttribute('Publisher', $mainIdentity.Publisher)
$main.SetAttribute('Version', $mainIdentity.Version)
$main.SetAttribute('ProcessorArchitecture', $mainIdentity.ProcessorArchitecture)
$main.SetAttribute('Uri', "$releaseBase/$([IO.Path]::GetFileName($mainPackage))")
$root.AppendChild($main) | Out-Null

if ($DependencyPaths.Count -gt 0) {
    $dependenciesNode = $document.CreateElement('Dependencies', $namespace)
    $seen = @{}

    foreach ($dependencyPath in $DependencyPaths) {
        $path = (Resolve-Path -LiteralPath $dependencyPath).Path
        $identity = Get-PackageIdentity -Path $path

        if ($identity.ProcessorArchitecture -notin @('x64', 'neutral')) {
            throw "Dependency '$($identity.Name)' is not compatible with x64: $($identity.ProcessorArchitecture)."
        }
        if ($seen.ContainsKey($identity.Name)) {
            throw "Duplicate framework dependency '$($identity.Name)' was found."
        }
        $seen[$identity.Name] = $true

        $dependency = $document.CreateElement('Package', $namespace)
        $dependency.SetAttribute('Name', $identity.Name)
        $dependency.SetAttribute('Publisher', $identity.Publisher)
        $dependency.SetAttribute('Version', $identity.Version)
        if ($identity.ProcessorArchitecture) {
            $dependency.SetAttribute('ProcessorArchitecture', $identity.ProcessorArchitecture)
        }
        $dependency.SetAttribute('Uri', "$releaseBase/$([IO.Path]::GetFileName($path))")
        $dependenciesNode.AppendChild($dependency) | Out-Null
    }

    $root.AppendChild($dependenciesNode) | Out-Null
}

$updateSettings = $document.CreateElement('UpdateSettings', $namespace)
$onLaunch = $document.CreateElement('OnLaunch', $namespace)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '12')
$onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
$onLaunch.SetAttribute('ShowPrompt', 'true')
$updateSettings.AppendChild($onLaunch) | Out-Null

$backgroundTask = $document.CreateElement('AutomaticBackgroundTask', $namespace)
$updateSettings.AppendChild($backgroundTask) | Out-Null
$root.AppendChild($updateSettings) | Out-Null

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and !(Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.Indent = $true
$writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
try {
    $document.Save($writer)
}
finally {
    $writer.Dispose()
}

[xml]$validation = Get-Content -LiteralPath $OutputPath -Raw
if ($validation.DocumentElement.GetAttribute('Uri') -ne $stableAppInstallerUri) {
    throw 'The App Installer manifest does not contain the expected stable update URI.'
}
if (!$validation.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")) {
    throw 'The App Installer manifest is missing MainPackage.'
}

Write-Host "Created App Installer manifest: $OutputPath"
Write-Host "Application version: $Version"
Write-Host "Release asset base: $releaseBase"
Write-Host "Stable update URI: $stableAppInstallerUri"
Write-Host "Framework dependencies: $($DependencyPaths.Count)"
