namespace LmChat;

// Seite „Hilfe“: Kurzanleitung, Fehlerbehebung, Lizenz. Die Texte stammen aus denselben Sprachdateien wie unter Windows.
internal sealed partial class MainWindow
{
    private NSView BuildHelpPage()
    {
        var page = new Col(14);
        page.Add(Ui.PageTitle(L.UI("ui.Hilfe")));
        page.Add(new Rich(13, Ui.Secondary).Text(L.UI("ui.EinEinfacherChatClientFuer") + $" Version {AppVersion}. " + L.UI("ui.Projektseite") + " ")
            .Link("guentherhaslbeck.de/projekte/freeguenther", ProjectUrl).Text(" · ")
            .Link(L.UI("ui.NeuesteVersion"), "https://github.com/ghaslbe/FreeGuenther/releases/latest").View());

        NSView Section(string heading, Rich body, bool warning = false)
        {
            var column = new Col(6);
            column.Add(Ui.Heading(heading));
            column.Add(body.View());
            return warning ? Ui.Card(column, Ui.WarningFill, Ui.Warning.ColorWithAlphaComponent(0.4f)) : Ui.Card(column);
        }
        // Zeile aus fett gesetztem Begriff und Erklärung
        Rich Line(Rich r, string term, string text, bool last = false) =>
            r.Bold(L.UI(term) + " ").Text(L.UI(text) + (last ? "" : "\n"));

        page.Add(Section(L.UI("ui.KostenAbsichernBeiJedemAPI"), new Rich()
            .Text(L.UI("ui.EinAPISchluesselIstWie") + "\n")
            .Bold(L.UI("ui.SetzeDeshalbUnbedingtBeiJedem") + " ").Text(L.UI("ui.BevorDuIhnInFreeGuenther") + "\n")
            .Text("• ").Link(L.UI("ui.OpenRouter"), "https://openrouter.ai/settings/keys").Text(L.UI("ui.JederSchluesselKannEinEigenes") + "\n")
            .Text("• ").Link(L.UI("ui.OpenAI"), "https://platform.openai.com/settings/organization/limits").Text(L.UI("ui.InDenOrganisationsEinstellungenUnter") + "\n")
            .Text("• ").Link(L.UI("ui.Anthropic"), "https://platform.claude.com/settings/limits").Text(L.UI("ui.InDerClaudeKonsoleUnter") + "\n")
            .Bold(L.UI("ui.Ausserdem") + " ").Text(L.UI("ui.FuerFreeGuentherEinenEigenenSchluessel") + "\n")
            .Text(L.UI("ui.LokaleAnbieterLMStudioOllama")), warning: true));

        page.Add(Section(L.UI("ui.Plugins"), new Rich()
            .Text(L.UI("ui.PluginsGebenDemModellZusaetzliche2") + " ").Bold(L.UI("ui.Plugins")).Text(" " + L.UI("ui.SchaltestDuJedesEinzelnEin") + " ")
            .Bold(L.UI("ui.Wetter")).Text(", ").Bold(L.UI("ui.DatumUndUhrzeit")).Text(" " + L.UI("ui.Und") + " ")
            .Bold(L.UI("ui.Dateien")).Text(" " + L.UI("ui.LiestNurInEinemFreigegebenen") + " ")
            .Bold(L.UI("ui.WebseiteAbrufen")).Text(" " + L.UI("ui.LaedtEineAdressePerGET") + " ")
            .Bold(L.UI("ui.Webhook")).Text(" " + L.UI("ui.SchicktEineAntwortSamtBild") + "\n")
            .Text(L.UI("ui.DanachEinfachNormalFragenZ") + "\n")
            .Text(L.UI("ui.VoraussetzungEinModellDasWerkzeuge") + "\n")
            .Bold(L.UI("ui.MCP") + " ").Text(L.UI("ui.PluginsVomTypMCPVerbinden"))));

        page.Add(Section(L.UI("ui.WasPassiertGeradeVerbrauch"), new Rich()
            .Text(L.UI("ui.SolangeNochKeinAntworttextDa") + "\n")
            .Text(L.UI("ui.Unter") + " ").Bold(L.UI("ui.Verbrauch")).Text(" " + L.UI("ui.ZaehltFreeGuentherAntwortenTokensUnd") + " ")
            .Bold(L.UI("ui.DasIstNurEineSchaetzung")).Text(" " + L.UI("ui.DieExaktenKostenStehenNur"))));

        page.Add(Section(L.UI("ui.ErsteSchritte"), new Rich()
            .Text(L.UI("ui.1UnterEinstellungenDenAnbieter") + "\n").Text(L.UI("ui.2BeiLokalenAnbieternGgf") + "\n")
            .Text(L.UI("ui.3ListeAktualisierenEinModell") + "\n").Text(L.UI("ui.4UnterChatEineFrage"))));

        page.Add(Section(L.UI("ui.Anbieter"), new Rich()
            .Bold(L.UI("ui.LMStudio")).Text(" " + L.UI("ui.LokalStandardHttpLocalhost1234") + "\n")
            .Bold(L.UI("ui.Ollama")).Text(" " + L.UI("ui.LokalStandardHttpLocalhost11434") + "\n")
            .Bold(L.UI("ui.OpenAI")).Text(", ").Bold(L.UI("ui.AnthropicClaude")).Text(", ").Bold(L.UI("ui.OpenRouter"))
            .Text(" " + L.UI("ui.OnlineBrauchenEinenAPISchluessel") + "\n")
            .Text(L.UI("ui.BeiOpenRouterKannstDuDen") + " ").Bold(L.UI("ui.StandortDerRechenzentren")).Text(" " + L.UI("ui.FestlegenUeberDieHosterAuswahl") + "\n")
            .Text(L.UI("ui.DieAPISchluesselWerdenVerschluesselt"))));

        var controls = new Rich();
        foreach (var (term, text) in new[]
                 {
                     ("ui.Enter", "ui.NachrichtSenden"), ("ui.UmschaltEnter", "ui.NeueZeile"), ("ui.StrgV", "ui.BildZBScreenshotOder"),
                     ("ui.ImEingabefeld", "ui.VorlagenUndPersonasAuswaehlenEnter"), ("ui.Datei", "ui.PDFWordOderTextdateiAnhaengen"),
                     ("ui.ModelleVergleichen", "ui.DieselbeFrageAnZweiModelle"), ("ui.Kostenbremse2", "ui.TagesMonatsbudgetFuerOnlineAnbieter"),
                     ("ui.StoppKnopf", "ui.LaufendeAntwortAbbrechen"), ("ui.SuchfeldLinks", "ui.ChatsNachTitelFiltern"),
                     ("ui.KopierenUnterEinerAntwort", "ui.AntwortMitMarkdownInDie"), ("ui.NeuErzeugenUnterEinerAntwort", "ui.DieselbeFrageNochmalStellenZ"),
                     ("ui.BearbeitenUnterDeinerNachricht", "ui.NachrichtAendernUndNeuSenden"), ("ui.KopierenUeberEinemCodeBlock", "ui.NurDenCodeInDie"),
                     ("ui.ExportKnopfObenRechts", "ui.ChatAlsMarkdownOderHTML"), ("ui.HochscrollenWaehrendEinerAntwort", "ui.DerChatSpringtNichtMehr"),
                 })
            Line(controls, term, text);
        Line(controls, "ui.ZeileUnterJederAntwort", "ui.AnbieterModellGeschwindigkeitTokenS", last: true);
        page.Add(Section(L.UI("ui.Bedienung"), controls));

        var chats = new Rich().Text(L.UI("ui.JederChatWirdAutomatischGespeichert") + "\n");
        Line(chats, "ui.ChatLoeschen2", "ui.UeberDenPapierkorbInDer");
        Line(chats, "ui.VerlaufMitsenden2", "ui.MitHakenStandardKenntDas", last: true);
        page.Add(Section(L.UI("ui.ChatsUndVerlauf"), chats));

        page.Add(Section(L.UI("ui.Bilder"), new Rich().Text(L.UI("ui.ModelleMitVerstehenBilderEin"))));

        var generate = new Rich().Text(L.UI("ui.ModelleMitInDerModellliste") + "\n");
        Line(generate, "ui.Bearbeiten", "ui.HaengEinBildAnOder");
        generate.Text(L.UI("ui.UnterDemBildBildKopieren"));
        page.Add(Section(L.UI("ui.BilderErzeugen"), generate));

        var trouble = new Rich();
        Line(trouble, "ui.FehlerKeineVerbindung", "ui.LokalLaeuftLMStudioBzw");
        Line(trouble, "ui.FehlerWegenTemperaturOAe", "ui.MancheModelleZBReasoning");
        Line(trouble, "ui.ModelllisteIstLeer", "ui.InDenEinstellungenListeAktualisieren");
        Line(trouble, "ui.ErsteAntwortDauertLange", "ui.LMStudioLaedtDasModell");
        Line(trouble, "ui.BildKnopfAusgegraut", "ui.DasGewaehlteModellVerstehtKeine", last: true);
        page.Add(Section(L.UI("ui.WennEsNichtKlappt"), trouble));

        page.Add(Section(L.UI("ui.Daten"), new Rich().Text(L.UI("ui.ChatsBilderUndEinstellungenLiegen") + " ").Mono(DbPath)
            .Text(L.UI("ui.GesendetWirdAusschliesslichAnDen"))));

        var license = new Col(8);
        license.Add(Ui.Heading(L.UI("ui.LizenzUndHaftungsausschluss")));
        license.Add(new Rich().Text(L.UI("ui.DieseSoftwareStehtUnterDer") + " ").Bold(L.UI("ui.MITLizenz")).Text(L.UI("ui.SieDarfFreiGenutztVeraendert") + "\n")
            .Text(L.UI("ui.SieWird") + " ").Bold(L.UI("ui.OhneJedeGewaehrleistung")).Text(" " + L.UI("ui.BereitgestelltDieAutorenUebernehmen") + " ")
            .Bold(L.UI("ui.KeinerleiHaftung")).Text(" " + L.UI("ui.FuerSchaedenDatenverlustOderSonstige")).View());
        license.Add(new Rich(11, Ui.Secondary).Mono(L.UI("ui.MITLicenseCopyrightC2026") + "\n\n" + L.UI("ui.PermissionIsHerebyGrantedFree") + "\n\n" +
                                                    L.UI("ui.TheAboveCopyrightNoticeAnd") + "\n\n" + L.UI("ui.THESOFTWAREISPROVIDEDAS")).View());
        page.Add(Ui.Card(license));
        return Ui.ScrollPage(page, 860);
    }
}
