using System.Windows.Forms;

namespace BaldLight;

internal static class Program
{
    private static Mutex? _instanceLock;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Log.MinLevel = LogLevel.Debug;
            SelfTest.Run();
            return;
        }

        // A second instance would fight the first one for the duplication object and
        // the COM port, and both would lose.
        _instanceLock = new Mutex(true, @"Local\BaldLight.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("BaldLight is already running.", "BaldLight",
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
        Log.Info($"BaldLight starting, pid {Environment.ProcessId}");

        var config = AppConfig.Load();
        var layout = LedLayout.Load(config.Layout);

        if (layout.Count == 0)
        {
            Log.Error("Layout is empty, nothing to drive");
            MessageBox.Show($"No LED layout could be built.\n\nCheck {LedLayout.FilePath}",
                "BaldLight", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        Application.Run(tray);
    }
}
