using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClaudeUsageTray.Localization;

namespace ClaudeUsageTray.Tray;

/// <summary>
/// Tray icons through <c>Shell_NotifyIcon</c> directly (no WinForms/System.Drawing). One hidden message window serves
/// all icons (told apart by <c>uID</c>). Double-click opens settings; right-click shows a native menu.
/// Remembers what is shown so the icons come back when Explorer restarts. Icon handles are NOT owned: the caller
/// keeps each handle alive while it is shown and destroys it after replacing it.
/// The same window receives the "open settings" request of a second launch (<see cref="InstanceSignal"/>).
/// Uses WPF core only (<see cref="HwndSource"/>), not WPF-UI or the settings window.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint CommandSettings = 1, CommandRefresh = 2, CommandExit = 3;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const int WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, WmNull = 0;
    private const int WmSettingChange = 0x001A, WmDisplayChange = 0x007E, WmDpiChanged = 0x02E0;
    private const uint MsgfltAllow = 1;
    private const uint MfString = 0x0, MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100, TpmNoNotify = 0x80;
    /// <summary><c>szTip</c> holds 128 chars including the terminating null.</summary>
    public const int MaxTipLength = 127;

    private readonly Action _openSettings;
    private readonly Action _refreshNow;
    private readonly Action _exit;
    private readonly HwndSource _messageWindow;
    private readonly int _taskbarCreatedMessage;
    private readonly int _showMessage;
    private readonly Dictionary<uint, (IntPtr Icon, string Tip)> _shown = [];
    private bool _disposed;

    public TrayIconHost(Action openSettings, Action refreshNow, Action exit)
    {
        _openSettings = openSettings;
        _refreshNow = refreshNow;
        _exit = exit;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _showMessage = InstanceSignal.RegisterMessage();

        // Top-level but never shown (message-only windows do not receive the TaskbarCreated broadcast).
        _messageWindow = new HwndSource(new HwndSourceParameters(nameof(TrayIconHost)) { Width = 0, Height = 0, WindowStyle = 0 });
        _messageWindow.AddHook(WndProc);

        // An elevated run would otherwise drop these messages sent by the (non-elevated) shell / second launch.
        ChangeWindowMessageFilterEx(_messageWindow.Handle, (uint)_taskbarCreatedMessage, MsgfltAllow, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_messageWindow.Handle, (uint)_showMessage, MsgfltAllow, IntPtr.Zero);
    }

    /// <summary>Raised on the window thread when DPI, display layout or a system setting changed (icons may need a new size).</summary>
    public event EventHandler? SystemMetricsChanged;

    /// <summary>Adds the icon or updates it in place.</summary>
    public void Show(uint id, IntPtr icon, string tip)
    {
        if (tip.Length > MaxTipLength)
            tip = tip[..(MaxTipLength - 1)] + "…";
        bool existed = _shown.ContainsKey(id);
        _shown[id] = (icon, tip);
        NotifyIconData data = CreateData(id, NifMessage | NifIcon | NifTip, icon, tip);
        if (existed)
        {
            if (!Shell_NotifyIcon(NimModify, ref data))
                Shell_NotifyIcon(NimAdd, ref data);
        }
        else if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    public void Hide(uint id)
    {
        if (!_shown.Remove(id))
            return;
        NotifyIconData data = CreateData(id, 0, IntPtr.Zero, "");
        Shell_NotifyIcon(NimDelete, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (uint id in _shown.Keys.ToList())
            Hide(id);
        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
    }

    private void AddAllInOrder()
    {
        foreach (uint id in _shown.Keys.Order())
        {
            (IntPtr icon, string tip) = _shown[id];
            NotifyIconData data = CreateData(id, NifMessage | NifIcon | NifTip, icon, tip);
            if (!Shell_NotifyIcon(NimAdd, ref data))
                Shell_NotifyIcon(NimModify, ref data);
        }
    }

    private NotifyIconData CreateData(uint id, uint flags, IntPtr icon, string tip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _messageWindow.Handle,
        uID = id,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tip,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            AddAllInOrder();
            return IntPtr.Zero;
        }
        if (msg == WmDpiChanged || msg == WmSettingChange || msg == WmDisplayChange)
        {
            SystemMetricsChanged?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        if (msg != CallbackMessage && msg != _showMessage)
            return IntPtr.Zero;

        handled = true;
        try
        {
            if (msg == _showMessage)
            {
                _openSettings();
                return IntPtr.Zero;
            }
            switch ((int)lParam)
            {
                case WmLButtonDblClk:
                    _openSettings();
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    break;
            }
        }
        catch (Exception ex)
        {
            // Exceptions must not unwind through the window procedure: report and keep the tray alive.
            // Deferred: a modal box inside the window procedure would run a nested message loop in the middle of it.
            string text = ex.ToString();
            _messageWindow.Dispatcher.BeginInvoke(() =>
                System.Windows.MessageBox.Show(text, App.DisplayName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error));
        }
        return IntPtr.Zero;
    }

    /// <summary>Built per opening, so it always has the current language and holds nothing while closed.</summary>
    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        uint command;
        try
        {
            LocalizationManager loc = LocalizationManager.Instance;
            AppendMenu(menu, MfString, CommandRefresh, loc["TrayRefresh"]);
            AppendMenu(menu, MfString, CommandSettings, loc["TraySettings"]);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CommandExit, loc["TrayExit"]);
            SetMenuDefaultItem(menu, CommandSettings, 0);

            GetCursorPos(out Point point);
            // Foreground + WM_NULL afterwards: the documented way to make the menu close when clicking elsewhere.
            SetForegroundWindow(_messageWindow.Handle);
            command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, point.X, point.Y, _messageWindow.Handle, IntPtr.Zero);
            PostMessage(_messageWindow.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }

        switch (command)
        {
            case CommandSettings:
                _openSettings();
                break;
            case CommandRefresh:
                _refreshNow();
                break;
            case CommandExit:
                _exit();
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
