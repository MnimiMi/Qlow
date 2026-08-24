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

Ambilight software has a habit of working perfectly until it doesn't. The strip
goes dark, the app still sits in the tray looking healthy, and only a restart
brings it back. It is almost always one of a small set of causes, and each one has
a fix that belongs in the driver rather than in the user's habits.

| Failure | What Qlow does about it |
|---|---|
| Windows renumbers the USB adapter | Finds the board by USB vendor and product id on every connect, never by a remembered COM name |
| A write fails and the handle is left open | Any write error closes the port and re-locates the device, with backoff, forever |
| Opening the port reboots the board | Holds DTR and RTS de-asserted — on an Arduino Nano, DTR is capacitively coupled to RESET |
| Screen capture is lost | Treats duplication loss as routine and rebuilds unconditionally; a watchdog covers stalls that never report themselves |
| Sleep, lock screen, resolution change | Each event forces a full re-init of both capture and serial |
| Firmware blanks on a quiet link | Resends the last frame on a heartbeat, so a static screen never looks like a dead host |
| Resolution changes break the mapping | Stores the layout in normalised coordinates rather than absolute pixels |
| The board resets and nobody notices | Watches the return channel for the boot greeting and logs every reset with its timing |

---

## Features

- **Zone capture via DXGI Desktop Duplication** — the desktop is copied into a
  mipped texture on the GPU and only a small mip is read back, so a 2560×1440
  screen costs a 320×180 readback instead of 14 MB per frame
- **Configurable strip geometry** — per-side LED counts, sampling depth, direction
  and start offset, all in JSON
- **Colour pipeline** — vibrance, white balance, temporal smoothing, gamma, a noise
  floor, and a current limiter that scales the whole frame uniformly so hues survive
- **Test patterns** for working out orientation and channel order on a strip you did
  not wire yourself
- **Self-test mode** that exercises capture without opening the serial port
- **Always-on logging** with a health line every minute
- Tray toggle, autostart, hot config reload — no restart for anything

---

## Hardware

Anything that speaks Adalight over a serial port will do. The reference build:

| Part | Used here |
|---|---|
| Controller | Arduino Nano (ATmega328P) |
| USB bridge | CH340 |
| Strip | 120 × WS2812B in a closed loop around the panel |
| Power | Strip on its own 5 V supply, grounds tied to the board |
| Display | 2560×1440 |

Other boards, other LED counts and other USB bridges are all configuration, not
code. Discovery covers CH340/CH341/CH9102, CP2102, FTDI, Arduino Uno and Leonardo,
SparkFun and native ESP32 USB — and if none of those match but the machine has
exactly one USB serial port, that port is used anyway.

> **Wiring note.** Do not feed 5 V into the board's `5V` pin while USB is also
> connected. That puts two supplies in parallel on one rail, and when it sags the
> MCU resets while the USB bridge keeps the COM port open — so the strip flashes
> its startup pattern and the host sees nothing wrong. Power the board from USB,
> the strip from its own supply, and tie the grounds. A 330–470 Ω resistor in the
> data line and 1000 µF across the strip's supply are both worth adding.

---

## Setup

1. Run `Qlow.exe`. An icon appears in the tray — that is the whole UI.
2. Open the log and confirm a line like `Serial open on COM7 at 115200 baud`.
3. Set your per-side LED counts in `config.json`, then **Reload config and layout**.
4. Use the test patterns to sort out direction and colour order.

Files land in:

| Path | What |
|---|---|
| `%APPDATA%\Qlow\config.json` | Every setting |
| `%APPDATA%\Qlow\layout.json` | One sampling rectangle per LED, in strip order |
| `%LOCALAPPDATA%\Qlow\logs\qlow.log` | The first place to look when anything misbehaves |

---

## Configuration

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

`reverse` and `rotate` are applied on every load, so they can be changed and
reloaded without regenerating `layout.json`. Changing the per-side counts needs
the file deleted first.

### Frame rate ceiling

A frame is `6 + leds × 3` bytes and serial costs 10 bits per byte:

| Baud | Bytes/s | Max fps at 120 LEDs |
|---|---|---|
| 115200 | 11 520 | ~31 |
| 500000 | 50 000 | ~136 |

No value of `targetFps` beats this. To go faster, flash the bundled firmware with a
higher `BAUD_RATE` and match `serial.baudRate` — both sides must agree.

### Ambient floor on a dark screen

`minLuminance` and `minBrightness` are opposite ends of the same range and work
together:

| Setting | Does |
|---|---|
| `minLuminance` | A noise gate. Anything below it is forced to black, so a nearly-dark zone does not shimmer on sensor noise |
| `minBrightness` | A floor. Nothing is allowed to sit below it, so the strip keeps a low glow instead of going out |

Set `minBrightness` to `10` and a black screen leaves the strip at 10% rather than
dark. It is applied per zone, so a fully black screen is just the case where every
zone hits the floor at once — a dark corner of a bright scene gets the same
treatment.

A zone that still has colour is scaled up and keeps its hue. A zone that is
genuinely black has no hue to keep, so it takes `darkColor`, normalised so the
result lands exactly on the floor. `#FFFFFF` gives neutral white; something like
`#FF8000` gives a warm glow instead.

### Current limiting

`powerSupplyAmps` defaults to `0`, meaning no limit, which is right when the supply
is sized properly. Set it to the real rating to enforce headroom: the whole frame is
then scaled uniformly, preserving hue, rather than clipped per LED. 120 WS2812B at
full white want roughly 6 A, so a low limit will be dimming most of the time.

---

## Test Patterns

Tray menu → **Test patterns**. Orientation is guesswork on a strip you did not wire,
so these answer it directly.

| Pattern | Answers |
|---|---|
| **Chase** — one dot walks the strip | Which physical LED is number 1, and which way it runs → `layout.reverse`, `layout.rotate` |
| **Red, green, blue** — whole strip in turn | Whether the channel order is right → `serial.colorOrder` |
| **Sides** — each run in its own colour | Whether the per-side counts match the strip |

Solid patterns run at a quarter brightness on purpose: flooding a whole strip at
full white on an underspecified supply is a good way to brown the board out
mid-test.

---

## Firmware

`firmware/Qlow_Adalight` speaks the Adalight framing, so the host works with any
stock Adalight sketch. Flashing this one adds three things:

- **Silence holds the last frame** instead of blanking the strip, so a brief hiccup
  on the PC does not read as a hardware fault
- **The header parser resynchronises byte by byte**, so a truncated write costs one
  frame instead of desyncing the stream
- **The baud rate is a constant you can raise**

Set `NUM_LEDS`, `DATA_PIN`, `LED_TYPE` and `COLOR_ORDER` at the top before flashing.
Requires the FastLED library.

`USE_WATCHDOG` is off by default. It is worth enabling, but **only on a board with
the Optiboot bootloader** — every current Nano and Uno has it. On the older
pre-Optiboot bootloader a watchdog reset causes a boot loop that looks exactly like
a dead board and needs an ISP programmer to recover.

---

## Diagnostics

```bash
Qlow.exe --selftest
```

Exercises capture, layout and colour processing **without opening the serial port**,
so it can run while something else still owns the device. Writes
`%LOCALAPPDATA%\Qlow\selftest.txt` with the resolved port, every USB serial port
on the machine, the capture resolution and rate, the serial frame-rate ceiling and
sampled LED values.

The log answers most of the rest:

| Line | Meaning |
|---|---|
| `Serial write failed on COM7` → `Closing COM7` | The board went away; the following lines show it reconnecting |
| `AcquireNextFrame failed: ... (desktop switch, fullscreen transition or mode change)` | Normal, rebuilds itself |
| `No frame for 3000 ms, forcing capture rebuild` | The watchdog caught a stall that did not report itself |
| `Controller rebooted (137.4s since the last one, 3 total)` | The board reset on its own while the link stayed up — brownout, a loose lead, or a watchdog in the sketch |
| `Health: 30.0 fps captured, 54000 frames sent on COM7, 0 controller reboots` | Written every minute |

Set `"logLevel": "Debug"` for more.

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

For something that runs on a machine with no .NET installed:

```bash
dotnet publish Qlow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist/Qlow
```

One 66 MB `.exe` with the runtime inside it, no dependencies.

---

## License

MIT. Third-party components: [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT),
`System.IO.Ports` (MIT), [FastLED](https://github.com/FastLED/FastLED) (MIT).
