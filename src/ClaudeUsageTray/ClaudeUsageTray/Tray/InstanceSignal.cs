using System.Runtime.InteropServices;

namespace ClaudeUsageTray.Tray;

/// <summary>
/// "Open settings" request from a second launch to the running instance: a registered message broadcast to every
/// top-level window; only the running instance's hidden tray window knows it. Kept apart from
/// <see cref="TrayIconHost"/> so the second process never loads WPF.
/// </summary>
public static class InstanceSignal
{
    public const string MessageName = "ClaudeUsageTray.ShowSettings";

    private static readonly IntPtr HwndBroadcast = 0xFFFF;

    public static int RegisterMessage() => RegisterWindowMessage(MessageName);

    public static void SignalRunningInstance() => PostMessage(HwndBroadcast, RegisterMessage(), IntPtr.Zero, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
