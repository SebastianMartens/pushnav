---
title: Camera & Lens
---

## Sample Frame @ Bortle 8.5

**M45 (Pleiades), invisible to the naked eye from a Bortle 8.5 sky in Chennai, India.** The camera captures it clearly in a single short exposure. Naked-eye limiting magnitude here is around 2.3 mag; the camera reaches about 6.3 mag. Four magnitudes of improvement means roughly 40× more stars in the frame, plenty for a reliable plate-solve.

![Sample Frame](assets/single_frame_bortle8.png)

- Bortle 8.5 city sky (Chennai, India), under a hazy light-pollution dome.
- Naked-eye visual limiting magnitude here: around 2.3 mag.
- Sensor + F2.4 lens captures stars down to about 6.3 mag in this sky, with good plate-solve confidence.
- More than 40 stars detected per frame, well above the threshold for a reliable solve.
- Rule of thumb: each magnitude of fainter detection adds roughly 2.5× more stars in the frame, so 4 magnitudes ≈ 40× more visible stars.

## Quick Specs

```text
QUICK SPECS

Camera:       OV9281, 1MP mono, USB UVC
Lens:         M12, 25mm, F/2.4
FOV:          8.86° × 4.98°
Pixel scale:  24.9″/px
Plate-solve mag (Bortle 8.5):  ~6.3
Cost:         $41 USD (camera + lens)
```

## Supported Hardware

These OV9281-based USB cameras are recognized automatically (matched by USB VID/PID):

| Camera | Status |
|---|---|
| [Waveshare OV9281 1MP Mono USB Camera](https://www.waveshare.com/ov9281-1mp-usb-camera-a.htm) [$26] | Supported (reference) |
| Arducam OV9281 | Beta — awaiting results |
| DECXIN OV9281 | Beta — awaiting results |

**Lens**: [M12 Mount 25mm F2.4 Lens](https://www.seeedstudio.com/5MP-25mm-lens-p-5579.html) [$15]

These are reference suppliers. The **OV9281** module itself is widely sold by Arducam, Innomaker, AliExpress and others, and any M12 25mm F/2.4 lens with the correct mount works. If a link above goes dead, search the sensor name (`OV9281`) and lens specs and pick any reputable seller — the parts themselves are commodity.

## Why This Camera and Lens

- Off the shelf, affordable, and widely available
- For use in urban light polluted skies:
    - Mono sensor provides substantially better sensitivity and contrast for star detection compared to a color sensor
    - F2.4 aperture provides good light gathering power required to detect faint stars in light polluted urban skies
    - 25mm focal length in combination with the OV9281's 1/4" sensor size provides a ~8° field of view which is a good balance between having enough stars for reliable plate-solving and speed of solving
    - In my Bortle 8 city sky with a visual limiting magnitude of around 2.5, this camera/lens combo can reliably detect and plate-solve on stars down to about magnitude 6.5
- Standard USB (UVC) interface allows for cross-platform compatibility without needing custom drivers
- Calculated image scale and field of view (Astrometry results):
    - Size:	8.86° x 4.98° 
    - Radius: 5.081°
    - Pixel scale: 24.9 arcsec/pixel

>The Waveshare OV9281 combination is the fully verified setup; the other OV9281 modules listed above are in beta, awaiting field results. Support for additional cameras and lenses may be added in the future based on demand and usability. I decided to keep it this way for now to ensure a consistent and reliable user experience. Also this combination is very affordable and widely available, so it should be accessible for most users.

## IDS uEye cameras (Windows, optional)

PushNav also works with **IDS uEye** industrial cameras on Windows. The model it was built and tested with is the **UI-306xCP-M** (USB 3, USB ID `1409:3060`, Sony IMX174 mono sensor, global shutter, 1936 × 1216 pixels, 5.86 µm). Other uEye models are listed and opened with generic defaults but have not been tried.

**What you need**

1. The **IDS Software Suite** from IDS (ids-imaging.com, Downloads). It installs the camera driver and the uEye API runtime (`ueye_api_64.dll`). Version 4.97 is verified. After installing it, the camera shows up in the IDS Camera Manager.
2. The C# camera server, `camera_server_dotnet.exe`. It is the default camera server on Windows. A source checkout builds it with `camera\windows-dotnet\build.bat` (.NET 10 SDK and the MSVC linker).
3. Close other programs that use the camera (for example *uEye Cockpit*): only one program can open it at a time.

**Licensing: IDS software is not part of PushNav.** The IDS driver and the uEye runtime are proprietary software of IDS Imaging Development Systems. PushNav does not contain, bundle or redistribute any IDS file. The camera server loads `ueye_api_64.dll` only at run time and only when the IDS Software Suite has put it on your machine; without it the IDS support is simply not available and everything else works as before. The API declarations in PushNav's C# code are written by PushNav and released under the same GPL-3.0-or-later licence as the rest of the project.

**How it behaves**

- The camera runs in 8-bit mono with all automatic exposure, gain and frame-rate features switched off. Frames are sent to the app as grayscale JPEG, about 10 frames per second at the default settings.
- **Exposure** appears in the app in the same units as for the OV9281 (steps of 0.1 ms, 0.1 ms to 1.5 s). A longer exposure lowers the frame rate by itself so the frame always fits. Beyond about 1 s the sensor switches to its long-exposure mode and stays there until the camera server restarts (leaving that mode would interrupt the picture for about 2.5 s); shorter exposures are still accepted in that mode.
- **Gain** is the sensor's master gain, 0 to 100.
- If the camera is unplugged the camera server stops and the app tries to reconnect for about half a minute. If you plug the camera back in later, use the app's *Retry camera* button.

**Choosing the camera server.** The default `"dotnet"` starts the C# server first and falls back to the original C server when the C# server finds no supported camera (that is how the OV9281 modules keep working). To force one of them set `camera.windows_server` to `"dotnet"` or `"c"` in the config file, or set the environment variable `PUSHNAV_CAMERA_SERVER` for a single run. `camera_server_dotnet.exe --list` prints the cameras it sees and their controls as JSON, which is the first thing to check when a camera is not found.

**Lens and plate solving.** The reference setup is a 25 mm lens on this sensor, a field of 25.6° × 16.2°. The star catalog shipped with PushNav is built for a 7° to 10° field, so plate solving with this camera needs a wide-field catalog that does not exist yet (tracked in [issue #3](https://github.com/SebastianMartens/pushnav/issues/3)). Until then the camera shows its live image in the app but will not solve.

## Adding Support for More Cameras

- Adding the camera's VID/PID to the camera server's supported device list and implementing any necessary quirks for frame capture (if required)
- Generating the tetra3rs star catalog for the new camera/lens's field of view and plate scale (via `scripts/build_tetra3rs_catalog.py`).
