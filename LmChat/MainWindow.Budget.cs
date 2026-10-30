using System.Globalization;
using System.Windows;

namespace LmChat;

// Kostenbremse: Tages- und Monatsbudget für Online-Anbieter (Grundlage: dieselbe Rechnung wie auf der Seite „Verbrauch“)
public partial class MainWindow
{
    private enum CostKind { Local, Reported, Estimated, Partial, Unknown }

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
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
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
                MessageBox.Show(this, text + "\n\n" + L.T("Die Kostenbremse ist auf „hart“ gestellt: Anfragen an Online-Anbieter sind gesperrt. " +
                                                         "Lokale Modelle gehen weiter. Ändern kannst du das unter „Verbrauch“ → „Kostenbremse“."),
                    L.T("Kostenbremse"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return (false, null);
            }
            var go = MessageBox.Show(this, text + "\n\n" + L.T("Trotzdem senden?"), L.T("Kostenbremse"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            return (go, null);
        }

        var near = limits.Where(l => l.Spent >= 0.8 * l.Limit)
            .Select(l => L.F(l.Daily ? "{0:P0} des Tagesbudgets" : "{0:P0} des Monatsbudgets", l.Spent / l.Limit)).ToList();
        return (true, near.Count > 0 ? L.F("Hinweis: {0} verbraucht.", string.Join(L.T(" und "), near)) : null);
    }

    // Für die Datei-Automatik: ohne Rückfrage – ist ein Limit erreicht, kommt der Grund zurück (sonst null)
    private async Task<string?> BudgetBlockReasonAsync(ProviderInfo provider)
    {
        if (provider.IsLocal) return null;
        if (_settings.DailyBudget is { } day && await SpentSinceAsync(DateTime.Today) is var spentToday && spentToday >= day)
            return L.F("Dein Tagesbudget von {0} ist erreicht – bisher ≈ {1} (geschätzt, siehe „Verbrauch“).", FormatDollar(day), FormatDollar(spentToday));
        if (_settings.MonthlyBudget is { } month && await SpentSinceAsync(MonthStart) is var spentMonth && spentMonth >= month)
            return L.F("Dein Monatsbudget von {0} ist erreicht – bisher ≈ {1} (geschätzt, siehe „Verbrauch“).", FormatDollar(month), FormatDollar(spentMonth));
        return null;
    }

    // ---------- Karte „Kostenbremse“ auf der Seite Verbrauch ----------

    private async Task ShowBudgetAsync()
    {
        DailyBudgetBox.Text = _settings.DailyBudget?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        MonthlyBudgetBox.Text = _settings.MonthlyBudget?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        BudgetHardCheck.IsChecked = _settings.BudgetHardStop;
        var today = await SpentSinceAsync(DateTime.Today);
        var month = await SpentSinceAsync(MonthStart);
        string Of(double spent, double? limit) => limit is { } l ? L.F("{0} von {1}", FormatDollar(spent), FormatDollar(l)) : FormatDollar(spent);
        BudgetSpent.Text = L.F("Bisher (≈): heute {0} · diesen Monat {1}", Of(today, _settings.DailyBudget), Of(month, _settings.MonthlyBudget));
    }

    private void SaveBudget_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseBudget(DailyBudgetBox.Text, out var day) || !TryParseBudget(MonthlyBudgetBox.Text, out var month))
        {
            BudgetStatus.Text = L.T("Bitte Beträge als Zahl eingeben, z. B. 0,50 – oder leer lassen für „kein Limit“.");
            return;
        }
        // Nur diese Werte übernehmen – ein ungespeicherter Entwurf auf der Einstellungsseite bleibt unberührt
        foreach (var s in new[] { _settings, _draft })
        {
            s.DailyBudget = day;
            s.MonthlyBudget = month;
            s.BudgetHardStop = BudgetHardCheck.IsChecked == true;
        }
        _settings.Save(_store);
        BudgetStatus.Text = day is null && month is null ? L.T("Gespeichert – kein Limit gesetzt.") : L.F("Gespeichert ({0:HH:mm:ss}).", DateTime.Now);
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
