using System.Globalization;

namespace LmChat;

// Seite „Verbrauch“ und Kostenbremse: Tages- und Monatsbudget für Online-Anbieter
internal sealed partial class MainWindow
{
    private enum CostKind { Local, Reported, Estimated, Partial, Unknown }

    private NSPopUpButton _usagePeriod = null!;
    private Col _usageList = null!;
    private NSView _usageTable = null!;
    private WrapLabel _usageEmpty = null!, _usageStatus = null!, _budgetSpent = null!, _budgetStatus = null!;
    private NSTextField _usageRequestsTile = null!, _usageTokensTile = null!, _usageCostTile = null!, _dailyBudgetBox = null!, _monthlyBudgetBox = null!;
    private NSButton _budgetHardCheck = null!;

    private NSView BuildUsagePage()
    {
        var page = new Col(14);
        page.Add(Ui.PageTitle(L.UI("ui.Verbrauch")));
        page.Add(Ui.Hint(L.UI("ui.WieVieleAnfragenUndTokens")));

        var warning = new Col(4);
        warning.Add(Ui.Heading(L.UI("ui.NurEineSchaetzungKeineAbrechnung")));
        warning.Add(Ui.Body(L.UI("ui.DieZahlenZaehltFreeGuentherSelbst")));
        warning.Add(new Rich().Text(L.UI("ui.DieExaktenKostenSiehstDu") + " ")
            .Link(L.UI("ui.OpenRouter"), "https://openrouter.ai/activity").Text(" · ")
            .Link(L.UI("ui.OpenAI"), "https://platform.openai.com/usage").Text(" · ")
            .Link(L.UI("ui.Anthropic"), "https://platform.claude.com/usage").View());
        page.Add(Ui.Card(warning, Ui.WarningFill, Ui.Warning.ColorWithAlphaComponent(0.4f)));

        var budget = new Col(8);
        budget.Add(Ui.Heading(L.UI("ui.Kostenbremse")));
        budget.Add(Ui.Hint(L.UI("ui.EinEigenesLimitInFreeGuenther")));
        _dailyBudgetBox = Ui.Field();
        _monthlyBudgetBox = Ui.Field();
        _dailyBudgetBox.WidthAnchor.ConstraintEqualTo(90).Active = true;
        _monthlyBudgetBox.WidthAnchor.ConstraintEqualTo(90).Active = true;
        budget.Add(Ui.Row(8, Ui.Label(L.UI("ui.ProTagUS")), _dailyBudgetBox, Ui.Label(L.UI("ui.ProMonatUS")), _monthlyBudgetBox, Ui.Spacer()));
        _budgetHardCheck = Ui.Check(L.UI("ui.HartSperrenBeiErreichtemLimit"));
        budget.Add(_budgetHardCheck);
        _budgetStatus = Ui.Hint();
        budget.Add(Ui.Row(8, Ui.Button(L.UI("ui.KostenbremseSpeichern"), SaveBudget), _budgetStatus, Ui.Spacer()));
        _budgetSpent = Ui.Hint();
        budget.Add(_budgetSpent);
        page.Add(Ui.Card(budget));

        _usagePeriod = Ui.Popup(() => _ = ShowUsageAsync());
        foreach (var key in new[] { "ui.Heute", "ui.Letzte7Tage", "ui.DieserMonat", "ui.Letzte30Tage", "ui.Gesamt" })
            _usagePeriod.AddItem(L.UI(key));
        _usagePeriod.SelectItem(2);
        page.Add(Ui.Row(8, Ui.Label(L.UI("ui.Zeitraum")), _usagePeriod, Ui.Spacer(), Ui.Button(L.UI("ui.ZaehlerZuruecksetzen"), ClearUsage)));

        var tiles = Ui.Row(12,
            Tile(L.UI("ui.Antworten"), out _usageRequestsTile, out _),
            Tile(L.UI("ui.TokensGesendetErhalten"), out _usageTokensTile, out _),
            Tile(L.UI("ui.KostenUngefaehr"), out _usageCostTile, out _));
        tiles.Distribution = NSStackViewDistribution.FillEqually;
        page.Add(tiles);

        var table = new Col(6);
        table.Add(Ui.Heading(L.UI("ui.JeModell")));
        _usageList = new Col(6);
        _usageTable = _usageList;
        table.Add(_usageList);
        _usageEmpty = Ui.Hint(L.UI("ui.InDiesemZeitraumGabEs"));
        table.Add(_usageEmpty);
        _usageStatus = Ui.Hint();
        table.Add(_usageStatus);
        page.Add(Ui.Card(table));

        var how = new Col(4);
        how.Add(Ui.Heading(L.UI("ui.SoEntstehenDieKosten")));
        how.Add(Ui.Hint(L.UI("ui.GemeldetBetragDenDerAnbieter")));
        page.Add(Ui.Card(how));
        return Ui.ScrollPage(page, 860);
    }

    private NSView UsageRow(bool bold, string provider, string model, string requests, string input, string output, string cost)
    {
        NSTextField Cell(string text, nfloat? width, NSTextAlignment alignment = NSTextAlignment.Left)
        {
            var label = Ui.Label(text, 12, bold: bold);
            label.Alignment = alignment;
            if (width is { } w) label.WidthAnchor.ConstraintEqualTo(w).Active = true;
            return label;
        }
        var name = Cell(model, null);
        name.ToolTip = model;
        return Ui.Row(8, Cell(provider, 110), name, Cell(requests, 56, NSTextAlignment.Right), Cell(input, 76, NSTextAlignment.Right),
            Cell(output, 76, NSTextAlignment.Right), Cell(cost, 120, NSTextAlignment.Right));
    }

    private async Task ShowUsageAsync()
    {
        var now = DateTime.Now;
        DateTime? since = (int)_usagePeriod.IndexOfSelectedItem switch
        {
            0 => now.Date,
            1 => now.Date.AddDays(-6),
            2 => new DateTime(now.Year, now.Month, 1),
            3 => now.Date.AddDays(-29),
            _ => null
        };
        var rows = _store.UsageByModel(since);
        var priceNote = await EnsurePricesAsync(rows, TimeSpan.FromSeconds(20))
            ? "" : L.T("Preisliste nicht erreichbar – Kosten ohne Angabe des Anbieters sind unbekannt.");

        double total = 0;
        var anyEstimate = false;
        var anyUnknown = false;
        _usageList.Clear();
        if (rows.Count > 0)
            _usageList.Add(UsageRow(true, L.UI("ui.Anbieter"), L.UI("ui.Modell"), L.UI("ui.Antworten"), L.UI("ui.TokensEin"), L.UI("ui.TokensAus"), L.UI("ui.Kosten")));
        foreach (var r in rows)
        {
            var (amount, kind) = CostOf(r);
            total += amount;
            anyEstimate |= kind == CostKind.Estimated;
            anyUnknown |= kind is CostKind.Partial or CostKind.Unknown;
            var cost = kind switch
            {
                CostKind.Local => L.T("0 $ (lokal)"),
                CostKind.Reported => L.F("{0} gemeldet", FormatDollar(amount)),
                CostKind.Estimated => L.F("≈ {0} geschätzt", FormatDollar(amount)),
                CostKind.Partial => L.F("mind. {0}", FormatDollar(amount)),
                _ => L.T("unbekannt")
            };
            _usageList.Add(UsageRow(false, ProviderInfo.Get(r.Provider).Name, r.Model, r.Requests.ToString("N0"), r.InputTokens.ToString("N0"),
                r.OutputTokens.ToString("N0"), cost));
        }

        _usageEmpty.Hidden = rows.Count > 0;
        _usageRequestsTile.StringValue = rows.Sum(r => r.Requests).ToString("N0");
        _usageTokensTile.StringValue = $"{rows.Sum(r => r.InputTokens):N0} / {rows.Sum(r => r.OutputTokens):N0}";
        _usageCostTile.StringValue = "≈ " + FormatDollar(total) + (anyUnknown ? " +" : "");
        _usageStatus.StringValue = priceNote.Length > 0 ? priceNote
            : anyUnknown ? L.T("„+“: Für manche Antworten ist kein Preis bekannt, die Summe ist also eher höher.")
            : anyEstimate ? L.T("Enthält geschätzte Beträge.") : "";
    }

    private static string FormatDollar(double d) =>
        d == 0 ? "0 $" : d < 0.0001 ? $"< {0.0001:0.0000} $" : d < 0.01 ? $"{d:0.0000} $" : $"{d:0.00} $";

    private void ClearUsage()
    {
        if (!Ui.Confirm(L.T("Alle mitgezählten Tokens und Kosten löschen? Die Chats bleiben erhalten."), L.T("Zähler zurücksetzen"))) return;
        _store.ClearUsage();
        _ = ShowUsageAsync();
    }

    // Kosten einer Verbrauchszeile: gemeldet, sonst nach Listenpreis geschätzt, sonst (teilweise) unbekannt
    private static (double Amount, CostKind Kind) CostOf(UsageRow r)
    {
        if (ProviderInfo.Get(r.Provider).IsLocal) return (0, CostKind.Local);
        if (r.CostCount == r.Requests) return (r.Cost, CostKind.Reported);
        // Ganz oder teilweise ohne gemeldete Kosten: alles nach Listenpreis schätzen
        if (PriceEstimate.Estimate(r.Provider, r.Model, r.InputTokens, r.OutputTokens) is { } estimate)
            return (estimate, CostKind.Estimated);
        return r.CostCount > 0 ? (r.Cost, CostKind.Partial) : (0, CostKind.Unknown);
    }

    // Listenpreise für OpenAI/Anthropic nur laden, wenn sie gebraucht werden. false = Preisliste nicht erreichbar
    private static async Task<bool> EnsurePricesAsync(IEnumerable<UsageRow> rows, TimeSpan timeout)
    {
        if (!rows.Any(r => !ProviderInfo.Get(r.Provider).IsLocal && r.CostCount < r.Requests)) return true;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await PriceEstimate.LoadAsync(cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private async Task<double> SpentSinceAsync(DateTime since)
    {
        var rows = _store.UsageByModel(since);
        await EnsurePricesAsync(rows, TimeSpan.FromSeconds(8));
        return rows.Sum(r => CostOf(r).Amount);
    }

    private static DateTime MonthStart => new(DateTime.Today.Year, DateTime.Today.Month, 1);

    // Vor jeder Anfrage an einen Online-Anbieter: Budget überschritten → nachfragen bzw. (hart) sperren.
    // Rückgabe: ob gesendet werden darf; warning = Hinweis ab 80 % (sonst null)
    private async Task<(bool Allowed, string? Warning)> CheckBudgetAsync(IEnumerable<ProviderInfo> providers)
    {
        if (providers.All(p => p.IsLocal) || (_settings.DailyBudget is null && _settings.MonthlyBudget is null))
            return (true, null);

        // Daily = Tagesbudget, sonst Monatsbudget (die Texte dazu sind ganze Sätze, damit sie sich übersetzen lassen)
        var limits = new List<(bool Daily, double Limit, double Spent)>();
        if (_settings.DailyBudget is { } day)
            limits.Add((true, day, await SpentSinceAsync(DateTime.Today)));
        if (_settings.MonthlyBudget is { } month)
            limits.Add((false, month, await SpentSinceAsync(MonthStart)));

        if (limits.Where(l => l.Spent >= l.Limit).Select(l => (Over: true, l.Daily, l.Limit, l.Spent)).FirstOrDefault() is { Over: true } over)
        {
            var text = over.Daily
                ? L.F("Dein Tagesbudget von {0} ist erreicht – bisher ≈ {1} (geschätzt, siehe „Verbrauch“).", FormatDollar(over.Limit), FormatDollar(over.Spent))
                : L.F("Dein Monatsbudget von {0} ist erreicht – bisher ≈ {1} (geschätzt, siehe „Verbrauch“).", FormatDollar(over.Limit), FormatDollar(over.Spent));
            if (_settings.BudgetHardStop)
            {
                Ui.Info(text + "\n\n" + L.T("Die Kostenbremse ist auf „hart“ gestellt: Anfragen an Online-Anbieter sind gesperrt. " +
                                            "Lokale Modelle gehen weiter. Ändern kannst du das unter „Verbrauch“ → „Kostenbremse“."),
                    L.T("Kostenbremse"), warning: true);
                return (false, null);
            }
            return (Ui.Confirm(text + "\n\n" + L.T("Trotzdem senden?"), L.T("Kostenbremse"), warning: true), null);
        }

        var near = limits.Where(l => l.Spent >= 0.8 * l.Limit)
            .Select(l => L.F(l.Daily ? "{0:P0} des Tagesbudgets" : "{0:P0} des Monatsbudgets", l.Spent / l.Limit)).ToList();
        return (true, near.Count > 0 ? L.F("Hinweis: {0} verbraucht.", string.Join(L.T(" und "), near)) : null);
    }

    // Für die Automatiken: ohne Rückfrage – ist ein Limit erreicht, kommt der Grund zurück (sonst null)
    private async Task<string?> BudgetBlockReasonAsync(ProviderInfo provider)
    {
        if (provider.IsLocal) return null;
        if (_settings.DailyBudget is { } day && await SpentSinceAsync(DateTime.Today) is var spentToday && spentToday >= day)
            return L.F("Dein Tagesbudget von {0} ist erreicht – bisher ≈ {1} (geschätzt, siehe „Verbrauch“).", FormatDollar(day), FormatDollar(spentToday));
        if (_settings.MonthlyBudget is { } month && await SpentSinceAsync(MonthStart) is var spentMonth && spentMonth >= month)
            return L.F("Dein Monatsbudget von {0} ist erreicht – bisher ≈ {1} (geschätzt, siehe „Verbrauch“).", FormatDollar(month), FormatDollar(spentMonth));
        return null;
    }

    private async Task ShowBudgetAsync()
    {
        _dailyBudgetBox.StringValue = _settings.DailyBudget?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        _monthlyBudgetBox.StringValue = _settings.MonthlyBudget?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        _budgetHardCheck.State = _settings.BudgetHardStop ? NSCellStateValue.On : NSCellStateValue.Off;
        var today = await SpentSinceAsync(DateTime.Today);
        var month = await SpentSinceAsync(MonthStart);
        string Of(double spent, double? limit) => limit is { } l ? L.F("{0} von {1}", FormatDollar(spent), FormatDollar(l)) : FormatDollar(spent);
        _budgetSpent.StringValue = L.F("Bisher (≈): heute {0} · diesen Monat {1}", Of(today, _settings.DailyBudget), Of(month, _settings.MonthlyBudget));
    }

    private void SaveBudget()
    {
        if (!TryParseBudget(_dailyBudgetBox.StringValue, out var day) || !TryParseBudget(_monthlyBudgetBox.StringValue, out var month))
        {
            _budgetStatus.StringValue = L.T("Bitte Beträge als Zahl eingeben, z. B. 0,50 – oder leer lassen für „kein Limit“.");
            return;
        }
        // Nur diese Werte übernehmen – ein ungespeicherter Entwurf auf der Einstellungsseite bleibt unberührt
        foreach (var s in new[] { _settings, _draft })
        {
            s.DailyBudget = day;
            s.MonthlyBudget = month;
            s.BudgetHardStop = _budgetHardCheck.State == NSCellStateValue.On;
        }
        _settings.Save(_store);
        _budgetStatus.StringValue = day is null && month is null ? L.T("Gespeichert – kein Limit gesetzt.") : L.F("Gespeichert ({0:HH:mm:ss}).", DateTime.Now);
        _ = ShowBudgetAsync();
    }

    private static bool TryParseBudget(string text, out double? value)
    {
        value = null;
        text = text.Trim().Replace("$", "").Trim();
        if (text.Length == 0) return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return false;
        if (v < 0) return false;
        value = v;
        return true;
    }
}
