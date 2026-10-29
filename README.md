# lmstudioChatWindows

Ein einfacher Windows-Chat-Client für lokale und Online-Sprachmodelle: [LM Studio](https://lmstudio.ai), [Ollama](https://ollama.com), [OpenAI](https://platform.openai.com), [Anthropic (Claude)](https://platform.claude.com) und [OpenRouter](https://openrouter.ai). Er läuft als **eine einzige .exe** und braucht keine Installation und kein .NET auf dem Zielrechner.

![LM Chat: Bild erzeugen und bearbeiten mit OpenRouter](docs/screenshot-bild.png)

*Bild erzeugen und per Folgeanweisung abändern, darunter Anbieter, Modell, Tokens, Dauer und Kosten.*

![LM Chat: Antwort mit Code-Block, Liste und Tabelle](docs/screenshot-code.png)

*Antworten werden als Markdown dargestellt: Code-Blöcke, Listen, Tabellen und Inline-Code.*

![LM Chat: Verbrauchsseite mit Tokens und geschätzten Kosten je Modell](docs/screenshot-verbrauch.png)

*Verbrauch je Modell: Antworten, Tokens und ungefähre Kosten. Das ist nur eine Schätzung, die exakten Kosten stehen im Konto des Anbieters.*

![LM Chat: Willkommensseite beim ersten Start](docs/screenshot-willkommen.png)

*Beim ersten Start: Auswahl, ob lokal oder online. Danach führen die Einstellungen Schritt für Schritt weiter.*

![LM Chat: Antwort mit Wetter- und Datums-Plugin](docs/screenshot-wetter.png)

*Plugins: Das Modell ruft selbst „Datum und Uhrzeit“ und „Wetter“ auf. Unter der Antwort steht, welche Plugins benutzt wurden.*

![LM Chat: Plugin-Seite](docs/screenshot-plugins.png)

*Plugin-Seite: Jedes Plugin lässt sich einzeln einschalten und einstellen, weitere kommen aus dem Katalog oder per Import.*

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
- **Geführte Einrichtung:** Ist noch kein Modell eingerichtet, zeigt die Chat-Seite eine Willkommensseite. Dort wählst du „lokal“ (LM Studio, Ollama: kostenlos, kein Schlüssel) oder „online“ (OpenRouter empfohlen, OpenAI, Anthropic: mit API-Schlüssel und Ausgabenlimit). In den Einstellungen folgt eine Anleitung in 4 Schritten, die abhakt, was schon erledigt ist:
  1. Anbieter wählen
  2. Schlüssel eintragen bzw. Server starten
  3. Modell wählen
  4. Speichern

  Danach geht es mit „Los geht's – zum Chat“ weiter.
- **Chat mit Live-Antwort:** Die Antwort erscheint Wort für Wort (Streaming). Danach wird sie als **Markdown** dargestellt, mit Überschriften, Fettdruck, Listen, Tabellen, Codeblöcken und Links.
- **Was passiert gerade?** Bis der erste Text kommt, zeigt eine graue Zeile im Chat den Stand mit Sekundenzähler, z. B. „Anfrage an OpenRouter gesendet, warte auf Antwort … 3 s“. Die Phasen sind:
  - Hoster suchen (bei OpenRouter mit Regionsfilter)
  - Anfrage gesendet
  - Anfrage angenommen
  - „Das Modell denkt nach“ (wenn das Modell seine Denkphase mitschickt)

  Nach 30 Sekunden kommt ein Hinweis, bei lokalen Anbietern z. B., dass das Modell evtl. erst geladen wird. Sobald Text kommt, verschwindet die Zeile.
- **Verbrauch (eigene Seite):** Antworten, Tokens und ungefähre Kosten je Anbieter und Modell, wahlweise für heute, 7 Tage, diesen Monat, 30 Tage oder gesamt.
  - Die Kosten meldet OpenRouter mit der Antwort.
  - Für OpenAI, Anthropic und Antworten ohne Angabe schätzt die App sie aus Tokens × Listenpreis. Die Preise stammen aus der öffentlichen Preisliste von OpenRouter.
  - Lokale Anbieter kosten nichts.
  - Der Verbrauch bleibt beim Löschen eines Chats erhalten und lässt sich zurücksetzen.
  - **Das ist nur eine Schätzung.** Die exakten Kosten stehen im Konto des jeweiligen Anbieters ([OpenRouter](https://openrouter.ai/activity), [OpenAI](https://platform.openai.com/usage), [Anthropic](https://platform.claude.com/usage)).
- **Bilder erzeugen (OpenRouter, OpenAI):** Bildmodelle sind in der Modellliste mit 🎨 markiert, bei OpenRouter erkannt über `output_modalities` bzw. `/api/v1/images/models`, bei OpenAI `gpt-image-…` und `dall-e-…`.
  - Ist ein Bildmodell aktiv, ist die Nachricht die Bildbeschreibung. Angefragt wird über die Bild-Schnittstelle des Anbieters: `POST /api/v1/images` bzw. `/v1/images/generations`.
  - **Bearbeiten:** Ein angehängtes Bild dient als Vorlage, ohne Anhang (mit „Verlauf mitsenden“) das zuletzt erzeugte. So geht z. B. „mach daraus eine Nachtszene“. Technisch läuft das über `input_references` bzw. `/v1/images/edits`.
  - Das Bild erscheint im Chat, mit „Bild kopieren“, „Speichern“ (PNG) und den Kosten, sofern gemeldet. Erzeugte Bilder werden mit dem Chat gespeichert.
- **Kopieren:** Der Knopf „Kopieren“ unter jeder Antwort legt sie mit Markdown-Formatierung in die Zwischenablage.
- **Angaben unter jeder Antwort** (klein, wird mit dem Chat gespeichert):
  - Anbieter und tatsächlich antwortendes Modell, bei OpenRouter auch der Hoster, z. B. `OpenRouter · openai/gpt-6-luna (über OpenAI)`
  - Token pro Sekunde (gemessen ab dem ersten Text- bzw. Denk-Token)
  - Tokens in/out, soweit der Anbieter sie meldet
  - Gesamtdauer
  - Kosten, soweit der Anbieter sie meldet (OpenRouter)

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
- **Plugins (eigene Seite):** geben dem Modell zusätzliche Fähigkeiten über Tool Calling. Alles ist in die .exe eingebaut, es muss nichts zusätzlich installiert werden.
  - **Einzeln einschaltbar**, jedes Plugin mit eigener **Einstellungsseite**. Die Felder legt das Plugin selbst fest, z. B. Standardort, Einheiten, Ordner oder Schlüssel. Schlüssel werden verschlüsselt gespeichert.
  - **Mitgeliefert:**
    - **Wetter:** Open-Meteo, kostenlos, ohne Schlüssel
    - **Datum und Uhrzeit:** läuft lokal
    - **Dateien:** liest nur in einem freigegebenen Ordner, mit Rückfrage
    - **Webseite abrufen (GET):** lädt eine Adresse. Webseiten werden zu lesbarem Text, JSON und Text kommen unverändert. Optional lässt sich eine Liste erlaubter Domains einstellen, und Adressen im lokalen Netz sind standardmäßig gesperrt.
    - **Webhook (POST):** schickt eine Antwort als JSON an eine Adresse deiner Wahl, z. B. n8n, Make, Zapier, Home Assistant oder einen eigenen Server. Das geht per Knopf „Senden“ unter jeder Antwort oder per Chat („Schick die letzte Antwort an den Webhook“), dann mit Rückfrage. Ein optionaler Schlüssel wird als `Authorization: Bearer …` mitgeschickt. Format:

      ```json
      {
        "app": "LM Chat", "appVersion": "1.14.0", "sentAt": "2026-10-29T13:26:06+01:00",
        "chat": "Titel des Chats", "type": "text | image | text+image",
        "question": "die Frage dazu", "text": "die Antwort (Markdown)",
        "image": { "mimeType": "image/png", "base64": "…" },
        "provider": "OpenRouter", "model": "google/gemini-2.5-flash-image", "note": "optionale Notiz"
      }
      ```
  - **Weitere herunterladen:** aus dem [Plugin-Katalog](plugins/) (z. B. Wikipedia, Währungsrechner, DeepWiki per MCP), per Datei-Import oder von einer https-Adresse. Vor der Installation zeigt LM Chat, worauf das Plugin zugreift.
  - **MCP:** Plugins vom Typ MCP binden einen MCP-Server an, per URL (Streamable HTTP) oder als lokales Programm (stdio). Die Anbindung nutzt das offizielle C#-SDK `ModelContextProtocol`. Vor jeder Aktion fragt LM Chat nach.
  - **Unterstützte Anbieter:** alle (OpenAI-kompatibel über `tools`/`tool_calls`, Anthropic über `tool_use`), mit bis zu 8 Werkzeugrunden pro Antwort. Ob ein Modell Werkzeuge kann, erkennt die App:
    - LM Studio: `tool_use`
    - Ollama: `tools`
    - OpenRouter: `supported_parameters`

    Geeignete Modelle sind in der Liste mit 🔧 markiert. Lehnt ein Modell Werkzeuge ab, kommt die Antwort automatisch ohne Plugins.
  - Im Chat zeigt die Statuszeile „🔧 Wetter: …“ bzw. „❓ Warte auf deine Bestätigung“. Unter der Antwort steht, welche Plugins benutzt wurden.
  - Eigene Plugins schreiben: siehe [plugins/README.md](plugins/README.md).
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

Die SQLite-Datenbank hat vier Tabellen:

- `chats`
- `messages`: Bilder werden als PNG-BLOB gespeichert
- `settings`
- `usage`: Verbrauch je Antwort (Anbieter, Modell, Tokens, gemeldete Kosten), unabhängig von den Chats

Installierte Plugins liegen als JSON-Dateien in `%LOCALAPPDATA%\LmChat\plugins`. Ihr Zustand (an/aus, Einstellungen) steht in der Datenbank unter `settings` → `plugins`.

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

Zum Testen mit einer anderen bzw. leeren Datenbank (z. B. für die Willkommensseite) die Umgebungsvariable `LMCHAT_DB` setzen:

```
$env:LMCHAT_DB = "$env:TEMP\lmchat-test.db"; .\LmChat\publish\LmChat.exe
```

## Projektaufbau

| Datei | Inhalt |
|---|---|
| `LmChat/MainWindow.xaml` | Oberfläche: Chat, Einstellungen, Hilfe |
| `LmChat/MainWindow.xaml.cs` | Logik: Chat, Bilder, Chatliste, Einstellungsseite |
| `LmChat/MainWindow.Plugins.cs` | Plugin-Seite: Liste, Einstellungen je Plugin, Katalog, Import |
| `LmChat/Plugins.cs` | Plugin-Manifest, Laden/Installieren, Ausführen (Web, eingebaut, MCP) |
| `plugins/` | Plugin-Katalog (`index.json`) und Plugin-Dateien, Anleitung für eigene Plugins |
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
