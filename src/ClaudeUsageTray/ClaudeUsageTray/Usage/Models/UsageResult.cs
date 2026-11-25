using System.Text.Json.Nodes;

namespace ClaudeUsageTray.Usage.Models;

/// <summary>
/// One answer of <see cref="UsageProvider"/>. <see cref="Data"/> is a fresh object per call, owned by the caller.
/// When the refresh failed but an older value exists, <see cref="IsStale"/> is set and <see cref="Data"/> carries
/// <c>_stale</c>, <c>_error</c>, <c>_cached_at</c> like <c>src/index.js</c>.
/// </summary>
public sealed class UsageResult
{
    public required JsonObject Data { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public bool IsStale { get; init; }

    /// <summary>Why the refresh failed (only when <see cref="IsStale"/>).</summary>
    public string? Error { get; init; }
}
