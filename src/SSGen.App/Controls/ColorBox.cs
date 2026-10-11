using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SSGen.App.Controls;

/// <summary>
/// A colour field: a swatch that opens a palette (brand and document colours first, then a basic set) and a hex
/// box (#RRGGBB or #AARRGGBB). <see cref="Color"/> binds two-way.
/// </summary>
public sealed partial class ColorBox : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorBox),
        new FrameworkPropertyMetadata("#FFFFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorBox)d).Sync()));

    public static readonly DependencyProperty SwatchesProperty = DependencyProperty.Register(
        nameof(Swatches), typeof(IEnumerable<string>), typeof(ColorBox), new PropertyMetadata(null));

    private static readonly string[] Basic =
    [
        "#FFFFFFFF", "#FFF3F4F6", "#FF9CA3AF", "#FF4B5563", "#FF111827", "#FF000000",
        "#FFEF4444", "#FFF97316", "#FFF59E0B", "#FFFACC15", "#FF22C55E", "#FF14B8A6",
        "#FF0EA5E9", "#FF3B82F6", "#FF6366F1", "#FF8B5CF6", "#FFEC4899", "#FFF43F5E",
    ];

    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Popup _popup;
    private readonly WrapPanel _palette;

    public ColorBox()
    {
        _swatch = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var open = new Button { Content = _swatch, Padding = new Thickness(2), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Choose a colour" };
        AutomationProperties.SetName(open, "Choose a colour");
        open.Click += (_, _) => OpenPalette();

        _hex = new TextBox { MinWidth = 96, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_hex, "Colour hex value");
        _hex.LostFocus += (_, _) => Commit();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
            }
        };

        _palette = new WrapPanel { Width = 6 * 30 };
        var paletteBorder = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = _palette };
        paletteBorder.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        paletteBorder.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        _popup = new Popup { PlacementTarget = open, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = paletteBorder };

        var panel = new DockPanel();
        DockPanel.SetDock(open, Dock.Left);
        panel.Children.Add(open);
        panel.Children.Add(_hex);
        panel.Children.Add(_popup);
        Content = panel;
        Sync();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Colours offered first (brand palette, colours already in the image).</summary>
    public IEnumerable<string>? Swatches
    {
        get => (IEnumerable<string>?)GetValue(SwatchesProperty);
        set => SetValue(SwatchesProperty, value);
    }

    public static bool IsHex(string? s) => s is not null && HexPattern().IsMatch(s);

    public static Color ToMediaColor(string? hex)
    {
        if (!IsHex(hex))
        {
            return Colors.Transparent;
        }

        var v = Convert.ToUInt32(hex![1..], 16);
        return hex.Length == 7
            ? System.Windows.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : System.Windows.Media.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private void Sync()
    {
        if (_hex is null)
        {
            return;
        }

        _hex.Text = Color;
        _swatch.Background = new SolidColorBrush(ToMediaColor(Color));
    }

    private void Commit()
    {
        var text = _hex.Text.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (IsHex(text))
        {
            Color = text.ToUpperInvariant();
        }
        else
        {
            _hex.Text = Color; // not a colour: put the current value back
        }
    }

    private void OpenPalette()
    {
        _palette.Children.Clear();
        var colours = (Swatches ?? []).Where(IsHex).Select(c => c.ToUpperInvariant()).Concat(Basic).Distinct().Take(36);
        foreach (var colour in colours)
        {
            var chip = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(ToMediaColor(colour)),
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            var button = new Button { Content = chip, Padding = new Thickness(0), Margin = new Thickness(4), MinWidth = 0, MinHeight = 0, ToolTip = colour };
            AutomationProperties.SetName(button, colour);
            var value = colour;
            button.Click += (_, _) =>
            {
                Color = value;
                _popup.IsOpen = false;
            };
            _palette.Children.Add(button);
        }

        _popup.IsOpen = true;
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexPattern();
}
