namespace Qlow;

/// <summary>
/// Carries settings over from the application's former name.
///
/// Renaming moves both data directories, which would otherwise silently orphan a
/// hand-tuned layout: the app would find nothing, generate a default loop, and the
/// mapping someone spent an evening getting right would simply be gone. The files
/// are copied rather than moved, so the old install stays intact if it is still
/// wanted.
/// </summary>
public static class Migration
{
    private const string LegacyName = "BaldLight";

    public static void FromLegacy()
    {
        try
        {
            var legacyConfigDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);

            if (Directory.Exists(legacyConfigDir))
            {
                Directory.CreateDirectory(AppConfig.Directory);

                foreach (var file in new[] { "config.json", "layout.json" })
                {
                    var from = Path.Combine(legacyConfigDir, file);
                    var to = Path.Combine(AppConfig.Directory, file);

                    // Only ever fill a gap. An existing file always wins, so this
                    // cannot overwrite newer settings on a later run.
                    if (File.Exists(from) && !File.Exists(to))
                    {
                        File.Copy(from, to);
                        Log.Info($"Migrated {file} from the previous {LegacyName} install");
                    }
                }
            }

            MigrateLog();
            MigrateStartupEntry();
        }
        catch (Exception ex)
        {
            Log.Error("Migration from the previous install failed", ex);
        }
    }

    /// <summary>
    /// Keeps the old log alongside the new one. Diagnosing an intermittent fault
    /// depends on history, and starting a fresh file would throw it away.
    /// </summary>
    private static void MigrateLog()
    {
        var legacyLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyName, "logs", "baldlight.log");

        if (!File.Exists(legacyLog)) return;

        var target = Path.Combine(Log.Directory, $"{LegacyName.ToLowerInvariant()}-archived.log");
        if (File.Exists(target)) return;

        Directory.CreateDirectory(Log.Directory);
        File.Copy(legacyLog, target);
        Log.Info($"Kept the previous log as {Path.GetFileName(target)}");
    }

    /// <summary>
    /// The autostart entry still points at the old executable, which no longer
    /// exists. Replace it rather than leaving a broken command behind.
    /// </summary>
    private static void MigrateStartupEntry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;

            if (key.GetValue(LegacyName) == null) return;

            key.DeleteValue(LegacyName, throwOnMissingValue: false);
            Startup.Set(true);
            Log.Info("Moved the autostart entry to the new name and path");
        }
        catch (Exception ex)
        {
            Log.Error("Could not move the autostart entry", ex);
        }
    }
}
