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
    private readonly TextBlock _description;
    private readonly TextBlock _range;
    private readonly string _format, _title, _descriptionText;
    private readonly double _min, _max, _typical;
    private string? _providerName;

    public FrameworkElement Element { get; }

    // title und description sind deutsch und werden über L.T übersetzt (auch nach einem Sprachwechsel)
    public ParamRow(string title, string description, double min, double max, double step, double typical, string format)
    {
        (_format, _title, _descriptionText, _min, _max, _typical) = (format, title, description, min, max, typical);
        _enabled = new CheckBox { FontWeight = FontWeights.SemiBold };
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

        _range = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(26, 0, 0, 0) };
        _description = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(26, 2, 0, 0) };

        Element = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 6),
            Children = { _enabled, _description, sliderRow, _range }
        };
        ApplyTexts();
        L.Changed += ApplyTexts;
    }

    private void ApplyTexts()
    {
        _enabled.Content = L.T(_title);
        _description.Text = L.T(_descriptionText);
        _range.Text = L.F("Bereich: {0} bis {1}   ·   üblich: {2}", _min.ToString(_format, CultureInfo.CurrentCulture),
            _max.ToString(_format, CultureInfo.CurrentCulture), _typical.ToString(_format, CultureInfo.CurrentCulture));
        Element?.SetValue(FrameworkElement.ToolTipProperty,
            _supported || _providerName == null ? null : L.F("Wird von {0} nicht unterstützt und deshalb nicht gesendet.", _providerName));
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
        _providerName = providerName;
        _enabled.IsEnabled = supported;
        Element.Opacity = supported ? 1 : 0.45;
        ApplyTexts();
    }

    private bool _supported = true;

    private void Update()
    {
        var on = _enabled.IsChecked == true && _supported;
        _slider.IsEnabled = on;
        _valueText.Text = !_supported ? L.T("nicht unterstützt") : on ? _slider.Value.ToString(_format, CultureInfo.CurrentCulture) : L.T("Standard");
        _valueText.Opacity = on ? 1 : 0.5;
    }
}
