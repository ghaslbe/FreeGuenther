using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LmChat;

// Eine Einstellung mit Haken ("eigenen Wert verwenden"), Erklärung und Schieberegler.
// Der Regler lässt nur gültige Werte im erlaubten Bereich zu.
internal sealed class ParamRow
{
    private readonly CheckBox _enabled;
    private readonly Slider _slider;
    private readonly TextBlock _valueText;
    private readonly string _format;

    public FrameworkElement Element { get; }

    public ParamRow(string title, string description, double min, double max, double step, double typical, string format)
    {
        _format = format;
        _enabled = new CheckBox { Content = title, FontWeight = FontWeights.SemiBold };
        _slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Value = typical,
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _valueText = new TextBlock
        {
            Width = 120,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };

        _enabled.Checked += (_, _) => Update();
        _enabled.Unchecked += (_, _) => Update();
        _slider.ValueChanged += (_, _) => Update();

        var sliderRow = new Grid { Margin = new Thickness(26, 4, 0, 0) };
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition());
        sliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sliderRow.Children.Add(_slider);
        Grid.SetColumn(_valueText, 1);
        sliderRow.Children.Add(_valueText);

        var range = new TextBlock
        {
            Text = $"Bereich: {min.ToString(format, CultureInfo.CurrentCulture)} bis {max.ToString(format, CultureInfo.CurrentCulture)}" +
                   $"   ·   üblich: {typical.ToString(format, CultureInfo.CurrentCulture)}",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(26, 0, 0, 0)
        };

        Element = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 6),
            Children =
            {
                _enabled,
                new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                    Margin = new Thickness(26, 2, 0, 0)
                },
                sliderRow,
                range
            }
        };
        Update();
    }

    // null = kein Haken = Standardwert des Modells
    public double? Value
    {
        get => _enabled.IsChecked == true ? Math.Round(_slider.Value, 3) : null;
        set
        {
            _enabled.IsChecked = value.HasValue;
            if (value is { } v)
                _slider.Value = Math.Clamp(v, _slider.Minimum, _slider.Maximum);
            Update();
        }
    }

    // Manche Anbieter kennen nicht jede Einstellung – dann ausgegraut mit Hinweis statt Fehler beim Senden
    public void SetSupported(bool supported, string providerName)
    {
        _supported = supported;
        _enabled.IsEnabled = supported;
        Element.Opacity = supported ? 1 : 0.45;
        Element.ToolTip = supported ? null : $"Wird von {providerName} nicht unterstützt und deshalb nicht gesendet.";
        Update();
    }

    private bool _supported = true;

    private void Update()
    {
        var on = _enabled.IsChecked == true && _supported;
        _slider.IsEnabled = on;
        _valueText.Text = !_supported ? "nicht unterstützt" : on ? _slider.Value.ToString(_format, CultureInfo.CurrentCulture) : "Standard";
        _valueText.Opacity = on ? 1 : 0.5;
    }
}
