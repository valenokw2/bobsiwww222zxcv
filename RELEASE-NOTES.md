# Pixel Overlay v1.0.1 Beta 1

First public test package for the Windows / Xbox Game Bar UWP widget.

## Included
- Signed x64 MSIX/AppX package.
- Self-signed test certificate and PowerShell installer.
- Any x64 framework dependencies produced by the packaging build.

## Current functionality
- Import PNG, JPG, JPEG and GIF images through the settings widget.
- Drag and drop images into the settings widget.
- Image library and persistent selection.
- Opacity, zoom, position and rotation controls.
- Animated GIF playback in the overlay.
- Proportional image fitting as the Game Bar widget is resized.

## Known limitations
- This is a beta sideload build, not a Microsoft Store release.
- The package uses a self-signed test certificate; the included installer imports it into the current user's Trusted People store.
- Settings preview displays the first GIF frame; animated playback is in the overlay.
- Position offsets are in device-independent pixels, not normalized percentages of the widget size.
- Runtime behavior inside the actual Game Bar host still needs hands-on validation across Windows systems.

Report failures with the Windows version, exact reproduction steps, image format/dimensions, and a screenshot of the widget.
