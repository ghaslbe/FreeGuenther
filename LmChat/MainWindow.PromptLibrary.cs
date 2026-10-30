using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LmChat;

// Ein gespeicherter Prompt der Datei- bzw. E-Mail-Automatik
public sealed class SavedPrompt
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";

    [JsonIgnore]
    public string Preview => Text.ReplaceLineEndings(" ");
}

// Knopf „Gespeicherte Prompts“ unter dem Prompt beider Automatiken: Liste zum Übernehmen, Speichern unter einem Namen
// und Löschen. Gespeichert unter settings → automationPrompts bzw. mailPrompts; solange nichts gespeichert wurde,
// gelten die mitgelieferten Beispiele (in der jeweiligen Sprache).
public partial class MainWindow
{
    private const string FilePromptsKey = "automationPrompts", MailPromptsKey = "mailPrompts";

    private static List<SavedPrompt> FilePromptDefaults() =>
    [
        new() { Name = L.T("Rechnung auslesen (JSON)"), Text = L.T(
            "Lies die Rechnung aus und gib die Daten als JSON-Objekt mit diesen Feldern zurück: " +
            "rechnungssteller (name, adresse, ust_id), rechnungsempfaenger (name, adresse), rechnungsnummer, rechnungsdatum, " +
            "leistungsdatum, faellig_am, positionen (Liste mit beschreibung, menge, einzelpreis, gesamtpreis), netto, " +
            "ust_satz, ust_betrag, brutto, waehrung, iban, bic. Datumsangaben im Format JJJJ-MM-TT, Beträge als Zahl mit Punkt. " +
            "Fehlt eine Angabe in der Rechnung, setze null – erfinde nichts.") },
        new() { Name = L.T("Rechnung – Kurzübersicht"), Text = L.T(
            "Erstelle eine kurze Übersicht der Rechnung, je Angabe eine Zeile: Rechnungssteller, Rechnungsempfänger, " +
            "Rechnungsnummer, Rechnungsdatum, Bruttobetrag mit Währung, fällig am, Bankverbindung. " +
            "Fehlt eine Angabe, schreibe „nicht angegeben“.") },
        new() { Name = L.T("Dokument zusammenfassen"), Text = L.T(
            "Um welche Art von Dokument handelt es sich (z. B. Rechnung, Vertrag, Brief, Angebot)? " +
            "Nenne Absender, Datum und Betreff und fasse den Inhalt in höchstens 5 Stichpunkten zusammen. " +
            "Erwähne Fristen und Beträge, falls vorhanden.") },
    ];

    private static List<SavedPrompt> MailPromptDefaults() =>
    [
        new() { Name = L.T("Eingang bestätigen"), Text = L.T(
            "Bedanke dich freundlich für die Nachricht, bestätige den Eingang und kündige eine Antwort in den nächsten Tagen an. " +
            "Kurz und höflich, ohne inhaltliche Zusagen.") },
        new() { Name = L.T("Anfrage beantworten"), Text = L.T(
            "Beantworte die Fragen aus der E-Mail sachlich und freundlich. Wo dir eine Information fehlt, " +
            "setze einen Platzhalter wie [BITTE ERGÄNZEN: Liefertermin] ein – erfinde keine Fakten, Preise oder Termine.") },
        new() { Name = L.T("Rechnung erhalten"), Text = L.T(
            "Bestätige den Eingang der Rechnung. Nenne Rechnungsnummer und Betrag, falls sie in der E-Mail oder im Anhang stehen, " +
            "und teile mit, dass die Rechnung geprüft und innerhalb der Zahlungsfrist beglichen wird.") },
    ];

    private List<SavedPrompt> LoadPrompts(string key, Func<List<SavedPrompt>> defaults)
    {
        try
        {
            if (_store.GetSetting(key) is { } json && JsonSerializer.Deserialize<List<SavedPrompt>>(json) is { } list) return list;
        }
        catch (JsonException) { }
        return defaults();
    }

    private void SavePrompts(string key, List<SavedPrompt> prompts) => _store.SetSetting(key, JsonSerializer.Serialize(prompts));

    private void AutomationPrompts_Click(object sender, RoutedEventArgs e) =>
        ShowPromptLibrary((FrameworkElement)sender, AutomationPromptBox, FilePromptsKey, FilePromptDefaults);

    private void MailPrompts_Click(object sender, RoutedEventArgs e) =>
        ShowPromptLibrary((FrameworkElement)sender, MailPromptBox, MailPromptsKey, MailPromptDefaults);

    // Auswahl als Popup unter dem Knopf (bei jedem Öffnen neu aufgebaut, damit die Texte zur aktuellen Sprache passen)
    private void ShowPromptLibrary(FrameworkElement anchor, TextBox promptBox, string key, Func<List<SavedPrompt>> defaults)
    {
        var prompts = LoadPrompts(key, defaults);
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 6,
            StaysOpen = false, AllowsTransparency = true, Width = 520
        };

        var hint = new TextBlock { Text = L.T("Anklicken übernimmt den Prompt ins Feld."), FontSize = 11.5, Margin = new Thickness(8, 2, 8, 6) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var empty = new TextBlock { Text = L.T("Noch keine Prompts gespeichert."), FontSize = 13, Margin = new Thickness(8, 0, 8, 6) };
        var list = new StackPanel();
        var scroll = new ScrollViewer { MaxHeight = 280, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var status = new TextBlock { FontSize = 12, Margin = new Thickness(8, 6, 8, 0), TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        void Refresh()
        {
            list.Children.Clear();
            foreach (var p in prompts) list.Children.Add(PromptRow(p));
            empty.Visibility = prompts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        FrameworkElement PromptRow(SavedPrompt p)
        {
            var name = new TextBlock { Text = p.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold };
            var preview = new TextBlock { Text = p.Preview, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            var pick = new Button
            {
                Style = (Style)FindResource("FlatRowButton"),
                Content = new StackPanel { Children = { name, preview } },
                ToolTip = new TextBlock { Text = p.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }
            };
            AutomationProperties.SetName(pick, p.Name);
            pick.Click += (_, _) => Apply(p);
            var delete = new Button
            {
                Content = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 12,
                Padding = new Thickness(7, 5, 7, 5), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = L.T("Löschen"), Style = (Style)FindResource("IconButton")
            };
            AutomationProperties.SetName(delete, L.T("Löschen") + " " + p.Name);
            delete.Click += (_, _) =>
            {
                prompts.Remove(p);
                SavePrompts(key, prompts);
                status.Text = L.F("„{0}“ gelöscht.", p.Name);
                Refresh();
            };
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(pick);
            return row;
        }

        void Apply(SavedPrompt p)
        {
            promptBox.Text = p.Text;
            promptBox.CaretIndex = promptBox.Text.Length;
            popup.IsOpen = false;
            promptBox.Focus();
        }

        // Aktuellen Prompt unter einem Namen speichern (gleicher Name = überschreiben)
        var nameBox = new TextBox { Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(nameBox, L.T("Name für den Prompt"));
        var save = new Button { Content = L.T("Speichern"), Padding = new Thickness(16, 5, 16, 6) };
        void Save()
        {
            var name = nameBox.Text.Trim();
            var text = promptBox.Text.Trim();
            if (text.Length == 0) { status.Text = L.T("Das Prompt-Feld ist leer – erst einen Prompt eingeben."); return; }
            if (name.Length == 0) { status.Text = L.T("Bitte einen Namen eingeben."); nameBox.Focus(); return; }
            var existing = prompts.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (existing != null) existing.Text = text;
            else prompts.Add(new SavedPrompt { Name = name, Text = text });
            SavePrompts(key, prompts);
            status.Text = existing != null ? L.F("„{0}“ überschrieben.", name) : L.F("„{0}“ gespeichert.", name);
            nameBox.Clear();
            Refresh();
        }
        save.Click += (_, _) => Save();
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };

        var saveRow = new DockPanel { Margin = new Thickness(8, 4, 8, 0) };
        DockPanel.SetDock(save, Dock.Right);
        saveRow.Children.Add(save);
        saveRow.Children.Add(nameBox);
        var saveLabel = new TextBlock { Text = L.T("Aktuellen Prompt speichern als:"), FontSize = 12, Margin = new Thickness(8, 10, 8, 4) };
        saveLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var separator = new Border { Height = 1, Margin = new Thickness(4, 6, 4, 0) };
        separator.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");

        var border = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 6, 6, 10), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { hint, empty, scroll, separator, saveLabel, saveRow, status } }
        };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        popup.Child = border;

        Refresh();
        popup.IsOpen = true;
    }
}
