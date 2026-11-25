using System.Net.Http;
using System.Net.Http.Headers;

namespace ClaudeUsageTray.Usage;

/// <summary>
/// Calls <c>GET https://api.anthropic.com/api/oauth/usage</c> with the same headers as <c>src/index.js</c>.
/// The <see cref="HttpClient"/> is created on the first request (the tray may never need it before the first tick)
/// and owned by this object.
/// </summary>
public sealed class UsageApiClient : IDisposable
{
    /// <summary>Same User-Agent version as the Node server, so the API sees one client.</summary>
    public const string ClientVersion = "1.0.0";

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private readonly CredentialReader _credentials;
    private readonly object _lock = new();
    private HttpClient? _http;

    public UsageApiClient(CredentialReader credentials)
    {
        _credentials = credentials;
    }

    /// <returns>The raw JSON response body.</returns>
    public async Task<string> RequestAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_USAGE_DEBUG") is { Length: > 0 })
            Console.Error.WriteLine($"[{Environment.ProcessId}] calling usage API");

        using HttpRequestMessage request = new(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credentials.ReadToken());
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", $"claude-usage-mcp/{ClientVersion}");
        // index.js sends Content-Type on its GET; HttpClient only allows it on content, so attach an empty body.
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Default ResponseContentRead: the 15s Timeout covers reading the body too.
        using HttpResponseMessage response = await GetClient().SendAsync(request, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Usage API HTTP {(int)response.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");
        return text;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _http?.Dispose();
            _http = null;
        }
    }

    private HttpClient GetClient()
    {
        lock (_lock)
            return _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }
}
