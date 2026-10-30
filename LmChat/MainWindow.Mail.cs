using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace LmChat;

// Seite „E-Mail-Automatik“: prüft alle x Sekunden den IMAP-Posteingang. Für jede neue Mail schreibt das Modell nach dem
// Prompt eine Antwort, die als Entwurf (an den Absender, mit zitierter Mail) im Ordner „Entwürfe“ landet.
// Die Original-Mail wird als gelesen markiert. Gesendet wird nie – Entwürfe prüft und verschickt der Nutzer selbst.
public partial class MainWindow
{
    private MailJob _mailJob = new();
    private readonly DispatcherTimer _mailTimer = new();
    private readonly ObservableCollection<ModelInfo> _mailModels = new();
    private readonly ObservableCollection<string> _mailLog = new();
    private readonly Dictionary<uint, int> _mailFailures = new();
    private CancellationTokenSource? _mailCts;
    private bool _mailBusy, _loadingMail;
    private int _mailModelLoadId;
    private DateTime? _mailLastCheck;

    private static readonly string[] DraftFolderNames = ["Drafts", "Entwürfe", "Draft", "Brouillons", "Borradores", "Bozze", "Concepts"];

    private void InitMail()
    {
        _mailJob = MailJob.Load(_store);
        _mailTimer.Tick += MailTimer_Tick;
        MailModelCombo.ItemsSource = _mailModels;
        MailLogList.ItemsSource = _mailLog;
        MailProviderCombo.ItemsSource = ProviderInfo.All;
        SetupReveal(MailPasswordBox, MailPasswordPlain, MailPasswordReveal);
        ShowMail();
        if (_mailJob.Enabled)
        {
            MailLog(L.T("Automatik läuft weiter (war beim Beenden aktiv)."));
            StartMailTimer();
        }
    }

    private void ShowMail()
    {
        _loadingMail = true;
        MailHostBox.Text = _mailJob.Host;
        MailPortBox.Text = _mailJob.Port.ToString();
        MailSecurityCombo.SelectedIndex = _mailJob.Security == "starttls" ? 1 : 0;
        MailUserBox.Text = _mailJob.User;
        MailPasswordBox.Password = _mailJob.Password;
        MailFromBox.Text = _mailJob.FromAddress;
        MailPromptBox.Text = _mailJob.Prompt;
        MailIntervalBox.Text = _mailJob.IntervalSeconds.ToString();
        MailProviderCombo.SelectedItem = ProviderInfo.Get(_mailJob.Provider);
        _loadingMail = false;
        _ = LoadMailModelsAsync();
        UpdateMailUi();
    }

    private void UpdateMailUi()
    {
        var running = _mailJob.Enabled;
        MailStartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        MailStopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        MailSettingsPanel.IsEnabled = !running;
        MailStatus.Text = running
            ? L.F("Läuft – prüft alle {0} s", _mailJob.IntervalSeconds) +
              (_mailLastCheck is { } t ? " · " + L.F("zuletzt {0:HH:mm:ss}", t) : "") +
              (_mailBusy ? " · " + L.T("arbeitet …") : "")
            : L.T("Gestoppt.");
        SetStatusDot(MailDot, NavMail, running, MailStatus.Text);
    }

    private void MailLog(string text)
    {
        _mailLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (_mailLog.Count > 200) _mailLog.RemoveAt(_mailLog.Count - 1);
    }

    // ---------- Bedienung ----------

    private void MailSecurity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMail || MailPortBox == null) return;
        // Üblicher Port zur gewählten Verschlüsselung
        if (MailPortBox.Text is "993" or "143" or "")
            MailPortBox.Text = MailSecurityCombo.SelectedIndex == 1 ? "143" : "993";
    }

    private async void MailProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingMail) await LoadMailModelsAsync();
    }

    private async Task LoadMailModelsAsync()
    {
        if (MailProviderCombo.SelectedItem is not ProviderInfo info) return;
        var config = _settings.Config(info.Kind);
        var wanted = info.Kind == _mailJob.Provider && _mailJob.Model.Length > 0 ? _mailJob.Model : config.Model;
        _mailModels.Clear();
        if (info.NeedsKey && config.ApiKey.Length == 0)
        {
            MailModelHint.Text = L.F("Für {0} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.", info.Name);
            return;
        }
        var loadId = ++_mailModelLoadId;
        MailModelHint.Text = L.F("Lade Modelle von {0} …", info.Name);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RegionOption.IsHosterFilter(config.Region) ? 180 : 30));
            var models = await LlmClient.ListModelsAsync(info, config.Server, config.Region, config.ApiKey, cts.Token);
            if (loadId != _mailModelLoadId) return;
            foreach (var m in models.Where(m => !m.ImageGen))
                _mailModels.Add(m);
            MailModelHint.Text = "";
        }
        catch (Exception ex)
        {
            if (loadId != _mailModelLoadId) return;
            MailModelHint.Text = L.F("Modelle von {0} konnten nicht geladen werden: {1}", info.Name, ex.Message);
            if (wanted.Length > 0) _mailModels.Add(new ModelInfo(wanted, null, false));
        }
        MailModelCombo.SelectedValue = _mailModels.Any(m => m.Id == wanted) ? wanted : _mailModels.FirstOrDefault()?.Id;
    }

    // Eingaben prüfen und in einen Auftrag übernehmen (null = Fehler, steht dann in der Statuszeile)
    private MailJob? ReadMailForm(bool needModel)
    {
        var job = new MailJob
        {
            Host = MailHostBox.Text.Trim(),
            Security = MailSecurityCombo.SelectedIndex == 1 ? "starttls" : "ssl",
            User = MailUserBox.Text.Trim(),
            FromAddress = MailFromBox.Text.Trim(),
            Prompt = MailPromptBox.Text.Trim()
        };
        job.Password = MailPasswordBox.Password;
        if (job.Host.Length == 0 || job.User.Length == 0 || job.Password.Length == 0)
        {
            MailStatus.Text = L.T("Bitte Server, Benutzername und Passwort eintragen.");
            return null;
        }
        if (!int.TryParse(MailPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MailStatus.Text = L.T("Bitte einen gültigen Port eintragen (meist 993).");
            return null;
        }
        job.Port = port;
        if (!needModel) return job;

        if (!MailboxAddress.TryParse(job.Sender, out _))
        {
            MailStatus.Text = L.T("Bitte eine gültige Absender-Adresse eintragen.");
            return null;
        }
        if (job.Prompt.Length == 0) { MailStatus.Text = L.T("Bitte einen Prompt eingeben."); return null; }
        if (!int.TryParse(MailIntervalBox.Text.Trim(), out var interval) || interval < MailJob.MinInterval)
        {
            MailStatus.Text = L.F("Intervall: mindestens {0} Sekunden.", MailJob.MinInterval);
            return null;
        }
        if (MailProviderCombo.SelectedItem is not ProviderInfo provider || MailModelCombo.SelectedItem is not ModelInfo model)
        {
            MailStatus.Text = L.T("Bitte Anbieter und Modell wählen.");
            return null;
        }
        if (provider.NeedsKey && _settings.Config(provider.Kind).ApiKey.Length == 0)
        {
            MailStatus.Text = L.F("Für {0} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.", provider.Name);
            return null;
        }
        job.IntervalSeconds = interval;
        job.Provider = provider.Kind;
        job.Model = model.Id;
        return job;
    }

    private static async Task<ImapClient> ConnectAsync(MailJob job, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(job.Host, job.Port,
                job.Security == "starttls" ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(job.User, job.Password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // Ordner „Entwürfe“: über die Kennzeichnung des Servers (\Drafts), sonst über übliche Namen
    private static async Task<IMailFolder?> FindDraftsAsync(ImapClient client, CancellationToken ct)
    {
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            try
            {
                if (client.GetFolder(SpecialFolder.Drafts) is { } special) return special;
            }
            catch (NotSupportedException) { }
        }
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var folders = await root.GetSubfoldersAsync(true, ct);
        return folders.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Drafts))
               ?? folders.FirstOrDefault(f => DraftFolderNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
    }

    private async void MailTest_Click(object sender, RoutedEventArgs e)
    {
        if (ReadMailForm(needModel: false) is not { } job) return;
        MailStatus.Text = L.T("Verbinde …");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = await ConnectAsync(job, cts.Token);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cts.Token);
            var drafts = await FindDraftsAsync(client, cts.Token);
            MailStatus.Text = L.F("✓ Verbunden – Posteingang: {0} Mails, Entwürfe-Ordner: {1}", client.Inbox.Count,
                drafts?.FullName ?? L.T("nicht gefunden"));
            await client.DisconnectAsync(true, cts.Token);
        }
        catch (Exception ex)
        {
            MailStatus.Text = L.T("✗ Keine Verbindung:") + " " + ex.Message;
        }
    }

    private async void MailStart_Click(object sender, RoutedEventArgs e)
    {
        if (ReadMailForm(needModel: true) is not { } job) return;
        var model = MailModelCombo.SelectedItem as ModelInfo;
        MailStatus.Text = L.T("Verbinde …");
        MailStartButton.IsEnabled = false;
        try
        {
            // Stand des Posteingangs merken: Was jetzt schon da ist, wird nicht bearbeitet
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = await ConnectAsync(job, cts.Token);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cts.Token);
            if (await FindDraftsAsync(client, cts.Token) is null)
            {
                MailStatus.Text = L.T("Kein Ordner „Entwürfe“ gefunden – bitte im Mailprogramm prüfen.");
                return;
            }
            job.UidValidity = client.Inbox.UidValidity;
            job.LastUid = client.Inbox.UidNext is { } next ? next.Id - 1
                : (await client.Inbox.SearchAsync(SearchQuery.All, cts.Token)).Select(u => u.Id).DefaultIfEmpty(0u).Max();
            await client.DisconnectAsync(true, cts.Token);
        }
        catch (Exception ex)
        {
            MailStatus.Text = L.T("✗ Keine Verbindung:") + " " + ex.Message;
            return;
        }
        finally
        {
            MailStartButton.IsEnabled = true;
        }

        job.Enabled = true;
        _mailJob = job;
        _mailJob.Save(_store);
        _mailFailures.Clear();
        MailLog(L.T("Gestartet – Mails, die schon im Posteingang liegen, werden übersprungen.") +
                (model?.Vision == false ? " " + L.T("Das gewählte Modell versteht keine Bilder – Bildanhänge werden übersprungen.") : ""));
        StartMailTimer();
    }

    private void MailStop_Click(object sender, RoutedEventArgs e) => StopMail(L.T("Automatik gestoppt."));

    // Eingaben ohne Prüfung merken, damit sie nach einem Neustart wieder da sind (nur solange die Automatik aus ist)
    private void SaveMailDraft()
    {
        if (_mailJob.Enabled || MailHostBox == null) return;
        _mailJob.Host = MailHostBox.Text.Trim();
        if (int.TryParse(MailPortBox.Text.Trim(), out var port) && port is > 0 and <= 65535) _mailJob.Port = port;
        _mailJob.Security = MailSecurityCombo.SelectedIndex == 1 ? "starttls" : "ssl";
        _mailJob.User = MailUserBox.Text.Trim();
        _mailJob.Password = MailPasswordBox.Password;
        _mailJob.FromAddress = MailFromBox.Text.Trim();
        _mailJob.Prompt = MailPromptBox.Text.Trim();
        if (int.TryParse(MailIntervalBox.Text.Trim(), out var interval) && interval > 0) _mailJob.IntervalSeconds = interval;
        if (MailProviderCombo.SelectedItem is ProviderInfo provider)
        {
            if (MailModelCombo.SelectedItem is ModelInfo model) _mailJob.Model = model.Id;
            else if (provider.Kind != _mailJob.Provider) _mailJob.Model = "";
            _mailJob.Provider = provider.Kind;
        }
        _mailJob.Save(_store);
    }

    private void StartMailTimer()
    {
        _mailTimer.Interval = TimeSpan.FromSeconds(Math.Max(MailJob.MinInterval, _mailJob.IntervalSeconds));
        _mailTimer.Start();
        UpdateMailUi();
        MailTimer_Tick(this, EventArgs.Empty);
    }

    private void StopMail(string message)
    {
        _mailTimer.Stop();
        _mailCts?.Cancel();
        _mailJob.Enabled = false;
        _mailJob.Save(_store);
        MailLog(message);
        UpdateMailUi();
    }

    // ---------- Prüfen und bearbeiten ----------

    private async void MailTimer_Tick(object? sender, EventArgs e)
    {
        if (_mailBusy || !_mailJob.Enabled) return;
        _mailBusy = true;
        _mailCts = new CancellationTokenSource();
        UpdateMailUi();
        try
        {
            await RunMailOnceAsync(_mailCts.Token);
        }
        catch (AuthenticationException ex)
        {
            StopMail(L.T("Anmeldung fehlgeschlagen – Automatik angehalten.") + " " + ex.Message);
        }
        catch (OperationCanceledException) when (_mailCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MailLog("✗ " + ex.Message);   // z. B. Server kurz nicht erreichbar – beim nächsten Durchlauf erneut
        }
        finally
        {
            _mailCts.Dispose();
            _mailCts = null;
            _mailBusy = false;
            _mailLastCheck = DateTime.Now;
            UpdateMailUi();
        }
    }

    private async Task RunMailOnceAsync(CancellationToken ct)
    {
        var job = _mailJob;
        using var client = await ConnectAsync(job, ct);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

        // Posteingang wurde auf dem Server neu angelegt (UIDs ungültig): Stand neu festlegen, nichts nachträglich bearbeiten
        if (inbox.UidValidity != job.UidValidity)
        {
            job.UidValidity = inbox.UidValidity;
            job.LastUid = (await inbox.SearchAsync(SearchQuery.All, ct)).Select(u => u.Id).DefaultIfEmpty(0u).Max();
            job.Save(_store);
            MailLog(L.T("Posteingang wurde auf dem Server neu nummeriert – vorhandene Mails werden übersprungen."));
            return;
        }

        var pending = (await inbox.SearchAsync(SearchQuery.All, ct)).Where(u => u.Id > job.LastUid).OrderBy(u => u.Id).ToList();
        if (pending.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return;
        }
        var drafts = await FindDraftsAsync(client, ct)
                     ?? throw new InvalidOperationException(L.T("Kein Ordner „Entwürfe“ gefunden – bitte im Mailprogramm prüfen."));
        var provider = ProviderInfo.Get(job.Provider);
        var vision = _mailModels.FirstOrDefault(m => m.Id == job.Model)?.Vision != false;

        foreach (var uid in pending)
        {
            if (ct.IsCancellationRequested || !_mailJob.Enabled) break;
            if (await BudgetBlockReasonAsync(provider) is { } reason)
            {
                StopMail(reason + " " + L.T("Automatik angehalten."));
                break;
            }
            var done = await ProcessMailAsync(job, provider, vision, inbox, drafts, uid, ct);
            // Reihenfolge einhalten: Schlägt eine Mail fehl, beim nächsten Durchlauf erneut versuchen (höchstens 3-mal)
            if (!done) break;
            job.LastUid = uid.Id;
            job.Save(_store);
        }
        await client.DisconnectAsync(true, ct);
    }

    // true = erledigt (Entwurf angelegt oder nach zu vielen Fehlversuchen übersprungen)
    private async Task<bool> ProcessMailAsync(MailJob job, ProviderInfo provider, bool vision, IMailFolder inbox,
        IMailFolder drafts, UniqueId uid, CancellationToken ct)
    {
        var label = "";   // Absender und Betreff fürs Protokoll
        try
        {
            var message = await inbox.GetMessageAsync(uid, ct);
            var from = message.From.Mailboxes.FirstOrDefault();
            var sender = from == null ? "?" : from.Name is { Length: > 0 } name ? $"{name} <{from.Address}>" : from.Address;
            label = $"{sender} · „{message.Subject}“";
            var content = MailTools.Read(message, vision, ImageToPng);

            var config = _settings.Config(provider.Kind);
            var task = job.Prompt + "\n\nWrite the reply to the e-mail given as data below.";
            const string format = "Plain text only: just the body of the reply e-mail – no subject line, no headers, " +
                                  "no quotation of the original e-mail, no Markdown.";
            var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, job.Model,
                PromptGuard.SystemPrompt(task, format), _settings, [new ChatMessage("user", content.ForModel(), content.Image)]);
            var answer = new StringBuilder();
            var result = new LlmClient.StreamResult();
            // Keine Werkzeuge (tools = null): Das Modell kann nichts ausführen, nur Text zurückgeben
            await LlmClient.StreamAsync(request, chunk => answer.Append(chunk), _ => { }, result, null, ct);
            var text = System.Text.RegularExpressions.Regex.Replace(answer.ToString(),
                @"^\s*<think>[\s\S]*?(</think>|$)|<\|channel\|?>[\s\S]*?<\|?channel\|>", "").Trim();
            if (text.Length == 0) throw new InvalidDataException(L.T("Das Modell hat keine Antwort geliefert."));

            var reply = MailTools.BuildReply(message, job.Sender, text, content.Warnings);
            // Entwurf ablegen (der Ordner muss dafür nicht geöffnet sein), dann Original als gelesen markieren
            await drafts.AppendAsync(new AppendRequest(reply, MessageFlags.Draft | MessageFlags.Seen), ct);
            await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);

            _store.AddUsage(provider.Kind, new AnswerStats(provider.Name, result.Model ?? job.Model, result.Hoster,
                result.InputTokens, result.OutputTokens, null, 0, result.Cost));
            MailLog($"✓ {label} → " + L.T("Entwurf angelegt") +
                    (content.Documents.Count + (content.Image != null ? 1 : 0) > 0
                        ? " · " + L.F("{0} Anhang/Anhänge gelesen", content.Documents.Count + (content.Image != null ? 1 : 0)) : "") +
                    (content.Warnings.Count > 0 ? "  · " + PromptGuard.Warning(content.Warnings) : ""));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not AuthenticationException)
        {
            var attempts = _mailFailures[uid.Id] = _mailFailures.GetValueOrDefault(uid.Id) + 1;
            var skip = attempts >= MaxAttempts;
            MailLog($"✗ {(label.Length > 0 ? label : "UID " + uid.Id)}: {ex.Message}" + (skip ? "  · " + L.F("nach {0} Fehlversuchen übersprungen", MaxAttempts) : ""));
            return skip;
        }
    }

    // Bildanhang für das Modell: auf höchstens 2048 px verkleinern, als PNG (null = kein lesbares Bild)
    private static byte[]? ImageToPng(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(data);
            bitmap.EndInit();
            var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            BitmapSource scaled = scale < 1 ? new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)) : bitmap;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
