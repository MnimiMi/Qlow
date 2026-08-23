using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BaldLight;

/// <summary>
/// One LED sampling window, stored in normalised screen coordinates (0..1).
/// Prismatik stores absolute pixels, so a resolution change silently corrupts its
/// layout; normalised rects survive that.
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
    /// Prismatik writes CRLF, and in .NET a multiline "$" anchors before the "\n"
    /// only. That leaves the "\r" stranded, so a pattern ending in \d+$ never
    /// matches. Every pattern here therefore ends in \s*$ rather than $.
    /// </summary>
    private static string? CaptureGroup(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
