using System.Text.Json.Nodes;

namespace ClaudeUsageTray.Usage;

/// <summary>
/// Reads the Claude Code OAuth access token: env <c>CLAUDE_CODE_OAUTH_TOKEN</c>, else
/// <c>claudeAiOauth.accessToken</c> in <c>.credentials.json</c> under <c>CLAUDE_CONFIG_DIR</c> (default <c>~/.claude</c>).
/// Read on every request, so a token Claude Code refreshed is picked up without a restart.
/// </summary>
public sealed class CredentialReader
{
    public string ReadToken()
    {
        string? envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
            return envToken;

        string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configDir
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string file = Path.Combine(dir, ".credentials.json");

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot read OAuth credentials at {file}: {ex.Message}", ex);
        }

        JsonNode? oauth = json?["claudeAiOauth"];
        string? token = oauth?["accessToken"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? oauth["accessToken"]!.GetValue<string>()
            : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException($"No claudeAiOauth.accessToken in {file}");

        if (oauth!["expiresAt"] is JsonValue expires
            && expires.TryGetValue(out double expiresAt)
            && expiresAt > 0
            && expiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("OAuth token expired. Run any Claude Code command to refresh it.");

        return token;
    }
}
