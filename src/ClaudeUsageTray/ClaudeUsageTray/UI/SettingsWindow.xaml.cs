using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Config.Enums;
using ClaudeUsageTray.Localization;
using ClaudeUsageTray.Mcp;
using ClaudeUsageTray.Mcp.Models;
using ClaudeUsageTray.Startup;
using ClaudeUsageTray.Tray;
using ClaudeUsageTray.Tray.Enums;
using ClaudeUsageTray.Usage;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ClaudeUsageTray.UI;

/// <summary>
/// Settings window. Closing really closes it (the app builds a new one per opening, then unloads WPF-UI and trims),
/// so it unhooks everything it attached to long-lived objects in <see cref="OnClosed"/>.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly AppConfig _config;
    private readonly UsageTrayController _controller;
    private readonly AutoStartManager _autoStart;
    private readonly ClaudeCliRegistrar _registrar;
    private readonly ThemeController _theme;
    private bool _loading;
    private bool _closed;

    public SettingsWindow(AppConfig config, UsageTrayController controller, AutoStartManager autoStart, ClaudeCliRegistrar registrar)
    {
        _config = config;
        _controller = controller;
        _autoStart = autoStart;
        _registrar = registrar;
        _theme = new ThemeController(this);
        InitializeComponent();

        _loading = true;
        SessionPanel.Bind(config, UsageIconKind.Session, "SessionIconHeader");
        WeeklyPanel.Bind(config, UsageIconKind.Weekly, "WeeklyIconHeader");
        SessionPanel.Saved += OnPanelSaved;
        WeeklyPanel.Saved += OnPanelSaved;
        IntervalBox.Value = config.Current.RefreshSeconds;
        AutoStartToggle.IsChecked = _autoStart.IsEnabled;
        LanguageBox.ItemsSource = LocalizationManager.SupportedCultures;
        LanguageBox.SelectedItem = LocalizationManager.SupportedCultures.First(x => x.Name == LocalizationManager.Instance.Culture.Name);
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == config.Current.Theme.ToString());
        OrderBox.SelectedItem = OrderBox.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == config.Current.IconOrder.ToString());
        List<string> fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(x => x.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!fonts.Contains(config.Current.FontFamily, StringComparer.OrdinalIgnoreCase))
            fonts.Insert(0, config.Current.FontFamily);
        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = fonts.First(x => string.Equals(x, config.Current.FontFamily, StringComparison.OrdinalIgnoreCase));
        BoldToggle.IsChecked = config.Current.FontBold;
        CommandBox.Text = _registrar.RegisterCommand;
        _loading = false;

        _controller.Updated += OnUsageUpdated;
        LocalizationManager.Instance.CultureChanged += OnCultureChanged;
        UpdateStatus();
        UpdateBothHidden();

        // System mode needs the window handle to watch Windows theme changes, so apply again once loaded.
        _theme.Apply(_config.Current.Theme);
        Loaded += (_, _) =>
        {
            _theme.Apply(_config.Current.Theme);
            _ = CheckMcpStatusAsync();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Here, not in OnClosed: unwatching needs the window handle, which is gone by then.
        if (!e.Cancel)
            _theme.Detach();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _controller.Updated -= OnUsageUpdated;
        LocalizationManager.Instance.CultureChanged -= OnCultureChanged;
        base.OnClosed(e);
    }

    private void OnUsageUpdated(object? sender, EventArgs e) => UpdateStatus();

    private void OnCultureChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        RefreshButton.IsEnabled = !_controller.IsRefreshing;
        if (_controller.LastError is { } error)
        {
            StatusText.Text = "Error: " + error;
            return;
        }
        if (_controller.Latest is not { } latest)
        {
            StatusText.Text = loc["StatusLoading"];
            return;
        }
        string text = UsageFormatter.ToText(UsageFormatter.Summarize(latest.Data), latest.Data);
        StatusText.Text = text + "\n" + loc.Format("StatusUpdated", latest.FetchedAt.ToLocalTime().ToString("T", loc.Culture));
    }

    private void UpdateBothHidden()
        => BothHiddenText.Visibility = !_config.Current.Session.Visible && !_config.Current.Weekly.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnPanelSaved(object? sender, Exception? error)
    {
        if (error is not null)
            ShowSaveError(error);
        UpdateBothHidden();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _controller.RefreshNow();

    private void OnIntervalChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || IntervalBox.Value is not double seconds)
            return;
        TrySave(c => c with { RefreshSeconds = (int)Math.Round(seconds) });
        IntervalBox.Value = _config.Current.RefreshSeconds; // clamped to 30..86400
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _autoStart.SetEnabled(AutoStartToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            ShowSaveError(ex);
        }
        AutoStartToggle.IsChecked = _autoStart.IsEnabled;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not CultureInfo culture || culture.Name == LocalizationManager.Instance.Culture.Name)
            return;
        LocalizationManager.Instance.Culture = culture;
        TrySave(c => c with { Language = culture.Name });
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse(tag, out ThemeMode mode))
            return;
        _theme.Apply(mode);
        TrySave(c => c with { Theme = mode });
    }

    private void OnOrderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OrderBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse(tag, out IconOrder order))
            return;
        TrySave(c => c with { IconOrder = order });
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontBox.SelectedItem is not string family)
            return;
        TrySave(c => c with { FontFamily = family });
    }

    private void OnBoldClick(object sender, RoutedEventArgs e) => TrySave(c => c with { FontBold = BoldToggle.IsChecked == true });

    private void TrySave(Func<Config.Models.ConfigData, Config.Models.ConfigData> change)
    {
        try
        {
            _config.Update(change);
        }
        catch (Exception ex)
        {
            ShowSaveError(ex);
        }
    }

    private void ShowSaveError(Exception ex)
        => MessageBox.Show(this, ex.Message, LocalizationManager.Instance["ErrorSaveConfig"], MessageBoxButton.OK, MessageBoxImage.Error);

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CommandBox.Text);
            CopyButton.Content = LocalizationManager.Instance["Copied"];
        }
        catch (Exception ex)
        {
            ShowMcpOutput(ex.Message);
        }
    }

    private void OnRegisterClick(object sender, RoutedEventArgs e) => _ = RunCliAsync(_registrar.RegisterAsync, showOutput: true);

    private void OnUnregisterClick(object sender, RoutedEventArgs e) => _ = RunCliAsync(_registrar.UnregisterAsync, showOutput: true);

    private void OnStatusClick(object sender, RoutedEventArgs e) => _ = CheckMcpStatusAsync();

    private async Task CheckMcpStatusAsync()
    {
        CliResult? result = await RunCliAsync(_registrar.GetStatusAsync, showOutput: false);
        if (_closed || result is null)
            return;
        LocalizationManager loc = LocalizationManager.Instance;
        if (result.ExitCode != 0)
        {
            McpStatusText.Text = loc["McpStatusNotRegistered"];
            return;
        }
        string? scope = ClaudeCliRegistrar.ParseScope(result.Output);
        McpStatusText.Text = scope is null ? loc["McpStatusRegistered"] : loc.Format("McpStatusRegisteredScope", scope);
    }

    /// <summary>Runs one CLI action off the UI thread (the process API is awaited), buttons disabled meanwhile.</summary>
    private async Task<CliResult?> RunCliAsync(Func<Task<CliResult?>> action, bool showOutput)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        SetMcpBusy(true);
        McpStatusText.Text = loc["McpRunning"];
        try
        {
            CliResult? result = await Task.Run(action);
            if (_closed)
                return result;
            if (result is null)
            {
                McpStatusText.Text = loc["McpClaudeNotFound"];
                return null;
            }
            McpStatusText.Text = "";
            if (showOutput)
            {
                ShowMcpOutput($"exit {result.ExitCode}\n{result.Output}");
                _ = CheckMcpStatusAsync();
            }
            return result;
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                McpStatusText.Text = "";
                ShowMcpOutput(ex.Message);
            }
            return null;
        }
        finally
        {
            if (!_closed)
                SetMcpBusy(false);
        }
    }

    private void SetMcpBusy(bool busy)
    {
        RegisterButton.IsEnabled = !busy;
        UnregisterButton.IsEnabled = !busy;
        StatusButton.IsEnabled = !busy;
    }

    private void ShowMcpOutput(string text)
    {
        McpOutputBox.Text = text;
        McpOutputBox.Visibility = Visibility.Visible;
    }
}
