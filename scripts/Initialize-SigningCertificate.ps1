[CmdletBinding()]
param(
    [string]$Repository = 'valenokw2/bobsiwww222zxcv'
)

$ErrorActionPreference = 'Stop'

if (!(Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) is required. Install it from https://cli.github.com/ and run: gh auth login'
}

& gh auth status --hostname github.com
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI is not authenticated. Run: gh auth login'
}

$now = Get-Date
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq 'CN=PixelOverlayDeveloper' -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt $now.AddYears(1) -and
        @($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (!$certificate) {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=PixelOverlayDeveloper' -FriendlyName 'Pixel Overlay persistent MSIX signing certificate' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter $now.AddYears(5)
}

if (!$certificate.HasPrivateKey) {
    throw 'The selected certificate has no private key. Remove it from CurrentUser\My and run this script again.'
}

$randomBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
$pfxPassword = [Convert]::ToBase64String($randomBytes)
[Array]::Clear($randomBytes, 0, $randomBytes.Length)

$pfxPath = Join-Path $env:TEMP ("PixelOverlay-Signing-" + [guid]::NewGuid().ToString('N') + '.pfx')

try {
    $securePassword = ConvertTo-SecureString $pfxPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword -Force | Out-Null
    $pfxBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath))

    $pfxBase64 | & gh secret set PIXELOVERLAY_SIGNING_PFX_BASE64 --repo $Repository
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not upload PIXELOVERLAY_SIGNING_PFX_BASE64. Check repository admin access and gh authentication.'
    }

    $pfxPassword | & gh secret set PIXELOVERLAY_SIGNING_PFX_PASSWORD --repo $Repository
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not upload PIXELOVERLAY_SIGNING_PFX_PASSWORD. Run this script again to retry both secrets.'
    }
}
finally {
    if (Test-Path -LiteralPath $pfxPath) {
        Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
    }
    $pfxPassword = $null
    $pfxBase64 = $null
}

Write-Host ''
Write-Host 'Persistent Pixel Overlay signing certificate is ready.' -ForegroundColor Green
Write-Host "Subject:    $($certificate.Subject)"
Write-Host "Thumbprint: $($certificate.Thumbprint)"
Write-Host "Expires:    $($certificate.NotAfter)"
Write-Host "Secrets uploaded to: $Repository"
Write-Host 'The private PFX was not written to the repository. A temporary PFX copy was deleted.'
