using ClaudeUsageTray.Config.Enums;

namespace ClaudeUsageTray.Config.Models;

/// <summary>JSON shape of <c>config.json</c>; immutable, changed through <see cref="AppConfig.Update"/>.</summary>
// Setters, not init: the .NET 8 JSON source generator sets init-only properties through an object initializer,
// so a property missing from the file would get default(T) instead of the initializer value. Treat as immutable.
public sealed record ConfigData
{
    public const int MinRefreshSeconds = 30;
    public const int MaxRefreshSeconds = 86400;
    public const string DefaultFontFamily = "Segoe UI";
    public const int MinMcpPort = 1024;
    public const int MaxMcpPort = 65535;
    public const int DefaultMcpPort = 52417;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Culture name (<c>en</c>, <c>vi</c>); <c>null</c> = follow the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>Not below 30s: the usage value is cached for 30s anyway.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>Applied by re-adding both icons; the user dragging an icon in the tray can override it.</summary>
    public IconOrder IconOrder { get; set; } = IconOrder.SessionFirst;

    /// <summary>GDI face name of the tray number; Segoe UI when not installed.</summary>
    public string FontFamily { get; set; } = DefaultFontFamily;

    public bool FontBold { get; set; } = true;

    public IconSettings Session { get; set; } = new();

    public IconSettings Weekly { get; set; } = IconSettings.CreateWeeklyDefault();

    /// <summary>Port of the MCP HTTP server (<c>http://127.0.0.1:&lt;port&gt;/mcp</c>) the tray runs.</summary>
    public int McpPort { get; set; } = DefaultMcpPort;
}
