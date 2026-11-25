using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ClaudeUsageTray.Mcp.Models;

namespace ClaudeUsageTray.Mcp;

/// <summary>
/// Registers this exe as the user-scope <c>claude-usage</c> MCP server through the <c>claude</c> CLI found on PATH
/// (<c>claude.exe</c>, or the npm <c>claude.cmd</c> shim run through cmd.exe).
/// </summary>
public sealed class ClaudeCliRegistrar
{
    public const string ServerName = "claude-usage";

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(90);

    private readonly string _executablePath;

    public ClaudeCliRegistrar(string executablePath)
    {
        _executablePath = executablePath;
    }

    /// <summary>What a user would type to register by hand.</summary>
    public string RegisterCommand => $"claude mcp add --scope user {ServerName} -- \"{_executablePath}\" --mcp";

    /// <summary>
    /// Removes any user-scope entry first (failure ignored: usually "not found"), then adds, so registering again
    /// after the exe moved replaces the old path instead of failing with "already exists".
    /// </summary>
    /// <returns>The result of <c>add</c>; <c>null</c> when <c>claude</c> is not on PATH.</returns>
    public async Task<CliResult?> RegisterAsync()
    {
        try
        {
            await RunAsync($"mcp remove --scope user {ServerName}");
        }
        catch (Exception)
        {
            // Nothing to remove, or the CLI hiccuped: add reports the real problem.
        }
        return await RunAsync($"mcp add --scope user {ServerName} -- \"{_executablePath}\" --mcp");
    }

    /// <summary>Removes the entry from the scope <see cref="GetStatusAsync"/> reports (user when unknown).</summary>
    public async Task<CliResult?> UnregisterAsync()
    {
        CliResult? status = await GetStatusAsync();
        if (status is null)
            return null;
        string scope = (status.ExitCode == 0 ? ParseScope(status.Output) : null) ?? "user";
        return await RunAsync($"mcp remove --scope {scope} {ServerName}");
    }

    /// <summary>Exit code 0 = registered (in any scope, see <see cref="ParseScope"/>).</summary>
    public Task<CliResult?> GetStatusAsync() => RunAsync($"mcp get {ServerName}");

    /// <summary>
    /// Reads the scope out of <c>claude mcp get</c> output (<c>Scope: User config (...)</c>).
    /// Returns <c>user</c>, <c>local</c> or <c>project</c>; <c>null</c> when it cannot be told.
    /// </summary>
    public static string? ParseScope(string output)
    {
        Match match = Regex.Match(output, @"^\s*Scope:\s*(user|local|project)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private async Task<CliResult?> RunAsync(string arguments)
    {
        string? claude = FindClaude();
        if (claude is null)
            return null;

        bool isScript = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo info = new()
        {
            FileName = isScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : claude,
            // cmd /s /c "<whole line>": outer quotes stripped, inner quoting kept.
            Arguments = isScript ? $"/d /s /c \"\"{claude}\" {arguments}\"" : arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {claude}");
        process.StandardInput.Close();
        // Both pipes read concurrently, or a full pipe would hang the child.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(RunTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already exited.
            }
            try
            {
                // Observe the readers so a pipe error does not surface later as an unobserved exception.
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Output of a killed run is not needed.
            }
            throw new TimeoutException($"claude {arguments} did not finish in {RunTimeout.TotalSeconds:0}s.");
        }
        string output = (await stdout + "\n" + await stderr).Trim();
        return new CliResult { ExitCode = process.ExitCode, Output = output };
    }

    /// <summary>
    /// The PATH of this process can be stale (the app may have been started before <c>claude</c> was installed), so the
    /// machine and user <c>Path</c> are re-read from the registry on each call, then the usual install folders are tried.
    /// </summary>
    private static string? FindClaude()
    {
        string[] names = ["claude.exe", "claude.cmd", "claude.bat"];
        foreach (string dir in SearchDirectories())
        {
            foreach (string name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim().Trim('"'), name);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        foreach (string dir in SplitPath(Environment.GetEnvironmentVariable("PATH")))
            yield return dir;
        foreach (string dir in SplitPath(ReadRegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")))
            yield return dir;
        foreach (string dir in SplitPath(ReadRegistryPath(Registry.CurrentUser, "Environment")))
            yield return dir;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
            yield return Path.Combine(profile, ".local", "bin");
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length > 0)
            yield return Path.Combine(appData, "npm");
    }

    private static string? ReadRegistryPath(RegistryKey root, string subKey)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKey);
            string? raw = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return raw is null ? null : Environment.ExpandEnvironmentVariables(raw);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<string> SplitPath(string? path)
        => (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
