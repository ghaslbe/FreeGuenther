# lmstudioChatWindows

Ein einfacher Windows-Chat-Client für lokale und Online-Sprachmodelle: [LM Studio](https://lmstudio.ai), [Ollama](https://ollama.com), [OpenAI](https://platform.openai.com), [Anthropic (Claude)](https://platform.claude.com) und [OpenRouter](https://openrouter.ai). Er läuft als **eine einzige .exe** und braucht keine Installation und kein .NET auf dem Zielrechner.

**Download:** [LmChat.exe (neueste Version)](https://github.com/ghaslbe/lmstudioChatWindows/releases/latest/download/LmChat.exe). Alle Versionen findest du unter [Releases](https://github.com/ghaslbe/lmstudioChatWindows/releases).

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
- **OpenRouter: Standort der Rechenzentren:** Zur Wahl stehen „Weltweit“, „Nur Europa“ (EU/EWR, Großbritannien, Schweiz) oder „Nur USA“. Mit Region zeigt die Modellliste nur Modelle, die dort laufen, und die Anfragen gehen über `eu.openrouter.ai` bzw. `us.openrouter.ai` ([In-Region-Routing](https://openrouter.ai/docs/guides/features/sovereign-ai)). Ist ein Modell in der Region nicht verfügbar, gibt es einen Fehler statt einer Weiterleitung. Einzelne Länder wie Deutschland bietet OpenRouter nicht an. Laut OpenRouter gehört die Regionen-Bindung zum Business-Tarif.
- **Chat mit Live-Antwort:** Die Antwort erscheint Wort für Wort (Streaming). Danach wird sie als **Markdown** dargestellt, mit Überschriften, Fettdruck, Listen, Tabellen, Codeblöcken und Links.
- **Gespeicherte Chats:** Jeder Chat wird in einer SQLite-Datenbank gespeichert und lässt sich links in der Liste wieder aufrufen oder löschen.
- **„Bisherigen Chatverlauf mitsenden“:** Das Häkchen ist standardmäßig an, so kann man ein Gespräch führen. Ohne Häkchen wird nur die aktuelle Nachricht gesendet.
- **Bilder senden:** Bei Modellen, die Bilder verstehen (in LM Studio Typ `vlm`), lässt sich ein Bild anhängen, z. B. mit der Frage „Lies mal das Bild aus“. Dafür gibt es drei Wege:
  - Button **Bild …**
  - **Strg+V** (Screenshot oder kopierte Bilddatei)
  - Bilddatei ins Fenster ziehen

  Große Bilder werden automatisch auf maximal 2048 px verkleinert. Im Chat erscheint eine Vorschau, ein Klick darauf öffnet das Bild in voller Größe.
- **Einstellungen (eigene Seite):**
  - Server-Adresse (wird beim Speichern geprüft)
  - Modell per Dropdown, direkt aus LM Studio geladen. Bildfähige Modelle sind mit 🖼 markiert, geladene mit ●.
  - System-Prompt
  - Temperatur, Top-P, Top-K, maximale Antwortlänge und Wiederholungsstrafe

  Die Zahlenwerte stellst du per Schieberegler ein, nur innerhalb gültiger Bereiche und jeweils mit Erklärung. Ohne Haken gilt der Standardwert des Modells.
- **Hilfe-Seite:** Kurzanleitung, Fehlerbehebung, Lizenz und Link zu diesem Repository.
- **Aussehen:** Die App folgt dem Hell- oder Dunkelmodus von Windows 11 (Fluent) und hat ein eigenes Icon.

## Bedienung

| Taste | Aktion |
|---|---|
| Enter | Senden |
| Umschalt+Enter | Neue Zeile |
| Strg+V | Bild aus der Zwischenablage anhängen |

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

Bei jedem Push auf `main` baut GitHub Actions (`.github/workflows/release.yml`) die .exe und legt sie als Release `v<Version>` ab. Gibt es das Release schon, wird nur die .exe ersetzt.

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
