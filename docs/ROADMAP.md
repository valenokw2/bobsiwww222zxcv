# Follow-up work

## Viewport-relative overlay scaling

The overlay and settings preview now use proportional `Uniform` rendering so the complete image is visible inside the available widget area. The existing Scale control remains a zoom multiplier on top of that fitted baseline.

A dedicated follow-up is still needed for window-relative sizing behavior:

- Add explicit image sizing modes such as **Fit**, **Fill/Crop**, and **Actual Size**.
- Recalculate the image's fitted dimensions from the overlay viewport's current `ActualWidth` and `ActualHeight` whenever the Game Bar widget is resized.
- Preserve the source aspect ratio, and keep the existing zoom, position, rotation, and opacity controls composable with the selected sizing mode.
- Persist the selected mode and zoom settings in `OverlayState` and include them in presets.
- Verify PNG, JPEG, and animated GIF rendering in portrait, landscape, and extreme-aspect-ratio windows, including repeated resize operations.

The Game Bar overlay window supports user resizing, so scaling must follow the widget's client area rather than assume the initial 300 × 300 size.
