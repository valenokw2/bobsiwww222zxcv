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
    [string]$ManifestUri = '',
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
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

function Get-PackageManifestInfo {
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
                $manifestText = $reader.ReadToEnd()
                $manifest = ConvertFrom-SafeXmlText -XmlText $manifestText
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }

        $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
        if (!$identity) {
            throw "Package '$Path' has no Package/Identity element."
        }

        return [pscustomobject]@{
            Path = $resolvedPath
            Manifest = $manifest
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = [version]$identity.GetAttribute('Version')
            ProcessorArchitecture = $identity.GetAttribute('ProcessorArchitecture').ToLowerInvariant()
        }
    }
    finally { $archive.Dispose() }
}

function Test-ArchitectureCompatible {
    param([string]$Actual, [string]$Required)

    $actual = if ($Actual) { $Actual.ToLowerInvariant() } else { '' }
    $required = if ($Required) { $Required.ToLowerInvariant() } else { 'x64' }
    if ($required -eq 'neutral') { return $actual -eq 'neutral' }
    return ($actual -eq $required -or $actual -eq 'neutral')
}

$mainPackage = (Resolve-Path -LiteralPath $MainPackagePath).Path
$mainInfo = Get-PackageManifestInfo -Path $mainPackage

if ($mainInfo.Name -ne 'PixelOverlay') {
    throw "Unexpected main package identity: $($mainInfo.Name)."
}
if ($mainInfo.Version -ne [version]$Version) {
    throw "Manifest version '$Version' does not match MSIX version '$($mainInfo.Version)'."
}
if ($mainInfo.Publisher -ne 'CN=PixelOverlayDeveloper') {
    throw "Unexpected package publisher: $($mainInfo.Publisher)."
}
if ($mainInfo.ProcessorArchitecture -ne 'x64') {
    throw "Expected an x64 MSIX, received '$($mainInfo.ProcessorArchitecture)'."
}

$releaseBase = "https://github.com/$RepositoryFullName/releases/download/$ReleaseTag"
if ([string]::IsNullOrWhiteSpace($ManifestUri)) {
    $ManifestUri = "https://github.com/$RepositoryFullName/releases/download/update-channel/PixelOverlay.appinstaller"
}
if ($ManifestUri -notmatch '^https://github\.com/') {
    throw 'ManifestUri must be an HTTPS GitHub release asset URL.'
}

$namespace = 'http://schemas.microsoft.com/appx/appinstaller/2021'
$document = New-Object System.Xml.XmlDocument
$document.AppendChild($document.CreateXmlDeclaration('1.0', 'UTF-8', $null)) | Out-Null

$root = $document.CreateElement('AppInstaller', $namespace)
$root.SetAttribute('Version', $Version)
$root.SetAttribute('Uri', $ManifestUri)
$document.AppendChild($root) | Out-Null

$main = $document.CreateElement('MainPackage', $namespace)
$main.SetAttribute('Name', $mainInfo.Name)
$main.SetAttribute('Publisher', $mainInfo.Publisher)
$main.SetAttribute('Version', $mainInfo.Version.ToString())
$main.SetAttribute('ProcessorArchitecture', $mainInfo.ProcessorArchitecture)
$main.SetAttribute('Uri', "$releaseBase/$([IO.Path]::GetFileName($mainPackage))")
$root.AppendChild($main) | Out-Null

$requiredNodes = $mainInfo.Manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']")
$requiredDependencies = @(
    foreach ($node in $requiredNodes) {
        $name = $node.GetAttribute('Name')
        $publisher = $node.GetAttribute('Publisher')
        if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($publisher)) {
            throw 'The main package contains a dependency without a Name or Publisher.'
        }

        $minimumVersionText = $node.GetAttribute('MinVersion')
        if ([string]::IsNullOrWhiteSpace($minimumVersionText)) { $minimumVersionText = '0.0.0.0' }
        $architecture = $node.GetAttribute('ProcessorArchitecture')
        if ([string]::IsNullOrWhiteSpace($architecture)) { $architecture = $mainInfo.ProcessorArchitecture }

        [pscustomobject]@{
            Name = $name
            Publisher = $publisher
            MinVersion = [version]$minimumVersionText
            Architecture = $architecture.ToLowerInvariant()
        }
    }
)

$candidates = @(
    foreach ($dependencyPath in $DependencyPaths) {
        $info = Get-PackageManifestInfo -Path $dependencyPath
        if (!(Test-ArchitectureCompatible -Actual $info.ProcessorArchitecture -Required $mainInfo.ProcessorArchitecture)) {
            throw "Dependency '$($info.Name)' has incompatible architecture '$($info.ProcessorArchitecture)'."
        }
        $info
    }
)

$selectedDependencies = @()
foreach ($required in $requiredDependencies) {
    $matches = @($candidates | Where-Object {
        $_.Name -eq $required.Name -and
        $_.Publisher -eq $required.Publisher -and
        (Test-ArchitectureCompatible -Actual $_.ProcessorArchitecture -Required $required.Architecture) -and
        $_.Version -ge $required.MinVersion
    } | Sort-Object Version -Descending)

    if ($matches.Count -eq 0) {
        throw ("No compatible framework package was supplied for '{0}' ({1}), architecture {2}, minimum version {3}." -f
            $required.Name, $required.Publisher, $required.Architecture, $required.MinVersion)
    }

    $selected = $matches[0]
    if ($selectedDependencies.Name -contains $selected.Name) {
        continue
    }
    $selectedDependencies += $selected
}

if ($selectedDependencies.Count -gt 0) {
    $dependenciesNode = $document.CreateElement('Dependencies', $namespace)
    foreach ($identity in $selectedDependencies) {
        $dependency = $document.CreateElement('Package', $namespace)
        $dependency.SetAttribute('Name', $identity.Name)
        $dependency.SetAttribute('Publisher', $identity.Publisher)
        $dependency.SetAttribute('Version', $identity.Version.ToString())
        if ($identity.ProcessorArchitecture) {
            $dependency.SetAttribute('ProcessorArchitecture', $identity.ProcessorArchitecture)
        }
        $dependency.SetAttribute('Uri', "$releaseBase/$([IO.Path]::GetFileName($identity.Path))")
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
try { $document.Save($writer) }
finally { $writer.Dispose() }

$validation = ConvertFrom-SafeXmlText -XmlText (Get-Content -LiteralPath $OutputPath -Raw)
if ($validation.DocumentElement.GetAttribute('Uri') -ne $ManifestUri) {
    throw 'The App Installer manifest Uri does not match ManifestUri.'
}
if (!$validation.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']")) {
    throw 'The App Installer manifest is missing MainPackage.'
}
$writtenDependencies = @($validation.SelectNodes("/*[local-name()='AppInstaller']/*[local-name()='Dependencies']/*[local-name()='Package']"))
if ($writtenDependencies.Count -ne $selectedDependencies.Count) {
    throw 'The generated App Installer manifest does not contain the expected dependency list.'
}

Write-Host "Created App Installer manifest: $OutputPath"
Write-Host "Application version: $Version"
Write-Host "Release asset base: $releaseBase"
Write-Host "Manifest URI: $ManifestUri"
Write-Host "Required framework dependencies: $($requiredDependencies.Count)"
Write-Host "Published compatible framework dependencies: $($selectedDependencies.Count)"
