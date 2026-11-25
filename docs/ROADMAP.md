# Follow-up work

## Viewport-relative sizing and placement

The overlay and settings preview currently use `Stretch="Uniform"`, so the image automatically fits inside the current widget viewport while preserving its aspect ratio. The existing Scale control (10–500%) acts as zoom on top of that fitted size. The transform origin is kept at the viewport center and recalculated on `SizeChanged`.

The remaining sizing work is to give users explicit modes and make placement adapt predictably when the Game Bar window is resized:

- Add **Fit**, **Fill/Crop**, and **Actual Size** modes.
- Add viewport-relative position anchors (percentages or normalized coordinates) alongside the current DIP offsets, so a placement can remain proportional after a resize.
- Persist the sizing mode and anchor values in `OverlayState` and include them in presets.
- Verify portrait, landscape, ultrawide, and repeated resize operations with static images and GIFs.

## Image pipeline follow-up

- The settings preview currently displays the first GIF frame; animated playback is implemented in the overlay, not in the settings preview.
- Run manual Xbox Game Bar tests for file picker activation, drag-and-drop, cross-process settings propagation, transparency/click-through, pinned mode, and resize behavior.
- CI validates the Windows UWP build and manifest resources, but it does not launch the widget inside the Game Bar host.
