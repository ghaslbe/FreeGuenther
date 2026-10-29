# Plugins für LM Chat

Dieser Ordner ist der **Plugin-Katalog**. LM Chat lädt `index.json` und bietet die Plugins unter „Plugins → Weitere Plugins“ zum Installieren an. Mitgeliefert sind `wetter.json`, `datum-uhrzeit.json` und `dateien.json`; sie stecken in der .exe.

Ein Plugin ist **eine JSON-Datei**. Es enthält keinen Programmcode, und es muss nichts zusätzlich installiert werden. Importieren kannst du sie in LM Chat auf zwei Wegen:
- „Plugin-Datei importieren …“
- „Von Adresse installieren“ (https)

Installierte Plugins liegen in `%LOCALAPPDATA%\LmChat\plugins`.

## Arten

| `type` | Was es tut |
|---|---|
| `http` | Werkzeuge rufen eine oder mehrere Web-Adressen ab (GET) und geben die Antwort ans Modell. Es werden nur die im Manifest stehenden Adressen aufgerufen. |
| `mcp` | Verbindet sich mit einem MCP-Server (`mcp.url`, Streamable HTTP) oder startet ihn als Programm (`mcp.command` + `args`, stdio). Dessen Werkzeuge werden übernommen. Vor jeder Aktion fragt LM Chat nach. |
| `builtin` | In LM Chat eingebaute Funktionen (`datetime`, `files_list`, `files_read`, `files_search`). |

## Aufbau

```json
{
  "id": "mein-plugin",
  "name": "Mein Plugin",
  "version": "1.0.0",
  "author": "Name",
  "description": "Was das Plugin kann – wird vor der Installation angezeigt.",
  "homepage": "https://…",
  "type": "http",
  "askBeforeRun": false,
  "settings": [
    { "key": "apikey", "label": "API-Schlüssel", "type": "secret", "description": "…" },
    { "key": "unit", "label": "Einheit", "type": "choice", "default": "metric",
      "options": ["metric", "imperial"], "optionLabels": ["metrisch", "imperial"] }
  ],
  "tools": [
    {
      "name": "suche",
      "description": "Beschreibung fürs Modell: wann und wofür es das Werkzeug nutzen soll.",
      "parameters": {
        "type": "object",
        "properties": { "begriff": { "type": "string", "description": "…" } },
        "required": ["begriff"]
      },
      "requests": [
        { "as": "a", "url": "https://api.example.com/search?q={begriff}&units={settings.unit}",
          "headers": { "Authorization": "Bearer {settings.apikey}" },
          "require": "a.results.0", "error": "Nichts gefunden zu „{begriff}“." },
        { "url": "https://api.example.com/item/{a.results.0.id}" }
      ],
      "result": "Text, der vor die Rohantwort gesetzt wird (z. B. Hinweise zur Deutung)."
    }
  ]
}
```

Die einzelnen Felder:

- **`id`:** nur Kleinbuchstaben, Ziffern und Bindestriche. Eine neuere `version` mit derselben `id` ersetzt die alte, auch ein mitgeliefertes Plugin.
- **`settings`:** daraus baut LM Chat die Einstellungsseite des Plugins. Die Typen sind:
  - `text`
  - `secret`: wird verschlüsselt gespeichert (Windows DPAPI)
  - `number`
  - `bool`
  - `choice`
  - `folder`: mit Ordnerauswahl
- **`parameters`:** JSON-Schema der Werte, die das Modell übergibt.
- **`requests`:** werden nacheinander ausgeführt. Mit `as` lässt sich die Antwort in späteren Adressen verwenden.
- **Ergebnis:** die letzte Antwort, auf 12.000 Zeichen gekürzt, geht ans Modell.

### Platzhalter

| Schreibweise | Bedeutung |
|---|---|
| `{param}` | Wert, den das Modell übergibt |
| `{settings.key}` | Wert aus den Plugin-Einstellungen |
| `{alias.pfad.0.feld}` | Feld aus einer früheren Antwort (`as`), Zahlen sind Listen-Indizes |
| `{a\|b\|'text'}` | erster nicht leerer Wert, `'text'` ist ein fester Text |

In Adressen werden die Werte automatisch URL-kodiert.

### MCP

```json
"type": "mcp",
"mcp": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer {settings.token}" } }
```

Oder als lokales Programm (es muss auf dem Rechner vorhanden sein):

```json
"mcp": { "command": "C:\\Tools\\mein-mcp-server.exe", "args": ["--ordner", "{settings.folder}"], "env": { "KEY": "{settings.apikey}" } }
```

## In den Katalog aufnehmen

Das geht so:

1. Die Plugin-Datei hier ablegen.
2. In `index.json` eintragen: `id`, `name`, `version`, `type`, `description` und die raw-Adresse unter `url`.
3. Eine höhere `version` zeigt LM Chat als „Aktualisieren“ an.
