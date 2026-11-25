using System.Text.Json;
using ClaudeUsageTray.Config.Enums;
using ClaudeUsageTray.Config.Models;

namespace ClaudeUsageTray.Config;

/// <summary>
/// Settings in <c>%APPDATA%\ClaudeUsageTray\config.json</c>. Holds one immutable <see cref="ConfigData"/> snapshot,
/// replaced atomically by <see cref="Update"/> (saved, then <see cref="Changed"/> raised on the caller's thread),
/// so the timer thread can read <see cref="Current"/> without locks.
/// A file that cannot be parsed is renamed to <c>config.json.corrupt-yyyyMMddHHmmss</c> by <see cref="Load"/> so the
/// next save does not overwrite what the user may want to repair.
/// </summary>
public sealed class AppConfig
{
    private readonly string _filePath;
    private readonly object _saveLock = new();
    private volatile ConfigData _current = new();

    public AppConfig(string filePath)
    {
        _filePath = filePath;
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "config.json");

    /// <summary>
    /// Raised on the thread that called <see cref="Update"/>, after the save. Subscribers touch UI and tray state, so
    /// <see cref="Update"/> must be called on the dispatcher thread.
    /// </summary>
    public event EventHandler? Changed;

    public string FilePath => _filePath;

    public ConfigData Current => _current;

    /// <exception cref="InvalidDataException">The file is not valid JSON; it was renamed (path in the message) and defaults are used.</exception>
    public void Load()
    {
        ConfigData data = new();
        if (File.Exists(_filePath))
        {
            try
            {
                data = JsonSerializer.Deserialize(File.ReadAllText(_filePath), ConfigJsonContext.Default.ConfigData) ?? new ConfigData();
            }
            catch (JsonException ex)
            {
                _current = Normalize(new ConfigData());
                string backup = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                try
                {
                    File.Move(_filePath, backup, overwrite: true);
                }
                catch (Exception moveError)
                {
                    throw new InvalidDataException($"{ex.Message} (could not keep a copy of the broken file: {moveError.Message})", ex);
                }
                throw new InvalidDataException($"{ex.Message} (the broken file was kept as {backup})", ex);
            }
        }
        _current = Normalize(data);
    }

    /// <summary>Applies <paramref name="change"/> to the current settings, saves and raises <see cref="Changed"/>.</summary>
    public void Update(Func<ConfigData, ConfigData> change)
    {
        lock (_saveLock)
        {
            ConfigData next = Normalize(change(_current));
            if (next == _current)
                return;
            _current = next;
            Save(next);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save(ConfigData data)
    {
        string json = JsonSerializer.Serialize(data, ConfigJsonContext.Default.ConfigData);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static ConfigData Normalize(ConfigData data) => data with
    {
        Theme = Enum.IsDefined(data.Theme) ? data.Theme : ThemeMode.System,
        IconOrder = Enum.IsDefined(data.IconOrder) ? data.IconOrder : IconOrder.SessionFirst,
        FontFamily = string.IsNullOrWhiteSpace(data.FontFamily) ? ConfigData.DefaultFontFamily : data.FontFamily.Trim(),
        RefreshSeconds = Math.Clamp(data.RefreshSeconds, ConfigData.MinRefreshSeconds, ConfigData.MaxRefreshSeconds),
        Session = Normalize(data.Session ?? new IconSettings()),
        Weekly = Normalize(data.Weekly ?? new IconSettings()),
    };

    private static IconSettings Normalize(IconSettings icon)
    {
        IconSettings defaults = new();
        int low = Math.Clamp(icon.LowThreshold, 0, 100);
        return icon with
        {
            LowThreshold = low,
            HighThreshold = Math.Clamp(icon.HighThreshold, low, 100),
            LowColor = icon.LowColor ?? defaults.LowColor,
            MidColor = icon.MidColor ?? defaults.MidColor,
            HighColor = icon.HighColor ?? defaults.HighColor,
            BackgroundColor = icon.BackgroundColor ?? defaults.BackgroundColor,
        };
    }
}
