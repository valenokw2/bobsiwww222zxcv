# Pixel Overlay release process

## One-time signing setup

The package publisher must remain CN=PixelOverlayDeveloper and every update must be signed by the same persistent certificate. Do not generate a new certificate for each release and never commit a PFX/private key.

On a trusted Windows checkout, install GitHub CLI, authenticate it, and run:

\`\`\`powershell
gh auth login
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Initialize-SigningCertificate.ps1
\`\`\`

The script creates or reuses a five-year Code Signing certificate in the current user's certificate store, exports a temporary PFX, uploads two repository Actions secrets, and deletes the temporary PFX:
- PIXELOVERLAY_SIGNING_PFX_BASE64
- PIXELOVERLAY_SIGNING_PFX_PASSWORD

The GitHub account must have permission to manage Actions secrets. Only the public .cer is distributed with releases.

## Build a release

Open GitHub Actions → Pixel Overlay Release → Run workflow. Enter a four-part numeric MSIX version such as 1.0.2.0. For a beta release, keep prerelease enabled and use a channel such as beta.1.

Alternatively, push a tag in the format v1.2.3.4 or v1.2.3.4-beta.1. The four-part numeric portion becomes the package version and must always increase. For example:
- v1.0.2.0-beta.1
- v1.0.2.1-beta.2
- v1.0.3.0

The release workflow builds and signs x64 MSIX, discovers and publishes its x64 framework dependencies, generates the .appinstaller document, compiles PixelOverlay-Setup.exe, publishes a GitHub Release, and updates PixelOverlay.appinstaller on main.

## Update channel

The App Installer document is served from the stable URL:

https://raw.githubusercontent.com/valenokw2/bobsiwww222zxcv/main/PixelOverlay.appinstaller

The manifest's main package and dependency URIs point to the exact assets for the current release. OnLaunch checks are configured for 12-hour intervals, and AutomaticBackgroundTask enables Windows background update checks. Keep the stable manifest URL and package Publisher unchanged.

## Important

- Never regenerate the signing certificate after the first setup unless intentionally migrating the publisher identity and update trust.
- Never commit .pfx files, private keys, or signing passwords.
- Increment all four MSIX version components monotonically for every package update.
- A self-signed package certificate does not grant SmartScreen reputation. A publicly trusted code-signing certificate or Microsoft Store distribution is needed for a polished public release.
- The first Setup.exe is a bridge for existing beta users; they must run it once to install the persistent publisher certificate and associate the app with App Installer. Later MSIX updates can be handled by Windows.
