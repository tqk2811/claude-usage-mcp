namespace ClaudeUsageTray.Mcp.Models;

/// <summary>Outcome of one <c>claude</c> CLI run.</summary>
public sealed class CliResult
{
    public required int ExitCode { get; init; }

    /// <summary>stdout and stderr together, trimmed.</summary>
    public required string Output { get; init; }
}
