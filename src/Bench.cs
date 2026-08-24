using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Qlow.Capture;
using Qlow.Processing;

namespace Qlow;

/// <summary>
/// Measures where the per-frame budget actually goes, at each level of the GPU mip
/// chain. Two things are timed separately because they scale differently: the
/// readback, which is dominated by how many bytes cross the bus, and the CPU work,
/// which is dominated by how many pixels each zone has to average.
///
/// An animated window is kept on screen throughout. Without it the desktop is
/// static, AcquireNextFrame simply times out, and the benchmark would measure a
/// code path that never does any work.
/// </summary>
public static class Bench
{
    public static string ReportPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Qlow", "bench.txt");

    private static readonly StringBuilder Report = new();

    public static void Run()
    {
        using var animator = BuildAnimator();

        var worker = new Thread(() =>
        {
            try
            {
                Measure();
            }
            catch (Exception ex)
            {
                Say($"benchmark failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Flush();
                try { animator.BeginInvoke(Application.ExitThread); } catch { }
            }
        })
        { IsBackground = true, Name = "Qlow.Bench" };

        animator.Shown += (_, _) => worker.Start();
        Application.Run(animator);
    }

    private static void Measure()
    {
        var config = AppConfig.Load();
        var layout = LedLayout.Load(config.Layout);

        Say($"Qlow benchmark, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"{layout.Count} LEDs, {Environment.ProcessorCount} logical cores");
        Say("");

        // ---- Capture and readback -------------------------------------------------
        Say("CAPTURE: grab, GPU mip chain, readback to system memory");
        Say("");
        Say("  requested   mip     readback     per frame    per frame   ceiling");
        Say("      width  size        bytes       mean ms      p95 ms       fps");
        Say("  --------------------------------------------------------------------");

        foreach (var width in new[] { 1920, 1280, 640, 320, 160 })
        {
            var (mip, bytes, mean, p95) = MeasureCapture(width);
            if (mean <= 0)
            {
                Say($"  {width,9}   {"failed",-12}");
                continue;
            }
            Say($"  {width,9}  {mip,-11}  {bytes,10:N0}  {mean,10:F2}  {p95,10:F2}  {1000 / mean,8:F0}");
        }

        Say("");

        // ---- Zone averaging and colour pipeline ------------------------------------
        Say("CPU: zone averaging and colour pipeline, per frame");
        Say("");
        Say("      frame  px per     sample     pipeline      total   share of");
        Say("       size     zone      ms         ms            ms     33ms budget");
        Say("  --------------------------------------------------------------------");

        foreach (var (w, h) in new[] { (2560, 1440), (1280, 720), (640, 360), (320, 180), (160, 90) })
        {
            var (perZone, sample, pipeline) = MeasureCpu(layout, config, w, h);
            var total = sample + pipeline;
            Say($"  {w + "x" + h,11}  {perZone,6:N0}  {sample,9:F3}  {pipeline,11:F3}  {total,9:F3}  {total / 33.3 * 100,8:F1}%");
        }

        Say("");
        Say("Notes:");
        Say("  - The GPU work before the readback is the same at every level: one full-frame");
        Say("    copy plus GenerateMips. Only the bytes pulled back to the CPU change.");
        Say("  - A new pixel buffer is allocated per frame, so the readback column is also");
        Say("    the garbage rate per frame.");
        Say($"  - At {config.Serial.BaudRate} baud a {6 + layout.Count * 3} byte frame caps the link at " +
            $"{config.Serial.BaudRate / 10.0 / (6 + layout.Count * 3):F1} fps.");
    }

    private static (string Mip, long Bytes, double Mean, double P95) MeasureCapture(int desiredWidth)
    {
        using var duplicator = new DesktopDuplicator(0, desiredWidth);

        // Warm up: build the device, textures and mip chain before anything is timed.
        var warmupDeadline = Stopwatch.StartNew();
        while (duplicator.FramesDownscaled < 10 && warmupDeadline.Elapsed.TotalSeconds < 5)
            duplicator.TryGrab(100, out _);

        if (duplicator.FramesDownscaled < 10) return ("", 0, 0, 0);

        var samples = new List<double>(300);
        var clock = new Stopwatch();
        var budget = Stopwatch.StartNew();
        long bytes = 0;

        while (samples.Count < 250 && budget.Elapsed.TotalSeconds < 8)
        {
            var before = duplicator.FramesDownscaled;

            clock.Restart();
            var frame = duplicator.TryGrab(100, out _);
            clock.Stop();

            // Only count calls that genuinely did the work; a timeout returns the
            // previous frame and would drag the average towards the timeout value.
            if (duplicator.FramesDownscaled == before || frame == null) continue;

            bytes = frame.Pixels.LongLength;
            samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        if (samples.Count == 0) return ("", 0, 0, 0);

        samples.Sort();
        var mean = samples.Average();
        var p95 = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))];

        return ($"{duplicator.MipWidth}x{duplicator.MipHeight}", bytes, mean, p95);
    }

    private static (int PerZone, double Sample, double Pipeline) MeasureCpu(
        LedLayout layout, AppConfig config, int width, int height)
    {
        var frame = new CapturedFrame
        {
            Width = width,
            Height = height,
            Stride = width * 4,
            Pixels = new byte[width * 4 * height]
        };

        // Fill with something non-uniform so nothing can be short-circuited.
        var random = new Random(1234);
        random.NextBytes(frame.Pixels);

        var sampler = new ZoneSampler(layout);
        var pipeline = new ColorPipeline(config.Color, config.Power, config.Serial.ColorOrder);

        for (var i = 0; i < 20; i++) pipeline.Process(sampler.Sample(frame));

        const int iterations = 100;

        var clock = Stopwatch.StartNew();
        Rgb[] zones = Array.Empty<Rgb>();
        for (var i = 0; i < iterations; i++) zones = sampler.Sample(frame);
        clock.Stop();
        var sampleMs = clock.Elapsed.TotalMilliseconds / iterations;

        clock.Restart();
        for (var i = 0; i < iterations; i++) pipeline.Process(zones);
        clock.Stop();
        var pipelineMs = clock.Elapsed.TotalMilliseconds / iterations;

        var perZone = layout.Count == 0
            ? 0
            : (int)Math.Round(layout.Zones.Average(z => z.W * width * z.H * height));

        return (perZone, sampleMs, pipelineMs);
    }

    /// <summary>A small window that repaints constantly, so the desktop keeps changing.</summary>
    private static Form BuildAnimator()
    {
        var form = new Form
        {
            Text = "Qlow benchmark",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(60, 60, 520, 220),
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = Color.Black
        };

        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Text = "Benchmark running, please leave this window visible..."
        };
        form.Controls.Add(label);

        var tick = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 1 };
        timer.Tick += (_, _) =>
        {
            tick++;
            form.BackColor = Color.FromArgb((tick * 7) % 256, (tick * 13) % 256, (tick * 29) % 256);
        };
        timer.Start();

        form.FormClosed += (_, _) => timer.Dispose();
        return form;
    }

    private static void Say(string line)
    {
        Report.AppendLine(line);
        Log.Info("bench: " + line);
    }

    private static void Flush()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            File.WriteAllText(ReportPath, Report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error("Could not write benchmark report", ex);
        }
    }
}
