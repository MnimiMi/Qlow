using System.Windows.Forms;

namespace Qlow;

internal static class Program
{
    private const string QuitEventName = @"Local\Qlow.Quit";

    private static Mutex? _instanceLock;

    [STAThread]
    private static void Main(string[] args)
    {
        // Before anything reads a config: settings from the former name have to be
        // in place first, or loading would create defaults and orphan them.
        Migration.FromLegacy();

        // Asks a running instance to shut down properly, so the strip gets its black
        // frame and the port is released cleanly. Killing the process skips all of it.
        if (args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase)))
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var existing))
            {
                using (existing) existing.Set();
                Log.Info("Quit requested by --quit");
            }
            else
            {
                Log.Info("--quit: no running instance found");
            }
            return;
        }

        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Log.MinLevel = LogLevel.Debug;
            SelfTest.Run();
            return;
        }

        if (args.Any(a => string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase)))
        {
            Log.MinLevel = LogLevel.Info;
            ApplicationConfiguration.Initialize();
            Bench.Run();
            return;
        }

        var importAt = Array.FindIndex(args, a => string.Equals(a, "--import", StringComparison.OrdinalIgnoreCase));
        if (importAt >= 0)
        {
            Log.MinLevel = LogLevel.Debug;

            if (importAt + 1 >= args.Length)
            {
                Log.Error("--import needs a path to a Hyperion/HyperHDR .json or a Prismatik .ini");
                return;
            }

            var imported = LedLayout.ImportFromFile(args[importAt + 1]);
            if (imported == null)
            {
                Log.Error($"Import failed: {args[importAt + 1]}");
                return;
            }

            imported.Save();
            Log.Info($"Imported {imported.Count} zones; restart Qlow or use Reload config and layout");
            return;
        }

        // A second instance would fight the first one for the duplication object and
        // the COM port, and both would lose.
        _instanceLock = new Mutex(true, @"Local\Qlow.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Qlow is already running.", "Qlow",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error($"Unhandled exception: {e.ExceptionObject}");
        Application.ThreadException += (_, e) =>
            Log.Error("Unhandled UI exception", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();

        Log.Info("----------------------------------------");
        Log.Info($"Qlow starting, pid {Environment.ProcessId}");

        var config = AppConfig.Load();
        var layout = LedLayout.Load(config.Layout);

        if (layout.Count == 0)
        {
            Log.Error("Layout is empty, nothing to drive");
            MessageBox.Show($"No LED layout could be built.\n\nCheck {LedLayout.FilePath}",
                "Qlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var engine = new Engine(config, layout);
        engine.Start();

        using var tray = new TrayApp(engine);
        Application.ApplicationExit += (_, _) =>
        {
            Log.Info("Exiting");
            engine.Dispose();
        };

        // A second process started with --quit sets this, which is the only way to
        // stop a windowless tray app from outside without killing it.
        using var quitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        var quitWatcher = new Thread(() =>
        {
            quitSignal.WaitOne();
            Log.Info("Quit signal received");
            tray.RequestExit();
        })
        { IsBackground = true, Name = "Qlow.QuitWatcher" };
        quitWatcher.Start();

        Application.Run(tray);
    }
}
