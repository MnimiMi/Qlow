using System.Text;
using Qlow;
using Xunit;

namespace Qlow.Tests;

public class LedLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qlow-tests-" + Guid.NewGuid().ToString("N"));

    public LedLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- geometry ---------------------------------------------------------

    [Fact]
    public void GeneratesOneZonePerLed()
    {
        var layout = LedLayout.Generate(10, 22, 38, 22, 28, 0.15);
        Assert.Equal(120, layout.Count);
    }

    [Fact]
    public void KeepsEveryZoneInsideTheScreen()
    {
        var layout = LedLayout.Generate(10, 22, 38, 22, 28, 0.15);

        Assert.All(layout.Zones, z =>
        {
            Assert.InRange(z.X, 0, 1);
            Assert.InRange(z.Y, 0, 1);
            Assert.InRange(z.X + z.W, 0, 1.0001);
            Assert.InRange(z.Y + z.H, 0, 1.0001);
        });
    }

    [Fact]
    public void ClosesTheLoopWhereItStarted()
    {
        // The first and last LED sit either side of the seam on the bottom edge,
        // so they must touch. A gap here means the strip is mapped wrong.
        var layout = LedLayout.Generate(10, 22, 38, 22, 28, 0.15);

        var first = layout.Zones[0];
        var last = layout.Zones[^1];

        Assert.Equal(first.X + first.W, last.X, 4);
    }

    [Fact]
    public void ReverseFlipsTheOrderWithoutMovingAnything()
    {
        var layout = LedLayout.Generate(2, 2, 2, 2, 2, 0.15);
        var flipped = layout.WithOrientation(reverse: true, rotate: 0);

        Assert.Equal(layout.Count, flipped.Count);
        Assert.Equal(layout.Zones[0].X, flipped.Zones[^1].X, 6);
        Assert.Equal(layout.Zones[^1].X, flipped.Zones[0].X, 6);
    }

    [Fact]
    public void RotateMovesTheStartingPoint()
    {
        var layout = LedLayout.Generate(2, 2, 2, 2, 2, 0.15);
        var rotated = layout.WithOrientation(reverse: false, rotate: 3);

        Assert.Equal(layout.Zones[3].X, rotated.Zones[0].X, 6);
    }

    [Fact]
    public void RotateWrapsAroundRatherThanRunningOffTheEnd()
    {
        var layout = LedLayout.Generate(2, 2, 2, 2, 2, 0.15);

        var forward = layout.WithOrientation(false, layout.Count + 2);
        var backward = layout.WithOrientation(false, -1);

        Assert.Equal(layout.Zones[2].X, forward.Zones[0].X, 6);
        Assert.Equal(layout.Zones[^1].X, backward.Zones[0].X, 6);
    }

    // ---- importing from other software ------------------------------------

    /// <summary>
    /// The regression that started this test project. Prismatik writes CRLF, and
    /// in .NET a multiline "$" anchors before the "\n" only, leaving the "\r"
    /// between it and the digits — so a pattern ending in \d+$ never matched.
    /// The import returned "could not read this" and silently generated a default
    /// layout instead, with nothing in the log to say why.
    /// </summary>
    [Fact]
    public void ReadsAPrismatikProfileWrittenWithWindowsLineEndings()
    {
        var profile = WritePrismatikProfile(ledCount: 4, crlf: true);

        var layout = LedLayout.ImportFromFile(profile);

        Assert.NotNull(layout);
        Assert.Equal(4, layout!.Count);
    }

    [Fact]
    public void ReadsAPrismatikProfileWithUnixLineEndingsToo()
    {
        var profile = WritePrismatikProfile(ledCount: 4, crlf: false);

        var layout = LedLayout.ImportFromFile(profile);

        Assert.NotNull(layout);
        Assert.Equal(4, layout!.Count);
    }

    [Fact]
    public void NormalisesPrismatikPixelsAgainstTheScreenItWasDrawnFor()
    {
        var profile = WritePrismatikProfile(ledCount: 4, crlf: true);

        var layout = LedLayout.ImportFromFile(profile)!;

        // The fixture puts LED 1 at x=0 with width 100, and the widest extent is
        // 400, so the first zone must start at 0 and be a quarter of the screen.
        Assert.Equal(0.0, layout.Zones[0].X, 4);
        Assert.Equal(0.25, layout.Zones[0].W, 4);
    }

    [Fact]
    public void IgnoresTheFillerEntriesPastTheConfiguredLedCount()
    {
        // The .ini always carries far more LED sections than are wired up.
        var profile = WritePrismatikProfile(ledCount: 2, crlf: true, extraSections: 20);

        var layout = LedLayout.ImportFromFile(profile)!;

        Assert.Equal(2, layout.Count);
    }

    [Fact]
    public void ReadsTheFlatHyperionShape()
    {
        var path = Path.Combine(_dir, "hyperion.json");
        File.WriteAllText(path, """
        {
          "leds": [
            { "hmin": 0.0, "hmax": 0.25, "vmin": 0.8, "vmax": 1.0 },
            { "hmin": 0.5, "hmax": 0.75, "vmin": 0.0, "vmax": 0.2 }
          ]
        }
        """);

        var layout = LedLayout.ImportFromFile(path)!;

        Assert.Equal(2, layout.Count);
        Assert.Equal(0.0, layout.Zones[0].X, 4);
        Assert.Equal(0.25, layout.Zones[0].W, 4);
        Assert.Equal(0.2, layout.Zones[0].H, 4);
    }

    [Fact]
    public void ReadsTheNestedClassicHyperionShapeAndHonoursTheIndex()
    {
        // Deliberately out of order: the index decides the strip order, not the
        // position in the file.
        var path = Path.Combine(_dir, "classic.json");
        File.WriteAllText(path, """
        {
          "leds": [
            { "index": 1, "hscan": { "minimum": 0.5, "maximum": 0.75 }, "vscan": { "minimum": 0.0, "maximum": 0.2 } },
            { "index": 0, "hscan": { "minimum": 0.0, "maximum": 0.25 }, "vscan": { "minimum": 0.8, "maximum": 1.0 } }
          ]
        }
        """);

        var layout = LedLayout.ImportFromFile(path)!;

        Assert.Equal(2, layout.Count);
        Assert.Equal(0.0, layout.Zones[0].X, 4);   // index 0 comes first
        Assert.Equal(0.5, layout.Zones[1].X, 4);
    }

    [Fact]
    public void RefusesAFileWithNothingUsableInIt()
    {
        var path = Path.Combine(_dir, "empty.json");
        File.WriteAllText(path, """{ "general": { "name": "nothing here" } }""");

        Assert.Null(LedLayout.ImportFromFile(path));
    }

    [Fact]
    public void RefusesAFileThatIsNotThere()
    {
        Assert.Null(LedLayout.ImportFromFile(Path.Combine(_dir, "absent.json")));
    }

    // ---- fixtures ---------------------------------------------------------

    /// <summary>
    /// Builds a miniature Prismatik install: main.conf naming the device and LED
    /// count, and a profile of zones in absolute pixels.
    /// </summary>
    private string WritePrismatikProfile(int ledCount, bool crlf, int extraSections = 0)
    {
        var nl = crlf ? "\r\n" : "\n";

        var root = Path.Combine(_dir, "Prismatik");
        var profiles = Path.Combine(root, "Profiles");
        Directory.CreateDirectory(profiles);

        var main = new StringBuilder();
        main.Append("[General]").Append(nl);
        main.Append("ProfileLast=Test").Append(nl);
        main.Append("ConnectedDevice=Adalight").Append(nl);
        main.Append(nl);
        main.Append("[Adalight]").Append(nl);
        main.Append("SerialPort=COM6").Append(nl);
        main.Append($"NumberOfLeds={ledCount}").Append(nl);
        File.WriteAllText(Path.Combine(root, "main.conf"), main.ToString());

        var profile = new StringBuilder();
        profile.Append("[General]").Append(nl);
        profile.Append("LightpackMode=Ambilight").Append(nl);
        for (var i = 1; i <= ledCount + extraSections; i++)
        {
            profile.Append($"[LED_{i}]").Append(nl);
            profile.Append($"Position=@Point({(i - 1) * 100} 0)").Append(nl);
            profile.Append("Size=@Size(100 100)").Append(nl);
            profile.Append("IsEnabled=true").Append(nl);
        }

        var path = Path.Combine(profiles, "Test.ini");
        File.WriteAllText(path, profile.ToString());
        return path;
    }
}
