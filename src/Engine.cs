using System.Diagnostics;
using Qlow.Capture;
using Qlow.Output;
using Qlow.Processing;
using Microsoft.Win32;

namespace Qlow;

public enum TestPattern
{
    /// <summary>A single dot walks the strip: reveals LED 1 and the direction.</summary>
    Chase,
    /// <summary>Whole strip red, green, blue in turn: reveals a wrong channel order.</summary>
    ColorOrder,
    /// <summary>Each configured side run in its own colour: reveals wrong per-side counts.</summary>
    Sides
}

public sealed class EngineStatus
{
    public bool Enabled;
    public bool Paused;
    public bool CaptureReady;
    public bool SerialConnected;
    public string? PortName;
    public double Fps;
    public int LedCount;
    public string CaptureDescription = "";
}

/// <summary>
/// Ties capture, colour processing and serial output together, and owns the
/// recovery policy: a stalled capture is rebuilt, a resumed machine reconnects,
/// and neither failure is ever allowed to end the loop.
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly object _gate = new();

    private AppConfig _config;
    private LedLayout _layout;

    private DesktopDuplicator? _duplicator;
    private ZoneSampler _sampler;
    private readonly ColorPipeline _pipeline;
    private readonly AdalightLink _link;

    private Thread? _captureThread;
    private System.Threading.Timer? _watchdog;
    private FileSystemWatcher? _configWatcher;
    private System.Threading.Timer? _reloadDebounce;
    private string _watchedHash = "";
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _testRunning;

    private DateTime _lastCaptureHealthyUtc = DateTime.UtcNow;
    private DateTime _resumeCooldownUntilUtc = DateTime.MinValue;
    private DateTime _lastHealthLogUtc = DateTime.UtcNow;
    private DateTime _lastFpsSample = DateTime.UtcNow;
    private int _framesSinceSample;
    private double _fps;
    private int _consecutiveFailures;

    public event Action? StatusChanged;

    public Engine(AppConfig config, LedLayout layout)
    {
        _config = config;
        _layout = layout;
        _sampler = new ZoneSampler(layout);
        _pipeline = new ColorPipeline(config.Color, config.Power, config.Serial.ColorOrder);
        _link = new AdalightLink(config.Serial, config.Watchdog);
        _link.StateChanged += () => StatusChanged?.Invoke();
    }

    public EngineStatus Status => new()
    {
        Enabled = _config.Enabled,
        Paused = _paused,
        CaptureReady = _duplicator?.IsReady ?? false,
        SerialConnected = _link.IsConnected,
        PortName = _link.PortName,
        Fps = _fps,
        LedCount = _layout.Count,
        CaptureDescription = _duplicator?.Description ?? "not initialised"
    };

    public void Start()
    {
        if (_running) return;
        _running = true;

        Log.Info($"Engine starting: {_layout.Count} LEDs, target {_config.Capture.TargetFps} fps");

        _link.Start();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Qlow.Capture",
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();

        _watchdog = new System.Threading.Timer(WatchdogTick, null, 1000, 1000);

        StartConfigWatcher();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _watchdog?.Dispose();
        _watchdog = null;

        if (_configWatcher != null) _configWatcher.EnableRaisingEvents = false;
        _configWatcher?.Dispose();
        _configWatcher = null;
        _reloadDebounce?.Dispose();
        _reloadDebounce = null;

        _captureThread?.Join(TimeSpan.FromSeconds(3));
        _captureThread = null;

        if (_config.BlackOnExit && _layout.Count > 0) _link.SubmitBlack(_layout.Count);
        _link.Stop(blackout: _config.BlackOnExit);

        _duplicator?.Dispose();
        _duplicator = null;

        Log.Info("Engine stopped");
    }

    public void SetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _config.Save();

        if (!enabled && _layout.Count > 0)
        {
            _pipeline.Reset();
            _link.SubmitBlack(_layout.Count);
        }

        Log.Info($"Backlight {(enabled ? "enabled" : "disabled")}");
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Applies edits to config.json and layout.json without anyone having to restart
    /// or find a menu item. Editing a file and seeing nothing happen is a bad enough
    /// surprise that it is worth the small amount of machinery.
    ///
    /// Loading the config rewrites it, which would otherwise make the watcher
    /// retrigger itself forever, so changes are compared by content hash rather than
    /// by the fact that a write happened.
    /// </summary>
    private void StartConfigWatcher()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            _watchedHash = HashWatchedFiles();

            _configWatcher = new FileSystemWatcher(AppConfig.Directory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            // Editors save in several steps, so wait for the dust to settle rather
            // than reacting to each individual write.
            void Bump(object? _, FileSystemEventArgs __) =>
                _reloadDebounce?.Change(800, Timeout.Infinite);

            _configWatcher.Changed += Bump;
            _configWatcher.Created += Bump;
            _configWatcher.Renamed += (_, _) => _reloadDebounce?.Change(800, Timeout.Infinite);

            _reloadDebounce = new System.Threading.Timer(_ =>
            {
                try
                {
                    var hash = HashWatchedFiles();
                    if (hash == _watchedHash) return;

                    Log.Info("Config or layout changed on disk, reloading");
                    ReloadConfig();
                    _watchedHash = HashWatchedFiles();
                }
                catch (Exception ex)
                {
                    Log.Error("Automatic reload failed", ex);
                }
            }, null, Timeout.Infinite, Timeout.Infinite);

            Log.Info($"Watching {AppConfig.Directory} for edits");
        }
        catch (Exception ex)
        {
            Log.Error("Could not watch the config directory; edits will need a manual reload", ex);
        }
    }

    private static string HashWatchedFiles()
    {
        var sb = new System.Text.StringBuilder();

        foreach (var path in new[] { AppConfig.FilePath, LedLayout.FilePath })
        {
            try
            {
                if (!File.Exists(path)) { sb.Append('-'); continue; }
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sha = System.Security.Cryptography.SHA256.Create();
                sb.Append(Convert.ToHexString(sha.ComputeHash(stream)));
            }
            catch
            {
                // Mid-save the file can be briefly unreadable; treat it as unchanged
                // and let the next event settle it.
                sb.Append('?');
            }
        }

        return sb.ToString();
    }

    public void ReloadConfig()
    {
        var fresh = AppConfig.Load();
        var layout = LedLayout.Load(fresh.Layout);

        lock (_gate)
        {
            _config = fresh;
            _layout = layout;
            _sampler = new ZoneSampler(layout);
            _pipeline.Update(fresh.Color, fresh.Power, fresh.Serial.ColorOrder);
            _pipeline.Reset();
        }

        _link.UpdateConfig(fresh.Serial, fresh.Watchdog);
        _duplicator?.Invalidate("config reloaded");

        Log.Info("Config reloaded");
        StatusChanged?.Invoke();
    }

    public void ForceReconnect()
    {
        _duplicator?.Invalidate("manual reconnect");
        _link.ForceReconnect("manual reconnect");
    }

    /// <summary>
    /// Runs a diagnostic pattern instead of the screen for a few seconds. This is
    /// how you tell, on a strip you did not build, which end the data goes in, which
    /// way it runs, and whether the channel order is right. Capture keeps running
    /// but stops submitting for the duration.
    /// </summary>
    public void RunTest(TestPattern pattern)
    {
        if (_testRunning) return;

        var count = _layout.Count;
        if (count == 0) return;

        _testRunning = true;
        StatusChanged?.Invoke();

        var worker = new Thread(() =>
        {
            try
            {
                Log.Info($"Test pattern: {pattern}");
                var frame = new byte[count * 3];

                switch (pattern)
                {
                    case TestPattern.Chase:
                        RunChase(frame, count);
                        break;
                    case TestPattern.ColorOrder:
                        RunColorOrder(frame, count);
                        break;
                    case TestPattern.Sides:
                        RunSides(frame, count);
                        break;
                }

                Array.Clear(frame);
                _link.Submit(frame, count);
                Log.Info("Test pattern finished");
            }
            catch (Exception ex)
            {
                Log.Error("Test pattern failed", ex);
            }
            finally
            {
                _testRunning = false;
                _pipeline.Reset();
                StatusChanged?.Invoke();
            }
        })
        { IsBackground = true, Name = "Qlow.Test" };

        worker.Start();
    }

    /// <summary>One white dot walks the strip, so LED 1 and the direction are obvious.</summary>
    private void RunChase(byte[] frame, int count)
    {
        for (var i = 0; i < count && _testRunning && _running; i++)
        {
            Array.Clear(frame);

            // A short tail makes the direction readable even on a fast lap.
            _pipeline.WriteOrdered(frame, i, 255, 255, 255);
            if (i >= 1) _pipeline.WriteOrdered(frame, i - 1, 60, 60, 60);
            if (i >= 2) _pipeline.WriteOrdered(frame, i - 2, 15, 15, 15);

            _link.Submit(frame, count);
            Thread.Sleep(45);
        }
    }

    /// <summary>
    /// Whole strip red, then green, then blue. If the colours come out in a
    /// different order, serial.colorOrder is wrong. Held at a quarter brightness
    /// because a full-white-equivalent flood on an underspecified supply is exactly
    /// the kind of thing that browns a board out mid-test.
    /// </summary>
    private void RunColorOrder(byte[] frame, int count)
    {
        (byte R, byte G, byte B, string Name)[] steps =
        {
            (64, 0, 0, "red"),
            (0, 64, 0, "green"),
            (0, 0, 64, "blue")
        };

        foreach (var step in steps)
        {
            if (!_testRunning || !_running) return;

            Log.Info($"Test: whole strip {step.Name}");
            for (var i = 0; i < count; i++) _pipeline.WriteOrdered(frame, i, step.R, step.G, step.B);
            _link.Submit(frame, count);

            for (var waited = 0; waited < 2000 && _testRunning && _running; waited += 50) Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Paints each configured run in its own colour, so the per-side counts in
    /// config.json can be checked against the physical strip at a glance.
    /// </summary>
    private void RunSides(byte[] frame, int count)
    {
        var l = _config.Layout;
        (int Length, byte R, byte G, byte B)[] runs =
        {
            (l.BottomLeft, 64, 0, 0),
            (l.Left, 0, 64, 0),
            (l.Top, 0, 0, 64),
            (l.Right, 64, 64, 0),
            (l.BottomRight, 64, 0, 64)
        };

        var index = 0;
        foreach (var run in runs)
        {
            for (var i = 0; i < run.Length && index < count; i++, index++)
                _pipeline.WriteOrdered(frame, index, run.R, run.G, run.B);
        }

        // Anything past the configured runs stays dark, which is itself the signal
        // that the counts do not add up to the strip length.
        _link.Submit(frame, count);
        for (var waited = 0; waited < 8000 && _testRunning && _running; waited += 50) Thread.Sleep(50);
    }

    private void CaptureLoop()
    {
        var clock = Stopwatch.StartNew();
        var nextFrameMs = 0.0;

        while (_running)
        {
            AppConfig config;
            LedLayout layout;
            ZoneSampler sampler;
            lock (_gate)
            {
                config = _config;
                layout = _layout;
                sampler = _sampler;
            }

            if (!config.Enabled || _paused || _testRunning)
            {
                Thread.Sleep(150);
                continue;
            }

            // Nothing D3D-related happens until this passes. See the comment in
            // OnPowerModeChanged: creating a device too soon after resume has crashed
            // the whole process with an access violation the CLR cannot let this
            // try/catch stop, so the guard has to sit before the call is ever made.
            if (DateTime.UtcNow < _resumeCooldownUntilUtc)
            {
                Thread.Sleep(150);
                continue;
            }

            _duplicator ??= new DesktopDuplicator(config.Capture.MonitorIndex, config.Capture.DownscaleWidth);

            var frameIntervalMs = 1000.0 / Math.Clamp(config.Capture.TargetFps, 1, 240);
            var frame = _duplicator.TryGrab((int)Math.Max(16, frameIntervalMs * 2), out var status);

            if (status == CaptureStatus.Unavailable)
            {
                // Rebuild is already scheduled inside the duplicator. Back off a
                // little so a monitor that stays asleep does not spin a core.
                _consecutiveFailures++;
                var wait = Math.Min(1000, 50 * Math.Min(_consecutiveFailures, 20));
                Thread.Sleep(wait);
                StatusChanged?.Invoke();
                continue;
            }

            // Duplication answered, so it is healthy. That includes answering "nothing
            // changed": an idle desktop is not a fault, and treating it as one is how
            // the watchdog used to end up rebuilding the stack every few seconds
            // forever.
            _consecutiveFailures = 0;
            _lastCaptureHealthyUtc = DateTime.UtcNow;

            if (frame == null)
            {
                // Healthy, but nothing has been captured yet at all, so there is
                // nothing to send. Wait for the screen to do something.
                Thread.Sleep((int)Math.Max(16, frameIntervalMs));
                continue;
            }

            if (frame.Width > 0 && frame.Height > 0 && layout.Count > 0)
            {
                var zones = sampler.Sample(frame);
                var bytes = _pipeline.Process(zones);
                _link.Submit(bytes, layout.Count);

                _lastCaptureHealthyUtc = DateTime.UtcNow;
                _framesSinceSample++;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastFpsSample).TotalSeconds >= 2)
            {
                _fps = _framesSinceSample / (now - _lastFpsSample).TotalSeconds;
                _framesSinceSample = 0;
                _lastFpsSample = now;
                StatusChanged?.Invoke();
            }

            // Simple pacing against a monotonic clock so drift does not accumulate.
            nextFrameMs += frameIntervalMs;
            var slack = nextFrameMs - clock.Elapsed.TotalMilliseconds;
            if (slack > 1) Thread.Sleep((int)slack);
            else if (slack < -250) nextFrameMs = clock.Elapsed.TotalMilliseconds;
        }
    }

    private void WatchdogTick(object? state)
    {
        if (!_running || !_config.Enabled || _paused || _testRunning) return;

        try
        {
            // A heartbeat line every minute, so an intermittent fault can be placed on
            // a timeline afterwards instead of having to be caught in the act.
            if ((DateTime.UtcNow - _lastHealthLogUtc).TotalSeconds >= 60)
            {
                _lastHealthLogUtc = DateTime.UtcNow;
                Log.Info($"Health: {_fps:F1} fps captured, {_link.FramesWritten} frames sent on " +
                         $"{_link.PortName ?? "-"}, {_link.ControllerReboots} controller reboots");
            }

            // Keyed on the last time duplication answered at all, not on the last new
            // frame. A static screen answers "nothing changed" and is perfectly
            // healthy; only silence from the duplication object itself is a fault.
            var stalledFor = (DateTime.UtcNow - _lastCaptureHealthyUtc).TotalMilliseconds;
            if (stalledFor > _config.Watchdog.CaptureStallMs)
            {
                Log.Warn($"Capture unavailable for {stalledFor:F0} ms, forcing rebuild");
                _duplicator?.Invalidate("watchdog stall");
                _lastCaptureHealthyUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Watchdog tick failed", ex);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Log.Info($"Power mode: {e.Mode}");

        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _paused = true;
                if (_layout.Count > 0) _link.SubmitBlack(_layout.Count);
                break;

            case PowerModes.Resume:
                // The USB stack re-enumerates on resume and the duplication object is
                // always invalid by then. Rebuild both rather than hoping.
                _paused = false;
                _pipeline.Reset();
                _lastCaptureHealthyUtc = DateTime.UtcNow;
                _duplicator?.Invalidate("resumed from sleep");
                _link.ForceReconnect("resumed from sleep");

                // The display driver is not necessarily ready the instant this event
                // fires. Calling D3D11CreateDevice into that window has been observed
                // to raise AccessViolationException from inside the native driver —
                // a corrupted-state exception the CLR will not let managed code catch,
                // so the whole process dies with no chance to log anything. The only
                // real defence is not making the call yet. Held here rather than as a
                // flat startup delay so it only costs time right after a resume.
                _resumeCooldownUntilUtc = DateTime.UtcNow.AddSeconds(5);
                Log.Info("Holding capture for 5s to let the display driver settle after resume");
                break;
        }

        StatusChanged?.Invoke();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Log.Info($"Session switch: {e.Reason}");

        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
                if (_config.BlackOnLock)
                {
                    _paused = true;
                    if (_layout.Count > 0) _link.SubmitBlack(_layout.Count);
                }
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
                _paused = false;
                _pipeline.Reset();
                _lastCaptureHealthyUtc = DateTime.UtcNow;
                // The lock screen runs on a separate desktop, so duplication was lost.
                _duplicator?.Invalidate("session unlocked");
                break;
        }

        StatusChanged?.Invoke();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Info("Display settings changed");
        _lastCaptureHealthyUtc = DateTime.UtcNow;
        _duplicator?.Invalidate("display settings changed");
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _link.Dispose();
    }
}
