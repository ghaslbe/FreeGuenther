# lmstudioChatWindows

Ein einfacher Windows-Chat-Client für lokale und Online-Sprachmodelle: [LM Studio](https://lmstudio.ai), [Ollama](https://ollama.com), [OpenAI](https://platform.openai.com), [Anthropic (Claude)](https://platform.claude.com) und [OpenRouter](https://openrouter.ai). Er läuft als **eine einzige .exe** und braucht keine Installation und kein .NET auf dem Zielrechner.

![LM Chat: Bild erzeugen und bearbeiten mit OpenRouter](docs/screenshot-bild.png)

*Bild erzeugen und per Folgeanweisung abändern, darunter Anbieter, Modell, Tokens, Dauer und Kosten.*

![LM Chat: Antwort mit Code-Block, Liste und Tabelle](docs/screenshot-code.png)

*Antworten werden als Markdown dargestellt: Code-Blöcke, Listen, Tabellen und Inline-Code.*

## Download

| Datei | Wofür |
|---|---|
| **[LmChat-Setup.exe](https://github.com/ghaslbe/lmstudioChatWindows/releases/latest/download/LmChat-Setup.exe)** (Installer, empfohlen) | Installiert LM Chat mit Startmenü-Eintrag und optionalem Desktop-Symbol. Deinstallation über die Windows-Einstellungen. Keine Administratorrechte nötig, Updates einfach drüberinstallieren. |
| **[LmChat.exe](https://github.com/ghaslbe/lmstudioChatWindows/releases/latest/download/LmChat.exe)** (portabel) | Ohne Installation, z. B. vom USB-Stick oder zum schnellen Ausprobieren. Herunterladen und starten. |

Beide Varianten sind dieselbe App und nutzen dieselben Chats und Einstellungen (`%LOCALAPPDATA%\LmChat`). Alle Versionen findest du unter [Releases](https://github.com/ghaslbe/lmstudioChatWindows/releases).

Hinweis: Die Dateien sind nicht digital signiert. Windows SmartScreen kann deshalb beim ersten Start warnen. Über „Weitere Informationen“ → „Trotzdem ausführen“ lässt sich die App starten.

## Funktionen

- **Mehrere Anbieter:** Jeder Anbieter merkt sich eigene Adresse, Schlüssel und Modell. In den Einstellungen gibt es zu jedem Anbieter Links zu Website, API-Schlüsseln, Modellen und Preisen.

  | Anbieter | Art | Standard-Adresse | Schlüssel |
  |---|---|---|---|
  | LM Studio | lokal | `http://localhost:1234` | – |
  | Ollama | lokal | `http://localhost:11434` | – |
  | OpenAI | online | `https://api.openai.com` | ja |
  | Anthropic (Claude) | online | `https://api.anthropic.com` | ja |
  | OpenRouter | online | `https://openrouter.ai/api` | ja |

  API-Schlüssel werden mit Windows DPAPI verschlüsselt gespeichert, nur für das eigene Windows-Konto lesbar.
- **OpenRouter: Standort der Rechenzentren:** Die Modellliste zeigt nur Modelle, die in der gewählten Region laufen. Es gibt zwei Wege:
  - **Über Hoster-Auswahl (Europa, Deutschland oder USA, jeder Tarif):** Die App ermittelt pro Modell die Hoster-Endpunkte mit bekanntem Standort. Grundlage ist die Region in der Endpunkt-Kennung, z. B. `mistral/eu`, `amazon-bedrock/eu-central-1` oder `google-vertex/us`, ersatzweise die Rechenzentrums-Länder des Hosters. Beim Senden lässt die App per `provider.only` nur diese Endpunkte zu und schaltet Ausweichhoster ab (`allow_fallbacks: false`). Die Standortangaben stammen von OpenRouter und den Hostern und sind nicht garantiert; Endpunkte wie `…/global` gelten als unbekannt und werden ausgeschlossen. Stand Oktober 2026 meldet kein Endpunkt einen Standort in Deutschland, die DE-Liste ist deshalb leer.
  - **Garantiert (Europa oder USA, nur Business-Tarif):** Anfragen gehen über `eu.openrouter.ai` bzw. `us.openrouter.ai` ([In-Region-Routing](https://openrouter.ai/docs/guides/features/sovereign-ai)). Ohne Business-Tarif antwortet OpenRouter mit HTTP 403.
- **Chat mit Live-Antwort:** Die Antwort erscheint Wort für Wort (Streaming). Danach wird sie als **Markdown** dargestellt, mit Überschriften, Fettdruck, Listen, Tabellen, Codeblöcken und Links.
- **Bilder erzeugen (OpenRouter, OpenAI):** Bildmodelle sind in der Modellliste mit 🎨 markiert, bei OpenRouter erkannt über `output_modalities` bzw. `/api/v1/images/models`, bei OpenAI `gpt-image-…` und `dall-e-…`.
  - Ist ein Bildmodell aktiv, ist die Nachricht die Bildbeschreibung. Angefragt wird über die Bild-Schnittstelle des Anbieters: `POST /api/v1/images` bzw. `/v1/images/generations`.
  - **Bearbeiten:** Ein angehängtes Bild dient als Vorlage, ohne Anhang (mit „Verlauf mitsenden“) das zuletzt erzeugte. So geht z. B. „mach daraus eine Nachtszene“. Technisch läuft das über `input_references` bzw. `/v1/images/edits`.
  - Das Bild erscheint im Chat, mit „Bild kopieren“, „Speichern“ (PNG) und den Kosten, sofern gemeldet. Erzeugte Bilder werden mit dem Chat gespeichert.
- **Kopieren:** Der Knopf „Kopieren“ unter jeder Antwort legt sie mit Markdown-Formatierung in die Zwischenablage.
- **Angaben unter jeder Antwort** (klein, wird mit dem Chat gespeichert):
  - Anbieter und tatsächlich antwortendes Modell, bei OpenRouter auch der Hoster, z. B. `OpenRouter · openai/gpt-6-luna (über OpenAI)`
  - Token pro Sekunde (gemessen ab dem ersten Textstück)
  - Tokens in/out, soweit der Anbieter sie meldet
  - Gesamtdauer

  Die Token-Zahlen stammen bei OpenAI-kompatiblen Anbietern aus `stream_options.include_usage`, bei Anthropic aus den Stream-Ereignissen der Messages API.
- **Gespeicherte Chats:** Jeder Chat wird in einer SQLite-Datenbank gespeichert und lässt sich links in der Liste wieder aufrufen. Löschen geht über den Papierkorb in der Liste (beim Darüberfahren), per Rechtsklick, mit der Entf-Taste oder oben rechts im geöffneten Chat, jeweils mit Sicherheitsabfrage.
- **„Bisherigen Chatverlauf mitsenden“:** Das Häkchen ist standardmäßig an, so kann man ein Gespräch führen. Ohne Häkchen wird nur die aktuelle Nachricht gesendet.
- **Bilder senden:** Bei Modellen, die Bilder verstehen (in LM Studio Typ `vlm`), lässt sich ein Bild anhängen, z. B. mit der Frage „Lies mal das Bild aus“. Dafür gibt es drei Wege:
  - Button **Bild …**
  - **Strg+V** (Screenshot oder kopierte Bilddatei)
  - Bilddatei ins Fenster ziehen

  Große Bilder werden automatisch auf maximal 2048 px verkleinert. Im Chat erscheint eine Vorschau, ein Klick darauf öffnet das Bild in voller Größe.
- **Einstellungen (eigene Seite):**
  - Server-Adresse (wird beim Speichern geprüft)
  - Modell per Dropdown, direkt vom Anbieter geladen. 🎨 = erzeugt Bilder, 🖼 = versteht Bilder, ● = gerade geladen.
  - Darüber ein **Filter**: ein Suchfeld (alle Wörter müssen im Namen vorkommen, z. B. „gemini flash“) und eine Auswahl „Alle Modelle“, „Erzeugt Bilder“, „Versteht Bilder“ oder „Gerade geladen“. Daneben steht, wie viele Modelle passen.
  - System-Prompt
  - Temperatur, Top-P, Top-K, maximale Antwortlänge und Wiederholungsstrafe

  Die Zahlenwerte stellst du per Schieberegler ein, nur innerhalb gültiger Bereiche und jeweils mit Erklärung. Ohne Haken gilt der Standardwert des Modells.
- **Hilfe-Seite:** Kurzanleitung, Fehlerbehebung, Lizenz und Link zu diesem Repository.
- **Oberfläche:**
  - Seitenleiste mit Logo, Navigation (Chat, Einstellungen, Hilfe), Suchfeld und nach Datum gruppierter Chatliste (Heute, Gestern, Letzte 7 Tage …). Unten zeigt eine Karte den aktiven Anbieter und das Modell.
  - Chat in einer mittigen Lesespalte: eigene Nachrichten als Sprechblasen, Antworten als Fließtext. Ein leerer Chat zeigt eine Startansicht.
  - Eingabe in einer abgerundeten Karte mit Bild-Knopf, „Verlauf mitsenden“ und rundem Senden-Knopf.
  - Einstellungen und Hilfe als Karten, oben in den Einstellungen Kacheln für Anbieter, Modell und Anzahl der Chats.
  - Die App folgt dem Hell- oder Dunkelmodus und der Akzentfarbe von Windows 11 (Fluent).

## Bedienung

| Taste | Aktion |
|---|---|
| Enter | Senden |
| Umschalt+Enter | Neue Zeile |
| Strg+V | Bild aus der Zwischenablage anhängen |

## ⚠ Wichtig: Kosten absichern

Ein API-Schlüssel ist wie eine Kreditkarte. **Setze bei jedem Schlüssel bzw. Konto unbedingt ein Ausgabenlimit**, bevor du ihn in LM Chat einträgst:

- **OpenRouter:** Jeder Schlüssel kann ein eigenes Guthaben-Limit bekommen ([Schlüssel verwalten](https://openrouter.ai/settings/keys)). Nur so viel Guthaben aufladen wie nötig, automatisches Aufladen ausschalten.
- **OpenAI:** Budget bzw. Nutzungslimit in den Organisations-Einstellungen ([Limits](https://platform.openai.com/settings/organization/limits)). Automatisches Aufladen ausschalten.
- **Anthropic:** Ausgabenlimit in der Claude-Konsole ([Limits](https://platform.claude.com/settings/limits)).

Lege für LM Chat einen eigenen Schlüssel an, damit du ihn gezielt sperren kannst. Bei Verdacht auf Missbrauch löschst du ihn sofort beim Anbieter. Die App weist in der Hilfe und beim API-Schlüssel darauf hin, und in den Anbieter-Links steht „⚠ Ausgabenlimit setzen“.

## Einrichtung der Anbieter

**LM Studio:** Unter **Developer** den Server starten (Standard-Port `1234`). Für Zugriff von anderen Rechnern **„Serve on Local Network“** einschalten und gegebenenfalls den Port in der Firewall freigeben.

**Ollama:** Modelle mit `ollama pull <name>` laden. Für Zugriff von anderen Rechnern Ollama mit `OLLAMA_HOST=0.0.0.0` starten.

**OpenAI, Anthropic, OpenRouter:** API-Schlüssel im Konto des Anbieters erzeugen und in den Einstellungen eintragen. Die Nutzung kostet je nach Modell Geld. Nachrichten und Bilder werden an den Dienst gesendet.

## Technische Details

| Anbieter | Chat | Modellliste | Bilderkennung |
|---|---|---|---|
| LM Studio | `/v1/chat/completions` (OpenAI-kompatibel) | `/api/v0/models` | Typ `vlm` |
| Ollama | `/v1/chat/completions` (OpenAI-kompatibel) | `/api/tags` und `/api/show` | Fähigkeit `vision` |
| OpenAI | `/v1/chat/completions` | `/v1/models` | wird nicht gemeldet |
| OpenRouter | `/api/v1/chat/completions` | `/api/v1/models` | `input_modalities` enthält `image` |
| Anthropic | offizielles [Anthropic-SDK für C#](https://www.nuget.org/packages/Anthropic) (Messages API, Streaming) | Models API | alle Claude-Modelle |

Welche Feineinstellungen gesendet werden, hängt vom Anbieter ab. Nicht unterstützte Einstellungen sind ausgegraut und werden nicht gesendet; bei Anthropic sind das z. B. Temperatur, Top-P und Top-K. Bei aktuellen Claude-Modellen (Opus 5.5, Opus 5, Fable 5.1, Sonnet 5.5) ist der serverseitige Fallback aktiv: Lehnt das Modell eine Anfrage ab, antwortet automatisch ein passendes Ersatzmodell.

## Daten

Chats und Einstellungen liegen nur lokal in:

```
%LOCALAPPDATA%\LmChat\chats.db
```

Die SQLite-Datenbank hat drei Tabellen:

- `chats`
- `messages`: Bilder werden als PNG-BLOB gespeichert
- `settings`

## Versionen und Releases

Die Versionsnummer steht in `LmChat/LmChat.csproj` (`<Version>`) und wird bei jeder Änderung hochgezählt. Sie erscheint im Fenstertitel und in der Hilfe.

Bei jedem Push auf `main` baut GitHub Actions (`.github/workflows/release.yml`) die portable `LmChat.exe` und mit [Inno Setup](https://jrsoftware.org/isinfo.php) den Installer `LmChat-Setup.exe` (Skript: `installer/LmChat.iss`). Beide landen im Release `v<Version>`. Gibt es das Release schon, werden nur die Dateien ersetzt.

Installer lokal bauen (nach `dotnet publish`):

```
ISCC.exe /DAppVersion=1.10.0 installer\LmChat.iss
```

## Bauen

Benötigt wird das [.NET 10 SDK](https://dotnet.microsoft.com/download):

```
winget install Microsoft.DotNet.SDK.10
```

Zum Entwickeln und Starten:

```
dotnet run --project LmChat
```

Eigenständige Einzel-.exe erzeugen:

```
dotnet publish LmChat -c Release -o LmChat\publish -p:DebugType=none
```

Ergebnis: `LmChat\publish\LmChat.exe` (ca. 63 MB, enthält die .NET-Laufzeit).

Das Icon erzeugt `tools\make-icon.ps1` neu.

## Projektaufbau

| Datei | Inhalt |
|---|---|
| `LmChat/MainWindow.xaml` | Oberfläche: Chat, Einstellungen, Hilfe |
| `LmChat/MainWindow.xaml.cs` | Logik: Chat, Bilder, Chatliste, Einstellungsseite |
| `LmChat/Providers.cs` | Anbieter (Standardwerte, Links) und `LlmClient`: Modelllisten und Streaming je Anbieter |
| `LmChat/MarkdownRenderer.cs` | Markdown → WPF-FlowDocument (über Markdig) |
| `LmChat/ParamRow.cs` | Einstellungszeile mit Haken, Erklärung und Schieberegler |
| `LmChat/ChatStore.cs` | SQLite-Speicher für Chats, Nachrichten, Einstellungen |
| `LmChat/LlmSettings.cs` | Einstellungen je Anbieter (als JSON in der Datenbank, Schlüssel per DPAPI verschlüsselt) |
| `LmChat/app.ico` | App-Icon |

Technik: C# / WPF auf .NET 10, `Microsoft.Data.Sqlite`, `Markdig`, `Anthropic`, `System.Security.Cryptography.ProtectedData`.

## Lizenz und Haftungsausschluss

Dieses Projekt steht unter der [MIT-Lizenz](LICENSE). Du darfst es frei nutzen, verändern und weitergeben.

Die Software wird **ohne jede Gewährleistung** bereitgestellt. Die Autoren übernehmen **keinerlei Haftung** für Schäden, Datenverlust oder sonstige Folgen der Nutzung. Antworten von KI-Modellen können falsch sein. Prüfe wichtige Informationen selbst.
