using BaldLight.Capture;

namespace BaldLight.Processing;

/// <summary>
/// Turns averaged zone colours into the bytes the strip wants: vibrance, white
/// balance, temporal smoothing, gamma, brightness, a noise floor, and a current
/// limiter so a bright scene cannot brown out the supply.
///
/// Order matters. Smoothing runs before gamma so the easing is perceptually even,
/// and the current limiter runs last so its budget is computed on what actually
/// reaches the LEDs.
/// </summary>
public sealed class ColorPipeline
{
    private ColorConfig _color;
    private PowerConfig _power;
    private Rgb[] _state = Array.Empty<Rgb>();
    private byte[] _bytes = Array.Empty<byte>();
    private int[] _order = { 0, 1, 2 };

    private double[] _gammaTable = Array.Empty<double>();
    private double _gammaTableFor = double.NaN;

    public ColorPipeline(ColorConfig color, PowerConfig power, string colorOrder)
    {
        _color = color;
        _power = power;
        SetColorOrder(colorOrder);
    }

    public void Update(ColorConfig color, PowerConfig power, string colorOrder)
    {
        _color = color;
        _power = power;
        SetColorOrder(colorOrder);
    }

    /// <summary>Maps strip channel position to source channel index (0=R, 1=G, 2=B).</summary>
    private void SetColorOrder(string order)
    {
        var normalised = (order ?? "RGB").Trim().ToUpperInvariant();
        if (normalised.Length != 3) normalised = "RGB";

        var map = new int[3];
        for (var i = 0; i < 3; i++)
        {
            map[i] = normalised[i] switch { 'R' => 0, 'G' => 1, 'B' => 2, _ => i };
        }
        _order = map;
    }

    public void Reset()
    {
        Array.Clear(_state);
    }

    /// <summary>
    /// Writes one logical RGB triple straight into the strip's channel order,
    /// skipping gamma, smoothing and the rest. Used by the test patterns, where the
    /// whole point is to see the unprocessed value.
    /// </summary>
    public void WriteOrdered(byte[] dest, int ledIndex, byte r, byte g, byte b)
    {
        var src = new[] { r, g, b };
        var o = ledIndex * 3;
        if (o + 2 >= dest.Length) return;

        dest[o] = src[_order[0]];
        dest[o + 1] = src[_order[1]];
        dest[o + 2] = src[_order[2]];
    }

    public byte[] Process(Rgb[] zones)
    {
        if (_state.Length != zones.Length) _state = new Rgb[zones.Length];
        if (_bytes.Length != zones.Length * 3) _bytes = new byte[zones.Length * 3];

        EnsureGammaTable();

        var smoothing = Math.Clamp(_color.Smoothing, 0, 0.99);
        var alpha = 1.0 - smoothing;
        var vibrance = Math.Clamp(_color.Vibrance, 0, 100) / 100.0;
        var brightness = Math.Clamp(_color.Brightness, 0, 100) / 100.0;
        var (tr, tg, tb) = TemperatureScale(_color.TemperatureK);
        double minLum = Math.Clamp(_color.MinLuminance, 0, 255);

        // Pass one: shape and smooth, writing straight into the output bytes.
        for (var i = 0; i < zones.Length; i++)
        {
            var c = zones[i];

            // Vibrance: push each channel away from the zone average. Cheap, and it
            // matches what Prismatik calls OverBrighten closely enough to feel the same.
            if (vibrance > 0)
            {
                var mean = (c.R + c.G + c.B) / 3.0;
                c.R = mean + (c.R - mean) * (1 + vibrance);
                c.G = mean + (c.G - mean) * (1 + vibrance);
                c.B = mean + (c.B - mean) * (1 + vibrance);
            }

            c.R *= tr;
            c.G *= tg;
            c.B *= tb;

            c.R = Math.Clamp(c.R, 0, 255);
            c.G = Math.Clamp(c.G, 0, 255);
            c.B = Math.Clamp(c.B, 0, 255);

            // Rec. 709 luma. Below the floor the signal is sensor noise, not content.
            var luma = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            if (luma < minLum) c = default;

            var s = _state[i];
            s.R += (c.R - s.R) * alpha;
            s.G += (c.G - s.G) * alpha;
            s.B += (c.B - s.B) * alpha;
            _state[i] = s;

            var src = new[]
            {
                _gammaTable[(int)Math.Clamp(Math.Round(s.R), 0, 255)] * brightness,
                _gammaTable[(int)Math.Clamp(Math.Round(s.G), 0, 255)] * brightness,
                _gammaTable[(int)Math.Clamp(Math.Round(s.B), 0, 255)] * brightness
            };

            var o = i * 3;
            _bytes[o] = (byte)Math.Clamp(Math.Round(src[_order[0]]), 0, 255);
            _bytes[o + 1] = (byte)Math.Clamp(Math.Round(src[_order[1]]), 0, 255);
            _bytes[o + 2] = (byte)Math.Clamp(Math.Round(src[_order[2]]), 0, 255);
        }

        ApplyCurrentLimit();
        return _bytes;
    }

    /// <summary>
    /// Scales the whole frame down if the estimated draw exceeds the supply budget.
    /// Uniform scaling keeps hues intact; per-LED clipping would not.
    /// </summary>
    private void ApplyCurrentLimit()
    {
        var budgetMa = _power.PowerSupplyAmps * 1000.0;
        if (budgetMa <= 0) return;

        var perLedMa = Math.Max(1, _power.LedMilliAmps);
        long sum = 0;
        for (var i = 0; i < _bytes.Length; i++) sum += _bytes[i];

        // Full white on one LED is 765 counts and draws perLedMa.
        var estimateMa = sum / 765.0 * perLedMa;
        if (estimateMa <= budgetMa) return;

        var scale = budgetMa / estimateMa;
        for (var i = 0; i < _bytes.Length; i++) _bytes[i] = (byte)(_bytes[i] * scale);
    }

    private void EnsureGammaTable()
    {
        var gamma = Math.Clamp(_color.Gamma, 1.0, 4.0);
        if (_gammaTable.Length == 256 && Math.Abs(gamma - _gammaTableFor) < 0.0001) return;

        _gammaTable = new double[256];
        for (var i = 0; i < 256; i++) _gammaTable[i] = Math.Pow(i / 255.0, gamma) * 255.0;
        _gammaTableFor = gamma;
    }

    /// <summary>
    /// Tanner Helland's black-body approximation, normalised so 6500 K is a no-op.
    /// </summary>
    private static (double R, double G, double B) TemperatureScale(int kelvin)
    {
        if (kelvin <= 0) return (1, 1, 1);

        var t = Math.Clamp(kelvin, 1000, 40000) / 100.0;

        double r, g, b;

        if (t <= 66) r = 255;
        else r = Math.Clamp(329.698727446 * Math.Pow(t - 60, -0.1332047592), 0, 255);

        if (t <= 66) g = Math.Clamp(99.4708025861 * Math.Log(t) - 161.1195681661, 0, 255);
        else g = Math.Clamp(288.1221695283 * Math.Pow(t - 60, -0.0755148492), 0, 255);

        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = Math.Clamp(138.5177312231 * Math.Log(t - 10) - 305.0447927307, 0, 255);

        return (r / 255.0, g / 255.0, b / 255.0);
    }
}
