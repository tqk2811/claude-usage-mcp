using System.Windows;
using System.Windows.Threading;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Config.Enums;
using ClaudeUsageTray.Diagnostics;
using ClaudeUsageTray.Localization;
using ClaudeUsageTray.Mcp;
using ClaudeUsageTray.Startup;
using ClaudeUsageTray.Tray;
using ClaudeUsageTray.UI;
using ClaudeUsageTray.Usage;
using Wpf.Ui.Markup;

namespace ClaudeUsageTray;

/// <summary>
/// Tray-resident app (code only, no App.xaml: <see cref="Program"/> owns Main). Started with <c>--tray</c> it stays
/// in the tray; otherwise it also opens settings. The idle tray uses WPF core (<see cref="Application"/>, the
/// <c>HwndSource</c> message window, <see cref="Dispatcher"/>), which stays loaded for the whole run. Only the settings
/// window and the WPF-UI dictionaries are created on demand: closing the window releases them and trims the working set.
/// </summary>
public sealed class App : Application
{
    public const string DisplayName = "Claude Usage Tray";

    private readonly bool _startHidden;
    private readonly FileLogger _log = new(FileLogger.DefaultDirectory);
    private AppConfig? _config;
    private UsageProvider? _usage;
    private TrayIconHost? _host;
    private UsageTrayController? _controller;
    private AutoStartManager? _autoStart;
    private ClaudeCliRegistrar? _registrar;
    private McpHttpServer? _mcpServer;
    private SettingsWindow? _window;
    private bool _exiting;

    public App(bool startHidden)
    {
        _startHidden = startHidden;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log.Info($"Start {typeof(App).Assembly.GetName().Version} (hidden: {_startHidden})");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            _config = new AppConfig(AppConfig.DefaultFilePath);
            Exception? loadError = null;
            try
            {
                _config.Load();
            }
            catch (Exception ex)
            {
                loadError = ex;
                _log.Error($"Config load failed: {_config.FilePath}", ex);
            }
            LocalizationManager loc = LocalizationManager.Instance;
            loc.Culture = LocalizationManager.Resolve(_config.Current.Language);
            if (loadError is not null)
            {
                MessageBox.Show(loc.Format("ConfigLoadFailed", _config.FilePath, loadError.Message),
                    DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            string exePath = Environment.ProcessPath!;
            _autoStart = new AutoStartManager(exePath);
            _registrar = new ClaudeCliRegistrar(_config);
            _usage = new UsageProvider(new UsageApiClient(new CredentialReader()));
            // Same provider as the tray icons, so MCP calls share the 30s cache. A failed listen only logs.
            _mcpServer = new McpHttpServer(new McpRequestHandler(_usage), _log);
            _mcpServer.Start(_config.Current.McpPort);
            _config.Changed += OnConfigChanged;
            _host = new TrayIconHost(ShowSettings, () => _controller?.RefreshNow(), ExitApp);
            _controller = new UsageTrayController(_config, _usage, _host, new NumberIconRenderer(), Dispatcher, _log);
            _controller.Updated += OnFirstUsage;
            _controller.Start();
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed", ex);
            MessageBox.Show(ex.ToString(), DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (_startHidden)
            ScheduleTrim();
        else
            ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mcpServer?.Dispose();
        _controller?.Dispose();
        _host?.Dispose();
        _usage?.Dispose();
        _log.Info($"Exit (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }

    private void ShowSettings()
    {
        if (_exiting)
            return;
        if (_window is null)
        {
            LoadUiDictionaries();
            _window = new SettingsWindow(_config!, _controller!, _autoStart!, _registrar!, _mcpServer!);
            _window.Closed += OnWindowClosed;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Restarts the MCP server when the port setting changed.</summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        if (_mcpServer!.Port != _config!.Current.McpPort)
            _mcpServer.Start(_config.Current.McpPort);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window!.Closed -= OnWindowClosed;
        _window = null;
        if (_exiting)
            return;
        UnloadUiDictionaries();
        ScheduleTrim();
    }

    /// <summary>WPF-UI themes and control styles, merged only while the window is alive (they are large).</summary>
    private void LoadUiDictionaries()
    {
        ThemeMode mode = _config!.Current.Theme;
        var theme = mode == ThemeMode.Dark
            || (mode == ThemeMode.System
                && Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() == Wpf.Ui.Appearance.SystemTheme.Dark)
            ? Wpf.Ui.Appearance.ApplicationTheme.Dark
            : Wpf.Ui.Appearance.ApplicationTheme.Light;
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
    }

    private void UnloadUiDictionaries()
    {
        // The theme manager swaps the themes dictionary for a new instance and writes accent brushes into the root
        // dictionary, so clear everything, not only what was merged here.
        Resources.MergedDictionaries.Clear();
        Resources.Clear();
    }

    /// <summary>Trims once the dispatcher is idle, after the closed window's last messages are processed.</summary>
    private void ScheduleTrim()
        => Dispatcher.BeginInvoke(() =>
        {
            if (_window is null && !_exiting)
                MemoryTrimmer.Trim();
        }, DispatcherPriority.ApplicationIdle);

    /// <summary>The first successful fetch loads HTTP/TLS/JSON code; trim once it is done so that does not stay in the working set.</summary>
    private void OnFirstUsage(object? sender, EventArgs e)
    {
        if (_controller!.IsRefreshing || _controller.Latest is null)
            return;
        _controller.Updated -= OnFirstUsage;
        ScheduleTrim();
    }

    private void ExitApp()
    {
        _exiting = true;
        _window?.Close();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(e.Exception.ToString(), DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => _log.Error($"Unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject);
}
