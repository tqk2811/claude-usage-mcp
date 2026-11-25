using System.Windows;
using System.Windows.Controls;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Config.Models;
using ClaudeUsageTray.Localization;
using ClaudeUsageTray.Tray.Enums;

namespace ClaudeUsageTray.UI.Controls;

/// <summary>Settings of one tray icon (visibility, thresholds, colors); every change is saved and applied live.</summary>
public partial class IconSettingsPanel : UserControl
{
    private AppConfig? _config;
    private UsageIconKind _kind;
    private bool _loading;

    public IconSettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised after a change was saved (or failed to save, with the exception).</summary>
    public event EventHandler<Exception?>? Saved;

    public void Bind(AppConfig config, UsageIconKind kind, string titleKey)
    {
        _config = config;
        _kind = kind;
        TitleText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding($"[{titleKey}]")
        {
            Source = LocalizationManager.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay,
        });
        Reload();
    }

    public void Reload()
    {
        IconSettings s = Current;
        _loading = true;
        VisibleToggle.IsChecked = s.Visible;
        LowBox.Value = s.LowThreshold;
        HighBox.Value = s.HighThreshold;
        LowColorBox.Color = s.LowColor;
        MidColorBox.Color = s.MidColor;
        HighColorBox.Color = s.HighColor;
        BackgroundColorBox.Color = s.BackgroundColor;
        _loading = false;
    }

    private IconSettings Current => _kind == UsageIconKind.Session ? _config!.Current.Session : _config!.Current.Weekly;

    private void OnVisibleClick(object sender, RoutedEventArgs e)
        => Change(s => s with { Visible = VisibleToggle.IsChecked == true });

    private void OnThresholdChanged(object sender, RoutedEventArgs e)
    {
        if (LowBox.Value is not double low || HighBox.Value is not double high)
            return;
        Change(s => s with { LowThreshold = (int)Math.Round(low), HighThreshold = (int)Math.Round(high) });
    }

    private void OnColorChanged(object? sender, EventArgs e) => Change(s => s with
    {
        LowColor = LowColorBox.Color,
        MidColor = MidColorBox.Color,
        HighColor = HighColorBox.Color,
        BackgroundColor = BackgroundColorBox.Color,
    });

    /// <summary>Back to this icon's default thresholds and colors; visibility is kept.</summary>
    private void OnResetClick(object sender, RoutedEventArgs e) => Change(s =>
    {
        IconSettings defaults = _kind == UsageIconKind.Session ? new IconSettings() : IconSettings.CreateWeeklyDefault();
        return defaults with { Visible = s.Visible };
    });

    private void Change(Func<IconSettings, IconSettings> change)
    {
        if (_loading || _config is null)
            return;
        Exception? error = null;
        try
        {
            _config.Update(c =>
            {
                bool isSession = _kind == UsageIconKind.Session;
                IconSettings next = change(isSession ? c.Session : c.Weekly);
                IconSettings other = isSession ? c.Weekly : c.Session;
                // At least one icon stays shown, otherwise the tray app has nothing to click.
                if (!next.Visible && !other.Visible)
                    next = next with { Visible = true };
                return isSession ? c with { Session = next } : c with { Weekly = next };
            });
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Reload(); // the config may have clamped the values (e.g. high below low)
        Saved?.Invoke(this, error);
    }
}
