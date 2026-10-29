using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LmChat;

// Zustimmung beim ersten Start: Nur mit „JA“ (Schreibweise egal) geht es weiter, sonst wird die App beendet
internal sealed class ConsentWindow : Window
{
    private readonly TextBox _answer = new() { FontSize = 15, Margin = new Thickness(0, 6, 0, 0) };

    public ConsentWindow()
    {
        Title = "FreeGuenther – Zustimmung";
        ThemeMode = ThemeMode.System;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico"));
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        System.Windows.Automation.AutomationProperties.SetName(_answer, "Zustimmung");

        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Margin = new Thickness(0, 0, 0, 14) };
        info.Inlines.Add(new Run("FreeGuenther ist kostenlose Software und wird ohne jede Gewährleistung bereitgestellt (MIT-Lizenz). " +
                                 "Antworten von KI-Modellen können falsch sein – prüfe wichtige Informationen selbst. Bei Online-Anbietern " +
                                 "gehen deine Nachrichten an den Anbieter, und es können Kosten entstehen."));
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var statement = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF5, 0x9E, 0x0B)),
            Child = new TextBlock
            {
                Text = "Ich stimme zu, dass ich diese Anwendung auf meine eigene Verantwortung nutze.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold
            }
        };

        var ok = new Button { Content = "Weiter", IsDefault = true, MinWidth = 110, Padding = new Thickness(0, 6, 0, 6) };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) => Confirm();
        var quit = new Button { Content = "Beenden", IsCancel = true, MinWidth = 110, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(8, 0, 0, 0) };

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 22),
            Children =
            {
                new TextBlock { Text = "Willkommen bei FreeGuenther", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) },
                info,
                statement,
                new TextBlock { Text = "Gib JA in das folgende Feld ein (Groß- und Kleinschreibung egal):", Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap },
                _answer,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0),
                    Children = { ok, quit }
                }
            }
        };
        Loaded += (_, _) => _answer.Focus();
    }

    // Nur „ja“ zählt als Zustimmung – jede andere Eingabe beendet die App (DialogResult false)
    private void Confirm()
    {
        if (_answer.Text.Trim().Equals("ja", StringComparison.OrdinalIgnoreCase))
        {
            DialogResult = true;
            return;
        }
        MessageBox.Show(this, "Ohne Zustimmung kann FreeGuenther nicht genutzt werden. Die Anwendung wird jetzt beendet.",
            "FreeGuenther", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = false;
    }
}
