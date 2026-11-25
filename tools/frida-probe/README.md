# Frida probe (Android)

`uiautomator dump` only gives Driftcheck the bounds, text and `resource-id` of each element. This probe attaches
[Frida](https://frida.re) to the running app and reads what the View tree really holds:

- text: size (sp), weight, font family, color, line height, letter spacing
- background: color, corner radius, stroke width and color (`ColorDrawable`, `GradientDrawable`,
  `MaterialShapeDrawable`, `CardView`, `MaterialButton`, React Native backgrounds and borders)
- effective alpha, elevation, padding

`compare_screen` then compares font size, weight and family and the corner radius as values, and
`capture_screen` lists them. Android Views and React Native; Jetpack Compose isn't supported yet.

## Setup

1. On the Mac: `pip3 install frida` (the probe runs with `python3`; set `DRIFTCHECK_PYTHON` for another one).
2. On the device, a `frida-server` of the **same version** as the Python module, running as root. On an
   emulator with a Google APIs image (`userdebug`):

   ```bash
   adb root
   adb push frida-server /data/local/tmp/frida-server
   adb shell "chmod 755 /data/local/tmp/frida-server; /data/local/tmp/frida-server -D &"
   ```

   Download it from the [Frida releases](https://github.com/frida/frida/releases)
   (`frida-server-<version>-android-<abi>.xz`, ABI from `adb shell getprop ro.product.cpu.abi`).

Without Frida everything works as before: the report says the fonts were only estimated from the screenshot.
`DRIFTCHECK_FRIDA=off` turns the probe off.

## Files

- `agent/index.js` — the agent that walks the View tree.
- `_agent.js` — the agent compiled with `frida-java-bridge` (Frida 17 no longer bundles it). Rebuild it after
  changing the agent: `npm install && npm run build`.
- `probe.py` — attaches to the app and prints the JSON that `FridaProbe.cs` parses.
