using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTray.Usage.Models;

namespace ClaudeUsageTray.Usage;

/// <summary>Pure formatting of the usage response, matching <c>summarize</c>/<c>toText</c> of <c>src/index.js</c>.</summary>
public static class UsageFormatter
{
    public const string SessionKey = "five_hour";
    public const string WeeklyKey = "seven_day";

    private static readonly Dictionary<string, string> _labels = new()
    {
        [SessionKey] = "5-hour session",
        [WeeklyKey] = "Weekly (all models)",
        ["seven_day_opus"] = "Weekly (Opus)",
        ["seven_day_sonnet"] = "Weekly (Sonnet)",
        ["seven_day_oauth_apps"] = "Weekly (OAuth apps)",
    };

    /// <summary>Windows with a numeric <c>utilization</c>, in response order.</summary>
    public static List<UsageWindow> Summarize(JsonObject data)
    {
        List<UsageWindow> windows = [];
        foreach ((string key, JsonNode? value) in data)
        {
            if (value is not JsonObject obj || obj["utilization"] is not JsonValue utilization
                || utilization.GetValueKind() != JsonValueKind.Number)
                continue;
            string? resetsRaw = obj["resets_at"] is JsonValue r && r.GetValueKind() == JsonValueKind.String
                ? r.GetValue<string>()
                : null;
            DateTimeOffset? resetsAt = resetsRaw is not null
                && DateTimeOffset.TryParse(resetsRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            windows.Add(new UsageWindow
            {
                Key = key,
                Label = _labels.GetValueOrDefault(key, key),
                UtilizationPercent = utilization.GetValue<double>(),
                ResetsAtRaw = string.IsNullOrEmpty(resetsRaw) ? null : resetsRaw,
                ResetsAt = string.IsNullOrEmpty(resetsRaw) ? null : resetsAt,
            });
        }
        return windows;
    }

    /// <summary>The MCP <c>structuredContent</c> summary object.</summary>
    public static JsonObject ToSummaryJson(IEnumerable<UsageWindow> windows)
    {
        JsonObject result = [];
        foreach (UsageWindow w in windows)
        {
            result[w.Key] = new JsonObject
            {
                ["label"] = w.Label,
                ["utilization_percent"] = w.UtilizationPercent,
                ["resets_at"] = w.ResetsAtRaw,
                ["resets_at_local"] = w.ResetsAt is { } at ? FormatLocal(at) : null,
                ["resets_in"] = w.ResetsAt is { } at2 ? FormatRemaining(at2 - DateTimeOffset.Now) : null,
            };
        }
        return result;
    }

    public static string ToText(IEnumerable<UsageWindow> windows, JsonObject data)
    {
        string stale = data["_stale"] is JsonValue s && s.GetValueKind() == JsonValueKind.True
            ? $"(stale cache from {data["_cached_at"]}; refresh failed: {data["_error"]})\n"
            : "";
        return stale + string.Join("\n", windows.Select(w =>
            $"{w.Label}: {FormatPercent(w.UtilizationPercent)}% used"
            + (w.ResetsAt is { } at ? $", resets {FormatLocal(at)} (in {FormatRemaining(at - DateTimeOffset.Now)})" : "")));
    }

    /// <summary>Shortest round-trip form, like a JavaScript number (42, 42.5).</summary>
    public static string FormatPercent(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>1d 2h 5m</c>; zero days/hours are left out, minutes always shown; <c>now</c> when past.</summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "now";
        long m = (long)Math.Floor(remaining.TotalMinutes + 0.5); // Math.round of JavaScript
        long d = m / 1440, h = m % 1440 / 60;
        List<string> parts = [];
        if (d != 0)
            parts.Add($"{d}d");
        if (h != 0)
            parts.Add($"{h}h");
        parts.Add($"{m % 60}m");
        return string.Join(' ', parts);
    }

    private static string FormatLocal(DateTimeOffset at) => at.ToLocalTime().DateTime.ToString("G", CultureInfo.CurrentCulture);
}
