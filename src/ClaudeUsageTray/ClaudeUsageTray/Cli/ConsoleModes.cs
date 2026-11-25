using System.Runtime.InteropServices;
using System.Text;
using ClaudeUsageTray.Mcp;
using ClaudeUsageTray.Usage;
using ClaudeUsageTray.Usage.Models;

namespace ClaudeUsageTray.Cli;

/// <summary>
/// The console entry points (<c>--mcp</c>, <c>--once</c>). Touches no WPF type, so these modes never load WPF.
/// The exe is a WinExe: stdio works when the parent redirects it (Claude Code does); for <c>--once</c> typed in a
/// console, the parent console is attached so the output is visible.
/// </summary>
public static class ConsoleModes
{
    private const int StdOutputHandle = -11, StdErrorHandle = -12;
    private const int AttachParentProcess = -1;

    public static int RunMcp()
    {
        using UsageProvider usage = CreateProvider();
        using StreamReader input = new(Console.OpenStandardInput(), new UTF8Encoding(false));
        using StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false));
        // Entry point boundary: Main has to stay synchronous ([STAThread] for the tray mode).
        new McpServer(usage, input, output).RunAsync().GetAwaiter().GetResult();
        return 0;
    }

    public static int RunOnce()
    {
        // Redirected (file/pipe) stdout or stderr means someone already collects the output; attaching would hijack it.
        if (!HasStdHandle(StdOutputHandle) && !HasStdHandle(StdErrorHandle))
            AttachConsole(AttachParentProcess);
        using UsageProvider usage = CreateProvider();
        using StreamWriter output = new(Console.OpenStandardOutput(), new UTF8Encoding(false));
        try
        {
            // Entry point boundary, see RunMcp.
            UsageResult result = usage.GetAsync(force: true).GetAwaiter().GetResult();
            output.WriteLine(UsageFormatter.ToText(UsageFormatter.Summarize(result.Data), result.Data));
            return 0;
        }
        catch (Exception ex)
        {
            using StreamWriter error = new(Console.OpenStandardError(), new UTF8Encoding(false));
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static UsageProvider CreateProvider() => new(new UsageApiClient(new CredentialReader()));

    private static bool HasStdHandle(int id)
    {
        IntPtr handle = GetStdHandle(id);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
