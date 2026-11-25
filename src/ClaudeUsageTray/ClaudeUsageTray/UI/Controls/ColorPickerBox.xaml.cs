using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeUsageTray.Config.Helpers;

namespace ClaudeUsageTray.UI.Controls;

/// <summary>Color field: preset swatches in a popup plus a <c>#AARRGGBB</c> text box.</summary>
public partial class ColorPickerBox : UserControl
{
    private static readonly string[] _presets =
    [
        "#00000000", "#FFFFFFFF", "#FF000000", "#FF9CA3AF", "#FF4B5563",
        "#FF22C55E", "#FF84CC16", "#FFF59E0B", "#FFF97316", "#FFEF4444",
        "#FFEC4899", "#FFA855F7", "#FF3B82F6", "#FF06B6D4", "#FF14B8A6",
    ];

    private string _color = "#00000000";

    public ColorPickerBox()
    {
        InitializeComponent();
        foreach (string preset in _presets)
        {
            Button button = new()
            {
                Width = 32,
                Height = 32,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                ToolTip = preset,
                Tag = preset,
                Content = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray,
                    Background = ToBrush(ColorHexHelper.Parse(preset, 0)),
                },
            };
            button.Click += OnPresetClick;
            PresetPanel.Children.Add(button);
        }
        ShowColor();
    }

    /// <summary>Raised when the user picks or types a valid color.</summary>
    public event EventHandler? ColorChanged;

    public string Color
    {
        get => _color;
        set
        {
            _color = ColorHexHelper.Format(ColorHexHelper.Parse(value, 0));
            ShowColor();
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e) => PresetPopup.IsOpen = true;

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        PresetPopup.IsOpen = false;
        Commit((string)((Button)sender).Tag);
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => Commit(HexBox.Text);

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Commit(HexBox.Text);
    }

    private void Commit(string text)
    {
        if (!ColorHexHelper.TryParse(text, out uint argb))
        {
            ShowColor(); // invalid: back to the last good value
            return;
        }
        string formatted = ColorHexHelper.Format(argb);
        bool changed = formatted != _color;
        _color = formatted;
        ShowColor();
        if (changed)
            ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowColor()
    {
        HexBox.Text = _color;
        Preview.Fill = ToBrush(ColorHexHelper.Parse(_color, 0));
    }

    private static SolidColorBrush ToBrush(uint argb)
        => new(System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
