using System.Globalization;
using System.Windows.Threading;
using ClaudeUsageTray.Config;
using ClaudeUsageTray.Config.Enums;
using ClaudeUsageTray.Config.Helpers;
using ClaudeUsageTray.Config.Models;
using ClaudeUsageTray.Localization;
using ClaudeUsageTray.Tray.Enums;
using ClaudeUsageTray.Usage;
using ClaudeUsageTray.Usage.Models;

namespace ClaudeUsageTray.Tray;

/// <summary>
/// Keeps the two tray icons up to date: a <see cref="System.Threading.Timer"/> fetches usage on the thread pool every
/// <see cref="ConfigData.RefreshSeconds"/>, results are marshalled to the dispatcher (tray window thread) where the
/// icons are re-rendered. Owns the icon handles it creates; does not own the config/provider/host/renderer.
/// All members except <see cref="RefreshNow"/> must be called on the dispatcher thread.
/// Timer ticks are skipped while a refresh is still running; a forced refresh may overlap (it queues on the provider).
/// </summary>
public sealed class UsageTrayController : IDisposable
{
    private static readonly UsageIconKind[] _kinds = [UsageIconKind.Session, UsageIconKind.Weekly];

    private readonly AppConfig _config;
    private readonly UsageProvider _usage;
    private readonly TrayIconHost _host;
    private readonly NumberIconRenderer _renderer;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<UsageIconKind, (IntPtr Icon, string Key)> _icons = [];
    private Timer? _timer;
    private int _periodSeconds;
    private int _inFlight;
    private bool _disposed;

    public UsageTrayController(AppConfig config, UsageProvider usage, TrayIconHost host, NumberIconRenderer renderer, Dispatcher dispatcher)
    {
        _config = config;
        _usage = usage;
        _host = host;
        _renderer = renderer;
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the dispatcher thread after each refresh attempt.</summary>
    public event EventHandler? Updated;

    public UsageResult? Latest { get; private set; }

    /// <summary>Error of the last attempt when there was no value to fall back to.</summary>
    public string? LastError { get; private set; }

    /// <summary>True from the start of a refresh until every refresh that was started has finished.</summary>
    public bool IsRefreshing => Volatile.Read(ref _inFlight) > 0;

    public void Start()
    {
        _config.Changed += OnConfigChanged;
        LocalizationManager.Instance.CultureChanged += OnCultureChanged;
        _host.SystemMetricsChanged += OnSystemMetricsChanged;
        _periodSeconds = _config.Current.RefreshSeconds;
        Apply();
        _timer = new Timer(_ => Refresh(force: false), null, TimeSpan.Zero, TimeSpan.FromSeconds(_periodSeconds));
    }

    /// <summary>Fetches bypassing the cache. Any thread.</summary>
    public void RefreshNow() => Refresh(force: true);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
        LocalizationManager.Instance.CultureChanged -= OnCultureChanged;
        _host.SystemMetricsChanged -= OnSystemMetricsChanged;
        _timer?.Dispose();
        foreach ((UsageIconKind kind, (IntPtr icon, _)) in _icons)
        {
            _host.Hide((uint)kind);
            NumberIconRenderer.DestroyIconHandle(icon);
        }
        _icons.Clear();
    }

    private void Refresh(bool force)
    {
        // Count first: a timer tick that finds another refresh running gives its slot back and skips.
        if (Interlocked.Increment(ref _inFlight) > 1 && !force)
        {
            Interlocked.Decrement(ref _inFlight);
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                    Updated?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception)
        {
            // Dispatcher already shut down.
        }
        _ = RefreshAsync(force);
    }

    private async Task RefreshAsync(bool force)
    {
        UsageResult? result = null;
        string? error = null;
        try
        {
            result = await _usage.GetAsync(force);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        Interlocked.Decrement(ref _inFlight);
        try
        {
            await _dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                    return;
                if (result is not null)
                    Latest = result;
                LastError = error;
                Apply();
                Updated?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception)
        {
            // Dispatcher shut down while the request was running.
        }
    }

    private void OnCultureChanged(object? sender, EventArgs e) => Apply();

    /// <summary>DPI, display or system setting changed: icons are re-rendered when their size/content differs.</summary>
    private void OnSystemMetricsChanged(object? sender, EventArgs e) => Apply();

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        int period = _config.Current.RefreshSeconds;
        if (period != _periodSeconds)
        {
            _periodSeconds = period;
            _timer?.Change(TimeSpan.FromSeconds(period), TimeSpan.FromSeconds(period));
        }
        Apply();
    }

    private void Apply()
    {
        if (_disposed)
            return;
        ConfigData config = _config.Current;
        List<UsageWindow> windows = Latest is null ? [] : UsageFormatter.Summarize(Latest.Data);
        int size = NumberIconRenderer.SystemIconSize;
        bool added = false;
        foreach (UsageIconKind kind in _kinds)
        {
            IconSettings settings = kind == UsageIconKind.Session ? config.Session : config.Weekly;
            if (!settings.Visible)
            {
                RemoveIcon(kind);
                continue;
            }

            (string text, uint color, string tip) = Describe(kind, settings, windows);
            uint background = ColorHexHelper.Parse(settings.BackgroundColor, 0);
            string key = string.Join('|', text, color, background, size, tip, config.FontFamily, config.FontBold);
            if (_icons.TryGetValue(kind, out var current) && current.Key == key)
                continue;

            added |= !_icons.ContainsKey(kind);
            IntPtr icon = _renderer.Render(text, color, background, size, config.FontFamily, config.FontBold);
            _host.Show((uint)kind, icon, tip);
            _icons[kind] = (icon, key);
            NumberIconRenderer.DestroyIconHandle(current.Icon);
        }

        // A newly shown icon lands at the end, so re-add both to keep the chosen order.
        // Icons are added in this order. Windows 11 also remembers where each icon was (and any drag by the user),
        // so the shell may keep an earlier position.
        uint[] order = config.IconOrder == IconOrder.WeeklyFirst
            ? [(uint)UsageIconKind.Weekly, (uint)UsageIconKind.Session]
            : [(uint)UsageIconKind.Session, (uint)UsageIconKind.Weekly];
        _host.SetOrder(order, force: added && _icons.Count > 1);
    }

    private void RemoveIcon(UsageIconKind kind)
    {
        if (!_icons.Remove(kind, out var current))
            return;
        _host.Hide((uint)kind);
        NumberIconRenderer.DestroyIconHandle(current.Icon);
    }

    private (string Text, uint Color, string Tip) Describe(UsageIconKind kind, IconSettings settings, List<UsageWindow> windows)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        IconSettings defaults = new();
        uint low = ColorHexHelper.Parse(settings.LowColor, ColorHexHelper.Parse(defaults.LowColor, 0));
        uint mid = ColorHexHelper.Parse(settings.MidColor, ColorHexHelper.Parse(defaults.MidColor, 0));
        uint high = ColorHexHelper.Parse(settings.HighColor, ColorHexHelper.Parse(defaults.HighColor, 0));
        string label = loc[kind == UsageIconKind.Session ? "LabelSession" : "LabelWeekly"];

        if (LastError is not null)
            return ("!", high, loc.Format("TipError", label, LastError));

        string apiKey = kind == UsageIconKind.Session ? UsageFormatter.SessionKey : UsageFormatter.WeeklyKey;
        UsageWindow? window = windows.FirstOrDefault(x => x.Key == apiKey);
        if (window is null)
            return ("?", mid, loc.Format("TipNoData", label));

        double percent = window.UtilizationPercent;
        uint color = percent < settings.LowThreshold ? low : percent < settings.HighThreshold ? mid : high;
        string number = Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 0, 999).ToString(CultureInfo.InvariantCulture);
        string percentText = UsageFormatter.FormatPercent(Math.Round(percent, 1));
        string tip = window.ResetsAt is { } resetsAt
            ? loc.Format("TipUsage", label, percentText, FormatReset(resetsAt, loc.Culture),
                UsageFormatter.FormatRemaining(resetsAt - DateTimeOffset.Now))
            : loc.Format("TipUsageNoReset", label, percentText);
        if (Latest!.IsStale)
        {
            tip += " " + loc["TipStale"];
            // Dim to half opacity so a number that could not be refreshed is visibly different.
            color = (color & 0x00FFFFFF) | ((color >> 24) / 2 << 24);
        }
        return (number, color, tip);
    }

    /// <summary>Time only for today, otherwise weekday + time: short enough for the 127-char tooltip.</summary>
    private static string FormatReset(DateTimeOffset at, CultureInfo culture)
    {
        DateTime local = at.ToLocalTime().DateTime;
        return local.Date == DateTime.Today
            ? local.ToString("t", culture)
            : local.ToString("ddd ", culture) + local.ToString("t", culture);
    }
}
