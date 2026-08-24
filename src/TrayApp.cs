using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Qlow;

/// <summary>
/// Tray-only shell. There is no main window on purpose: everything is a JSON file
/// plus a log, which is far easier to reason about when something misbehaves.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly Engine _engine;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _statusItem;
    private Icon? _connectedIcon;
    private Icon? _disconnectedIcon;

    /// <summary>
    /// Exists purely to own a window handle on the UI thread, so status updates
    /// raised on the capture or serial threads have somewhere to marshal to.
    ///
    /// A ContextMenuStrip cannot do this job: it creates its handle lazily, the first
    /// time the menu is shown. Until someone right-clicks the tray icon there is no
    /// handle, InvokeRequired reports false, and every update would run on a worker
    /// thread straight into NotifyIcon, which is not thread-safe.
    /// </summary>
    private readonly Control _marshaller;

    private string _lastTipText = "";
    private Icon? _lastIcon;

    public TrayApp(Engine engine)
    {
        _engine = engine;

        // Touching Handle forces creation now, on the thread that constructs TrayApp,
        // which is the UI thread. Everything below can then rely on it.
        _marshaller = new Control();
        _ = _marshaller.Handle;

        // The bundled artwork if it is there, otherwise something drawn on the fly so
        // the tray is never left without an icon.
        _connectedIcon = LoadEmbeddedIcon()
                         ?? BuildIcon(Color.FromArgb(90, 200, 255), Color.FromArgb(255, 140, 60));

        _disconnectedIcon = Desaturate(_connectedIcon)
                            ?? BuildIcon(Color.FromArgb(90, 90, 90), Color.FromArgb(120, 120, 120));

        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Backlight", null, (_, _) => ToggleEnabled())
        {
            CheckOnClick = true,
            Checked = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripMenuItem("Reconnect now", null, (_, _) => _engine.ForceReconnect()));

        var tests = new ToolStripMenuItem("Test patterns");
        tests.DropDownItems.Add(new ToolStripMenuItem("Chase - find LED 1 and direction", null,
            (_, _) => _engine.RunTest(TestPattern.Chase)));
        tests.DropDownItems.Add(new ToolStripMenuItem("Red, green, blue - check colour order", null,
            (_, _) => _engine.RunTest(TestPattern.ColorOrder)));
        tests.DropDownItems.Add(new ToolStripMenuItem("Sides - check per-side counts", null,
            (_, _) => _engine.RunTest(TestPattern.Sides)));
        menu.Items.Add(tests);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Edit config...", null, (_, _) => OpenPath(AppConfig.FilePath)));
        var import = new ToolStripMenuItem("Import layout");
        import.DropDownItems.Add(new ToolStripMenuItem("From Prismatik (auto-detect)", null,
            (_, _) => ReimportLayout()));
        import.DropDownItems.Add(new ToolStripMenuItem("From file...", null,
            (_, _) => ImportLayoutFromFile()));
        menu.Items.Add(import);
        menu.Items.Add(new ToolStripMenuItem("Reload config and layout", null, (_, _) => _engine.ReloadConfig()));
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenPath(Log.FilePath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Run at startup", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = true,
            Checked = Startup.IsEnabled()
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon
        {
            Icon = _disconnectedIcon,
            Text = "Qlow",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ToggleEnabled();

        _engine.StatusChanged += OnStatusChanged;
        RefreshUi();
    }

    /// <summary>
    /// Shuts down the same way the Exit menu item does, but callable from another
    /// thread. Without this the only ways to stop a tray-only app from outside are
    /// killing it, which skips the shutdown path entirely, including the black frame
    /// that stops the strip being left lit.
    /// </summary>
    public void RequestExit()
    {
        try
        {
            if (_marshaller.IsDisposed) return;
            _marshaller.BeginInvoke(ExitThread);
        }
        catch (ObjectDisposedException)
        {
            // Already going down.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ToggleEnabled()
    {
        _engine.SetEnabled(!_engine.Status.Enabled);
        RefreshUi();
    }

    private void ReimportLayout()
    {
        var imported = LedLayout.ImportFromPrismatik();
        if (imported == null)
        {
            MessageBox.Show(
                "No usable Prismatik profile was found.\n\nExpected it under your user folder in Prismatik\\Profiles.",
                "Qlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        imported.Save();
        _engine.ReloadConfig();
        MessageBox.Show($"Imported {imported.Count} zones.", "Qlow",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Imports from a file the user points at. Hyperion.ng 2.x keeps its settings in a
    /// database rather than a file on a predictable path, so asking is more reliable
    /// than guessing where to look.
    /// </summary>
    private void ImportLayoutFromFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import LED layout",
            Filter = "Supported layouts (*.json;*.ini)|*.json;*.ini|" +
                     "Hyperion / HyperHDR config (*.json)|*.json|" +
                     "Prismatik profile (*.ini)|*.ini|" +
                     "All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != DialogResult.OK) return;

        var imported = LedLayout.ImportFromFile(dialog.FileName);
        if (imported == null)
        {
            MessageBox.Show(
                $"Could not read a layout from:\n{dialog.FileName}\n\n" +
                "Expected a Hyperion, Hyperion.ng or HyperHDR config with a \"leds\" array, " +
                "or a Prismatik profile with main.conf alongside it.\n\nSee the log for details.",
                "Qlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        imported.Save();
        _engine.ReloadConfig();
        MessageBox.Show($"Imported {imported.Count} zones.", "Qlow",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ToggleStartup()
    {
        var enable = !Startup.IsEnabled();
        Startup.Set(enable);
        Log.Info($"Run at startup: {enable}");
    }

    private void OnStatusChanged()
    {
        try
        {
            if (_marshaller.IsDisposed) return;

            if (_marshaller.InvokeRequired)
            {
                _marshaller.BeginInvoke(RefreshUi);
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; the tray icon is already on its way out.
            return;
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call.
            return;
        }

        RefreshUi();
    }

    private void RefreshUi()
    {
        var s = _engine.Status;

        var state = !s.Enabled ? "off"
            : s.Paused ? "paused"
            : s.SerialConnected && s.CaptureReady ? $"{s.Fps:F0} fps"
            : s.SerialConnected ? "no capture"
            : "no device";

        _statusItem.Text = $"{s.LedCount} LEDs on {s.PortName ?? "-"} : {state}";
        _enabledItem.Checked = s.Enabled;

        // Only touch the icon and tooltip when they actually change. Each assignment
        // is a Shell_NotifyIcon round trip, and this runs every couple of seconds.
        var healthy = s.Enabled && !s.Paused && s.SerialConnected;
        var wanted = healthy ? _connectedIcon : _disconnectedIcon;
        if (!ReferenceEquals(wanted, _lastIcon))
        {
            _icon.Icon = wanted;
            _lastIcon = wanted;
        }

        // The tray tooltip is capped at 63 characters; anything longer is dropped.
        var tip = $"Qlow {(s.PortName ?? "disconnected")} {state}";
        if (tip.Length > 62) tip = tip[..62];
        if (tip != _lastTipText)
        {
            _icon.Text = tip;
            _lastTipText = tip;
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                if (!File.Exists(path)) File.WriteAllText(path, "");
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    /// <summary>
    /// Pulls the icon out of the assembly. Reading it from a resource rather than a
    /// file keeps it working inside the single-file publish, where there is no assets
    /// folder next to the exe.
    /// </summary>
    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var assembly = typeof(TrayApp).Assembly;

            // Matched by suffix rather than by full name, so renaming the project or
            // moving the file does not silently drop the icon.
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("qlow.ico", StringComparison.OrdinalIgnoreCase));

            if (resource == null)
            {
                Log.Warn("Embedded tray icon not found, falling back to a drawn one");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream == null) return null;

            // Asking for the shell's small icon size lets Windows pick the best frame
            // in the .ico for the current DPI.
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the embedded tray icon", ex);
            return null;
        }
    }

    /// <summary>
    /// A greyed, dimmed copy used while the device is missing. Worth keeping even with
    /// custom artwork: the whole point of the tray icon is telling you at a glance
    /// whether the strip is actually being driven.
    /// </summary>
    private static Icon? Desaturate(Icon? source)
    {
        if (source == null) return null;

        try
        {
            using var original = source.ToBitmap();
            using var grey = new Bitmap(original.Width, original.Height);

            for (var y = 0; y < original.Height; y++)
            {
                for (var x = 0; x < original.Width; x++)
                {
                    var c = original.GetPixel(x, y);

                    // Rec. 709 luma, lifted rather than dimmed. The artwork is mostly
                    // dark with thin bright strokes, so darkening a greyscale copy of
                    // it leaves an almost invisible smudge on a dark taskbar. Losing
                    // the colour is already signal enough that the strip is not live.
                    var luma = (int)Math.Clamp((0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) * 1.6, 0, 255);
                    grey.SetPixel(x, y, Color.FromArgb(c.A, luma, luma, luma));
                }
            }

            var handle = grey.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not build the greyed tray icon", ex);
            return null;
        }
    }

    /// <summary>Draws the tray icon so the build does not depend on a binary asset.</summary>
    private static Icon BuildIcon(Color inner, Color outer)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var glow = new SolidBrush(Color.FromArgb(70, outer));
            g.FillEllipse(glow, 1, 1, 30, 30);

            using var ring = new Pen(outer, 3f);
            g.DrawEllipse(ring, 4, 4, 24, 24);

            using var core = new SolidBrush(inner);
            g.FillEllipse(core, 11, 11, 10, 10);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unsubscribe first: a status update arriving mid-teardown would otherwise
            // try to marshal onto a handle that is about to go away.
            _engine.StatusChanged -= OnStatusChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _marshaller.Dispose();
            _connectedIcon?.Dispose();
            _disconnectedIcon?.Dispose();
            _connectedIcon = null;
            _disconnectedIcon = null;
            _lastIcon = null;
        }
        base.Dispose(disposing);
    }
}
