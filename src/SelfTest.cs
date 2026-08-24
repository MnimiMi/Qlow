using System.Diagnostics;
using System.Text;
using Qlow.Capture;
using Qlow.Output;
using Qlow.Processing;

namespace Qlow;

/// <summary>
/// Exercises capture, layout and colour processing without ever opening the
/// serial port, so it can be run while another ambilight app still owns the
/// device. Writes a report to disk because this is a windowed binary with no
/// console attached.
/// </summary>
public static class SelfTest
{
    public static string ReportPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Qlow", "selftest.txt");

    public static void Run()
    {
        var sb = new StringBuilder();
        void Say(string line)
        {
            sb.AppendLine(line);
            Log.Info("selftest: " + line);
        }

        Say($"Qlow self test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say("");

        var config = AppConfig.Load();
        var layout = LedLayout.Load(config.Layout);

        Say($"config      : {AppConfig.FilePath}");
        Say($"layout      : {LedLayout.FilePath}");
        Say($"led count   : {layout.Count}");

        if (layout.Count > 0)
        {
            var z = layout.Zones[0];
            var last = layout.Zones[^1];
            Say($"zone 1      : x={z.X:F4} y={z.Y:F4} w={z.W:F4} h={z.H:F4}");
            Say($"zone {layout.Count,-6}: x={last.X:F4} y={last.Y:F4} w={last.W:F4} h={last.H:F4}");
        }
        Say("");

        // Device presence only. Opening the port here would fight whatever is
        // already driving the strip.
        var usbPorts = SerialPortLocator.EnumerateUsbSerialPorts();
        Say($"usb serial  : {(usbPorts.Count == 0 ? "none found" : string.Join(", ", usbPorts))}");

        var port = SerialPortLocator.Locate(config.Serial.UsbIds, config.Serial.PortOverride);
        Say($"selected    : {port ?? "NOT FOUND"}");
        Say($"baud        : {config.Serial.BaudRate}");
        Say($"color order : {config.Serial.ColorOrder}");
        if (port != null)
        {
            var frameBytes = 6 + layout.Count * 3;
            var maxFps = config.Serial.BaudRate / 10.0 / frameBytes;
            Say($"frame size  : {frameBytes} bytes -> serial ceiling {maxFps:F1} fps");
        }
        Say("");

        using var duplicator = new DesktopDuplicator(config.Capture.MonitorIndex, config.Capture.DownscaleWidth);
        var sampler = new ZoneSampler(layout);
        var pipeline = new ColorPipeline(config.Color, config.Power, config.Serial.ColorOrder);

        var clock = Stopwatch.StartNew();
        var frames = 0;
        var nulls = 0;
        CapturedFrame? lastFrame = null;
        byte[]? lastBytes = null;

        while (clock.Elapsed.TotalSeconds < 3)
        {
            var frame = duplicator.TryGrab(50, out _);
            if (frame == null)
            {
                nulls++;
                Thread.Sleep(20);
                continue;
            }

            lastFrame = frame;
            var zones = sampler.Sample(frame);
            lastBytes = pipeline.Process(zones);
            frames++;
        }
        clock.Stop();

        Say($"capture     : {duplicator.Description}");
        Say($"frames      : {frames} in {clock.Elapsed.TotalSeconds:F1}s ({frames / clock.Elapsed.TotalSeconds:F1} fps), {nulls} rebuild waits");

        if (lastFrame != null)
        {
            Say($"frame size  : {lastFrame.Width}x{lastFrame.Height}, stride {lastFrame.Stride}");
        }
        else
        {
            Say("frame size  : NO FRAME CAPTURED");
        }

        if (lastBytes != null)
        {
            var lit = 0;
            for (var i = 0; i < layout.Count; i++)
            {
                var o = i * 3;
                if (lastBytes[o] + lastBytes[o + 1] + lastBytes[o + 2] > 0) lit++;
            }

            Say($"non-black   : {lit} of {layout.Count} zones");
            Say("");
            Say("sample LEDs (strip order, post-pipeline bytes):");
            foreach (var i in new[] { 0, 4, 20, 40, 60, 80, 100, layout.Count - 1 })
            {
                if (i < 0 || i >= layout.Count) continue;
                var o = i * 3;
                Say($"  led {i + 1,3} : {lastBytes[o],3} {lastBytes[o + 1],3} {lastBytes[o + 2],3}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"log: {Log.FilePath}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            File.WriteAllText(ReportPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("Could not write self test report", ex);
        }
    }
}
