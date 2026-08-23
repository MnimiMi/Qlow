using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BaldLight;

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

    public TrayApp(Engine engine)
    {
        _engine = engine;

        _connectedIcon = BuildIcon(Color.FromArgb(90, 200, 255), Color.FromArgb(255, 140, 60));
        _disconnectedIcon = BuildIcon(Color.FromArgb(90, 90, 90), Color.FromArgb(120, 120, 120));

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
        menu.Items.Add(new ToolStripMenuItem("Re-import layout from Prismatik", null, (_, _) => ReimportLayout()));
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
            Text = "BaldLight",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ToggleEnabled();

        _engine.StatusChanged += OnStatusChanged;
        RefreshUi();
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
                "BaldLight", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        imported.Save();
        _engine.ReloadConfig();
        MessageBox.Show($"Imported {imported.Count} zones.", "BaldLight",
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
        if (_icon.ContextMenuStrip?.IsHandleCreated == true && _icon.ContextMenuStrip.InvokeRequired)
        {
            _icon.ContextMenuStrip.BeginInvoke(RefreshUi);
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

        var healthy = s.Enabled && !s.Paused && s.SerialConnected;
        _icon.Icon = healthy ? _connectedIcon : _disconnectedIcon;

        // The tray tooltip is capped at 63 characters; anything longer is dropped.
        var tip = $"BaldLight {(s.PortName ?? "disconnected")} {state}";
        _icon.Text = tip.Length > 62 ? tip[..62] : tip;
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
            _engine.StatusChanged -= OnStatusChanged;
            _icon.Visible = false;
            _icon.Dispose();
            _connectedIcon?.Dispose();
            _disconnectedIcon?.Dispose();
            _connectedIcon = null;
            _disconnectedIcon = null;
        }
        base.Dispose(disposing);
    }
}
