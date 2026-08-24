using System.Text;

namespace Qlow;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Tiny always-on logger. An intermittent fault is worth very little without a
/// record of when it happened, so this writes unconditionally, rotates by size,
/// and never throws out of a log call.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private const int RingCapacity = 400;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qlow", "logs");

    public static string FilePath { get; } = Path.Combine(Directory, "qlow.log");

    public static event Action<string>? LineWritten;

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static void Error(string m, Exception ex) =>
        Write(LogLevel.Error, $"{m} :: {ex.GetType().Name}: {ex.Message}");

    public static string[] Recent()
    {
        lock (Gate) return Ring.ToArray();
    }

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[0]}] {message}";

        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity) Ring.Dequeue();

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    var old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        try { LineWritten?.Invoke(line); } catch { }
    }
}
