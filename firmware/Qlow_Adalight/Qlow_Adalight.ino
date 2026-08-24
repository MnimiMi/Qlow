// Qlow firmware - an Adalight-compatible receiver built to fail safely.
//
// Differences from the stock Adalight sketch, all of them aimed at the strip
// going dark for no visible reason:
//
//   * Serial silence holds the last frame instead of blanking the strip. The
//     stock sketch treats a quiet link as "turn everything off", so any hiccup
//     on the host is immediately visible as darkness.
//   * The header parser resynchronises on every byte, so a truncated write can
//     never desync the stream permanently.
//   * An optional hardware watchdog recovers the board from a lockup.
//   * The baud rate is a single constant, matching config.json on the host.
//
// Wiring assumed: data on DATA_PIN, strip on its own supply, grounds tied
// together between the supply and the board.

#include <FastLED.h>

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

#define NUM_LEDS      120
#define DATA_PIN      5
#define LED_TYPE      WS2812B
#define COLOR_ORDER   RGB

// Must match "baudRate" in config.json.
// 115200 is the stock Adalight rate and caps out near 31 fps at 120 LEDs.
// 500000 is clean on a 16 MHz AVR and lifts that ceiling well past the refresh
// rate of the panel.
#define BAUD_RATE     115200

// Hold the last frame forever when the host goes quiet (1), or fade out after
// HOLD_TIMEOUT_MS (0). Holding is the safer default: a frozen picture is a much
// clearer symptom than a dark strip, and it does not look like a hardware fault.
#define HOLD_LAST_FRAME 1
#define HOLD_TIMEOUT_MS 10000UL

// Hardware watchdog. Leave this at 0 unless the board has a modern bootloader.
// Optiboot, which every current Nano and Uno ships with, clears the watchdog
// reset flag on boot and behaves correctly. The original pre-Optiboot bootloader
// does not, and a watchdog reset there puts the board into a boot loop that
// looks like a dead board until it is reflashed with an ISP programmer.
#define USE_WATCHDOG  0

#include <avr/wdt.h>

// ---------------------------------------------------------------------------
// Reset cause reporting
// ---------------------------------------------------------------------------
//
// MCUSR records why the chip last reset, but it is not cleared by a reset, so the
// flags accumulate until something wipes them. It also has to be read before the
// bootloader or any library gets a chance to clear it. Placing this in .init3 runs
// it before main(), which is early enough.
//
// The four flags answer very different questions:
//   BORF  brown-out   supply dipped below the detector threshold
//   EXTRF external    something pulled the RESET pin low
//   WDRF  watchdog    the sketch stopped feeding the timer
//   PORF  power-on    supply went away completely and came back

static uint8_t resetFlags __attribute__((section(".noinit")));

void captureResetFlags(void) __attribute__((naked, used, section(".init3")));
void captureResetFlags(void) {
  resetFlags = MCUSR;
  MCUSR = 0;
  wdt_disable();
}

// ---------------------------------------------------------------------------
// Survival state and supply monitoring
// ---------------------------------------------------------------------------
//
// MCUSR reading zero says no hardware reset flag was set, which usually means the
// chip jumped to address zero in software rather than being reset. It does not on
// its own clear the supply of suspicion: if the brown-out detector fuse is off, a
// dip simply makes the CPU execute nonsense, and one common outcome is exactly
// that jump, with no flag set.
//
// So two things are tracked across restarts, in .noinit so they survive a jump to
// zero but not a real loss of power:
//   - a canary proving RAM was never lost, which rules the supply out directly
//   - the lowest supply voltage seen, measured by the chip itself
//
// Vcc is read by measuring the internal 1.1 V bandgap against AVcc. The reference
// is only accurate to about 10 percent, so the absolute number is rough; what
// matters is whether it dips, and by how much.

#define RAM_CANARY 0xBA1DCA7Ful

static uint32_t ramCanary  __attribute__((section(".noinit")));
static uint16_t bootCount  __attribute__((section(".noinit")));
static uint16_t minVccMv   __attribute__((section(".noinit")));

static uint16_t readVccMv(void) {
  // 1.1 V bandgap as the input, AVcc as the reference.
  ADMUX = _BV(REFS0) | _BV(MUX3) | _BV(MUX2) | _BV(MUX1);
  delayMicroseconds(300);          // the reference needs time to settle
  ADCSRA |= _BV(ADSC);
  while (ADCSRA & _BV(ADSC)) { }
  uint16_t adc = ADC;
  if (adc == 0) return 0;
  return (uint16_t)(1125300UL / adc);   // 1.1 V * 1023 * 1000
}

// ---------------------------------------------------------------------------

static CRGB leds[NUM_LEDS];

// Header is 'A' 'd' 'a', count-high, count-low, checksum.
static const uint8_t MAGIC[3] = { 'A', 'd', 'a' };

static uint8_t  headerPos = 0;
static uint8_t  header[6];
static uint32_t lastFrameMs = 0;
static bool     everReceived = false;
static uint32_t lastVccSampleMs = 0;
static uint32_t lastDiagMs = 0;

void setup() {
  FastLED.addLeds<LED_TYPE, DATA_PIN, COLOR_ORDER>(leds, NUM_LEDS);
  FastLED.clear(true);

  Serial.begin(BAUD_RATE);

  // Announce ourselves so a host can identify the board without guessing.
  Serial.print(F("Ada\n"));

  // Then say why we just started. The host logs this, which turns "it rebooted
  // again" into an actual cause instead of a guess.
  Serial.print(F("rst="));
  if (resetFlags & _BV(WDRF))  Serial.print(F("WDT "));
  if (resetFlags & _BV(BORF))  Serial.print(F("BROWNOUT "));
  if (resetFlags & _BV(EXTRF)) Serial.print(F("EXTERNAL "));
  if (resetFlags & _BV(PORF))  Serial.print(F("POWERON "));
  if (resetFlags == 0)         Serial.print(F("UNKNOWN "));
  Serial.print(F("raw:"));
  Serial.println(resetFlags, HEX);

  // Did RAM survive? If the canary is intact the supply never actually went away,
  // whatever else happened. If it is gone, power was lost and everything else in
  // .noinit is meaningless, so start the counters again.
  bool ramSurvived = (ramCanary == RAM_CANARY);
  if (!ramSurvived) {
    ramCanary = RAM_CANARY;
    bootCount = 0;
    minVccMv = 0xFFFF;
  }
  bootCount++;

  uint16_t vcc = readVccMv();
  if (vcc > 0 && vcc < minVccMv) minVccMv = vcc;

  Serial.print(F("diag ram="));
  Serial.print(ramSurvived ? F("KEPT") : F("LOST"));
  Serial.print(F(" boot="));
  Serial.print(bootCount);
  Serial.print(F(" vcc="));
  Serial.print(vcc);
  Serial.print(F(" vmin="));
  Serial.println(minVccMv);

  lastFrameMs = millis();

#if USE_WATCHDOG
  wdt_enable(WDTO_2S);
#endif
}

void loop() {
#if USE_WATCHDOG
  wdt_reset();
#endif

  if (waitForHeader()) {
    readFrame();
  }

  // Sample the supply between frames. This will not catch a microsecond spike, but
  // any dip that lasts long enough to upset the CPU is far longer than that.
  uint32_t now = millis();
  if (now - lastVccSampleMs >= 20) {
    lastVccSampleMs = now;
    uint16_t vcc = readVccMv();
    if (vcc > 0 && vcc < minVccMv) minVccMv = vcc;
  }

  // A periodic line so the supply can be watched live against what is on screen,
  // instead of only being seen after a restart.
  if (now - lastDiagMs >= 5000) {
    lastDiagMs = now;
    Serial.print(F("diag ram=KEPT boot="));
    Serial.print(bootCount);
    Serial.print(F(" vcc="));
    Serial.print(readVccMv());
    Serial.print(F(" vmin="));
    Serial.println(minVccMv);
  }

#if HOLD_LAST_FRAME == 0
  if (everReceived && (millis() - lastFrameMs) > HOLD_TIMEOUT_MS) {
    FastLED.clear(true);
    everReceived = false;
  }
#endif
}

// Slides a six byte window along the stream until the magic word and a matching
// checksum line up. Because the window slides one byte at a time, a partial or
// corrupted write costs one frame rather than desyncing the link.
static bool waitForHeader() {
  while (Serial.available() > 0) {
#if USE_WATCHDOG
    wdt_reset();
#endif

    uint8_t b = (uint8_t)Serial.read();

    if (headerPos < 3) {
      // Still hunting the magic word.
      if (b == MAGIC[headerPos]) {
        header[headerPos++] = b;
      } else {
        // Restart, but allow this byte to be the first letter of a new header.
        headerPos = (b == MAGIC[0]) ? 1 : 0;
        if (headerPos == 1) header[0] = b;
      }
      continue;
    }

    header[headerPos++] = b;

    if (headerPos < 6) continue;

    headerPos = 0;

    uint8_t hi  = header[3];
    uint8_t lo  = header[4];
    uint8_t chk = header[5];

    if (chk != (uint8_t)(hi ^ lo ^ 0x55)) {
      // Bad checksum: this was not a real header. Keep hunting.
      continue;
    }

    uint16_t count = ((uint16_t)hi << 8) | lo;
    count += 1;

    // Only accept the length we are actually wired for; anything else means the
    // host is misconfigured and writing a frame of the wrong size would smear
    // colours across the strip.
    if (count != NUM_LEDS) continue;

    return true;
  }

  return false;
}

// Reads exactly NUM_LEDS * 3 bytes. Gives up and returns to header hunting if
// the host stalls mid-frame, so a half-written frame never latches.
static void readFrame() {
  const uint16_t expected = NUM_LEDS * 3;
  uint16_t received = 0;
  uint32_t deadline = millis() + 200;

  while (received < expected) {
#if USE_WATCHDOG
    wdt_reset();
#endif

    if (Serial.available() > 0) {
      ((uint8_t *)leds)[received++] = (uint8_t)Serial.read();
      deadline = millis() + 200;
      continue;
    }

    if ((int32_t)(millis() - deadline) >= 0) {
      // Truncated frame. Leave the strip showing the previous one.
      return;
    }
  }

  FastLED.show();
  lastFrameMs = millis();
  everReceived = true;
}
