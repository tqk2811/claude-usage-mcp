namespace ClaudeUsageTray.Usage.Models;

/// <summary>One limit window of the usage response (<c>five_hour</c>, <c>seven_day</c>, ...).</summary>
public sealed class UsageWindow
{
    /// <summary>API key, e.g. <c>five_hour</c>.</summary>
    public required string Key { get; init; }

    /// <summary>English label, as in <c>src/index.js</c>.</summary>
    public required string Label { get; init; }

    public required double UtilizationPercent { get; init; }

    /// <summary><c>resets_at</c> exactly as the API sent it.</summary>
    public string? ResetsAtRaw { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }
}
