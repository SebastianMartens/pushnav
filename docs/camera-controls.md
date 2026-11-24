---
title: Camera controls
---

# Camera controls

<!-- fork: user note for the camera controls (specs/camera-server/SPEC_CAMERA_CONTROLS.md) -->

The **Camera** card in the side panel lets you change **exposure**, **frame rate**, **gain** and
**binning** while PushNav runs. Each camera offers only what it really has, with its real ranges,
so the card looks different for different cameras:

| Camera | What the card shows |
|---|---|
| IDS UI-306xCP-M (C# camera server) | exposure up to 4 s, frame rate, gain, binning 1x1 / 2x2 |
| OV9281 module and the other cameras (C, Linux, macOS camera servers) | exposure and gain, as before |

Your settings are saved **per camera model** and restored the next time that camera connects.
They are restored in the order binning, frame rate, exposure, gain.

## Exposure and frame rate belong together

A frame cannot be shorter than its exposure, so the two controls follow each other:

- Set a **longer exposure** than one frame lasts and PushNav lowers the **frame rate** for you
  instead of cutting the exposure short.
- The **frame rate** slider only goes as high as the current exposure allows. To get more frames
  per second, shorten the exposure first.
- Both sliders show real units (µs, ms, s and frames per second), and the exposure slider is
  logarithmic, so a few hundred microseconds and several seconds are both easy to reach.

With a 4 s exposure a new frame arrives every 4 s. PushNav knows that and waits for it, so the camera
is not mistaken for a stalled one and restarted.

## Binning

**Binning** adds up neighbouring pixels. 2x2 binning turns the IDS camera's 1936 x 1216 pixels into
968 x 608: the picture has a quarter of the pixels, each one collects four times the light, and the
camera can run faster. The field of view stays the same; every pixel covers twice the sky, which is
also what the plate-solver sees (about 97 arc seconds per pixel instead of 48 with the 25 mm lens).

Choose 2x2 when the sky is dark and stars are faint, or when you want more frames per second; stay at
1x1 for the finest detail.

- Changing the binning interrupts the picture for a moment and the card shows
  **Reconfiguring camera…**. The controls are disabled meanwhile. PushNav does not restart the camera
  server and does not lose the connection.
- Your sync and roll calibration stay valid, and so does the field of view you entered.
- The camera measures its frame timing again at the new binning, so the ranges of the frame rate and
  the exposure can change; the sliders move to the new ranges by themselves.

## Long exposures on the IDS camera

Beyond about 1 s the sensor switches to its **long-exposure mode**. In that mode the camera no longer
regulates the frame rate (a frame lasts as long as the exposure), so the **Frame rate** slider is
locked and shows the rate the camera really runs at. The binning cannot be changed either.

When you bring the exposure back below about 1 s, the camera leaves long-exposure mode. Leaving it
takes a few seconds in which no picture arrives; the card shows **Reconfiguring camera…** for that time
and PushNav does not treat it as a failure. Afterwards the frame rate slider works again.

## Older camera servers

A camera server that only speaks the first version of the camera protocol (the Windows C server and
the macOS and Linux servers) has no frame-rate or binning control. Those cameras show the exposure
and gain sliders they always had.
