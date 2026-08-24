using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qlow;

/// <summary>
/// One LED sampling window, stored in normalised screen coordinates (0..1).
/// Absolute pixel coordinates have to be rebuilt every time the resolution changes;
/// normalised rects survive it untouched.
/// </summary>
public sealed class LedZone
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

public sealed class LedLayout
{
    public int Version { get; set; } = 1;
    public List<LedZone> Zones { get; set; } = new();

    [JsonIgnore] public int Count => Zones.Count;

    public static string FilePath { get; } = Path.Combine(AppConfig.Directory, "layout.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Returns the layout ready to drive, with orientation applied. The file on disk
    /// always holds the unrotated geometry, so Reverse and Rotate can be changed in
    /// config.json and reloaded without regenerating anything.
    /// </summary>
    public static LedLayout Load(LayoutConfig config)
    {
        return LoadBase(config).WithOrientation(config.Reverse, config.Rotate);
    }

    private static LedLayout LoadBase(LayoutConfig config)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var layout = JsonSerializer.Deserialize<LedLayout>(File.ReadAllText(FilePath), JsonOpts);
                if (layout is { Zones.Count: > 0 })
                {
                    Log.Info($"Layout loaded: {layout.Count} zones from {FilePath}");
                    return layout;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Layout unreadable, regenerating", ex);
        }

        LedLayout? built = null;

        if (!string.Equals(config.Source, "generated", StringComparison.OrdinalIgnoreCase))
        {
            built = ImportFromPrismatik();
            if (built == null && string.Equals(config.Source, "prismatik", StringComparison.OrdinalIgnoreCase))
                Log.Warn("layout.source is 'prismatik' but no usable profile was found; generating instead");
        }

        built ??= Generate(config.BottomLeft, config.Left, config.Top, config.Right, config.BottomRight, config.Depth);
        built.Save();
        return built;
    }

    /// <summary>
    /// Reorders zones to match how the strip is physically wired. Reverse flips the
    /// direction it runs, Rotate moves which physical LED counts as the first one.
    /// Reverse is applied first.
    /// </summary>
    public LedLayout WithOrientation(bool reverse, int rotate)
    {
        if (Zones.Count == 0 || (!reverse && rotate % Math.Max(1, Zones.Count) == 0)) return this;

        var ordered = new List<LedZone>(Zones);
        if (reverse) ordered.Reverse();

        var n = ordered.Count;
        var shift = ((rotate % n) + n) % n;
        if (shift != 0)
            ordered = ordered.Skip(shift).Concat(ordered.Take(shift)).ToList();

        Log.Info($"Layout orientation applied: reverse={reverse}, rotate={rotate}");
        return new LedLayout { Version = Version, Zones = ordered };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            Log.Info($"Layout saved: {Count} zones -> {FilePath}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not save layout", ex);
        }
    }

    /// <summary>
    /// Builds a closed loop that starts on the bottom edge just left of centre and
    /// runs anticlockwise: left along the bottom, up the left side, right across the
    /// top, down the right side, then left along the bottom back to the seam.
    /// </summary>
    public static LedLayout Generate(int bottomLeft, int left, int top, int right, int bottomRight, double depth)
    {
        var layout = new LedLayout();
        var bottomTotal = bottomLeft + bottomRight;
        // The seam sits where the two bottom runs meet.
        var seam = bottomTotal == 0 ? 0.5 : (double)bottomLeft / bottomTotal;

        // Bottom, walking left from the seam to x = 0.
        for (var i = 0; i < bottomLeft; i++)
        {
            var w = seam / bottomLeft;
            layout.Zones.Add(new LedZone { X = seam - w * (i + 1), Y = 1 - depth, W = w, H = depth });
        }

        // Left edge, walking up.
        for (var i = 0; i < left; i++)
        {
            var h = 1.0 / left;
            layout.Zones.Add(new LedZone { X = 0, Y = 1 - h * (i + 1), W = depth, H = h });
        }

        // Top edge, walking right.
        for (var i = 0; i < top; i++)
        {
            var w = 1.0 / top;
            layout.Zones.Add(new LedZone { X = w * i, Y = 0, W = w, H = depth });
        }

        // Right edge, walking down.
        for (var i = 0; i < right; i++)
        {
            var h = 1.0 / right;
            layout.Zones.Add(new LedZone { X = 1 - depth, Y = h * i, W = depth, H = h });
        }

        // Bottom again, walking left from x = 1 back to the seam.
        for (var i = 0; i < bottomRight; i++)
        {
            var w = (1 - seam) / bottomRight;
            layout.Zones.Add(new LedZone { X = 1 - w * (i + 1), Y = 1 - depth, W = w, H = depth });
        }

        Log.Info($"Layout generated: {layout.Count} zones");
        return layout;
    }

    /// <summary>
    /// Imports a layout from another ambilight tool, picking the reader by what the
    /// file actually is rather than by extension alone.
    /// </summary>
    public static LedLayout? ImportFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Log.Warn($"Import failed: {path} does not exist");
                return null;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();

            if (extension == ".ini") return ImportPrismatikProfileFile(path);
            if (extension == ".json") return ImportFromHyperion(path);

            Log.Warn($"Import failed: {path} is neither a .json layout nor a .ini profile");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Import from {path} failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Reads a Hyperion, Hyperion.ng or HyperHDR layout. Their coordinates are already
    /// fractions of the screen, so this is a straight translation of min/max pairs into
    /// the rects used here.
    ///
    /// Two shapes exist in the wild and both are accepted: classic Hyperion nests the
    /// ranges under "hscan"/"vscan" with "minimum"/"maximum", while Hyperion.ng and
    /// HyperHDR flatten them to "hmin"/"hmax"/"vmin"/"vmax".
    ///
    /// Note that Hyperion.ng 2.x keeps its settings in a SQLite database rather than a
    /// JSON file, so this expects a config exported from its web UI.
    /// </summary>
    public static LedLayout? ImportFromHyperion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!TryFindLedsArray(document.RootElement, out var leds))
            {
                Log.Warn($"Import failed: no \"leds\" array found in {path}");
                return null;
            }

            var entries = new List<(int Index, LedZone Zone)>();
            var fallbackIndex = 0;

            foreach (var led in leds.EnumerateArray())
            {
                if (led.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadRanges(led, out var hMin, out var hMax, out var vMin, out var vMax)) continue;

                // Some exports write the pair the other way round.
                if (hMax < hMin) (hMin, hMax) = (hMax, hMin);
                if (vMax < vMin) (vMin, vMax) = (vMax, vMin);

                var index = led.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var parsed)
                    ? parsed
                    : fallbackIndex;
                fallbackIndex++;

                entries.Add((index, new LedZone
                {
                    X = Math.Clamp(hMin, 0, 1),
                    Y = Math.Clamp(vMin, 0, 1),
                    W = Math.Clamp(hMax - hMin, 0, 1),
                    H = Math.Clamp(vMax - vMin, 0, 1)
                }));
            }

            if (entries.Count == 0)
            {
                Log.Warn($"Import failed: \"leds\" in {path} held no usable entries");
                return null;
            }

            var layout = new LedLayout();
            foreach (var entry in entries.OrderBy(e => e.Index)) layout.Zones.Add(entry.Zone);

            Log.Info($"Imported {layout.Count} zones from Hyperion layout {path}");
            return layout;
        }
        catch (Exception ex)
        {
            Log.Error($"Hyperion import from {path} failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Finds the "leds" array, whether it sits at the root or inside a per-instance
    /// wrapper, and tolerates a file that is just the array on its own.
    /// </summary>
    private static bool TryFindLedsArray(JsonElement root, out JsonElement leds)
    {
        leds = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            leds = root;
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object) return false;

        if (root.TryGetProperty("leds", out var direct) && direct.ValueKind == JsonValueKind.Array)
        {
            leds = direct;
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindLedsArray(property.Value, out leds))
                return true;
        }

        return false;
    }

    private static bool TryReadRanges(JsonElement led, out double hMin, out double hMax, out double vMin, out double vMax)
    {
        hMin = hMax = vMin = vMax = 0;

        // Hyperion.ng and HyperHDR: flat hmin/hmax/vmin/vmax.
        if (TryReadDouble(led, "hmin", out hMin) && TryReadDouble(led, "hmax", out hMax) &&
            TryReadDouble(led, "vmin", out vMin) && TryReadDouble(led, "vmax", out vMax))
            return true;

        // Classic Hyperion: hscan/vscan objects with minimum/maximum.
        if (led.TryGetProperty("hscan", out var hscan) && led.TryGetProperty("vscan", out var vscan) &&
            TryReadDouble(hscan, "minimum", out hMin) && TryReadDouble(hscan, "maximum", out hMax) &&
            TryReadDouble(vscan, "minimum", out vMin) && TryReadDouble(vscan, "maximum", out vMax))
            return true;

        return false;
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetDouble(out value);
    }

    /// <summary>
    /// Imports a Prismatik profile chosen directly. The profile itself never records
    /// how many LEDs are wired up, so main.conf has to be found alongside it.
    /// </summary>
    private static LedLayout? ImportPrismatikProfileFile(string profilePath)
    {
        var profilesDir = Path.GetDirectoryName(profilePath);
        var root = profilesDir == null ? null : Path.GetDirectoryName(profilesDir);

        foreach (var candidate in new[]
                 {
                     root == null ? null : Path.Combine(root, "main.conf"),
                     profilesDir == null ? null : Path.Combine(profilesDir, "main.conf")
                 })
        {
            if (candidate != null && File.Exists(candidate))
                return ImportFromPrismatik(profilePath, candidate);
        }

        Log.Warn($"Import failed: no main.conf found next to {profilePath}, so the LED count is unknown");
        return null;
    }

    /// <summary>
    /// Reads an existing Prismatik profile and normalises it, so an already tuned
    /// layout does not have to be redrawn by hand.
    /// </summary>
    public static LedLayout? ImportFromPrismatik(string? profilePath = null, string? mainConfPath = null)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = Path.Combine(home, "Prismatik");
            mainConfPath ??= Path.Combine(root, "main.conf");
            if (!File.Exists(mainConfPath)) return null;

            var main = File.ReadAllText(mainConfPath);

            var device = CaptureGroup(main, @"^ConnectedDevice=(.+?)\s*$") ?? "Adalight";
            var section = Regex.Match(main, $@"\[{Regex.Escape(device)}\](.*?)(\n\[|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var ledCount = int.TryParse(CaptureGroup(section.Groups[1].Value, @"^NumberOfLeds=(\d+)\s*$"), out var n) ? n : 0;
            if (ledCount <= 0)
            {
                Log.Warn($"Could not read NumberOfLeds for device '{device}' from {mainConfPath}");
                return null;
            }

            if (profilePath == null)
            {
                var profile = CaptureGroup(main, @"^ProfileLast=(.+?)\s*$");
                if (string.IsNullOrWhiteSpace(profile)) return null;
                profilePath = Path.Combine(root, "Profiles", profile + ".ini");
            }
            if (!File.Exists(profilePath)) return null;

            // The .ini always carries 1500 LED_n sections regardless of how many are
            // wired up, so anything past NumberOfLeds is filler and must be dropped.
            var zones = new SortedDictionary<int, (int X, int Y, int W, int H)>();
            var index = -1;
            int px = 0, py = 0;

            foreach (var raw in File.ReadLines(profilePath))
            {
                var line = raw.Trim();

                var header = Regex.Match(line, @"^\[LED_(\d+)\]$");
                if (header.Success)
                {
                    index = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (index < 1 || index > ledCount) continue;

                var pos = Regex.Match(line, @"^Position=@Point\((-?\d+)\s+(-?\d+)\)$");
                if (pos.Success)
                {
                    px = int.Parse(pos.Groups[1].Value, CultureInfo.InvariantCulture);
                    py = int.Parse(pos.Groups[2].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                var size = Regex.Match(line, @"^Size=@Size\((-?\d+)\s+(-?\d+)\)$");
                if (size.Success)
                {
                    var w = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                    var h = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
                    zones[index] = (px, py, w, h);
                }
            }

            if (zones.Count < ledCount)
            {
                Log.Warn($"Prismatik profile only yielded {zones.Count} of {ledCount} zones, ignoring it");
                return null;
            }

            // Prismatik never records the screen size, so recover it from the extents
            // of the zones themselves.
            var screenW = zones.Values.Max(z => z.X + z.W);
            var screenH = zones.Values.Max(z => z.Y + z.H);
            if (screenW <= 0 || screenH <= 0) return null;

            var layout = new LedLayout();
            foreach (var pair in zones)
            {
                var z = pair.Value;
                layout.Zones.Add(new LedZone
                {
                    X = Math.Clamp((double)z.X / screenW, 0, 1),
                    Y = Math.Clamp((double)z.Y / screenH, 0, 1),
                    W = Math.Clamp((double)z.W / screenW, 0, 1),
                    H = Math.Clamp((double)z.H / screenH, 0, 1)
                });
            }

            Log.Info($"Imported {layout.Count} zones from Prismatik profile ({screenW}x{screenH} reference)");
            return layout;
        }
        catch (Exception ex)
        {
            Log.Error("Prismatik import failed", ex);
            return null;
        }
    }

    /// <summary>
    /// These files are CRLF, and in .NET a multiline "$" anchors before the "\n"
    /// only. That leaves the "\r" stranded, so a pattern ending in \d+$ never
    /// matches. Every pattern here therefore ends in \s*$ rather than $.
    /// </summary>
    private static string? CaptureGroup(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
