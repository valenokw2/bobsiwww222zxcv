# Pixel Overlay v__VERSION__

A single-file Windows installer bundles the signed app package, all required x64 framework dependencies, the publisher certificate and the deployment logic. Windows App Installer remains the update provider after setup.

## Installation and updates
- Download and run `PixelOverlay-Setup.exe`; no separate MSIX, APPX dependency or certificate downloads are required.
- The setup imports the embedded public publisher certificate into Local Computer > Trusted People after one administrator approval.
- The app itself is installed for the currently signed-in Windows user.
- The setup installs the embedded signed MSIX and its bundled x64 framework dependencies from local files.
- If Windows reports ERROR_PACKAGES_IN_USE (0x80073D02), setup shows the affected package list and asks before force-closing apps that use shared components; the default choice is to leave apps running.
- The stable App Installer update source is registered after installation when the update channel is reachable.
- Windows continues to check for updates on launch and through its background update task.
- The versioned release also retains the package assets used by the App Installer update channel. They are implementation assets and do not need to be downloaded manually.
- Windows manages the protected MSIX package location and the configured default app package volume.
- The installer and app package are signed with the persistent Pixel Overlay Code Signing certificate; the private key is not stored in the repository.

## Application functionality
- Import PNG, JPG, JPEG and GIF images through the settings widget.
- Drag and drop images into the settings widget.
- Image library and persistent selection.
- Opacity, zoom, position and rotation controls.
- Animated GIF playback in the overlay.
- Proportional image fitting as the Game Bar widget is resized.

## Known limitations
- Beta sideload build, not a Microsoft Store release.
- The package publisher certificate is self-signed; Windows may show a SmartScreen warning because the publisher is not backed by a public certificate authority.
- Automatic update registration requires the stable App Installer channel to be reachable during setup. The bundled app can still be installed locally if the update channel is temporarily unavailable.
- Settings preview displays the first GIF frame; animated playback is in the overlay.
- Position offsets are in device-independent pixels, not normalized percentages of the widget size.
- Runtime behavior inside the actual Game Bar host still requires hands-on validation on supported Windows systems.

Report failures with the Windows build, exact reproduction steps, image format/dimensions, and a screenshot of the widget.
