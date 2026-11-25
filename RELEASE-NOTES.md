# Pixel Overlay v__VERSION__

This release is installed and updated through Windows App Installer. No separate setup executable is required.

## Installation and updates
- Open the stable [PixelOverlay.appinstaller](https://github.com/valenokw2/bobsiwww222zxcv/releases/download/update-channel/PixelOverlay.appinstaller) link to install or update Pixel Overlay.
- Windows App Installer downloads and registers the signed MSIX package and its required x64 framework dependencies.
- A stable .appinstaller update URI is registered for on-launch and Windows background update checks.
- The public publisher certificate (PixelOverlay.cer) is included for first-time certificate trust when Windows requires it.
- Windows manages the protected MSIX package location and the configured default app package volume.
- The package is signed using a persistent Code Signing certificate managed through GitHub Actions Secrets; the private key is not stored in the repository.

## Application functionality
- Import PNG, JPG, JPEG and GIF images through the settings widget.
- Drag and drop images into the settings widget.
- Image library and persistent selection.
- Opacity, zoom, position and rotation controls.
- Animated GIF playback in the overlay.
- Proportional image fitting as the Game Bar widget is resized.

## Known limitations
- Beta sideload build, not a Microsoft Store release.
- The package publisher certificate is self-signed; Windows may require a one-time certificate trust step.
- Settings preview displays the first GIF frame; animated playback is in the overlay.
- Position offsets are in device-independent pixels, not normalized percentages of the widget size.
- Runtime behavior inside the actual Game Bar host still requires hands-on validation on supported Windows systems.

Report failures with the Windows build, exact reproduction steps, image format/dimensions, and a screenshot of the widget.
