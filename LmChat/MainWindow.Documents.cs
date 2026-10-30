using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LmChat;

// Angehängte Dateien (PDF, Word, Text): werden in Text umgewandelt und mit der Nachricht an das Modell geschickt
public partial class MainWindow
{
    private readonly List<AttachedDocument> _pendingDocuments = new();

    // Bilder und Dokumente aus Dateiauswahl, Hineinziehen oder Zwischenablage
    private async void AttachFiles(IEnumerable<string> paths)
    {
        if (_cts != null) return;
        var files = paths.ToList();
        var image = files.FirstOrDefault(IsImageFile);
        if (image != null) AttachImageFile(image);

        var unsupported = files.Where(f => !IsImageFile(f) && !DocumentReader.IsSupported(f)).Select(Path.GetFileName).ToList();
        var problems = new List<string>();
        foreach (var path in files.Where(DocumentReader.IsSupported))
        {
            StatusText.Text = L.F("Lese „{0}“ …", Path.GetFileName(path));
            try
            {
                AddDocument(await Task.Run(() => DocumentReader.Read(path)));
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }
        }
        if (unsupported.Count > 0)
            problems.Add(L.F("Nicht unterstützt: {0} (geht: Bilder, PDF, Word .docx, Excel .xlsx, Text- und Code-Dateien).", string.Join(", ", unsupported)));
        StatusText.Text = problems.Count > 0 ? string.Join(" ", problems) : DocumentsSummary();
        InputBox.Focus();
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

    // Kärtchen über dem Eingabefeld: je Datei Name, Größe in Tokens und ein Knopf zum Entfernen
    private void RenderDocumentChips()
    {
        DocumentsPanel.Children.Clear();
        foreach (var document in _pendingDocuments)
        {
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Padding = new Thickness(6, 3, 6, 3),
                ToolTip = L.T("Datei entfernen"),
                Content = new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 10 }
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"{document.Name} entfernen");
            var current = document;
            remove.Click += (_, _) =>
            {
                _pendingDocuments.Remove(current);
                RenderDocumentChips();
                StatusText.Text = DocumentsSummary();
            };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 2, 3),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = document.Text.Length > 600 ? document.Text[..600] + " …" : document.Text,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"📄 {document.Name} · ≈ {DocumentReader.EstimateTokens(document.Text):N0} Tokens",
                            FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                        },
                        remove
                    }
                }
            };
            chip.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            DocumentsPanel.Children.Add(chip);
        }
        DocumentsPanel.Visibility = _pendingDocuments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // In der eigenen Sprechblase: welche Dateien mitgeschickt wurden
    private static UIElement DocumentList(IReadOnlyList<AttachedDocument> documents)
    {
        var panel = new StackPanel();
        foreach (var d in documents)
            panel.Children.Add(new TextBlock
            {
                Text = $"📄 {d.Name}  (≈ {DocumentReader.EstimateTokens(d.Text):N0} Tokens)",
                FontSize = 12.5,
                Opacity = 0.8,
                ToolTip = d.Text.Length > 600 ? d.Text[..600] + " …" : d.Text
            });
        return panel;
    }
}
