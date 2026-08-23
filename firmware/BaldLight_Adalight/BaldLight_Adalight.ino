// BaldLight firmware - an Adalight-compatible receiver built to fail safely.
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
#define DATA_PIN      6
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

#if USE_WATCHDOG
#include <avr/wdt.h>
#endif

// ---------------------------------------------------------------------------

static CRGB leds[NUM_LEDS];

// Header is 'A' 'd' 'a', count-high, count-low, checksum.
static const uint8_t MAGIC[3] = { 'A', 'd', 'a' };

static uint8_t  headerPos = 0;
static uint8_t  header[6];
static uint32_t lastFrameMs = 0;
static bool     everReceived = false;

void setup() {
#if USE_WATCHDOG
  // Disable first: if we arrived here via a watchdog reset, the timer is still
  // armed with whatever interval tripped it.
  wdt_disable();
#endif

  FastLED.addLeds<LED_TYPE, DATA_PIN, COLOR_ORDER>(leds, NUM_LEDS);
  FastLED.clear(true);

  Serial.begin(BAUD_RATE);

  // Announce ourselves so a host can identify the board without guessing. The
  // BaldLight host ignores this; it is here for manual probing with a terminal.
  Serial.print(F("Ada\n"));

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
