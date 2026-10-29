# lmstudioChatWindows

Ein einfacher Windows-Chat-Client für [LM Studio](https://lmstudio.ai). Er läuft als **eine einzige .exe** und braucht keine Installation und kein .NET auf dem Zielrechner.

**Download:** [LmChat.exe (neuester Stand)](https://github.com/ghaslbe/lmstudioChatWindows/releases/latest/download/LmChat.exe)

Die .exe wird bei jedem Push auf `main` automatisch von GitHub Actions gebaut (`.github/workflows/release.yml`) und ersetzt das Release `latest`.

## Funktionen

- **Chat mit Live-Antwort:** Die Antwort erscheint Wort für Wort (Streaming).
- **Gespeicherte Chats:** Jeder Chat wird in einer SQLite-Datenbank gespeichert und lässt sich links in der Liste wieder aufrufen oder löschen.
- **„Bisherigen Chatverlauf mitsenden“:** Das Häkchen ist standardmäßig an, so kann man ein Gespräch führen. Ohne Häkchen wird nur die aktuelle Nachricht gesendet.
- **Bilder senden:** Bei Modellen, die Bilder verstehen (in LM Studio Typ `vlm`), lässt sich ein Bild anhängen, z. B. mit der Frage „Lies mal das Bild aus“. Dafür gibt es drei Wege:
  - Button **Bild …**
  - **Strg+V** (Screenshot oder kopierte Bilddatei)
  - Bilddatei ins Fenster ziehen

  Große Bilder werden automatisch auf maximal 2048 px verkleinert.
- **Einstellungen (eigene Seite):**
  - Server-Adresse
  - Modell per Dropdown, direkt aus LM Studio geladen. Bildfähige Modelle sind mit 🖼 markiert, geladene mit ●.
  - System-Prompt, Temperatur, Top-P, Top-K, Max. Tokens, Wiederholungsstrafe

  Leere Felder bedeuten: Standardwert des Modells verwenden.
- **Aussehen:** Die App folgt dem Hell- oder Dunkelmodus von Windows 11 (Fluent).

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

Chats und Einstellungen liegen in:

```
%LOCALAPPDATA%\LmChat\chats.db
```

Die SQLite-Datenbank hat drei Tabellen:

- `chats`
- `messages`: Bilder werden als PNG-BLOB gespeichert
- `settings`

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

## Projektaufbau

| Datei | Inhalt |
|---|---|
| `LmChat/MainWindow.xaml` | Oberfläche: Chat-Seite und Einstellungen |
| `LmChat/MainWindow.xaml.cs` | Logik: Senden/Streaming, Bilder, Chatliste, Modellliste |
| `LmChat/ChatStore.cs` | SQLite-Speicher für Chats, Nachrichten, Einstellungen |
| `LmChat/LlmSettings.cs` | LM-Studio-Einstellungen (als JSON in der Datenbank) |

Technik: C# / WPF auf .NET 10, `Microsoft.Data.Sqlite`.
