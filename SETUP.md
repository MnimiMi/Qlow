# Qlow — setting it up on your own build

You need: a Windows 10/11 machine, a microcontroller on USB running an Adalight
sketch, and an addressable strip around the screen. Nothing else — the `.exe` has
the runtime inside it.

It does not matter which controller board, which USB-serial chip, how many LEDs
you have, or which corner the strip starts at. All of that is configuration.

---

## 1. Run it

Double-click `Qlow.exe`. An icon appears in the tray. That is the whole app —
there is no main window, on purpose. Everything is a JSON file and a log.

Two files get created in `%APPDATA%\Qlow`:

- `config.json` — every setting
- `layout.json` — one sampling rectangle per LED, in strip order

The log is at `%LOCALAPPDATA%\Qlow\logs\qlow.log`. **Read it first when
anything misbehaves.** Every reconnect, every capture rebuild and every reason for
both is in there.

### Coming from another ambilight app

If you have already mapped your strip somewhere else, import that mapping rather
than redoing it. Whatever you had also has to be **closed first** — two apps cannot
own the same COM port.

**Prismatik.** Detected automatically on first run: Qlow reads
`%USERPROFILE%\Prismatik\main.conf`, follows it to the active profile under
`Prismatik\Profiles`, and converts the zones. Tray menu →
**Import layout → From Prismatik (auto-detect)** re-runs it at any time.

**Hyperion, Hyperion.ng or HyperHDR.** Tray menu →
**Import layout → From file...** and point it at the config. Both layout shapes are
read: the classic `hscan`/`vscan` objects and the flat `hmin`/`hmax`/`vmin`/`vmax`
used by Hyperion.ng and HyperHDR. Their coordinates are already fractions of the
screen, so the mapping transfers exactly.

Note that Hyperion.ng 2.x keeps its settings in a SQLite database rather than a
JSON file — export a config from its web UI first, or paste the LED layout into a
`.json` of its own. A file that is nothing but the `leds` array works too.

There is a headless equivalent, useful for scripting a setup:

```bash
Qlow.exe --import "C:\path\to\hyperion_config.json"
```

Either way the result lands in `layout.json`, and `layout.reverse` / `layout.rotate`
still apply on top — so if the imported strip runs the wrong way, you fix it there
rather than re-importing.

---

## 2. Check it found your board

Tray menu → **Open log**. You want a line like:

```
Serial open on COM7 at 115200 baud
```

If instead you see `No matching USB serial device is present`, run
`Qlow.exe --selftest` from a terminal and open
`%LOCALAPPDATA%\Qlow\selftest.txt`. It lists every USB serial port Windows
can see, with its ids:

```
usb serial  : COM7 (10C4:EA60), COM9 (0403:6001)
selected    : COM7
```

The port is found by USB vendor and product id, never by a remembered "COM7", so
Windows renumbering it changes nothing. The defaults in `serial.usbIds` cover
CH340, CH341, CH9102, CP2102, FTDI, Arduino Uno and Leonardo, SparkFun and native
ESP32 USB. If your adapter is not listed **and it is the only USB serial device on
the machine, it gets used anyway**. Only if you have several do you need to add
your id to `serial.usbIds` or pin `serial.portOverride` to a name.

---

## 3. Tell it about your strip

Tray menu → **Edit config**, and set the `layout` section. Think of the strip as a
loop around the screen, and count the LEDs on each run:

```jsonc
"layout": {
  "source": "auto",     // "generated" skips Prismatik auto-detect and always builds from these numbers
  "bottomLeft": 10,     // from the seam leftwards to the bottom-left corner
  "left": 22,           // up the left edge
  "top": 38,            // across the top, left to right
  "right": 22,          // down the right edge
  "bottomRight": 28,    // from the bottom-right corner leftwards to the seam
  "depth": 0.15,        // how far in from the edge each LED samples
  "reverse": false,     // flip the direction the strip runs
  "rotate": 0           // shift which physical LED counts as the first
}
```

The "seam" is wherever the two ends of the loop meet on the bottom edge. If your
strip starts in a corner instead, set `bottomLeft` to 0 and put everything in the
other four numbers.

Then delete `layout.json` and use **Reload config and layout**. It is regenerated
from these numbers.

`reverse` and `rotate` do **not** need a regenerate — they are applied on every
load, so you can tweak and reload freely.

---

## 4. Get the orientation right

This is the part that is guesswork on someone else's rig, so there are patterns
for it. Tray menu → **Test patterns**.

**Chase — find LED 1 and direction.** One white dot walks the strip once.

- Dot starts at the wrong end → set `"reverse": true`
- Dot starts in the right direction but at the wrong place → adjust `"rotate"`
  by however many LEDs it is off (negative works)

**Red, green, blue — check colour order.** The strip goes red, then green, then
blue, two seconds each.

- Sees them in a different order → fix `serial.colorOrder`. WS2812B is usually
  `GRB`, WS2811 is often `RGB`, some SK6812 want `BGR`. Just try until red is red.

**Sides — check per-side counts.** Each configured run gets its own colour: bottom-left
red, left green, top blue, right yellow, bottom-right magenta.

- A colour boundary lands away from a physical corner → that side's count is wrong
- LEDs at the end stay dark → your counts add up to fewer LEDs than the strip has

The solid patterns run at a quarter brightness on purpose. Flooding a whole strip
at full white on an underspecified supply is a good way to brown the board out
mid-test.

---

## 5. Power

If the strip has its own supply, leave `power.powerSupplyAmps` at `0`. That is no
limit, and it is the right answer when the supply is sized properly.

If you are not sure the supply can carry the strip, set it to the real rating:

```jsonc
"power": {
  "ledMilliAmps": 50,     // one LED at full white; WS2812B is 50-60
  "powerSupplyAmps": 4    // your supply's actual rating
}
```

The whole frame is then scaled down uniformly when it would exceed the budget,
which keeps hues intact — clipping per LED would not. Note that a full-white
frame on 120 WS2812B wants about 6 A, so a low limit will be dimming most of the
time.

**Tie the grounds.** The strip supply's ground and the board's ground must be
connected, or the data line has no reference and you get random colours.

### Keeping a glow on a black screen

By default a black screen means a dark strip. `color.minBrightness` sets a floor,
as a percentage, below which no LED is allowed to fall:

```jsonc
"color": {
  "minBrightness": 10,     // 0 disables the floor
  "darkColor": "#FFFFFF"   // hue used where a zone has no colour of its own
}
```

It is applied per zone, so a fully black screen is just the case where every zone
hits the floor at once. A zone that still has some colour is scaled up and keeps
its hue; one that is genuinely black takes `darkColor`, normalised to land exactly
on the floor. `#FFFFFF` gives neutral white, something like `#FF8000` a warm glow.

**This costs real current, continuously.** At 10% on 120 LEDs the strip draws
roughly 0.6 A whenever it is on, where before it drew almost nothing on dark
content. On a supply with little headroom that constant load is enough to change
behaviour, so raise the floor and the supply together, not one without the other.

Do not confuse it with `color.minLuminance`, which is the opposite end of the same
range: a noise gate that forces anything dimmer than it to black, so a nearly-dark
zone does not shimmer on sensor noise.

### If the controller keeps restarting

A strip flashing its startup pattern, or blinking out for a moment and coming
back, is the board restarting. `Qlow.exe --selftest` and the log will tell you
whether the host ever stopped sending; if it did not, the fault is on the strip
side.

The usual cause is the step in current when every LED changes at once. At 120
LEDs a cut from black to white swings several amps inside one frame, and on a
shared ground that step is enough to upset the microcontroller. Two fixes, and
they work on the same thing from opposite ends:

- **1000 µF across the strip's power input**, at the point the supply arrives.
  Smooths the step in hardware. This is the proper fix and worth doing anyway.
- **`color.maxChangePerFrame`** caps how far any channel may move per frame, so
  the step never gets large in the first place. At `12` a black-to-white cut
  takes about seven tenths of a second instead of one frame, and the current step
  is roughly a ninth of the size. `0` turns it off.

The second needs no soldering and costs only the snap of hard cuts. Raise it to
20 or 25 if transitions feel sluggish; the effect weakens but does not vanish.

The bundled firmware also reports the supply voltage it measures and why it last
restarted, which is usually enough to tell a power problem from a firmware one.

---

## 6. Two things worth fixing on the PC

Both of these cause a strip that works fine and then goes dark for no reason.

**USB selective suspend.** Windows powers down the USB serial adapter. On an
Arduino Nano, DTR is capacitively coupled to RESET, so any port bounce reboots
the board. Qlow holds DTR and RTS de-asserted so it never causes this itself,
but the power manager still can. In an admin terminal:

```bash
powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
```

```bash
powercfg /setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
```

```bash
powercfg /setactive SCHEME_CURRENT
```

Then in Device Manager, find your USB-serial device → **Power Management** → clear
"Allow the computer to turn off this device to save power". Do the same for the
USB Root Hubs.

**Frame rate ceiling.** A frame is `6 + leds * 3` bytes and serial costs 10 bits
per byte. At 115200 with 120 LEDs that is 31.5 fps, and no amount of `targetFps`
changes it. `--selftest` prints your exact ceiling. To go faster, flash the
included firmware with a higher `BAUD_RATE` and match `serial.baudRate`.

---

## 7. Firmware (optional)

`firmware/Qlow_Adalight` is Adalight-compatible, so Qlow works with
whatever sketch you already have. Flashing this one buys three things:

- **Silence holds the last frame for eight seconds, then fades out.** The stock
  Adalight sketch blanks the moment the link goes quiet, so any brief hiccup on
  the PC shows as a strip that drops dead. Holding rides that out. Fading
  afterwards matters just as much: the strip is the only status indicator this
  system has, and a strip still glowing after the program has crashed or the
  machine has rebooted is telling you something that is not true. Lit means live.
- **The header parser resynchronises byte by byte**, so a truncated write costs
  one frame instead of desyncing the stream.
- **The baud rate is a constant you can raise.**

Set `NUM_LEDS`, `DATA_PIN`, `LED_TYPE` and `COLOR_ORDER` at the top before
flashing. Needs the FastLED library.

`USE_WATCHDOG` is off by default. It is worth turning on, but **only on a board
with the Optiboot bootloader** — every current Nano and Uno has it. On the older
pre-Optiboot bootloader, a watchdog reset causes a boot loop that looks exactly
like a dead board and needs an ISP programmer to recover.

---

## Tray menu

| Item | What it does |
|---|---|
| Backlight | On/off. Double-clicking the tray icon does the same |
| Reconnect now | Rebuild capture and re-find the device |
| Test patterns | Chase, colour order, side counts |
| Edit config | Opens `config.json` |
| Import layout | From a detected Prismatik profile, or from a Hyperion / HyperHDR file you pick |
| Reload config and layout | Apply changes without restarting |
| Open log | The first place to look |
| Run at startup | An `HKCU\...\Run` entry, no elevation needed |

## When something goes wrong

Open the log. Everything is in it:

- `Serial write failed on COM7` followed by `Closing COM7` → the board went away;
  the next lines show it reconnecting
- `AcquireNextFrame failed: ... (desktop switch, fullscreen transition or mode
  change)` → normal, it rebuilds by itself
- `No frame for 3000 ms, forcing capture rebuild` → the watchdog caught a stall
  that did not report itself
- `No configured USB id matched, and 2 USB serial ports are present` → say which
  one, via `serial.portOverride`

Set `"logLevel": "Debug"` for more detail.
