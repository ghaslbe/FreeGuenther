namespace LmChat;

// Angehängte Dateien (PDF, Word, Text) und Bilder: Dateien werden in Text umgewandelt und mit der Nachricht an das Modell geschickt
internal sealed partial class MainWindow
{
    private readonly List<AttachedDocument> _pendingDocuments = new();

    private void PickFiles()
    {
        var extensions = Images.Extensions.Concat(DocumentReader.Extensions).Select(e => e.TrimStart('.')).ToArray();
        var files = Ui.OpenDialog(L.T("Datei anhängen"), extensions, multiple: true);
        if (files.Length > 0) AttachFiles(files);
    }

    // Bilder und Dokumente aus Dateiauswahl, Hineinziehen oder Zwischenablage
    private async void AttachFiles(string[] paths)
    {
        if (IsBusy) return;
        var files = paths.ToList();
        var image = files.FirstOrDefault(Images.IsImageFile);
        if (image != null) AttachImageFile(image);

        var unsupported = files.Where(f => !Images.IsImageFile(f) && !DocumentReader.IsSupported(f)).Select(Path.GetFileName).ToList();
        var problems = new List<string>();
        foreach (var path in files.Where(DocumentReader.IsSupported))
        {
            _status.StringValue = L.F("Lese „{0}“ …", Path.GetFileName(path));
            try
            {
                var document = await Task.Run(() => DocumentReader.Read(path));
                AddDocument(document);
                if (PromptGuard.Detect(document.Text) is { Count: > 0 } reasons)
                    problems.Add($"„{document.Name}“: " + PromptGuard.Warning(reasons));
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }
        }
        if (unsupported.Count > 0)
            problems.Add(L.F("Nicht unterstützt: {0} (geht: Bilder, PDF, Word .docx, Excel .xlsx, Text- und Code-Dateien).", string.Join(", ", unsupported)));
        _status.StringValue = problems.Count > 0 ? string.Join(" ", problems) : DocumentsSummary();
        FocusInput();
    }

    private void AddDocument(AttachedDocument document)
    {
        _pendingDocuments.RemoveAll(d => d.Name == document.Name);
        _pendingDocuments.Add(document);
        RenderDocumentChips();
    }

    private void ClearDocuments()
    {
        _pendingDocuments.Clear();
        RenderDocumentChips();
    }

    private string DocumentsSummary()
    {
        if (_pendingDocuments.Count == 0) return "";
        var tokens = _pendingDocuments.Sum(d => DocumentReader.EstimateTokens(d.Text));
        var warning = tokens > 8000
            ? " " + L.T("Das ist viel: Lokale Modelle verarbeiten oft nur 4.000–32.000 Tokens – ggf. in LM Studio die Kontextlänge erhöhen.")
            : "";
        return L.F("{0} Datei(en) angehängt, zusammen ≈ {1:N0} Tokens.", _pendingDocuments.Count, tokens) + warning;
    }

    private static string Preview(AttachedDocument d) => d.Text.Length > 600 ? d.Text[..600] + " …" : d.Text;

    // Kärtchen über dem Eingabefeld: je Datei Name, Größe in Tokens und ein Knopf zum Entfernen
    private void RenderDocumentChips()
    {
        _documentsPanel.Clear();
        foreach (var document in _pendingDocuments)
        {
            var current = document;
            var remove = Ui.IconButton("xmark", L.T("Datei entfernen"), () =>
            {
                _pendingDocuments.Remove(current);
                RenderDocumentChips();
                _status.StringValue = DocumentsSummary();
            }, 10);
            var label = Ui.Label($"📄 {document.Name} · ≈ {DocumentReader.EstimateTokens(document.Text):N0} Tokens", 12);
            var chip = Ui.Card(Ui.Row(6, label, remove), NSColor.FromSrgb(0.5f, 0.5f, 0.5f, 0.12f), NSColor.Clear, 5, 9);
            chip.ToolTip = Preview(document);
            _documentsPanel.Add(chip, stretch: false);
        }
        _documentsPanel.Hidden = _pendingDocuments.Count == 0;
    }

    // In der eigenen Sprechblase: welche Dateien mitgeschickt wurden
    private static NSView DocumentList(IReadOnlyList<AttachedDocument> documents)
    {
        var panel = new Col(2);
        foreach (var d in documents)
        {
            var label = Ui.Label($"📄 {d.Name}  (≈ {DocumentReader.EstimateTokens(d.Text):N0} Tokens)", 12.5f, Ui.Secondary);
            label.ToolTip = Preview(d);
            panel.Add(label, stretch: false);
        }
        return panel;
    }

    // ---------- Bilder ----------

    private bool TryPaste()
    {
        var board = NSPasteboard.GeneralPasteboard;
        var files = Drop.Files(board).Where(f => Images.IsImageFile(f) || DocumentReader.IsSupported(f)).ToArray();
        if (files.Length > 0)
        {
            AttachFiles(files);
            return true;
        }
        // Text hat Vorrang, wenn die Zwischenablage beides enthält (z. B. aus Word oder dem Browser)
        if (board.GetStringForType(NSPasteboard.NSPasteboardTypeString) != null) return false;
        var data = board.GetDataForType("public.png") ?? board.GetDataForType("public.tiff");
        if (data == null) return false;
        AttachImage(data.ToArray());
        return true;
    }

    private void AttachImageFile(string path)
    {
        try
        {
            AttachImage(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.StringValue = L.F("Bild konnte nicht geladen werden: {0}", ex.Message);
        }
    }

    private void AttachImage(byte[] data)
    {
        if (CurrentVision == false)
        {
            _status.StringValue = L.T("Das gewählte Modell kann keine Bilder verarbeiten.");
            return;
        }
        if (IsBusy) return;

        // Große Bilder verkleinern, damit die Anfrage nicht unnötig groß wird
        if (Images.ToPng(data, Images.MaxImageSide) is not { } png)
        {
            _status.StringValue = L.F("Bild konnte nicht geladen werden: {0}", L.T("unbekannt"));
            return;
        }
        SetImage(png);
        FocusInput();
    }

    private void SetImage(byte[] png)
    {
        _pendingImage = png;
        using var data = NSData.FromArray(png);
        _attachmentImage.Image = new NSImage(data);
        var (width, height) = Images.PixelSize(png);
        _attachmentText.StringValue = L.F("Bild angehängt ({0} × {1}, {2} KB)", width, height, png.Length / 1024);
        _attachmentPanel.Hidden = false;
    }

    private void ClearImage()
    {
        _pendingImage = null;
        _attachmentImage.Image = null!;
        _attachmentPanel.Hidden = true;
    }
}
