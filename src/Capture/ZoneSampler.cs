namespace Qlow.Capture;

/// <summary>Linear RGB triplet in 0..255, kept as doubles so smoothing does not quantise.</summary>
public struct Rgb
{
    public double R;
    public double G;
    public double B;
}

/// <summary>
/// Averages each layout zone over the downscaled frame. The GPU mip chain has
/// already done the heavy averaging, so this is a cheap box filter on a few
/// hundred pixels per zone at most.
/// </summary>
public sealed class ZoneSampler
{
    private LedLayout _layout;
    private Rgb[] _output;

    public ZoneSampler(LedLayout layout)
    {
        _layout = layout;
        _output = new Rgb[layout.Count];
    }

    public int Count => _layout.Count;

    public void SetLayout(LedLayout layout)
    {
        _layout = layout;
        _output = new Rgb[layout.Count];
    }

    public Rgb[] Sample(CapturedFrame frame)
    {
        var zones = _layout.Zones;

        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];

            var x0 = (int)Math.Round(z.X * frame.Width);
            var y0 = (int)Math.Round(z.Y * frame.Height);
            var x1 = (int)Math.Round((z.X + z.W) * frame.Width);
            var y1 = (int)Math.Round((z.Y + z.H) * frame.Height);

            x0 = Math.Clamp(x0, 0, frame.Width - 1);
            y0 = Math.Clamp(y0, 0, frame.Height - 1);
            x1 = Math.Clamp(x1, x0 + 1, frame.Width);
            y1 = Math.Clamp(y1, y0 + 1, frame.Height);

            long sumR = 0, sumG = 0, sumB = 0;
            var count = 0;

            for (var y = y0; y < y1; y++)
            {
                var row = y * frame.Stride;
                for (var x = x0; x < x1; x++)
                {
                    var p = row + x * 4; // BGRA
                    sumB += frame.Pixels[p];
                    sumG += frame.Pixels[p + 1];
                    sumR += frame.Pixels[p + 2];
                    count++;
                }
            }

            if (count == 0)
            {
                _output[i] = default;
                continue;
            }

            _output[i] = new Rgb
            {
                R = (double)sumR / count,
                G = (double)sumG / count,
                B = (double)sumB / count
            };
        }

        return _output;
    }
}
