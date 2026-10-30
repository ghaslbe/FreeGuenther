namespace LmChat;

// Vorlagen (Text fürs Eingabefeld) und Personas (zusätzlicher System-Prompt je Chat).
// Im Chat: „/“ tippen öffnet die Auswahl; verwaltet werden sie in den Einstellungen.
internal sealed partial class MainWindow
{
    private PromptTemplate? _pendingPersona;   // Persona für den nächsten neuen Chat (solange noch keiner angelegt ist)
    private readonly List<PromptTemplate> _draftTemplates = new();
    private readonly List<PromptTemplate> _pickerMatches = new();
    private int _pickerIndex;
    private NSView _picker = null!;
    private Col _pickerList = null!;
    private WrapLabel _pickerEmpty = null!;
    private bool _pickerOpen;

    private NSPopUpButton _templatePopup = null!;
    private NSTextField _templateName = null!;
    private TextArea _templateText = null!;
    private NSButton _templatePersona = null!;
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
            _currentChat = chat with { Persona = copy };
            ReloadChatList();
        }
        else
            _pendingPersona = copy;
        UpdatePersonaChip();
        _status.StringValue = copy == null ? L.T("Persona entfernt.") : L.F("Persona „{0}“ gilt ab der nächsten Nachricht für diesen Chat.", copy.Name);
    }

    private void UpdatePersonaChip()
    {
        var persona = CurrentPersona;
        _personaPanel.Hidden = persona == null;
        _personaText.StringValue = persona == null ? "" : $"🎭 Persona: {persona.Name}";
        _personaPanel.ToolTip = persona?.Text ?? "";
    }

    // ---------- Auswahl mit „/“ ----------

    private void BuildTemplatePicker(Col area)
    {
        var column = new Col(4);
        column.Add(new WrapLabel(L.UI("ui.VorlagenUndPersonasWaehlenEnter"), 11.5f, Ui.Tertiary));
        _pickerList = new Col(1);
        column.Add(_pickerList);
        _pickerEmpty = Ui.Hint(L.UI("ui.KeinePassendeVorlageAnlegenKannst"));
        column.Add(_pickerEmpty);
        _picker = Ui.Card(column, NSColor.ControlBackground, NSColor.Separator, 8, 12);
        _picker.Hidden = true;
        area.Add(_picker);
    }

    private void InputChanged()
    {
        var text = _input.Text;
        if (!text.StartsWith('/') || text.Contains('\n') || IsBusy)
        {
            ClosePicker();
            return;
        }
        var words = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _pickerMatches.Clear();
        _pickerMatches.AddRange(Templates.Where(t => words.All(w => t.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase))));
        _pickerIndex = 0;
        _pickerOpen = true;
        _picker.Hidden = false;
        RenderPicker();
    }

    private void ClosePicker()
    {
        _pickerOpen = false;
        _picker.Hidden = true;
    }

    private void RenderPicker()
    {
        _pickerList.Clear();
        _pickerEmpty.Hidden = _pickerMatches.Count > 0;
        // Höchstens acht Einträge rund um die Auswahl zeigen
        var start = Math.Clamp(_pickerIndex - 3, 0, Math.Max(0, _pickerMatches.Count - 8));
        foreach (var (template, index) in _pickerMatches.Select((t, i) => (t, i)).Skip(start).Take(8))
        {
            var name = Ui.Label(template.Name, 13, bold: true);
            var preview = Ui.Label(template.Kind + "  " + template.Preview, 11.5f, Ui.Secondary);
            var inner = new Col(1);
            inner.Add(name);
            inner.Add(preview);
            var row = Ui.Card(inner, index == _pickerIndex ? NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.18f) : NSColor.Clear, NSColor.Clear, 5, 6);
            var current = template;
            row.AddGestureRecognizer(new NSClickGestureRecognizer(() => ApplyTemplate(current)));
            _pickerList.Add(row);
        }
    }

    // Tasten im Eingabefeld, solange die Auswahl offen ist. true = Taste erledigt
    private bool HandleTemplateKeys(ushort key)
    {
        if (!_pickerOpen) return false;
        var count = _pickerMatches.Count;
        switch (key)
        {
            case 125 when count > 0:                    // ↓
                _pickerIndex = (_pickerIndex + 1) % count;
                break;
            case 126 when count > 0:                    // ↑
                _pickerIndex = (_pickerIndex - 1 + count) % count;
                break;
            case 36 or 76 or 48 when count > 0:         // Enter, Tab
                ApplyTemplate(_pickerMatches[_pickerIndex]);
                return true;
            case 53:                                    // Esc
                ClosePicker();
                return true;
            default:
                return false;
        }
        RenderPicker();
        return true;
    }

    private void ApplyTemplate(PromptTemplate template)
    {
        ClosePicker();
        if (template.Persona)
        {
            _input.Text = "";
            SetPersona(template);
        }
        else
        {
            _input.Text = template.Text;
            _status.StringValue = L.F("Vorlage „{0}“ eingefügt – jetzt deinen Text dazuschreiben.", template.Name);
        }
        ClosePicker();
        FocusInput();
    }

    // ---------- Verwaltung in den Einstellungen ----------

    private NSView BuildTemplateCard()
    {
        var column = new Col(8);
        column.Add(Ui.Heading(L.UI("ui.VorlagenUndPersonas")));
        column.Add(new Rich(12, Ui.Secondary).Bold(L.UI("ui.ImChat") + " ").Text("„/“ " + L.UI("ui.InsEingabefeldTippenOderKnopf") + " ")
            .Bold(L.UI("ui.Vorlage")).Text(" " + L.UI("ui.SetztIhrenTextInsEingabefeld") + " ").Bold(L.UI("ui.Persona")).Text(" " + L.UI("ui.GiltFuerDenGanzenChat")).View());

        _templatePopup = Ui.Popup(ShowSelectedTemplate);
        var add = Ui.Button(L.UI("ui.Neu"), () =>
        {
            _draftTemplates.Add(new PromptTemplate { Name = L.T("Neue Vorlage") });
            RefreshTemplatePopup(_draftTemplates.Count - 1);
            _window.MakeFirstResponder(_templateName);
        });
        var delete = Ui.Button(L.UI("ui.Loeschen"), () =>
        {
            var index = (int)_templatePopup.IndexOfSelectedItem;
            if (index < 0 || index >= _draftTemplates.Count) return;
            _draftTemplates.RemoveAt(index);
            RefreshTemplatePopup(Math.Min(index, _draftTemplates.Count - 1));
        });
        column.Add(Ui.Row(8, _templatePopup, Ui.Spacer(), add, delete));

        column.Add(Ui.Label(L.UI("ui.Name"), bold: true));
        _templateName = Ui.Field();
        _templateName.Changed += (_, _) => TemplateChanged();
        column.Add(_templateName);
        _templatePersona = Ui.Check(L.UI("ui.PersonaGiltFuerDenGanzen"), TemplateChanged);
        column.Add(_templatePersona);
        column.Add(Ui.Label(L.UI("ui.Text"), bold: true));
        _templateText = new TextArea(90);
        _templateText.Changed += TemplateChanged;
        column.Add(_templateText);
        return Ui.Card(column);
    }

    private void ShowTemplates()
    {
        _draftTemplates.Clear();
        foreach (var t in _draft.Templates ?? PromptTemplate.Defaults())
            _draftTemplates.Add(new PromptTemplate { Name = t.Name, Text = t.Text, Persona = t.Persona });
        RefreshTemplatePopup(_draftTemplates.Count > 0 ? 0 : -1);
    }

    private void RefreshTemplatePopup(int select)
    {
        _templatePopup.RemoveAllItems();
        foreach (var t in _draftTemplates)
        {
            _templatePopup.AddItem($"{t.Name}   {t.Kind}");
            _templatePopup.LastItem!.Title = $"{t.Name}   {t.Kind}";
        }
        if (select >= 0) _templatePopup.SelectItem(select);
        ShowSelectedTemplate();
    }

    private void ShowSelectedTemplate()
    {
        var index = (int)_templatePopup.IndexOfSelectedItem;
        var t = index >= 0 && index < _draftTemplates.Count ? _draftTemplates[index] : null;
        _loadingTemplate = true;
        _templateName.StringValue = t?.Name ?? "";
        _templateText.Text = t?.Text ?? "";
        _templatePersona.State = t?.Persona == true ? NSCellStateValue.On : NSCellStateValue.Off;
        _templateName.Enabled = _templatePersona.Enabled = t != null;
        _templateText.Enabled = t != null;
        _loadingTemplate = false;
    }

    private void TemplateChanged()
    {
        var index = (int)_templatePopup.IndexOfSelectedItem;
        if (_loadingTemplate || index < 0 || index >= _draftTemplates.Count) return;
        var t = _draftTemplates[index];
        t.Name = _templateName.StringValue;
        t.Text = _templateText.Text;
        t.Persona = _templatePersona.State == NSCellStateValue.On;
        if (_templatePopup.ItemAtIndex(index) is { } item)
            item.Title = $"{t.Name}   {t.Kind}";
    }

    private void StoreTemplates() =>
        _draft.Templates = _draftTemplates.Where(t => t.Name.Trim().Length > 0 && t.Text.Trim().Length > 0)
            .Select(t => new PromptTemplate { Name = t.Name.Trim(), Text = t.Text, Persona = t.Persona }).ToList();
}
