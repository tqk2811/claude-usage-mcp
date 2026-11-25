using System.Globalization;

namespace ClaudeUsageTray.Config.Helpers;

/// <summary>Parses/formats <c>#RRGGBB</c> / <c>#AARRGGBB</c> as a packed ARGB value.</summary>
public static class ColorHexHelper
{
    public static bool TryParse(string? text, out uint argb)
    {
        argb = 0;
        if (text is null)
            return false;
        string hex = text.Trim().TrimStart('#');
        if ((hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            return false;
        argb = hex.Length == 6 ? 0xFF000000 | value : value;
        return true;
    }

    /// <summary>Falls back to <paramref name="fallback"/> when the text is not a color.</summary>
    public static uint Parse(string? text, uint fallback) => TryParse(text, out uint argb) ? argb : fallback;

    public static string Format(uint argb) => $"#{argb:X8}";
}
