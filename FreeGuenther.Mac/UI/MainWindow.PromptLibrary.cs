using System.Text.Json;
using System.Text.Json.Serialization;

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
internal sealed partial class MainWindow
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

    // Auswahl als Popover unter dem Knopf (bei jedem Öffnen neu aufgebaut, damit die Texte zur aktuellen Sprache passen)
    private void ShowPromptLibrary(NSView anchor, TextArea promptBox, string key, Func<List<SavedPrompt>> defaults)
    {
        var prompts = LoadPrompts(key, defaults);
        var popover = new NSPopover { Behavior = NSPopoverBehavior.Transient };

        var column = new Col(6);
        column.Add(new WrapLabel(L.T("Anklicken übernimmt den Prompt ins Feld."), 11.5f, Ui.Tertiary));
        var empty = Ui.Body(L.T("Noch keine Prompts gespeichert."));
        column.Add(empty);
        var list = new Col(2);
        column.Add(list);
        var status = Ui.Hint();

        void Refresh()
        {
            list.Clear();
            foreach (var p in prompts) list.Add(PromptRow(p));
            empty.Hidden = prompts.Count > 0;
        }

        NSView PromptRow(SavedPrompt p)
        {
            var inner = new Col(1);
            inner.Add(Ui.Label(p.Name, 13.5f, bold: true));
            inner.Add(Ui.Label(p.Preview, 12, Ui.Tertiary));
            var pick = Ui.Card(inner, NSColor.Clear, NSColor.Clear, 4, 6);
            pick.ToolTip = p.Text;
            pick.AddGestureRecognizer(new NSClickGestureRecognizer(() =>
            {
                promptBox.Text = p.Text;
                promptBox.CaretToEnd();
                popover.Close();
                promptBox.Focus();
            }));
            var delete = Ui.IconButton("trash", L.T("Löschen"), () =>
            {
                prompts.Remove(p);
                SavePrompts(key, prompts);
                status.StringValue = L.F("„{0}“ gelöscht.", p.Name);
                Refresh();
            }, 12);
            return Ui.Row(4, pick, delete);
        }

        // Aktuellen Prompt unter einem Namen speichern (gleicher Name = überschreiben)
        var nameBox = Ui.Field();
        void Save()
        {
            var name = nameBox.StringValue.Trim();
            var text = promptBox.Text.Trim();
            if (text.Length == 0) { status.StringValue = L.T("Das Prompt-Feld ist leer – erst einen Prompt eingeben."); return; }
            if (name.Length == 0) { status.StringValue = L.T("Bitte einen Namen eingeben."); return; }
            var existing = prompts.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (existing != null) existing.Text = text;
            else prompts.Add(new SavedPrompt { Name = name, Text = text });
            SavePrompts(key, prompts);
            status.StringValue = existing != null ? L.F("„{0}“ überschrieben.", name) : L.F("„{0}“ gespeichert.", name);
            nameBox.StringValue = "";
            Refresh();
        }
        nameBox.Activated += (_, _) => Save();
        column.Add(Ui.Hint(L.T("Aktuellen Prompt speichern als:")));
        column.Add(Ui.Row(8, nameBox, Ui.Button(L.T("Speichern"), Save)));
        column.Add(status);

        var host = Ui.Pad(column, 12, 12, 12, 12);
        host.WidthAnchor.ConstraintEqualTo(520).Active = true;
        popover.ContentViewController = new Host(host);
        Refresh();
        popover.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
    }
}
