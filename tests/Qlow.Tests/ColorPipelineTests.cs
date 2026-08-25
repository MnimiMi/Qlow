using Qlow;
using Qlow.Capture;
using Qlow.Processing;
using Xunit;

namespace Qlow.Tests;

/// <summary>
/// The colour pipeline is pure arithmetic with no system underneath it, which
/// makes it the part of Qlow most worth pinning down: a wrong number here is
/// invisible in code review and shows up as "the colours look a bit off".
/// </summary>
public class ColorPipelineTests
{
    /// <summary>A pipeline with every optional stage switched off, so one thing is tested at a time.</summary>
    private static ColorPipeline Plain(string order = "RGB", Action<ColorConfig>? tweak = null)
    {
        var colour = new ColorConfig
        {
            Gamma = 1.0,          // identity, so input maps straight to output
            Brightness = 100,
            Vibrance = 0,
            Smoothing = 0,        // no blending with the previous frame
            MinBrightness = 0,
            MinLuminance = 0,
            TemperatureK = 0
        };
        tweak?.Invoke(colour);
        return new ColorPipeline(colour, new PowerConfig(), order);
    }

    private static Rgb[] One(double r, double g, double b) => new[] { new Rgb { R = r, G = g, B = b } };

    [Fact]
    public void PassesAColourThroughUntouchedWhenEveryStageIsOff()
    {
        var bytes = Plain().Process(One(10, 120, 250));

        Assert.Equal(10, bytes[0]);
        Assert.Equal(120, bytes[1]);
        Assert.Equal(250, bytes[2]);
    }

    [Theory]
    [InlineData("RGB", 10, 120, 250)]
    [InlineData("GRB", 120, 10, 250)]
    [InlineData("BGR", 250, 120, 10)]
    [InlineData("BRG", 250, 10, 120)]
    public void ReordersChannelsToMatchTheStrip(string order, byte first, byte second, byte third)
    {
        var bytes = Plain(order).Process(One(10, 120, 250));

        Assert.Equal(first, bytes[0]);
        Assert.Equal(second, bytes[1]);
        Assert.Equal(third, bytes[2]);
    }

    [Fact]
    public void ForcesNearBlackToBlackSoDarkScenesDoNotShimmer()
    {
        var bytes = Plain(tweak: c => c.MinLuminance = 10).Process(One(3, 3, 3));

        Assert.Equal(new byte[] { 0, 0, 0 }, bytes);
    }

    [Fact]
    public void LeavesAnythingAboveTheNoiseFloorAlone()
    {
        var bytes = Plain(tweak: c => c.MinLuminance = 10).Process(One(40, 40, 40));

        Assert.Equal(40, bytes[0]);
    }

    [Fact]
    public void LiftsABlackZoneToExactlyTheFloor()
    {
        // 10 % of 255 is 25.5, and white means all three channels carry it equally.
        var bytes = Plain(tweak: c =>
        {
            c.MinBrightness = 10;
            c.DarkColor = "#FFFFFF";
        }).Process(One(0, 0, 0));

        Assert.Equal(26, bytes[0]);
        Assert.Equal(26, bytes[1]);
        Assert.Equal(26, bytes[2]);
    }

    [Fact]
    public void NormalisesAColouredFloorToTheRequestedBrightness()
    {
        // #FF8000 has a luma of 145.76, so scaling it to a luma of 25.5 gives
        // 45, 22, 0. This is the case that was checked by hand against the strip.
        var bytes = Plain(tweak: c =>
        {
            c.MinBrightness = 10;
            c.DarkColor = "#FF8000";
        }).Process(One(0, 0, 0));

        Assert.Equal(45, bytes[0]);
        Assert.Equal(22, bytes[1]);
        Assert.Equal(0, bytes[2]);
    }

    [Fact]
    public void LeavesTheFloorOffByDefault()
    {
        Assert.Equal(new byte[] { 0, 0, 0 }, Plain().Process(One(0, 0, 0)));
    }

    [Fact]
    public void ScalesTheWholeFrameWhenItWouldExceedTheSupply()
    {
        // Ten LEDs at full white draw 10 x 50 mA = 500 mA. A 250 mA budget must
        // halve them, not clip some and leave others.
        var colour = new ColorConfig
        {
            Gamma = 1.0, Brightness = 100, Vibrance = 0,
            Smoothing = 0, MinBrightness = 0, MinLuminance = 0, TemperatureK = 0
        };
        var power = new PowerConfig { LedMilliAmps = 50, PowerSupplyAmps = 0.25 };
        var pipeline = new ColorPipeline(colour, power, "RGB");

        var zones = new Rgb[10];
        for (var i = 0; i < zones.Length; i++) zones[i] = new Rgb { R = 255, G = 255, B = 255 };

        var bytes = pipeline.Process(zones);

        Assert.All(bytes, b => Assert.InRange(b, 120, 132));
    }

    [Fact]
    public void KeepsHueIntactWhileLimitingCurrent()
    {
        var colour = new ColorConfig
        {
            Gamma = 1.0, Brightness = 100, Vibrance = 0,
            Smoothing = 0, MinBrightness = 0, MinLuminance = 0, TemperatureK = 0
        };
        var power = new PowerConfig { LedMilliAmps = 50, PowerSupplyAmps = 0.05 };
        var pipeline = new ColorPipeline(colour, power, "RGB");

        var zones = new Rgb[10];
        for (var i = 0; i < zones.Length; i++) zones[i] = new Rgb { R = 200, G = 100, B = 50 };

        var bytes = pipeline.Process(zones);

        // Clipping per LED would flatten these ratios; uniform scaling preserves them.
        Assert.True(bytes[0] > bytes[1] && bytes[1] > bytes[2]);
        Assert.InRange(bytes[0] / (double)bytes[1], 1.8, 2.2);
    }

    [Fact]
    public void MovesOnlyPartWayTowardsTheTargetWhenSmoothing()
    {
        var pipeline = Plain(tweak: c => c.Smoothing = 0.5);

        var first = pipeline.Process(One(200, 200, 200))[0];
        var second = pipeline.Process(One(200, 200, 200))[0];

        Assert.InRange(first, 90, 110);      // halfway from black
        Assert.True(second > first);          // and closer still on the next frame
        Assert.True(second < 200);
    }

    [Fact]
    public void NeverMovesFurtherThanTheStepAllowsInOneFrame()
    {
        var pipeline = Plain(tweak: c => c.MaxChangePerFrame = 12);

        // First frame under the limit is adopted whole; the cap starts after it.
        pipeline.Process(One(0, 0, 0));

        var second = pipeline.Process(One(255, 255, 255))[0];
        var third = pipeline.Process(One(255, 255, 255))[0];

        Assert.Equal(12, second);
        Assert.Equal(24, third);
    }

    [Fact]
    public void LimitsFallingJustAsMuchAsRising()
    {
        var pipeline = Plain(tweak: c => c.MaxChangePerFrame = 12);

        pipeline.Process(One(255, 255, 255));
        var next = pipeline.Process(One(0, 0, 0))[0];

        Assert.Equal(243, next);
    }

    [Fact]
    public void LetsSmallChangesThroughUntouched()
    {
        var pipeline = Plain(tweak: c => c.MaxChangePerFrame = 12);

        pipeline.Process(One(100, 100, 100));
        var next = pipeline.Process(One(105, 100, 100))[0];

        Assert.Equal(105, next);
    }

    [Fact]
    public void JumpsFreelyWhenTheStepLimitIsOff()
    {
        var pipeline = Plain();

        pipeline.Process(One(0, 0, 0));
        Assert.Equal(255, pipeline.Process(One(255, 255, 255))[0]);
    }

    [Fact]
    public void FallsBackToWhiteWhenTheFloorColourIsNotAHexValue()
    {
        var bytes = Plain(tweak: c =>
        {
            c.MinBrightness = 10;
            c.DarkColor = "not a colour";
        }).Process(One(0, 0, 0));

        Assert.Equal(bytes[0], bytes[1]);
        Assert.Equal(bytes[1], bytes[2]);
        Assert.True(bytes[0] > 0);
    }
}
