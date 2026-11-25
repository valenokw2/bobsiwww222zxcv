# Pixel Overlay v__VERSION__

This release introduces a single-file Windows setup program and the first persistent update channel.

## Installation and updates
- PixelOverlay-Setup.exe built with Inno Setup.
- Automatically checks Windows version and x64 architecture.
- Installs the publisher certificate into Local Computer > Trusted People after UAC approval; the app package remains per-user.
- Installs the signed MSIX package and its x64 framework dependencies through Windows App Installer.
- Registers a stable .appinstaller update URI hosted as a fixed GitHub Release asset.
- Enables on-launch update checks and Windows background update checks.
- Uses a persistent Code Signing certificate imported from GitHub Actions Secrets; the private key is not stored in the repository.

## Application functionality
- Import PNG, JPG, JPEG and GIF images through the settings widget.
- Drag and drop images into the settings widget.
- Image library and persistent selection.
- Opacity, zoom, position and rotation controls.
- Animated GIF playback in the overlay.
- Proportional image fitting as the Game Bar widget is resized.

## Known limitations
- Beta sideload build, not a Microsoft Store release.
- The package publisher certificate is self-signed; Windows SmartScreen may still warn about the installer.
- Settings preview displays the first GIF frame; animated playback is in the overlay.
- Position offsets are in device-independent pixels, not normalized percentages of the widget size.
- Runtime behavior inside the actual Game Bar host still requires hands-on validation on supported Windows systems.

Report failures with the Windows build, exact reproduction steps, image format/dimensions, and a screenshot of the widget.
