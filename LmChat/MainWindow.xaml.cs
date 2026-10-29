using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LmChat;

public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];
    private const int MaxImageSide = 2048;

    private readonly ChatStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LmChat", "chats.db"));
    private readonly ObservableCollection<ChatInfo> _chats = new();
    private readonly ObservableCollection<ModelInfo> _models = new();
    private readonly List<ChatMessage> _messages = new();

    // _settings = gespeicherter, aktiver Stand; _draft = was gerade auf der Einstellungsseite bearbeitet wird
    private LlmSettings _settings;
    private LlmSettings _draft;
    private ProviderKind? _shownProvider;
    private int _modelLoadId;

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
        _draft = _settings.Clone();
        foreach (var chat in _store.ListChats())
            _chats.Add(chat);
        ChatList.ItemsSource = _chats;
        ProviderCombo.ItemsSource = ProviderInfo.All;
        ModelCombo.ItemsSource = _models;
        foreach (var row in ParamRows)
            ParamsPanel.Children.Add(row.Element);
        ShowSettings();
        // Beim Befüllen der Einstellungen kann ein Element dort den Fokus bekommen – immer im Chat starten
        Loaded += (_, _) =>
        {
            MainTabs.SelectedIndex = 0;
            SettingsScroll.ScrollToTop();
            InputBox.Focus();
        };
    }

    private ProviderInfo ActiveProvider => ProviderInfo.Get(_settings.Provider);
    private bool? CurrentVision => _settings.Current.ModelVision;

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

        var provider = ActiveProvider;
        var config = _settings.Current;
        if (config.Model.Length == 0)
        {
            StatusText.Text = "Bitte zuerst unter „Einstellungen“ ein Modell auswählen und speichern.";
            return;
        }
        if (provider.NeedsKey && config.ApiKey.Length == 0)
        {
            StatusText.Text = $"Für {provider.Name} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.";
            return;
        }
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
        var answerView = AddMessageBlock(new ChatMessage("assistant", ""), streaming: true);

        List<ChatMessage> toSend = HistoryCheck.IsChecked == true ? _messages.ToList() : [userMsg];
        var request = new ChatRequest(provider, config.Server, config.ApiKey, config.Model, _settings.SystemPrompt, _settings, toSend);

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var answer = new StringBuilder();
        string? note = null;
        try
        {
            await LlmClient.StreamAsync(request, chunk =>
            {
                answer.Append(chunk);
                answerView.Stream!.Text += chunk;
                ChatBox.ScrollToEnd();
            }, _cts.Token);
            StatusText.Text = "Fertig.";
        }
        catch (OperationCanceledException)
        {
            note = "[abgebrochen]";
            StatusText.Text = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            note = $"[Fehler: {ex.Message}]";
            StatusText.Text = "Fehler – Anbieter, Schlüssel und Modell in den Einstellungen prüfen.";
        }
        finally
        {
            // Fertige Antwort als Markdown darstellen (während des Streamings war es reiner Text)
            answerView.Body.Blocks.Clear();
            if (answer.Length > 0)
                MarkdownRenderer.Render(answer.ToString(), answerView.Body.Blocks);
            if (note != null)
                answerView.Body.Blocks.Add(new Paragraph(new Italic(new Run(note))) { Margin = new Thickness(0) });
            ChatBox.ScrollToEnd();

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

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cts?.Cancel();

    // ---------- Chatliste und Anzeige ----------

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

    // Body = Inhalt der Nachricht; Stream = Text, in den während des Empfangs geschrieben wird
    private sealed record MessageView(Section Body, Run? Stream);

    private MessageView AddMessageBlock(ChatMessage m, bool streaming = false)
    {
        var blocks = ChatBox.Document.Blocks;
        blocks.Add(new Paragraph(new Run(m.Role == "user" ? "Du" : "LLM") { FontWeight = FontWeights.Bold })
        {
            Margin = new Thickness(0, blocks.Count == 0 ? 0 : 16, 0, 4)
        });
        if (m.Image != null)
            blocks.Add(new BlockUIContainer(MakeThumbnail(m.Image)) { Margin = new Thickness(0, 0, 0, 6) });

        var body = new Section();
        blocks.Add(body);
        Run? stream = null;
        if (streaming)
        {
            stream = new Run();
            body.Blocks.Add(new Paragraph(stream) { Margin = new Thickness(0) });
        }
        else if (m.Role == "assistant")
        {
            MarkdownRenderer.Render(m.Content, body.Blocks);
        }
        else
        {
            body.Blocks.Add(new Paragraph(new Run(m.Content)) { Margin = new Thickness(0) });
        }
        ChatBox.ScrollToEnd();
        return new MessageView(body, stream);
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
        "1000 Tokens sind etwa ein bis zwei Seiten Text. Ohne Haken: keine Begrenzung (bei Anthropic: 64000).",
        64, 32768, 64, 2048, "0");

    private readonly ParamRow _repeatPenaltyRow = new("Wiederholungsstrafe",
        "Bremst das Modell, wenn es sich wiederholt. 1,00 = aus, 1,10 = leicht (üblich). " +
        "Über 1,30 kann die Sprache holprig werden.",
        1, 2, 0.01, 1.1, "0.00");

    private IEnumerable<ParamRow> ParamRows => [_temperatureRow, _topPRow, _topKRow, _maxTokensRow, _repeatPenaltyRow];

    private ProviderInfo ShownProvider => ProviderInfo.Get(_shownProvider ?? _draft.Provider);

    private void ShowSettings()
    {
        SystemPromptBox.Text = _draft.SystemPrompt;
        _temperatureRow.Value = _draft.Temperature;
        _topPRow.Value = _draft.TopP;
        _topKRow.Value = _draft.TopK;
        _maxTokensRow.Value = _draft.MaxTokens;
        _repeatPenaltyRow.Value = _draft.RepeatPenalty;
        _shownProvider = null;
        ProviderCombo.SelectedItem = ProviderInfo.Get(_draft.Provider);
        UpdateModelUi();
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderCombo.SelectedItem is not ProviderInfo info) return;
        // Eingaben des bisher gezeigten Anbieters im Entwurf merken, bevor gewechselt wird
        if (_shownProvider is { } previous)
            StoreProviderFields(previous);
        ShowProvider(info);
    }

    private void StoreProviderFields(ProviderKind kind)
    {
        var config = _draft.Config(kind);
        config.Server = ServerBox.Text.Trim().TrimEnd('/');
        config.ApiKey = ApiKeyBox.Password.Trim();
        if (ModelCombo.SelectedItem is ModelInfo model)
        {
            config.Model = model.Id;
            config.ModelVision = model.Vision;
        }
    }

    private void ShowProvider(ProviderInfo info)
    {
        _shownProvider = info.Kind;
        var config = _draft.Config(info.Kind);
        ProviderHint.Text = info.Description;
        ProviderLinks.Inlines.Clear();
        ProviderLinks.Inlines.Add(new Run("Links:  ") { FontWeight = FontWeights.SemiBold });
        foreach (var (text, url) in info.Links)
        {
            if (ProviderLinks.Inlines.Count > 1)
                ProviderLinks.Inlines.Add(new Run("   ·   ") { Foreground = Brushes.Gray });
            var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = url };
            link.RequestNavigate += Link_RequestNavigate;
            ProviderLinks.Inlines.Add(link);
        }
        ServerBox.Text = config.Server;
        ApiKeyPanel.Visibility = info.NeedsKey ? Visibility.Visible : Visibility.Collapsed;
        if (info.KeyUrl != null)
        {
            KeyLink.NavigateUri = new Uri(info.KeyUrl);
            KeyLinkText.Text = info.KeyUrl.Replace("https://", "");
        }
        ApiKeyBox.Password = config.ApiKey;

        _temperatureRow.SetSupported(info.SupportsSampling, info.Name);
        _topPRow.SetSupported(info.SupportsSampling, info.Name);
        _topKRow.SetSupported(info.SupportsTopK, info.Name);
        _repeatPenaltyRow.SetSupported(info.SupportsRepeatPenalty, info.Name);

        _models.Clear();
        EnsureModelItem(config.Model, config.ModelVision);
        ModelCombo.SelectedValue = config.Model;
        _ = LoadModelsAsync();
    }

    private void DefaultServer_Click(object sender, RoutedEventArgs e) => ServerBox.Text = ShownProvider.DefaultServer;

    private void ResetParams_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ParamRows)
            row.Value = null;
        SettingsStatus.Text = "Alle Feineinstellungen auf Standard – zum Übernehmen „Speichern“ klicken.";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var info = ShownProvider;
        var server = ServerBox.Text.Trim().TrimEnd('/');
        if (!server.Contains("://"))
            server = (info.IsLocal ? "http://" : "https://") + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            SettingsStatus.Text = $"Die Server-Adresse ist ungültig. Standard für {info.Name}: {info.DefaultServer}";
            return;
        }
        if (info.NeedsKey && ApiKeyBox.Password.Trim().Length == 0)
        {
            SettingsStatus.Text = $"Für {info.Name} wird ein API-Schlüssel benötigt.";
            return;
        }
        if (ModelCombo.SelectedItem is not ModelInfo)
        {
            SettingsStatus.Text = "Bitte ein Modell auswählen (ggf. erst „Liste aktualisieren“).";
            return;
        }

        ServerBox.Text = server;
        StoreProviderFields(info.Kind);
        _draft.Provider = info.Kind;
        _draft.SystemPrompt = SystemPromptBox.Text.Trim();
        _draft.Temperature = _temperatureRow.Value;
        _draft.TopP = _topPRow.Value;
        _draft.TopK = _topKRow.Value;
        _draft.MaxTokens = _maxTokensRow.Value;
        _draft.RepeatPenalty = _repeatPenaltyRow.Value;
        _draft.Save(_store);
        _settings = _draft.Clone();
        UpdateModelUi();
        SettingsStatus.Text = $"Gespeichert – aktiv: {info.Name}, {_settings.Current.Model} ({DateTime.Now:HH:mm:ss}).";
    }

    private async void ReloadModels_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync();

    // Holt die Modellliste des gerade gezeigten Anbieters (mit Adresse und Schlüssel aus den Eingabefeldern)
    private async Task LoadModelsAsync()
    {
        var info = ShownProvider;
        var key = ApiKeyBox.Password.Trim();
        if (info.NeedsKey && key.Length == 0)
        {
            SettingsStatus.Text = $"Für die Modellliste von {info.Name} bitte zuerst den API-Schlüssel eintragen.";
            return;
        }

        var selected = ModelCombo.SelectedValue as string ?? _draft.Config(info.Kind).Model;
        var loadId = ++_modelLoadId;
        SettingsStatus.Text = $"Lade Modelle von {info.Name} …";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await LlmClient.ListModelsAsync(info, ServerBox.Text, key, cts.Token);
            if (loadId != _modelLoadId) return; // inzwischen anderer Anbieter gewählt
            _models.Clear();
            foreach (var m in models)
                _models.Add(m);
            SettingsStatus.Text = $"{models.Count} Modelle von {info.Name} gefunden.";
        }
        catch (Exception ex)
        {
            if (loadId != _modelLoadId) return;
            SettingsStatus.Text = $"Modelle konnten nicht geladen werden: {ex.Message}";
        }
        EnsureModelItem(selected, null);
        ModelCombo.SelectedValue = selected;

        // Bildfähigkeit des aktiven Modells aktualisieren (z. B. nach dem Programmstart)
        if (info.Kind == _settings.Provider &&
            _models.FirstOrDefault(m => m.Id == _settings.Current.Model) is { Vision: not null } active &&
            active.Vision != _settings.Current.ModelVision)
        {
            _settings.Current.ModelVision = active.Vision;
            _draft.Config(info.Kind).ModelVision = active.Vision;
            _settings.Save(_store);
            UpdateModelUi();
        }
    }

    private void EnsureModelItem(string id, bool? vision)
    {
        if (id.Length > 0 && _models.All(m => m.Id != id))
            _models.Add(new ModelInfo(id, vision, false));
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ModelHint.Text = (ModelCombo.SelectedItem as ModelInfo)?.Vision switch
        {
            true => "🖼 Dieses Modell versteht Bilder.",
            false => "Dieses Modell versteht nur Text.",
            _ => ModelCombo.SelectedItem == null ? "" : "Ob das Modell Bilder versteht, meldet dieser Anbieter nicht – einfach ausprobieren."
        };
    }

    private void UpdateModelUi()
    {
        var vision = CurrentVision;
        var model = _settings.Current.Model;
        ModelInfoText.Text = $"{ActiveProvider.Name}   ·   {(model.Length > 0 ? model : "kein Modell gewählt")}" + vision switch
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
