using System.Globalization;
using System.Text;

namespace ClaudeUsageTray.Diagnostics;

/// <summary>
/// Appends lines to one file per day (<c>yyyy-MM-dd.log</c>) in <see cref="Directory"/> and deletes the files older
/// than <see cref="RetentionDays"/> days, on start and whenever the day changes. Thread-safe. Write failures are
/// swallowed: logging must never take the app down.
/// </summary>
public sealed class FileLogger
{
    public const int RetentionDays = 30;
    private const string DateFormat = "yyyy-MM-dd";

    private readonly object _lock = new();
    private DateTime _currentDay;

    public FileLogger(string directory)
    {
        Directory = directory;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray", "logs");

    public string Directory { get; }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, object? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        DateTime now = DateTime.Now;
        string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                if (now.Date != _currentDay)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    _currentDay = now.Date;
                    DeleteExpired(now.Date);
                }
                File.AppendAllText(Path.Combine(Directory, now.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log"),
                    line, Encoding.UTF8);
            }
            catch
            {
                // Disk full, folder locked...: nothing better to do than drop the line.
            }
        }
    }

    /// <summary>The date comes from the file name, not the file time, so a copied or touched file still expires.</summary>
    private void DeleteExpired(DateTime today)
    {
        DateTime cutoff = today.AddDays(-RetentionDays);
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.log"))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime day) || day >= cutoff)
                continue;
            try
            {
                File.Delete(file);
            }
            catch
            {
                // In use or read-only: try again on the next day change.
            }
        }
    }
}
