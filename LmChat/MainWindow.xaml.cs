using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LmChat;

// Vision = null heißt: unbekannt (z. B. Server nicht erreichbar)
public record ModelInfo(string Id, bool? Vision, bool Loaded)
{
    public string Label => Id + (Vision == true ? "    🖼 Bilder" : "") + (Loaded ? "    ● geladen" : "");
}

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];
    private const int MaxImageSide = 2048;

    private readonly ChatStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LmChat", "chats.db"));
    private readonly ObservableCollection<ChatInfo> _chats = new();
    private readonly ObservableCollection<ModelInfo> _models = new();
    private readonly List<ChatMessage> _messages = new();
    private readonly LlmSettings _settings;
    private ChatInfo? _currentChat;
    private byte[]? _pendingImage;
    private CancellationTokenSource? _cts;
    private bool _suppressSelection;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"LM Chat {AppVersion}";
        VersionRun.Text = $"Version {AppVersion}.";
        _settings = LlmSettings.Load(_store);
        foreach (var chat in _store.ListChats())
            _chats.Add(chat);
        ChatList.ItemsSource = _chats;
        ModelCombo.ItemsSource = _models;
        ShowSettings();
        InputBox.Focus();
        Loaded += async (_, _) => await LoadModelsAsync();
    }

    private bool? CurrentVision => _models.FirstOrDefault(m => m.Id == _settings.Model)?.Vision;

    // ---------- Chat senden ----------

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            Send_Click(sender, e);
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && TryPasteImage())
        {
            e.Handled = true;
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        var image = _pendingImage;
        if ((text.Length == 0 && image == null) || _cts != null) return;
        if (image != null && CurrentVision == false)
        {
            StatusText.Text = "Das gewählte Modell kann keine Bilder verarbeiten – Bild entfernen oder Modell wechseln.";
            return;
        }

        if (_currentChat == null)
        {
            _currentChat = _store.CreateChat(MakeTitle(text));
            _chats.Insert(0, _currentChat);
            SelectChatSilently(_currentChat);
        }
        var chat = _currentChat;

        var userMsg = new ChatMessage("user", text, image);
        userMsg = userMsg with { Id = _store.AddMessage(chat.Id, userMsg) };
        _messages.Add(userMsg);
        InputBox.Clear();
        ClearImage();
        AddMessageBlock(userMsg);
        var answerRun = AddMessageBlock(new ChatMessage("assistant", ""));

        List<ChatMessage> toSend = HistoryCheck.IsChecked == true ? _messages.ToList() : [userMsg];

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var answer = new StringBuilder();
        try
        {
            await StreamAnswerAsync(toSend, chunk =>
            {
                answer.Append(chunk);
                AppendToRun(answerRun, chunk);
            }, _cts.Token);
            StatusText.Text = "Fertig.";
        }
        catch (OperationCanceledException)
        {
            AppendToRun(answerRun, " [abgebrochen]");
            StatusText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            AppendToRun(answerRun, $"[Fehler: {ex.Message}]");
            StatusText.Text = "Fehler – Server und Modell in den Einstellungen prüfen.";
        }
        finally
        {
            if (answer.Length > 0)
            {
                var reply = new ChatMessage("assistant", answer.ToString());
                _messages.Add(reply with { Id = _store.AddMessage(chat.Id, reply) });
            }
            else
            {
                // Ohne Antwort: Frage nicht speichern, sondern zum erneuten Senden zurück ins Eingabefeld
                _store.DeleteMessage(userMsg.Id);
                _messages.Remove(userMsg);
                InputBox.Text = text;
                if (image != null) SetImage(image);
            }

            if (_messages.Count == 0)
            {
                _store.DeleteChat(chat.Id);
                _chats.Remove(chat);
                _currentChat = null;
            }
            else
            {
                var updated = chat with { Updated = _store.Touch(chat.Id) };
                _chats.Remove(chat);
                _chats.Insert(0, updated);
                _currentChat = updated;
                SelectChatSilently(updated);
            }

            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            InputBox.Focus();
        }
    }

    // Sendet die Nachrichten an die OpenAI-kompatible API von LM Studio und liefert die Antwort stückweise
    private async Task StreamAnswerAsync(List<ChatMessage> messages, Action<string> onChunk, CancellationToken ct)
    {
        var apiMessages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
            apiMessages.Add(new JsonObject { ["role"] = "system", ["content"] = _settings.SystemPrompt });
        foreach (var m in messages)
            apiMessages.Add(ToApiMessage(m));

        var body = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = apiMessages,
            ["stream"] = true
        };
        if (_settings.Temperature is { } temperature) body["temperature"] = temperature;
        if (_settings.TopP is { } topP) body["top_p"] = topP;
        if (_settings.TopK is { } topK) body["top_k"] = (int)topK;
        if (_settings.MaxTokens is { } maxTokens) body["max_tokens"] = (int)maxTokens;
        if (_settings.RepeatPenalty is { } repeatPenalty) body["repeat_penalty"] = repeatPenalty;

        using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl + "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            if (JsonNode.Parse(data)?["choices"] is not JsonArray { Count: > 0 } choices) continue;
            var chunk = choices[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk))
                onChunk(chunk);
        }
    }

    private static JsonObject ToApiMessage(ChatMessage m)
    {
        if (m.Image == null)
            return new JsonObject { ["role"] = m.Role, ["content"] = m.Content };

        var parts = new JsonArray();
        if (m.Content.Length > 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
        parts.Add(new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(m.Image) }
        });
        return new JsonObject { ["role"] = m.Role, ["content"] = parts };
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cts?.Cancel();

    // ---------- Chatliste ----------

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ChatList.SelectedItem is not ChatInfo chat) return;
        _currentChat = chat;
        _messages.Clear();
        _messages.AddRange(_store.LoadMessages(chat.Id));
        RenderChat();
        InputBox.Focus();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _currentChat = null;
        _messages.Clear();
        ChatBox.Document.Blocks.Clear();
        SelectChatSilently(null);
        StatusText.Text = "Neuer Chat.";
        InputBox.Focus();
    }

    private void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if (ChatList.SelectedItem is not ChatInfo chat) return;
        if (MessageBox.Show(this, $"Chat „{chat.Title}“ wirklich löschen?", "Chat löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _store.DeleteChat(chat.Id);
        _chats.Remove(chat);
        if (chat.Id == _currentChat?.Id)
            NewChat_Click(sender, e);
    }

    private void SelectChatSilently(ChatInfo? chat)
    {
        _suppressSelection = true;
        ChatList.SelectedItem = chat;
        _suppressSelection = false;
    }

    private static string MakeTitle(string text)
    {
        var firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length == 0) return "Bild";
        return firstLine.Length > 40 ? firstLine[..40] + "…" : firstLine;
    }

    private void RenderChat()
    {
        ChatBox.Document.Blocks.Clear();
        foreach (var m in _messages)
            AddMessageBlock(m);
    }

    // Fügt eine Nachricht (Überschrift, ggf. Bildvorschau, Text) an und liefert den Text-Run zum Weiterschreiben
    private Run AddMessageBlock(ChatMessage m)
    {
        var blocks = ChatBox.Document.Blocks;
        blocks.Add(new Paragraph(new Run(m.Role == "user" ? "Du" : "LLM") { FontWeight = FontWeights.Bold })
        {
            Margin = new Thickness(0, blocks.Count == 0 ? 0 : 14, 0, 4)
        });
        if (m.Image != null)
            blocks.Add(new BlockUIContainer(MakeThumbnail(m.Image)) { Margin = new Thickness(0, 0, 0, 6) });

        var run = new Run(m.Content);
        blocks.Add(new Paragraph(run) { Margin = new Thickness(0) });
        ChatBox.ScrollToEnd();
        return run;
    }

    private void AppendToRun(Run run, string text)
    {
        run.Text += text;
        ChatBox.ScrollToEnd();
    }

    private static Image MakeThumbnail(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();

        var image = new Image
        {
            Source = bitmap,
            MaxHeight = 180,
            MaxWidth = 320,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = "Klicken zum Vergrößern"
        };
        image.MouseLeftButtonUp += (_, _) => OpenImage(png);
        return image;
    }

    // Öffnet das Bild in voller Größe mit dem Standard-Bildbetrachter
    private static void OpenImage(byte[] png)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LmChat");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"bild-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(png))[..12]}.png");
        if (!File.Exists(file))
            File.WriteAllBytes(file, png);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        NewChatButton.IsEnabled = !busy;
        DeleteChatButton.IsEnabled = !busy;
        ChatList.IsEnabled = !busy;
        UpdateModelUi();
        if (busy) StatusText.Text = "Antwort wird empfangen …";
    }

    // ---------- Bilder ----------

    private void ImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Bild anhängen",
            Filter = "Bilder|" + string.Join(";", ImageExtensions.Select(x => "*" + x))
        };
        if (dialog.ShowDialog(this) == true)
            AttachImageFile(dialog.FileName);
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e) => ClearImage();

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var file = files.FirstOrDefault(IsImageFile);
        if (file != null) AttachImageFile(file);
        e.Handled = true;
    }

    private bool TryPasteImage()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
            if (file == null) return false;
            AttachImageFile(file);
            return true;
        }

        // Screenshots liegen meist zusätzlich als PNG vor – das ist zuverlässiger als GetImage()
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            AttachImage(BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
            return true;
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
        {
            // Zwischenablage-Bitmaps haben oft einen kaputten Alphakanal → ohne Transparenz übernehmen
            AttachImage(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0));
            return true;
        }
        return false;
    }

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private void AttachImageFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            AttachImage(bitmap);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException)
        {
            StatusText.Text = $"Bild konnte nicht geladen werden: {ex.Message}";
        }
    }

    private void AttachImage(BitmapSource source)
    {
        if (CurrentVision == false)
        {
            StatusText.Text = "Das gewählte Modell kann keine Bilder verarbeiten.";
            return;
        }
        if (_cts != null) return;

        // Große Bilder verkleinern, damit die Anfrage nicht unnötig groß wird
        var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        SetImage(ms.ToArray());
        InputBox.Focus();
    }

    private void SetImage(byte[] png)
    {
        _pendingImage = png;
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.StreamSource = new MemoryStream(png);
        preview.EndInit();
        AttachmentImage.Source = preview;
        AttachmentText.Text = $"Bild angehängt ({preview.PixelWidth} × {preview.PixelHeight}, {png.Length / 1024} KB)";
        AttachmentPanel.Visibility = Visibility.Visible;
    }

    private void ClearImage()
    {
        _pendingImage = null;
        AttachmentImage.Source = null;
        AttachmentPanel.Visibility = Visibility.Collapsed;
    }

    // ---------- Einstellungen ----------

    private string ServerUrl => _settings.Server.Trim().TrimEnd('/');

    private readonly ParamRow _temperatureRow = new("Temperatur",
        "Wie frei bzw. zufällig das Modell formuliert. Niedrig (0 bis 0,3): sachlich und bei gleicher Frage fast " +
        "immer gleiche Antwort – gut für Fakten, Code und Bilder auslesen. Mittel (0,7): ausgewogen. " +
        "Hoch (über 1): kreativ, aber zunehmend wirr.",
        0, 2, 0.05, 0.7, "0.00");

    private readonly ParamRow _topPRow = new("Top-P",
        "Das Modell wählt das nächste Wort nur aus den wahrscheinlichsten Kandidaten, die zusammen diesen Anteil " +
        "ausmachen. 0,95 = die oberen 95 %. Kleiner = vorsichtiger. Meist reicht es, nur die Temperatur zu ändern.",
        0.05, 1, 0.01, 0.95, "0.00");

    private readonly ParamRow _topKRow = new("Top-K",
        "Das Modell wählt das nächste Wort nur aus den K wahrscheinlichsten Kandidaten. 40 ist üblich. " +
        "Kleiner = eintöniger, größer = abwechslungsreicher.",
        1, 200, 1, 40, "0");

    private readonly ParamRow _maxTokensRow = new("Maximale Antwortlänge (Tokens)",
        "Begrenzt, wie lang eine Antwort höchstens wird. Ein Token ist ungefähr ein halbes bis ganzes Wort; " +
        "1000 Tokens sind etwa ein bis zwei Seiten Text. Ohne Haken: keine Begrenzung.",
        64, 32768, 64, 2048, "0");

    private readonly ParamRow _repeatPenaltyRow = new("Wiederholungsstrafe",
        "Bremst das Modell, wenn es sich wiederholt. 1,00 = aus, 1,10 = leicht (üblich). " +
        "Über 1,30 kann die Sprache holprig werden.",
        1, 2, 0.01, 1.1, "0.00");

    private IEnumerable<ParamRow> ParamRows => [_temperatureRow, _topPRow, _topKRow, _maxTokensRow, _repeatPenaltyRow];

    private void ShowSettings()
    {
        foreach (var row in ParamRows)
            ParamsPanel.Children.Add(row.Element);

        ServerBox.Text = _settings.Server;
        SystemPromptBox.Text = _settings.SystemPrompt;
        _temperatureRow.Value = _settings.Temperature;
        _topPRow.Value = _settings.TopP;
        _topKRow.Value = _settings.TopK;
        _maxTokensRow.Value = _settings.MaxTokens;
        _repeatPenaltyRow.Value = _settings.RepeatPenalty;
        EnsureModelItem(_settings.Model);
        ModelCombo.SelectedValue = _settings.Model;
        UpdateModelUi();
    }

    private void ResetParams_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ParamRows)
            row.Value = null;
        SettingsStatus.Text = "Alle Feineinstellungen auf Standard – zum Übernehmen „Speichern“ klicken.";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim().TrimEnd('/');
        if (!server.Contains("://"))
            server = "http://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SettingsStatus.Text = "Die Server-Adresse ist ungültig. Beispiel: http://192.168.178.191:1234";
            return;
        }
        if (ModelCombo.SelectedValue is not string model)
        {
            SettingsStatus.Text = "Bitte ein Modell auswählen.";
            return;
        }

        ServerBox.Text = server;
        var serverChanged = _settings.Server != server;
        _settings.Server = server;
        _settings.Model = model;
        _settings.SystemPrompt = SystemPromptBox.Text.Trim();
        _settings.Temperature = _temperatureRow.Value;
        _settings.TopP = _topPRow.Value;
        _settings.TopK = _topKRow.Value;
        _settings.MaxTokens = _maxTokensRow.Value;
        _settings.RepeatPenalty = _repeatPenaltyRow.Value;
        _settings.Save(_store);
        UpdateModelUi();
        SettingsStatus.Text = $"Gespeichert ({DateTime.Now:HH:mm:ss}).";

        if (serverChanged)
            _ = LoadModelsAsync();
    }

    private async void ReloadModels_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync(ServerBox.Text);

    // Holt die Modellliste aus der LM-Studio-REST-API; "vlm" = Modell versteht Bilder
    private async Task LoadModelsAsync(string? server = null)
    {
        server = (server ?? _settings.Server).Trim().TrimEnd('/');
        var selected = ModelCombo.SelectedValue as string ?? _settings.Model;
        SettingsStatus.Text = "Lade Modelle …";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var json = JsonNode.Parse(await Http.GetStringAsync(server + "/api/v0/models", cts.Token));
            var models = (json?["data"] as JsonArray ?? [])
                .Select(n => (Id: (string?)n?["id"], Type: (string?)n?["type"], State: (string?)n?["state"]))
                .Where(m => m.Id != null && m.Type is "llm" or "vlm")
                .Select(m => new ModelInfo(m.Id!, m.Type == "vlm", m.State == "loaded"))
                .OrderByDescending(m => m.Loaded).ThenBy(m => m.Id)
                .ToList();

            _models.Clear();
            foreach (var m in models)
                _models.Add(m);
            SettingsStatus.Text = $"{models.Count} Modelle gefunden.";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Modelle konnten nicht geladen werden: {ex.Message}";
        }
        EnsureModelItem(selected);
        ModelCombo.SelectedValue = selected;
        UpdateModelUi();
    }

    private void EnsureModelItem(string id)
    {
        if (id.Length > 0 && _models.All(m => m.Id != id))
            _models.Add(new ModelInfo(id, null, false));
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ModelHint.Text = (ModelCombo.SelectedItem as ModelInfo)?.Vision switch
        {
            true => "🖼 Dieses Modell versteht Bilder.",
            false => "Dieses Modell versteht nur Text.",
            _ => "Bildfähigkeit unbekannt (Server nicht erreichbar?)."
        };
    }

    private void UpdateModelUi()
    {
        var vision = CurrentVision;
        ModelInfoText.Text = $"Modell: {_settings.Model}" + vision switch
        {
            true => "   ·   🖼 versteht Bilder",
            false => "   ·   nur Text",
            _ => ""
        };
        ImageButton.IsEnabled = vision != false && _cts == null;
        ImageButton.ToolTip = vision == false
            ? "Dieses Modell kann keine Bilder verarbeiten"
            : "Bild anhängen (auch per Strg+V oder Hineinziehen)";
    }

    // ---------- Hilfe ----------

    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
