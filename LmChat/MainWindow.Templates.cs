using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LmChat;

// Vorlagen (Text fürs Eingabefeld) und Personas (zusätzlicher System-Prompt je Chat).
// Im Chat: „/“ tippen öffnet die Auswahl; verwaltet werden sie in den Einstellungen.
public partial class MainWindow
{
    private PromptTemplate? _pendingPersona;   // Persona für den nächsten neuen Chat (solange noch keiner angelegt ist)
    private readonly ObservableCollection<PromptTemplate> _draftTemplates = new();
    private bool _loadingTemplate;

    private PromptTemplate? CurrentPersona => _currentChat != null ? _currentChat.Persona : _pendingPersona;

    private IReadOnlyList<PromptTemplate> Templates => _settings.Templates ?? PromptTemplate.Defaults();

    // System-Prompt aus den Einstellungen plus Persona des Chats
    private string SystemPromptFor(PromptTemplate? persona) =>
        persona == null ? _settings.SystemPrompt
            : string.Join("\n\n", new[] { _settings.SystemPrompt, persona.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // ---------- Persona im Chat ----------

    private void SetPersona(PromptTemplate? persona)
    {
        var copy = persona == null ? null : new PromptTemplate { Name = persona.Name, Text = persona.Text, Persona = true };
        if (_currentChat is { } chat)
        {
            _store.SetPersona(chat.Id, copy);
            var updated = chat with { Persona = copy };
            var index = _chats.IndexOf(chat);
            if (index >= 0) _chats[index] = updated;
            _currentChat = updated;
            SelectChatSilently(updated);
        }
        else
            _pendingPersona = copy;
        UpdatePersonaChip();
        StatusText.Text = copy == null ? L.T("Persona entfernt.") : L.F("Persona „{0}“ gilt ab der nächsten Nachricht für diesen Chat.", copy.Name);
    }

    private void UpdatePersonaChip()
    {
        var persona = CurrentPersona;
        PersonaPanel.Visibility = persona == null ? Visibility.Collapsed : Visibility.Visible;
        PersonaText.Text = persona == null ? "" : $"🎭 Persona: {persona.Name}";
        PersonaPanel.ToolTip = persona?.Text;
    }

    private void RemovePersona_Click(object sender, RoutedEventArgs e) => SetPersona(null);

    // ---------- Auswahl mit „/“ ----------

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text;
        if (!text.StartsWith('/') || text.Contains('\n') || _cts != null)
        {
            TemplatePopup.IsOpen = false;
            return;
        }
        var words = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Templates.Where(t => words.All(w => t.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
        TemplatePickList.ItemsSource = matches;
        TemplatePickEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TemplatePickList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        TemplatePopup.Width = InputCard.ActualWidth;
        TemplatePopup.IsOpen = true;
    }

    // Tasten im Eingabefeld, solange die Auswahl offen ist. true = Taste erledigt
    private bool HandleTemplateKeys(KeyEventArgs e)
    {
        if (!TemplatePopup.IsOpen) return false;
        var count = TemplatePickList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                TemplatePickList.SelectedIndex = (TemplatePickList.SelectedIndex + 1) % count;
                break;
            case Key.Up when count > 0:
                TemplatePickList.SelectedIndex = (TemplatePickList.SelectedIndex - 1 + count) % count;
                break;
            case Key.Enter or Key.Tab when TemplatePickList.SelectedItem is PromptTemplate template:
                ApplyTemplate(template);
                break;
            case Key.Escape:
                TemplatePopup.IsOpen = false;
                break;
            default:
                return false;
        }
        if (TemplatePickList.SelectedItem != null)
            TemplatePickList.ScrollIntoView(TemplatePickList.SelectedItem);
        return true;
    }

    private void TemplatePickList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (TemplatePickList.SelectedItem is PromptTemplate template)
            ApplyTemplate(template);
    }

    private void TemplatesButton_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Text = "/";
        InputBox.CaretIndex = 1;
        InputBox.Focus();
    }

    private void ApplyTemplate(PromptTemplate template)
    {
        TemplatePopup.IsOpen = false;
        if (template.Persona)
        {
            InputBox.Clear();
            SetPersona(template);
        }
        else
        {
            InputBox.Text = template.Text;
            InputBox.CaretIndex = InputBox.Text.Length;
            StatusText.Text = L.F("Vorlage „{0}“ eingefügt – jetzt deinen Text dazuschreiben.", template.Name);
        }
        InputBox.Focus();
    }

    // ---------- Verwaltung in den Einstellungen ----------

    private void ShowTemplates()
    {
        _draftTemplates.Clear();
        foreach (var t in _draft.Templates ?? PromptTemplate.Defaults())
            _draftTemplates.Add(new PromptTemplate { Name = t.Name, Text = t.Text, Persona = t.Persona });
        TemplateList.ItemsSource = _draftTemplates;
        TemplateList.SelectedIndex = _draftTemplates.Count > 0 ? 0 : -1;
        ShowSelectedTemplate();
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedTemplate();

    private void ShowSelectedTemplate()
    {
        var t = TemplateList.SelectedItem as PromptTemplate;
        _loadingTemplate = true;
        TemplateNameBox.Text = t?.Name ?? "";
        TemplateTextBox.Text = t?.Text ?? "";
        TemplatePersonaCheck.IsChecked = t?.Persona == true;
        TemplateEditor.IsEnabled = t != null;
        _loadingTemplate = false;
    }

    private void TemplateField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTemplate || TemplateList.SelectedItem is not PromptTemplate t) return;
        t.Name = TemplateNameBox.Text;
        t.Text = TemplateTextBox.Text;
        t.Persona = TemplatePersonaCheck.IsChecked == true;
        TemplateList.Items.Refresh();
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        var t = new PromptTemplate { Name = L.T("Neue Vorlage") };
        _draftTemplates.Add(t);
        TemplateList.SelectedItem = t;
        TemplateNameBox.Focus();
        TemplateNameBox.SelectAll();
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is not PromptTemplate t) return;
        var index = TemplateList.SelectedIndex;
        _draftTemplates.Remove(t);
        TemplateList.SelectedIndex = Math.Min(index, _draftTemplates.Count - 1);
    }

    private void StoreTemplates() =>
        _draft.Templates = _draftTemplates.Where(t => t.Name.Trim().Length > 0 && t.Text.Trim().Length > 0)
            .Select(t => new PromptTemplate { Name = t.Name.Trim(), Text = t.Text, Persona = t.Persona }).ToList();
}
