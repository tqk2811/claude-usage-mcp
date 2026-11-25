namespace ClaudeUsageTray.Config.Models;

/// <summary>
/// Look of one tray icon. Below <see cref="LowThreshold"/> the number uses <see cref="LowColor"/>, below
/// <see cref="HighThreshold"/> <see cref="MidColor"/>, otherwise <see cref="HighColor"/>.
/// Colors are <c>#RRGGBB</c> or <c>#AARRGGBB</c>.
/// </summary>
// Setters, not init: the .NET 8 JSON source generator sets init-only properties through an object initializer,
// so a property missing from the file would get default(T) instead of the initializer value. Treat as immutable.
public sealed record IconSettings
{
    public bool Visible { get; set; } = true;

    public int LowThreshold { get; set; } = 50;

    public int HighThreshold { get; set; } = 80;

    public string LowColor { get; set; } = "#FF22C55E";

    public string MidColor { get; set; } = "#FFF59E0B";

    public string HighColor { get; set; } = "#FFEF4444";

    /// <summary>Default fully transparent: only the number is drawn.</summary>
    public string BackgroundColor { get; set; } = "#00000000";
}
