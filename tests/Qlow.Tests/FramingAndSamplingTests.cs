using Qlow;
using Qlow.Capture;
using Qlow.Output;
using Xunit;

namespace Qlow.Tests;

public class AdalightFramingTests
{
    [Fact]
    public void StartsEveryFrameWithTheMagicWord()
    {
        var header = new byte[AdalightLink.HeaderLength];
        AdalightLink.WriteHeader(header, 120);

        Assert.Equal((byte)'A', header[0]);
        Assert.Equal((byte)'d', header[1]);
        Assert.Equal((byte)'a', header[2]);
    }

    [Theory]
    [InlineData(1, 0x00, 0x00)]
    [InlineData(120, 0x00, 0x77)]   // 119 = 0x0077
    [InlineData(256, 0x00, 0xFF)]   // 255
    [InlineData(300, 0x01, 0x2B)]   // 299 = 0x012B
    public void CarriesTheLedCountMinusOneAsBigEndian(int ledCount, byte hi, byte lo)
    {
        var header = new byte[AdalightLink.HeaderLength];
        AdalightLink.WriteHeader(header, ledCount);

        Assert.Equal(hi, header[3]);
        Assert.Equal(lo, header[4]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(300)]
    public void ChecksumAgreesWithWhatTheFirmwareRecomputes(int ledCount)
    {
        var header = new byte[AdalightLink.HeaderLength];
        AdalightLink.WriteHeader(header, ledCount);

        // The sketch accepts a header only if this holds, which is how it finds
        // the start of a frame again after a truncated write.
        Assert.Equal((byte)(header[3] ^ header[4] ^ 0x55), header[5]);
    }

    [Fact]
    public void RejectsABufferTooSmallToHoldTheHeader()
    {
        Assert.Throws<ArgumentException>(() => AdalightLink.WriteHeader(new byte[5], 120));
    }
}

public class ZoneSamplerTests
{
    /// <summary>A frame of one flat colour, laid out the way DXGI hands it over: B, G, R, A.</summary>
    private static CapturedFrame Flat(int width, int height, byte r, byte g, byte b)
    {
        var stride = width * 4;
        var frame = new CapturedFrame
        {
            Width = width,
            Height = height,
            Stride = stride,
            Pixels = new byte[stride * height]
        };

        for (var i = 0; i < frame.Pixels.Length; i += 4)
        {
            frame.Pixels[i] = b;
            frame.Pixels[i + 1] = g;
            frame.Pixels[i + 2] = r;
            frame.Pixels[i + 3] = 255;
        }

        return frame;
    }

    [Fact]
    public void ReadsChannelsInTheOrderTheGpuProvidesThem()
    {
        // Guards the one assumption that silently turns the sky red if it is wrong.
        var layout = new LedLayout { Zones = { new LedZone { X = 0, Y = 0, W = 1, H = 1 } } };
        var sampler = new ZoneSampler(layout);

        var zones = sampler.Sample(Flat(8, 8, r: 10, g: 120, b: 250));

        Assert.Equal(10, zones[0].R, 1);
        Assert.Equal(120, zones[0].G, 1);
        Assert.Equal(250, zones[0].B, 1);
    }

    [Fact]
    public void AveragesOnlyTheHalfOfTheFrameTheZoneCovers()
    {
        var width = 8;
        var height = 4;
        var frame = Flat(width, height, 0, 0, 0);

        // Paint the left half white, leave the right half black.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width / 2; x++)
            {
                var p = y * frame.Stride + x * 4;
                frame.Pixels[p] = frame.Pixels[p + 1] = frame.Pixels[p + 2] = 255;
            }
        }

        var layout = new LedLayout
        {
            Zones =
            {
                new LedZone { X = 0,   Y = 0, W = 0.5, H = 1 },
                new LedZone { X = 0.5, Y = 0, W = 0.5, H = 1 }
            }
        };

        var zones = new ZoneSampler(layout).Sample(frame);

        Assert.Equal(255, zones[0].R, 1);
        Assert.Equal(0, zones[1].R, 1);
    }

    [Fact]
    public void SurvivesAZoneSittingRightOnTheEdge()
    {
        // Rounding a zone that ends at exactly 1.0 must not read past the buffer.
        var layout = new LedLayout
        {
            Zones = { new LedZone { X = 0.9, Y = 0.9, W = 0.1, H = 0.1 } }
        };

        var zones = new ZoneSampler(layout).Sample(Flat(16, 16, 40, 40, 40));

        Assert.Equal(40, zones[0].R, 1);
    }
}
