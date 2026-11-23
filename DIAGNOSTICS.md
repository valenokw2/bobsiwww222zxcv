# UWP / Xbox Game Bar diagnostics

This branch adds runtime logging for the native UWP widget. It does not add a browser, server, WebView, or Angular dependency.

## Reproduction

1. Build and deploy the package from this branch (or install the CI artifact if one is provided).
2. Close every existing Pixel Overlay widget instance.
3. Open Xbox Game Bar with Win+G and launch **Pixel Overlay**.
4. Wait 10 seconds, then capture a screenshot of the blank/working widget.
5. Close and reopen the widget once. This helps distinguish first activation from reactivation.
6. Run the collector below from PowerShell. It creates a ZIP on the Desktop.

## Collect logs

From the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\Collect-Diagnostics.ps1
```

The ZIP contains:
- PixelOverlay's LocalState runtime log;
- package identity/version/install location;
- Windows build and basic display information;
- available Xbox Game Bar event-channel entries (when Windows exposes them).

The runtime log is written inside the app container:
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\pixeloverlay-diagnostics.log`

## What to send back

Send the generated ZIP and:
- screenshot of the blank widget;
- screenshot of the widget's normal state, if it ever renders;
- exact action sequence (Win+G, widget selected, settings opened, etc.);
- whether Settings opens while the overlay is blank.

Do not send signing certificates, passwords, or unrelated personal files.

## Current investigation points

The manifest registers two Game Bar extensions: `PixelOverlay` (Standard) and `PixelOverlaySettings` (Settings). Activation is routed in `App.OnActivated`. The most useful first evidence is whether the app receives `ms-gamebarwidget` activation, which extension ID is supplied, and whether the frame navigates to `OverlayPage`.

The logger records activation, navigation, page load/unload, widget state events, image selection/loading, and exceptions. It is intentionally fail-safe and writes to LocalState.


## Build status

The diagnostics branch is built by `.github/workflows/uwp-diagnostics-build.yml`. Do not distribute a package until the workflow completes successfully.
