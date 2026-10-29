# lmstudioChatWindows

Ein einfacher Windows-Chat-Client für [LM Studio](https://lmstudio.ai). Er läuft als **eine einzige .exe** und braucht keine Installation und kein .NET auf dem Zielrechner.

**Download:** [LmChat.exe (neueste Version)](https://github.com/ghaslbe/lmstudioChatWindows/releases/latest/download/LmChat.exe). Alle Versionen findest du unter [Releases](https://github.com/ghaslbe/lmstudioChatWindows/releases).

## Funktionen

- **Chat mit Live-Antwort:** Die Antwort erscheint Wort für Wort (Streaming).
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

## Voraussetzungen in LM Studio

1. Unter **Developer** den Server starten (Standard-Port `1234`).
2. **„Serve on Local Network“** einschalten, damit andere Rechner im Netzwerk zugreifen können.
3. Gegebenenfalls Port 1234 in der Firewall freigeben.

Die App nutzt zwei Schnittstellen von LM Studio:

- `/v1/chat/completions`: OpenAI-kompatible Schnittstelle für den Chat
- `/api/v0/models`: Modellliste inkl. Typ (`llm`/`vlm`) und Ladezustand

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
| `LmChat/MainWindow.xaml.cs` | Logik: Senden/Streaming, Bilder, Chatliste, Modellliste, Einstellungen |
| `LmChat/ParamRow.cs` | Einstellungszeile mit Haken, Erklärung und Schieberegler |
| `LmChat/ChatStore.cs` | SQLite-Speicher für Chats, Nachrichten, Einstellungen |
| `LmChat/LlmSettings.cs` | LM-Studio-Einstellungen (als JSON in der Datenbank) |
| `LmChat/app.ico` | App-Icon |

Technik: C# / WPF auf .NET 10, `Microsoft.Data.Sqlite`.

## Lizenz und Haftungsausschluss

Dieses Projekt steht unter der [MIT-Lizenz](LICENSE). Du darfst es frei nutzen, verändern und weitergeben.

Die Software wird **ohne jede Gewährleistung** bereitgestellt. Die Autoren übernehmen **keinerlei Haftung** für Schäden, Datenverlust oder sonstige Folgen der Nutzung. Antworten von KI-Modellen können falsch sein. Prüfe wichtige Informationen selbst.
