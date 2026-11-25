using System.Runtime.CompilerServices;
using ClaudeUsageTray.Cli;
using ClaudeUsageTray.Startup;
using ClaudeUsageTray.Tray;

namespace ClaudeUsageTray;

/// <summary>
/// Entry point. <c>--mcp</c> and <c>--once</c> branch off before any WPF type is touched (no mutex, no tray);
/// otherwise a single tray instance runs, and a second launch asks it to open settings and exits.
/// </summary>
public static class Program
{
    private const string MutexName = @"Local\ClaudeUsageTray.Mutex";

    [STAThread]
    public static int Main(string[] args)
    {
        if (HasArg(args, "--mcp"))
            return ConsoleModes.RunMcp();
        if (HasArg(args, "--once"))
            return ConsoleModes.RunOnce();

        using Mutex mutex = new(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Autostart while already running: nothing to do. By hand: bring up the running instance's settings.
            if (!HasArg(args, AutoStartManager.TrayArgument))
                InstanceSignal.SignalRunningInstance();
            return 0;
        }
        return RunTray(HasArg(args, AutoStartManager.TrayArgument));
    }

    /// <summary>Separate method so WPF assemblies load only when the tray really starts (JIT of Main does not need them).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunTray(bool startHidden)
    {
        App app = new(startHidden);
        return app.Run();
    }

    private static bool HasArg(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);
}
