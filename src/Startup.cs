using Microsoft.Win32;

namespace Qlow;

/// <summary>
/// Per-user autostart. HKCU only, so it never needs elevation.
///
/// Writing the Run value is not enough on its own. Windows keeps a second
/// register of which startup entries it will honour — the same list Task Manager
/// shows under Startup apps — and an entry it does not recognise there can be
/// skipped at logon with no error, no event and no trace: the executable is
/// never even opened. Both records are written together, and cleared together.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Qlow";

    /// <summary>
    /// Twelve bytes. The low bit of the first says disabled; the rest is the time
    /// it was switched off, which is meaningless while it is on.
    /// </summary>
    private static readonly byte[] Approved = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) == null) return false;

            // Present in Run but switched off in Task Manager still counts as off.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags)
                return (flags[0] & 1) == 0;

            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Startup check failed: {ex.Message}");
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run == null) return;

            if (!enabled)
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
                RemoveApproval();
                Log.Info("Autostart removed");
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Warn("Cannot enable autostart: the executable path is unknown");
                return;
            }

            run.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            AddApproval();
            Log.Info($"Autostart set to {exe}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not change startup entry", ex);
        }
    }

    private static void AddApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
            key?.SetValue(ValueName, Approved, RegistryValueKind.Binary);
        }
        catch (Exception ex)
        {
            // Not fatal on machines that honour a bare Run entry, so the autostart
            // may still work; say so rather than failing the whole operation.
            Log.Warn($"Could not mark the startup entry as approved: {ex.Message}");
        }
    }

    private static void RemoveApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not clear the startup approval: {ex.Message}");
        }
    }
}
