# BaldLight

An ambilight driver for a hand-built Adalight strip. Written to replace Prismatik
on one specific complaint: it stops working and does not come back.

Hardware this was built against: CH340 USB-serial adapter, Arduino Nano, 120
WS2812B on a separate supply, 2560x1440 panel.

## Why not just keep Prismatik

Prismatik (the `psieg/Lightpack` fork) is fine software, but its failure handling
assumes things that are not true on a desktop that sleeps, locks, and runs games:

| Prismatik behaviour | What it costs you |
|---|---|
| Serial port pinned to a name in `main.conf` | Windows renumbers CH340 adapters; the pinned port then points at nothing |
| Write failures leave the handle open | The app looks alive while writing into a dead endpoint |
| Desktop Duplication loss is not always recovered | Fullscreen transitions, driver resets and the lock screen all kill it |
| LED layout stored in absolute pixels | A resolution change silently corrupts the mapping |
| Only sends when colours change | An Adalight sketch that blanks on timeout goes dark on a static screen |

BaldLight inverts each of those.

## What it does differently

**Finds the device, does not assume it.** The port is located by USB vendor and
product id on every connect. `portOverride` exists if you want to pin one anyway.

**Reconnects forever.** Any write failure closes the handle, re-locates the
device, and retries with exponential backoff between `reconnectMinMs` and
`reconnectMaxMs`. There is no state in which it gives up.

**Rebuilds capture unconditionally.** `DXGI_ERROR_ACCESS_LOST` and friends are
treated as normal events, not errors. A watchdog also forces a rebuild if no
frame has arrived for `captureStallMs`, which covers the failure modes that do
not report themselves at all.

**Knows about sleep and locking.** Resume, session unlock and display changes
each trigger a full re-init of both the capture stack and the serial link.

**Heartbeats the strip.** The last frame is resent every `heartbeatMs` even when
nothing on screen moved, so a firmware timeout never fires.

**Never resets your board by accident.** DTR and RTS are held de-asserted. On a
Nano, DTR is capacitively coupled to RESET, and a port that opens and closes
repeatedly reboots the board every time.

**Layout in normalised coordinates.** Zones are stored as fractions of the
screen, so changing resolution does not move them.

Setting it up on a different rig — other board, other LED count, other wiring
direction — is covered in [SETUP.md](SETUP.md).

## Build

Needs the .NET 8 SDK.

```bash
dotnet build BaldLight.csproj -c Release
```

The result lands in `bin\Release\net8.0-windows\win-x64\BaldLight.exe`.

To produce something someone else can run without installing anything:

```bash
dotnet publish BaldLight.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist/BaldLight
```

One 66 MB `.exe` with the runtime inside it, no dependencies.

## Finding the device

The port is located by USB vendor and product id on every connect, never by a
remembered COM name. `serial.usbIds` ships with the bridges found on hobby boards
(CH340/CH341/CH9102, CP2102, FTDI, Arduino Uno and Leonardo, SparkFun, native
ESP32 USB). If none of them match but the machine has exactly one USB serial port,
that port is used anyway — an unlisted adapter still works without configuration.
The motherboard's own COM1 is never a candidate, because discovery walks the USB
device tree rather than `SerialPort.GetPortNames`.

## Test patterns

Tray menu → **Test patterns**. These exist because orientation is pure guesswork
on a strip you did not wire yourself.

| Pattern | Answers |
|---|---|
| Chase | Which physical LED is number 1, and which way the strip runs → `layout.reverse`, `layout.rotate` |
| Red, green, blue | Whether the channel order is right → `serial.colorOrder` |
| Sides | Whether the per-side counts match the strip → `layout.bottomLeft` and friends |

Solid patterns run at a quarter brightness deliberately: flooding a whole strip at
full white on an underspecified supply is a good way to brown the board out
mid-test.

## Diagnostics

```bash
BaldLight.exe --selftest
```

Exercises capture, layout and colour processing **without opening the serial
port**, so it can run while another ambilight app still owns the device. Writes
`%LOCALAPPDATA%\BaldLight\selftest.txt` with the resolved port, every USB serial
port on the machine, the capture resolution and rate, the serial frame-rate
ceiling, and sampled LED values.

## First run

On first start it looks for an existing Prismatik profile under
`%USERPROFILE%\Prismatik` and imports the LED layout from it, so a strip that was
already mapped by hand does not have to be mapped again. Failing that, it
generates a default 120-LED loop.

Two files are written to `%APPDATA%\BaldLight`:

- `config.json` — everything tunable
- `layout.json` — one normalised rect per LED, in strip order

The log is at `%LOCALAPPDATA%\BaldLight\logs\baldlight.log`.

## Configuration

```jsonc
{
  "enabled": true,
  "blackOnExit": true,      // send a black frame before releasing the port
  "blackOnLock": false,     // go dark on the lock screen
  "logLevel": "Info",       // Debug while diagnosing

  "serial": {
    "usbIds": ["1A86:7523", "10C4:EA60", "0403:6001", "..."],
    "portOverride": null,   // set to "COM6" to pin it
    "baudRate": 115200,     // 500000 after flashing the bundled firmware
    "colorOrder": "RGB",
    "bootDelayMs": 2000     // AVR bootloader settling time
  },

  "layout": {
    "source": "auto",       // auto | prismatik | generated
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
    "targetFps": 30,        // the serial link is the real ceiling at 115200
    "downscaleWidth": 320   // GPU mip level chosen to land near this width
  },

  "color": {
    "gamma": 2.0,
    "brightness": 100,
    "vibrance": 41,         // Prismatik calls this OverBrighten
    "smoothing": 0.55,      // 0 = instant, 0.95 = very slow
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

### Frame rate ceiling

At 120 LEDs a frame is 366 bytes. Serial costs 10 bits per byte, so:

| Baud | Bytes/s | Max fps |
|---|---|---|
| 115200 | 11520 | ~31 |
| 500000 | 50000 | ~136 |

Raising `baudRate` requires flashing `firmware/BaldLight_Adalight` with a matching
`BAUD_RATE`. Both sides must agree or you get garbage.

### Current limiting

`powerSupplyAmps` defaults to 0, meaning no limit. Set it to the real rating of
the strip supply if you want the headroom enforced — the whole frame is then
scaled uniformly, which preserves hue, rather than clipped per LED, which does
not. 120 WS2812B at full white draw roughly 6 A, so a limit that low will be
visibly dimming most of the time.

## Firmware

`firmware/BaldLight_Adalight` is Adalight-compatible, so the host works with a
stock sketch too. Flashing it buys three things: silence holds the last frame
instead of blanking, the header parser resynchronises byte by byte, and the baud
rate can be raised.

`USE_WATCHDOG` is off by default. It is worth turning on, but only on a board with
the Optiboot bootloader — every current Nano and Uno has it. On the original
pre-Optiboot bootloader a watchdog reset causes a boot loop that looks like a
dead board and needs an ISP programmer to recover.

## Tray menu

- **Backlight** — toggle, also on double-click
- **Reconnect now** — force a rebuild of capture and serial
- **Edit config** / **Reload config and layout** — no restart needed
- **Re-import layout from Prismatik**
- **Open log**
- **Run at startup** — an HKCU `Run` entry, no elevation
