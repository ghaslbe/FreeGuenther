using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace LmChat;

// Seite „E-Mail-Automatik“: prüft alle x Sekunden den IMAP-Posteingang. Für jede neue Mail schreibt das Modell nach dem
// Prompt eine Antwort, die als Entwurf (an den Absender, mit zitierter Mail) im Ordner „Entwürfe“ landet.
// Die Original-Mail wird als gelesen markiert. Gesendet wird nie – Entwürfe prüft und verschickt der Nutzer selbst.
internal sealed partial class MainWindow
{
    private MailJob _mailJob = new();
    private readonly Ticker _mailTimer = new();
    private readonly List<ModelInfo> _mailModels = new();
    private readonly List<string> _mailLog = new();
    private readonly Dictionary<uint, int> _mailFailures = new();
    private CancellationTokenSource? _mailCts;
    private bool _mailBusy, _loadingMail;
    private int _mailModelLoadId;
    private DateTime? _mailLastCheck;

    private static readonly string[] DraftFolderNames = ["Drafts", "Entwürfe", "Draft", "Brouillons", "Borradores", "Bozze", "Concepts"];

    private NSTextField _mailHost = null!, _mailPort = null!, _mailUser = null!, _mailFrom = null!, _mailInterval = null!;
    private WrapLabel _mailStatus = null!, _mailModelHint = null!;
    private NSPopUpButton _mailSecurity = null!, _mailProvider = null!, _mailModel = null!;
    private SecretField _mailPassword = null!;
    private TextArea _mailPrompt = null!, _mailLogBox = null!;
    private NSButton _mailStart = null!, _mailStop = null!;
    private NSView _mailSettings = null!;

    private NSView BuildMailPage()
    {
        var page = new Col(14);
        page.Add(Ui.PageTitle(L.UI("ui.EMailAutomatik")));
        page.Add(Ui.Hint(L.UI("ui.MailHinweis")));
        page.Add(Injection());

        var form = new Col(8);
        _mailStart = Ui.Button(L.UI("ui.Starten"), MailStart, primary: true);
        _mailStop = Ui.Button(L.UI("ui.Stoppen"), () => StopMail(L.T("Automatik gestoppt.")));
        var test = Ui.Button(L.UI("ui.VerbindungTesten"), MailTest);
        form.Add(Ui.Row(8, Ui.Heading(L.UI("ui.Auftrag")), Ui.Spacer(), test, _mailStart, _mailStop));
        _mailStatus = Ui.Hint();
        form.Add(_mailStatus);

        var fields = new Col(8);
        fields.Add(Ui.Label(L.UI("ui.Postfach"), 13, bold: true));
        _mailHost = Ui.Field(L.UI("ui.ImapServer"));
        _mailPort = Ui.Field();
        _mailPort.WidthAnchor.ConstraintEqualTo(70).Active = true;
        _mailSecurity = Ui.Popup(() =>
        {
            if (_loadingMail) return;
            // Üblicher Port zur gewählten Verschlüsselung
            if (_mailPort.StringValue is "993" or "143" or "")
                _mailPort.StringValue = _mailSecurity.IndexOfSelectedItem == 1 ? "143" : "993";
        });
        _mailSecurity.AddItem("SSL/TLS");
        _mailSecurity.AddItem("STARTTLS");
        fields.Add(Ui.Row(8, _mailHost, _mailPort, _mailSecurity));

        _mailUser = Ui.Field(L.UI("ui.Benutzername"));
        fields.Add(_mailUser);
        fields.Add(Ui.Label(L.UI("ui.Passwort"), bold: true));
        _mailPassword = new SecretField();
        fields.Add(_mailPassword);
        fields.Add(Ui.Hint(L.UI("ui.PasswortHinweis")));
        fields.Add(Ui.Label(L.UI("ui.AbsenderAdresse"), bold: true));
        _mailFrom = Ui.Field();
        fields.Add(_mailFrom);
        fields.Add(Ui.Hint(L.UI("ui.AbsenderHinweis")));

        fields.Add(Ui.Label(L.UI("ui.Prompt"), bold: true));
        fields.Add(Ui.Hint(L.UI("ui.MailPromptHinweis")));
        _mailPrompt = new TextArea(100);
        fields.Add(_mailPrompt);
        NSButton? library = null;
        library = Ui.Button(L.UI("ui.GespeichertePrompts"), () => ShowPromptLibrary(library!, _mailPrompt, MailPromptsKey, MailPromptDefaults));
        fields.Add(library, stretch: false);

        _mailInterval = Ui.Field();
        _mailInterval.WidthAnchor.ConstraintEqualTo(70).Active = true;
        fields.Add(Ui.Row(8, Ui.Label(L.UI("ui.PruefenAlle")), _mailInterval, Ui.Spacer()));

        fields.Add(Ui.Label(L.UI("ui.AnbieterUndModell"), bold: true));
        _mailProvider = Ui.Popup(async () => { if (!_loadingMail) await LoadMailModelsAsync(); });
        foreach (var info in ProviderInfo.All)
            _mailProvider.AddItem(info.Label);
        _mailModel = Ui.Popup();
        fields.Add(Ui.Row(8, _mailProvider, _mailModel));
        _mailModelHint = Ui.Hint();
        fields.Add(_mailModelHint);
        _mailSettings = fields;
        form.Add(fields);
        page.Add(Ui.Card(form));

        var log = new Col(6);
        log.Add(Ui.Heading(L.UI("ui.Protokoll")));
        _mailLogBox = new TextArea(180, editable: false, mono: true);
        log.Add(_mailLogBox);
        page.Add(Ui.Card(log));
        return Ui.ScrollPage(page, 860);
    }

    private ProviderInfo MailProviderInfo => ProviderInfo.All[Math.Max(0, (int)_mailProvider.IndexOfSelectedItem)];

    private ModelInfo? MailModelInfo
    {
        get
        {
            var index = (int)_mailModel.IndexOfSelectedItem;
            return index >= 0 && index < _mailModels.Count ? _mailModels[index] : null;
        }
    }

    private void InitMail()
    {
        _mailJob = MailJob.Load(_store);
        _mailTimer.Tick += MailTick;
        _loadingMail = true;
        _mailHost.StringValue = _mailJob.Host;
        _mailPort.StringValue = _mailJob.Port.ToString();
        _mailSecurity.SelectItem(_mailJob.Security == "starttls" ? 1 : 0);
        _mailUser.StringValue = _mailJob.User;
        _mailPassword.Value = _mailJob.Password;
        _mailFrom.StringValue = _mailJob.FromAddress;
        _mailPrompt.Text = _mailJob.Prompt;
        _mailInterval.StringValue = _mailJob.IntervalSeconds.ToString();
        _mailProvider.SelectItem(Array.FindIndex(ProviderInfo.All, p => p.Kind == _mailJob.Provider));
        _loadingMail = false;
        _ = LoadMailModelsAsync();
        UpdateMailUi();
        if (_mailJob.Enabled)
        {
            MailLog(L.T("Automatik läuft weiter (war beim Beenden aktiv)."));
            StartMailTimer();
        }
    }

    private void UpdateMailUi()
    {
        var running = _mailJob.Enabled;
        _mailStart.Hidden = running;
        _mailStop.Hidden = !running;
        SetEnabled(_mailSettings, !running);
        _mailPrompt.Enabled = !running;
        _mailStatus.StringValue = running
            ? L.F("Läuft – prüft alle {0} s", _mailJob.IntervalSeconds) +
              (_mailLastCheck is { } t ? " · " + L.F("zuletzt {0:HH:mm:ss}", t) : "") +
              (_mailBusy ? " · " + L.T("arbeitet …") : "")
            : L.T("Gestoppt.");
        SetStatusDot(Page.Mail, running, _mailStatus.StringValue);
    }

    private void MailLog(string text)
    {
        _mailLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (_mailLog.Count > 200) _mailLog.RemoveAt(_mailLog.Count - 1);
        _mailLogBox.Text = string.Join("\n", _mailLog);
    }

    private async Task LoadMailModelsAsync()
    {
        var info = MailProviderInfo;
        var config = _settings.Config(info.Kind);
        var wanted = info.Kind == _mailJob.Provider && _mailJob.Model.Length > 0 ? _mailJob.Model : config.Model;
        _mailModels.Clear();
        _mailModel.RemoveAllItems();
        if (info.NeedsKey && config.ApiKey.Length == 0)
        {
            _mailModelHint.StringValue = L.F("Für {0} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.", info.Name);
            return;
        }
        var loadId = ++_mailModelLoadId;
        _mailModelHint.StringValue = L.F("Lade Modelle von {0} …", info.Name);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RegionOption.IsHosterFilter(config.Region) ? 180 : 30));
            var models = await LlmClient.ListModelsAsync(info, config.Server, config.Region, config.ApiKey, cts.Token);
            if (loadId != _mailModelLoadId) return;
            _mailModels.AddRange(models.Where(m => !m.ImageGen));
            _mailModelHint.StringValue = "";
        }
        catch (Exception ex)
        {
            if (loadId != _mailModelLoadId) return;
            _mailModelHint.StringValue = L.F("Modelle von {0} konnten nicht geladen werden: {1}", info.Name, ex.Message);
            if (wanted.Length > 0) _mailModels.Add(new ModelInfo(wanted, null, false));
        }
        FillModels(_mailModel, _mailModels, wanted);
    }

    // Eingaben prüfen und in einen Auftrag übernehmen (null = Fehler, steht dann in der Statuszeile)
    private MailJob? ReadMailForm(bool needModel)
    {
        var job = new MailJob
        {
            Host = _mailHost.StringValue.Trim(),
            Security = _mailSecurity.IndexOfSelectedItem == 1 ? "starttls" : "ssl",
            User = _mailUser.StringValue.Trim(),
            FromAddress = _mailFrom.StringValue.Trim(),
            Prompt = _mailPrompt.Text.Trim()
        };
        job.Password = _mailPassword.Value;
        if (job.Host.Length == 0 || job.User.Length == 0 || job.Password.Length == 0)
        {
            _mailStatus.StringValue = L.T("Bitte Server, Benutzername und Passwort eintragen.");
            return null;
        }
        if (!int.TryParse(_mailPort.StringValue.Trim(), out var port) || port is < 1 or > 65535)
        {
            _mailStatus.StringValue = L.T("Bitte einen gültigen Port eintragen (meist 993).");
            return null;
        }
        job.Port = port;
        if (!needModel) return job;

        if (!MailboxAddress.TryParse(job.Sender, out _))
        {
            _mailStatus.StringValue = L.T("Bitte eine gültige Absender-Adresse eintragen.");
            return null;
        }
        if (job.Prompt.Length == 0) { _mailStatus.StringValue = L.T("Bitte einen Prompt eingeben."); return null; }
        if (!int.TryParse(_mailInterval.StringValue.Trim(), out var interval) || interval < MailJob.MinInterval)
        {
            _mailStatus.StringValue = L.F("Intervall: mindestens {0} Sekunden.", MailJob.MinInterval);
            return null;
        }
        var provider = MailProviderInfo;
        if (MailModelInfo is not { } model)
        {
            _mailStatus.StringValue = L.T("Bitte Anbieter und Modell wählen.");
            return null;
        }
        if (provider.NeedsKey && _settings.Config(provider.Kind).ApiKey.Length == 0)
        {
            _mailStatus.StringValue = L.F("Für {0} fehlt der API-Schlüssel – bitte unter „Einstellungen“ eintragen.", provider.Name);
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

    private async void MailTest()
    {
        if (ReadMailForm(needModel: false) is not { } job) return;
        _mailStatus.StringValue = L.T("Verbinde …");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = await ConnectAsync(job, cts.Token);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cts.Token);
            var drafts = await FindDraftsAsync(client, cts.Token);
            _mailStatus.StringValue = L.F("✓ Verbunden – Posteingang: {0} Mails, Entwürfe-Ordner: {1}", client.Inbox.Count,
                drafts?.FullName ?? L.T("nicht gefunden"));
            await client.DisconnectAsync(true, cts.Token);
        }
        catch (Exception ex)
        {
            _mailStatus.StringValue = L.T("✗ Keine Verbindung:") + " " + ex.Message;
        }
    }

    private async void MailStart()
    {
        if (ReadMailForm(needModel: true) is not { } job) return;
        var model = MailModelInfo;
        _mailStatus.StringValue = L.T("Verbinde …");
        _mailStart.Enabled = false;
        try
        {
            // Stand des Posteingangs merken: Was jetzt schon da ist, wird nicht bearbeitet
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = await ConnectAsync(job, cts.Token);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, cts.Token);
            if (await FindDraftsAsync(client, cts.Token) is null)
            {
                _mailStatus.StringValue = L.T("Kein Ordner „Entwürfe“ gefunden – bitte im Mailprogramm prüfen.");
                return;
            }
            job.UidValidity = client.Inbox.UidValidity;
            job.LastUid = client.Inbox.UidNext is { } next ? next.Id - 1
                : (await client.Inbox.SearchAsync(SearchQuery.All, cts.Token)).Select(u => u.Id).DefaultIfEmpty(0u).Max();
            await client.DisconnectAsync(true, cts.Token);
        }
        catch (Exception ex)
        {
            _mailStatus.StringValue = L.T("✗ Keine Verbindung:") + " " + ex.Message;
            return;
        }
        finally
        {
            _mailStart.Enabled = true;
        }

        job.Enabled = true;
        _mailJob = job;
        _mailJob.Save(_store);
        _mailFailures.Clear();
        MailLog(L.T("Gestartet – Mails, die schon im Posteingang liegen, werden übersprungen.") +
                (model?.Vision == false ? " " + L.T("Das gewählte Modell versteht keine Bilder – Bildanhänge werden übersprungen.") : ""));
        StartMailTimer();
    }

    // Eingaben ohne Prüfung merken, damit sie nach einem Neustart wieder da sind (nur solange die Automatik aus ist)
    private void SaveMailDraft()
    {
        if (_mailJob.Enabled || _mailHost == null) return;
        _mailJob.Host = _mailHost.StringValue.Trim();
        if (int.TryParse(_mailPort.StringValue.Trim(), out var port) && port is > 0 and <= 65535) _mailJob.Port = port;
        _mailJob.Security = _mailSecurity.IndexOfSelectedItem == 1 ? "starttls" : "ssl";
        _mailJob.User = _mailUser.StringValue.Trim();
        _mailJob.Password = _mailPassword.Value;
        _mailJob.FromAddress = _mailFrom.StringValue.Trim();
        _mailJob.Prompt = _mailPrompt.Text.Trim();
        if (int.TryParse(_mailInterval.StringValue.Trim(), out var interval) && interval > 0) _mailJob.IntervalSeconds = interval;
        var provider = MailProviderInfo;
        if (MailModelInfo is { } model) _mailJob.Model = model.Id;
        else if (provider.Kind != _mailJob.Provider) _mailJob.Model = "";
        _mailJob.Provider = provider.Kind;
        _mailJob.Save(_store);
    }

    private void StartMailTimer()
    {
        _mailTimer.Start(Math.Max(MailJob.MinInterval, _mailJob.IntervalSeconds));
        UpdateMailUi();
        MailTick();
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

    private async void MailTick()
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
            var content = MailTools.Read(message, vision, data => Images.ToPng(data, Images.MaxImageSide));

            var config = _settings.Config(provider.Kind);
            var task = job.Prompt + "\n\nWrite the reply to the e-mail given as data below.";
            const string format = "Plain text only: just the body of the reply e-mail – no subject line, no headers, " +
                                  "no quotation of the original e-mail, no Markdown.";
            var request = new ChatRequest(provider, config.Server, config.Region, config.ApiKey, job.Model,
                PromptGuard.SystemPrompt(task, format), _settings, [new ChatMessage("user", content.ForModel(), content.Image)]);
            var answer = new StringBuilder();
            var result = new LlmClient.StreamResult();
            // Keine Werkzeuge (tools = null): Das Modell kann nichts ausführen, nur Text zurückgeben
            await LlmClient.StreamAsync(request, chunk => { lock (answer) answer.Append(chunk); }, _ => { }, result, null, ct);
            var text = Regex.Replace(answer.ToString(), @"^\s*<think>[\s\S]*?(</think>|$)|<\|channel\|?>[\s\S]*?<\|?channel\|>", "").Trim();
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
}
