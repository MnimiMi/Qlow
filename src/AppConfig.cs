using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qlow;

public sealed class SerialConfig
{
    /// <summary>
    /// USB ids to accept, as "VID:PID" in hex. The defaults cover the bridges found
    /// on hobby boards. If none of them match, a lone USB serial port is used
    /// whatever its id, so an unlisted adapter still works without editing this.
    /// </summary>
    public List<string> UsbIds { get; set; } = new()
    {
        "1A86:7523", // CH340
        "1A86:5523", // CH341
        "1A86:55D4", // CH9102
        "10C4:EA60", // CP2102
        "10C4:EA70", // CP2105
        "0403:6001", // FT232R
        "0403:6015", // FT231X
        "2341:0043", // Arduino Uno
        "2341:0001", // Arduino Uno, older revision
        "2341:0036", // Arduino Leonardo, bootloader
        "2341:8036", // Arduino Leonardo, sketch
        "2A03:0043", // Arduino.org Uno
        "1B4F:9206", // SparkFun Pro Micro
        "303A:1001"  // ESP32-S2 / S3 native USB
    };

    /// <summary>Force a specific port (e.g. "COM6"). Leave null to locate by USB id every time.</summary>
    public string? PortOverride { get; set; }
    /// <summary>115200 matches the stock Adalight sketch. Reflash the bundled firmware to use 500000.</summary>
    public int BaudRate { get; set; } = 115200;
    /// <summary>Channel order the strip expects: RGB, GRB, BGR, RBG, GBR, BRG.</summary>
    public string ColorOrder { get; set; } = "RGB";
    /// <summary>Milliseconds to wait after opening the port, letting the AVR bootloader finish.</summary>
    public int BootDelayMs { get; set; } = 2000;
}

/// <summary>
/// Describes the strip as a loop around the screen edge. Nearly every hand-built
/// ambilight is that shape; what differs between builds is only how many LEDs sit
/// on each run, where the data-in end is, and which way round it goes. Reverse and
/// Rotate cover the last two without anyone having to redraw a layout.
/// </summary>
public sealed class LayoutConfig
{
    /// <summary>auto = use a Prismatik profile if one exists, else generate. Also: prismatik, generated.</summary>
    public string Source { get; set; } = "auto";

    /// <summary>LEDs from the seam on the bottom edge leftwards to the bottom-left corner.</summary>
    public int BottomLeft { get; set; } = 10;
    public int Left { get; set; } = 22;
    public int Top { get; set; } = 38;
    public int Right { get; set; } = 22;
    /// <summary>LEDs from the bottom-right corner leftwards back to the seam.</summary>
    public int BottomRight { get; set; } = 28;

    /// <summary>How far in from the edge each zone samples, as a fraction of the screen.</summary>
    public double Depth { get; set; } = 0.15;

    /// <summary>Flip the direction the strip runs. Applied before Rotate.</summary>
    public bool Reverse { get; set; }

    /// <summary>Shift which physical LED is treated as the first one, in LEDs.</summary>
    public int Rotate { get; set; }
}

public sealed class CaptureConfig
{
    public int MonitorIndex { get; set; }
    /// <summary>Upper bound on capture rate. The serial link is usually the real limit.</summary>
    public int TargetFps { get; set; } = 30;
    /// <summary>Desired width of the downscaled frame the zones are averaged from.</summary>
    public int DownscaleWidth { get; set; } = 320;
}

public sealed class ColorConfig
{
    public double Gamma { get; set; } = 2.0;
    /// <summary>Master brightness, 0-100.</summary>
    public int Brightness { get; set; } = 100;
    /// <summary>Saturation boost, 0-100.</summary>
    public int Vibrance { get; set; } = 41;
    /// <summary>0 = no smoothing, 0.95 = very slow. Applied per frame.</summary>
    public double Smoothing { get; set; } = 0.55;
    /// <summary>
    /// Lowest brightness a zone is allowed to fall to, 0-100. 0 disables the floor and
    /// a black screen means a dark strip. Set to, say, 10 to keep a low ambient glow.
    /// A zone that still has colour keeps its hue on the way up; one that is genuinely
    /// black uses DarkColor.
    /// </summary>
    public int MinBrightness { get; set; }

    /// <summary>Hue used by MinBrightness when a zone has no colour of its own, as #RRGGBB.</summary>
    public string DarkColor { get; set; } = "#FFFFFF";

    /// <summary>Zones dimmer than this (0-255) are forced to black to kill sensor noise.</summary>
    public int MinLuminance { get; set; } = 2;
    /// <summary>Set to 0 to disable white-balance correction.</summary>
    public int TemperatureK { get; set; }
}

public sealed class PowerConfig
{
    /// <summary>Current one LED draws at full white. WS2812B is about 50-60 mA.</summary>
    public int LedMilliAmps { get; set; } = 50;
    /// <summary>Budget for the strip. 0 disables the limiter.</summary>
    public double PowerSupplyAmps { get; set; }
}

public sealed class WatchdogConfig
{
    /// <summary>Resend the last frame at least this often so the firmware never times out to black.</summary>
    public int HeartbeatMs { get; set; } = 100;
    /// <summary>Rebuild the capture stack if no frame has arrived for this long.</summary>
    public int CaptureStallMs { get; set; } = 3000;
    /// <summary>Reconnect backoff bounds.</summary>
    public int ReconnectMinMs { get; set; } = 250;
    public int ReconnectMaxMs { get; set; } = 5000;
}

public sealed class AppConfig
{
    public bool Enabled { get; set; } = true;
    public bool BlackOnExit { get; set; } = true;
    public bool BlackOnLock { get; set; }
    public string LogLevel { get; set; } = "Info";

    public SerialConfig Serial { get; set; } = new();
    public LayoutConfig Layout { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public ColorConfig Color { get; set; } = new();
    public PowerConfig Power { get; set; } = new();
    public WatchdogConfig Watchdog { get; set; } = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qlow");

    public static string FilePath { get; } = Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
                if (cfg != null)
                {
                    cfg.Apply();
                    // Write it straight back so a config from an older build gains any
                    // new sections at their defaults, instead of leaving them invisible.
                    cfg.Save();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Config unreadable, falling back to defaults", ex);
        }

        var fresh = new AppConfig();
        fresh.Save();
        fresh.Apply();
        return fresh;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save config", ex);
        }
    }

    private void Apply()
    {
        Log.MinLevel = Enum.TryParse<LogLevel>(LogLevel, true, out var lvl) ? lvl : Qlow.LogLevel.Info;
    }
}
