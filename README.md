# Qlow

> Ambilight driver for a hand-built LED strip. Captures the screen edge, averages it into zones and streams the result to a WS2812B strip over serial. A Windows tray app with no main window, built so that nothing short of unplugging the board stops it.

---

## Tech Stack

![C#](https://img.shields.io/badge/C%23-.NET_8-512BD4?logo=dotnet&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10_%7C_11-0078D4?logo=windows&logoColor=white)
![WinForms](https://img.shields.io/badge/WinForms-Tray_App-512BD4?logo=dotnet&logoColor=white)
![Vortice](https://img.shields.io/badge/Vortice-Direct3D_11-5C2D91)
![DXGI](https://img.shields.io/badge/DXGI-Desktop_Duplication-107C10)
![Arduino](https://img.shields.io/badge/Arduino-C%2B%2B-00979D?logo=arduino&logoColor=white)
![FastLED](https://img.shields.io/badge/FastLED-WS2812B-FF5252)
![Adalight](https://img.shields.io/badge/Adalight-Serial_Protocol-F5A623)
![License](https://img.shields.io/badge/License-MIT-3DA639)

---

## Why

Ambilight software has a habit of working perfectly until it doesn't. The strip goes dark, the app still sits in the tray looking healthy, and only a restart brings it back. Qlow's answer is: never trust the last known state, always re-derive it.

| Failure | What Qlow does about it |
|---|---|
| Windows renumbers the USB adapter | Finds the board by USB vendor/product id, never a remembered COM name |
| Opening the port reboots the board | Holds DTR/RTS de-asserted — on a Nano, DTR is wired straight to RESET |
| Screen capture silently stalls | A watchdog rebuilds it; duplication loss is treated as routine, not an error |
| Firmware blanks on a quiet link | Resends the last frame on a heartbeat |
| The board resets and nobody notices | Watches for the boot greeting, logs every reset with its timing |

<details>
<summary>Full failure list</summary>

| Failure | What Qlow does about it |
|---|---|
| A write fails and the handle is left open | Any write error closes the port and re-locates the device, with backoff, forever |
| Sleep, lock screen, resolution change | Each event forces a full re-init of both capture and serial |
| Resolution changes break the mapping | Stores the layout in normalised coordinates rather than absolute pixels |

</details>

---

## Features

- **Zone capture via DXGI Desktop Duplication** — the desktop is copied into a mipped texture on the GPU and only a small mip is read back, so a 2560×1440 screen costs a 320×180 readback instead of 14 MB per frame
- **Configurable strip geometry** — per-side LED counts, sampling depth, direction and start offset, all in JSON
- **Colour pipeline** — vibrance, white balance, temporal smoothing, gamma, a noise floor, and a current limiter that scales the whole frame uniformly so hues survive
- **Test patterns** for working out orientation and channel order on a strip you did not wire yourself
- **Self-test mode** that exercises capture without opening the serial port
- Always-on logging with a health line every minute, tray toggle, autostart, hot config reload

---

## Hardware

Anything that speaks Adalight over serial will do. The reference build:

| Part | Used here |
|---|---|
| Controller | Arduino Nano (ATmega328P) |
| USB bridge | CH340 |
| Strip | 120 × WS2812B in a closed loop around the panel |
| Power | Strip on its own 5 V supply, grounds tied to the board |
| Display | 2560×1440 |

Discovery covers CH340/CH341/CH9102, CP2102, FTDI, Arduino Uno and Leonardo, SparkFun and native ESP32 USB — and if none of those match but the machine has exactly one USB serial port, that port is used anyway.

> **Wiring note.** Don't feed 5 V into the board's `5V` pin while USB is also connected — two supplies in parallel on one rail, and when it sags the MCU resets while the USB bridge keeps the COM port open, so the strip flashes its startup pattern and the host sees nothing wrong. Power the board from USB, the strip from its own supply, tie the grounds. A 330–470 Ω resistor in the data line and 1000 µF across the strip's supply are both worth adding.

---

## Setup

1. Run `Qlow.exe`. An icon appears in the tray — that's the whole UI.
2. Open the log and confirm a line like `Serial open on COM7 at 115200 baud`.
3. Set your per-side LED counts in `config.json`, then **Reload config and layout**.
4. Use the test patterns to sort out direction and colour order.

| Path | What |
|---|---|
| `%APPDATA%\Qlow\config.json` | Every setting |
| `%APPDATA%\Qlow\layout.json` | One sampling rectangle per LED, in strip order |
| `%LOCALAPPDATA%\Qlow\logs\qlow.log` | The first place to look when anything misbehaves |

---

## Configuration

<details>
<summary>Full <code>config.json</code> reference</summary>

```jsonc
{
  "enabled": true,
  "blackOnExit": true,      // send a black frame before releasing the port
  "blackOnLock": false,     // go dark on the lock screen
  "logLevel": "Info",       // Debug while diagnosing

  "serial": {
    "usbIds": ["1A86:7523", "10C4:EA60", "0403:6001", "..."],
    "portOverride": null,   // set to "COM7" to pin it
    "baudRate": 115200,     // 500000 after flashing the bundled firmware
    "colorOrder": "RGB",    // GRB on most WS2812B
    "bootDelayMs": 2000     // AVR bootloader settling time
  },

  "layout": {
    "source": "generated",  // build from the numbers below
    "bottomLeft": 10,       // LEDs from the bottom seam to the bottom-left corner
    "left": 22,
    "top": 38,
    "right": 22,
    "bottomRight": 28,      // bottom-right corner back to the seam
    "depth": 0.15,          // how far in from the edge a zone samples
    "reverse": false,       // flip the direction the strip runs
    "rotate": 0             // shift which physical LED is the first one
  },

  "capture": {
    "monitorIndex": 0,
    "targetFps": 30,        // the serial link is usually the real ceiling
    "downscaleWidth": 320   // GPU mip level chosen to land near this width
  },

  "color": {
    "gamma": 2.0,
    "brightness": 100,
    "vibrance": 41,         // saturation boost, 0-100
    "smoothing": 0.55,      // 0 = instant, 0.95 = very slow
    "minBrightness": 0,     // 0-100, lowest a zone may fall to. 0 = a black screen is dark
    "darkColor": "#FFFFFF", // hue the floor uses when a zone has no colour of its own
    "minLuminance": 2,      // noise floor, below this a zone is forced black
    "temperatureK": 0       // 0 disables white balance, else e.g. 6500
  },

  "power": {
    "ledMilliAmps": 50,     // per LED at full white
    "powerSupplyAmps": 0    // 0 disables the current limiter
  },

  "watchdog": {
    "heartbeatMs": 100,
    "captureStallMs": 3000,
    "reconnectMinMs": 250,
    "reconnectMaxMs": 5000
  }
}
```

`reverse` and `rotate` are applied on every load, so they can be changed and reloaded without regenerating `layout.json`. Changing the per-side counts needs the file deleted first.

</details>

**Frame rate ceiling.** A frame is `6 + leds × 3` bytes, serial costs 10 bits/byte — at 120 LEDs that's ~31 fps at 115200 baud, ~136 fps at 500000. No value of `targetFps` beats this; to go faster, flash the bundled firmware with a higher `BAUD_RATE` and match `serial.baudRate` on both sides.

**Ambient floor.** `minLuminance` is a noise gate — below it, a zone is forced black. `minBrightness` is a floor — nothing sits below it, so a black screen glows instead of going dark. A zone with real colour scales up and keeps its hue; a genuinely black zone takes `darkColor` instead (`#FFFFFF` for neutral white, `#FF8000` for warm).

**Current limiting.** `powerSupplyAmps` defaults to `0` (no limit) — set it to the real rating and the whole frame scales down uniformly, preserving hue, rather than clipping per LED. 120 WS2812B at full white want roughly 6 A.

---

## Test Patterns

Tray menu → **Test patterns**. Orientation is guesswork on a strip you didn't wire, so these answer it directly.

| Pattern | Answers |
|---|---|
| **Chase** — one dot walks the strip | Which physical LED is number 1, which way it runs → `layout.reverse`, `layout.rotate` |
| **Red, green, blue** — whole strip in turn | Whether the channel order is right → `serial.colorOrder` |
| **Sides** — each run in its own colour | Whether the per-side counts match the strip |

Solid patterns run at a quarter brightness on purpose — flooding a whole strip at full white on an underspecified supply is a good way to brown the board out mid-test.

---

## Firmware

`firmware/Qlow_Adalight` speaks standard Adalight framing, so the host works with any stock Adalight sketch too. Flashing this one adds: last-frame hold on silence (a brief hiccup doesn't read as a hardware fault), byte-by-byte header resync (a truncated write costs one frame, not a desync), and a baud rate you can raise.

Set `NUM_LEDS`, `DATA_PIN`, `LED_TYPE` and `COLOR_ORDER` before flashing. Requires FastLED.

`USE_WATCHDOG` is off by default — worth enabling, but **only on an Optiboot board** (every current Nano/Uno qualifies). On the older pre-Optiboot bootloader a watchdog reset causes a boot loop that looks exactly like a dead board and needs an ISP programmer to recover.

---

## Diagnostics

```bash
Qlow.exe --selftest
```

Exercises capture, layout and colour processing **without opening the serial port**, so it can run while something else still owns the device. Writes `%LOCALAPPDATA%\Qlow\selftest.txt` with the resolved port, every USB serial port on the machine, capture resolution/rate, the frame-rate ceiling and sampled LED values.

<details>
<summary>Reading the log</summary>

| Line | Meaning |
|---|---|
| `Serial write failed on COM7` → `Closing COM7` | The board went away; the following lines show it reconnecting |
| `AcquireNextFrame failed: ...` | Normal — desktop switch, fullscreen transition, or mode change; rebuilds itself |
| `No frame for 3000 ms, forcing capture rebuild` | The watchdog caught a stall that didn't report itself |
| `Controller rebooted (137.4s since the last one, 3 total)` | The board reset on its own while the link stayed up — brownout, a loose lead, or a watchdog in the sketch |
| `Health: 30.0 fps captured, 54000 frames sent on COM7, 0 controller reboots` | Written every minute |

Set `"logLevel": "Debug"` for more.

</details>

---

## Tray Menu

| Item | Does |
|---|---|
| Backlight | On/off. Double-clicking the tray icon does the same |
| Reconnect now | Rebuild capture and re-find the device |
| Test patterns | Chase, colour order, side counts |
| Edit config | Opens `config.json` |
| Reload config and layout | Apply changes without restarting |
| Open log | The first place to look |
| Run at startup | An `HKCU\...\Run` entry, no elevation needed |

---

## Build

**Prerequisites:** .NET 8 SDK

```bash
dotnet build Qlow.csproj -c Release
```

Output in `bin\Release\net8.0-windows\win-x64\Qlow.exe`.

<details>
<summary>Self-contained build (no .NET required on the target machine)</summary>

```bash
dotnet publish Qlow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist/Qlow
```

One 66 MB `.exe` with the runtime inside it, no dependencies.

</details>

---

## License

MIT. Third-party components: [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT), `System.IO.Ports` (MIT), [FastLED](https://github.com/FastLED/FastLED) (MIT).
