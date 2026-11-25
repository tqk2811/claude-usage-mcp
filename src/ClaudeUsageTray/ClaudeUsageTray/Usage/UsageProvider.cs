using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeUsageTray.Usage.Models;

namespace ClaudeUsageTray.Usage;

/// <summary>
/// Per-process usage cache: last response + time, 30s TTL, one request in flight at a time (callers queue on a
/// semaphore and then reuse what the previous holder fetched). <c>force</c> only accepts a value fetched after the call
/// started. When a refresh fails and an older value exists, that value is returned marked stale.
/// Owns the <see cref="UsageApiClient"/> passed in.
/// </summary>
public sealed class UsageProvider : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly UsageApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Kept as text: every caller gets its own parsed tree (JsonNode is not safe to share across threads).
    private string? _lastText;
    private DateTimeOffset _lastAt;

    public UsageProvider(UsageApiClient api)
    {
        _api = api;
    }

    public async Task<UsageResult> GetAsync(bool force, CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastText is not null && (force ? _lastAt >= started : DateTimeOffset.UtcNow - _lastAt < CacheTtl))
                return Fresh(_lastText, _lastAt);

            string text;
            try
            {
                text = await _api.RequestAsync(cancellationToken);
                if (JsonNode.Parse(text) is not JsonObject)
                    throw new FormatException("Usage API returned a non-object JSON body.");
            }
            catch (Exception ex) when (_lastText is not null && ex is not OperationCanceledException { CancellationToken.IsCancellationRequested: true })
            {
                return Stale(_lastText, _lastAt, ex.Message);
            }

            _lastText = text;
            _lastAt = DateTimeOffset.UtcNow;
            return Fresh(text, _lastAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // _gate is deliberately not disposed: a refresh still running at exit releases it afterwards, and releasing a
        // disposed SemaphoreSlim throws. It holds no OS handle unless AvailableWaitHandle is used, so nothing leaks.
        _api.Dispose();
    }

    private static UsageResult Fresh(string text, DateTimeOffset at)
        => new() { Data = (JsonObject)JsonNode.Parse(text)!, FetchedAt = at };

    private static UsageResult Stale(string text, DateTimeOffset at, string error)
    {
        JsonObject data = (JsonObject)JsonNode.Parse(text)!;
        data["_stale"] = true;
        data["_error"] = error;
        // Same shape as JavaScript's Date.toISOString().
        data["_cached_at"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return new() { Data = data, FetchedAt = at, IsStale = true, Error = error };
    }
}
